import Foundation
import MasonCore
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

// The interactive mason session for macOS. It needs no options: it finds a running model, restarts the one
// used last, offers the coding models on this computer, or suggests a download that fits.

final class Box<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: T
    init(_ value: T) { stored = value }
    var value: T {
        get { lock.lock(); defer { lock.unlock() }; return stored }
        set { lock.lock(); stored = newValue; lock.unlock() }
    }
}

/// What the engine reports while a request runs, handed from its task to the terminal loop.
final class RunFeed: @unchecked Sendable {
    private let lock = NSLock()
    private var finished: [Activity] = []
    private var asking: (ApprovalRequest, CheckedContinuation<Bool, Never>)?
    private var live = "", running = "", note = ""
    var done = false

    func push(_ event: AgentEvent) {
        lock.lock(); defer { lock.unlock() }
        switch event {
        case .text(let text): live += text
        case .activityStarted(let activity): running = (activity.tool.replacingOccurrences(of: "_", with: " ") + " " + activity.subject); live = ""
        case .activityFinished(let activity): finished.append(activity); running = ""
        case .status(let text): note = text
        }
    }
    func ask(_ request: ApprovalRequest, _ continuation: CheckedContinuation<Bool, Never>) { lock.lock(); asking = (request, continuation); lock.unlock() }
    func takeApproval() -> (ApprovalRequest, CheckedContinuation<Bool, Never>)? { lock.lock(); defer { asking = nil; lock.unlock() }; return asking }
    func takeFinished() -> [Activity] { lock.lock(); defer { finished = []; lock.unlock() }; return finished }
    var doing: String {
        lock.lock(); defer { lock.unlock() }
        if !live.isEmpty { return live.replacingOccurrences(of: "\n", with: " ") }
        return !running.isEmpty ? running : !note.isEmpty ? note : "Thinking"
    }
}

final class CLI {
    let store: SessionStore
    let workspace: Workspace
    var session: Session
    var endpoint: ModelEndpoint?
    var home: URL { store.folder.deletingLastPathComponent() }
    var endpointName = ""
    var server: ModelServer?
    var listed: [LocalModel] = []
    var found: [HubModel] = []
    let current = Box<Task<Void, Never>?>(nil)
    let lastInterrupt = Box<Date>(.distantPast)

    init(store: SessionStore, workspace: Workspace, session: Session) { self.store = store; self.workspace = workspace; self.session = session }

    static let usage = """
        mason [--workspace PATH] [--resume] [--endpoint URL --model ID] [--read-only | --review | --edits | --auto]
          Opens an interactive session in the current folder. No options are needed: mason uses a model that is
          already running, restarts the one you used last, or helps you choose or download one.
          New sessions ask before each edit and command (--review).
        """

    static func main() async -> Int32 {
        var values: [String: String] = [:]; var flags = Set<String>()
        var arguments = Array(CommandLine.arguments.dropFirst())
        if arguments.first == "chat" { arguments.removeFirst() }
        if arguments.contains("--help") || arguments.contains("-h") { print(usage); return 0 }
        var index = 0
        while index < arguments.count {
            let name = arguments[index]; index += 1
            switch name {
            case "--auto", "--edits", "--review", "--read-only", "--resume": flags.insert(String(name.dropFirst(2)))
            case "--workspace", "--endpoint", "--model":
                guard index < arguments.count else { FileHandle.standardError.write(Data("\(name) needs a value.\n".utf8)); return 1 }
                values[String(name.dropFirst(2))] = arguments[index]; index += 1
            default: FileHandle.standardError.write(Data("Unknown option \(name). Run mason --help.\n".utf8)); return 1
            }
        }
        do {
            guard (values["endpoint"] == nil) == (values["model"] == nil) else { throw MasonError("Use --endpoint and --model together.") }
            let workspace = try Workspace(path: values["workspace"] ?? FileManager.default.currentDirectoryPath)
            let store = try SessionStore()
            let mine = store.sessions(in: workspace.root.path)
            var session: Session
            if flags.contains("resume") {
                guard let latest = mine.first(where: { !$0.turns.isEmpty }) else { throw MasonError("No saved session to resume in this folder.") }
                session = latest
            } else {
                session = try mine.first(where: { $0.turns.isEmpty }) ?? store.create(workspace: workspace.root.path)
            }
            let cli = CLI(store: store, workspace: workspace, session: session)
            if let chosen = ["auto": Permission.auto, "edits": .workspaceEdits, "review": .reviewEdits, "read-only": .readOnly].first(where: { flags.contains($0.key) })?.value { cli.setPermission(chosen) }
            if let url = values["endpoint"], let model = values["model"] {
                _ = try ModelEndpoint.validated(url)
                // Remembered, so the next plain "mason" and the app use the same server while it is running.
                cli.use(ModelEndpoint(url: try ModelEndpoint.validated(url).absoluteString, model: model), name: model)
            }
            await cli.loop(resumed: flags.contains("resume"))
            cli.server?.stop()
            return 0
        } catch {
            FileHandle.standardError.write(Data(((error as? MasonError)?.description ?? error.localizedDescription).appending("\n").utf8))
            return 1
        }
    }

    // MARK: session loop

    var modeName: String { session.askOnly ? "ask · read-only" : "build · " + session.permission.label }
    func setPermission(_ permission: Permission) { session.permission = permission; session.askOnly = permission == .readOnly; try? store.save(session) }

    func loop(resumed: Bool) async {
        watchInterrupts()
        if endpoint == nil { await findRunningModel() }
        Term.line()
        Term.line("  " + Term.paint(Term.bold + Term.accent, "Mason Agent") + Term.paint(Term.dim, "  ·  " + workspace.root.path))
        Term.line("  " + Term.paint(Term.dim, "model ") + (endpoint == nil ? Term.paint(Term.yellow, "none running") : endpointName) + Term.paint(Term.dim, "  ·  \(modeName)  ·  /help for commands"))
        if resumed { Term.line("  " + Term.paint(Term.dim, "resumed ") + session.title + Term.paint(Term.dim, "  ·  \(session.turns.count) requests  ·  \(session.state)")) }
        if endpoint == nil { await firstRun() }
        while true {
            guard let input = readRequest() else { Term.line(); return }
            if input.isEmpty { continue }
            do {
                if input.hasPrefix("/") { if try await command(input) == false { return } }
                else { await send(input) }
            } catch is CancellationError { Term.line(Term.paint(Term.yellow, "  Cancelled.")) }
            catch { Term.line(Term.paint(Term.red, "✗ " + ((error as? MasonError)?.description ?? error.localizedDescription))) }
        }
    }

