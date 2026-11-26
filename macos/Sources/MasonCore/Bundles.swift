import Foundation

/// A bundle is a folder of skills and documentation that the user installs once and every project can then use. It
/// holds `bundle.json` (`{"name": ..., "version": ..., "description": ...}`), `skills/NAME/SKILL.md`, `docs/*.md` and,
/// optionally, `mcp.json`: MCP servers in the format of Mason's own list. Only those text files are copied. A bundle's
/// servers are not used until the user adds them, and then they arrive switched off. A project's own documentation
/// lives in `.mason/docs`.
public struct InstalledBundle: Equatable {
    public var name: String
    public var version: String
    public var description: String
    public var folder: URL
}

public enum Bundles {
    static func root(_ home: URL) -> URL { home.appendingPathComponent("bundles") }
    static let maxFiles = 200, maxBytes = 8 * 1024 * 1024

    static func manifest(_ folder: URL) -> InstalledBundle? {
        guard let data = try? Data(contentsOf: folder.appendingPathComponent("bundle.json")), data.count <= 64 * 1024, let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let name = object["name"] as? String, Customization.validName(name), let version = object["version"] as? String, (1...40).contains(version.count),
              version.range(of: "^[A-Za-z0-9][A-Za-z0-9._+-]*$", options: .regularExpression) != nil else { return nil }
        return InstalledBundle(name: name, version: version, description: String((object["description"] as? String ?? "").prefix(300)), folder: folder)
    }

    public static func list(home: URL = DataHome.url) -> [InstalledBundle] {
        let names = ((try? FileManager.default.contentsOfDirectory(atPath: root(home).path)) ?? []).sorted()
        return names.compactMap { name in manifest(root(home).appendingPathComponent(name)).flatMap { $0.name == name ? $0 : nil } }
    }

    /// The text files a bundle is made of, relative to its folder.
    static func files(in folder: URL) throws -> [String] {
        var found = ["bundle.json"]
        let manager = FileManager.default
        if manager.fileExists(atPath: folder.appendingPathComponent("mcp.json").path) { found.append("mcp.json") }
        for skill in ((try? manager.contentsOfDirectory(atPath: folder.appendingPathComponent("skills").path)) ?? []).sorted() where Customization.validName(skill) {
            if manager.fileExists(atPath: folder.appendingPathComponent("skills/\(skill)/SKILL.md").path) { found.append("skills/\(skill)/SKILL.md") }
        }
        for document in ((try? manager.contentsOfDirectory(atPath: folder.appendingPathComponent("docs").path)) ?? []).sorted() where document.lowercased().hasSuffix(".md") && document.range(of: "^[A-Za-z0-9][A-Za-z0-9 ._-]{0,80}$", options: .regularExpression) != nil {
            found.append("docs/" + document)
        }
        var total = 0
        for path in found {
            let file = folder.appendingPathComponent(path)
            // A link could point anywhere on the computer, so only real files are taken.
            guard let attributes = try? manager.attributesOfItem(atPath: file.path), attributes[.type] as? FileAttributeType == .typeRegular, let size = attributes[.size] as? Int else { throw MasonError("\(path) in the bundle is not an ordinary file.") }
            total += size
        }
        guard found.count <= maxFiles, total <= maxBytes else { throw MasonError("A bundle holds at most \(maxFiles) files and 8 MiB.") }
        return found
    }

    /// Copies a bundle into Mason's own folder, replacing an earlier version of the same name.
    public static func install(from path: String, home: URL = DataHome.url) throws -> InstalledBundle {
        let source = URL(fileURLWithPath: (path as NSString).expandingTildeInPath).standardizedFileURL
        guard let found = manifest(source) else { throw MasonError("That folder has no valid bundle.json. It needs a name (letters, digits, - or _) and a version.") }
        let paths = try files(in: source)
        guard paths.count > 1 else { throw MasonError("The bundle \(found.name) has no skills or docs.") }
        let manager = FileManager.default
        let staging = root(home).appendingPathComponent(".installing-" + UUID().uuidString)
        defer { try? manager.removeItem(at: staging) }
        for relative in paths {
            let target = staging.appendingPathComponent(relative)
            try manager.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
            try manager.copyItem(at: source.appendingPathComponent(relative), to: target)
        }
        let place = root(home).appendingPathComponent(found.name)
        try? manager.removeItem(at: place)
        try manager.moveItem(at: staging, to: place)
        return InstalledBundle(name: found.name, version: found.version, description: found.description, folder: place)
    }

