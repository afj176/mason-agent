import SwiftUI
import Charts
import MasonCore

/// The chats in the sidebar while the Chat pane is showing.
struct ChatSidebar: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        List(selection: $model.chatSelection) {
            Section("Chats") {
                ForEach(model.chats) { chat in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(chat.title).lineLimit(1)
                        Text(chat.model ?? "No answer yet").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    .tag(chat.id)
                    .contextMenu {
                        Button("Delete Chat", role: .destructive) { model.deleteChat(chat) }
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .scrollContentBackground(.hidden)
        .background(Brand.sidebar)
        .safeAreaInset(edge: .bottom) {
            Button { model.newChat() } label: { Label("New Chat", systemImage: "square.and.pencil").frame(maxWidth: .infinity, alignment: .leading) }
                .buttonStyle(.borderless)
                .padding(10)
        }
    }
}

/// A conversation with the running model. Chat has no tools: it cannot read the project or change anything.
struct ChatView: View {
    @EnvironmentObject var model: AppModel
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 0) {
            if let chat = model.chat {
                ScrollViewReader { proxy in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 16) {
                            ForEach(Array(chat.messages.enumerated()), id: \.offset) { _, message in
                                if message.role == "user" {
                                    Speaker(mason: false)
                                    Text(ChatView.shown(message.content)).textSelection(.enabled).messageBody()
                                } else if message.role == "assistant", let calls = message.toolCalls, !calls.isEmpty {
                                    // A round of tools shows as compact steps: the host, query or tool, and how it ended.
                                    if !message.content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                                        MarkdownText(text: message.content).textSelection(.enabled).messageBody()
                                    }
                                    ForEach(calls, id: \.id) { call in
                                        if let step = chat.steps?.first(where: { $0.id == call.id }) { ChatStepRow(step: step).messageBody() }
                                    }
                                } else if message.role == "assistant" {
                                    Speaker(mason: true)
                                    MarkdownText(text: Tables.prose(message.content)).textSelection(.enabled).messageBody()
                                    // A table in the answer is laid out as a grid, with a chart of each numeric column.
                                    ForEach(Array(Tables.find(in: message.content).enumerated()), id: \.offset) { _, table in
                                        TableCard(table: table).messageBody()
                                    }
                                }
                            }
                            if model.chatRunning {
                                ForEach(Array(model.chatSteps.enumerated()), id: \.offset) { _, step in
                                    ChatStepRow(step: step).messageBody()
                                }
                                HStack(spacing: 8) {
                                    PoseImage(pose: .planning, height: 34, meaning: "working on the answer")
                                    if model.chatLive.isEmpty { ProgressView().controlSize(.small); Text("Thinking").foregroundStyle(.secondary) }
                                }
                                if !model.chatLive.isEmpty {
                                    MarkdownText(text: model.chatLive)
                                }
                            }
                            Color.clear.frame(height: 1).id("end")
                        }
                        .padding(20)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .onChange(of: chat.messages.count) { _, _ in proxy.scrollTo("end") }
                    .onChange(of: model.chatLive) { _, _ in proxy.scrollTo("end") }
                    .onChange(of: model.chatSteps.count) { _, _ in proxy.scrollTo("end") }
                }
                if chat.messages.count <= 1, !model.chatRunning {
                    PoseImage(pose: .listening, height: 104)
                    Text("Ask anything. With Tools on, Chat can search and read the web, read your skills and use your MCP servers, asking before each new host or call. It cannot read your project, edit files or run commands. Use Code for that.")
                        .foregroundStyle(.secondary).multilineTextAlignment(.center).padding(.horizontal, 60).padding(.bottom, 12)
                }
                if let pending = model.chatApproval { ChatApprovalCard(pending: pending) }
            } else {
                ContentUnavailableView {
                    Label("No chat selected", systemImage: "bubble.left.and.bubble.right")
                } description: {
                    Text("Talk a problem through with the running model. Chats are saved on this Mac.")
                } actions: {
                    Button("New Chat") { model.newChat() }.keyboardShortcut(.defaultAction)
                }
                .frame(maxHeight: .infinity)
            }
            if model.chat != nil { composer }
        }
    }

    /// A message with attached files shows the file names, not their whole content.
    static func shown(_ content: String) -> String {
        var parts: [String] = []
        for piece in content.components(separatedBy: "\n\nAttached file ") {
            if parts.isEmpty, !piece.hasPrefix("Attached file ") { parts.append(piece); continue }
            let name = piece.replacingOccurrences(of: "Attached file ", with: "").prefix { $0 != ":" }
            parts.append("📎 " + name)
        }
        return parts.joined(separator: "\n")
    }

    var composer: some View {
        VStack(alignment: .leading, spacing: 8) {
            if !model.chatAttachments.isEmpty {
                HStack(spacing: 8) {
                    ForEach(model.chatAttachments, id: \.name) { file in
                        HStack(spacing: 4) {
                            Image(systemName: "paperclip")
                            Text(file.name).lineLimit(1)
                            Button { model.chatAttachments.removeAll { $0.name == file.name } } label: { Image(systemName: "xmark.circle.fill") }
                                .buttonStyle(.borderless).accessibilityLabel("Remove \(file.name)")
                        }
                        .font(.caption).padding(.horizontal, 8).padding(.vertical, 4).background(.quaternary, in: Capsule())
                    }
                }
            }
            TextEditor(text: $model.chatDraft)
                .font(.body)
                .focused($focused)
                .frame(height: 62)
                .scrollContentBackground(.hidden)
                .overlay(alignment: .topLeading) {
                    if model.chatDraft.isEmpty {
                        Text("Message the model…").foregroundStyle(.tertiary).padding(.horizontal, 5).allowsHitTesting(false)
                    }
                }
                .accessibilityLabel("Message")
            HStack {
                Button { model.attachToChat() } label: { Label("Attach", systemImage: "paperclip") }
                    .disabled(model.chatRunning || model.chatAttachments.count >= ChatAttachment.maxPerMessage)
                    .help("Attach text files, pictures, PDFs or Word documents to this message (up to 5)")
                if let chat = model.chat {
                    ChatOptionsButton(chat: chat).disabled(model.chatRunning)
                    ChatToolsMenu(chat: chat).disabled(model.chatRunning || !model.chatCanCallTools)
                    if !model.chatCanCallTools { Text("This model can't call tools.").font(.caption).foregroundStyle(.secondary) }
                }
                Text(model.endpoint == nil ? "No model is running" : model.endpointName).font(.caption).foregroundStyle(.secondary)
                Spacer()
                if model.chatRunning {
                    Button(role: .cancel) { model.stopChat() } label: { Label("Stop", systemImage: "stop.fill") }
                } else {
                    Button { model.sendChat() } label: {
                        Image(systemName: "arrow.up").font(.system(size: 15, weight: .bold)).foregroundStyle(Brand.onAccent)
                            .frame(width: 34, height: 34).background(Brand.accent, in: Circle())
                    }
                    .buttonStyle(.plain)
                    .disabled(model.chatDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && model.chatAttachments.isEmpty)
                    .accessibilityLabel("Send")
                    .help("Send (⌘Return)")
                }
            }
        }
        .padding(12)
        .card(radius: 18)
        .shadow(color: .black.opacity(0.14), radius: 14, y: 6)
        .frame(maxWidth: 900)
        .frame(maxWidth: .infinity)
        .padding(.horizontal, 16).padding(.bottom, 14).padding(.top, 6)
        // The message box takes the keyboard whenever a chat is shown. The short wait lets the pane finish appearing;
        // asking for focus in the same pass as the switch is ignored.
        .task(id: model.chatSelection) {
            try? await Task.sleep(nanoseconds: 200_000_000)
            focused = true
        }
    }
}

