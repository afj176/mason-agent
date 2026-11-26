import SwiftUI
import WebKit
import MasonCore

// The Files and Website panels of the side panel.

/// The project's files, and one of them to read. The model can point at a line with open_file.
struct FilesPanel: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    @State private var filter = ""
    @State private var paths: [String] = []
    @State private var clipped = false

    var shown: [String] {
        let needle = filter.trimmingCharacters(in: .whitespaces).lowercased()
        return needle.isEmpty ? paths : paths.filter { $0.lowercased().contains(needle) }
    }

    var body: some View {
        if let open = model.fileShown {
            FileReader(session: session, path: open, pointed: model.fileLine)
        } else {
            VStack(alignment: .leading, spacing: 0) {
                TextField("Filter files", text: $filter).textFieldStyle(.roundedBorder).padding(12)
                if paths.isEmpty {
                    ContentUnavailableView("No files", systemImage: "folder", description: Text("This project has no files yet."))
                } else {
                    List(shown, id: \.self) { path in
                        Button {
                            model.fileLine = 1
                            model.fileShown = path
                        } label: {
                            Text(path).font(.system(.callout, design: .monospaced)).lineLimit(1).truncationMode(.head)
                                .frame(maxWidth: .infinity, alignment: .leading).contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                    if clipped { Text("The first 2,000 files are listed.").font(.caption).foregroundStyle(.secondary).padding(12) }
                }
            }
            .task(id: session.workspace) {
                let listing = try? Workspace(path: session.workspace).files(limit: 2000)
                paths = listing?.paths ?? []
                clipped = listing?.truncated ?? false
            }
        }
    }
}

/// One file with line numbers, for reading. A line the model pointed at is marked and brought into view.
struct FileReader: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    let path: String
    let pointed: Int
    @State private var lines: [String] = []
    @State private var problem: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Button { model.fileShown = nil } label: { Image(systemName: "chevron.left") }
                    .help("Back to the list of files").accessibilityLabel("Back to the list of files")
                Text(path).font(.headline).lineLimit(1).truncationMode(.head).textSelection(.enabled)
                Spacer()
            }
            .padding(12)
            Divider()
            if let problem {
                Text(problem).foregroundStyle(.secondary).padding(12)
                Spacer()
            } else {
                ScrollViewReader { proxy in
                    ScrollView([.vertical, .horizontal]) {
                        LazyVStack(alignment: .leading, spacing: 0) {
                            ForEach(Array(lines.enumerated()), id: \.offset) { index, text in
                                HStack(alignment: .top, spacing: 12) {
                                    Text("\(index + 1)").foregroundStyle(.secondary).frame(width: 44, alignment: .trailing)
                                    Text(text.isEmpty ? " " : text)
                                }
                                .font(.system(.callout, design: .monospaced))
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .background(pointed > 1 && index + 1 == pointed ? Color.orange.opacity(0.22) : Color.clear)
                                .id(index + 1)
                            }
                        }
                        .textSelection(.enabled)
                        .padding(.vertical, 8)
                    }
                    .onChange(of: lines.count) { _, _ in
                        if pointed > 1 { proxy.scrollTo(min(pointed, max(1, lines.count)), anchor: .center) }
                    }
                }
                Divider()
                Text("\(lines.count) line\(lines.count == 1 ? "" : "s"). This is for reading; ask Mason to change the file.")
                    .font(.caption).foregroundStyle(.secondary).padding(8)
            }
        }
        .task(id: path + "|\(pointed)") {
            do {
                lines = try Workspace(path: session.workspace).read(path).components(separatedBy: "\n")
                problem = nil
            } catch {
                lines = []
                problem = model.describe(error)
            }
        }
    }
}

/// The web view behind the Website panel, kept by the app so a page stays loaded while another panel is shown.
@MainActor
final class WebsiteState: NSObject, ObservableObject, WKScriptMessageHandler, WKNavigationDelegate {
    @Published var note = ""
    @Published var picked: PreviewPick?
    @Published var picking = false
    @Published var serving = false
    @Published var busy = false
    let view: WKWebView
    /// The project page shown, or empty when a server's address is shown.
    var page = ""
    private var pages: PagePreview?
    private var root = ""
    private var server: SitePreview.DevServer?

