import UserNotifications
import Foundation
import SwiftUI
import MasonCore

/// An approval the engine is waiting on, shown as a card above the composer.
struct PendingApproval: Identifiable {
    let id = UUID()
    let request: ApprovalRequest
    let answer: CheckedContinuation<Bool, Never>
}

/// A question a chat is waiting on, shown as a card in the chat: Once, Always or No.
struct ChatPendingApproval: Identifiable {
    let id = UUID()
    let request: ApprovalRequest
    let answer: CheckedContinuation<ChatApproval, Never>
}

struct DownloadState: Equatable {
    var title: String
    var fraction: Double
    var detail: String
}

/// Everything the windows show. All changes happen on the main actor; the engine runs in tasks and reports back here.
@MainActor
final class AppModel: ObservableObject {
    @Published var sessions: [Session] = []
    @Published var selection: UUID?
    @Published var draft = ""
    /// What the side panel shows: moments (the feed), moment (the opened moment), changes, plan, files or website.
    @Published var panel = "moments"
    /// The moment opened from the feed and kept on the shelf: nil for none, "end" for the run's summary, or a call's id
    /// with "|path" for one file of an edit (see `Moments.openKey`).
    @Published var activityPick: String?
    /// An opened moment shows each call's subject and result as they are, instead of the summary.
    @Published var activityRaw = false
    /// The file being read in the Files panel, and the line the model pointed at.
    @Published var fileShown: String?
    @Published var fileLine = 1
    /// A page the model asked to show; the Website panel opens it and clears this.
    @Published var pageShown: String?
    /// The Website panel's web view, kept so its page stays loaded while another panel is shown.
    lazy var website = WebsiteState()
    @Published var running = false
    @Published var liveText = ""
    @Published var liveStatus = ""
    @Published var liveActivities: [Activity] = []
    @Published var approval: PendingApproval?
    @Published var notice: String?
    /// The latest error turned into words, so a notice that reports it shows the check-failed pose.
    private(set) var lastError: String?
    /// When files were last put back (undo, redo or a fork that restores them); the status shows "restored" for 30 seconds.
    @Published var restoredAt: Date?
    /// The search box over sessions, projects, files, commands and chats, and what it has to choose from.
    @Published var paletteShown = false
    var paletteItems: [PaletteItem] = []

    /// Which part of the window is showing.
    @Published var pane: Pane = .code
    /// Whether the files the session changed are shown beside the conversation.
    @Published var showChanges = false
    @Published var chats: [ChatConversation] = []
    @Published var chatSelection: UUID?
    @Published var chatDraft = ""
    @Published var chatRunning = false
    @Published var chatLive = ""
    @Published var chatAttachments: [ChatAttachment] = []
    /// The tool steps of the answer being written, and the question it is waiting on.
    @Published var chatSteps: [ChatStep] = []
    @Published var chatApproval: ChatPendingApproval?
    /// The engine working on the current request, so a follow-up can be handed to it.
    private var activeAgent: Agent?
    /// Follow-ups sent during the current request that have not started yet.
    @Published var waitingFollowUps = 0
    /// The milestone whose check is running.
    @Published var milestoneBusy: UUID?
    /// True while the model is reading what the session changed.
    @Published var reviewing = false
    /// The Notes tray over the end of the transcript: whether it is open, its field and the file a new note is about.
    @Published var notesOpen = false
    @Published var noteField = ""
    @Published var noteSource: String?
    /// The notes of the project in view, newest first, and those whose file changed since.
    @Published var projectNotes: [ProjectNote] = []
    @Published var staleNotes: Set<UUID> = []
    /// What Undo in the tray puts back, while it shows.
    @Published var notesUndo: NotesUndo?
    /// True while the tray asks whether to clear every note.
    @Published var notesConfirmClear = false
    private var chatStore: ChatStore?
    private var chatTask: Task<Void, Never>?

