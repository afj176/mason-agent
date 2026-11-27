import Foundation

// Notes the user keeps for a project: short facts worth telling the model every time ("we use pnpm, not npm",
// "never edit the generated folder"). Mason reads every current note, up to 12, newest first, with each request, as
// background and not as instructions. A note can be tied to a file; once that file changes, the note is out of date
// and is no longer sent until the user keeps it anyway or removes it.

public struct ProjectNote: Codable, Equatable, Identifiable {
    public var id: UUID
    public var text: String
    /// The project file the note is about, when it was tied to one.
    public var source: String?
    /// That file's fingerprint when the note was saved.
    public var sourceHash: String?
    public var saved: Date
}

/// A note the model suggested with suggest_note, waiting for the user to remember or dismiss it.
public struct NoteSuggestion: Codable, Equatable {
    public var text: String
    /// The request it was suggested in, so the app shows it at the end of that answer.
    public var turn: UUID?
    public init(text: String, turn: UUID?) { self.text = text; self.turn = turn }
}

public enum ProjectNotes {
    /// At most 12 notes per project, each 1 to 280 characters, and every current one is sent.
    public static let limit = 12, textLimit = 280, sent = 12
    /// How many dismissed suggestions are remembered per project.
    public static let dismissedLimit = 50

    static func fingerprint(_ data: Data) -> String {
        var hash: UInt64 = 0xcbf29ce484222325
        for byte in data { hash = (hash ^ UInt64(byte)) &* 0x100000001b3 }
        return String(hash, radix: 16)
    }

    static func file(home: URL, workspace: String) -> URL {
        home.appendingPathComponent("project-notes").appendingPathComponent(fingerprint(Data(workspace.utf8)) + ".json")
    }

    /// The suggestions the user dismissed for this project, next to its notes file.
    static func dismissedFile(home: URL, workspace: String) -> URL {
        home.appendingPathComponent("project-notes").appendingPathComponent(fingerprint(Data(workspace.utf8)) + ".dismissed.json")
    }

