import SwiftUI
import MasonCore

/// Saved approvals for the current project.
struct ApprovalsPane: View {
    @EnvironmentObject var model: AppModel
    @State private var rules: [ApprovalRules.Rule] = []

    var workspace: String? { model.session?.workspace }

    var body: some View {
        Form {
            Section {
                if workspace == nil {
                    Text("Open a project to see the rules saved for it.").foregroundStyle(.secondary)
                } else if rules.isEmpty {
                    Text("No saved rules. Choose Always Allow… on an approval to save one for this project.").foregroundStyle(.secondary)
                }
                ForEach(Array(rules.enumerated()), id: \.offset) { index, rule in
                    LabeledContent {
                        Button("Revoke") { revoke(index) }
                    } label: {
                        Text("Allow \(rule.tool.replacingOccurrences(of: "_", with: " "))")
                        Text("when it starts with  \(rule.prefix)").font(.system(.caption, design: .monospaced))
                    }
                }
            } header: {
                Text("Saved approvals")
            } footer: {
                Text("Things Mason may do in this project without asking. Rules only skip the question; they never widen the sandbox.")
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: load)
        .onChange(of: model.selection) { _, _ in load() }
    }

    func load() { rules = workspace.map { ApprovalRules(workspace: $0).rules } ?? [] }
    func revoke(_ index: Int) {
        guard let workspace else { return }
        var current = ApprovalRules(workspace: workspace)
        guard index < current.rules.count else { return }
        do { try current.remove(at: index) } catch { model.notice = model.describe(error) }
        load()
    }
}

/// Custom agents and slash commands the current project defines.
struct AgentsPane: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        let workspace = model.session?.workspace
        let agents = workspace.map { Customization.agents(home: DataHome.url, workspace: $0) } ?? []
        let commands = workspace.map { Customization.commands(home: DataHome.url, workspace: $0) } ?? []
        Form {
            Section {
                if agents.isEmpty { Text(workspace == nil ? "Open a project first." : "No custom agents. Add a Markdown file to .mason/agents in the project.").foregroundStyle(.secondary) }
                ForEach(agents, id: \.name) { agent in
                    let current = model.session?.agent?.lowercased() == agent.name.lowercased()
                    LabeledContent {
                        Button(current ? "In Use" : "Use") { model.chooseAgent(agent.name) }.disabled(current || model.running)
                    } label: {
                        Text(agent.name)
                        Text("\(agent.description)  ·  \(agent.source)")
                    }
                }
                if model.session?.agent != nil {
                    LabeledContent {
                        Button("Use Default") { model.chooseAgent("none") }.disabled(model.running)
                    } label: {
                        Text("Default agent")
                        Text("Go back to Mason without extra instructions.")
                    }
                }
            } header: {
                Text("Agents")
            } footer: {
                Text("Standing instructions for a session, as Markdown files in .mason/agents (also .agents/agents and .claude/agents).")
            }
            Section {
                if commands.isEmpty { Text(workspace == nil ? "Open a project first." : "No custom commands. Add a Markdown file to .mason/commands in the project.").foregroundStyle(.secondary) }
                ForEach(commands, id: \.name) { command in
                    LabeledContent("/" + command.name) { Text("\(command.description)  ·  \(command.source)").foregroundStyle(.secondary) }
                }
            } header: {
                Text("Slash commands")
            } footer: {
                Text("Requests you run by name, as Markdown files in .mason/commands (also .claude/commands and .opencode/command). Type the name in the request box.")
            }
        }
        .formStyle(.grouped)
    }
}

/// MCP servers registered for every project, and which of their tools Mason may call.
struct McpPane: View {
    @EnvironmentObject var model: AppModel
    @State private var servers: [McpServer] = []
    @State private var offered: [String: [McpTool]] = [:]
    @State private var asking: String?
    @State private var newName = ""
    @State private var newAddress = ""

