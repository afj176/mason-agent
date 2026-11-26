import Foundation

// For languages beyond Python and JavaScript, `inspect_symbol` asks the language server the user already has for that
// language: csharp-ls or OmniSharp for C#, sourcekit-lsp for Swift, clangd for C and C++, gopls for Go, rust-analyzer
// for Rust. The server is a program on this computer that reads the project, and some run the project's build, so it
// is started the way a command is: inside the sandbox in Auto, and after asking otherwise.

public enum LanguageServers {
    public struct Server: Equatable {
        public let name: String
        public let command: [String]
        public let languageId: String
    }

    static let known: [(extensions: Set<String>, languageId: String, candidates: [[String]])] = [
        (["cs"], "csharp", [["csharp-ls"], ["OmniSharp", "-lsp"], ["omnisharp", "-lsp"]]),
        (["swift"], "swift", [["sourcekit-lsp"]]),
        (["c", "h"], "c", [["clangd"]]),
        (["cc", "cpp", "cxx", "hpp", "hh", "m", "mm"], "cpp", [["clangd"]]),
        (["go"], "go", [["gopls"]]),
        (["rs"], "rust", [["rust-analyzer"]]),
    ]

    /// `MASON_LANGUAGE_SERVERS` can name a server for an extension, as JSON such as {"cs": ["/opt/csharp-ls/csharp-ls"]}.
    static func overrides() -> [String: [String]] {
        guard let text = ProcessInfo.processInfo.environment["MASON_LANGUAGE_SERVERS"], let object = try? JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: [String]] else { return [:] }
        return object
    }

    /// The language server for a file of this kind, when one is installed.
    public static func server(for kind: String) -> Server? {
        guard let entry = known.first(where: { $0.extensions.contains(kind) }) else { return nil }
        if let named = overrides()[kind], let program = named.first, FileManager.default.isExecutableFile(atPath: program) {
            return Server(name: URL(fileURLWithPath: program).lastPathComponent, command: named, languageId: entry.languageId)
        }
        for candidate in entry.candidates {
            if let program = SymbolService.onPath(candidate[0]) { return Server(name: candidate[0], command: [program] + candidate.dropFirst(), languageId: entry.languageId) }
        }
        return nil
    }

    /// True when some language server is installed, so the tool is worth offering.
    public static var available: Bool { known.contains { entry in entry.extensions.contains { server(for: $0) != nil } } }

    static func frame(_ object: [String: Any]) -> Data {
        let body = (try? JSONSerialization.data(withJSONObject: object)) ?? Data()
        return Data("Content-Length: \(body.count)\r\n\r\n".utf8) + body
    }

    /// Everything the server is sent, in order: open the project and the file, ask the question, then leave. A
    /// document-symbols request before a diagnostics query makes the server finish reading the file first.
    static func conversation(root: URL, file: URL, text: String, languageId: String, line: Int, column: Int, mode: String) -> Data {
        let uri = file.absoluteString
        let position: [String: Any] = ["textDocument": ["uri": uri], "position": ["line": line - 1, "character": column - 1]]
        var messages: [[String: Any]] = [
            ["jsonrpc": "2.0", "id": 1, "method": "initialize", "params": ["processId": NSNull(), "rootUri": root.absoluteString, "capabilities": ["textDocument": ["publishDiagnostics": [:] as [String: Any]]],
                                                                             "workspaceFolders": [["uri": root.absoluteString, "name": root.lastPathComponent]]] as [String: Any]],
            ["jsonrpc": "2.0", "method": "initialized", "params": [:] as [String: Any]],
            ["jsonrpc": "2.0", "method": "textDocument/didOpen", "params": ["textDocument": ["uri": uri, "languageId": languageId, "version": 1, "text": text] as [String: Any]]],
        ]
        switch mode {
        case "definitions": messages.append(["jsonrpc": "2.0", "id": 2, "method": "textDocument/definition", "params": position])
        case "references":
            var params = position
            params["context"] = ["includeDeclaration": true]
            messages.append(["jsonrpc": "2.0", "id": 2, "method": "textDocument/references", "params": params])
        default: messages.append(["jsonrpc": "2.0", "id": 2, "method": "textDocument/documentSymbol", "params": ["textDocument": ["uri": uri]]])
        }
        messages.append(["jsonrpc": "2.0", "id": 3, "method": "shutdown"])
        messages.append(["jsonrpc": "2.0", "method": "exit"])
        return messages.reduce(Data()) { $0 + frame($1) }
    }

    /// The messages in what the server wrote back.
    static func messages(in output: Data) -> [[String: Any]] {
        var found: [[String: Any]] = [], rest = output[...]
        let marker = Data("Content-Length:".utf8), blank = Data("\r\n\r\n".utf8)
        while let header = rest.range(of: marker), let end = rest.range(of: blank, in: header.upperBound..<rest.endIndex) {
            let length = Int(String(decoding: rest[header.upperBound..<end.lowerBound], as: UTF8.self).split(separator: "\r").first.map { $0.trimmingCharacters(in: .whitespaces) } ?? "") ?? 0
            guard length > 0, rest.distance(from: end.upperBound, to: rest.endIndex) >= length else { break }
            let body = rest[end.upperBound..<rest.index(end.upperBound, offsetBy: length)]
            if let object = try? JSONSerialization.jsonObject(with: body) as? [String: Any] { found.append(object) }
            rest = rest[rest.index(end.upperBound, offsetBy: length)...]
        }
        return found
    }

    /// A file URI as a path relative to the project, or nil when it is outside it.
    static func relative(_ uri: String, root: URL) -> String? {
        guard let url = URL(string: uri), url.isFileURL else { return nil }
        let path = url.resolvingSymlinksInPath().path, prefix = root.resolvingSymlinksInPath().path + "/"
        return path.hasPrefix(prefix) ? String(path.dropFirst(prefix.count)) : nil
    }

    /// The server's answer as the model reads it: places as path:line:column with the line's text, or diagnostics.
    static func report(_ replies: [[String: Any]], mode: String, server: Server, workspace: Workspace, file: String) -> String {
        let root = workspace.root
        var lines: [String] = []
        if mode == "diagnostics" {
            for message in replies where message["method"] as? String == "textDocument/publishDiagnostics" {
                guard let params = message["params"] as? [String: Any], let path = relative(params["uri"] as? String ?? "", root: root) else { continue }
                for item in params["diagnostics"] as? [[String: Any]] ?? [] {
                    let start = (item["range"] as? [String: Any])?["start"] as? [String: Any]
                    let severity = ["", "error", "warning", "information", "hint"][min(4, max(0, item["severity"] as? Int ?? 0))]
                    let entry = "\(path):\((start?["line"] as? Int ?? 0) + 1):\((start?["character"] as? Int ?? 0) + 1)  \(severity)  \((item["message"] as? String ?? "").replacingOccurrences(of: "\n", with: " "))"
                    if !lines.contains(entry) { lines.append(entry) }
                }
            }
            return "\(server.name): \(lines.count) diagnostic\(lines.count == 1 ? "" : "s") for \(file)." + (lines.isEmpty ? "" : "\n" + lines.prefix(150).joined(separator: "\n"))
        }
        guard let answer = replies.first(where: { ($0["id"] as? Int) == 2 }) else { return "\(server.name) did not answer. It may still have been reading the project; try again." }
        if let error = answer["error"] as? [String: Any] { return "\(server.name) could not answer: \(error["message"] as? String ?? "unknown error")" }
        var places = answer["result"] as? [[String: Any]] ?? []
        if let single = answer["result"] as? [String: Any] { places = [single] }
        var outside = 0
        for place in places.prefix(150) {
            // A location, or a location link whose target is the place.
            let uri = place["uri"] as? String ?? place["targetUri"] as? String ?? ""
            let range = (place["range"] ?? place["targetSelectionRange"] ?? place["targetRange"]) as? [String: Any]
            let start = range?["start"] as? [String: Any]
            guard let path = relative(uri, root: root) else { outside += 1; continue }
            let line = (start?["line"] as? Int ?? 0) + 1
            let source = ((try? workspace.read(path)) ?? nil)?.components(separatedBy: "\n")
            let text = source.flatMap { line <= $0.count ? $0[line - 1].trimmingCharacters(in: .whitespaces) : nil } ?? ""
            lines.append("\(path):\(line):\((start?["character"] as? Int ?? 0) + 1)  \(text.prefix(200))")
        }
        var text = "\(server.name): \(lines.count) \(mode) in the project" + (outside > 0 ? ", and \(outside) outside it (libraries)" : "") + "."
        if !lines.isEmpty { text += "\n" + lines.joined(separator: "\n") }
        return text
    }

    /// Asks the server about one position. The server runs as a command does: `sandboxed` puts it in the sandbox.
    public static func query(_ workspace: Workspace, server: Server, path: String, line: Int, column: Int, mode: String, sandboxed: Bool) async throws -> String {
        guard ["definitions", "references", "diagnostics"].contains(mode), line > 0, column > 0 else { throw MasonError("Use definitions, references or diagnostics and a 1-based line and column.") }
        guard let data = try workspace.data(path), let text = String(data: data, encoding: .utf8) else { throw MasonError("\(path) is not a text file in the project.") }
        // The request travels on the command line, which holds only so much.
        guard data.count <= 60_000 else { throw MasonError("\(path) is too large to hand to the language server here (60 KB at most).") }
        let file = try workspace.resolve(path)
        let request = conversation(root: workspace.root, file: file, text: text, languageId: server.languageId, line: line, column: column, mode: mode)
        // Diagnostics are published a moment after the file is read, so the server is given that moment before it is told to leave.
        let closing = frame(["jsonrpc": "2.0", "id": 3, "method": "shutdown"]) + frame(["jsonrpc": "2.0", "method": "exit"])
        let opening = request.prefix(request.count - closing.count)
        func piece(_ data: Data) -> String { "printf %s " + ProjectWorkflows.quoted(data.base64EncodedString()) + " | base64 --decode" }
        let input = mode == "diagnostics" ? "{ " + piece(Data(opening)) + "; sleep 3; " + piece(closing) + "; }" : piece(request)
        let command = input + " | " + server.command.map(ProjectWorkflows.quoted).joined(separator: " ") + " 2>/dev/null"
        let result = try await Shell.run(command, in: workspace, sandboxed: sandboxed, timeout: 90)
        let replies = messages(in: Data(result.output.utf8))
        guard !replies.isEmpty else { throw MasonError(result.timedOut ? "\(server.name) did not finish within 90 seconds." : "\(server.name) did not answer (exit \(result.exitCode)).") }
        return report(replies, mode: mode, server: server, workspace: workspace, file: path)
    }
}