    /// Ctrl+C stops a run; at the prompt, a second Ctrl+C within five seconds leaves.
    func watchInterrupts() {
        signal(SIGINT, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: SIGINT, queue: .global())
        source.setEventHandler { [self] in
            if let running = current.value { running.cancel(); return }
            if Date().timeIntervalSince(lastInterrupt.value) < 5 { Term.raw(false); server?.stop(); print(); exit(0) }
            lastInterrupt.value = Date()
            Term.line(); Term.line(Term.paint(Term.dim, "  Press Ctrl+C again or type /exit to leave."))
            Term.write(prompt)
        }
        source.resume()
        interruptSource = source
        // Closing the terminal window or a kill request still restores the terminal and stops the model this session started.
        for number in [SIGHUP, SIGTERM] {
            signal(number, SIG_IGN)
            let ending = DispatchSource.makeSignalSource(signal: number, queue: .global())
            ending.setEventHandler { [self] in Term.raw(false); server?.stop(); exit(0) }
            ending.resume()
            endingSources.append(ending)
        }
    }
    var interruptSource: DispatchSourceSignal?
    var endingSources: [DispatchSourceSignal] = []
    var prompt: String { Term.paint(Term.dim, (modeName.components(separatedBy: " · ").last ?? "") + " ") + Term.paint(Term.accent + Term.bold, "› ") }

    /// One request. A trailing backslash continues on the next line.
    func readRequest() -> String? {
        var text = ""
        while true {
            Term.line()
            Term.write(text.isEmpty ? prompt : Term.paint(Term.dim, "  … "))
            guard let line = readLine() else { return text.isEmpty ? nil : text }
            if line.hasSuffix("\\") { text += line.dropLast() + "\n"; continue }
            return (text + line).trimmingCharacters(in: .whitespacesAndNewlines)
        }
    }

    // MARK: running a request

    func send(_ input: String) async {
        guard let endpoint, let model = try? OpenAIClient(endpoint) else {
            Term.line(Term.paint(Term.yellow, "  No model is running.") + Term.paint(Term.dim, " /models lists the ones on this computer; /models setup finds one to download."))
            return
        }
        guard await OpenAIClient.models(at: endpoint.url) != nil else {
            Term.line(Term.paint(Term.yellow, "  \(endpointName) is not answering at \(endpoint.url). Start it again or choose another with /model."))
            return
        }
        let feed = RunFeed(), box = Box(session), started = Date()
        let firstActivity = session.activities.count, turnsBefore = session.turns.count
        let agent = Agent(store: store, model: model, approve: { request in await withCheckedContinuation { feed.ask(request, $0) } }, emit: feed.push)
        let task = Task { var working = box.value; await agent.run(&working, request: input); box.value = working; feed.done = true }
        current.value = task
        Term.line()
        Term.raw(true)
        var frame = 0, stopping = false
        while !feed.done {
            for activity in feed.takeFinished() { show(activity) }
            if let (request, answer) = feed.takeApproval() { Term.setStatus(""); answer.resume(returning: approve(request)); continue }
            if let key = Term.key(), key == 3 || (key == 27 && Term.wasEscapeAlone()) { task.cancel() }
            if task.isCancelled, !stopping { stopping = true; Term.line(Term.paint(Term.dim, "  Stopping…")) }
            frame += 1
            let spinner = Array("⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏")[frame % 10]
            let room = max(20, Term.width - 36), doing = feed.doing
            Term.setStatus("  \(spinner) \(doing.count > room ? "…" + doing.suffix(room - 1) : doing)  ·  \(Int(Date().timeIntervalSince(started)))s  ·  Esc to stop")
        }
        await task.value
        current.value = nil
        Term.setStatus(""); Term.raw(false)
        for activity in feed.takeFinished() { show(activity) }
        session = box.value
        outcome(firstActivity: firstActivity, turnsBefore: turnsBefore, seconds: Date().timeIntervalSince(started))
    }

    func show(_ activity: Activity) {
        let mark: String
        switch activity.state {
        case .completed: mark = Term.paint(Term.green, "✓")
        case .failed: mark = Term.paint(Term.red, "✗")
        case .denied: mark = Term.paint(Term.yellow, "⊘")
        case .running: mark = Term.paint(Term.dim, "·")
        }
        let label = activity.tool.replacingOccurrences(of: "_", with: " ")
        Term.line("  \(mark) \(Term.paint(Term.bold, label)) \(Term.clip(activity.subject.replacingOccurrences(of: "\n", with: " "), max(10, Term.width - label.count - 8)))" + (activity.state == .denied ? Term.paint(Term.dim, "  declined") : ""))
        guard activity.tool == "run_shell" || activity.state == .failed else { return }
        let lines = activity.result.split(separator: "\n").map(String.init).filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        let limit = activity.state == .failed ? 10 : 6
        if lines.count > limit { Term.line(Term.paint(Term.dim, "    │ … \(lines.count - limit) earlier lines")) }
        for line in lines.suffix(limit) { Term.line(Term.paint(Term.dim, "    │ " + Term.clip(line, Term.width - 8))) }
    }