    @Published var endpoint: ModelEndpoint?
    @Published var endpointName = ""
    @Published var servers: [(name: String, endpoint: ModelEndpoint)] = []
    @Published var server: ModelServer?
    @Published var localModels: [LocalModel] = []
    /// Memory metadata for each startable model, read on every scan, for the Ladder panel.
    @Published var memoryProfiles: [String: ModelMemoryProfile] = [:]
    /// The model being started and how far it has got.
    @Published var startingPath: String?
    @Published var startStage: ModelStartStage?
    @Published var modelBusy: String?
    @Published var searchText = ""
    @Published var searchResults: [HubModel] = []
    @Published var repository: HubRepository?
    @Published var download: DownloadState?
    @Published var runtimePath: String? = LlamaRuntime.find()
    @Published var suggestions: [(repository: HubRepository, build: HubBuild)] = []

    var store: SessionStore?
    private var runTask: Task<Void, Never>?
    private var modelTask: Task<Void, Never>?

    init() {
        do {
            let opened = try SessionStore()
            store = opened
            sessions = opened.sessions
            selection = sessions.first?.id
        } catch {
            notice = "Mason could not open its data folder: \(describe(error))"
        }
        if let opened = try? ChatStore() {
            chatStore = opened
            chats = opened.conversations
            chatSelection = chats.first?.id
        }
        Task { await refreshModels(startLastIfNeeded: true) }
        // Scheduled requests are checked shortly after launch and then every minute while the app is open.
        scheduleTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in Task { @MainActor in await self?.runDueSchedules() } }
        Task { try? await Task.sleep(nanoseconds: 8_000_000_000); await runDueSchedules() }
    }

    // MARK: schedules and notifications

    private var scheduleTimer: Timer?
    private var schedulesRunning = false
    @Published var scheduleStatus = ""

    /// Runs every schedule that is due, one after another, each as its own unattended session.
    func runDueSchedules(only name: String? = nil) async {
        guard let store, !schedulesRunning else { return }
        let home = store.folder.deletingLastPathComponent()
        let all = Schedules.load(home: home)
        let chosen = name.map { wanted in all.filter { $0.name.lowercased() == wanted.lowercased() } } ?? Schedules.due(all)
        guard !chosen.isEmpty else { return }
        schedulesRunning = true
        for schedule in chosen {
            scheduleStatus = "Running the schedule \(schedule.name)"
            await Schedules.run(schedule, home: home, store: store)
            sessions = store.sessions
            let outcome = Schedules.load(home: home).first { $0.id == schedule.id }?.lastOutcome ?? "finished"
            notify("Scheduled: \(schedule.name)", outcome)
        }
        scheduleStatus = ""
        schedulesRunning = false
    }

    /// "/schedule ..." typed in the request box: list, add, switch or remove a schedule, or run one now.
    func scheduleCommand(_ arguments: String) {
        guard let store else { return }
        let home = store.folder.deletingLastPathComponent()
        if arguments.lowercased().hasPrefix("run ") {
            let name = arguments.dropFirst(4).trimmingCharacters(in: .whitespaces)
            guard Schedules.load(home: home).contains(where: { $0.name.lowercased() == name.lowercased() }) else { notice = "No schedule named \(name)."; return }
            notice = "Running \(name) now, unattended. Its session appears in the sidebar when it finishes."
            Task { await runDueSchedules(only: name) }
            return
        }
        do { notice = try Schedules.command(arguments, home: home, workspace: session?.workspace ?? NSHomeDirectory(), endpoint: endpoint) }
        catch { notice = describe(error) }
    }

    /// A system notification, shown only while Mason is in the background. It needs the app bundle and the user's permission.
    func notify(_ title: String, _ body: String) {
        guard !NSApp.isActive, Bundle.main.bundleIdentifier != nil else { return }
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
            guard granted else { return }
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body.count > 180 ? String(body.prefix(180)) + "…" : body
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }

    // MARK: chat

    var chat: ChatConversation? { chats.first { $0.id == chatSelection } }

    func newChat() {
        guard let chatStore else { return }
        pane = .chat
        // Reuse an untouched chat rather than collecting empty ones.
        if let empty = chats.first(where: { $0.messages.count <= 1 }) { chatSelection = empty.id; return }
        do { let created = try chatStore.create(); chats = chatStore.conversations; chatSelection = created.id } catch { notice = describe(error) }
    }

    func deleteChat(_ chat: ChatConversation) {
        guard !(chatRunning && chat.id == chatSelection) else { notice = "Stop the answer before deleting this chat."; return }
        chatStore?.delete(chat.id)
        chats.removeAll { $0.id == chat.id }
        if chatSelection == chat.id { chatSelection = chats.first?.id }
    }

    /// Sets the selected chat's reasoning level ("default" for the server's own), output limit (0 for none) and pinned reference.
    func configureChat(reasoning: String, maxTokens: Int, reference: String) {
        guard var current = chat, let chatStore, !chatRunning else { return }
        do {
            try Chat.configure(&current, reasoning: reasoning, maxTokens: maxTokens, reference: reference)
            try chatStore.save(current)
            chats = chatStore.conversations
        } catch { notice = describe(error) }
    }

    func attachToChat() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true; panel.canChooseDirectories = false; panel.allowsMultipleSelection = true
        panel.prompt = "Attach"
        panel.message = "Choose text files to send with your message."
        guard panel.runModal() == .OK else { return }
        for url in panel.urls where chatAttachments.count < ChatAttachment.maxPerMessage {
            do {
                let file = try ChatAttachment(file: url)
                if !chatAttachments.contains(where: { $0.name == file.name }) { chatAttachments.append(file) }
            } catch { notice = describe(error) }
        }
    }

    func sendChat() {
        let text = chatDraft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !chatRunning, let chatStore, let current = chat, !text.isEmpty || !chatAttachments.isEmpty else { return }
        guard let endpoint, let client = try? OpenAIClient(endpoint) else {
            notice = "No model is running. Open Models to start one, or start your own local server."
            return
        }
        let files = chatAttachments
        chatDraft = ""; chatAttachments = []; chatRunning = true; chatLive = ""; chatSteps = []
        // Tools ask here: each question shows as a card in the chat, and each step joins the answer as it happens.
        let tools = ChatTools(home: chatStore.folder.deletingLastPathComponent(), approve: { [weak self] request in
            await withCheckedContinuation { continuation in
                Task { @MainActor in
                    guard let self else { continuation.resume(returning: .no); return }
                    self.chatApproval = ChatPendingApproval(request: request, answer: continuation)
                    NSApp.requestUserAttention(.informationalRequest)
                }
            }
        }, onStep: { [weak self] step in
            Task { @MainActor in self?.chatSteps.append(step); self?.chatLive = "" }
        })
        chatTask = Task {
            var working = current
            do {
                try await Chat.send(&working, text: text, attachments: files, model: client, store: chatStore, tools: tools) { [weak self] piece in
                    Task { @MainActor in self?.chatLive += piece }
                }
            } catch is CancellationError {
            } catch {
                if !Task.isCancelled { notice = describe(error) }
            }
            if let pending = chatApproval { chatApproval = nil; pending.answer.resume(returning: .no) }
            // The store has the conversation as far as it got: with the answer, or with the question alone.
            chats = chatStore.conversations
            chatRunning = false; chatLive = ""; chatSteps = []
        }
    }

    func stopChat() {
        chatTask?.cancel()
        if let pending = chatApproval { chatApproval = nil; pending.answer.resume(returning: .no) }
    }

    /// Answers the question a chat is waiting on.
    func answerChat(_ pending: ChatPendingApproval, _ answer: ChatApproval) {
        guard chatApproval?.id == pending.id else { return }
        chatApproval = nil
        pending.answer.resume(returning: answer)
    }

    /// Whether the running model can be offered tools in Chat.
    var chatCanCallTools: Bool {
        guard let endpoint, let client = try? OpenAIClient(endpoint) else { return true }
        return Chat.canCallTools(client)
    }

    /// Turns the web, skills or MCP servers on or off for the selected chat; the choice is saved with it.
    func setChatTools(_ kind: String, _ on: Bool) {
        guard var current = chat, let chatStore, !chatRunning else { return }
        do {
            try Chat.setTools(&current, kind: kind, on: on)
            try chatStore.save(current)
            chats = chatStore.conversations
        } catch { notice = describe(error) }
    }

    /// ⌘Return and ⌘. act on whichever pane is showing.
    func sendCurrent() { if pane == .chat { sendChat() } else { send() } }
    func stopCurrent() { if pane == .chat { stopChat() } else { stop() } }

    func describe(_ error: Error) -> String {
        let words = (error as? MasonError)?.description ?? error.localizedDescription
        lastError = words
        return words
    }

    // MARK: sessions

    var session: Session? { sessions.first { $0.id == selection } }
    var projects: [String] {
        var seen = Set<String>()
        return sessions.map(\.workspace).filter { seen.insert($0).inserted }
    }
    func sessions(in project: String) -> [Session] { sessions.filter { $0.workspace == project } }

    func replace(_ session: Session) {
        if let index = sessions.firstIndex(where: { $0.id == session.id }) { sessions[index] = session } else { sessions.insert(session, at: 0) }
    }
    func save(_ session: Session) {
        replace(session)
        do { try store?.save(session) } catch { notice = "The session could not be saved: \(describe(error))" }
    }

    func openFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.allowsMultipleSelection = false
        panel.prompt = "Open Project"
        panel.message = "Choose the project folder Mason should work in."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        newSession(in: url.path)
    }

    func newSession(in folder: String? = nil) {
        guard let store else { return }
        guard let path = folder ?? session?.workspace else { openFolder(); return }
        do {
            let workspace = try Workspace(path: path)
            // Reuse an untouched session rather than collecting empty ones.
            if let empty = sessions.first(where: { $0.workspace == workspace.root.path && $0.turns.isEmpty }) { selection = empty.id; return }
            let created = try store.create(workspace: workspace.root.path, permission: session?.permission ?? .reviewEdits)
            replace(created); selection = created.id
        } catch { notice = describe(error) }
    }

    func delete(_ session: Session) {
        guard !(running && session.id == selection) else { notice = "Stop the run before deleting this session."; return }
        try? store?.delete(session.id)
        sessions.removeAll { $0.id == session.id }
        if selection == session.id { selection = sessions.first?.id }
    }

    func setPermission(_ permission: Permission) {
        guard var current = session, !running else { return }
        current.permission = permission; current.askOnly = permission == .readOnly
        save(current)
    }

    // MARK: running a request

    func send() {
        // While a request runs, Send queues the message for afterwards.
        if running { followUp(steer: false); return }
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        // Comments pinned to lines are a request of their own, with or without typed text.
        guard let current = session, let store, !text.isEmpty || !(current.comments ?? []).isEmpty else { return }
        // Two commands act on the session itself. Any other /name is a custom command, which the engine expands.
        if text.lowercased() == "/compact" { draft = ""; compact(); return }
        if text.lowercased() == "/mcp" { draft = ""; showMcpServers(); return }
        if text.lowercased() == "/schedule" || text.lowercased().hasPrefix("/schedule ") || text.lowercased() == "/schedules" {
            draft = ""; scheduleCommand(text.split(separator: " ", maxSplits: 1).dropFirst().first.map(String.init) ?? ""); return
        }
        // Commands that act on the project or on Mason itself, shared with the command line. What they say is shown as a notice.
        let word = text.split(separator: " ", maxSplits: 1).first.map { $0.lowercased() } ?? ""
        // "/note" or "/notes" alone opens the Notes tray; "/notes clear" opens it asking first.
        if word == "/note" || word == "/notes" {
            let arguments = ProjectNotes.arguments(word, String(text.dropFirst(word.count)))
            if arguments.isEmpty || arguments.lowercased() == "clear" {
                draft = ""
                openNotes(confirmClear: !arguments.isEmpty)
                return
            }
        }
        if ["/experiment", "/experiments", "/deliver", "/task", "/tasks", "/roles", "/role", "/policy", "/network", "/bundle", "/bundles", "/languages", "/language", "/generation", "/notes", "/note", "/skills", "/tools"].contains(word) {
            draft = ""
            hostCommand(word, String(text.dropFirst(word.count)).trimmingCharacters(in: .whitespaces), in: current)
            return
        }
        if text.lowercased() == "/agent" || text.lowercased().hasPrefix("/agent ") { draft = ""; chooseAgent(String(text.dropFirst(6)).trimmingCharacters(in: .whitespaces)); return }
        // "/skill NAME REQUEST" runs the request with the skill's instructions in front of it.
        if word == "/skill" {
            let words = text.split(separator: " ", maxSplits: 2).map(String.init)
            guard words.count == 3, let workspace = try? Workspace(path: current.workspace) else {
                notice = "Use /skill NAME REQUEST. Settings, Agents lists the skills."
                return
            }
            do { draft = try Skills.request(words[1], request: words[2], workspace: workspace, home: store.folder.deletingLastPathComponent()) }
            catch { notice = describe(error); return }
            send()
            return
        }
        guard let endpoint, let model = try? OpenAIClient(endpoint) else {
            notice = "No model is running. Open Models to start one, or start your own local server."
            return
        }
        draft = ""; running = true; liveText = ""; liveStatus = "Thinking"; liveActivities = []
        // Sending a request closes the Notes tray.
        closeNotes()
        // Show the request at once; the engine's copy replaces it when the run ends.
        var shown = current
        shown.turns.append(Turn(request: Review.request(text, comments: current.comments ?? []), firstActivity: current.activities.count))
        shown.state = "Running"; shown.ending = nil; shown.repeatStop = nil
        replace(shown)
        runTask = Task {
            let agent = Agent(store: store, model: model, approve: { [weak self] request in
                await withCheckedContinuation { continuation in
                    Task { @MainActor in
                        guard let self else { continuation.resume(returning: false); return }
                        self.approval = PendingApproval(request: request, answer: continuation)
                        NSApp.requestUserAttention(.informationalRequest)
                        self.notify("Mason needs your approval", request.title)
                    }
                }
            }, emit: { [weak self] event in
                Task { @MainActor in self?.receive(event) }
            })
            // The model may show the user a file at a line, or one of the project's pages.
            agent.present = { [weak self] kind, path, line in
                Task { @MainActor in
                    guard let self else { return }
                    if kind == "page" { self.pageShown = path; self.panel = "website" } else { self.fileLine = line; self.fileShown = path; self.panel = "files" }
                }
            }
            self.activeAgent = agent
            var working = current
            await agent.run(&working, request: text)
            self.activeAgent = nil
            self.finish(working)
        }
    }

    /// Sends what is in the request box to the running request: queued for afterwards, or steering it now.
    func followUp(steer: Bool) {
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard running, !text.isEmpty, approval == nil else { return }
        guard let activeAgent else { notice = "This request cannot take a follow-up. Wait for it to finish, or stop it."; return }
        do {
            try activeAgent.sendWhileRunning(text, steer: steer)
            draft = ""
            waitingFollowUps += 1
            closeNotes()
        } catch { notice = describe(error) }
    }

    /// Runs the follow-ups a stopped or failed request left waiting.
    func runQueued() {
        guard !running, session?.pendingInput?.isEmpty == false else { return }
        draft = "/queue"
        send()
    }

    private func receive(_ event: AgentEvent) {
        switch event {
        case .text(let text): liveText += text
        case .activityStarted(let activity):
            liveText = ""
            liveStatus = activity.tool.replacingOccurrences(of: "_", with: " ") + " " + activity.subject
            liveActivities.append(activity)
        case .activityFinished(let activity):
            if let index = liveActivities.firstIndex(where: { $0.id == activity.id }) { liveActivities[index] = activity }
            liveStatus = "Thinking"
        case .status(let text): liveStatus = text
        case .followUp(let state, let text):
            // The previous request is finished: show it as it ended, and the follow-up as the request now running.
            var shown = state
            shown.turns.append(Turn(request: text, firstActivity: state.activities.count))
            shown.state = "Running"; shown.ending = nil; shown.repeatStop = nil
            replace(shown)
            liveText = ""; liveActivities = []
            waitingFollowUps = max(0, waitingFollowUps - 1)
        }
    }

    private func finish(_ result: Session) {
        if let pending = approval { pending.answer.resume(returning: false); approval = nil }
        replace(result)
        notify(result.title, result.state + (result.detail.isEmpty ? "" : ": " + result.detail))
        running = false; liveText = ""; liveStatus = ""; liveActivities = []
        waitingFollowUps = 0
        runTask = nil
        // The request may have changed a file a note is about.
        reloadNotes()
    }

    func stop() { runTask?.cancel(); if let pending = approval { answer(pending, allow: false) } }

    func answer(_ pending: PendingApproval, allow: Bool, alwaysPrefix: String? = nil) {
        if allow, let prefix = alwaysPrefix, let current = session {
            var rules = ApprovalRules(workspace: current.workspace)
            do { try rules.add(tool: pending.request.tool, prefix: prefix) } catch { notice = describe(error) }
        }
        approval = nil
        pending.answer.resume(returning: allow)
    }

    /// Replaces the conversation so far with a summary from the model. The original messages stay in the session.
    func compact() {
        guard let current = session, let store, !running else { return }
        guard current.messages.count > 2 else { notice = "There is nothing to compact yet."; return }
        guard let endpoint, let client = try? OpenAIClient(endpoint) else { notice = "Compaction needs a running model. Open Models to start one."; return }
        running = true; liveText = ""; liveStatus = "Compacting the conversation"; liveActivities = []
        runTask = Task {
            var working = current
            do {
                if try await Compaction.compact(&working, model: client, keep: 0) {
                    try store.save(working)
                    replace(working)
                    notice = "The conversation was replaced by a summary, which goes with your next request. The original messages are kept."
                } else { notice = "There is nothing to compact yet." }
            } catch { notice = describe(error) }
            running = false; liveStatus = ""
        }
    }

    /// "/agent" lists the project's custom agents, "/agent NAME" uses one, "/agent none" goes back to the default.
    func chooseAgent(_ name: String) {
        guard var current = session, let store, !running else { return }
        let agents = Customization.agents(home: store.folder.deletingLastPathComponent(), workspace: current.workspace)
        if name.isEmpty {
            notice = agents.isEmpty ? "No custom agents. Add a Markdown file to .mason/agents (or .claude/agents) in this project."
                : "Agents: " + agents.map(\.name).joined(separator: ", ") + ". Type /agent NAME to use one, or /agent none for the default." + (current.agent.map { " Current: \($0)." } ?? "")
            return
        }
        if ["none", "default", "off"].contains(name.lowercased()) { current.agent = nil }
        else if let agent = agents.first(where: { $0.name.lowercased() == name.lowercased() }) { current.agent = agent.name }
        else { notice = "No agent named \(name)."; return }
        save(current)
        notice = current.agent.map { "Agent: \($0)." } ?? "Default agent."
    }

    /// MCP servers are registered for every project in Mason's data folder; the mason command manages them.
    func showMcpServers() {
        guard let store else { return }
        let servers = Mcp.enabled(home: store.folder.deletingLastPathComponent())
        notice = servers.isEmpty
            ? "No MCP servers are registered. In a terminal, run mason and type /mcp add NAME http://127.0.0.1:PORT/mcp (or the full path of a program)."
            : servers.map { "\($0.name): " + ($0.tools.isEmpty ? "no tools allowed yet" : $0.tools.joined(separator: ", ")) }.joined(separator: "\n") + "\n\nEvery call asks first. Manage servers with /mcp in the mason command."
    }

    func undo(redo: Bool = false) {
        guard var current = session, let store, !running else { return }
        do {
            guard let result = try Agent.undoLatest(&current, store: store, redo: redo) else { notice = redo ? "Nothing to redo." : "Nothing to undo in this session."; return }
            replace(current)
            restoredAt = Date()
            let verb = redo ? "reapplied" : "put back"
            notice = "\(result.restored.count) file\(result.restored.count == 1 ? "" : "s") \(verb)." + (result.skipped.isEmpty ? "" : " Changed since and left alone: \(result.skipped.joined(separator: ", ")).")
        } catch { notice = describe(error) }
    }

    // MARK: models

    func use(_ endpoint: ModelEndpoint, name: String) {
        self.endpoint = endpoint; endpointName = name
        var preferences = Preferences.load(); preferences.lastEndpoint = endpoint; preferences.save()
    }

    /// Finds running servers and local files. On launch, with nothing answering, it restarts the model used last.
    func refreshModels(startLastIfNeeded: Bool = false) async {
        var found = await LocalServers.detect()
        // The endpoint used last may be on a port the scan does not cover (a server the user started themselves).
        if let last = Preferences.load().lastEndpoint, !found.contains(where: { $0.endpoint == last }),
           let listed = await OpenAIClient.models(at: last.url), listed.contains(last.model) {
            found.insert((name: "Local server", endpoint: last), at: 0)
        }
        let files = await Task.detached { ModelLibrary.scan() }.value
        let profiles = await Task.detached {
            Dictionary(files.filter(\.startable).map { ($0.path, ModelMemory.read($0)) }, uniquingKeysWith: { first, _ in first })
        }.value
        servers = found; localModels = files; memoryProfiles = profiles; runtimePath = LlamaRuntime.find()
        if let current = endpoint, found.contains(where: { $0.endpoint == current }) { return }
        if let last = Preferences.load().lastEndpoint, found.contains(where: { $0.endpoint == last }) { endpoint = last; endpointName = last.model; return }
        if let first = found.first { use(first.endpoint, name: "\(first.endpoint.model) (\(first.name))"); return }
        endpoint = nil; endpointName = ""
        if startLastIfNeeded, let path = Preferences.load().lastModelPath, let model = files.first(where: { $0.path == path }), runtimePath != nil {
            start(model, context: Preferences.load().lastContext, cpuOnly: Preferences.load().lastCpuOnly ?? false)
        }
    }

    func start(_ model: LocalModel, context: Int? = nil, cpuOnly: Bool = false) {
        guard modelBusy == nil else { return }
        // Transformers weights run through Python, not llama.cpp.
        guard let runtime = model.transformers ? "" : runtimePath else { notice = "Install llama.cpp first; it runs GGUF models."; return }
        modelBusy = "Starting \(model.name)…"
        server?.stop(); server = nil
        startingPath = model.path; startStage = .inspect
        modelTask = Task {
            defer { self.startingPath = nil; self.startStage = nil }
            do {
                let started = try await ModelServer.start(model, runtime: runtime, context: context, cpuOnly: cpuOnly,
                                                          stage: { [weak self] stage in Task { @MainActor in self?.startStage = stage } }) { [weak self] line in
                    Task { @MainActor in if !line.isEmpty { self?.modelBusy = "Starting \(model.name)… \(line.suffix(90))" } }
                }
                self.server = started
                self.use(started.endpoint, name: model.name)
                var preferences = Preferences.load(); preferences.lastModelPath = model.path; preferences.lastContext = started.context; preferences.lastCpuOnly = cpuOnly; preferences.save()
            } catch is CancellationError {
            } catch { self.notice = "\(model.name) did not start. \(self.describe(error))" }
            self.modelBusy = nil
            await self.refreshModels()
        }
    }

    func stopServer() {
        guard let running = server else { return }
        running.stop(); server = nil
        if endpoint == running.endpoint { endpoint = nil; endpointName = "" }
        Task { await refreshModels() }
    }

    func cancelModelWork() { modelTask?.cancel() }

    /// Moves a model's files to the Trash (never a permanent delete) and scans again. A running model is left alone.
    func trash(_ item: LocalModel) {
        guard server?.model.path != item.path, modelBusy == nil else { return }
        do {
            for url in item.trashItems { try FileManager.default.trashItem(at: url, resultingItemURL: nil) }
            notice = "Moved \(item.name) to the Trash."
        } catch { notice = "\(item.name) was not moved to the Trash. \(describe(error))" }
        Task { await refreshModels() }
    }

    func installRuntime() {
        guard modelBusy == nil else { return }
        modelBusy = "Finding the current llama.cpp release…"
        modelTask = Task {
            do {
                let release = try await LlamaRuntime.latest()
                let path = try await LlamaRuntime.install(release) { [weak self] text in Task { @MainActor in self?.modelBusy = text } }
                self.runtimePath = path
            } catch is CancellationError {
            } catch { self.notice = "llama.cpp was not installed. \(self.describe(error))" }
            self.modelBusy = nil
        }
    }

    func search() {
        let query = searchText.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty, modelBusy == nil else { return }
        modelBusy = "Searching Hugging Face…"; repository = nil
        modelTask = Task {
            do { self.searchResults = try await Hub.search(query) } catch is CancellationError {} catch { self.notice = self.describe(error) }
            self.modelBusy = nil
        }
    }

    func inspect(_ model: HubModel) {
        guard modelBusy == nil else { return }
        modelBusy = "Reading \(model.id)…"
        modelTask = Task {
            do { self.repository = try await Hub.inspect(model.id) } catch is CancellationError {} catch { self.notice = self.describe(error) }
            self.modelBusy = nil
        }
    }

    func suggest() {
        guard modelBusy == nil else { return }
        modelBusy = "Finding coding models that fit this Mac…"
        modelTask = Task {
            do { self.suggestions = try await Hub.suggestions { [weak self] text in Task { @MainActor in self?.modelBusy = text } } }
            catch is CancellationError {} catch { self.notice = self.describe(error) }
            self.modelBusy = nil
        }
    }

    /// Downloads a build the user chose, verifies it, then starts it.
    func fetch(_ build: HubBuild, from repository: HubRepository) {
        guard download == nil else { return }
        download = DownloadState(title: build.file, fraction: 0, detail: "Starting…")
        modelTask = Task {
            do {
                let file = try await Hub.download(build, from: repository) { [weak self] done, total in
                    Task { @MainActor in
                        self?.download = DownloadState(title: build.file, fraction: total > 0 ? Double(done) / Double(total) : 0, detail: "\(ByteText.size(done)) of \(ByteText.size(total))")
                    }
                }
                self.download = nil
                await self.refreshModels()
                if let model = self.localModels.first(where: { $0.path == file.path }), model.startable { self.start(model) }
            } catch is CancellationError { self.download = nil }
            catch { self.download = nil; self.notice = "The download did not finish. \(self.describe(error))" }
        }
    }

    // MARK: command line tool

    /// Where the mason tool inside the app bundle lives.
    var bundledTool: URL? { Bundle.main.url(forResource: "mason", withExtension: nil, subdirectory: "bin") }

    /// Links mason into /usr/local/bin after the system's administrator prompt, so it works in any terminal.
    func installCommandLineTool() {
        guard let tool = bundledTool else { notice = "This copy of Mason Agent does not include the mason tool."; return }
        let quoted = tool.path.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let script = "do shell script \"mkdir -p /usr/local/bin && ln -sf \\\"\(quoted)\\\" /usr/local/bin/mason\" with administrator privileges"
        var failure: NSDictionary?
        NSAppleScript(source: script)?.executeAndReturnError(&failure)
        if let failure {
            let message = failure[NSAppleScript.errorMessage] as? String ?? "The link was not created."
            if (failure[NSAppleScript.errorNumber] as? Int) != -128 { notice = "mason was not installed. \(message)" }
        } else {
            notice = "mason is installed. Open a new Terminal window, go to a project folder and type mason."
        }
    }
}