/// A table from an answer: its cells in a grid, and its numeric columns as bars against the first column.
struct TableCard: View {
    let table: TableData

    struct Point: Identifiable {
        let id: Int
        let label: String
        let series: String
        let value: Double
    }

    var points: [Point] {
        var made: [Point] = []
        for series in table.series {
            for (index, value) in series.values.enumerated() { made.append(Point(id: made.count, label: table.labels[index], series: series.name, value: value)) }
        }
        return made
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            ScrollView(.horizontal) {
                Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 6) {
                    GridRow { ForEach(Array(table.headers.enumerated()), id: \.offset) { _, header in Text(header).font(.callout.weight(.semibold)) } }
                    Divider()
                    ForEach(Array(table.rows.enumerated()), id: \.offset) { _, row in
                        GridRow { ForEach(Array(row.enumerated()), id: \.offset) { _, cell in Text(cell).font(.callout).textSelection(.enabled) } }
                    }
                }
            }
            // Each numeric column has its own chart, so columns on different scales can both be read.
            ForEach(Array(table.series.enumerated()), id: \.offset) { _, series in
                Text(series.name).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                Chart(points.filter { $0.series == series.name }) { point in
                    BarMark(x: .value(table.headers.first ?? "", point.label), y: .value(series.name, point.value))
                }
                .frame(height: 150)
                .accessibilityLabel("Chart of " + series.name)
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.secondary.opacity(0.08)))
    }
}

/// One tool call in a chat, as a compact step: what it did, what it was about, and how it ended.
struct ChatStepRow: View {
    let step: ChatStep

