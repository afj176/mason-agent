import Foundation

public struct ApprovalRequest {
    public let tool: String
    public let title: String
    public let detail: String
    /// What an always-allow rule would match: the command text or the file path.
    public let subject: String
}

public enum AgentEvent {
    case text(String)
    case activityStarted(Activity)
    case activityFinished(Activity)
    case status(String)
}

/// Always-allow rules a user saved for one project. They only skip the prompt; they never widen the sandbox.
public struct ApprovalRules {
    public struct Rule: Codable, Equatable { public var tool: String; public var prefix: String }
    let file: URL
    public private(set) var rules: [Rule] = []

    public init(home: URL = DataHome.url, workspace: String) {
        var hash: UInt64 = 0xcbf29ce484222325
        for byte in workspace.utf8 { hash = (hash ^ UInt64(byte)) &* 0x100000001b3 }
        file = home.appendingPathComponent("approval-rules").appendingPathComponent(String(hash, radix: 16) + ".json")
        if let data = try? Data(contentsOf: file), let saved = try? JSONDecoder().decode([Rule].self, from: data) { rules = saved }
    }
    public func allows(tool: String, subject: String) -> Bool {
        rules.contains { $0.tool == tool && !$0.prefix.isEmpty && subject.hasPrefix($0.prefix) }
    }
    public mutating func add(tool: String, prefix: String) throws {
        let trimmed = prefix.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, trimmed.count <= 400 else { throw MasonError("A rule needs 1–400 characters to match.") }
        rules.removeAll { $0.tool == tool && $0.prefix == trimmed }
        rules.append(Rule(tool: tool, prefix: trimmed))
        try persist()
    }
    public mutating func remove(at index: Int) throws { rules.remove(at: index); try persist() }
    func persist() throws {
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try JSONEncoder().encode(rules).write(to: file, options: .atomic)
    }
    /// The program and its first argument for a command ("git status"), the folder for a file.
    public static func suggestion(tool: String, subject: String) -> String {
        if tool == "run_shell" { return subject.split(separator: " ").prefix(2).joined(separator: " ") }
        if let slash = subject.lastIndex(of: "/") { return String(subject[...slash]) }
        return subject
    }
}

/// One bounded, sequential agent loop: ask the model, run the tool it calls, hand the result back, until it answers in text.
public final class Agent {
    public static let actionLimit = 64
    let store: SessionStore
    let model: ChatModel
    let approve: (ApprovalRequest) async -> Bool
    let emit: (AgentEvent) -> Void

    public init(store: SessionStore, model: ChatModel, approve: @escaping (ApprovalRequest) async -> Bool, emit: @escaping (AgentEvent) -> Void) {
        self.store = store; self.model = model; self.approve = approve; self.emit = emit
    }

    public static let tools: [ToolSpec] = {
        func schema(_ properties: [String: [String: Any]], required: [String]) -> [String: Any] {
            ["type": "object", "properties": properties, "required": required, "additionalProperties": false]
        }
        let path: [String: Any] = ["type": "string", "description": "Path relative to the project folder."]
        return [
            ToolSpec(name: "list_files", description: "List the project's files.", parameters: schema(["path": ["type": "string", "description": "Folder to list, relative to the project. Use . for the whole project."]], required: ["path"])),
            ToolSpec(name: "read_file", description: "Read a UTF-8 text file with line numbers.", parameters: schema(["path": path], required: ["path"])),
            ToolSpec(name: "search_files", description: "Search the project's files for text (case-insensitive).", parameters: schema(["query": ["type": "string"]], required: ["query"])),
            ToolSpec(name: "edit_file", description: "Replace one exact passage in an existing file. old_text must match exactly once; include enough surrounding lines to make it unique.",
                     parameters: schema(["path": path, "old_text": ["type": "string"], "new_text": ["type": "string"]], required: ["path", "old_text", "new_text"])),
            ToolSpec(name: "write_file", description: "Create a file, or replace its whole content.", parameters: schema(["path": path, "content": ["type": "string"]], required: ["path", "content"])),
            ToolSpec(name: "run_shell", description: "Run a shell command in the project folder and return its output. No interactive input; 120 second limit.",
                     parameters: schema(["command": ["type": "string"]], required: ["command"])),
        ]
    }()

    static func systemPrompt(_ session: Session) -> String {
        """
        You are Mason Agent, a coding agent working in the project folder \(session.workspace) on macOS.
        Use the tools to inspect the project before changing it, and call one tool at a time. Read a file before editing it.
        Make the smallest change that does what was asked, then check your work when a quick check exists.
        \(session.askOnly || session.permission == .readOnly ? "This session is read-only: answer from what you read; do not edit files or run commands." : "")
        When you are done, reply in plain text with a short summary of what you did and anything the user should verify. Do not claim a check passed unless you ran it.
        """
    }