    func outcome(firstActivity: Int, turnsBefore: Int, seconds: TimeInterval) {
        let turn = session.turns.count > turnsBefore ? session.turns.last : nil
        if let answer = turn?.answer, !answer.isEmpty {
            Term.line()
            for line in Term.markdown(answer) { Term.line("  " + line) }
        }
        if let changes = turn?.changes, !changes.isEmpty {
            Term.line()
            for change in changes.prefix(30) {
                let mark = Term.paint(Term.accent, change.deleted ? "D" : change.created ? "A" : "M")
                let source = change.byCommand == true ? Term.paint(Term.dim, "  by a command") : ""
                if change.isOpaque { Term.line("  \(mark) \(change.path)  \(Term.paint(Term.dim, "binary or large file"))\(source)"); continue }
                let counts = Diff.counts(before: change.before, after: change.deleted ? "" : change.after)
                Term.line("  \(mark) \(change.path)  \(Term.paint(Term.green, "+\(counts.added)")) \(Term.paint(Term.red, "−\(counts.removed)"))\(source)")
            }
            Term.line(Term.paint(Term.dim, "  /diff shows the changes · /undo puts the files back"))
        }
        Term.line()
        let state: String
        switch session.state {
        case "Completed": state = Term.paint(Term.green, "✓ Completed")
        case "Stopped": state = Term.paint(Term.yellow, "■ Stopped")
        case "Failed": state = Term.paint(Term.red, "✗ Failed")
        default: state = Term.paint(Term.yellow, "● " + session.state)
        }
        let steps = session.activities.count - firstActivity
        var facts = ["\(steps) tool call\(steps == 1 ? "" : "s")", seconds < 90 ? "\(Int(seconds))s" : String(format: "%.1f min", seconds / 60)]
        if let tokens = session.promptTokens { facts.append("\(tokens) tokens of context") }
        Term.line("  " + state + Term.paint(Term.dim, "  ·  " + facts.joined(separator: "  ·  ")))
        if !session.detail.isEmpty { Term.line("  " + Term.paint(Term.dim, session.detail)) }
    }

    // MARK: approvals

    /// Called in raw mode. y allows once, a saves a rule for this project, n or Esc declines.
    func approve(_ request: ApprovalRequest) -> Bool {
        let tool = request.tool.replacingOccurrences(of: "_", with: " "), bar = Term.paint(Term.yellow, "  ▌ ")
        Term.line()
        Term.line(bar + Term.paint(Term.bold, "Review " + tool))
        for line in request.title.split(separator: "\n").prefix(6) { Term.line(bar + Term.clip(String(line), Term.width - 6)) }
        let detail = request.detail.split(separator: "\n", omittingEmptySubsequences: false)
        for line in detail.prefix(40) {
            Term.line(bar + Term.paint(line.hasPrefix("+") ? Term.green : line.hasPrefix("-") ? Term.red : Term.dim, Term.clip(String(line), Term.width - 6)))
        }
        if detail.count > 40 { Term.line(bar + Term.paint(Term.dim, "… \(detail.count - 40) more lines")) }
        guard Term.interactive else { Term.line(bar + "Declined: there is no terminal to ask."); return false }
        while true {
            Term.write("  \(Term.paint(Term.bold, "Allow?"))  \(Term.paint(Term.accent, "y")) once   \(Term.paint(Term.accent, "a")) always for this project…   \(Term.paint(Term.accent, "n")) decline  › ")
            Term.drainKeys()
            var choice: Character = " "
            while choice == " " {
                if current.value?.isCancelled == true { Term.line("cancelled"); return false }
                guard let key = Term.key() else { continue }
                if key == 3 || key == 27 { choice = "n" } else if let scalar = Unicode.Scalar(UInt32(key)), "yan".contains(Character(scalar).lowercased()) { choice = Character(Character(scalar).lowercased()) }
            }
            if choice == "n" { Term.line(Term.paint(Term.yellow, "declined")); return false }
            if choice == "y" { Term.line(Term.paint(Term.green, "allowed once")); return true }
            Term.line("always")
            let suggested = ApprovalRules.suggestion(tool: request.tool, subject: request.subject)
            Term.raw(false)
            Term.write("  Allow \(tool) when it starts with [\(Term.paint(Term.cyan, suggested))] (Enter keeps it, a single - cancels): ")
            let typed = readLine()?.trimmingCharacters(in: .whitespaces)
            Term.raw(true)
            guard let typed, typed != "-" else { continue }
            var rules = ApprovalRules(workspace: session.workspace)
            do {
                try rules.add(tool: request.tool, prefix: typed.isEmpty ? suggested : typed)
                Term.line(Term.paint(Term.green, "  Saved: always allow \(tool) · \(typed.isEmpty ? suggested : typed)") + Term.paint(Term.dim, "  ·  /rules lists and revokes"))
                return true
            } catch { Term.line(Term.paint(Term.red, "  " + ((error as? MasonError)?.description ?? error.localizedDescription))) }
        }
    }

    func confirm(_ question: String) -> Bool {
        guard Term.interactive else { Term.line("  \(question) Not confirmed: there is no terminal to ask."); return false }
        Term.write("  \(question)  \(Term.paint(Term.accent, "y")) yes   \(Term.paint(Term.accent, "n")) no  › ")
        let answer = readLine()?.trimmingCharacters(in: .whitespaces).lowercased() ?? ""
        return answer == "y" || answer == "yes"
    }

    /// Runs async work behind a status line. Esc or Ctrl+C cancels it.
    func busy<T>(_ label: String, _ work: @escaping (@escaping (String) -> Void) async throws -> T) async throws -> T {
        let detail = Box(label), result = Box<Result<T, Error>?>(nil), started = Date()
        let task = Task { do { result.value = .success(try await work { detail.value = $0.isEmpty ? label : $0 }) } catch { result.value = .failure(error) } }
        current.value = task
        Term.raw(true)
        var frame = 0
        while result.value == nil {
            if let key = Term.key(), key == 3 || (key == 27 && Term.wasEscapeAlone()) { task.cancel() }
            frame += 1
            Term.setStatus("  \(Array("⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏")[frame % 10]) \(Term.clip(detail.value, max(20, Term.width - 34)))  ·  \(Int(Date().timeIntervalSince(started)))s  ·  Esc to cancel")
        }
        current.value = nil
        Term.setStatus(""); Term.raw(false)
        return try result.value!.get()
    }

    // MARK: commands

