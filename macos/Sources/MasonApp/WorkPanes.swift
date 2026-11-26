import SwiftUI
import MasonCore

// Settings for the things that are also commands: project policy, experiments, pull requests and background tasks;
// model roles and the network; bundles. Each control runs the same code as its command.

extension AppModel {
    /// Runs one of the commands shared with the command line. Returns what it says, and a request to put in the
    /// request box when it hands one back.
    func commandText(_ name: String, _ rest: String) async throws -> (text: String, request: String?) {
        let home = store?.folder.deletingLastPathComponent() ?? DataHome.url
        switch name {
        case "/roles": return (try Roles.command(rest, home: home), nil)
        case "/network":
            // In a session the choice is the session's; without one it is the computer's.
            guard var current = session else { return (try NetworkSettings.command(rest, home: home), nil) }
            let said = try NetworkSettings.sessionCommand(rest, session: &current, home: home)
            save(current)
            return (said, nil)
        case "/bundle": return (try Bundles.command(rest, home: home), nil)
        case "/generation": return (try Generation.command(rest, endpoint: endpoint, home: home), nil)
        case "/languages": return (try await LanguageServers.command(rest, workspace: session.flatMap { try? Workspace(path: $0.workspace) }, home: home), nil)
        default: break
        }
        guard let session, let workspace = try? Workspace(path: session.workspace) else { throw MasonError("Open a project first.") }
        switch name {
        case "/policy": return (try Policies.command(rest, workspace: workspace, home: home), nil)
        case "/experiment": return (try await Experiments.command(rest, workspace: workspace, home: home), nil)
        case "/deliver": return try await Delivery.command(rest, workspace: workspace, receipt: session.passedReceipt, home: home)
        case "/task": return (try BackgroundTasks.command(rest, workspace: workspace, endpoint: endpoint, auto: session.permission == .auto && !session.askOnly, program: masonProgram, home: home), nil)
        default: throw MasonError("Unknown command \(name).")
        }
    }

    /// Runs a command from a control. A problem is always shown; what the command says is shown when `show` is set.
    func runControl(_ name: String, _ rest: String, show: Bool = false, then: @escaping () -> Void = {}) {
        Task {
            do {
                let said = try await commandText(name, rest)
                if show { notice = said.text }
                if let request = said.request { draft = request }
            } catch {
                notice = describe(error)
            }
            then()
        }
    }
}

/// A text field with a button that submits it, for a control that needs a few words.
struct CommandField: View {
    let prompt: String
    let label: String
    let submit: (String) -> Void
    @State private var text = ""

    var body: some View {
        HStack {
            TextField(prompt, text: $text).textFieldStyle(.roundedBorder).onSubmit(send)
            Button(label, action: send).disabled(text.trimmingCharacters(in: .whitespaces).isEmpty)
        }
    }

    func send() {
        let typed = text.trimmingCharacters(in: .whitespaces)
        guard !typed.isEmpty else { return }
        submit(typed)
        text = ""
    }
}

/// This project's policy, experiments, pull request and background tasks.
struct ProjectPane: View {
    @EnvironmentObject var model: AppModel
    @State private var policy: ProjectPolicy?
    @State private var experiments: [Experiment] = []
    @State private var draft: DeliveryDraft?
    @State private var tasks: [(task: MasonCore.BackgroundTask, status: TaskStatus)] = []

    var home: URL { model.store?.folder.deletingLastPathComponent() ?? DataHome.url }