    public static func remove(_ name: String, home: URL = DataHome.url) throws {
        guard let found = list(home: home).first(where: { $0.name.lowercased() == name.lowercased() }) else { throw MasonError("No bundle named \(name) is installed.") }
        try FileManager.default.removeItem(at: found.folder)
    }

    /// The MCP servers a bundle names. They are only a proposal until the user adds them.
    public static func servers(of bundle: InstalledBundle) throws -> [McpServer] {
        let file = bundle.folder.appendingPathComponent("mcp.json")
        guard FileManager.default.fileExists(atPath: file.path) else { return [] }
        let data = try Data(contentsOf: file)
        guard data.count <= 32768, let servers = try? JSONDecoder().decode([McpServer].self, from: data), servers.count <= 16 else { throw MasonError("The mcp.json of \(bundle.name) is not a list of up to 16 MCP servers.") }
        return servers
    }

    /// Adds a bundle's MCP servers to Mason's list, switched off, so each is looked at before it is used.
    /// Returns the names added; a server whose name is already registered is left as it is.
    public static func addServers(of bundle: InstalledBundle, home: URL = DataHome.url) throws -> [String] {
        var registered = try Mcp.all(home: home)
        var added: [String] = []
        for var server in try servers(of: bundle) where !registered.contains(where: { $0.name == server.name }) {
            try Mcp.validate(server)
            server.enabled = false
            registered.append(server)
            added.append(server.name)
        }
        if !added.isEmpty { try Mcp.save(registered, home: home) }
        return added
    }

    /// The skills installed bundles bring.
    public static func skills(home: URL = DataHome.url) -> [Definition] {
        var found: [Definition] = []
        for bundle in list(home: home) {
            for skill in ((try? FileManager.default.contentsOfDirectory(atPath: bundle.folder.appendingPathComponent("skills").path)) ?? []).sorted() where Customization.validName(skill) {
                guard let data = try? Data(contentsOf: bundle.folder.appendingPathComponent("skills/\(skill)/SKILL.md")), data.count <= 256 * 1024 else { continue }
                found.append(Customization.parse(name: skill, text: String(decoding: data, as: UTF8.self), source: "bundle \(bundle.name) \(bundle.version)"))
            }
        }
        return found
    }

    /// Every document to look things up in: the project's `.mason/docs`, then each installed bundle's docs.
    static func documents(home: URL, workspace: Workspace) -> [(source: String, text: String)] {
        var found: [(source: String, text: String)] = []
        for name in ((try? FileManager.default.contentsOfDirectory(atPath: workspace.root.appendingPathComponent(".mason/docs").path)) ?? []).sorted() where name.lowercased().hasSuffix(".md") {
            if let text = try? workspace.read(".mason/docs/" + name) { found.append((".mason/docs/" + name, text)) }
        }
        for bundle in list(home: home) {
            for name in ((try? FileManager.default.contentsOfDirectory(atPath: bundle.folder.appendingPathComponent("docs").path)) ?? []).sorted() where name.lowercased().hasSuffix(".md") {
                guard let data = try? Data(contentsOf: bundle.folder.appendingPathComponent("docs/" + name)), data.count <= 1024 * 1024 else { continue }
                found.append(("\(bundle.name) \(bundle.version)/docs/\(name)", String(decoding: data, as: UTF8.self)))
            }
        }
        return found
    }

    public static func hasDocuments(home: URL = DataHome.url, workspace: Workspace) -> Bool { !documents(home: home, workspace: workspace).isEmpty }