    var symbol: String {
        switch step.tool {
        case "web_search": return "magnifyingglass"
        case "web_fetch": return "globe"
        case "read_skill": return "book"
        default: return "server.rack"
        }
    }

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: symbol).foregroundStyle(.secondary).frame(width: 16)
            Text(Activity.label(step.tool)).font(.callout.weight(.medium))
            Text(step.subject).font(.callout).lineLimit(1).truncationMode(.middle)
            Text(step.outcome).font(.caption)
                .foregroundStyle(step.state == .completed ? Color.secondary : step.state == .denied ? Brand.caution : Brand.critical)
                .lineLimit(1)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 10).padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.secondary.opacity(0.08)))
        .accessibilityElement(children: .combine)
    }
}

/// A question from a chat's tools: read a host, or call an MCP tool. Once, Always or No.
struct ChatApprovalCard: View {
    @EnvironmentObject var model: AppModel
    let pending: ChatPendingApproval

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Label(pending.request.title, systemImage: "hand.raised.fill").font(.headline)
            if !pending.request.detail.isEmpty {
                ScrollView {
                    Text(pending.request.detail).font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary)
                        .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 120)
            }
            HStack {
                Button("Once") { model.answerChat(pending, .once) }.keyboardShortcut(.defaultAction)
                Button(pending.request.tool == "network" ? "Always, in every chat" : "Always") { model.answerChat(pending, .always) }
                Spacer()
                Button("No", role: .cancel) { model.answerChat(pending, .no) }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(14)
        .background(Brand.caution.opacity(0.12), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Brand.caution.opacity(0.5)))
        .frame(maxWidth: 900)
        .frame(maxWidth: .infinity)
        .padding(.horizontal, 16).padding(.top, 8)
    }
}

/// Which tools this chat may use: the web, skills and MCP servers. The choice is saved with the chat.
struct ChatToolsMenu: View {
    @EnvironmentObject var model: AppModel
    let chat: ChatConversation

    var body: some View {
        Menu {
            Toggle("Web", isOn: Binding(get: { chat.switches.web }, set: { model.setChatTools("web", $0) }))
            Toggle("Skills", isOn: Binding(get: { chat.switches.skills }, set: { model.setChatTools("skills", $0) }))
            Toggle("MCP Servers", isOn: Binding(get: { chat.switches.mcp }, set: { model.setChatTools("mcp", $0) }))
        } label: {
            Label("Tools", systemImage: "wrench.and.screwdriver")
        }
        .fixedSize()
        .help(model.chatCanCallTools ? "What this chat may use: the web, your skills and your MCP servers. Each new host and every MCP call asks first." : "This model can't call tools.")
    }
}

/// This chat's reasoning level, answer length and pinned reference, in a popover.
struct ChatOptionsButton: View {
    @EnvironmentObject var model: AppModel
    let chat: ChatConversation
    @State private var shown = false
    @State private var reasoning = "default"
    @State private var limit = ""
    @State private var reference = ""

    var isSet: Bool { chat.reasoning != nil || chat.maxTokens != nil || chat.reference != nil }

    var body: some View {
        Button { load(); shown = true } label: { Label(isSet ? "Options •" : "Options", systemImage: "slider.horizontal.3") }
            .help("This chat's reasoning level, answer length and pinned reference")
            .popover(isPresented: $shown) {
                VStack(alignment: .leading, spacing: 10) {
                    Picker("Reasoning", selection: $reasoning) {
                        Text("Server's default").tag("default")
                        Text("Off").tag("off")
                        Text("Low").tag("low")
                        Text("Medium").tag("medium")
                        Text("High").tag("high")
                    }
                    TextField("Most tokens in one answer (left to the server)", text: $limit).textFieldStyle(.roundedBorder)
                    Text("Reference: text the model is given with every message").font(.caption).foregroundStyle(.secondary)
                    TextEditor(text: $reference).font(.callout).frame(height: 150)
                        .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.secondary.opacity(0.3)))
                        .accessibilityLabel("Reference")
                    HStack {
                        Spacer()
                        Button("Apply") {
                            let typed = limit.trimmingCharacters(in: .whitespaces)
                            guard typed.isEmpty || Int(typed) != nil else { model.notice = "An answer's limit is a number of tokens, or empty."; return }
                            model.configureChat(reasoning: reasoning, maxTokens: Int(typed) ?? 0, reference: reference)
                            shown = false
                        }
                        .keyboardShortcut(.defaultAction)
                    }
                }
                .padding(14)
                .frame(width: 380)
            }
    }

    func load() {
        reasoning = chat.reasoning ?? "default"
        limit = chat.maxTokens.map(String.init) ?? ""
        reference = chat.reference ?? ""
    }
}