    /// Runs one request to completion. The session is saved after every step, so an interruption loses nothing that already happened.
    public func run(_ session: inout Session, request: String) async {
        let input = request.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !input.isEmpty, input.count <= 16_000 else {
            session.state = "Failed"; session.detail = "Describe a request in 1–16,000 characters."; return
        }
        guard let workspace = try? Workspace(path: session.workspace) else {
            session.state = "Failed"; session.detail = "The project folder \(session.workspace) is no longer available."; return
        }
        if session.title == "New session" { session.title = input.count > 55 ? String(input.prefix(55)) + "…" : input }
        if session.messages.first?.role == "system" { session.messages[0].content = Agent.systemPrompt(session) }
        else { session.messages.insert(ChatMessage(role: "system", content: Agent.systemPrompt(session)), at: 0) }
        // A note from undo or redo travels with the next request, so the conversation keeps alternating user and assistant turns.
        session.messages.append(ChatMessage(role: "user", content: (session.pendingNote.map { $0 + "\n\n" } ?? "") + input))
        session.pendingNote = nil
        session.turns.append(Turn(request: input, firstActivity: session.activities.count))
        session.state = "Running"; session.detail = ""
        try? store.save(session)
        do {
            var finished = false
            for _ in 0..<Agent.actionLimit {
                try Task.checkCancellation()
                var reply = try await model.complete(messages: session.messages, tools: Agent.tools) { [emit] text in emit(.text(text)) }
                // A call the model wrote as text instead of through the tool-call channel is recovered.
                if reply.toolCalls.isEmpty, let written = ToolCallRecovery.recover(from: reply.text, tools: Agent.tools) { reply.toolCalls = [written] }
                if let tokens = reply.promptTokens { session.promptTokens = tokens }
                session.messages.append(ChatMessage(role: "assistant", content: reply.text, toolCalls: reply.toolCalls.isEmpty ? nil : reply.toolCalls))
                if reply.toolCalls.isEmpty {
                    session.turns[session.turns.count - 1].answer = reply.text
                    finished = true
                    break
                }
                for call in reply.toolCalls {
                    try Task.checkCancellation()
                    let result = await perform(call, in: workspace, session: &session)
                    session.messages.append(ChatMessage(role: "tool", content: result, toolCallId: call.id))
                    try? store.save(session)
                }
            }
            session.state = finished ? "Completed" : "Stopped"
            session.detail = finished ? "" : "Stopped after \(Agent.actionLimit) steps. Send another message to continue."
        } catch is CancellationError {
            session.state = "Stopped"; session.detail = "Stopped. Completed edits are kept."
        } catch {
            session.state = "Failed"; session.detail = (error as? MasonError)?.description ?? error.localizedDescription
        }
        // A tool call without a result would make the next request invalid for the model, so close any that were cut short.
        Agent.closeOpenCalls(&session)
        try? store.save(session)
    }

    static func closeOpenCalls(_ session: inout Session) {
        var answered = Set(session.messages.compactMap { $0.toolCallId })
        var index = 0
        while index < session.messages.count {
            let message = session.messages[index]
            var insertAt = index + 1
            while insertAt < session.messages.count, session.messages[insertAt].role == "tool" { insertAt += 1 }
            for call in message.toolCalls ?? [] where !answered.contains(call.id) {
                session.messages.insert(ChatMessage(role: "tool", content: "Not run: the request was stopped first.", toolCallId: call.id), at: insertAt)
                answered.insert(call.id); insertAt += 1
            }
            index += 1
        }
    }