    /// Returns false to leave.
    func command(_ input: String) async throws -> Bool {
        let parts = input.split(separator: " ", maxSplits: 1).map(String.init)
        var name = parts[0].lowercased(), rest = parts.count > 1 ? parts[1].trimmingCharacters(in: .whitespaces) : ""
        if ["/read", "/review", "/edits", "/auto"].contains(name) { rest = String(name.dropFirst()); name = "/permission" }
        switch name {
        case "/exit", "/quit": return false
        case "/help", "/?": help()
        case "/new":
            if !session.turns.isEmpty {
                let previous = session
                session = try store.create(workspace: workspace.root.path, permission: previous.permission)
                session.askOnly = previous.askOnly; try store.save(session)
            }
            Term.line(Term.paint(Term.dim, "  New session · \(modeName)"))
            if !rest.isEmpty { await send(rest) }
        case "/resume", "/sessions":
            let mine = store.sessions(in: workspace.root.path).filter { !$0.turns.isEmpty || $0.id == session.id }.prefix(20).map { $0 }
            if let number = Int(rest), number >= 1, number <= mine.count {
                session = mine[number - 1]
                Term.line("  Resumed " + Term.paint(Term.bold, session.title) + Term.paint(Term.dim, "  ·  \(modeName)"))
            } else {
                Term.line()
                for (index, item) in mine.enumerated() {
                    Term.line("  \(Term.paint(Term.dim, String(index + 1).leftPad(2)))  \(item.id == session.id ? Term.paint(Term.bold, Term.clip(item.title, 60)) : Term.clip(item.title, 60))" + Term.paint(Term.dim, "  ·  \(item.state)" + (item.id == session.id ? "  ← current" : "")))
                }
                Term.line(Term.paint(Term.dim, "  Continue one with /resume NUMBER."))
            }
        case "/ask": session.askOnly = true; try store.save(session); Term.line(Term.paint(Term.dim, "  Ask mode: Mason reads and answers; it does not edit or run commands."))
        case "/build":
            session.askOnly = false
            if session.permission == .readOnly { session.permission = .reviewEdits }
            try store.save(session); Term.line(Term.paint(Term.dim, "  " + modeName))
        case "/permission", "/permissions":
            let choices: [String: Permission] = ["read": .readOnly, "read-only": .readOnly, "review": .reviewEdits, "edits": .workspaceEdits, "auto": .auto]
            guard !rest.isEmpty else { Term.line("  " + modeName + Term.paint(Term.dim, "  ·  /permission read | review | edits | auto")); break }
            guard let permission = choices[rest.lowercased()] else { throw MasonError("Use /permission read, review, edits or auto.") }
            setPermission(permission)
            switch permission {
            case .readOnly: Term.line(Term.paint(Term.dim, "  Read-only: Mason reads and answers."))
            case .reviewEdits: Term.line(Term.paint(Term.dim, "  Review edits: you approve each edit and command."))
            case .workspaceEdits: Term.line(Term.paint(Term.dim, "  Workspace edits: edits apply without asking; commands still ask."))
            case .auto:
                Term.line(Term.paint(Term.dim, "  Auto: commands run in the macOS sandbox, limited to this project, with network access off."))
                if !Shell.sandboxAvailable { Term.line(Term.paint(Term.yellow, "  The macOS sandbox is not available here, so commands will be refused in Auto.")) }
            }
        case "/status": status()
        case "/diff", "/changes": diff(rest)
        case "/undo", "/redo":
            guard let result = try Agent.undoLatest(&session, store: store, redo: name == "/redo") else { Term.line(Term.paint(Term.dim, name == "/undo" ? "  Nothing to undo in this session." : "  Nothing to redo.")); break }
            Term.line(Term.paint(Term.green, "  ✓ \(result.restored.count) file\(result.restored.count == 1 ? "" : "s") \(name == "/undo" ? "put back" : "reapplied")") + (result.skipped.isEmpty ? "" : Term.paint(Term.yellow, "  ·  changed since, left alone: " + result.skipped.joined(separator: ", "))))
        case "/rules":
            var rules = ApprovalRules(workspace: session.workspace)
            if rest.lowercased().hasPrefix("revoke"), let number = Int(rest.dropFirst(6).trimmingCharacters(in: .whitespaces)), number >= 1, number <= rules.rules.count {
                let removed = rules.rules[number - 1]; try rules.remove(at: number - 1)
                Term.line(Term.paint(Term.dim, "  Revoked: \(removed.tool.replacingOccurrences(of: "_", with: " ")) · \(removed.prefix)"))
            } else if rules.rules.isEmpty { Term.line(Term.paint(Term.dim, "  No saved rules. Choose \"a\" on an approval to save one for this project.")) }
            else {
                for (index, rule) in rules.rules.enumerated() { Term.line("  \(Term.paint(Term.dim, String(index + 1).leftPad(2)))  \(Term.paint(Term.green, "allow"))  \(rule.tool.replacingOccurrences(of: "_", with: " "))  \(Term.paint(Term.cyan, rule.prefix))") }
                Term.line(Term.paint(Term.dim, "  Remove one with /rules revoke NUMBER. Rules never widen the sandbox."))
            }
        case "/compact":
            guard session.messages.count > 2 else { Term.line(Term.paint(Term.dim, "  There is nothing to compact yet.")); break }
            guard let endpoint, let model = try? OpenAIClient(endpoint) else { throw MasonError("Compaction needs a running model. /models lists the ones on this computer.") }
            Term.line(Term.paint(Term.dim, "  Compacting the conversation… the original messages are kept."))
            let before = session.messages.count
            var working = session
            guard try await Compaction.compact(&working, model: model, keep: 0) else { Term.line(Term.paint(Term.dim, "  There is nothing to compact yet.")); break }
            session = working; try store.save(session)
            Term.line(Term.paint(Term.green, "  ✓ \(before - 1) messages replaced by a summary") + Term.paint(Term.dim, "  ·  it travels with your next request"))
        case "/agent", "/agents":
            let agents = Customization.agents(home: home, workspace: session.workspace)
            if rest.isEmpty {
                if agents.isEmpty { Term.line(Term.paint(Term.dim, "  No custom agents. Add a Markdown file to .mason/agents (or .claude/agents) in this project.")); break }
                Term.line()
                for agent in agents {
                    let current = agent.name.lowercased() == session.agent?.lowercased()
                    Term.line("  \(Term.paint(current ? Term.bold : Term.accent, agent.name.padding(toLength: 20, withPad: " ", startingAt: 0))) \(Term.paint(Term.dim, Term.clip(agent.description, max(20, Term.width - 40))))" + (current ? Term.paint(Term.dim, "  ← current") : ""))
                }
                Term.line(Term.paint(Term.dim, "  Use one with /agent NAME · /agent none goes back to the default."))
            } else if ["none", "default", "off"].contains(rest.lowercased()) {
                session.agent = nil; try store.save(session); Term.line(Term.paint(Term.dim, "  Default agent."))
            } else {
                guard let agent = agents.first(where: { $0.name.lowercased() == rest.lowercased() }) else { throw MasonError("No agent named \(rest). /agent lists them.") }
                session.agent = agent.name; try store.save(session)
                Term.line("  Agent: " + Term.paint(Term.bold, agent.name) + Term.paint(Term.dim, "  ·  \(agent.source)"))
            }
        case "/commands":
            let commands = Customization.commands(home: home, workspace: session.workspace)
            if commands.isEmpty { Term.line(Term.paint(Term.dim, "  No custom commands. Add a Markdown file to .mason/commands (or .claude/commands) in this project.")); break }
            Term.line()
            for command in commands { Term.line("  \(Term.paint(Term.accent, ("/" + command.name).padding(toLength: 22, withPad: " ", startingAt: 0))) \(Term.paint(Term.dim, Term.clip(command.description, max(20, Term.width - 30))))") }
            Term.line(Term.paint(Term.dim, "  Run one by typing it, with any arguments after the name."))
        case "/model": try await chooseModel(rest)
        case "/models": try await models(rest)
        default:
            // A custom command from the project or the data folder runs as a request.
            if Customization.expand(Customization.commands(home: home, workspace: session.workspace), input) != nil { await send(input) }
            else { Term.line(Term.paint(Term.yellow, "  Unknown command \(name).") + Term.paint(Term.dim, " /help lists them; /commands lists your own.")) }
        }
        return true
    }

