import Foundation

// Notes the user keeps for a project: short facts worth telling the model every time ("the API is at /v2",
// "never edit the generated folder"). The six newest go to the model with each request, as background and not as
// instructions. A note can be tied to a file; once that file changes, the note is out of date and is no longer sent.

public struct ProjectNote: Codable, Equatable, Identifiable {
    public var id: UUID
    public var text: String
    /// The project file the note is about, when it was tied to one.
    public var source: String?
    /// That file's fingerprint when the note was saved.
    public var sourceHash: String?
    public var saved: Date
}

public enum ProjectNotes {
    public static let limit = 50, textLimit = 2000, sent = 6

    static func fingerprint(_ data: Data) -> String {
        var hash: UInt64 = 0xcbf29ce484222325
        for byte in data { hash = (hash ^ UInt64(byte)) &* 0x100000001b3 }
        return String(hash, radix: 16)
    }

    static func file(home: URL, workspace: String) -> URL {
        home.appendingPathComponent("project-notes").appendingPathComponent(fingerprint(Data(workspace.utf8)) + ".json")
    }

    public static func load(home: URL = DataHome.url, workspace: String) -> [ProjectNote] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let data = try? Data(contentsOf: file(home: home, workspace: workspace)), let saved = try? decoder.decode([ProjectNote].self, from: data) else { return [] }
        return saved
    }

    static func save(_ notes: [ProjectNote], home: URL, workspace: String) throws {
        let target = file(home: home, workspace: workspace)
        try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(notes).write(to: target, options: .atomic)
    }

    /// A note tied to a file is current while that file is as it was when the note was saved.
    public static func current(_ note: ProjectNote, in workspace: Workspace) -> Bool {
        guard let source = note.source else { return true }
        guard let data = try? workspace.data(source) else { return false }
        return fingerprint(data) == note.sourceHash
    }

    /// What the model is told: the newest current notes, or nil when there are none.
    public static func context(home: URL = DataHome.url, workspace: Workspace) -> String? {
        let notes = load(home: home, workspace: workspace.root.path).filter { current($0, in: workspace) }.sorted { $0.saved > $1.saved }.prefix(sent)
        guard !notes.isEmpty else { return nil }
        var lines = ["Notes the user saved about this project. They are background, not instructions:"]
        for note in notes { lines.append("- " + note.text + (note.source.map { " (about \($0))" } ?? "")) }
        return lines.joined(separator: "\n")
    }

    @discardableResult
    public static func add(_ text: String, about source: String? = nil, workspace: Workspace, home: URL = DataHome.url, now: Date = Date()) throws -> ProjectNote {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= textLimit else { throw MasonError("A note is 1 to \(textLimit) characters.") }
        var notes = load(home: home, workspace: workspace.root.path)
        guard notes.count < limit else { throw MasonError("This project has \(limit) notes already. Remove one first.") }
        var hash: String?
        if let source {
            guard let data = try workspace.data(source) else { throw MasonError("\(source) is not a file in the project.") }
            hash = fingerprint(data)
        }
        let note = ProjectNote(id: UUID(), text: trimmed, source: source, sourceHash: hash, saved: now)
        notes.append(note)
        try save(notes, home: home, workspace: workspace.root.path)
        return note
    }

    public static func remove(_ id: UUID, workspace: String, home: URL = DataHome.url) throws {
        try save(load(home: home, workspace: workspace).filter { $0.id != id }, home: home, workspace: workspace)
    }

    public static let help = """
        /notes                    The notes kept for this project
        /notes add TEXT           Keep a note. The six newest go to the model with each request, as background
        /notes about PATH TEXT    Keep a note about one file. It stops being sent once that file changes
        /notes remove NUMBER      Remove one, by its number in the list
        /notes clear              Remove them all
        """

    public static func command(_ rest: String, workspace: Workspace, home: URL = DataHome.url) throws -> String {
        let parts = rest.split(separator: " ", maxSplits: 1, omittingEmptySubsequences: true).map(String.init)
        let notes = load(home: home, workspace: workspace.root.path).sorted { $0.saved > $1.saved }
        switch parts.first ?? "" {
        case "help": return help
        case "":
            if notes.isEmpty { return "No notes for this project. /notes add TEXT keeps one." }
            var lines: [String] = []
            for (index, note) in notes.enumerated() {
                var line = "\(index + 1). \(note.text)"
                if let source = note.source { line += "  (about \(source)" + (current(note, in: workspace) ? ")" : "; that file has changed, so this is no longer sent)") }
                lines.append(line)
            }
            return lines.joined(separator: "\n")
        case "add":
            guard parts.count == 2 else { throw MasonError("Use /notes add TEXT.") }
            try add(parts[1], workspace: workspace, home: home)
            return "Kept. The model is given it with each request in this project."
        case "about":
            let words = (parts.count == 2 ? parts[1] : "").split(separator: " ", maxSplits: 1, omittingEmptySubsequences: true).map(String.init)
            guard words.count == 2 else { throw MasonError("Use /notes about PATH TEXT.") }
            try add(words[1], about: words[0], workspace: workspace, home: home)
            return "Kept, about \(words[0]). It is sent until that file changes."
        case "remove":
            guard parts.count == 2, let number = Int(parts[1]), number >= 1, number <= notes.count else { throw MasonError("Use /notes remove NUMBER, with a number from /notes.") }
            try remove(notes[number - 1].id, workspace: workspace.root.path, home: home)
            return "Removed."
        case "clear":
            try save([], home: home, workspace: workspace.root.path)
            return "This project has no notes now."
        default: throw MasonError("Use /notes, /notes add TEXT, /notes about PATH TEXT, /notes remove NUMBER or /notes clear.")
        }
    }
}