    func perform(_ call: ToolCall, in workspace: Workspace, session: inout Session) async -> String {
        let arguments = (try? JSONSerialization.jsonObject(with: Data(call.arguments.utf8)) as? [String: Any]) ?? [:]
        func text(_ key: String) -> String? { arguments[key] as? String }
        let subject = text("command") ?? text("path") ?? text("query") ?? ""
        var activity = Activity(tool: call.name, subject: subject, state: .running, result: "")
        session.activities.append(activity)
        emit(.activityStarted(activity))
        func finish(_ state: Activity.State, _ result: String) -> String {
            activity.state = state; activity.result = String(result.prefix(8000))
            session.activities[session.activities.count - 1] = activity
            emit(.activityFinished(activity))
            return state == .completed ? result : "Error: " + result
        }
        let readOnly = session.askOnly || session.permission == .readOnly
        do {
            switch call.name {
            case "list_files":
                let listing = try workspace.files(under: text("path") ?? ".", limit: 800)
                return finish(.completed, listing.paths.isEmpty ? "No files." : listing.paths.joined(separator: "\n") + (listing.truncated ? "\n[Listing clipped at 800 files.]" : ""))
            case "read_file":
                guard let path = text("path") else { throw MasonError("read_file needs a path.") }
                let content = try workspace.read(path)
                let numbered = content.split(separator: "\n", omittingEmptySubsequences: false).enumerated().map { "\($0.offset + 1)\t\($0.element)" }.joined(separator: "\n")
                return finish(.completed, numbered.count > 60_000 ? String(numbered.prefix(60_000)) + "\n[File clipped.]" : numbered)
            case "search_files":
                guard let query = text("query") else { throw MasonError("search_files needs a query.") }
                let matches = try workspace.search(query)
                return finish(.completed, matches.isEmpty ? "No matches." : matches.map { "\($0.path):\($0.line): \($0.text)" }.joined(separator: "\n"))
            case "edit_file", "write_file":
                if readOnly { throw MasonError("This session is read-only. Tell the user what you would change instead.") }
                guard let path = text("path") else { throw MasonError("\(call.name) needs a path.") }
                let before: String? = workspace.exists(path) ? try workspace.read(path) : nil
                let after: String
                if call.name == "edit_file" {
                    guard let before, let old = text("old_text"), let new = text("new_text"), !old.isEmpty else { throw MasonError("edit_file needs an existing file, old_text and new_text.") }
                    let occurrences = before.components(separatedBy: old).count - 1
                    guard occurrences == 1 else {
                        throw MasonError(occurrences == 0 ? "old_text was not found in \(path). Read the file again and copy the passage exactly." : "old_text matches \(occurrences) places in \(path). Include more surrounding lines so it matches once.")
                    }
                    after = before.replacingOccurrences(of: old, with: new)
                } else {
                    guard let content = text("content") else { throw MasonError("write_file needs content.") }
                    after = content
                }
                if session.permission == .reviewEdits {
                    let rows = Diff.lines(before: before, after: after).filter { $0.kind != .same }.prefix(60).map { "\($0.kind.rawValue) \($0.text)" }.joined(separator: "\n")
                    if !(await allowed(ApprovalRequest(tool: call.name, title: (before == nil ? "Create " : "Edit ") + path, detail: rows, subject: path), session: &session)) {
                        return finish(.denied, "The user declined this edit. Do not retry it; ask what they want instead.")
                    }
                }
                try workspace.write(path, after)
                record(path: path, before: before, after: after, session: &session)
                let counts = Diff.counts(before: before, after: after)
                return finish(.completed, "\(before == nil ? "Created" : "Edited") \(path) (+\(counts.added) −\(counts.removed)).")
            case "run_shell":
                if readOnly { throw MasonError("This session is read-only. Commands are not available.") }
                guard let command = text("command") else { throw MasonError("run_shell needs a command.") }
                let sandboxed = session.permission == .auto
                if !sandboxed {
                    let detail = "Runs in \(workspace.root.path) with your account and environment. It is not sandboxed. Limit: 120 seconds."
                    if !(await allowed(ApprovalRequest(tool: call.name, title: command, detail: detail, subject: command), session: &session)) {
                        return finish(.denied, "The user declined this command. Do not retry it; ask what they want instead.")
                    }
                }
                emit(.status(sandboxed ? "Running sandboxed command" : "Running command"))
                let result = try await Shell.run(command, in: workspace, sandboxed: sandboxed)
                let output = "exit \(result.exitCode)\n" + result.output
                return finish(result.exitCode == 0 && !result.timedOut ? .completed : .failed, output)
            default:
                throw MasonError("Unknown tool \(call.name). Use one of: " + Agent.tools.map(\.name).joined(separator: ", "))
            }
        } catch is CancellationError {
            return finish(.failed, "Stopped by the user.")
        } catch {
            return finish(.failed, (error as? MasonError)?.description ?? error.localizedDescription)
        }
    }

    /// Asks the user unless a saved project rule already allows it.
    func allowed(_ request: ApprovalRequest, session: inout Session) async -> Bool {
        if ApprovalRules(home: store.folder.deletingLastPathComponent(), workspace: session.workspace).allows(tool: request.tool, subject: request.subject) { return true }
        session.state = "Awaiting review"
        try? store.save(session)
        let answer = await approve(request)
        session.state = "Running"
        return answer
    }

    /// Keeps the first "before" of each file per request, so undo returns to the state the request started from.
    func record(path: String, before: String?, after: String, session: inout Session) {
        let turn = session.turns.count - 1
        if let index = session.turns[turn].changes.firstIndex(where: { $0.path == path }) { session.turns[turn].changes[index].after = after }
        else { session.turns[turn].changes.append(FileChange(path: path, before: before, after: after)) }
    }

    public struct UndoResult { public var restored: [String] = []; public var skipped: [String] = [] }

    /// Puts back the files the latest request changed through edit tools. A file changed again since is left alone and reported.
    /// Files created or changed by commands are not tracked yet.
    public static func undoLatest(_ session: inout Session, store: SessionStore, redo: Bool = false) throws -> UndoResult? {
        guard let index = session.turns.lastIndex(where: { !$0.changes.isEmpty && $0.undone == redo }) else { return nil }
        let workspace = try Workspace(path: session.workspace)
        var result = UndoResult()
        for change in session.turns[index].changes {
            let expected = redo ? change.before : change.after
            let target = redo ? change.after : change.before
            let current: String? = workspace.exists(change.path) ? try? workspace.read(change.path) : nil
            guard current == expected else { result.skipped.append(change.path); continue }
            if let target { try workspace.write(change.path, target) } else { try workspace.delete(change.path) }
            result.restored.append(change.path)
        }
        session.turns[index].undone = !redo
        let note = redo ? "The user reapplied the changes of an earlier request." : "The user undid the file changes of the request \"\(session.turns[index].request.prefix(200))\"."
        session.pendingNote = note + " Read affected files again before relying on what you saw earlier."
        try store.save(session)
        return result
    }
}
