import Foundation
import MasonCore
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// What a chat reports while it runs, handed from its task to the terminal loop.
final class ChatFeed: @unchecked Sendable {
    private let lock = NSLock()
    private var live = ""
    private var steps: [ChatStep] = []
    private var asking: (ApprovalRequest, CheckedContinuation<ChatApproval, Never>)?
    private var finished = false
    private var failure: Error?

    func text(_ piece: String) { lock.lock(); live += piece; lock.unlock() }
    func step(_ step: ChatStep) { lock.lock(); steps.append(step); live = ""; lock.unlock() }
    func ask(_ request: ApprovalRequest, _ answer: CheckedContinuation<ChatApproval, Never>) { lock.lock(); asking = (request, answer); lock.unlock() }
    func end(_ error: Error?) { lock.lock(); failure = error; finished = true; lock.unlock() }

    var done: Bool { lock.lock(); defer { lock.unlock() }; return finished }
    var error: Error? { lock.lock(); defer { lock.unlock() }; return failure }
    var liveText: String { lock.lock(); defer { lock.unlock() }; return live }
    func takeSteps() -> [ChatStep] { lock.lock(); defer { steps = []; lock.unlock() }; return steps }
    func takeApproval() -> (ApprovalRequest, CheckedContinuation<ChatApproval, Never>)? { lock.lock(); defer { asking = nil; lock.unlock() }; return asking }
}

/// `mason chat`: a conversation with the running model, kept in the same store as the app's Chat, so a chat started in
/// either place continues in the other. It has no file, shell or code tools; it may read the web, skills and MCP servers.
final class ChatCLI {
    let store: ChatStore
    var conversation: ChatConversation
    var endpoint: ModelEndpoint?
    var endpointName = ""
    var attachments: [ChatAttachment] = []
    var home: URL { store.folder.deletingLastPathComponent() }

    init(store: ChatStore, conversation: ChatConversation) { self.store = store; self.conversation = conversation }

    static let usage = """
        mason chat [--resume]
          A conversation with the running model, shared with the app's Chat. It can read the web, your skills and your
          MCP servers, asking first; it cannot read your files or run commands. --resume continues the latest chat.
        """

    static func run(_ arguments: [String]) async -> Int32 {
        if arguments.contains("--help") || arguments.contains("-h") { print(usage); return 0 }
        if let unknown = arguments.first(where: { $0 != "--resume" }) {
            FileHandle.standardError.write(Data("Unknown option \(unknown). Run mason chat --help.\n".utf8))
            return 1
        }
        do {
            let store = try ChatStore()
            let conversation: ChatConversation
            if arguments.contains("--resume") {
                guard let latest = store.conversations.first(where: { $0.messages.count > 1 }) else { throw MasonError("No saved chat to resume.") }
                conversation = latest
            } else {
                conversation = try store.conversations.first(where: { $0.messages.count <= 1 }) ?? store.create()
            }
            let chat = ChatCLI(store: store, conversation: conversation)
            await chat.findModel()
            chat.banner(resumed: arguments.contains("--resume"))
            await chat.loop()
            return 0
        } catch {
            FileHandle.standardError.write(Data(((error as? MasonError)?.description ?? error.localizedDescription).appending("\n").utf8))
            return 1
        }
    }

    // MARK: the loop

    func findModel() async {
        if let last = Preferences.load().lastEndpoint, let listed = await OpenAIClient.models(at: last.url), listed.contains(last.model) { endpoint = last; endpointName = last.model; return }
        if let first = await LocalServers.detect().first { endpoint = first.endpoint; endpointName = first.endpoint.model + " (\(first.name))" }
    }

    func banner(resumed: Bool) {
        Term.line()
        Term.line("  " + Term.paint(Term.bold + Term.accent, "Mason Chat") + Term.paint(Term.dim, "  ·  model ") + (endpoint == nil ? Term.paint(Term.yellow, "none running") : endpointName) + Term.paint(Term.dim, "  ·  /help for commands"))
        if resumed {
            Term.line(Term.paint(Term.dim, "  resumed ") + conversation.title)
            showHistory()
        }
        if endpoint == nil { Term.line(Term.paint(Term.yellow, "  No model is running.") + Term.paint(Term.dim, " Start one with mason (/models), then /model here.")) }
    }