    /// The sections that mention most of the query's words, each with where it came from, so an answer can cite it.
    public static func read(_ query: String, home: URL = DataHome.url, workspace: Workspace) -> String {
        let words = Set(query.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init).filter { $0.count >= 3 })
        guard !words.isEmpty else { return "Give a few words to look for." }
        var sections: [(score: Int, source: String, line: Int, text: String)] = []
        for document in documents(home: home, workspace: workspace) {
            // A section runs from one heading to the next.
            var start = 1, current: [String] = []
            func close() {
                let text = current.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { return }
                let lowered = text.lowercased()
                let score = words.filter { lowered.contains($0) }.count
                if score > 0 { sections.append((score, document.source, start, text)) }
            }
            for (index, line) in document.text.components(separatedBy: "\n").enumerated() {
                if line.hasPrefix("#"), !current.isEmpty { close(); current = []; start = index + 1 }
                current.append(line)
            }
            close()
        }
        guard !sections.isEmpty else { return "The documentation does not mention that. Try other words." }
        let best = sections.enumerated().sorted { ($0.element.score, -$0.offset) > ($1.element.score, -$1.offset) }.prefix(3).map(\.element)
        return best.map { "[\($0.source):\($0.line)]\n" + ($0.text.count > 1500 ? String($0.text.prefix(1500)) + "\n[Section clipped.]" : $0.text) }.joined(separator: "\n\n")
            + "\n\nCite what you use by the name in brackets."
    }

    // MARK: the /bundle command, shared by the command line and the app

    public static let usage = """
        /bundle                    The bundles installed on this computer
        /bundle install FOLDER     Install a bundle: its skills and docs become available in every project
        /bundle servers NAME       The MCP servers a bundle names
        /bundle servers NAME add   Add them to your MCP servers, switched off; switch one on in Settings once you have looked at it
        /bundle remove NAME        Remove one
        A bundle is a folder with bundle.json ({"name": ..., "version": ...}), skills/NAME/SKILL.md and docs/*.md.
        """

    public static func command(_ rest: String, home: URL = DataHome.url) throws -> String {
        let trimmed = rest.trimmingCharacters(in: .whitespaces)
        let verb = trimmed.split(separator: " ").first.map { $0.lowercased() } ?? ""
        let argument = String(trimmed.dropFirst(verb.count)).trimmingCharacters(in: .whitespaces)
        switch verb {
        case "":
            let installed = list(home: home)
            return installed.isEmpty ? "No bundles are installed.\n" + usage : installed.map { "\($0.name) \($0.version)" + ($0.description.isEmpty ? "" : "  ·  " + $0.description) }.joined(separator: "\n")
        case "install":
            guard !argument.isEmpty else { throw MasonError("Use /bundle install FOLDER.") }
            let made = try install(from: argument, home: home)
            let named = try servers(of: made)
            let count = try files(in: made.folder).count - 1 - (named.isEmpty ? 0 : 1)
            return "Installed \(made.name) \(made.version): \(count) file\(count == 1 ? "" : "s") of skills and docs."
                + (named.isEmpty ? "" : "\nIt names \(named.count) MCP server\(named.count == 1 ? "" : "s") (\(named.map(\.name).joined(separator: ", "))). They are not in use: /bundle servers \(made.name) shows them.")
        case "servers":
            let words = argument.split(separator: " ").map(String.init)
            guard let name = words.first, let bundle = list(home: home).first(where: { $0.name.lowercased() == name.lowercased() }) else { throw MasonError("Use /bundle servers NAME, with the name of an installed bundle.") }
            let named = try servers(of: bundle)
            if named.isEmpty { return "\(bundle.name) names no MCP servers." }
            if words.count > 1, words[1].lowercased() == "add" {
                let added = try addServers(of: bundle, home: home)
                return added.isEmpty ? "Those servers are already in your list." : "Added, switched off: \(added.joined(separator: ", ")). Look at each and switch it on in Settings, under MCP servers."
            }
            return named.map { "\($0.name)  ·  \($0.transport)  ·  tools: \($0.tools.isEmpty ? "none listed" : $0.tools.joined(separator: ", "))" }.joined(separator: "\n") + "\n/bundle servers \(bundle.name) add puts them in your list, switched off."
        case "remove":
            try remove(argument, home: home)
            return "Removed \(argument)."
        default:
            return usage
        }
    }
}
