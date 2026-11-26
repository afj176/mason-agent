import SwiftUI
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
                                    HStack {
                                        Spacer(minLength: 80)
                                        Text(ChatView.shown(message.content))
                                            .textSelection(.enabled)
                                            .padding(.horizontal, 14).padding(.vertical, 9)
                                            .background(Color.accentColor.opacity(0.16), in: RoundedRectangle(cornerRadius: 14))
                                    }
                                } else if message.role == "assistant" {
                                    MarkdownText(text: message.content).textSelection(.enabled)
                                }
                            }
                            if model.chatRunning {
                                if model.chatLive.isEmpty {
                                    HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Thinking").foregroundStyle(.secondary) }
                                } else {
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
                }
                if chat.messages.count <= 1, !model.chatRunning {
                    Text("Ask anything. Chat answers from the conversation alone: it cannot read your project, edit files or run commands. Use Code for that.")
                        .foregroundStyle(.secondary).multilineTextAlignment(.center).padding(.horizontal, 60).padding(.bottom, 12)
                }
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
                .frame(minHeight: 44, maxHeight: 140)
                .scrollContentBackground(.hidden)
                .padding(8)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
                .overlay(alignment: .topLeading) {
                    if model.chatDraft.isEmpty {
                        Text("Message the model").foregroundStyle(.tertiary).padding(.horizontal, 13).padding(.vertical, 8).allowsHitTesting(false)
                    }
                }
                .accessibilityLabel("Message")
            HStack {
                Button { model.attachToChat() } label: { Label("Attach", systemImage: "paperclip") }
                    .disabled(model.chatRunning || model.chatAttachments.count >= ChatAttachment.maxPerMessage)
                    .help("Attach text files to this message (up to 5, 256 KiB each)")
                Text(model.endpoint == nil ? "No model is running" : model.endpointName).font(.caption).foregroundStyle(.secondary)
                Spacer()
                if model.chatRunning {
                    Button(role: .cancel) { model.stopChat() } label: { Label("Stop", systemImage: "stop.fill") }
                } else {
                    Button { model.sendChat() } label: { Label("Send", systemImage: "arrow.up.circle.fill") }
                        .buttonStyle(.borderedProminent)
                        .disabled(model.chatDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && model.chatAttachments.isEmpty)
                        .help("Send (⌘Return)")
                }
            }
        }
        .padding(16)
        // The message box takes the keyboard whenever a chat is shown. The short wait lets the pane finish appearing;
        // asking for focus in the same pass as the switch is ignored.
        .task(id: model.chatSelection) {
            try? await Task.sleep(nanoseconds: 200_000_000)
            focused = true
        }
    }
}