    /// The end of a resumed conversation, so it is clear where it stands.
    func showHistory() {
        let shown = conversation.messages.filter { ($0.role == "user" || $0.role == "assistant") && !$0.content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }.suffix(4)
        for message in shown {
            Term.line()
            if message.role == "user" { Term.line("  " + Term.paint(Term.accent + Term.bold, "› ") + Term.clip(message.content.replacingOccurrences(of: "\n", with: " "), max(20, Term.width - 6))) }
            else { for line in Term.markdown(message.content).suffix(12) { Term.line("  " + line) } }
        }
    }

    var prompt: String { Term.paint(Term.dim, "chat ") + Term.paint(Term.accent + Term.bold, "› ") }

    func readMessage() -> String? {
        var text = ""
        while true {
            Term.line()
            if text.isEmpty, !attachments.isEmpty { Term.line(Term.paint(Term.dim, "  attached: " + attachments.map(\.name).joined(separator: ", "))) }
            Term.write(text.isEmpty ? prompt : Term.paint(Term.dim, "  … "))
            guard let line = readLine() else { return text.isEmpty ? nil : text }
            if line.hasSuffix("\\") { text += line.dropLast() + "\n"; continue }
            return (text + line).trimmingCharacters(in: .whitespacesAndNewlines)
        }
    }

    func loop() async {
        while true {
            guard let input = readMessage() else { Term.line(); return }
            if input.isEmpty { continue }
            do {
                if input.hasPrefix("/") { if try await command(input) == false { return } }
                else { await send(input) }
            } catch { Term.line(Term.paint(Term.red, "  ✗ " + ((error as? MasonError)?.description ?? error.localizedDescription))) }
        }
    }

    // MARK: commands

    func command(_ input: String) async throws -> Bool {
        let parts = input.split(separator: " ", maxSplits: 1).map(String.init)
        let name = parts[0].lowercased(), rest = parts.count > 1 ? parts[1].trimmingCharacters(in: .whitespaces) : ""
        switch name {
        case "/exit", "/quit": return false
        case "/help", "/?": help()
        case "/new":
            conversation = try store.conversations.first(where: { $0.messages.count <= 1 }) ?? store.create()
            attachments = []
            Term.line(Term.paint(Term.dim, "  New chat."))
        case "/resume":
            // The app may have started or continued chats since this one began.
            try store.reload()
            let recent = store.conversations.filter { $0.messages.count > 1 }.prefix(20).map { $0 }
            guard !recent.isEmpty else { Term.line(Term.paint(Term.dim, "  No saved chats yet.")); break }
            var choice = Int(rest)
            if choice == nil {
                Term.line()
                for (index, item) in recent.enumerated() {
                    let current = item.id == conversation.id
                    Term.line("  " + Term.paint(Term.dim, String(index + 1).leftPad(2)) + "  " + (current ? Term.paint(Term.bold, Term.clip(item.title, 60)) : Term.clip(item.title, 60)) + Term.paint(Term.dim, "  ·  " + (item.model ?? "no answer yet") + (current ? "  ← current" : "")))
                }
                Term.write("  Number (Enter keeps this chat): ")
                choice = Int(readLine()?.trimmingCharacters(in: .whitespaces) ?? "")
            }
            guard let number = choice, number >= 1, number <= recent.count else { break }
            conversation = recent[number - 1]
            attachments = []
            Term.line("  Resumed " + Term.paint(Term.bold, conversation.title))
            showHistory()
        case "/attach":
            guard !rest.isEmpty else { throw MasonError("Use /attach FILE.") }
            guard attachments.count < ChatAttachment.maxPerMessage else { throw MasonError("Attach at most \(ChatAttachment.maxPerMessage) files to one message.") }
            let file = URL(fileURLWithPath: (rest as NSString).expandingTildeInPath, relativeTo: URL(fileURLWithPath: FileManager.default.currentDirectoryPath)).standardizedFileURL
            let attachment = try ChatAttachment(file: file)
            attachments.removeAll { $0.name == attachment.name }
            attachments.append(attachment)
            Term.line(Term.paint(Term.green, "  ✓ Attached \(attachment.name)") + Term.paint(Term.dim, "  ·  it goes with your next message"))
        case "/reasoning":
            let level = rest.lowercased()
            guard ["off", "low", "medium", "high", "default"].contains(level) else { throw MasonError("Use /reasoning off, low, medium, high or default.") }
            try Chat.configure(&conversation, reasoning: level, maxTokens: conversation.maxTokens ?? 0, reference: conversation.reference ?? "")
            try store.save(conversation)
            Term.line(Term.paint(Term.dim, "  Reasoning: " + (level == "default" ? "the server's default" : level)))
        case "/tools":
            let words = rest.lowercased().split(separator: " ").map(String.init)
            if words.count == 2, ["on", "off"].contains(words[0]) {
                try Chat.setTools(&conversation, kind: words[1], on: words[0] == "on")
                try store.save(conversation)
            } else if !words.isEmpty { throw MasonError("Use /tools, or /tools on|off web|skills|mcp.") }
            showTools()
        case "/model": try await chooseModel(rest)
        default:
            Term.line(Term.paint(Term.yellow, "  Unknown command \(name).") + Term.paint(Term.dim, " /help lists them."))
        }
        return true
    }

