import SwiftUI
import MasonCore

@main
struct MasonAgentApp: App {
    @StateObject private var model = AppModel()
    @AppStorage("appearance") private var appearance = AppearanceChoice.system.rawValue

    var body: some Scene {
        WindowGroup("Mason Agent") {
            RootView()
                .environmentObject(model)
                .frame(minWidth: 860, minHeight: 540)
                .tint(Brand.accent)
                .onAppear { NSApp.appearance = (AppearanceChoice(rawValue: appearance) ?? .system).appearance }
                .onChange(of: appearance) { _, choice in NSApp.appearance = (AppearanceChoice(rawValue: choice) ?? .system).appearance }
        }
        .defaultSize(width: 1180, height: 760)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("New Session") { model.pane = .code; model.newSession() }.keyboardShortcut("n")
                Button("New Chat") { model.newChat() }.keyboardShortcut("n", modifiers: [.command, .shift])
                Button("Open Project…") { model.openFolder() }.keyboardShortcut("o")
            }
            CommandGroup(after: .undoRedo) {
                Divider()
                Button("Undo Last Request's Edits") { model.undo() }.keyboardShortcut("z", modifiers: [.command, .option]).disabled(model.running)
                Button("Redo Last Request's Edits") { model.undo(redo: true) }.keyboardShortcut("z", modifiers: [.command, .option, .shift]).disabled(model.running)
                Button("Compact Conversation") { model.compact() }.disabled(model.running)
            }
            CommandGroup(after: .sidebar) {
                Button("Code") { model.pane = .code }.keyboardShortcut("1")
                Button("Chat") { model.pane = .chat }.keyboardShortcut("2")
                Button("Models") { model.pane = .models }.keyboardShortcut("3")
                Divider()
            }
            CommandMenu("Agent") {
                Button("Send") { model.sendCurrent() }.keyboardShortcut(.return, modifiers: .command).disabled(model.pane == .chat && model.chatRunning)
                Button("Steer") { model.followUp(steer: true) }.keyboardShortcut(.return, modifiers: [.command, .shift]).disabled(model.pane != .code || !model.running)
                Button("Stop") { model.stopCurrent() }.keyboardShortcut(".", modifiers: .command).disabled(model.pane == .chat ? !model.chatRunning : !model.running)
                Divider()
                Picker("Permission", selection: Binding(get: { model.session?.permission ?? .reviewEdits }, set: { model.setPermission($0) })) {
                    Text("Read-Only").tag(Permission.readOnly)
                    Text("Review Edits").tag(Permission.reviewEdits)
                    Text("Workspace Edits").tag(Permission.workspaceEdits)
                    Text("Auto (Sandboxed)").tag(Permission.auto)
                }
                Divider()
                Button("Install Command Line Tool…") { model.installCommandLineTool() }
            }
        }

        Settings {
            SettingsView().environmentObject(model)
        }
    }
}

enum Pane: String, CaseIterable, Identifiable {
    case code = "Code", chat = "Chat", models = "Models"
    var id: String { rawValue }
}