    var body: some View {
        Form {
            Section {
                if servers.isEmpty { Text("No MCP servers.").foregroundStyle(.secondary) }
                ForEach(servers, id: \.name) { server in
                    VStack(alignment: .leading, spacing: 6) {
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(server.name).fontWeight(.medium)
                                Text(server.transport).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                                Text(server.tools.isEmpty ? "No tools allowed yet" : "May call: " + server.tools.joined(separator: ", ")).font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button(asking == server.name ? "Asking…" : "Discover Tools") { discover(server) }.disabled(asking != nil)
                            Button("Remove", role: .destructive) { change { $0.removeAll { $0.name == server.name } } }
                            Toggle("Use \(server.name)", isOn: Binding(get: { server.enabled }, set: { on in change { all in for index in all.indices where all[index].name == server.name { all[index].enabled = on } } }))
                                .labelsHidden()
                        }
                        ForEach(offered[server.name] ?? [], id: \.name) { tool in
                            Toggle(isOn: Binding(get: { server.tools.contains(tool.name) }, set: { on in
                                change { all in
                                    for index in all.indices where all[index].name == server.name {
                                        all[index].tools.removeAll { $0 == tool.name }
                                        if on { all[index].tools.append(tool.name) }
                                    }
                                }
                            })) {
                                Text(tool.name)
                                Text(tool.description)
                            }
                            .padding(.leading, 18)
                        }
                    }
                }
                HStack {
                    TextField("Name", text: $newName).frame(width: 110)
                    TextField("http://127.0.0.1:PORT/mcp, or the full path of a program and its arguments", text: $newAddress)
                    Button("Add") { add() }.disabled(newName.trimmingCharacters(in: .whitespaces).isEmpty || newAddress.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            } header: {
                Text("MCP servers")
            } footer: {
                Text("Servers on this Mac whose tools Mason may call. Every call shows its arguments and asks first. Registered for all projects, in Mason's data folder.")
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: load)
    }

    func load() {
        do { servers = try Mcp.all(home: DataHome.url) } catch { model.notice = model.describe(error) }
    }

    /// Applies a change to the saved registrations and shows the result.
    func change(_ edit: (inout [McpServer]) -> Void) {
        do {
            var all = try Mcp.all(home: DataHome.url)
            edit(&all)
            try Mcp.save(all, home: DataHome.url)
        } catch { model.notice = model.describe(error) }
        load()
    }

    func add() {
        let words = newAddress.split(separator: " ").map(String.init)
        guard let first = words.first else { return }
        let name = newName.trimmingCharacters(in: .whitespaces)
        let server = first.hasPrefix("/") ? McpServer(name: name, executable: first, arguments: Array(words.dropFirst())) : McpServer(name: name, endpoint: first)
        do {
            try Mcp.validate(server)
            var all = try Mcp.all(home: DataHome.url)
            all.append(server)
            try Mcp.save(all, home: DataHome.url)
            newName = ""; newAddress = ""
            load()
            discover(server)
        } catch { model.notice = model.describe(error) }
    }

    func discover(_ server: McpServer) {
        asking = server.name
        Task {
            do { offered[server.name] = try await Mcp.discover(server) }
            catch { model.notice = "\(server.name) did not answer: " + model.describe(error) }
            asking = nil
        }
    }
}

/// Requests Mason runs by itself.
struct SchedulesPane: View {
    @EnvironmentObject var model: AppModel
    @State private var schedules: [Schedule] = []
    @State private var newSchedule = ""

    var body: some View {
        Form {
            Section {
                if schedules.isEmpty { Text("No schedules. Add one below; it will work in the current project with the model that is running now.").foregroundStyle(.secondary) }
                ForEach(schedules) { schedule in
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(schedule.name).fontWeight(.medium)
                            Text("\(schedule.summary)  ·  \(schedule.auto ? "Auto" : "read-only")  ·  \(URL(fileURLWithPath: schedule.workspace).lastPathComponent)").font(.caption).foregroundStyle(.secondary)
                            Text(schedule.prompt).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                            if !schedule.lastOutcome.isEmpty { Text("Last run: \(schedule.lastOutcome)").font(.caption).foregroundStyle(.secondary).lineLimit(2) }
                        }
                        Spacer()
                        Button("Run Now") { model.scheduleCommand("run " + schedule.name) }
                        Button("Remove", role: .destructive) { run("remove " + schedule.name) }
                        Toggle("Run \(schedule.name) on schedule", isOn: Binding(get: { schedule.enabled }, set: { run(($0 ? "on " : "off ") + schedule.name) })).labelsHidden()
                    }
                }
                HStack {
                    TextField("NAME daily 09:00 [auto] -- what to do   (also hourly :MM, weekdays HH:MM, weekly DAY HH:MM)", text: $newSchedule)
                        .onSubmit(add)
                    Button("Add", action: add).disabled(newSchedule.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            } header: {
                Text("Schedules")
            } footer: {
                Text("Mason runs these while the app is open, or when mason --run-due is run. A scheduled run is unattended: nothing is approved for it, so it is read-only unless it is marked Auto.")
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: load)
    }

    func load() { schedules = Schedules.load(home: DataHome.url) }

    /// Runs a /schedule command quietly; problems are shown, successes show in the list.
    func run(_ arguments: String) {
        do { _ = try Schedules.command(arguments, home: DataHome.url, workspace: model.session?.workspace ?? NSHomeDirectory(), endpoint: model.endpoint) }
        catch { model.notice = model.describe(error) }
        load()
    }

    func add() {
        let text = newSchedule.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty else { return }
        let before = schedules.count
        run("add " + text)
        if schedules.count > before { newSchedule = "" }
    }
}