    func help() {
        let rows = [
            ("/new [request]", "Start a separate session, optionally with its first request"),
            ("/resume [number]", "List this folder's sessions, or continue one"),
            ("/ask · /build", "Answer questions only, or make changes"),
            ("/permission read|review|edits|auto", "What Mason may do without asking (also /review, /edits, /auto)"),
            ("/diff [path]", "Show what this session changed"),
            ("/undo · /redo", "Put back, or reapply, everything the latest request changed"),
            ("/compact", "Replace the conversation so far with a summary to free context"),
            ("/agent [name|none]", "List your custom agents, or use one for this session"),
            ("/commands", "List your custom slash commands"),
            ("/rules [revoke NUMBER]", "Saved always-allow rules for this project"),
            ("/model [number]", "List running model servers, or switch to one"),
            ("/models", "List the models on this computer and what each is for"),
            ("/models start NUMBER|NAME", "Start a coding model here (add cpu, or a context size)"),
            ("/models stop", "Stop the model this session started"),
            ("/models search TEXT", "Search Hugging Face"),
            ("/models download REPO [BUILD]", "List a repository's builds, or download one"),
            ("/models setup", "Suggest coding models that fit this Mac and download one"),
            ("/models runtime", "Install or update llama.cpp"),
            ("/status", "Session, model, permission and storage"),
            ("/exit", "Leave (Ctrl+C twice also works)"),
        ]
        Term.line()
        for (command, text) in rows { Term.line("  \(Term.paint(Term.accent, command.padding(toLength: 36, withPad: " ", startingAt: 0))) \(Term.paint(Term.dim, text))") }
        Term.line()
        Term.line(Term.paint(Term.dim, "  While Mason works: Esc or Ctrl+C stops the run. End a line with \\ to continue on the next line."))
    }

    func status() {
        func row(_ label: String, _ value: String) { Term.line("  \(Term.paint(Term.dim, label.padding(toLength: 12, withPad: " ", startingAt: 0))) \(value)") }
        Term.line()
        row("Project", workspace.root.path)
        row("Session", session.title + Term.paint(Term.dim, "  \(session.id.uuidString.prefix(8)) · \(session.state)"))
        row("Model", endpoint.map { endpointName + Term.paint(Term.dim, "  " + $0.url) } ?? Term.paint(Term.yellow, "none running"))
        row("Mode", modeName)
        row("Shell", Shell.path + (Shell.sandboxAvailable ? Term.paint(Term.dim, "  ·  sandbox available for Auto") : Term.paint(Term.yellow, "  ·  no sandbox: Auto refuses commands")))
        if let agent = session.agent { row("Agent", agent) }
        if let tokens = session.promptTokens { row("Context", "\(tokens) tokens in the last request") }
        if let compactions = session.compactions { row("Compacted", "\(compactions) time\(compactions == 1 ? "" : "s"); \(session.archived?.count ?? 0) messages archived") }
        row("Stored in", store.folder.path)
    }

