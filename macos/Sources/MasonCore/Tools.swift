import Foundation

/// Named commands a project defines in `mason.workflows.json`, such as its tests or its linter:
/// `{"version": 1, "workflows": {"test": {"executable": "python3", "arguments": ["-m", "unittest"], "directory": "."}}}`
public struct ProjectWorkflows {
    public struct Workflow: Equatable { public let executable: String; public let arguments: [String]; public let directory: String }
    public let workflows: [String: Workflow]

    public static func load(_ workspace: Workspace) throws -> ProjectWorkflows {
        guard workspace.exists("mason.workflows.json") else { return ProjectWorkflows(workflows: [:]) }
        guard let object = try? JSONSerialization.jsonObject(with: Data(try workspace.read("mason.workflows.json").utf8)) as? [String: Any],
              object["version"] as? Int == 1, let listed = object["workflows"] as? [String: Any], listed.count <= 24 else {
            throw MasonError("mason.workflows.json must be version 1 with at most 24 workflows.")
        }
        var found: [String: Workflow] = [:]
        for (name, value) in listed {
            guard name.range(of: #"^[A-Za-z][A-Za-z0-9_-]{0,49}$"#, options: .regularExpression) != nil, let item = value as? [String: Any],
                  let executable = item["executable"] as? String, !executable.trimmingCharacters(in: .whitespaces).isEmpty,
                  let arguments = item["arguments"] as? [String] ?? (item["arguments"] == nil ? [] : nil), arguments.count <= 40, !arguments.contains(where: { $0.count > 4000 }) else {
                throw MasonError("The workflow \(name) in mason.workflows.json is not valid.")
            }
            let directory = item["directory"] as? String ?? "."
            if directory != "." { _ = try workspace.resolve(directory) }
            found[name] = Workflow(executable: executable, arguments: arguments, directory: directory)
        }
        return ProjectWorkflows(workflows: found)
    }

    /// A word quoted so the shell passes it on exactly.
    static func quoted(_ word: String) -> String { "'" + word.replacingOccurrences(of: "'", with: "'\\''") + "'" }

    /// The shell command that runs a workflow from the project folder.
    public static func commandLine(_ workflow: Workflow) -> String {
        let command = ([workflow.executable] + workflow.arguments).map(quoted).joined(separator: " ")
        return workflow.directory == "." ? command : "cd \(quoted(workflow.directory)) && \(command)"
    }
}

/// Instructions a project keeps for particular kinds of work, each in `.mason/skills/<name>/SKILL.md`
/// (also `.claude/skills` and `.agents/skills`). The agent sees their names and reads one when it needs it.
public enum Skills {
    static let folders = [".mason/skills", ".claude/skills", ".agents/skills"]

    public static func list(_ workspace: Workspace) -> [Definition] {
        var found: [String: Definition] = [:]
        for folder in folders {
            let root = workspace.root.appendingPathComponent(folder)
            let names = ((try? FileManager.default.contentsOfDirectory(atPath: root.path)) ?? []).sorted().prefix(64)
            for name in names where Customization.validName(name) && found[name.lowercased()] == nil {
                let relative = "\(folder)/\(name)/SKILL.md"
                // Read through the workspace, so a link out of the project or an oversized file is refused.
                guard let text = try? workspace.read(relative) else { continue }
                found[name.lowercased()] = Customization.parse(name: name, text: text, source: relative)
            }
        }
        return found.values.sorted { $0.name.lowercased() < $1.name.lowercased() }
    }
}

/// An outline of a project for the model: each source file with its size and the top-level things it defines.
public enum RepositoryMap {
    static let maxFiles = 400, maxBytes = 48 * 1024

