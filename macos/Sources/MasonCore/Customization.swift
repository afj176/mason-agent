import Foundation

/// A Markdown definition with simple "key: value" front matter.
public struct Definition: Equatable {
    public let name: String
    public let description: String
    public let body: String
    /// Where it came from, relative to the project when it is a project file.
    public let source: String
    public let fields: [String: String]
}

/// Custom agents and slash commands defined as Markdown, in the layouts other harnesses use:
/// agents in .mason/agents, .agents/agents or .claude/agents; commands in .mason/commands, .claude/commands,
/// .opencode/command or .opencode/commands; plus `agents` and `commands` in Mason's data folder for every project.
/// Project files win. A command body may use $ARGUMENTS and $1–$9. An agent body becomes standing instructions.
public enum Customization {
    public static let reserved = ["new", "queue", "compact", "undo", "redo", "help"]
    static let maxBody = 8000, maxFiles = 64
    static let agentFolders = [".mason/agents", ".agents/agents", ".claude/agents"]
    static let commandFolders = [".mason/commands", ".claude/commands", ".opencode/command", ".opencode/commands"]

    public static func agents(home: URL, workspace: String) -> [Definition] { load(home, workspace, agentFolders, "agents") }
    public static func commands(home: URL, workspace: String) -> [Definition] { load(home, workspace, commandFolders, "commands") }

    static func validName(_ name: String) -> Bool {
        guard let first = name.unicodeScalars.first, name.count <= 48 else { return false }
        let letters = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
        return letters.contains(first) && name.unicodeScalars.allSatisfy { letters.contains($0) || "_.-".unicodeScalars.contains($0) }
    }

    static func load(_ home: URL, _ workspace: String, _ projectFolders: [String], _ profileFolder: String) -> [Definition] {
        var found: [String: Definition] = [:]
        let root = URL(fileURLWithPath: workspace)
        let folders = projectFolders.map { (root.appendingPathComponent($0), true) } + [(home.appendingPathComponent(profileFolder), false)]
        for (folder, inProject) in folders {
            let files = ((try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.isSymbolicLinkKey, .fileSizeKey])) ?? [])
                .filter { $0.pathExtension == "md" }.sorted { $0.lastPathComponent.lowercased() < $1.lastPathComponent.lowercased() }.prefix(maxFiles)
            for file in files {
                let name = file.deletingPathExtension().lastPathComponent
                guard validName(name), found[name.lowercased()] == nil, !reserved.contains(name.lowercased()) else { continue }
                let values = try? file.resourceValues(forKeys: [.isSymbolicLinkKey, .fileSizeKey])
                guard values?.isSymbolicLink != true, (values?.fileSize ?? 0) <= 64 * 1024, let text = try? String(contentsOf: file, encoding: .utf8) else { continue }
                let source = inProject ? folder.path.dropFirst(root.path.count + 1) + "/" + file.lastPathComponent : file.path
                found[name.lowercased()] = parse(name: name, text: text, source: String(source))
            }
        }
        return found.values.sorted { $0.name.lowercased() < $1.name.lowercased() }
    }

    public static func parse(name: String, text: String, source: String) -> Definition {
        var text = text.replacingOccurrences(of: "\r\n", with: "\n")
        if text.hasPrefix("\u{FEFF}") { text.removeFirst() }
        var fields: [String: String] = [:]
        var body = text
        if text.hasPrefix("---\n"), let end = text.range(of: "\n---", range: text.index(text.startIndex, offsetBy: 4)..<text.endIndex) {
            for line in text[text.index(text.startIndex, offsetBy: 4)..<end.lowerBound].split(separator: "\n") {
                guard let colon = line.firstIndex(of: ":"), colon != line.startIndex, line.first?.isWhitespace == false else { continue }
                let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
                fields[line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()] = value
            }
            body = String(text[end.upperBound...])
            while body.hasPrefix("\n") { body.removeFirst() }
        }
        body = body.trimmingCharacters(in: .whitespacesAndNewlines)
        if body.count > maxBody { body = String(body.prefix(maxBody)) }
        var description = fields["description"] ?? ""
        if description.isEmpty, let line = body.split(separator: "\n").first(where: { !$0.trimmingCharacters(in: .whitespaces).isEmpty }) {
            description = line.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "# "))
        }
        if description.count > 160 { description = String(description.prefix(160)) + "…" }
        let named = fields["name"].flatMap { validName($0) ? $0 : nil } ?? name
        return Definition(name: named, description: description, body: body, source: source, fields: fields)
    }

    /// Splits "/name arguments"; nil when the input is not a slash command.
    static func invocation(_ input: String) -> (name: String, arguments: String)? {
        let trimmed = input.drop { $0.isWhitespace }
        guard trimmed.hasPrefix("/") else { return nil }
        let rest = trimmed.dropFirst()
        let name = String(rest.prefix { !$0.isWhitespace })
        guard validName(name) else { return nil }
        return (name, rest.dropFirst(name.count).trimmingCharacters(in: .whitespacesAndNewlines))
    }

    /// Expands "/name arguments" when a custom command has that name; otherwise returns nil.
    public static func expand(_ commands: [Definition], _ input: String) -> String? {
        guard let (name, arguments) = invocation(input), let command = commands.first(where: { $0.name.lowercased() == name.lowercased() }) else { return nil }
        let parts = words(arguments)
        var expanded = command.body.replacingOccurrences(of: "$ARGUMENTS", with: arguments)
        for index in stride(from: 9, through: 1, by: -1) { expanded = expanded.replacingOccurrences(of: "$\(index)", with: index <= parts.count ? parts[index - 1] : "") }
        if !command.body.contains("$"), !arguments.isEmpty { expanded += "\n\n" + arguments }
        return expanded.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// The request sent to the model: a custom command expanded, framed by the session agent's instructions.
    public static func prepare(home: URL, session: Session, input: String) -> String {
        var request = expand(commands(home: home, workspace: session.workspace), input) ?? input
        if let name = session.agent, !name.isEmpty, let agent = agents(home: home, workspace: session.workspace).first(where: { $0.name.lowercased() == name.lowercased() }) {
            request = "Standing instructions from the selected agent \"\(agent.name)\" (\(agent.source)):\n\(agent.body)\n\nRequest:\n\(request)"
        }
        return request
    }

    static func words(_ text: String) -> [String] {
        var found: [String] = [], current = "", quote: Character? = nil
        for character in text {
            if quote == nil, character == "\"" || character == "'" { quote = character; continue }
            if character == quote { quote = nil; continue }
            if quote == nil, character.isWhitespace { if !current.isEmpty { found.append(current); current = "" }; continue }
            current.append(character)
        }
        if !current.isEmpty { found.append(current) }
        return found
    }
}