    func diff(_ filter: String) {
        var merged: [String: FileChange] = [:]; var order: [String] = []
        for turn in session.turns where !turn.undone {
            for change in turn.changes where filter.isEmpty || change.path.localizedCaseInsensitiveContains(filter) {
                if var existing = merged[change.path] { existing.after = change.after; existing.afterBlob = change.afterBlob; existing.afterMissing = change.afterMissing; merged[change.path] = existing } else { merged[change.path] = change; order.append(change.path) }
            }
        }
        guard !order.isEmpty else { Term.line(Term.paint(Term.dim, filter.isEmpty ? "  This session has not changed any files." : "  No changed file matches \(filter).")); return }
        var budget = 400
        for path in order {
            guard let change = merged[path] else { continue }
            if change.isOpaque {
                Term.line(); Term.line("  " + Term.paint(Term.bold, path) + Term.paint(Term.dim, "  binary or large file, \(change.deleted ? "deleted" : change.created ? "created" : "changed")")); continue
            }
            let rows = Diff.lines(before: change.before, after: change.deleted ? "" : change.after)
            Term.line()
            Term.line("  " + Term.paint(Term.bold, path) + "  " + Term.paint(Term.green, "+\(rows.filter { $0.kind == .added }.count)") + " " + Term.paint(Term.red, "−\(rows.filter { $0.kind == .removed }.count)") + (change.deleted ? Term.paint(Term.dim, "  deleted") : change.created ? Term.paint(Term.dim, "  new file") : "") + (change.byCommand == true ? Term.paint(Term.dim, "  by a command") : ""))
            // Two lines of context around each change, with gaps marked.
            var keep = [Bool](repeating: false, count: rows.count)
            for (index, row) in rows.enumerated() where row.kind != .same { for near in max(0, index - 2)...min(rows.count - 1, index + 2) { keep[near] = true } }
            var gap = false
            for (index, row) in rows.enumerated() where budget > 0 {
                if !keep[index] { gap = true; continue }
                if gap, index > 0 { Term.line(Term.paint(Term.dim, "       ⋯")) }
                gap = false; budget -= 1
                let text = Term.clip(row.text.replacingOccurrences(of: "\t", with: "    "), Term.width - 12)
                let number = Term.paint(Term.dim, String(row.number).leftPad(6) + " ")
                switch row.kind {
                case .added: Term.line(number + Term.paint(Term.green, "+ " + text))
                case .removed: Term.line(number + Term.paint(Term.red, "- " + text))
                case .same: Term.line(number + "  " + text)
                }
            }
            if budget <= 0 { Term.line(Term.paint(Term.dim, "  … more changes not shown. Use /diff PATH for one file.")); return }
        }
    }

    // MARK: models

    func use(_ endpoint: ModelEndpoint, name: String) {
        self.endpoint = endpoint; endpointName = name
        var preferences = Preferences.load(); preferences.lastEndpoint = endpoint; preferences.save()
    }

    /// A model that is already answering: the one used last, else the first local server found.
    func findRunningModel() async {
        if let last = Preferences.load().lastEndpoint, let listed = await OpenAIClient.models(at: last.url), listed.contains(last.model) { endpoint = last; endpointName = last.model; return }
        if let first = await LocalServers.detect().first { use(first.endpoint, name: first.endpoint.model + " (\(first.name))") }
    }

    func chooseModel(_ rest: String) async throws {
        let servers = try await busy("Looking for running model servers") { _ in await LocalServers.detect() }
        guard !servers.isEmpty else {
            Term.line(Term.paint(Term.dim, "  No model server is running on this Mac. /models lists the models here and starts one."))
            return
        }
        if let number = Int(rest), number >= 1, number <= servers.count {
            use(servers[number - 1].endpoint, name: servers[number - 1].endpoint.model + " (\(servers[number - 1].name))")
            Term.line("  Model: " + Term.paint(Term.bold, endpointName)); return
        }
        Term.line()
        for (index, item) in servers.enumerated() {
            let isCurrent = item.endpoint.url == endpoint?.url && item.endpoint.model == endpoint?.model
            Term.line("  \(Term.paint(Term.dim, String(index + 1).leftPad(2)))  \(Term.paint(Term.green, "●")) \(isCurrent ? Term.paint(Term.bold, item.endpoint.model) : item.endpoint.model)" + Term.paint(Term.dim, "  \(item.name) · \(item.endpoint.url)" + (isCurrent ? "  ← current" : "")))
        }
        Term.line(Term.paint(Term.dim, "  Choose with /model NUMBER."))
    }

    func models(_ rest: String) async throws {
        let parts = rest.split(separator: " ", maxSplits: 1).map(String.init)
        let verb = parts.first?.lowercased() ?? "", argument = parts.count > 1 ? parts[1].trimmingCharacters(in: .whitespaces) : ""
        switch verb {
        case "", "list": try await listModels()
        case "start": try await startNamed(argument)
        case "stop":
            guard let running = server else { Term.line(Term.paint(Term.dim, "  This session has not started a model.")); break }
            running.stop(); server = nil
            if endpoint?.url == running.endpoint.url { endpoint = nil }
            Term.line(Term.paint(Term.dim, "  Stopped \(running.model.name)."))
        case "search": try await search(argument)
        case "download": try await download(argument)
        case "setup": try await setup()
        case "runtime": if let path = try await installRuntime(force: true) { Term.line(Term.paint(Term.green, "  ✓ llama.cpp is ready") + Term.paint(Term.dim, "  " + path)) }
        default: Term.line(Term.paint(Term.yellow, "  Use /models, /models start, stop, search, download, setup or runtime."))
        }
    }

    func listModels() async throws {
        listed = try await busy("Looking for models on this computer") { _ in ModelLibrary.scan() }
        guard !listed.isEmpty else {
            Term.line(Term.paint(Term.dim, "  No model files were found on this Mac."))
            Term.line(Term.paint(Term.dim, "  /models setup suggests one that fits and downloads it · /models search TEXT looks on Hugging Face."))
            return
        }
        Term.line()
        for (index, model) in listed.prefix(40).enumerated() {
            let running = server?.model.path == model.path && server?.isRunning == true
            let fit = model.startable ? (MemoryFit.fits(weights: model.bytes) ? "" : Term.paint(Term.yellow, "  larger than the memory available")) : ""
            Term.line("  \(Term.paint(Term.dim, String(index + 1).leftPad(2)))  \(model.startable ? Term.paint(Term.bold, Term.clip(model.name, 52)) : Term.clip(model.name, 52))" + Term.paint(Term.dim, "  \(model.size) · \(model.source)") + (running ? Term.paint(Term.green, "  ● running here") : fit))
            Term.line(Term.paint(Term.dim, "      " + model.verdict))
        }
        if listed.count > 40 { Term.line(Term.paint(Term.dim, "      … and \(listed.count - 40) more files")) }
        Term.line(Term.paint(Term.dim, "  Start a coding model with /models start NUMBER (add cpu, or a context size such as 16384)."))
    }

