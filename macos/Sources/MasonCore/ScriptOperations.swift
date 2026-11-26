import Foundation

extension RepositoryMap {
    public struct Match: Equatable {
        public let path: String
        public let line: Int
        public let kind: String
        public let name: String
        public let text: String
    }

    static let importLine = try! NSRegularExpression(pattern: #"^\s*(?:import|from|using|use|#\s*include|export\b.*\bfrom)\b|\brequire\s*\("#)

    /// Where a name is defined, imported, used and tested, in the order a reader wants them: definitions, then
    /// imports, callers and tests. `query` is an identifier or a qualified name such as `Cart.total`; the part before
    /// the last one only ranks matches, by whether the file or the line mentions it. These are whole-word text matches.
    public static func query(_ workspace: Workspace, _ query: String, under directory: String = ".", limit: Int = 32) throws -> (matches: [Match], files: Int, partial: Bool) {
        let parts = query.components(separatedBy: CharacterSet(charactersIn: ".:#/ ()")).filter { !$0.isEmpty }
        guard let name = parts.last, query.count <= 200 else { throw MasonError("Give an identifier or a qualified name such as Cart.total.") }
        let qualifier = parts.count > 1 ? parts[parts.count - 2].lowercased() : nil
        let definitions = try CodeNavigation.query(workspace, directory: directory, symbol: name, mode: "definitions")
        let references = try CodeNavigation.query(workspace, directory: directory, symbol: name, mode: "references")
        var groups: [[Match]] = [[], [], [], []]
        for hit in definitions.hits { groups[0].append(Match(path: hit.path, line: hit.line, kind: hit.kind, name: name, text: hit.text)) }
        for hit in references.hits {
            let imported = importLine.firstMatch(in: hit.text, range: NSRange(hit.text.startIndex..., in: hit.text)) != nil
            let group = imported ? 1 : hit.kind.hasPrefix("test") ? 3 : 2
            groups[group].append(Match(path: hit.path, line: hit.line, kind: imported ? "import candidate" : hit.kind, name: name, text: hit.text))
        }
        if let qualifier {
            // A stable partition: matches that mention the qualifier come first, in their original order.
            groups = groups.map { group in
                let near = group.filter { $0.path.lowercased().contains(qualifier) || $0.text.lowercased().contains(qualifier) }
                return near + group.filter { !near.contains($0) }
            }
        }
        // Every kind gets a share before any kind takes what is left.
        let shares = [12, 6, 10, 6]
        var chosen: [Match] = [], left: [Match] = []
        for (group, share) in zip(groups, shares) { chosen += group.prefix(share); left += group.dropFirst(share) }
        let all = groups.reduce(0) { $0 + $1.count }
        if chosen.count < limit { chosen += left.prefix(limit - chosen.count) }
        chosen = Array(chosen.prefix(limit))
        let order = ["definition candidate": 0, "import candidate": 1, "reference candidate": 2, "test reference candidate": 3]
        let ranked = chosen.enumerated().sorted { (order[$0.element.kind] ?? 4, $0.offset) < (order[$1.element.kind] ?? 4, $1.offset) }.map(\.element)
        return (ranked, max(definitions.files, references.files), definitions.partial || references.partial || all > ranked.count)
    }

    /// The result as the model reads it.
    public static func report(_ result: (matches: [Match], files: Int, partial: Bool), query: String) -> String {
        var lines = ["\(result.matches.count) matches for \(query) in \(result.files) source files\(result.partial ? " (not everything; narrow the folder or the name to see the rest)" : ""): definitions, then imports, callers and tests. These are whole-word text matches, not a compiler's answer; read the lines before relying on them."]
        lines += result.matches.map { "\($0.path):\($0.line)  [\($0.kind)]  \($0.text)" }
        if result.matches.isEmpty { lines.append("Nothing matched. Check the spelling, or search with search_files.") }
        return lines.joined(separator: "\n")
    }
}

/// Single operations for scripts: `mason exec`, `mason map`, `mason symbol`, `mason git-status`. Each prints JSON Lines,
/// one `{"type": ..., "data": ...}` object per line, and returns the exit code: 0 completed, 2 ran and did not
/// complete, 1 error.
public enum ScriptOperations {
    public static let names: Set<String> = ["exec", "map", "symbol", "git-status", "worktree", "task-start", "task-list", "task-show", "task-allow", "task-deny", "task-cancel", "task-run"]

    /// The path of the running mason, which is what a background task's worker is started from.
    public static var ownProgram: String { (Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments.first ?? "mason")).resolvingSymlinksInPath().path }

