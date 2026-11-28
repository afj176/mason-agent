import Foundation

/// Replaces the older part of a conversation with a summary the model writes, so a long session keeps fitting in the
/// model's context. The messages it replaces are kept in the session's archive; nothing is thrown away.
public enum Compaction {
    /// Compaction starts by itself when the last request used this share of the model's context.
    public static let threshold = 0.75
    static let transcriptLimit = 48_000

    public static func needed(_ session: Session, window: Int?) -> Bool { needed(promptTokens: session.promptTokens, window: window) }

    public static func needed(promptTokens: Int?, window: Int?) -> Bool {
        guard let window, window > 0, let used = promptTokens else { return false }
        return Double(used) >= Double(window) * threshold
    }

    /// What a coding run stops with when its context is full and automatic compaction is off (the same words as Windows).
    public static let fullMessage = "Coding context is full and automatic compaction is off. Originals are retained; compact manually or load a larger context."
    /// What a chat stops with in the same case. Chat has no manual compaction.
    public static let chatFullMessage = "Chat context is full and automatic compaction is off. Originals are retained; start a new chat or load a larger context."

    /// True when the last request left no room to work: under 5% of the context (and at least 128 tokens) is free.
    public static func full(promptTokens: Int?, window: Int?) -> Bool {
        guard let window, window > 0, let used = promptTokens else { return false }
        return window - used < max(128, window / 20)
    }

    /// The words /compact auto prints, shared by mason and its tests. `on` is the setting after the command.
    public static func autoText(_ argument: String, current: Bool, chat: Bool = false) throws -> (on: Bool, text: String) {
        switch argument.lowercased() {
        case "": return (current, "Automatic compaction is \(current ? "on" : "off") · /compact auto on | off")
        case "on": return (true, "Automatic compaction on: a long conversation is summarized when the context fills up.")
        case "off": return (false, chat ? "Automatic compaction off: the full history is sent until you start a new chat." : "Automatic compaction off: the full history is sent until you /compact.")
        default: throw MasonError("Use /compact auto on or /compact auto off.")
        }
    }

    /// Where the kept tail begins when `keep` recent messages stay as they are. A tool result is never separated
    /// from the assistant message that asked for it.
    static func cut(_ messages: [ChatMessage], keep: Int) -> Int {
        var start = max(1, messages.count - keep)
        while start > 1, start < messages.count, messages[start].role == "tool" { start -= 1 }
        return start
    }

    static func transcript(_ messages: ArraySlice<ChatMessage>) -> String {
        var lines: [String] = []
        for message in messages {
            var text = message.content.count > 2000 ? String(message.content.prefix(1400)) + "\n[…]\n" + String(message.content.suffix(500)) : message.content
            for call in message.toolCalls ?? [] { text += (text.isEmpty ? "" : "\n") + "calls \(call.name) \(call.arguments.prefix(600))" }
            lines.append("\(message.role.uppercased()): \(text)")
        }
        let joined = lines.joined(separator: "\n\n")
        guard joined.count > transcriptLimit else { return joined }
        return String(joined.prefix(transcriptLimit / 3)) + "\n\n[… the middle of the conversation is omitted …]\n\n" + String(joined.suffix(transcriptLimit * 2 / 3))
    }

    /// Summarises everything before the most recent `keep` messages. Returns false when there was nothing to compact.
    @discardableResult
    public static func compact(_ session: inout Session, model: ChatModel, keep: Int) async throws -> Bool {
        guard session.messages.count > 1, session.messages[0].role == "system" else { return false }
        let start = cut(session.messages, keep: keep)
        let older = session.messages[1..<start]
        guard older.count >= 2 else { return false }
        let ask = [
            ChatMessage(role: "system", content: "You condense a coding session so the same agent can continue it with less text. Reply with the summary only."),
            ChatMessage(role: "user", content: """
                Summarise the session below for the agent that will continue it. Keep: what the user asked for and any constraints they set; \
                decisions made; each file read or changed and its current state; commands run and what they showed; what is still to do. \
                Use exact paths, names and values. Do not invent anything. At most 400 words.

                \(transcript(older))
                """),
        ]
        let reply = try await model.complete(messages: ask, tools: []) { _ in }
        let summary = reply.text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !summary.isEmpty else { throw MasonError("The model returned no summary, so the conversation was left as it is.") }
        let block = "Summary of the earlier conversation (the original messages are archived):\n\(summary)"
        session.archived = (session.archived ?? []) + Array(older)
        var tail = Array(session.messages[start...])
        if tail.isEmpty {
            // Nothing follows, so the summary travels with the next request, like an undo note.
            session.pendingNote = (session.pendingNote.map { $0 + "\n\n" } ?? "") + block
        } else if tail[0].role == "user" {
            tail[0].content = block + "\n\n" + tail[0].content
        } else {
            tail.insert(ChatMessage(role: "user", content: block), at: 0)
        }
        session.messages = [session.messages[0]] + tail
        session.promptTokens = nil
        session.compactions = (session.compactions ?? 0) + 1
        return true
    }
}
