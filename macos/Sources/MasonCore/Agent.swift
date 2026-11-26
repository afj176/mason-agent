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
    /// A follow-up is about to run: the session as the previous request left it, and the follow-up's text.
    case followUp(Session, String)
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
    /// The project as last read, so each command only re-reads the files that changed.
    var snapshot: [String: Snapshot.Entry] = [:]
    var home: URL { store.folder.deletingLastPathComponent() }
    /// Follow-ups that arrive while a request runs, from whichever thread the user interface is on.
    private let inboxLock = NSLock()
    private var inbox: [FollowUp] = []
    private var accepting = false
    private var activeTurn: Task<Session, Never>?
    public static let followUpLimit = 16
    /// Background programs started during the current request.
    let jobs = Jobs()
    /// What reads and operates the attached window. Left nil, it is this computer's accessibility interface.
    public var desktop: DesktopAutomation?
    /// What looks at a picture of the attached window. Left nil, it is the running model, which must take images.
    public var vision: DesktopVision?
    /// The controls from the latest observation of the attached window; an action must name one of them.
    var lastObservation: [DesktopControl] = []
    /// What searches code by meaning. Left nil, it is the embeddings server saved in settings, if there is one.
    public var semantic: SemanticModels?
    var semanticModels: SemanticModels? { semantic ?? EmbeddingSettings.load(home: home).map(EmbeddingServers.init) }
    /// The tools offered for the request in progress, for the message about an unknown tool.
    var offeredNames: [String] = []
    /// A question the model asked with ask_user. The request stops there and waits for the user's next message.
    var pendingQuestion: String?

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

    /// Offered only when the user has registered an MCP server, so sessions without one keep a short tool list.
    public static let mcpTools: [ToolSpec] = {
        func schema(_ names: [String]) -> [String: Any] {
            ["type": "object", "properties": Dictionary(uniqueKeysWithValues: names.map { ($0, ["type": "string"] as [String: Any]) }), "required": names, "additionalProperties": false]
        }
        return [
            ToolSpec(name: "mcp_tools", description: "List the tools you may call on a user-registered MCP server.", parameters: schema(["server"])),
            ToolSpec(name: "mcp_call", description: "Call one allowed tool on a registered MCP server. arguments_json is a JSON object. The user is asked first.", parameters: schema(["server", "tool", "arguments_json"])),
            ToolSpec(name: "mcp_resources", description: "List the resources you may read from a registered MCP server.", parameters: schema(["server"])),
            ToolSpec(name: "mcp_read", description: "Read one allowed resource from a registered MCP server.", parameters: schema(["server", "uri"])),
        ]
    }()

    /// The tools for this request: the built-in ones, plus the MCP tools when a server is registered.
    func offeredTools() -> [ToolSpec] { Mcp.enabled(home: home).isEmpty ? Agent.tools : Agent.tools + Agent.mcpTools }

    static func systemPrompt(_ session: Session, servers: [McpServer] = []) -> String {
        """
        You are Mason Agent, a coding agent working in the project folder \(session.workspace) on macOS.
        Use the tools to inspect the project before changing it, and call one tool at a time. Read a file before editing it.
        Make the smallest change that does what was asked, then check your work when a quick check exists.
        \(session.askOnly || session.permission == .readOnly ? "This session is read-only: answer from what you read; do not edit files or run commands." : "")
        \(servers.isEmpty ? "" : "The user registered these MCP servers: " + servers.map(\.name).joined(separator: ", ") + ". Use mcp_tools to see what one offers before calling it.")
        When you are done, reply in plain text with a short summary of what you did and anything the user should verify. Do not claim a check passed unless you ran it.
        """
    }

    // MARK: verification

    /// How many times a request is handed back to the model because verification regressed.
    public static let repairLimit = 2
    /// The name of the workflow in mason.workflows.json that Mason runs to verify a request.
    public static let verifyWorkflow = "verify"
    /// Tools that can change the project.
    static let changing: Set<String> = ["edit_file", "write_file", "apply_edits", "run_shell", "run_workflow", "start_job"]

    /// The verify workflow, when the project defines one.
    func verifyCommand(_ workspace: Workspace) -> String? {
        (try? ProjectWorkflows.load(workspace))?.workflows[Agent.verifyWorkflow].map(ProjectWorkflows.commandLine)
    }

    /// Mason runs the workflow itself, so it needs the same permission a command would: Auto (it runs sandboxed),
    /// or a saved rule for "workflow verify". It never asks in the middle of a request.
    func mayVerify(_ session: Session) -> Bool {
        if session.askOnly || session.permission == .readOnly { return false }
        return session.permission == .auto || ApprovalRules(home: home, workspace: session.workspace).allows(tool: "run_workflow", subject: "workflow " + Agent.verifyWorkflow)
    }

    /// Runs the verify workflow, or returns nil when there is none or it may not be run.
    func verifyRun(_ workspace: Workspace, session: Session) async -> CommandResult? {
        guard let command = verifyCommand(workspace), mayVerify(session) else { return nil }
        emit(.status("Verifying"))
        let result = try? await Shell.run(command, in: workspace, sandboxed: session.permission == .auto)
        // Whatever the run left behind is not part of the request's changes.
        snapshot = Snapshot.capture(workspace, previous: snapshot).entries
        return result
    }

    /// After the model's answer: when the request changed files and the project has a verify workflow, the receipt.
    func verify(_ workspace: Workspace, session: inout Session, baseline: CommandResult?, repairs: Int) async throws -> Verification? {
        guard !session.turns[session.turns.count - 1].changes.isEmpty, let command = verifyCommand(workspace) else { return nil }
        guard mayVerify(session) else {
            return Verification(workflow: Agent.verifyWorkflow, command: command, verdict: .notRun, exitCode: nil, baselineExitCode: nil,
                                output: "Mason runs it by itself in Auto, or when you have chosen Always Allow for the verify workflow.", repairs: repairs)
        }
        try Task.checkCancellation()
        guard let after = await verifyRun(workspace, session: session) else { return nil }
        try Task.checkCancellation()
        let passed = after.exitCode == 0 && !after.timedOut
        let before = baseline.map { $0.exitCode == 0 && !$0.timedOut }
        let verdict: Verification.Verdict = passed ? (before == false ? .fixed : .passed) : (before == true ? .regression : before == false ? .failingBefore : .failed)
        return Verification(workflow: Agent.verifyWorkflow, command: command, verdict: verdict, exitCode: after.exitCode, baselineExitCode: baseline?.exitCode,
                            output: String(after.output.suffix(4000)), repairs: repairs)
    }

    /// Adds a message while a request is running. Steering stops the model's current step and handles the message
    /// next; otherwise the message is queued until the request completes.
    public func sendWhileRunning(_ text: String, steer: Bool) throws {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 16_000 else { throw MasonError("Write a message of 1–16,000 characters.") }
        let outcome: (problem: String?, turn: Task<Session, Never>?) = withInbox {
            guard accepting else { return ("No request is running in this session.", nil) }
            guard inbox.count < Agent.followUpLimit else { return ("At most \(Agent.followUpLimit) messages can wait at once.", nil) }
            inbox.append(FollowUp(text: trimmed, steer: steer))
            return (nil, activeTurn)
        }
        if let problem = outcome.problem { throw MasonError(problem) }
        if steer { outcome.turn?.cancel() }
    }

    /// The follow-up handled next: a steering message first, else the oldest queued one.
    static func nextFollowUp(_ session: Session) -> FollowUp? {
        let pending = session.pendingInput ?? []
        return pending.first { $0.steer } ?? pending.first
    }

    /// Runs a request, then any follow-ups sent while it ran. "/queue" resumes follow-ups left from an earlier stop.
    /// A queued follow-up runs only after a request that completed; a steering one runs whatever happened.
    public func run(_ session: inout Session, request: String) async {
        var input = request
        if request.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "/queue" {
            guard let next = Agent.nextFollowUp(session) else { session.state = "Failed"; session.detail = "No messages are queued."; return }
            session.pendingInput?.removeAll { $0.id == next.id }
            input = next.text
        }
        withInbox { accepting = true }
        while true {
            let start = session, text = input
            let turn = Task { [self] () -> Session in var working = start; await self.runTurn(&working, request: text); return working }
            // A steering message that arrived between requests interrupts this one at once.
            if withInbox({ activeTurn = turn; return inbox.contains { $0.steer } }) { turn.cancel() }
            session = await withTaskCancellationHandler { await turn.value } onCancel: { turn.cancel() }
            let stopping = Task.isCancelled, state = session.state, waiting = Agent.nextFollowUp(session)
            let (arrived, next, continuing): ([FollowUp], FollowUp?, Bool) = withInbox {
                activeTurn = nil
                let arrived = inbox; inbox = []
                let next = stopping ? nil : (arrived.first { $0.steer } ?? waiting ?? arrived.first)
                let continuing = next.map { $0.steer || state == "Completed" } ?? false
                if !continuing { accepting = false }
                return (arrived, next, continuing)
            }
            if !arrived.isEmpty { session.pendingInput = Array(((session.pendingInput ?? []) + arrived).suffix(Agent.followUpLimit)) }
            if continuing, let next {
                session.pendingInput?.removeAll { $0.id == next.id }
                input = next.text
            }
            if session.pendingInput?.isEmpty == true { session.pendingInput = nil }
            if !arrived.isEmpty || continuing { try? store.save(session) }
            if !continuing {
                // Whatever the request left running in the background ends with it.
                await jobs.stopAll()
                return
            }
            emit(.followUp(session, input))
            emit(.status(next?.steer == true ? "Steering" : "Starting the queued message"))
        }
    }

    /// Everything about follow-ups is read and changed under one lock.
    private func withInbox<T>(_ body: () -> T) -> T {
        inboxLock.lock(); defer { inboxLock.unlock() }
        return body()
    }

    /// Runs one request to completion. The session is saved after every step, so an interruption loses nothing that already happened.
    func runTurn(_ session: inout Session, request: String) async {
        guard let workspace = try? Workspace(path: session.workspace) else {
            session.state = "Failed"; session.detail = "The project folder \(session.workspace) is no longer available."; return
        }
        // Comments on lines of the project travel with the request, and only while those files are unchanged.
        let comments = session.comments ?? []
        do { try Review.validate(workspace, comments: comments) }
        catch { session.state = "Failed"; session.detail = "\(error)"; return }
        let input = Review.request(request, comments: comments)
        guard !input.isEmpty, input.count <= 16_000 else {
            session.state = "Failed"; session.detail = "Describe a request in 1–16,000 characters."; return
        }
        session.comments = nil
        if session.title == "New session" { session.title = input.count > 55 ? String(input.prefix(55)) + "…" : input }
        let servers = Mcp.enabled(home: home)
        let offered = Agent.tools + Agent.extraTools(session: session, workspace: workspace, semantic: semanticModels != nil)
            + Agent.desktopTools(for: session, available: (desktop ?? Desktop.system) != nil) + (servers.isEmpty ? [] : Agent.mcpTools)
        lastObservation = []
        offeredNames = offered.map(\.name)
        var prompt = Agent.systemPrompt(session, servers: servers)
        let skills = Skills.list(workspace)
        if !skills.isEmpty { prompt += "\nThis project has instructions for particular kinds of work. Read the one that fits with read_skill before starting: " + skills.map { "\($0.name) (\($0.description))" }.joined(separator: "; ") + "." }
        if let window = session.desktop, (desktop ?? Desktop.system) != nil { prompt += "\nThe user attached a window of another app: \(window.label). Read it with desktop_observe; use desktop_visual for what its controls do not name. Acting on it asks the user each time." }
        if offeredNames.contains("verify_acceptance") { prompt += "\nThis project has acceptance scenarios in mason.acceptance.json. Run verify_acceptance after changing a page they cover, and do not edit that file unless asked." }
        if let plan = session.milestones, !plan.isEmpty { prompt += "\n" + Milestones.context(workspace, plan: plan) }
        if session.messages.first?.role == "system" { session.messages[0].content = prompt }
        else { session.messages.insert(ChatMessage(role: "system", content: prompt), at: 0) }
        // A note from undo or redo travels with the next request, so the conversation keeps alternating user and assistant turns.
        // A custom command is expanded and the selected agent's instructions are added; the session still shows what was typed.
        let prepared = Customization.prepare(home: home, session: session, input: input)
        session.messages.append(ChatMessage(role: "user", content: (session.pendingNote.map { $0 + "\n\n" } ?? "") + prepared))
        session.pendingNote = nil
        session.turns.append(Turn(request: input, firstActivity: session.activities.count))
        session.state = "Running"; session.detail = ""
        try? store.save(session)
        do {
            var finished = false, asked = false
            var baselineTaken = false, baseline: CommandResult?, repairs = 0
            pendingQuestion = nil
            let window = await model.contextWindow()
            for _ in 0..<Agent.actionLimit {
                try Task.checkCancellation()
                if Compaction.needed(session, window: window) {
                    emit(.status("Compacting the conversation"))
                    if try await Compaction.compact(&session, model: model, keep: 6) { try? store.save(session) }
                    else { session.promptTokens = nil }
                }
                var reply = try await model.complete(messages: session.messages, tools: offered) { [emit] text in emit(.text(text)) }
                // A call the model wrote as text instead of through the tool-call channel is recovered.
                if reply.toolCalls.isEmpty, let written = ToolCallRecovery.recover(from: reply.text, tools: offered) { reply.toolCalls = [written] }
                if let tokens = reply.promptTokens { session.promptTokens = tokens }
                session.messages.append(ChatMessage(role: "assistant", content: reply.text, toolCalls: reply.toolCalls.isEmpty ? nil : reply.toolCalls))
                if reply.toolCalls.isEmpty {
                    session.turns[session.turns.count - 1].answer = reply.text
                    // The model says it is done. If the project can be verified and files changed, Mason checks for itself.
                    if let receipt = try await verify(workspace, session: &session, baseline: baseline, repairs: repairs) {
                        session.turns[session.turns.count - 1].verification = receipt
                        if receipt.verdict == .regression, repairs < Agent.repairLimit {
                            repairs += 1
                            session.messages.append(ChatMessage(role: "user", content: "Mason ran the project's verify workflow (\(receipt.command)). It passed before your changes and fails now:\n\(receipt.output)\nFix what your changes broke, then answer again."))
                            try? store.save(session)
                            continue
                        }
                    }
                    finished = true
                    break
                }
                for call in reply.toolCalls {
                    try Task.checkCancellation()
                    // Before the first step that can change the project, note whether it verifies as it stands.
                    if !baselineTaken, Agent.changing.contains(call.name) {
                        baselineTaken = true
                        baseline = await verifyRun(workspace, session: session)
                    }
                    let result = await perform(call, in: workspace, session: &session)
                    session.messages.append(ChatMessage(role: "tool", content: result, toolCallId: call.id))
                    try? store.save(session)
                    if pendingQuestion != nil { break }
                }
                if let question = pendingQuestion {
                    // The model needs something only the user can say. Its question is the answer to this request.
                    pendingQuestion = nil
                    session.turns[session.turns.count - 1].answer = question
                    asked = true
                    break
                }
            }
            session.state = asked ? "Needs your answer" : finished ? "Completed" : "Stopped"
            session.detail = asked ? "Mason asked a question. Reply to continue." : finished ? "" : "Stopped after \(Agent.actionLimit) steps. Send another message to continue."
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
        let named = text("command") ?? text("path") ?? text("query") ?? text("name") ?? text("id") ?? text("question")
        let subject = named ?? [text("server"), text("tool") ?? text("uri")].compactMap { $0 }.joined(separator: "/")
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
                // The project is read before and after, so whatever the command changed can be shown and undone.
                let before = Snapshot.capture(workspace, previous: snapshot)
                let result: CommandResult
                do { result = try await Shell.run(command, in: workspace, sandboxed: sandboxed) }
                catch { _ = recordCommandChanges(since: before.entries, in: workspace, session: &session); throw error }
                let changed = recordCommandChanges(since: before.entries, in: workspace, session: &session)
                var output = "exit \(result.exitCode)\n" + result.output
                if !changed.isEmpty {
                    output += "\n[Files changed by this command: " + changed.prefix(12).joined(separator: ", ") + (changed.count > 12 ? " and \(changed.count - 12) more" : "") + "]"
                }
                return finish(result.exitCode == 0 && !result.timedOut ? .completed : .failed, output)
            case "mcp_tools", "mcp_resources", "mcp_read", "mcp_call":
                // MCP servers run outside the sandbox and outside the project, so every call that can act asks first.
                guard let name = text("server"), let server = Mcp.enabled(home: home).first(where: { $0.name == name }) else {
                    throw MasonError("No MCP server named \(text("server") ?? "") is registered. Registered: " + Mcp.enabled(home: home).map(\.name).joined(separator: ", "))
                }
                if call.name == "mcp_call" {
                    guard let tool = text("tool"), server.tools.contains(tool) else { throw MasonError("Choose one of the tools allowed for \(server.name): " + server.tools.joined(separator: ", ")) }
                    guard let parsed = try? JSONSerialization.jsonObject(with: Data((text("arguments_json") ?? "{}").utf8)) as? [String: Any] else { throw MasonError("arguments_json must be a JSON object.") }
                    let shown = String(decoding: (try? JSONSerialization.data(withJSONObject: parsed, options: [.prettyPrinted, .sortedKeys])) ?? Data(), as: UTF8.self)
                    if !(await allowed(ApprovalRequest(tool: call.name, title: "Call \(server.name) / \(tool)", detail: server.transport + "\n" + shown, subject: server.name + "/" + tool), session: &session)) {
                        return finish(.denied, "The user declined this MCP call. Do not retry it; ask what they want instead.")
                    }
                    emit(.status("Calling \(server.name) / \(tool)"))
                    return finish(.completed, try await Mcp.request(server, .call(tool: tool, arguments: parsed)))
                }
                let uri = text("uri")
                if call.name == "mcp_read", uri == nil { throw MasonError("mcp_read needs a uri.") }
                // Starting a program, or reading a resource, asks first; listing what an HTTP server offers does not.
                if server.executable != nil || call.name == "mcp_read" {
                    let title = call.name == "mcp_read" ? "Read \(uri ?? "") from \(server.name)" : "Start \(server.name) to list what it offers"
                    if !(await allowed(ApprovalRequest(tool: call.name, title: title, detail: server.transport, subject: server.name + (uri.map { "/" + $0 } ?? "")), session: &session)) {
                        return finish(.denied, "The user declined. Do not retry it; ask what they want instead.")
                    }
                }
                let operation: Mcp.Operation = call.name == "mcp_read" ? .read(uri: uri ?? "") : call.name == "mcp_resources" ? .resources(all: false) : .tools(all: false)
                return finish(.completed, try await Mcp.request(server, operation))
            default:
                if let (state, result) = try await performExtra(call, arguments: arguments, in: workspace, session: &session) { return finish(state, result) }
                if let (state, result) = try await performDesktop(call, arguments: arguments, session: &session) { return finish(state, result) }
                throw MasonError("Unknown tool \(call.name). Use one of: " + offeredNames.joined(separator: ", "))
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
        if let index = session.turns[turn].changes.firstIndex(where: { $0.path == path }) {
            session.turns[turn].changes[index].after = after
            session.turns[turn].changes[index].afterBlob = nil
            session.turns[turn].changes[index].afterMissing = nil
        } else { session.turns[turn].changes.append(FileChange(path: path, before: before, after: after)) }
    }

    /// Records what a command created, changed or deleted, and returns the paths. Text is kept in the session like an
    /// edit; anything binary or over the text limit goes to the blob store.
    func recordCommandChanges(since before: [String: Snapshot.Entry], in workspace: Workspace, session: inout Session) -> [String] {
        let after = Snapshot.capture(workspace, previous: before)
        snapshot = after.entries
        let blobs = BlobStore(home: home)
        func split(_ data: Data?) -> (text: String?, blob: String?) {
            guard let data else { return (nil, nil) }
            if data.count <= Workspace.maxFileBytes, let text = String(data: data, encoding: .utf8) { return (text, nil) }
            return (nil, try? blobs.put(data))
        }
        let turn = session.turns.count - 1
        var paths: [String] = []
        for change in Snapshot.changes(from: before, to: after.entries) {
            let new = split(change.after)
            if change.after != nil, new.text == nil, new.blob == nil { continue }   // could not be saved, so it cannot be undone
            if let index = session.turns[turn].changes.firstIndex(where: { $0.path == change.path }) {
                session.turns[turn].changes[index].after = new.text ?? ""
                session.turns[turn].changes[index].afterBlob = new.blob
                session.turns[turn].changes[index].afterMissing = change.after == nil ? true : nil
            } else {
                let old = split(change.before)
                if change.before != nil, old.text == nil, old.blob == nil { continue }
                session.turns[turn].changes.append(FileChange(path: change.path, before: old.text, after: new.text ?? "", beforeBlob: old.blob, afterBlob: new.blob,
                                                              afterMissing: change.after == nil ? true : nil, byCommand: true))
            }
            paths.append(change.path)
        }
        return paths
    }

    public struct UndoResult { public var restored: [String] = []; public var skipped: [String] = [] }

    /// Puts back every file the latest request changed, whether an edit tool or a command changed it.
    /// A file changed again since is left alone and reported.
    public static func undoLatest(_ session: inout Session, store: SessionStore, redo: Bool = false) throws -> UndoResult? {
        guard let index = session.turns.lastIndex(where: { !$0.changes.isEmpty && $0.undone == redo }) else { return nil }
        let workspace = try Workspace(path: session.workspace)
        let blobs = BlobStore(home: store.folder.deletingLastPathComponent())
        var result = UndoResult()
        for change in session.turns[index].changes {
            // nil means "the file does not exist" on that side, so errors are kept apart from nil here.
            let before: Data?, after: Data?, current: Data?
            do { before = try change.beforeBytes(blobs); after = try change.afterBytes(blobs); current = try workspace.data(change.path) }
            catch { result.skipped.append(change.path); continue }
            let expected = redo ? before : after
            let target = redo ? after : before
            guard current == expected else { result.skipped.append(change.path); continue }
            if let target { try workspace.write(change.path, data: target) } else { try workspace.delete(change.path) }
            result.restored.append(change.path)
        }
        session.turns[index].undone = !redo
        let note = redo ? "The user reapplied the changes of an earlier request." : "The user undid the file changes of the request \"\(session.turns[index].request.prefix(200))\"."
        session.pendingNote = note + " Read affected files again before relying on what you saw earlier."
        try store.save(session)
        return result
    }
}