    override init() {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.userContentController.addUserScript(WKUserScript(source: SitePreview.script, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        view = WKWebView(frame: .zero, configuration: configuration)
        super.init()
        // A page here may load only from this computer: its own requests to other hosts are blocked too.
        WKContentRuleListStore.default().compileContentRuleList(forIdentifier: "mason-loopback-only", encodedContentRuleList: SitePreview.loopbackOnly) { [weak self] rules, _ in
            if let rules { self?.view.configuration.userContentController.add(rules) }
        }
        configuration.userContentController.add(self, name: "mason")
        view.navigationDelegate = self
    }

    /// True for an address on this computer, which is all the panel shows.
    static func loopback(_ url: URL) -> Bool {
        if url.absoluteString == "about:blank" { return true }
        guard url.scheme == "http", let host = url.host else { return false }
        return host == "127.0.0.1" || host == "localhost" || host == "::1"
    }

    /// Serves the session's project, stopping what was started for another one.
    func use(_ workspace: String) {
        guard workspace != root else { return }
        pages?.stop(); pages = nil
        stopServer()
        root = workspace; page = ""; picked = nil; note = ""
        guard let folder = try? Workspace(path: workspace), let made = try? PagePreview(workspace: folder) else { note = "The project's pages could not be served."; return }
        pages = made
        if folder.exists("index.html") { open("index.html") } else { note = "Type the path of an HTML page in this project, or start its development server." }
    }

    func open(_ given: String) {
        let text = given.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !root.isEmpty else { return }
        picked = nil; picking = false
        if text.hasPrefix("http://") || text.hasPrefix("https://") {
            guard let url = URL(string: text), WebsiteState.loopback(url) else { note = "The Website panel shows this project's pages and servers on this computer (127.0.0.1 or localhost), not the web."; return }
            page = ""
            note = "Showing \(text)."
            view.load(URLRequest(url: url))
            return
        }
        guard let folder = try? Workspace(path: root), folder.exists(text), ["html", "htm"].contains((text as NSString).pathExtension.lowercased()),
              let pages, let url = URL(string: pages.address(of: text)) else { note = "\(text) is not an HTML page in this project."; return }
        page = text
        note = "Showing \(text)."
        view.load(URLRequest(url: url))
    }

    func togglePick() {
        picking.toggle()
        view.evaluateJavaScript(picking ? SitePreview.arm : SitePreview.disarm, completionHandler: nil)
        if picking { note = "Click an element in the page." }
    }

    func verifyClick() {
        guard let picked, !page.isEmpty, !busy, let folder = try? Workspace(path: root) else { return }
        busy = true
        note = "Clicking it in a fresh browser…"
        let shown = page
        Task {
            do { note = try await SitePreview.verifyClick(folder, page: shown, selector: picked.selector) }
            catch { note = (error as? MasonError)?.description ?? error.localizedDescription }
            busy = false
        }
    }

    func startServer(workflow: String, port: String) {
        guard !busy, let folder = try? Workspace(path: root) else { return }
        guard !workflow.isEmpty, let number = Int(port) else { note = "Name the workflow that starts the server, and the port it listens on."; return }
        busy = true
        note = "Starting \(workflow) with your account, and waiting for port \(port)…"
        Task {
            do {
                let started = try await SitePreview.DevServer(workspace: folder, workflow: workflow, port: number)
                server = started
                serving = true
                open(started.address)
            } catch { note = (error as? MasonError)?.description ?? error.localizedDescription }
            busy = false
        }
    }

    func stopServer() {
        guard let running = server else { return }
        server = nil
        serving = false
        note = "Stopped the development server."
        Task { await running.stop() }
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let text = message.body as? String else { return }
        picking = false
        picked = SitePreview.pick(from: text, page: page.isEmpty ? (view.url?.absoluteString ?? "the page") : page)
        if let picked { note = SitePreview.evidence(picked) }
    }

    /// A navigation to anywhere but this computer is refused: the panel is for the project, not for the web.
    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        decisionHandler(navigationAction.request.url.map(WebsiteState.loopback) == true ? .allow : .cancel)
    }
}

struct WebViewHost: NSViewRepresentable {
    let view: WKWebView
    func makeNSView(context: Context) -> WKWebView { view }
    func updateNSView(_ nsView: WKWebView, context: Context) {}
}

/// The project's pages, or a development server on this computer: to look at, and to pick elements from.
struct WebsitePanel: View {
    @EnvironmentObject var model: AppModel
    @ObservedObject var state: WebsiteState
    let session: Session
    @State private var address = "index.html"
    @State private var workflow = ""
    @State private var port = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                TextField("index.html, or http://127.0.0.1:3000/", text: $address).textFieldStyle(.roundedBorder).onSubmit { state.open(address) }
                    .help("A page of this project, or the address of a server on this computer")
                Button("Open") { state.open(address) }
            }
            HStack {
                Button(state.picking ? "Cancel Pick" : "Pick Element") { state.togglePick() }.help("Then click something in the page to tell Mason about it")
                Button("Verify Click") { state.verifyClick() }.disabled(state.picked == nil || state.page.isEmpty || state.busy)
                    .help("Click the picked element in a fresh browser, at a phone width and a desktop width")
                Spacer()
            }
            DisclosureGroup("Development server") {
                HStack {
                    TextField("Workflow", text: $workflow).textFieldStyle(.roundedBorder).help("A workflow in mason.workflows.json that starts the project's server")
                    TextField("Port", text: $port).textFieldStyle(.roundedBorder).frame(width: 70)
                    Button(state.serving ? "Stop" : "Start") {
                        if state.serving { state.stopServer() } else { state.startServer(workflow: workflow.trimmingCharacters(in: .whitespaces), port: port.trimmingCharacters(in: .whitespaces)) }
                    }
                    .disabled(state.busy)
                }
            }
            if !state.note.isEmpty {
                Text(state.note).font(.caption).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            }
            if state.picked != nil {
                Button("Add to Request") {
                    let existing = model.draft
                    model.draft = existing + (existing.isEmpty || existing.hasSuffix("\n") ? "" : "\n\n") + state.note + "\n"
                }
                .help("Put what is shown above into your next request")
            }
            WebViewHost(view: state.view).frame(minHeight: 320).clipShape(RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.secondary.opacity(0.3)))
        }
        .padding(12)
        .task(id: session.workspace) { state.use(session.workspace) }
        .onChange(of: model.pageShown) { _, wanted in
            if let wanted { address = wanted; state.open(wanted); model.pageShown = nil }
        }
        .onAppear {
            if let wanted = model.pageShown { address = wanted; state.use(session.workspace); state.open(wanted); model.pageShown = nil }
        }
    }
}