    public static let usage = """
        mason exec --workspace PATH --endpoint URL --model ID --prompt TEXT [--auto] [--profile PATH] [--resume SESSION_ID]
                   [--visual-model ID [--visual-endpoint URL]] [--trace-dir FOLDER]
        mason map --workspace PATH --query NAME
        mason symbol --workspace PATH --path FILE --line N --column N --mode definitions|references|diagnostics
        mason git-status --workspace PATH
        mason worktree --workspace PATH --destination NEW_PATH --branch NEW_BRANCH
        mason task-start --workspace PATH --endpoint URL --model ID --prompt TEXT [--auto]
        mason task-list [--workspace PATH]
        mason task-show --id TASK_ID
        mason task-allow|task-deny --id TASK_ID --request APPROVAL_ID
        mason task-cancel --id TASK_ID
          For scripts: one operation, printed as JSON Lines, then exit. 0 means it completed, 2 that it ran and did not, 1 an error.
          exec only reads unless you pass --auto, which lets it edit files and run commands in the sandbox. Nothing is ever asked:
          an approval it would need is reported and declined. --prompt-file PATH reads the request from a file.
          Each exec keeps its own journal, or the one in --profile.
          A task runs in the background and keeps going after this command returns. Without --auto it waits for
          task-allow before each edit and command; task-show prints the approval it is waiting on.
        """