    /// One pattern per family of languages; each captures the name being defined.
    static let patterns: [(extensions: Set<String>, pattern: String)] = [
        (["py"], #"^(?:async\s+)?(?:def|class)\s+([A-Za-z_][A-Za-z0-9_]*)"#),
        (["js", "jsx", "ts", "tsx", "mjs", "cjs"], #"^(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:function\*?|class|interface|type|enum|const)\s+([A-Za-z_$][A-Za-z0-9_$]*)"#),
        (["swift"], #"^(?:public\s+|private\s+|internal\s+|fileprivate\s+|open\s+|final\s+|static\s+)*(?:func|class|struct|enum|protocol|extension|actor)\s+([A-Za-z_][A-Za-z0-9_.]*)"#),
        (["go"], #"^(?:func\s+(?:\([^)]*\)\s*)?|type\s+)([A-Za-z_][A-Za-z0-9_]*)"#),
        (["rs"], #"^(?:pub(?:\([a-z]+\))?\s+)?(?:async\s+)?(?:fn|struct|enum|trait|impl|mod)\s+([A-Za-z_][A-Za-z0-9_]*)"#),
        (["c", "h", "cc", "cpp", "cxx", "hpp", "hh"], #"^(?:class|struct|enum(?:\s+class)?|namespace)\s+([A-Za-z_][A-Za-z0-9_]*)"#),
        (["cs", "java", "kt"], #"^\s{0,8}(?:public|internal|private|protected)\s+(?:static\s+|final\s+|sealed\s+|abstract\s+|partial\s+|data\s+)*(?:class|interface|enum|record|struct|object|fun)\s+([A-Za-z_][A-Za-z0-9_]*)"#),
        (["rb"], #"^\s{0,4}(?:def|class|module)\s+([A-Za-z_][A-Za-z0-9_.?!]*)"#),
        (["php"], #"^\s{0,4}(?:abstract\s+|final\s+)?(?:function|class|interface|trait)\s+([A-Za-z_][A-Za-z0-9_]*)"#),
    ]

    public static func outline(_ workspace: Workspace, under folder: String = ".") throws -> String {
        let listing = try workspace.files(under: folder, limit: maxFiles)
        var lines: [String] = []
        var size = 0
        var shown = 0
        for path in listing.paths {
            let kind = (path as NSString).pathExtension.lowercased()
            var entry = path
            if let text = try? workspace.read(path) {
                let rows = text.split(separator: "\n", omittingEmptySubsequences: false)
                entry += "  (\(rows.count) lines)"
                if let pattern = patterns.first(where: { $0.extensions.contains(kind) })?.pattern, let expression = try? NSRegularExpression(pattern: pattern) {
                    var names: [String] = []
                    for row in rows where names.count < 40 {
                        let line = String(row.prefix(300))
                        guard let match = expression.firstMatch(in: line, range: NSRange(line.startIndex..., in: line)), match.numberOfRanges > 1, let range = Range(match.range(at: 1), in: line) else { continue }
                        names.append(String(line[range]))
                    }
                    if !names.isEmpty { entry += ": " + names.joined(separator: ", ") }
                }
            }
            if size + entry.utf8.count > maxBytes { break }
            lines.append(entry); size += entry.utf8.count + 1; shown += 1
        }
        if lines.isEmpty { return "No files." }
        var footer = ""
        if shown < listing.paths.count || listing.truncated { footer = "\n[\(shown) files shown. Map a subfolder to see more.]" }
        return lines.joined(separator: "\n") + footer
    }
}

/// Programs the agent starts and leaves running during one request: a dev server, a watcher, a long test run.
/// Every job is stopped when the request ends.
final class Jobs: @unchecked Sendable {
    final class Job {
        let command: String
        let output = OutputBuffer(limit: 256 * 1024)
        var task: Task<CommandResult?, Never>?
        var result: CommandResult?
        var failure: String?
        init(command: String) { self.command = command }
    }
    private let lock = NSLock()
    private var jobs: [String: Job] = [:]
    static let maxRunning = 3, maxTotal = 24
    static let timeout: TimeInterval = 30 * 60

    private func locked<T>(_ body: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return body() }

    func start(_ command: String, workspace: Workspace, sandboxed: Bool) throws -> String {
        let id = String(UUID().uuidString.prefix(8)).lowercased()
        let job = Job(command: command)
        let problem: String? = locked {
            if jobs.count >= Jobs.maxTotal { return "This request has started \(Jobs.maxTotal) jobs already." }
            if jobs.values.filter({ $0.result == nil && $0.failure == nil }).count >= Jobs.maxRunning { return "Three jobs are running. Stop one before starting another." }
            if jobs.values.contains(where: { $0.command == command && $0.result == nil && $0.failure == nil }) { return "That command is already running as a job. Poll or stop it." }
            jobs[id] = job
            return nil
        }
        if let problem { throw MasonError(problem) }
        job.task = Task { [self] in
            do {
                let result = try await Shell.run(command, in: workspace, sandboxed: sandboxed, timeout: Jobs.timeout) { job.output.append($0) }
                locked { job.result = result }
                return result
            } catch {
                locked { job.failure = error is CancellationError ? "stopped" : ((error as? MasonError)?.description ?? error.localizedDescription) }
                return nil
            }
        }
        return id
    }

    /// The job's state and the end of its output so far. `stop` ends it first; otherwise waits up to `waitMs` for it to finish.
    func observe(_ id: String, stop: Bool, waitMs: Int) async throws -> String {
        guard let job = locked({ jobs[id] }) else { throw MasonError("No job \(id) in this request. Jobs: " + locked { jobs.keys.sorted().joined(separator: ", ") }) }
        guard (0...10_000).contains(waitMs) else { throw MasonError("wait_ms must be between 0 and 10000.") }
        if stop { job.task?.cancel(); _ = await job.task?.value }
        else if waitMs > 0 {
            let deadline = Date().addingTimeInterval(Double(waitMs) / 1000)
            while Date() < deadline, locked({ job.result == nil && job.failure == nil }) { try await Task.sleep(nanoseconds: 50_000_000) }
        }
        let (result, failure) = locked { (job.result, job.failure) }
        let text = job.output.snapshot().0
        let tail = text.count > 8000 ? "[… earlier output omitted]\n" + String(text.suffix(8000)) : text
        let state = result.map { "exited \($0.exitCode)" + ($0.timedOut ? " (stopped at the 30 minute limit)" : "") } ?? failure ?? "running"
        return "job \(id): \(state)\n" + tail
    }

    func stopAll() async {
        let all = locked { Array(jobs.values) }
        for job in all { job.task?.cancel() }
        for job in all { _ = await job.task?.value }
        locked { jobs = [:] }
    }

    var isEmpty: Bool { locked { jobs.isEmpty } }
}

extension Agent {
    /// Tools beyond the basic file and shell ones. Which of them a request is offered depends on the session and the project.
    static func extraTools(session: Session, workspace: Workspace) -> [ToolSpec] {
        func schema(_ properties: [String: Any], required: [String]) -> [String: Any] {
            ["type": "object", "properties": properties, "required": required, "additionalProperties": false]
        }
        let text: [String: Any] = ["type": "string"]
        var tools = [
            ToolSpec(name: "read_lines", description: "Read up to 200 numbered lines of a text file from a starting line. Use it for large files instead of read_file.",
                     parameters: schema(["path": text, "start": ["type": "integer", "minimum": 1], "count": ["type": "integer", "minimum": 1, "maximum": 200]], required: ["path", "start", "count"])),
            ToolSpec(name: "git_status", description: "Show the Git branch and which files are changed, staged or untracked.", parameters: schema([:], required: [])),
            ToolSpec(name: "delegate_readonly", description: "Ask one question about 1 to 8 named files in a separate context that can only read them. Returns findings with line numbers. Use it to keep long files out of this conversation.",
                     parameters: schema(["question": text, "paths": ["type": "array", "items": text, "minItems": 1, "maxItems": 8]], required: ["question", "paths"])),
        ]
        tools.append(ToolSpec(name: "repository_map", description: "An outline of the project: its source files with their sizes and the functions, classes and types each defines. Use it to find where things live before reading files.",
                              parameters: schema(["path": ["type": "string", "description": "Folder to map, relative to the project. Use . for the whole project."]], required: ["path"])))
        tools.append(ToolSpec(name: "ask_user", description: "Ask the user one question when you cannot continue without their answer (a choice only they can make, or a missing fact). The request stops and their reply continues it. Do not use it for things you can find out yourself.",
                              parameters: schema(["question": text], required: ["question"])))
        let readOnly = session.askOnly || session.permission == .readOnly
        if !readOnly {
            tools.append(ToolSpec(name: "apply_edits", description: "Make several exact replacements in one existing file at once. Each old_text must match exactly once. Nothing is written unless every edit applies.",
                                  parameters: schema(["path": text, "edits": ["type": "array", "minItems": 1, "maxItems": 32, "items": schema(["old_text": text, "new_text": text], required: ["old_text", "new_text"])]], required: ["path", "edits"])))
            tools.append(ToolSpec(name: "start_job", description: "Start a long-running shell command (a server, a watcher, a long test run) and return at once with a job id. Read it with poll_job. Jobs stop when the request ends.",
                                  parameters: schema(["command": text], required: ["command"])))
            tools.append(ToolSpec(name: "poll_job", description: "Read a job's state and recent output, waiting up to wait_ms for it to finish.",
                                  parameters: schema(["id": text, "wait_ms": ["type": "integer", "minimum": 0, "maximum": 10000]], required: ["id", "wait_ms"])))
            tools.append(ToolSpec(name: "stop_job", description: "Stop a job and return its output.", parameters: schema(["id": text], required: ["id"])))
            if let workflows = try? ProjectWorkflows.load(workspace), !workflows.workflows.isEmpty {
                tools.append(ToolSpec(name: "run_workflow", description: "Run one of the project's named workflows: " + workflows.workflows.keys.sorted().joined(separator: ", ") + ".",
                                      parameters: schema(["name": text], required: ["name"])))
            }
        }
        if BrowserCheck.findBrowser() != nil {
            tools.append(ToolSpec(name: "check_page", description: "Load one of the project's HTML pages in a real browser and report what it shows and any script errors. steps_json is a JSON array of steps carried out in order, each one of: {\"action\":\"click\",\"text\":\"Add\"}, {\"action\":\"fill\",\"selector\":\"#name\",\"value\":\"Ada\"}, {\"action\":\"expect_text\",\"text\":\"Saved\"}, {\"action\":\"expect_no_text\",\"text\":\"Error\"}, {\"action\":\"wait\",\"ms\":500}. Use text or selector to name an element. Use [] to only load the page. The page has no network beyond this computer.",
                                  parameters: schema(["path": text, "steps_json": text], required: ["path", "steps_json"])))
        }
        if !Skills.list(workspace).isEmpty {
            tools.append(ToolSpec(name: "read_skill", description: "Read the project's instructions for one kind of work, by skill name.", parameters: schema(["name": text], required: ["name"])))
        }
        return tools
    }

    /// Carries out one of the extra tools. Returns nil when the tool is not one of them.
    func performExtra(_ call: ToolCall, arguments: [String: Any], in workspace: Workspace, session: inout Session) async throws -> (Activity.State, String)? {
        func text(_ key: String) -> String? { arguments[key] as? String }
        func number(_ key: String) -> Int? { (arguments[key] as? Int) ?? (arguments[key] as? Double).map(Int.init) ?? (arguments[key] as? String).flatMap(Int.init) }
        let readOnly = session.askOnly || session.permission == .readOnly
        let sandboxed = session.permission == .auto
        switch call.name {
        case "read_lines":
            guard let path = text("path"), let start = number("start"), let count = number("count"), start >= 1, (1...200).contains(count) else { throw MasonError("read_lines needs a path, a start line of 1 or more, and a count of 1 to 200.") }
            guard let data = try workspace.data(path) else { throw MasonError("\(path) does not exist.") }
            guard data.count <= 64 * 1024 * 1024 else { throw MasonError("\(path) is larger than 64 MiB.") }
            let lines = String(decoding: data, as: UTF8.self).split(separator: "\n", omittingEmptySubsequences: false)
            guard start <= lines.count else { throw MasonError("\(path) has \(lines.count) lines.") }
            let end = min(lines.count, start + count - 1)
            let shown = (start...end).map { "\($0)\t\(lines[$0 - 1].prefix(2000))" }.joined(separator: "\n")
            return (.completed, shown + "\n[lines \(start)–\(end) of \(lines.count)]")

        case "repository_map":
            return (.completed, try RepositoryMap.outline(workspace, under: text("path") ?? "."))

        case "ask_user":
            guard let question = text("question")?.trimmingCharacters(in: .whitespacesAndNewlines), !question.isEmpty, question.count <= 2000 else { throw MasonError("ask_user needs a question of up to 2,000 characters.") }
            pendingQuestion = question
            return (.completed, "Asked the user. The request stops here; their reply will come as the next message.")

        case "check_page":
            guard let path = text("path") else { throw MasonError("check_page needs the path of an HTML file in the project.") }
            guard let steps = try? JSONSerialization.jsonObject(with: Data((text("steps_json") ?? "[]").utf8)) as? [[String: Any]] else { throw MasonError("steps_json must be a JSON array of steps.") }
            // The page's scripts run in a browser outside the command sandbox, so outside Auto this asks like a command.
            if session.permission != .auto {
                let detail = "Opens \(path) in a headless browser with a fresh profile and no network beyond this computer, and carries out \(steps.count) step\(steps.count == 1 ? "" : "s")."
                if !(await allowed(ApprovalRequest(tool: call.name, title: "Check \(path) in a browser", detail: detail, subject: path), session: &session)) {
                    return (.denied, "The user declined. Do not retry it; ask what they want instead.")
                }
            }
            emit(.status("Checking \(path) in a browser"))
            let result = try await BrowserCheck.run(workspace, page: path, steps: steps)
            return (result.passed ? .completed : .failed, BrowserCheck.report(result, page: path))

        case "git_status":
            guard FileManager.default.fileExists(atPath: workspace.root.appendingPathComponent(".git").path) else { return (.completed, "This project is not a Git repository.") }
            // Nothing in the repository's own settings is allowed to start a program for this.
            let command = "git -c core.fsmonitor= -c core.hooksPath=/dev/null --no-optional-locks status --porcelain=v1 --branch"
            let result = try await Shell.run(command, in: workspace, sandboxed: Shell.sandboxAvailable, timeout: 20)
            return (result.exitCode == 0 ? .completed : .failed, result.output.isEmpty ? "Nothing to report." : result.output)

        case "delegate_readonly":
            guard let question = text("question"), !question.trimmingCharacters(in: .whitespaces).isEmpty, let paths = arguments["paths"] as? [String], (1...8).contains(paths.count) else {
                throw MasonError("delegate_readonly needs a question and 1 to 8 paths.")
            }
            var files = ""
            for path in paths {
                let numbered = try workspace.read(path).split(separator: "\n", omittingEmptySubsequences: false).enumerated().map { "\($0.offset + 1)\t\($0.element)" }.joined(separator: "\n")
                files += "File \(path):\n" + (numbered.count > 60_000 ? String(numbered.prefix(60_000)) + "\n[File clipped.]" : numbered) + "\n\n"
            }
            emit(.status("Asking about \(paths.count) file\(paths.count == 1 ? "" : "s")"))
            let ask = [
                ChatMessage(role: "system", content: "You answer one question about the files you are given. You cannot edit, run or read anything else. Cite findings as path:line. Say so when the files do not answer the question."),
                ChatMessage(role: "user", content: files + "Question: " + question),
            ]
            let reply = try await model.complete(messages: ask, tools: []) { _ in }
            let answer = reply.text.trimmingCharacters(in: .whitespacesAndNewlines)
            return answer.isEmpty ? (.failed, "The separate context returned nothing.") : (.completed, answer)

        case "apply_edits":
            if readOnly { throw MasonError("This session is read-only. Tell the user what you would change instead.") }
            guard let path = text("path"), let edits = arguments["edits"] as? [[String: Any]], (1...32).contains(edits.count) else { throw MasonError("apply_edits needs a path and 1 to 32 edits.") }
            guard workspace.exists(path) else { throw MasonError("\(path) does not exist. Use write_file to create it.") }
            let before = try workspace.read(path)
            var after = before
            for (index, edit) in edits.enumerated() {
                guard let old = edit["old_text"] as? String, let new = edit["new_text"] as? String, !old.isEmpty else { throw MasonError("Edit \(index + 1) needs old_text and new_text.") }
                let occurrences = after.components(separatedBy: old).count - 1
                guard occurrences == 1 else {
                    throw MasonError("Edit \(index + 1): old_text " + (occurrences == 0 ? "was not found" : "matches \(occurrences) places") + " in \(path). Nothing was changed. Read the file again and make each old_text match exactly once.")
                }
                after = after.replacingOccurrences(of: old, with: new)
            }
            if session.permission == .reviewEdits {
                let rows = Diff.lines(before: before, after: after).filter { $0.kind != .same }.prefix(60).map { "\($0.kind.rawValue) \($0.text)" }.joined(separator: "\n")
                if !(await allowed(ApprovalRequest(tool: call.name, title: "Edit \(path) (\(edits.count) changes)", detail: rows, subject: path), session: &session)) {
                    return (.denied, "The user declined these edits. Do not retry them; ask what they want instead.")
                }
            }
            try workspace.write(path, after)
            record(path: path, before: before, after: after, session: &session)
            let counts = Diff.counts(before: before, after: after)
            return (.completed, "Edited \(path) with \(edits.count) change\(edits.count == 1 ? "" : "s") (+\(counts.added) −\(counts.removed)).")

        case "start_job", "run_workflow":
            if readOnly { throw MasonError("This session is read-only. Commands are not available.") }
            let command: String, title: String, subject: String
            if call.name == "run_workflow" {
                guard let name = text("name"), let workflow = try ProjectWorkflows.load(workspace).workflows[name] else { throw MasonError("No workflow named \(text("name") ?? "") in mason.workflows.json.") }
                command = ProjectWorkflows.commandLine(workflow); title = "Run the workflow \(name): \(command)"; subject = "workflow " + name
            } else {
                guard let given = text("command"), !given.isEmpty else { throw MasonError("start_job needs a command.") }
                command = given; title = "Start in the background: \(given)"; subject = given
            }
            if !sandboxed {
                let detail = "Runs in \(workspace.root.path) with your account and environment. It is not sandboxed." + (call.name == "start_job" ? " It keeps running until this request ends, for at most 30 minutes." : " Limit: 120 seconds.")
                if !(await allowed(ApprovalRequest(tool: call.name, title: title, detail: detail, subject: subject), session: &session)) {
                    return (.denied, "The user declined. Do not retry it; ask what they want instead.")
                }
            }
            if call.name == "start_job" {
                let id = try jobs.start(command, workspace: workspace, sandboxed: sandboxed)
                return (.completed, "Started job \(id). Read it with poll_job; it stops when this request ends.")
            }
            emit(.status("Running the workflow"))
            let before = Snapshot.capture(workspace, previous: snapshot)
            let result: CommandResult
            do { result = try await Shell.run(command, in: workspace, sandboxed: sandboxed) }
            catch { _ = recordCommandChanges(since: before.entries, in: workspace, session: &session); throw error }
            let changed = recordCommandChanges(since: before.entries, in: workspace, session: &session)
            let output = "exit \(result.exitCode)\n" + result.output + (changed.isEmpty ? "" : "\n[Files changed by this workflow: " + changed.prefix(12).joined(separator: ", ") + "]")
            return (result.exitCode == 0 && !result.timedOut ? .completed : .failed, output)

        case "poll_job", "stop_job":
            guard let id = text("id") else { throw MasonError("\(call.name) needs the job id.") }
            return (.completed, try await jobs.observe(id, stop: call.name == "stop_job", waitMs: call.name == "poll_job" ? (number("wait_ms") ?? 0) : 0))

        case "read_skill":
            let skills = Skills.list(workspace)
            guard let name = text("name"), let skill = skills.first(where: { $0.name.lowercased() == name.lowercased() }) else {
                throw MasonError("No skill named \(text("name") ?? ""). Skills: " + skills.map(\.name).joined(separator: ", "))
            }
            return (.completed, "Skill \(skill.name) (\(skill.source)):\n" + skill.body)

        default:
            return nil
        }
    }
}