extension AppModel {
    /// The mason command line tool inside the app, which is what runs a background task.
    var masonProgram: String { Bundle.main.resourceURL?.appendingPathComponent("bin/mason").path ?? "/usr/local/bin/mason" }

    /// Runs one of the commands the app shares with the command line, and shows what it says.
    func hostCommand(_ name: String, _ rest: String, in session: Session) {
        guard let workspace = try? Workspace(path: session.workspace) else { notice = "The project folder is no longer available."; return }
        let home = store?.folder.deletingLastPathComponent() ?? DataHome.url
        let automatic = session.permission == .auto && !session.askOnly
        Task {
            do {
                switch name {
                case "/experiment", "/experiments": notice = try await Experiments.command(rest, workspace: workspace, home: home)
                case "/deliver":
                    let said = try await Delivery.command(rest, workspace: workspace, receipt: session.passedReceipt, home: home)
                    notice = said.text
                    // Feedback to act on is put in the request box for the user to send.
                    if let request = said.request { draft = request }
                case "/task", "/tasks": notice = try BackgroundTasks.command(rest, workspace: workspace, endpoint: endpoint, auto: automatic, program: masonProgram, home: home)
                case "/roles", "/role": notice = try Roles.command(rest, home: home)
                case "/policy": notice = try Policies.command(rest, workspace: workspace, home: home)
                case "/bundle", "/bundles": notice = try Bundles.command(rest, home: home)
                case "/generation": notice = try Generation.command(rest, endpoint: endpoint, home: home)
                case "/languages", "/language":
                    if rest.hasPrefix("install") { notice = "Installing. This can take a few minutes." }
                    notice = try await LanguageServers.command(rest, workspace: workspace, home: home)
                default: notice = try await commandText("/network", rest).text
                }
            } catch {
                notice = describe(error)
            }
        }
    }
}
