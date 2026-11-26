import Foundation

/// A background task is one request run by a separate `mason` process that keeps going after the app or the terminal
/// that started it has closed. The worker is the only writer of its own record; anyone else reads the record, answers
/// the approval it is waiting on, or asks it to stop, through files in the task's folder.
public struct BackgroundTask: Codable, Equatable {
    public var id: String
    public var workspace: String
    public var prompt: String
    public var endpoint: ModelEndpoint
    /// Auto: edits and sandboxed commands without asking. Otherwise each edit and command waits for an answer.
    public var auto: Bool
    public var created: Date
}

public struct TaskApproval: Codable, Equatable {
    public var id: String
    public var tool: String
    public var title: String
    public var detail: String
    public var subject: String
}

public struct TaskStatus: Codable, Equatable {
    public var state: String
    public var detail: String
    public var pid: Int32
    public var started: Date
    public var session: String?
    public var approval: TaskApproval?
    public var active: Bool { ["Starting", "Running", "Waiting for approval"].contains(state) }
}

public enum BackgroundTasks {
    static func folder(_ id: String, _ home: URL) -> URL { home.appendingPathComponent("background-tasks").appendingPathComponent(id) }
    static func checked(_ id: String) throws -> String {
        guard id.range(of: "^[a-f0-9]{8,32}$", options: .regularExpression) != nil else { throw MasonError("That is not a task id.") }
        return id
    }
    static var encoder: JSONEncoder { let made = JSONEncoder(); made.dateEncodingStrategy = .iso8601; made.outputFormatting = [.sortedKeys]; return made }
    static var decoder: JSONDecoder { let made = JSONDecoder(); made.dateDecodingStrategy = .iso8601; return made }
    static func read<T: Decodable>(_ type: T.Type, _ file: URL) -> T? {
        guard let data = try? Data(contentsOf: file), data.count <= 2 * 1024 * 1024 else { return nil }
        return try? decoder.decode(type, from: data)
    }
    static func write<T: Encodable>(_ value: T, _ file: URL) { try? encoder.encode(value).write(to: file, options: .atomic) }

    // MARK: for whoever starts and watches a task

