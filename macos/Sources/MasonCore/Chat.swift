import Foundation

/// A conversation with a model that has no tools: it cannot read the project, edit files or run anything.
/// Conversations are saved on this computer, one JSON file each, apart from coding sessions.
public struct ChatConversation: Codable, Identifiable, Equatable {
    public var id = UUID()
    public var title = "New chat"
    public var updated = Date()
    public var messages: [ChatMessage] = []
    /// The model that answered last, for display.
    public var model: String?
    public var promptTokens: Int?
    /// Messages a compaction replaced with a summary, oldest first.
    public var archived: [ChatMessage]?
    public var compactions: Int?
    public init() {}
}

/// A text file added to a chat message. Its content travels inside the message.
public struct ChatAttachment: Equatable {
    public let name: String
    public let content: String
    public static let maxBytes = 256 * 1024
    public static let maxPerMessage = 5

    public init(name: String, content: String) { self.name = name; self.content = content }

    /// Reads a UTF-8 text file. Binary files and files over 256 KiB are refused with a reason.
    public init(file: URL) throws {
        let name = file.lastPathComponent
        guard let size = (try? FileManager.default.attributesOfItem(atPath: file.path))?[.size] as? NSNumber else { throw MasonError("\(name) could not be read.") }
        guard size.intValue <= ChatAttachment.maxBytes else { throw MasonError("\(name) is larger than 256 KiB. Attach a smaller text file, or paste the part you need.") }
        guard let text = String(data: try Data(contentsOf: file), encoding: .utf8), !text.contains("\0") else { throw MasonError("\(name) is not a text file. Chat can attach UTF-8 text only.") }
        self.name = name; self.content = text
    }
}

public final class ChatStore {
    public let folder: URL
    public private(set) var conversations: [ChatConversation] = []
    let encoder: JSONEncoder = { let e = JSONEncoder(); e.outputFormatting = [.prettyPrinted, .sortedKeys]; e.dateEncodingStrategy = .iso8601; return e }()

    public init(home: URL = DataHome.url) throws {
        folder = home.appendingPathComponent("chats")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        for file in try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil) where file.pathExtension == "json" {
            if let data = try? Data(contentsOf: file), let conversation = try? decoder.decode(ChatConversation.self, from: data) { conversations.append(conversation) }
        }
        conversations.sort { $0.updated > $1.updated }
    }

    public func create() throws -> ChatConversation {
        let conversation = ChatConversation()
        try save(conversation)
        return conversation
    }

    public func save(_ conversation: ChatConversation) throws {
        var copy = conversation
        copy.updated = Date()
        try encoder.encode(copy).write(to: folder.appendingPathComponent(copy.id.uuidString + ".json"), options: .atomic)
        if let index = conversations.firstIndex(where: { $0.id == copy.id }) { conversations[index] = copy } else { conversations.insert(copy, at: 0) }
    }

    public func delete(_ id: UUID) {
        try? FileManager.default.removeItem(at: folder.appendingPathComponent(id.uuidString + ".json"))
        conversations.removeAll { $0.id == id }
    }
}

public enum Chat {
    static let systemPrompt = """
        You are Mason, an assistant running on the user's own computer. In this chat you have no tools: you cannot read their files, \
        run commands or browse. Answer from what is in the conversation, say so when you do not know, and format code in fenced blocks.
        """

    /// The text sent for one message: what was typed, followed by each attached file in a fenced block.
    public static func compose(_ text: String, attachments: [ChatAttachment]) -> String {
        var parts = [text.trimmingCharacters(in: .whitespacesAndNewlines)]
        for file in attachments {
            let fence = file.content.contains("```") ? "````" : "```"
            parts.append("Attached file \(file.name):\n\(fence)\n\(file.content)\n\(fence)")
        }
        return parts.filter { !$0.isEmpty }.joined(separator: "\n\n")
    }

    /// Sends one message and appends the answer. The conversation is saved before the model is asked and again after,
    /// so a stopped or failed request keeps the question. A long conversation is compacted first when the model's
    /// context is nearly full.
    public static func send(_ conversation: inout ChatConversation, text: String, attachments: [ChatAttachment] = [], model: ChatModel, store: ChatStore,
                            onText: @escaping (String) -> Void) async throws {
        let typed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !typed.isEmpty || !attachments.isEmpty else { throw MasonError("Type a message first.") }
        guard attachments.count <= ChatAttachment.maxPerMessage else { throw MasonError("Attach at most \(ChatAttachment.maxPerMessage) files to one message.") }
        let content = compose(typed, attachments: attachments)
        guard content.count <= 400_000 else { throw MasonError("That message is too long for a chat.") }
        if conversation.messages.first?.role != "system" { conversation.messages.insert(ChatMessage(role: "system", content: systemPrompt), at: 0) }
        if conversation.title == "New chat" {
            let label = typed.isEmpty ? (attachments.first?.name ?? "New chat") : typed
            conversation.title = label.count > 55 ? String(label.prefix(55)) + "…" : label
        }
        // A request that was stopped leaves a question without an answer; the next message joins it, so turns keep alternating.
        if conversation.messages.last?.role == "user" { conversation.messages[conversation.messages.count - 1].content += "\n\n" + content }
        else { conversation.messages.append(ChatMessage(role: "user", content: content)) }
        conversation.model = model.name
        try store.save(conversation)

        if Compaction.needed(promptTokens: conversation.promptTokens, window: await model.contextWindow()) {
            var holder = Session(workspace: "")
            holder.messages = conversation.messages; holder.archived = conversation.archived
            if try await Compaction.compact(&holder, model: model, keep: 4) {
                conversation.messages = holder.messages; conversation.archived = holder.archived
                conversation.compactions = (conversation.compactions ?? 0) + 1
            }
            conversation.promptTokens = nil
        }
        let reply = try await model.complete(messages: conversation.messages, tools: [], onText: onText)
        try Task.checkCancellation()
        guard !reply.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw MasonError("The model returned an empty answer. Try again, or choose another model.") }
        conversation.messages.append(ChatMessage(role: "assistant", content: reply.text))
        if let tokens = reply.promptTokens { conversation.promptTokens = tokens }
        try store.save(conversation)
    }
}