struct RootView: View {
    @EnvironmentObject var model: AppModel
    var body: some View {
        NavigationSplitView {
            Group {
                if model.pane == .chat { ChatSidebar() } else { Sidebar() }
            }
            .navigationSplitViewColumnWidth(min: 200, ideal: 250, max: 340)
        } detail: {
            VStack(spacing: 0) {
                Group {
                    switch model.pane {
                    case .code: SessionView(showChanges: $model.showChanges)
                    case .chat: ChatView()
                    case .models: ModelsView()
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                StatusBar()
            }
            .background(Brand.background)
            .toolbar {
                ToolbarItem(placement: .principal) {
                    Picker("View", selection: $model.pane) {
                        ForEach(Pane.allCases) { Text($0.rawValue).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .help("Switch between coding sessions, chat and the models on this Mac")
                }
                ToolbarItemGroup(placement: .primaryAction) {
                    if model.pane == .code {
                        Toggle(isOn: $model.showChanges) { Label("Changes", systemImage: "plusminus") }
                            .help("Show the files this session changed")
                    }
                    Button { model.pane = .models } label: {
                        Label(model.endpoint == nil ? "No model" : model.endpointName, systemImage: model.endpoint == nil ? "exclamationmark.triangle" : "cpu")
                            .labelStyle(.titleAndIcon)
                    }
                    .help(model.endpoint == nil ? "No model is running. Open Models to start one." : "Running at \(model.endpoint?.url ?? "")")
                }
            }
        }
        .alert("Mason Agent", isPresented: Binding(get: { model.notice != nil }, set: { if !$0 { model.notice = nil } })) {
            Button("OK", role: .cancel) { model.notice = nil }
        } message: {
            Text(model.notice ?? "")
        }
    }
}

struct Sidebar: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        List(selection: $model.selection) {
            Section {
                HStack(spacing: 8) {
                    Mascot(size: 26)
                    Text("Mason Agent").font(.headline).fontWeight(.heavy)
                }
                .padding(.vertical, 4)
                .listRowSeparator(.hidden)
                .selectionDisabled()
            }
            ForEach(model.projects, id: \.self) { project in
                Section {
                    ForEach(model.sessions(in: project)) { session in
                        Label {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(session.title).lineLimit(1)
                                Text(session.state).font(.caption).foregroundStyle(.secondary)
                            }
                        } icon: {
                            Image(systemName: session.state == "Running" || session.state == "Awaiting review" ? "ellipsis.bubble" : "bubble.left")
                        }
                        .tag(session.id)
                        .contextMenu {
                            Button("Export Session…") { model.exportSession(session, sanitize: false) }
                            Button("Export a Sanitized Copy…") { model.exportSession(session, sanitize: true) }
                            Button("Import a Session Into This Project…") { model.importSession(into: project) }
                            Divider()
                            Button("Delete Session", role: .destructive) { model.delete(session) }
                        }
                    }
                } header: {
                    HStack {
                        Label(URL(fileURLWithPath: project).lastPathComponent, systemImage: "folder")
                            .help(project)
                        Spacer()
                        Button { model.newSession(in: project) } label: { Image(systemName: "plus") }
                            .buttonStyle(.borderless)
                            .help("New session in \(URL(fileURLWithPath: project).lastPathComponent)")
                            .accessibilityLabel("New session in \(URL(fileURLWithPath: project).lastPathComponent)")
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .scrollContentBackground(.hidden)
        .background(Brand.sidebar)
        .overlay {
            if model.sessions.isEmpty {
                ContentUnavailableView {
                    Label("No projects yet", systemImage: "folder.badge.plus")
                } description: {
                    Text("Open a project folder to start a session.")
                } actions: {
                    Button("Open Project…") { model.openFolder() }
                }
            }
        }
        .safeAreaInset(edge: .bottom) {
            Button { model.openFolder() } label: { Label("Open Project…", systemImage: "folder.badge.plus").frame(maxWidth: .infinity, alignment: .leading) }
                .buttonStyle(.borderless)
                .padding(10)
        }
    }
}

/// Settings: how Mason looks and where it keeps things, then what it may do and use.
struct SettingsView: View {
    var body: some View {
        TabView {
            GeneralPane().tabItem { Label("General", systemImage: "gearshape") }
            ApprovalsPane().tabItem { Label("Approvals", systemImage: "hand.raised") }
            AgentsPane().tabItem { Label("Agents", systemImage: "person.crop.rectangle") }
            McpPane().tabItem { Label("MCP Servers", systemImage: "server.rack") }
            SchedulesPane().tabItem { Label("Schedules", systemImage: "clock") }
        }
        .frame(width: 680, height: 460)
    }
}

struct GeneralPane: View {
    @EnvironmentObject var model: AppModel
    @AppStorage("appearance") private var appearance = AppearanceChoice.system.rawValue

    var body: some View {
        Form {
            Section("Appearance") {
                Picker("Theme", selection: $appearance) {
                    ForEach(AppearanceChoice.allCases) { Text($0.rawValue).tag($0.rawValue) }
                }
                .pickerStyle(.segmented)
            }
            CodeSearchSection()
            Section("Command line") {
                LabeledContent("mason") {
                    Button("Install Command Line Tool…") { model.installCommandLineTool() }
                }
                Text("Adds the mason command to /usr/local/bin so it works in any Terminal window. macOS asks for an administrator password.")
                    .font(.callout).foregroundStyle(.secondary)
            }
            Section("Storage") {
                LabeledContent("Sessions and settings", value: DataHome.url.path)
                LabeledContent("Downloaded models", value: ModelLibrary.downloadFolder.path)
                Button("Show in Finder") { NSWorkspace.shared.open(DataHome.url) }
            }
            Section("Privacy") {
                Text("Everything stays on this Mac. Mason talks to models on localhost only, and reaches the internet only when you search or download a model or install llama.cpp.")
                    .font(.callout).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}
