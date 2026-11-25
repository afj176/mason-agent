import Foundation

/// Some local models write a tool call as text (fenced JSON, or inside <tool_call> tags) instead of using the
/// server's tool-call channel. When the text names one of the offered tools, that call is recovered so the request
/// can continue. Text that is not a call for an offered tool is left alone.
public enum ToolCallRecovery {
    public static func recover(from text: String, tools: [ToolSpec]) -> ToolCall? {
        let names = Set(tools.map(\.name))
        for candidate in candidates(in: text) {
            guard let data = candidate.data(using: .utf8), let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
            // {"name": ..., "arguments": {...}} or the OpenAI shape {"function": {"name": ..., "arguments": ...}}.
            let inner = object["function"] as? [String: Any] ?? object
            guard let name = inner["name"] as? String ?? inner["tool"] as? String, names.contains(name) else { continue }
            let raw = inner["arguments"] ?? inner["parameters"] ?? [String: Any]()
            let arguments: String
            if let already = raw as? String { arguments = already }
            else if JSONSerialization.isValidJSONObject(raw), let encoded = try? JSONSerialization.data(withJSONObject: raw, options: [.sortedKeys]) { arguments = String(decoding: encoded, as: UTF8.self) }
            else { continue }
            guard (try? JSONSerialization.jsonObject(with: Data(arguments.utf8))) is [String: Any] else { continue }
            return ToolCall(id: "recovered_" + UUID().uuidString.prefix(8), name: name, arguments: arguments)
        }
        return nil
    }

    /// JSON object texts worth trying, in order: tagged calls, fenced blocks, then the whole reply.
    static func candidates(in text: String) -> [String] {
        var found: [String] = []
        for pattern in [#"<tool_call>\s*([\s\S]*?)\s*</tool_call>"#, #"```[A-Za-z_]*\s*\n([\s\S]*?)```"#] {
            guard let expression = try? NSRegularExpression(pattern: pattern) else { continue }
            let whole = NSRange(text.startIndex..., in: text)
            for match in expression.matches(in: text, range: whole) {
                if let range = Range(match.range(at: 1), in: text) { found.append(String(text[range]).trimmingCharacters(in: .whitespacesAndNewlines)) }
            }
        }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.hasPrefix("{"), trimmed.hasSuffix("}") { found.append(trimmed) }
        return found.filter { $0.hasPrefix("{") }
    }
}