    var body: some View {
        Form {
            if model.session == nil {
                Text("Open a project to see its policy, experiments, pull request and background tasks.").foregroundStyle(.secondary)
            } else {
                Section {
                    Toggle("Run the project's hooks", isOn: Binding(get: { policy?.hooks ?? false }, set: { run("/policy", $0 ? "hooks on" : "hooks off") }))
                    LabeledContent("Tools switched off", value: (policy?.disabledTools.isEmpty ?? true) ? "None" : (policy?.disabledTools ?? []).joined(separator: ", "))
                    CommandField(prompt: "run_shell start_job", label: "Switch Off") { run("/policy", "disable " + $0) }
                    if !(policy?.disabledTools.isEmpty ?? true) { CommandField(prompt: "Tools to switch back on", label: "Switch On") { run("/policy", "enable " + $0) } }
                    LabeledContent("Workflows that may run", value: policy?.workflows.map { $0.isEmpty ? "None" : $0.joined(separator: ", ") } ?? "Any")
                    CommandField(prompt: "test lint  (or: any)", label: "Set") { run("/policy", "workflows " + $0) }
                    if policy != nil { Button("Remove Policy") { run("/policy", "clear") } }
                } header: {
                    Text("Project policy")
                } footer: {
                    Text("Limits for this project, kept outside it so nothing in the project can change them. With hooks on, in Auto, workflows marked afterEdit run after each edit and ones marked beforeVerify must pass before a request may finish.")
                }

                Section {
                    if experiments.isEmpty { Text("No experiments").foregroundStyle(.secondary) }
                    ForEach(experiments, id: \.name) { experiment in
                        HStack {
                            VStack(alignment: .leading) {
                                Text(experiment.name)
                                Text(experiment.path).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.head)
                            }
                            Spacer()
                            Button("What Changed") { run("/experiment", "diff " + experiment.name, show: true) }
                            Button("Forget") { run("/experiment", "forget " + experiment.name) }
                        }
                    }
                    CommandField(prompt: "Name of a new experiment", label: "Start") { run("/experiment", "new " + $0, show: true) }
                    CommandField(prompt: "NAME  PATH  (a file's hunks)", label: "Show") { run("/experiment", "diff " + $0, show: true) }
                    CommandField(prompt: "NAME  PATH  ID  (or: all)", label: "Apply") { run("/experiment", "apply " + $0, show: true) }
                } header: {
                    Text("Experiments")
                } footer: {
                    Text("Copies of this project in their own Git worktrees, each started from the project exactly as it stood. Try something there, then bring back the hunks you want.")
                }

                Section {
                    if let draft {
                        HStack {
                            VStack(alignment: .leading) {
                                Text(draft.title)
                                Text("Onto \(draft.base) · commit \(String(draft.commit.prefix(12))) · " + (draft.url ?? "not published")).font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            if draft.url == nil { Button("Publish") { run("/deliver", "publish", show: true) } } else {
                                Button("Feedback") { run("/deliver", "feedback", show: true) }
                                Button("Send to Mason") { run("/deliver", "feedback send", show: true) }
                            }
                        }
                    } else {
                        Text("No draft").foregroundStyle(.secondary)
                    }
                    CommandField(prompt: "Branch to review against, such as main", label: "Review") { run("/deliver", "review " + $0, show: true) }
                    CommandField(prompt: "main  all  Title of the pull request", label: "Draft") { run("/deliver", "draft " + $0, show: true) }
                } header: {
                    Text("Pull request")
                } footer: {
                    Text("One commit made from the hunks you choose, without touching your files, index or branches. Publishing pushes it to a new branch and opens a draft pull request through gh.")
                }

                Section {
                    if tasks.isEmpty { Text("No background tasks").foregroundStyle(.secondary) }
                    ForEach(tasks, id: \.task.id) { item in
                        HStack {
                            VStack(alignment: .leading) {
                                Text(item.task.prompt).lineLimit(1)
                                Text(item.status.state + (item.status.approval.map { " · waiting on: " + $0.title } ?? "")).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                            }
                            Spacer()
                            if item.status.approval != nil {
                                Button("Allow") { run("/task", "allow " + item.task.id) }
                                Button("Deny") { run("/task", "deny " + item.task.id) }
                            }
                            Button("Show") { run("/task", "show " + item.task.id, show: true) }
                            if item.status.active { Button("Stop") { run("/task", "cancel " + item.task.id) } }
                        }
                    }
                    CommandField(prompt: "What to do in the background", label: "Start") { run("/task", "start " + $0, show: true) }
                    Button("Refresh") { load() }
                } header: {
                    Text("Background tasks")
                } footer: {
                    Text("Requests that keep running after you close Mason. Outside Auto, a task waits here before each edit and command.")
                }
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: load)
    }

    func load() {
        guard let session = model.session, let workspace = try? Workspace(path: session.workspace) else { return }
        policy = Policies.load(home: home, workspace: session.workspace, includeExpired: true)
        experiments = Experiments.list(workspace, home: home)
        draft = Delivery.current(workspace, home: home)
        tasks = Array(BackgroundTasks.all(workspace: workspace, home: home).prefix(12))
    }

    func run(_ name: String, _ rest: String, show: Bool = false) { model.runControl(name, rest, show: show, then: load) }
}

/// The models in each role, and what Auto does about the network.
struct RolesPane: View {
    @EnvironmentObject var model: AppModel
    @State private var roles = ModelRoles()
    @State private var asks = false