    func help() {
        let rows = [
            ("/new", "Start a new chat"),
            ("/resume [number]", "List recent chats and continue one"),
            ("/attach FILE", "Attach a text file, picture, PDF or Word document to your next message (up to 5)"),
            ("/reasoning off|low|medium|high|default", "How much the model thinks before answering"),
            ("/tools", "This chat's tools: web, skills and MCP, each on or off"),
            ("/tools on|off web|skills|mcp", "Switch one on or off for this chat"),
            ("/model [number]", "List running model servers, or switch to one"),
            ("/help", "This list"),
            ("/exit", "Leave"),
        ]
        Term.line()
        for (command, text) in rows { Term.line("  \(Term.paint(Term.accent, command.padding(toLength: 40, withPad: " ", startingAt: 0))) \(Term.paint(Term.dim, text))") }
        Term.line()
        Term.line(Term.paint(Term.dim, "  Approvals take one key: y once, a always, n no. Esc or Ctrl+C stops an answer. End a line with \\ to continue it."))
        Term.line(Term.paint(Term.dim, "  Chats are shared with the Chat pane of Mason Agent. Plain mason is the coding agent."))
    }

    func showTools() {
        let switches = conversation.switches
        let model = endpoint.flatMap { try? OpenAIClient($0) }
        Term.line()
        if let model, !Chat.canCallTools(model) { Term.line(Term.paint(Term.yellow, "  This model can't call tools.")) }
        let internet = NetworkSettings.webAllowed(home: home)
        let skills = Chat.skills(home: home).count, servers = Mcp.enabled(home: home)
        func row(_ name: String, _ on: Bool, _ note: String) {
            Term.line("  " + name.padding(toLength: 8, withPad: " ", startingAt: 0) + (on ? Term.paint(Term.green, "on ") : Term.paint(Term.dim, "off")) + "  " + Term.paint(Term.dim, note))
        }
        row("web", switches.web, internet ? "web_fetch and web_search; each new host asks first" : "the internet is off on this computer (/network in mason)")
        row("skills", switches.skills, skills == 0 ? "no skills in bundles or \(home.appendingPathComponent("skills").path)" : "\(skills) skill\(skills == 1 ? "" : "s") from bundles and this computer")
        row("mcp", switches.mcp, servers.isEmpty ? "no MCP servers are switched on" : servers.map { "\($0.name) (\($0.tools.count) tools allowed)" }.joined(separator: ", ") + "; every call asks first")
        Term.line(Term.paint(Term.dim, "  Change one with /tools on|off web|skills|mcp. The choice is saved with this chat."))
    }

    func chooseModel(_ rest: String) async throws {
        let servers = await LocalServers.detect()
        guard !servers.isEmpty else { Term.line(Term.paint(Term.dim, "  No model server is running on this computer. Start one with mason (/models).")); return }
        if let number = Int(rest), number >= 1, number <= servers.count {
            endpoint = servers[number - 1].endpoint
            endpointName = servers[number - 1].endpoint.model + " (\(servers[number - 1].name))"
            var preferences = Preferences.load(); preferences.lastEndpoint = endpoint; preferences.save()
            Term.line("  Model: " + Term.paint(Term.bold, endpointName)); return
        }
        Term.line()
        for (index, item) in servers.enumerated() {
            let current = item.endpoint.url == endpoint?.url && item.endpoint.model == endpoint?.model
            Term.line("  \(Term.paint(Term.dim, String(index + 1).leftPad(2)))  \(Term.paint(Term.green, "●")) \(current ? Term.paint(Term.bold, item.endpoint.model) : item.endpoint.model)" + Term.paint(Term.dim, "  \(item.name) · \(item.endpoint.url)" + (current ? "  ← current" : "")))
        }
        Term.line(Term.paint(Term.dim, "  Choose with /model NUMBER."))
    }

    // MARK: sending

