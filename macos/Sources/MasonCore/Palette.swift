import Foundation

// The search box that reaches everything: sessions, projects, the project's files, commands and chats. The apps
// gather the items; choosing what matches what the user typed, and in what order, is done here so both apps agree.

public struct PaletteItem: Equatable, Identifiable {
    public enum Kind: String { case session, project, file, command, chat }
    public var kind: Kind
    public var title: String
    public var detail: String
    /// What the app acts on: a session or chat id, a folder, a file path, or a command's name.
    public var value: String
    public var id: String { kind.rawValue + " " + value }
    public init(kind: Kind, title: String, detail: String, value: String) {
        self.kind = kind; self.title = title; self.detail = detail; self.value = value
    }
}

public enum Palette {
    /// The commands the request box understands, for the palette to offer.
    public static let commands: [(name: String, detail: String)] = [
        ("/notes", "Notes kept for this project and given to the model"),
        ("/languages", "The language server that checks each language's edits"),
        ("/generation", "Temperature, top P and the like, for the model in use"),
        ("/experiment", "Try something in a copy of the project"),
        ("/deliver", "Review changes and draft a pull request"),
        ("/task", "Requests that keep running in the background"),
        ("/roles", "A reviewer model, a visual model, and a trace of model calls"),
        ("/policy", "This project's limits on tools, workflows and hooks"),
        ("/network", "Whether sandboxed commands may ask for a host"),
        ("/bundle", "Skills and documentation installed for every project"),
        ("/schedule", "Requests that run on a schedule"),
        ("/agent", "Use one of your agents for this session"),
    ]

    /// How well an item matches, lower being better, or nil when it does not. Every word typed must appear.
    static func score(_ item: PaletteItem, words: [String]) -> Int? {
        let title = item.title.lowercased(), detail = item.detail.lowercased()
        var worst = 0
        for word in words {
            var score = 3
            if title.hasPrefix(word) { score = 0 }
            else if title.contains(" " + word) || title.contains("/" + word) || title.contains("-" + word) || title.contains("_" + word) || title.contains("." + word) { score = 1 }
            else if title.contains(word) { score = 2 }
            else if !detail.contains(word) { return nil }
            worst = max(worst, score)
        }
        return worst
    }

    /// The items that match what was typed, best first; items that match equally keep the order they were given in.
    public static func matches(_ items: [PaletteItem], query: String, limit: Int = 40) -> [PaletteItem] {
        let words = query.lowercased().split(separator: " ").map(String.init)
        guard !words.isEmpty else { return Array(items.prefix(limit)) }
        var scored: [(score: Int, order: Int, item: PaletteItem)] = []
        for (order, item) in items.enumerated() {
            if let score = score(item, words: words) { scored.append((score, order, item)) }
        }
        scored.sort { $0.score != $1.score ? $0.score < $1.score : $0.order < $1.order }
        return scored.prefix(limit).map(\.item)
    }
}