    var home: URL { model.store?.folder.deletingLastPathComponent() ?? DataHome.url }

    var body: some View {
        Form {
            Section {
                LabeledContent("Reviewer", value: roles.reviewer.map { "\($0.model) at \($0.url)" } ?? "The model doing the work")
                CommandField(prompt: "http://127.0.0.1:8081/  MODEL", label: "Use") { run("/roles", "reviewer " + $0) }
                if roles.reviewer != nil { Button("Turn the Reviewer Off") { run("/roles", "reviewer off") } }
                LabeledContent("Visual model", value: roles.visual.map { "\($0.model) at \($0.url)" } ?? "The model doing the work")
                CommandField(prompt: "http://127.0.0.1:8082/  MODEL", label: "Use") { run("/roles", "visual " + $0) }
                if roles.visual != nil { Button("Turn the Visual Model Off") { run("/roles", "visual off") } }
                LabeledContent("Reviewer calls in one review", value: "\(roles.limit)")
                CommandField(prompt: "1 to 500", label: "Set") { run("/roles", "limit " + $0) }
                LabeledContent("Record every model call", value: roles.trace ?? "Off")
                CommandField(prompt: "A folder outside your projects", label: "Record") { run("/roles", "trace " + $0) }
                if roles.trace != nil { Button("Stop Recording") { run("/roles", "trace off") } }
            } header: {
                Text("Model roles")
            } footer: {
                Text("Other local models beside the one doing the work. A reviewer reads what a request changed before it may finish, and for the Review button. A visual model looks at pictures of a window.")
            }
            GenerationSection()
            Section {
                Toggle("Ask when a command needs a host", isOn: Binding(get: { asks }, set: { run("/network", $0 ? "computer ask" : "computer off") }))
            } header: {
                Text("Network in Auto")
            } footer: {
                Text("The setting for this computer; a session can choose its own with /network. Commands in the sandbox have no network unless this is on. A sandboxed command can then ask for one host over HTTPS, and your answer lasts for that request. This computer, your local network and plain HTTP stay out of reach.")
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: load)
    }

    func load() { roles = Roles.load(home: home); asks = NetworkSettings.asks(home: home) }
    func run(_ name: String, _ rest: String) { model.runControl(name, rest, then: load) }
}

/// Folders of skills and documentation installed for every project.
struct BundlesPane: View {
    @EnvironmentObject var model: AppModel
    @State private var bundles: [InstalledBundle] = []

    var home: URL { model.store?.folder.deletingLastPathComponent() ?? DataHome.url }

    var body: some View {
        Form {
            Section {
                if bundles.isEmpty { Text("No bundles are installed").foregroundStyle(.secondary) }
                ForEach(bundles, id: \.name) { bundle in
                    HStack {
                        VStack(alignment: .leading) {
                            Text("\(bundle.name) \(bundle.version)")
                            if !bundle.description.isEmpty { Text(bundle.description).font(.caption).foregroundStyle(.secondary) }
                        }
                        Spacer()
                        Button("Add Its Servers") { run("servers \(bundle.name) add", show: true) }
                        Button("Remove") { run("remove " + bundle.name) }
                    }
                }
                Button("Install a Bundle…") {
                    let panel = NSOpenPanel()
                    panel.canChooseDirectories = true
                    panel.canChooseFiles = false
                    panel.allowsMultipleSelection = false
                    panel.prompt = "Install"
                    if panel.runModal() == .OK, let folder = panel.url { run("install " + folder.path, show: true) }
                }
            } header: {
                Text("Bundles")
            } footer: {
                Text("A bundle is a folder with bundle.json, skills/NAME/SKILL.md and docs/*.md. Its skills and documentation become available in every project. It may also name MCP servers; those are added switched off, for you to look at first.")
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: load)
    }

    func load() { bundles = Bundles.list(home: home) }
    func run(_ rest: String, show: Bool = false) { model.runControl("/bundle", rest, show: show, then: load) }
}

/// The language servers that check edits: which are installed, and installing the ones a language's own tool can fetch.
struct LanguagesPane: View {
    @EnvironmentObject var model: AppModel
    @State private var everything = false
    @State private var rows: [LanguageRow] = []
    @State private var installing: String?