    func send(_ text: String) async {
        guard let endpoint, let client = try? OpenAIClient(endpoint) else {
            Term.line(Term.paint(Term.yellow, "  No model is running.") + Term.paint(Term.dim, " Start one with mason (/models), then /model here."))
            return
        }
        let feed = ChatFeed(), box = Box(conversation), files = attachments, store = store, started = Date()
        attachments = []
        let tools = ChatTools(home: home, approve: { request in await withCheckedContinuation { feed.ask(request, $0) } }, onStep: feed.step)
        let task = Task {
            var working = box.value
            var failure: Error?
            do { try await Chat.send(&working, text: text, attachments: files, model: client, store: store, tools: tools, onText: feed.text) } catch { failure = error }
            box.value = working
            feed.end(failure)
        }
        Term.line()
        Term.raw(true)
        var frame = 0, stopping = false
        while !feed.done {
            for step in feed.takeSteps() { Term.setStatus(""); show(step) }
            if let (request, answer) = feed.takeApproval() { Term.setStatus(""); answer.resume(returning: approve(request, task: task)); continue }
            if let key = Term.key(), key == 3 || (key == 27 && Term.wasEscapeAlone()) { task.cancel() }
            if task.isCancelled, !stopping { stopping = true; Term.line(Term.paint(Term.dim, "  Stopping…")) }
            frame += 1
            let spinner = Array("⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏")[frame % 10]
            let live = feed.liveText.replacingOccurrences(of: "\n", with: " ")
            let room = max(20, Term.width - 30)
            let doing = live.isEmpty ? "Thinking" : (live.count > room ? "…" + live.suffix(room - 1) : live)
            Term.setStatus("  \(spinner) \(doing)  ·  \(Int(Date().timeIntervalSince(started)))s  ·  Esc to stop")
        }
        await task.value
        Term.setStatus(""); Term.raw(false)
        for step in feed.takeSteps() { show(step) }
        conversation = box.value
        if let error = feed.error {
            if error is CancellationError || task.isCancelled { Term.line(Term.paint(Term.yellow, "  Stopped.") + Term.paint(Term.dim, " Your message is kept; the next one continues it.")) }
            else { Term.line(Term.paint(Term.red, "  ✗ " + ((error as? MasonError)?.description ?? error.localizedDescription))) }
            if !files.isEmpty, conversation.messages.last?.role != "user" { attachments = files }
            return
        }
        if let answer = conversation.messages.last, answer.role == "assistant" {
            Term.line()
            for line in Term.markdown(answer.content) { Term.line("  " + line) }
        }
    }

    func show(_ step: ChatStep) {
        let mark: String
        switch step.state {
        case .completed: mark = Term.paint(Term.green, "✓")
        case .denied: mark = Term.paint(Term.yellow, "⊘")
        default: mark = Term.paint(Term.red, "✗")
        }
        let label = Activity.label(step.tool)
        Term.line("  \(mark) \(Term.paint(Term.bold, label)) \(Term.clip(step.subject.replacingOccurrences(of: "\n", with: " "), max(10, Term.width - label.count - 40)))" + Term.paint(Term.dim, "  " + step.outcome))
    }

    /// Called in raw mode. One key: y once, a always, n (or Esc) no.
    func approve(_ request: ApprovalRequest, task: Task<Void, Never>) -> ChatApproval {
        let bar = Term.paint(Term.yellow, "  ▌ ")
        Term.line()
        Term.line(bar + Term.paint(Term.bold, request.title))
        for line in request.detail.split(separator: "\n").prefix(20) { Term.line(bar + Term.paint(Term.dim, Term.clip(String(line), Term.width - 6))) }
        guard Term.interactive else { Term.line(bar + "Declined: there is no terminal to ask."); return .no }
        let always = request.tool == "network" ? "always for every chat" : "always"
        Term.write("  \(Term.paint(Term.bold, "Allow?"))  \(Term.paint(Term.accent, "y")) once   \(Term.paint(Term.accent, "a")) \(always)   \(Term.paint(Term.accent, "n")) no  › ")
        Term.drainKeys()
        while true {
            if task.isCancelled { Term.line("cancelled"); return .no }
            guard let key = Term.key() else { continue }
            if key == 3 || key == 27 { Term.line(Term.paint(Term.yellow, "no")); return .no }
            switch Character(Unicode.Scalar(key)).lowercased() {
            case "y": Term.line(Term.paint(Term.green, "once")); return .once
            case "a": Term.line(Term.paint(Term.green, "always")); return .always
            case "n": Term.line(Term.paint(Term.yellow, "no")); return .no
            default: continue
            }
        }
    }
}