    /// `model` replaces the endpoint in tests. `write` receives each line.
    public static func run(_ arguments: [String], model: ChatModel? = nil, write: @escaping (String) -> Void) async -> Int32 {
        func event(_ type: String, _ data: [String: Any]) {
            guard let encoded = try? JSONSerialization.data(withJSONObject: ["type": type, "data": data], options: [.sortedKeys, .withoutEscapingSlashes]) else { return }
            write(String(decoding: encoded, as: UTF8.self))
        }
        do {
            guard let operation = arguments.first, names.contains(operation) else { throw MasonError("Unknown operation.") }
            var values: [String: String] = [:], auto = false, index = 1
            while index < arguments.count {
                let name = arguments[index]; index += 1
                if name == "--auto" { auto = true; continue }
                guard name.hasPrefix("--"), index < arguments.count, values[String(name.dropFirst(2))] == nil else { throw MasonError("Expected unique --option value pairs.") }
                values[String(name.dropFirst(2))] = arguments[index]; index += 1
            }
            func get(_ name: String) throws -> String {
                guard let value = values[name] else { throw MasonError("--\(name) is required.") }
                return value
            }
            if operation.hasPrefix("task-") {
                let home = values["task-home"].map { URL(fileURLWithPath: $0) } ?? DataHome.url
                switch operation {
                case "task-run":
                    BackgroundTasks.detach()
                    return await BackgroundTasks.work(try get("id"), home: home, model: model)
                case "task-start":
                    let prompt = try values["prompt-file"].map { try String(contentsOfFile: $0, encoding: .utf8) } ?? get("prompt")
                    let endpoint = ModelEndpoint(url: try ModelEndpoint.validated(try get("endpoint")).absoluteString, model: try get("model"))
                    event("task.started", ["id": try BackgroundTasks.start(program: ownProgram, workspace: try Workspace(path: try get("workspace")), prompt: prompt, endpoint: endpoint, auto: auto, home: home)])
                case "task-list":
                    let found = BackgroundTasks.all(workspace: try values["workspace"].map { try Workspace(path: $0) }, home: home)
                    event("tasks", ["tasks": found.map { ["id": $0.task.id, "workspace": $0.task.workspace, "prompt": $0.task.prompt, "state": $0.status.state, "detail": $0.status.detail] }])
                case "task-show":
                    let id = try get("id"), state = try BackgroundTasks.status(id, home: home)
                    var data: [String: Any] = ["id": id, "state": state.state, "detail": state.detail, "session": state.session ?? "",
                                               "events": try BackgroundTasks.tail(id, home: home).compactMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) }]
                    if let approval = state.approval { data["approval"] = ["id": approval.id, "tool": approval.tool, "title": approval.title, "detail": approval.detail, "subject": approval.subject] }
                    event("task", data)
                case "task-allow", "task-deny":
                    try BackgroundTasks.decide(try get("id"), request: try get("request"), allow: operation == "task-allow", home: home)
                    event("task.decision", ["id": try get("id"), "request": try get("request"), "allowed": operation == "task-allow"])
                default:
                    try BackgroundTasks.cancel(try get("id"), home: home)
                    event("task.cancel", ["id": try get("id")])
                }
                return 0
            }
            let workspace = try Workspace(path: try get("workspace"))
            switch operation {
            case "map":
                let query = try get("query")
                let result = try RepositoryMap.query(workspace, query)
                event("map.result", ["query": query, "files": result.files, "partial": result.partial,
                                     "matches": result.matches.map { ["path": $0.path, "line": $0.line, "kind": $0.kind, "name": $0.name, "text": $0.text] as [String: Any] }])
                return 0

            case "worktree":
                event("worktree.created", ["path": try await Experiments.createWorktree(workspace, destination: try get("destination"), branch: try get("branch"))])
                return 0

            case "git-status":
                guard FileManager.default.fileExists(atPath: workspace.root.appendingPathComponent(".git").path) else {
                    event("git.status", ["repository": false, "output": ""])
                    return 0
                }
                // Nothing in the repository's own settings is allowed to start a program for this.
                let result = try await Shell.run("git -c core.fsmonitor= -c core.hooksPath=/dev/null --no-optional-locks status --porcelain=v1 --branch", in: workspace, sandboxed: Shell.sandboxAvailable, timeout: 20)
                event("git.status", ["repository": true, "exit": Int(result.exitCode), "output": result.output])
                return result.exitCode == 0 ? 0 : 2

            case "symbol":
                let path = try get("path"), mode = try get("mode")
                guard let line = Int(try get("line")), let column = Int(try get("column")), line >= 1, column >= 1 else { throw MasonError("--line and --column are numbers from 1.") }
                let kind = (path as NSString).pathExtension.lowercased()
                let report: String
                if SymbolService.python.contains(kind) || SymbolService.script.contains(kind) {
                    report = try await SymbolService.query(workspace, path: path, line: line, column: column, mode: mode)
                } else {
                    guard let server = LanguageServers.server(for: kind) else { throw MasonError("No language server for .\(kind) files was found on this computer.") }
                    // A language server may run the project's build, and a script has nobody to ask, so it only runs sandboxed.
                    guard Shell.sandboxAvailable else { throw MasonError("\(server.name) is only started inside the sandbox here, and the sandbox is unavailable.") }
                    report = try await LanguageServers.query(workspace, server: server, path: path, line: line, column: column, mode: mode, sandboxed: true)
                }
                event("symbol.result", ["path": path, "line": line, "column": column, "mode": mode, "report": report])
                return 0

            default:
                let prompt = try values["prompt-file"].map { try String(contentsOfFile: $0, encoding: .utf8) } ?? get("prompt")
                let profile = values["profile"].map { URL(fileURLWithPath: $0).standardizedFileURL } ?? DataHome.url.appendingPathComponent("cli-runs").appendingPathComponent(UUID().uuidString)
                // Compared as real paths, so a link (such as /var on macOS) cannot hide a journal inside the project.
                // The journal may not exist yet: resolve the part of its path that does.
                var existing = profile, rest: [String] = []
                while !FileManager.default.fileExists(atPath: existing.path), existing.path != "/" { rest.insert(existing.lastPathComponent, at: 0); existing.deleteLastPathComponent() }
                let real = ([realPath(existing.path)] + rest).joined(separator: "/")
                guard !(real + "/").hasPrefix(realPath(workspace.root.path) + "/") else { throw MasonError("Keep the journal outside the project.") }
                let chosen: ChatModel
                if let model { chosen = model } else {
                    chosen = try OpenAIClient(ModelEndpoint(url: try ModelEndpoint.validated(try get("endpoint")).absoluteString, model: try get("model")))
                }
                let store = try SessionStore(home: profile)
                var session: Session
                if let id = values["resume"] {
                    guard let found = store.sessions(in: workspace.root.path).first(where: { $0.id.uuidString.lowercased() == id.lowercased() }) else { throw MasonError("No session \(id) for this project in that journal.") }
                    session = found
                    session.permission = auto ? .auto : .readOnly
                } else {
                    session = try store.create(workspace: workspace.root.path, permission: auto ? .auto : .readOnly)
                }
                session.askOnly = !auto
                event("run.started", ["id": session.id.uuidString, "workspace": workspace.root.path, "profile": profile.path, "model": values["model"] ?? "", "mode": auto ? "build" : "ask"])
                var working = Roles.implementer(chosen, workspace: workspace)
                if let folder = values["trace-dir"] {
                    let place = URL(fileURLWithPath: folder).standardizedFileURL
                    try FileManager.default.createDirectory(at: place, withIntermediateDirectories: true)
                    guard !(realPath(place.path) + "/").hasPrefix(realPath(workspace.root.path) + "/") else { throw MasonError("Keep decision traces outside the project.") }
                    working = RoleModel(chosen, role: "implementer", trace: place)
                }
                let agent = Agent(store: store, model: working, approve: { request in
                    event("approval.required", ["tool": request.tool, "title": request.title, "subject": request.subject])
                    return false
                }, emit: { change in
                    if case .activityFinished(let activity) = change {
                        event("tool.result", ["tool": activity.tool, "subject": activity.subject, "state": "\(activity.state)", "result": activity.result])
                    }
                })
                if let visual = values["visual-model"] {
                    agent.vision = ModelVision(ModelEndpoint(url: try ModelEndpoint.validated(try values["visual-endpoint"] ?? get("endpoint")).absoluteString, model: visual))
                }
                await agent.run(&session, request: prompt)
                event("run.finished", ["id": session.id.uuidString, "state": session.state, "detail": session.detail, "answer": session.turns.last?.answer ?? ""])
                return session.state == "Completed" ? 0 : 2
            }
        } catch {
            event("error", ["message": (error as? MasonError)?.description ?? error.localizedDescription])
            return 1
        }
    }
}
