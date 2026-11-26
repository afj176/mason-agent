import Foundation
#if canImport(PDFKit)
import PDFKit
#endif

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

/// A file added to a chat message. A text file's content travels inside the message; so does the text taken out of a
/// PDF or a Word document. A picture travels beside the text, for a model that can see.
public struct ChatAttachment: Equatable {
    public let name: String
    public let content: String
    /// For a picture: its bytes as a data: address.
    public let image: String?
    public static let maxBytes = 256 * 1024
    public static let maxPerMessage = 5
    public static let maxImageBytes = 4 * 1024 * 1024
    public static let maxDocumentBytes = 20 * 1024 * 1024
    public static let maxExtracted = 200_000
    static let imageTypes = ["png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg", "gif": "image/gif", "webp": "image/webp"]

    public init(name: String, content: String, image: String? = nil) { self.name = name; self.content = content; self.image = image }

    /// Reads a file to attach. Text must be UTF-8 and at most 256 KiB; a picture at most 4 MiB; a PDF or Word document
    /// at most 20 MiB, and its text is what is attached. Anything else is refused with a reason.
    public init(file: URL) throws {
        let name = file.lastPathComponent, kind = file.pathExtension.lowercased()
        guard let size = (try? FileManager.default.attributesOfItem(atPath: file.path))?[.size] as? NSNumber else { throw MasonError("\(name) could not be read.") }
        if let type = ChatAttachment.imageTypes[kind] {
            guard size.intValue <= ChatAttachment.maxImageBytes else { throw MasonError("\(name) is larger than 4 MiB. Attach a smaller picture.") }
            self.name = name; self.content = ""; self.image = "data:\(type);base64," + (try Data(contentsOf: file)).base64EncodedString()
            return
        }
        if kind == "pdf" || kind == "docx" {
            guard size.intValue <= ChatAttachment.maxDocumentBytes else { throw MasonError("\(name) is larger than 20 MiB.") }
            let text = (kind == "pdf" ? try ChatAttachment.pdfText(file) : ChatAttachment.wordText(xml: try ChatAttachment.output(of: ["unzip", "-p", file.path, "word/document.xml"], missing: "Reading a Word document needs the unzip program.")))
                .trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { throw MasonError("No text could be taken from \(name). A scanned document has none; attach a picture of the page for a model that can see.") }
            self.name = name; self.image = nil
            self.content = text.count > ChatAttachment.maxExtracted ? String(text.prefix(ChatAttachment.maxExtracted)) + "\n[The rest of the document was left out.]" : text
            return
        }
        guard size.intValue <= ChatAttachment.maxBytes else { throw MasonError("\(name) is larger than 256 KiB. Attach a smaller text file, or paste the part you need.") }
        guard let text = String(data: try Data(contentsOf: file), encoding: .utf8), !text.contains("\0") else { throw MasonError("\(name) is not text, a picture (PNG, JPEG, GIF, WebP), a PDF or a Word document (.docx).") }
        self.name = name; self.content = text; self.image = nil
    }

    /// What a program prints. The program is found on the PATH.
    static func output(of command: [String], missing: String) throws -> String {
        let process = Process(), pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        process.arguments = command
        process.standardOutput = pipe; process.standardError = FileHandle.nullDevice; process.standardInput = FileHandle.nullDevice
        do { try process.run() } catch { throw MasonError(missing) }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        guard !data.isEmpty else { throw MasonError(missing + " (Or the file is not what its name says.)") }
        return String(decoding: data, as: UTF8.self)
    }

    /// The text of a Word document, from its main part: paragraphs as lines, tabs kept.
    public static func wordText(xml: String) -> String {
        var text = xml.replacingOccurrences(of: "</w:p>", with: "\n").replacingOccurrences(of: "<w:tab/>", with: "\t").replacingOccurrences(of: "<w:br/>", with: "\n")
        text = text.replacingOccurrences(of: "<[^>]+>", with: "", options: .regularExpression)
        for (entity, character) in [("&lt;", "<"), ("&gt;", ">"), ("&quot;", "\""), ("&apos;", "'"), ("&amp;", "&")] { text = text.replacingOccurrences(of: entity, with: character) }
        return text
    }