    func startNamed(_ argument: String) async throws {
        var words = argument.split(separator: " ").map(String.init), cpu = false, context: Int?
        for word in words.dropFirst() {
            if word.lowercased() == "cpu" { cpu = true; words.removeAll { $0 == word } }
            else if let tokens = Int(word), tokens >= 512 { context = tokens; words.removeAll { $0 == word } }
        }
        let wanted = words.joined(separator: " ")
        guard !wanted.isEmpty else { throw MasonError("Use /models start NUMBER or NAME. /models lists them.") }
        if listed.isEmpty { listed = ModelLibrary.scan() }
        let model: LocalModel
        if let number = Int(wanted), number >= 1, number <= listed.count { model = listed[number - 1] }
        else if let match = listed.first(where: { $0.startable && $0.name.localizedCaseInsensitiveContains(wanted) }) { model = match }
        else { throw MasonError("No model on this computer matches \"\(wanted)\". /models lists them.") }
        guard model.startable else { throw MasonError("\(model.name) cannot be started here: \(model.verdict).") }
        _ = try await start(model, context: context, cpuOnly: cpu)
    }

    /// Starts a model with llama.cpp and makes it this session's model.
    func start(_ model: LocalModel, context: Int? = nil, cpuOnly: Bool = false) async throws -> Bool {
        var runtime = LlamaRuntime.find()
        if runtime == nil {
            Term.line(Term.paint(Term.yellow, "  llama.cpp is not installed yet; it runs GGUF models."))
            runtime = try await installRuntime(force: false)
        }
        guard let runtime else { return false }
        server?.stop(); server = nil
        do {
            let started = try await busy("Starting \(model.name)") { progress in try await ModelServer.start(model, runtime: runtime, context: context, cpuOnly: cpuOnly, progress: progress) }
            server = started
            use(started.endpoint, name: model.name)
            var preferences = Preferences.load(); preferences.lastModelPath = model.path; preferences.lastContext = started.context; preferences.save()
            Term.line(Term.paint(Term.green, "  ✓ \(model.name) is running") + Term.paint(Term.dim, "  ·  \(started.context) context · stops when you leave mason"))
            return true
        } catch let error as MasonError {
            Term.line(Term.paint(Term.red, "  ✗ \(model.name) did not start. ") + error.description)
            return false
        }
    }

    func installRuntime(force: Bool) async throws -> String? {
        if !force, let existing = LlamaRuntime.find() { return existing }
        let release = try await busy("Finding the current llama.cpp release") { _ in try await LlamaRuntime.latest() }
        Term.line("  llama.cpp \(release.tag) · \(release.asset) · \(ByteText.size(release.size))")
        Term.line(Term.paint(Term.dim, "  From the official ggml-org release on GitHub\(release.sha256 == nil ? "" : ", SHA-256 verified"), installed under \(LlamaRuntime.managedFolder.path)"))
        guard confirm("Download and install it?") else { return nil }
        return try await busy("Installing llama.cpp") { progress in try await LlamaRuntime.install(release, progress: progress) }
    }

    func search(_ query: String) async throws {
        guard !query.isEmpty else { throw MasonError("Use /models search TEXT, for example /models search qwen coder.") }
        let results = try await busy("Searching Hugging Face") { _ in try await Hub.search(query) }
        found = Array(results.prefix(20))
        guard !found.isEmpty else { Term.line(Term.paint(Term.dim, "  Nothing on Hugging Face matches \"\(query)\".")); return }
        Term.line()
        for (index, model) in found.enumerated() {
            Term.line("  \(Term.paint(Term.dim, String(index + 1).leftPad(2)))  \(Term.paint(Term.bold, Term.clip(model.id, 60)))" + Term.paint(Term.dim, "  \(model.downloads) downloads\(model.gated ? " · access terms" : "")"))
            Term.line(Term.paint(Term.dim, "      " + model.verdict))
        }
        if results.count > found.count { Term.line(Term.paint(Term.dim, "      … \(results.count - found.count) more results. Narrow the search to see them.")) }
        Term.line(Term.paint(Term.dim, "  See the builds of one with /models download NUMBER."))
    }

    func download(_ argument: String) async throws {
        let words = argument.split(separator: " ", maxSplits: 1).map(String.init)
        guard let first = words.first else { throw MasonError("Use /models download NUMBER (from a search) or OWNER/NAME, then choose a build.") }
        let id: String
        if let number = Int(first), number >= 1, number <= found.count { id = found[number - 1].id } else { id = first }
        let repository = try await busy("Reading \(id)") { _ in try await Hub.inspect(id) }
        guard !repository.builds.isEmpty else { Term.line(Term.paint(Term.yellow, "  \(id) has no single-file GGUF builds to download.")); return }
        let best = repository.suggested()
        guard words.count > 1 else {
            Term.line()
            Term.line("  " + Term.paint(Term.bold, repository.id) + Term.paint(Term.dim, "  license \(repository.license.isEmpty ? "not stated" : repository.license)\(repository.gated ? " · access terms apply" : "") · \(ByteText.size(MemoryFit.budgetBytes)) of memory available for a model"))
            for (index, build) in repository.builds.prefix(40).enumerated() {
                let fit = build.isProjector ? Term.paint(Term.dim, "vision companion file") : MemoryFit.fits(weights: build.bytes) ? Term.paint(Term.green, "fits · about \(ByteText.size(MemoryFit.neededBytes(weights: build.bytes)))") : Term.paint(Term.yellow, "needs about \(ByteText.size(MemoryFit.neededBytes(weights: build.bytes)))")
                Term.line("  \(Term.paint(Term.dim, String(index + 1).leftPad(2)))  \(Term.clip(build.file, 46).padding(toLength: 46, withPad: " ", startingAt: 0)) \(Term.paint(Term.dim, ByteText.size(build.bytes).padding(toLength: 12, withPad: " ", startingAt: 0))) \(fit)\(build == best ? Term.paint(Term.accent, "  ← suggested") : "")")
            }
            Term.line(Term.paint(Term.dim, "  Download one with /models download \(repository.id) NUMBER."))
            return
        }
        let build: HubBuild
        if let number = Int(words[1]), number >= 1, number <= repository.builds.count { build = repository.builds[number - 1] }
        else if let match = repository.builds.first(where: { $0.file.localizedCaseInsensitiveContains(words[1]) }) { build = match }
        else { throw MasonError("No build of \(id) matches \"\(words[1])\".") }
        _ = try await fetch(build, from: repository)
    }