    /// Records the task and starts its worker, `program task-run --id ID`, detached from this process. Returns the id.
    public static func start(program: String, workspace: Workspace, prompt: String, endpoint: ModelEndpoint, auto: Bool, home: URL = DataHome.url) throws -> String {
        let prompt = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (1...16_000).contains(prompt.count) else { throw MasonError("Describe the task in 1–16,000 characters.") }
        _ = try ModelEndpoint.validated(endpoint.url)
        guard FileManager.default.isExecutableFile(atPath: program) else { throw MasonError("The mason program that would run the task was not found at \(program).") }
        let id = String(UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: "").prefix(12))
        let place = folder(id, home)
        try FileManager.default.createDirectory(at: place, withIntermediateDirectories: true)
        write(BackgroundTask(id: id, workspace: realPath(workspace.root.path), prompt: prompt, endpoint: endpoint, auto: auto, created: Date()), place.appendingPathComponent("request.json"))
        write(TaskStatus(state: "Starting", detail: "", pid: 0, started: Date()), place.appendingPathComponent("status.json"))
        // The shell starts the worker and leaves at once; the worker then takes its own session, so closing this
        // terminal or app does not reach it.
        let launcher = Process()
        launcher.executableURL = URL(fileURLWithPath: "/bin/sh")
        launcher.arguments = ["-c", "\"$0\" task-run --id \"$1\" --task-home \"$2\" </dev/null >>\"$3\" 2>&1 &", program, id, home.path, place.appendingPathComponent("worker.log").path]
        launcher.standardInput = FileHandle.nullDevice; launcher.standardOutput = FileHandle.nullDevice; launcher.standardError = FileHandle.nullDevice
        do { try launcher.run() } catch { throw MasonError("The task could not start: \(error.localizedDescription)") }
        return id
    }

    /// A task's state. A worker that has gone without finishing is reported as such.
    public static func status(_ id: String, home: URL = DataHome.url) throws -> TaskStatus {
        guard var found = read(TaskStatus.self, folder(try checked(id), home).appendingPathComponent("status.json")) else { throw MasonError("No task \(id).") }
        if found.active, found.pid > 0, kill(found.pid, 0) != 0, errno == ESRCH {
            found.state = "Stopped"; found.detail = "The worker is no longer running and did not finish."; found.approval = nil
        } else if found.state == "Starting", Date().timeIntervalSince(found.started) > 30 {
            found.state = "Failed"; found.detail = "The worker did not start. See worker.log in the task's folder."
        }
        return found
    }

    /// Every task, newest first; only one project's when `workspace` is given.
    public static func all(workspace: Workspace? = nil, home: URL = DataHome.url) -> [(task: BackgroundTask, status: TaskStatus)] {
        let root = workspace.map { realPath($0.root.path) }
        let ids = (try? FileManager.default.contentsOfDirectory(atPath: home.appendingPathComponent("background-tasks").path)) ?? []
        var found: [(task: BackgroundTask, status: TaskStatus)] = []
        for id in ids {
            guard let task = read(BackgroundTask.self, folder(id, home).appendingPathComponent("request.json")), root == nil || task.workspace == root, let state = try? status(id, home: home) else { continue }
            found.append((task, state))
        }
        return found.sorted { $0.task.created > $1.task.created }
    }

    /// The end of what the task has done so far, one JSON object per line.
    public static func tail(_ id: String, lines: Int = 40, home: URL = DataHome.url) throws -> [String] {
        let text = (try? String(contentsOf: folder(try checked(id), home).appendingPathComponent("events.jsonl"), encoding: .utf8)) ?? ""
        return Array(text.split(separator: "\n").suffix(lines).map(String.init))
    }

    /// Answers the approval the task is waiting on. `request` must be that approval's id, so an answer meant for an
    /// earlier question cannot allow a later one.
    public static func decide(_ id: String, request: String, allow: Bool, home: URL = DataHome.url) throws {
        let found = try status(id, home: home)
        guard found.state == "Waiting for approval", let approval = found.approval else { throw MasonError("Task \(id) is not waiting for an approval.") }
        guard approval.id == request else { throw MasonError("Task \(id) is now waiting on a different approval (\(approval.id)). Look at it again.") }
        write(["request": request, "decision": allow ? "allow" : "deny"], folder(id, home).appendingPathComponent("decision.json"))
    }

    public static func cancel(_ id: String, home: URL = DataHome.url) throws {
        _ = try status(id, home: home)
        FileManager.default.createFile(atPath: folder(id, home).appendingPathComponent("cancel").path, contents: nil)
    }

    // MARK: the worker

    /// Runs the task to its end. This is what `mason task-run` does. `model` replaces the endpoint in tests.
    public static func work(_ id: String, home: URL = DataHome.url, model: ChatModel? = nil) async -> Int32 {
        guard let id = try? checked(id), let task = read(BackgroundTask.self, folder(id, home).appendingPathComponent("request.json")) else { return 1 }
        let place = folder(id, home)
        let record = StatusRecord(TaskStatus(state: "Running", detail: "", pid: getpid(), started: Date()), place.appendingPathComponent("status.json"))
        let events = place.appendingPathComponent("events.jsonl")
        func log(_ type: String, _ data: [String: Any]) {
            guard let line = try? JSONSerialization.data(withJSONObject: ["type": type, "data": data, "at": ISO8601DateFormatter().string(from: Date())], options: [.sortedKeys, .withoutEscapingSlashes]) else { return }
            if let handle = try? FileHandle(forWritingTo: events) { _ = try? handle.seekToEnd(); try? handle.write(contentsOf: line + Data("\n".utf8)); try? handle.close() }
            else { try? (line + Data("\n".utf8)).write(to: events) }
        }
        let cancelFile = place.appendingPathComponent("cancel").path, decisionFile = place.appendingPathComponent("decision.json")
        let main = Task { () -> Int32 in
            do {
                let chosen = try model ?? OpenAIClient(task.endpoint)
                let workspace = try Workspace(path: task.workspace)
                let store = try SessionStore(home: home)
                var session = try store.create(workspace: workspace.root.path, permission: task.auto ? .auto : .reviewEdits)
                session.title = "Background: " + String(task.prompt.prefix(60))
                record.change { $0.session = session.id.uuidString }
                log("task.started", ["session": session.id.uuidString, "workspace": task.workspace, "mode": task.auto ? "auto" : "review"])
                let agent = Agent(store: store, model: chosen, approve: { request in
                    let approval = TaskApproval(id: String(UUID().uuidString.lowercased().prefix(8)), tool: request.tool, title: request.title, detail: String(request.detail.prefix(4000)), subject: request.subject)
                    record.change { $0.state = "Waiting for approval"; $0.approval = approval }
                    log("approval.required", ["id": approval.id, "tool": approval.tool, "title": approval.title, "subject": approval.subject])
                    var allowed = false
                    while !Task.isCancelled, !FileManager.default.fileExists(atPath: cancelFile) {
                        if let answer = read([String: String].self, decisionFile) {
                            try? FileManager.default.removeItem(at: decisionFile)
                            if answer["request"] == approval.id { allowed = answer["decision"] == "allow"; break }
                        }
                        try? await Task.sleep(nanoseconds: 200_000_000)
                    }
                    record.change { $0.state = "Running"; $0.approval = nil }
                    log("approval.decided", ["id": approval.id, "allowed": allowed])
                    return allowed
                }, emit: { change in
                    if case .activityFinished(let activity) = change { log("tool.result", ["tool": activity.tool, "subject": activity.subject, "state": "\(activity.state)", "result": String(activity.result.prefix(2000))]) }
                })
                await agent.run(&session, request: task.prompt)
                let stopped = FileManager.default.fileExists(atPath: cancelFile)
                record.change { $0.state = stopped ? "Cancelled" : session.state; $0.detail = session.detail; $0.approval = nil }
                log("task.finished", ["state": stopped ? "Cancelled" : session.state, "detail": session.detail, "answer": session.turns.last?.answer ?? ""])
                return session.state == "Completed" && !stopped ? 0 : 2
            } catch {
                let message = (error as? MasonError)?.description ?? error.localizedDescription
                record.change { $0.state = "Failed"; $0.detail = message; $0.approval = nil }
                log("task.finished", ["state": "Failed", "detail": message])
                return 1
            }
        }
        let watcher = Task {
            while !Task.isCancelled {
                if FileManager.default.fileExists(atPath: cancelFile) { main.cancel(); return }
                try? await Task.sleep(nanoseconds: 300_000_000)
            }
        }
        let code = await main.value
        watcher.cancel()
        return code
    }

    /// The worker's own record, changed and written under a lock because approvals and tool results arrive on different tasks.
    final class StatusRecord: @unchecked Sendable {
        private let lock = NSLock()
        private var value: TaskStatus
        private let file: URL
        init(_ value: TaskStatus, _ file: URL) { self.value = value; self.file = file; BackgroundTasks.write(value, file) }
        func change(_ body: (inout TaskStatus) -> Void) { lock.lock(); body(&value); BackgroundTasks.write(value, file); lock.unlock() }
    }

    /// The worker leaves the terminal or app that started it: its own session, and no hang-up when that one closes.
    public static func detach() {
        _ = setsid()
        signal(SIGHUP, SIG_IGN)
    }

    // MARK: the /task command, shared by the command line and the app

    public static let usage = """
        /task                     This project's background tasks
        /task start REQUEST       Run a request in the background; it keeps going after you close Mason
        /task show ID             What a task has done, and the approval it is waiting on
        /task allow ID            Allow what the task is waiting on (deny: /task deny ID)
        /task cancel ID           Stop a task
        A task started in Auto edits files and runs sandboxed commands without asking. Otherwise each edit and command waits for /task allow.
        """

    static func summary(_ task: BackgroundTask, _ status: TaskStatus) -> String {
        "\(task.id)  \(status.state)\(status.detail.isEmpty ? "" : " (\(status.detail))")  ·  \(task.prompt.prefix(70).replacingOccurrences(of: "\n", with: " "))"
            + (status.approval.map { "\n    waiting on: \($0.title)  ·  /task allow \(task.id) or /task deny \(task.id)" } ?? "")
    }

    public static func command(_ rest: String, workspace: Workspace, endpoint: ModelEndpoint?, auto: Bool, program: String, home: URL = DataHome.url) throws -> String {
        let trimmed = rest.trimmingCharacters(in: .whitespaces)
        let verb = trimmed.split(separator: " ").first.map { $0.lowercased() } ?? ""
        let argument = String(trimmed.dropFirst(verb.count)).trimmingCharacters(in: .whitespaces)
        switch verb {
        case "", "list":
            let mine = all(workspace: workspace, home: home)
            return mine.isEmpty ? "No background tasks in this project. /task start REQUEST runs one." : mine.prefix(20).map { summary($0.task, $0.status) }.joined(separator: "\n")
        case "start":
            guard let endpoint else { throw MasonError("Choose a model first; a background task uses the one this session uses.") }
            let id = try start(program: program, workspace: workspace, prompt: argument, endpoint: endpoint, auto: auto, home: home)
            return "Started task \(id) in the background" + (auto ? ", in Auto." : "; it will wait for /task allow before each edit and command.") + " /task show \(id) follows it."
        case "show":
            let state = try status(argument, home: home)
            guard let task = read(BackgroundTask.self, folder(argument, home).appendingPathComponent("request.json")) else { throw MasonError("No task \(argument).") }
            var lines = [summary(task, state)]
            if let approval = state.approval, !approval.detail.isEmpty { lines.append(approval.detail) }
            for line in try tail(argument, lines: 12, home: home) {
                guard let object = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any], let type = object["type"] as? String, let data = object["data"] as? [String: Any] else { continue }
                switch type {
                case "tool.result": lines.append("  \(data["tool"] as? String ?? "") \(data["subject"] as? String ?? "")  →  \(data["state"] as? String ?? "")")
                case "task.finished": lines.append("  " + ((data["answer"] as? String).flatMap { $0.isEmpty ? nil : String($0.prefix(1200)) } ?? "finished: \(data["state"] as? String ?? "")"))
                default: break
                }
            }
            if let session = state.session, !state.active { lines.append("Its session is \(session); /resume shows it in that project.") }
            return lines.joined(separator: "\n")
        case "allow", "deny":
            guard let approval = try status(argument, home: home).approval else { throw MasonError("Task \(argument) is not waiting for an approval.") }
            try decide(argument, request: approval.id, allow: verb == "allow", home: home)
            return (verb == "allow" ? "Allowed: " : "Denied: ") + approval.title
        case "cancel":
            try cancel(argument, home: home)
            return "Asked task \(argument) to stop."
        default:
            return usage
        }
    }
}
