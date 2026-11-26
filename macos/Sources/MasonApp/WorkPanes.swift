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
        case "/network": return (try NetworkSettings.command(rest, home: home), nil)
        case "/bundle": return (try Bundles.command(rest, home: home), nil)
        default: break
        }
        guard let session, let workspace = try? Workspace(path: session.workspace) else { throw MasonError("Open a project first.") }
        switch name {
        case "/policy": return (try Policies.command(rest, workspace: workspace, home: home), nil)
        case "/experiment": return (try await Experiments.command(rest, workspace: workspace, home: home), nil)
        case "/deliver": return try await Delivery.command(rest, workspace: workspace, home: home)
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
    @State private var tasks: [(task: BackgroundTask, status: TaskStatus)] = []

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
            Section {
                Toggle("Ask when a command needs a host", isOn: Binding(get: { asks }, set: { run("/network", $0 ? "ask" : "off") }))
            } header: {
                Text("Network in Auto")
            } footer: {
                Text("Commands in the sandbox have no network unless this is on. A sandboxed command can then ask for one host over HTTPS, and your answer lasts for that request. This computer, your local network and plain HTTP stay out of reach.")
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