    /// Downloads one build after confirmation, then offers to start it.
    func fetch(_ build: HubBuild, from repository: HubRepository) async throws -> Bool {
        Term.line("  \(Term.paint(Term.bold, repository.id)) · \(build.file) · \(ByteText.size(build.bytes)) · license \(repository.license.isEmpty ? "not stated" : repository.license)")
        Term.line(Term.paint(Term.dim, "  From huggingface.co, verified against its published SHA-256, saved under \(ModelLibrary.downloadFolder.path)"))
        if repository.gated { Term.line(Term.paint(Term.yellow, "  This repository has access terms. Accept them on its model page if the download is refused.")) }
        guard confirm("Download it?") else { return false }
        let file = try await busy("Downloading \(build.file)") { progress in
            try await Hub.download(build, from: repository) { done, total in progress("Downloading \(build.file) · \(total > 0 ? Int(Double(done) / Double(total) * 100) : 0)% · \(ByteText.size(done)) of \(ByteText.size(total))") }
        }
        Term.line(Term.paint(Term.green, "  ✓ Downloaded and verified") + Term.paint(Term.dim, "  " + file.path))
        listed = []
        let model = LocalModel(name: file.deletingPathExtension().lastPathComponent, path: file.path, bytes: build.bytes, source: "Mason downloads")
        guard model.startable, confirm("Start it now?") else { return true }
        return try await start(model)
    }

    func setup() async throws {
        let picks = try await busy("Finding coding models that fit this Mac") { progress in try await Hub.suggestions(progress: progress) }
        Term.line("  " + Term.paint(Term.dim, "\(ByteText.size(MemoryFit.budgetBytes)) of memory available for a model"))
        guard !picks.isEmpty else { Term.line(Term.paint(Term.yellow, "  No suggested coding model fits. Pick one with /models search.")); return }
        for (index, pick) in picks.enumerated() {
            Term.line("  \(Term.paint(Term.dim, String(index + 1).leftPad(2)))  \(Term.paint(Term.bold, pick.repository.id)) · \(pick.build.file) · \(ByteText.size(pick.build.bytes))")
            Term.line(Term.paint(Term.dim, "      \(pick.build.quant) is the highest-quality build that fits: about \(ByteText.size(MemoryFit.neededBytes(weights: pick.build.bytes)))."))
        }
        guard Term.interactive else { return }
        Term.write("  Download which? Type a number, or press Enter to skip: ")
        guard let number = Int(readLine()?.trimmingCharacters(in: .whitespaces) ?? ""), number >= 1, number <= picks.count else { return }
        _ = try await fetch(picks[number - 1].build, from: picks[number - 1].repository)
    }

    /// Runs when nothing is answering at startup: restart the model used last, else offer the coding models here, else offer a download.
    func firstRun() async {
        guard Term.interactive else {
            Term.line(Term.paint(Term.yellow, "  No model is running.") + Term.paint(Term.dim, " /models lists the models on this computer; /models setup finds one to download."))
            return
        }
        do {
            let preferences = Preferences.load()
            if let path = preferences.lastModelPath, FileManager.default.fileExists(atPath: path), LlamaRuntime.find() != nil {
                let size = ((try? FileManager.default.attributesOfItem(atPath: URL(fileURLWithPath: path).resolvingSymlinksInPath().path))?[.size] as? NSNumber)?.int64Value ?? 0
                let model = LocalModel(name: URL(fileURLWithPath: path).deletingPathExtension().lastPathComponent, path: path, bytes: size, source: "Last used")
                Term.line(Term.paint(Term.dim, "  Nothing is answering yet, so Mason is starting the model you used last."))
                if try await start(model, context: preferences.lastContext) { return }
            }
            listed = try await busy("Looking for models on this computer") { _ in ModelLibrary.scan() }
            let choices = Array(listed.filter(\.startable).prefix(12))
            Term.line()
            guard !choices.isEmpty else {
                Term.line("  Nothing is answering and no coding model was found on this Mac.")
                if confirm("Find one that fits this Mac and download it?") { try await setup() }
                else { Term.line(Term.paint(Term.dim, "  Later: /models setup, /models search TEXT, or start your own server and type /model.")) }
                return
            }
            Term.line("  Nothing is answering yet. Coding models on this Mac:")
            for (index, model) in choices.enumerated() { Term.line("  \(Term.paint(Term.dim, String(index + 1).leftPad(2)))  \(Term.paint(Term.bold, Term.clip(model.name, 60)))" + Term.paint(Term.dim, "  \(model.size)")) }
            Term.write("  Start which? Type a number, or press Enter to skip: ")
            if let number = Int(readLine()?.trimmingCharacters(in: .whitespaces) ?? ""), number >= 1, number <= choices.count { _ = try await start(choices[number - 1]) }
            else { Term.line(Term.paint(Term.dim, "  Later: /models lists them · /models setup suggests a download · /model switches to a running server.")) }
        } catch is CancellationError { Term.line(Term.paint(Term.yellow, "  Cancelled.") + Term.paint(Term.dim, " /models continues when you are ready.")) }
        catch { Term.line(Term.paint(Term.red, "  ✗ " + ((error as? MasonError)?.description ?? error.localizedDescription))) }
    }
}

extension String {
    func leftPad(_ width: Int) -> String { count >= width ? self : String(repeating: " ", count: width - count) + self }
}

exit(await CLI.main())