    struct LanguageRow: Identifiable {
        let id: String
        let name: String
        let text: String
        let canInstall: Bool
    }

    var home: URL { model.store?.folder.deletingLastPathComponent() ?? DataHome.url }
    var workspace: Workspace? { model.session.flatMap { try? Workspace(path: $0.workspace) } }

    var body: some View {
        Form {
            Section {
                if workspace != nil { Toggle("Show every language, not only this project's", isOn: $everything) }
                if rows.isEmpty { Text("No file in this project is in a language Mason knows a server for").foregroundStyle(.secondary) }
                ForEach(rows) { row in
                    HStack {
                        VStack(alignment: .leading) {
                            Text(row.name)
                            Text(row.text).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if installing == row.id { ProgressView().controlSize(.small) }
                        if row.canInstall { Button("Install") { install(row) }.disabled(installing != nil) }
                    }
                }
            } header: {
                Text("Language servers")
            } footer: {
                Text("After each edit Mason hands the file to its language's server, and the errors the server reports go back to the model. A server runs in the sandbox in Auto, and asks first otherwise. Mason installs a server only when you choose Install, using that language's own tool. One you installed yourself is found on the PATH.")
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: load)
        .onChange(of: everything) { _, _ in load() }
    }

    func load() {
        let home = home
        var listed = LanguageServers.known
        if !everything, let workspace { listed = LanguageServers.present(in: workspace) }
        var made: [LanguageRow] = []
        for language in listed {
            let said = LanguageServers.described(language, home: home)
            made.append(LanguageRow(id: language.key, name: language.name, text: said.text, canInstall: said.canInstall))
        }
        rows = made
    }

    func install(_ row: LanguageRow) {
        installing = row.id
        model.runControl("/languages", "install " + row.id, show: true) {
            installing = nil
            load()
        }
    }
}

/// Temperature, top P and the like, kept for the model in use.
struct GenerationSection: View {
    @EnvironmentObject var model: AppModel
    @State private var values: [String: Double] = [:]

    var home: URL { model.store?.folder.deletingLastPathComponent() ?? DataHome.url }

    var body: some View {
        Section {
            if model.endpoint == nil {
                Text("Start or choose a model first").foregroundStyle(.secondary)
            } else {
                ForEach(Generation.fields, id: \.key) { field in
                    LabeledContent(field.label, value: values[field.key].map { Generation.shown($0, field) } ?? "The server's default")
                    CommandField(prompt: range(field), label: "Set") { run(field.key + " " + $0) }
                }
                if !values.isEmpty { Button("Use the Server's Defaults") { run("clear") } }
            }
        } header: {
            Text("Generation settings")
        } footer: {
            Text("Kept for " + (model.endpoint.map { "\($0.model) at \($0.url)" } ?? "the model in use") + ", and sent whenever it works as the coding agent. A setting you leave alone is the server's. Top K, Min P and repeat penalty are llama.cpp's. Chats have their own options.")
        }
        .onAppear(perform: load)
    }

    func range(_ field: Generation.Field) -> String {
        field.key == "seed" ? "A whole number, or off" : "\(Generation.shown(field.low, field)) to \(Generation.shown(field.high, field)), or off"
    }

    func load() { values = model.endpoint.map { Generation.values(for: $0, home: home) } ?? [:] }
    func run(_ rest: String) { model.runControl("/generation", rest, then: load) }
}
