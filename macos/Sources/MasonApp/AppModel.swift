import Foundation
import SwiftUI
import MasonCore

/// An approval the engine is waiting on, shown as a card above the composer.
struct PendingApproval: Identifiable {
    let id = UUID()
    let request: ApprovalRequest
    let answer: CheckedContinuation<Bool, Never>
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
    @Published var running = false
    @Published var liveText = ""
    @Published var liveStatus = ""
    @Published var liveActivities: [Activity] = []
    @Published var approval: PendingApproval?
    @Published var notice: String?

    @Published var endpoint: ModelEndpoint?
    @Published var endpointName = ""
    @Published var servers: [(name: String, endpoint: ModelEndpoint)] = []
    @Published var server: ModelServer?
    @Published var localModels: [LocalModel] = []
    @Published var modelBusy: String?
    @Published var searchText = ""
    @Published var searchResults: [HubModel] = []
    @Published var repository: HubRepository?
    @Published var download: DownloadState?
    @Published var runtimePath: String? = LlamaRuntime.find()
    @Published var suggestions: [(repository: HubRepository, build: HubBuild)] = []

    private var store: SessionStore?
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
        Task { await refreshModels(startLastIfNeeded: true) }
    }

    func describe(_ error: Error) -> String { (error as? MasonError)?.description ?? error.localizedDescription }

    // MARK: sessions

    var session: Session? { sessions.first { $0.id == selection } }
    var projects: [String] {
        var seen = Set<String>()
        return sessions.map(\.workspace).filter { seen.insert($0).inserted }
    }
    func sessions(in project: String) -> [Session] { sessions.filter { $0.workspace == project } }

    private func replace(_ session: Session) {
        if let index = sessions.firstIndex(where: { $0.id == session.id }) { sessions[index] = session } else { sessions.insert(session, at: 0) }
    }
    private func save(_ session: Session) {
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
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !running, let current = session, let store else { return }
        guard let endpoint, let model = try? OpenAIClient(endpoint) else {
            notice = "No model is running. Open Models to start one, or start your own local server."
            return
        }
        draft = ""; running = true; liveText = ""; liveStatus = "Thinking"; liveActivities = []
        // Show the request at once; the engine's copy replaces it when the run ends.
        var shown = current
        shown.turns.append(Turn(request: text, firstActivity: current.activities.count))
        shown.state = "Running"
        replace(shown)
        runTask = Task {
            let agent = Agent(store: store, model: model, approve: { [weak self] request in
                await withCheckedContinuation { continuation in
                    Task { @MainActor in
                        guard let self else { continuation.resume(returning: false); return }
                        self.approval = PendingApproval(request: request, answer: continuation)
                        NSApp.requestUserAttention(.informationalRequest)
                    }
                }
            }, emit: { [weak self] event in
                Task { @MainActor in self?.receive(event) }
            })
            var working = current
            await agent.run(&working, request: text)
            self.finish(working)
        }
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
        }
    }

    private func finish(_ result: Session) {
        if let pending = approval { pending.answer.resume(returning: false); approval = nil }
        replace(result)
        running = false; liveText = ""; liveStatus = ""; liveActivities = []
        runTask = nil
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

    func undo(redo: Bool = false) {
        guard var current = session, let store, !running else { return }
        do {
            guard let result = try Agent.undoLatest(&current, store: store, redo: redo) else { notice = redo ? "Nothing to redo." : "Nothing to undo in this session."; return }
            replace(current)
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
        let found = await LocalServers.detect()
        let files = await Task.detached { ModelLibrary.scan() }.value
        servers = found; localModels = files; runtimePath = LlamaRuntime.find()
        if let current = endpoint, found.contains(where: { $0.endpoint == current }) { return }
        if let last = Preferences.load().lastEndpoint, found.contains(where: { $0.endpoint == last }) { endpoint = last; endpointName = last.model; return }
        if let first = found.first { use(first.endpoint, name: "\(first.endpoint.model) (\(first.name))"); return }
        endpoint = nil; endpointName = ""
        if startLastIfNeeded, let path = Preferences.load().lastModelPath, let model = files.first(where: { $0.path == path }), runtimePath != nil {
            start(model, context: Preferences.load().lastContext)
        }
    }

    func start(_ model: LocalModel, context: Int? = nil) {
        guard modelBusy == nil else { return }
        guard let runtime = runtimePath else { notice = "Install llama.cpp first; it runs GGUF models."; return }
        modelBusy = "Starting \(model.name)…"
        server?.stop(); server = nil
        modelTask = Task {
            do {
                let started = try await ModelServer.start(model, runtime: runtime, context: context) { [weak self] line in
                    Task { @MainActor in if !line.isEmpty { self?.modelBusy = "Starting \(model.name)… \(line.suffix(90))" } }
                }
                self.server = started
                self.use(started.endpoint, name: model.name)
                var preferences = Preferences.load(); preferences.lastModelPath = model.path; preferences.lastContext = started.context; preferences.save()
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