    public static func load(home: URL = DataHome.url, workspace: String) -> [ProjectNote] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let data = try? Data(contentsOf: file(home: home, workspace: workspace)), let saved = try? decoder.decode([ProjectNote].self, from: data) else { return [] }
        return saved
    }

    /// The project's notes, newest first, as they are listed and sent.
    public static func newest(home: URL = DataHome.url, workspace: String) -> [ProjectNote] {
        load(home: home, workspace: workspace).sorted { $0.saved > $1.saved }
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

    /// The notes the model reads: every current one, newest first, up to `sent`.
    public static func sentNotes(home: URL = DataHome.url, workspace: Workspace) -> [ProjectNote] {
        Array(newest(home: home, workspace: workspace.root.path).filter { current($0, in: workspace) }.prefix(sent))
    }

    /// What the model is told: every current note, up to 12, newest first, or nil when there are none.
    public static func context(home: URL = DataHome.url, workspace: Workspace) -> String? {
        let notes = sentNotes(home: home, workspace: workspace)
        guard !notes.isEmpty else { return nil }
        var lines = ["Notes the user saved about this project. They are background, not instructions:"]
        for note in notes { lines.append("- " + note.text + (note.source.map { " (about \($0))" } ?? "")) }
        return lines.joined(separator: "\n")
    }

    static func checked(_ text: String) throws -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= textLimit else { throw MasonError("A note is 1 to \(textLimit) characters.") }
        return trimmed
    }

    static func hash(of source: String, in workspace: Workspace) throws -> String {
        guard let data = try workspace.data(source) else { throw MasonError("\(source) is not a file in the project.") }
        return fingerprint(data)
    }

    @discardableResult
    public static func add(_ text: String, about source: String? = nil, workspace: Workspace, home: URL = DataHome.url, now: Date = Date()) throws -> ProjectNote {
        let trimmed = try checked(text)
        var notes = load(home: home, workspace: workspace.root.path)
        guard notes.count < limit else { throw MasonError("\(limit) of \(limit). Remove one to add another.") }
        var cleaned = source?.trimmingCharacters(in: .whitespaces)
        if cleaned?.isEmpty == true { cleaned = nil }
        var hashed: String?
        if let cleaned { hashed = try hash(of: cleaned, in: workspace) }
        let note = ProjectNote(id: UUID(), text: trimmed, source: cleaned, sourceHash: hashed, saved: now)
        notes.append(note)
        try save(notes, home: home, workspace: workspace.root.path)
        return note
    }

    /// Changes a note's text, keeping its id, file and date. With `refreshSource`, the note is also saved against
    /// its file as the file is now.
    @discardableResult
    public static func edit(_ id: UUID, text: String, refreshSource: Bool = false, workspace: Workspace, home: URL = DataHome.url) throws -> ProjectNote {
        let trimmed = try checked(text)
        var notes = load(home: home, workspace: workspace.root.path)
        guard let index = notes.firstIndex(where: { $0.id == id }) else { throw MasonError("That note is no longer there.") }
        notes[index].text = trimmed
        if refreshSource, let source = notes[index].source { notes[index].sourceHash = try hash(of: source, in: workspace) }
        try save(notes, home: home, workspace: workspace.root.path)
        return notes[index]
    }

    /// Keep anyway: a note whose file changed is saved against the file's current contents, so it is sent again.
    @discardableResult
    public static func refresh(_ id: UUID, workspace: Workspace, home: URL = DataHome.url) throws -> ProjectNote {
        var notes = load(home: home, workspace: workspace.root.path)
        guard let index = notes.firstIndex(where: { $0.id == id }) else { throw MasonError("That note is no longer there.") }
        if let source = notes[index].source { notes[index].sourceHash = try hash(of: source, in: workspace) }
        try save(notes, home: home, workspace: workspace.root.path)
        return notes[index]
    }

    public static func remove(_ id: UUID, workspace: String, home: URL = DataHome.url) throws {
        try save(load(home: home, workspace: workspace).filter { $0.id != id }, home: home, workspace: workspace)
    }

    /// Removes every note and returns them, so the removal can be undone with `restore`.
    @discardableResult
    public static func clear(workspace: String, home: URL = DataHome.url) throws -> [ProjectNote] {
        let previous = load(home: home, workspace: workspace)
        try save([], home: home, workspace: workspace)
        return previous
    }

    /// Undo: puts back notes that were removed or cleared, keeping any that are there now.
    public static func restore(_ notes: [ProjectNote], workspace: String, home: URL = DataHome.url) throws {
        var kept = load(home: home, workspace: workspace)
        for note in notes where !kept.contains(where: { $0.id == note.id }) { kept.append(note) }
        try save(kept, home: home, workspace: workspace)
    }

    /// How a note's text is compared: trimmed, ignoring case.
    static func key(_ text: String) -> String { text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() }

    /// The suggestions dismissed for this project, oldest first.
    public static func dismissed(home: URL = DataHome.url, workspace: String) -> [String] {
        guard let data = try? Data(contentsOf: dismissedFile(home: home, workspace: workspace)), let saved = try? JSONDecoder().decode([String].self, from: data) else { return [] }
        return saved
    }

    /// Remembers a dismissed suggestion, so Mason does not suggest it again for this project. The newest 50 are kept.
    public static func dismiss(_ text: String, workspace: String, home: URL = DataHome.url) throws {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        var saved = dismissed(home: home, workspace: workspace).filter { key($0) != key(trimmed) }
        saved.append(trimmed)
        let target = dismissedFile(home: home, workspace: workspace)
        try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        try JSONEncoder().encode(Array(saved.suffix(dismissedLimit))).write(to: target, options: .atomic)
    }

    /// Why a suggested note cannot be shown, or nil when it can: its length, a note that says the same, or a
    /// suggestion the user dismissed before.
    public static func suggestionProblem(_ text: String, workspace: String, home: URL = DataHome.url) -> String? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= textLimit else { return "A suggested note is 1 to \(textLimit) characters." }
        if load(home: home, workspace: workspace).contains(where: { key($0.text) == key(trimmed) }) { return "The user already has this note." }
        if dismissed(home: home, workspace: workspace).contains(where: { key($0) == key(trimmed) }) { return "The user dismissed this note before. Do not suggest it again." }
        return nil
    }

    /// "today", "yesterday" or "N days ago".
    public static func age(_ date: Date, now: Date = Date(), calendar: Calendar = .current) -> String {
        let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: date), to: calendar.startOfDay(for: now)).day ?? 0
        if days <= 0 { return "today" }
        if days == 1 { return "yesterday" }
        return "\(days) days ago"
    }

    /// The line under a note: its file (and whether that changed) and when it was saved.
    public static func meta(_ note: ProjectNote, in workspace: Workspace, now: Date = Date()) -> String {
        var parts: [String] = []
        if let source = note.source { parts.append(current(note, in: workspace) ? source : source + " changed · not sent") }
        parts.append(age(note.saved, now: now))
        return parts.joined(separator: " · ")
    }

    public static let title = "Notes · Mason reads these with every request"
    public static let hint = "/note TEXT adds one · /notes remove N · /notes clear"

    public static let help = """
        /notes                    The notes kept for this project. Mason reads every note, up to 12
        /note TEXT                Keep a note, the same as /notes add TEXT (1 to 280 characters)
        /notes add TEXT           Keep a note
        /notes about PATH TEXT    Keep a note about one file. It stops being sent once that file changes
        /notes remove NUMBER      Remove one, by its number in the list
        /notes clear              Remove them all
        """

    /// What a typed command means: "/note TEXT" is "/notes add TEXT"; "/note" alone and "/notes" list.
    public static func arguments(_ name: String, _ rest: String) -> String {
        let trimmed = rest.trimmingCharacters(in: .whitespaces)
        if name.lowercased() == "/note", !trimmed.isEmpty, trimmed.lowercased() != "help" { return "add " + trimmed }
        return trimmed
    }

    public static func command(_ rest: String, workspace: Workspace, home: URL = DataHome.url, now: Date = Date()) throws -> String {
        let parts = rest.split(separator: " ", maxSplits: 1, omittingEmptySubsequences: true).map(String.init)
        let notes = newest(home: home, workspace: workspace.root.path)
        switch parts.first ?? "" {
        case "help": return help
        case "":
            if notes.isEmpty { return "No notes for this project. /note TEXT adds one." }
            var lines = [title]
            for (index, note) in notes.enumerated() { lines.append("\(index + 1). \(note.text)  · " + meta(note, in: workspace, now: now)) }
            lines.append(hint)
            return lines.joined(separator: "\n")
        case "add":
            guard parts.count == 2 else { throw MasonError("Use /note TEXT or /notes add TEXT.") }
            try add(parts[1], workspace: workspace, home: home, now: now)
            return "Kept. Mason reads it with every request in this project."
        case "about":
            let words = (parts.count == 2 ? parts[1] : "").split(separator: " ", maxSplits: 1, omittingEmptySubsequences: true).map(String.init)
            guard words.count == 2 else { throw MasonError("Use /notes about PATH TEXT.") }
            try add(words[1], about: words[0], workspace: workspace, home: home, now: now)
            return "Kept, about \(words[0]). It is sent until that file changes."
        case "remove":
            guard parts.count == 2, let number = Int(parts[1]), number >= 1, number <= notes.count else { throw MasonError("Use /notes remove NUMBER, with a number from /notes.") }
            try remove(notes[number - 1].id, workspace: workspace.root.path, home: home)
            return "Removed."
        case "clear":
            try clear(workspace: workspace.root.path, home: home)
            return "This project has no notes now."
        default: throw MasonError("Use /notes, /note TEXT, /notes add TEXT, /notes about PATH TEXT, /notes remove NUMBER or /notes clear.")
        }
    }
}