    static func pdfText(_ file: URL) throws -> String {
        #if canImport(PDFKit)
        if let document = PDFDocument(url: file), let text = document.string { return text }
        throw MasonError("\(file.lastPathComponent) could not be read as a PDF.")
        #else
        return try output(of: ["pdftotext", "-layout", file.path, "-"], missing: "Reading a PDF needs the pdftotext program (poppler-utils).")
        #endif
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
            if file.image != nil { parts.append("Attached picture: \(file.name)"); continue }
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
        let pictures = attachments.compactMap(\.image)
        if !pictures.isEmpty { conversation.messages[conversation.messages.count - 1].images = (conversation.messages[conversation.messages.count - 1].images ?? []) + pictures }
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

/// A table from an answer, as cells to lay out in a grid and, where a column is all numbers, as a series to chart.
public struct TableData: Equatable {
    public struct Series: Equatable {
        public let name: String
        public let values: [Double]
    }
    public let headers: [String]
    public let rows: [[String]]
    /// The numeric columns, each against the first column's labels.
    public let series: [Series]
    public var labels: [String] { rows.map { $0.first ?? "" } }
}

public enum Tables {
    static func cells(_ line: String) -> [String]? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        guard trimmed.hasPrefix("|"), trimmed.hasSuffix("|"), trimmed.count >= 2 else { return nil }
        return trimmed.dropFirst().dropLast().split(separator: "|", omittingEmptySubsequences: false).map { $0.trimmingCharacters(in: .whitespaces) }
    }

    /// A cell as a number: digits with an optional sign, thousands separators, a currency sign before or a percent sign after.
    public static func number(_ cell: String) -> Double? {
        var text = cell.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: ",", with: "")
        if let first = text.first, "$€£".contains(first) { text.removeFirst() }
        if text.hasSuffix("%") { text.removeLast() }
        guard !text.isEmpty, text.range(of: #"^[-+]?(\d+\.?\d*|\.\d+)$"#, options: .regularExpression) != nil else { return nil }
        return Double(text)
    }

    /// The tables written in Markdown in `text`: a header row, a row of dashes, then 1 to 40 rows of the same width.
    public static func find(in text: String) -> [TableData] { scan(text).map(\.table) }

    /// `text` without the lines of its tables, for showing beside the tables laid out as grids.
    public static func prose(_ text: String) -> String {
        let ranges = scan(text).map { $0.first...$0.last }
        return text.components(separatedBy: "\n").enumerated().filter { line in !ranges.contains { $0.contains(line.offset) } }.map(\.element).joined(separator: "\n")
    }

    /// Each table with the lines it occupies.
    static func scan(_ text: String) -> [(table: TableData, first: Int, last: Int)] {
        let lines = text.components(separatedBy: "\n")
        var found: [(table: TableData, first: Int, last: Int)] = [], index = 0, fenced = false
        while index < lines.count {
            if lines[index].trimmingCharacters(in: .whitespaces).hasPrefix("```") { fenced.toggle(); index += 1; continue }
            guard !fenced, index + 2 < lines.count, let headers = cells(lines[index]), headers.count >= 2, let rule = cells(lines[index + 1]), rule.count == headers.count,
                  rule.allSatisfy({ !$0.isEmpty && $0.allSatisfy { "-: ".contains($0) } && $0.contains("-") }) else { index += 1; continue }
            var rows: [[String]] = []
            var next = index + 2
            while next < lines.count, let row = cells(lines[next]), row.count == headers.count, rows.count < 40 { rows.append(row); next += 1 }
            if !rows.isEmpty {
                var series: [TableData.Series] = []
                for column in 1..<headers.count {
                    let values = rows.compactMap { number($0[column]) }
                    if values.count == rows.count { series.append(TableData.Series(name: headers[column], values: values)) }
                }
                found.append((TableData(headers: headers, rows: rows, series: Array(series.prefix(4))), index, next - 1))
            }
            index = next
        }
        return Array(found.prefix(6))
    }
}
