import Foundation

// Generation settings kept for one model connection: how the model picks its words when it works as the coding
// agent. A setting left unset is not sent, so the server's own default applies. Top K, Min P and repeat penalty are
// llama.cpp's; another server may ignore them.

public enum Generation {
    public struct Field {
        public let key: String
        public let label: String
        public let low: Double
        public let high: Double
        public let whole: Bool
    }

    public static let fields: [Field] = [
        Field(key: "temperature", label: "Temperature", low: 0, high: 2, whole: false),
        Field(key: "top_p", label: "Top P", low: 0, high: 1, whole: false),
        Field(key: "top_k", label: "Top K", low: 1, high: 1000, whole: true),
        Field(key: "min_p", label: "Min P", low: 0, high: 1, whole: false),
        Field(key: "repeat_penalty", label: "Repeat penalty", low: 0, high: 2, whole: false),
        Field(key: "seed", label: "Seed", low: 0, high: 2_147_483_647, whole: true),
    ]

    static func file(_ home: URL) -> URL { home.appendingPathComponent("generation.json") }

    /// A connection is a server and a model on it.
    static func name(_ endpoint: ModelEndpoint) -> String { endpoint.url + " " + endpoint.model }

    static func all(home: URL) -> [String: [String: Double]] {
        guard let data = try? Data(contentsOf: file(home)), let saved = try? JSONDecoder().decode([String: [String: Double]].self, from: data) else { return [:] }
        return saved
    }

    /// The settings kept for a connection, by request field.
    public static func values(for endpoint: ModelEndpoint, home: URL = DataHome.url) -> [String: Double] {
        (all(home: home)[name(endpoint)] ?? [:]).filter { entry in fields.contains { $0.key == entry.key } }
    }

    /// The same settings as they go in a request: whole numbers where the server expects them.
    static func body(for endpoint: ModelEndpoint, home: URL = DataHome.url) -> [String: Any] {
        var body: [String: Any] = [:]
        for (key, value) in values(for: endpoint, home: home) {
            body[key] = fields.first { $0.key == key }?.whole == true ? Int(value) as Any : value as Any
        }
        return body
    }

    static func save(_ values: [String: Double], for endpoint: ModelEndpoint, home: URL) throws {
        var saved = all(home: home)
        saved[name(endpoint)] = values.isEmpty ? nil : values
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(saved).write(to: file(home), options: .atomic)
    }

    public static func shown(_ value: Double, _ field: Field) -> String {
        field.whole ? String(Int(value)) : String(format: "%g", value)
    }

    public static let help = """
        /generation                  The generation settings kept for the model in use
        /generation NAME VALUE       Set one: temperature (0–2), top_p (0–1), top_k (1–1000), min_p (0–1), repeat_penalty (0–2), seed
        /generation NAME off         Leave one to the server's default
        /generation clear            Leave them all to the server's defaults
        They apply to this server and model whenever it works as the coding agent. Chats have their own options.
        """

    public static func command(_ rest: String, endpoint: ModelEndpoint?, home: URL = DataHome.url) throws -> String {
        let words = rest.split(separator: " ").map(String.init)
        if words.first == "help" { return help }
        guard let endpoint else { throw MasonError("No model is in use. Generation settings are kept for a model, so start or choose one first.") }
        var values = values(for: endpoint, home: home)
        if words.isEmpty {
            var lines = ["Generation settings for \(endpoint.model) at \(endpoint.url):"]
            for field in fields {
                lines.append("\(field.key)  ·  " + (values[field.key].map { shown($0, field) } ?? "the server's default"))
            }
            return lines.joined(separator: "\n")
        }
        if words == ["clear"] {
            try save([:], for: endpoint, home: home)
            return "Generation settings for \(endpoint.model) are left to the server's defaults."
        }
        let key = words[0].lowercased().replacingOccurrences(of: "-", with: "_")
        guard words.count == 2, let field = fields.first(where: { $0.key == key }) else {
            throw MasonError("Use /generation NAME VALUE, with one of: " + fields.map(\.key).joined(separator: ", ") + ".")
        }
        if ["off", "default", "none"].contains(words[1].lowercased()) {
            values[field.key] = nil
            try save(values, for: endpoint, home: home)
            return "\(field.label) is left to the server's default for \(endpoint.model)."
        }
        guard let value = Double(words[1]), value >= field.low, value <= field.high, !field.whole || value == value.rounded() else {
            throw MasonError("\(field.label) is a \(field.whole ? "whole number" : "number") from \(shown(field.low, field)) to \(shown(field.high, field)), or off.")
        }
        values[field.key] = value
        try save(values, for: endpoint, home: home)
        return "\(field.label) is \(shown(value, field)) for \(endpoint.model)."
    }
}
