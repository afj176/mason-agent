import Foundation

// Finding where a name is defined and used. `navigate_code` reads the project's source as text and labels what it
// finds as candidates. `inspect_symbol` asks a real language service about one position: Jedi for Python, the
// TypeScript compiler for JavaScript and TypeScript. Both run on a copy of the source and never run project code.

public enum CodeNavigation {
    static let extensions: Set<String> = ["cs", "py", "js", "jsx", "mjs", "cjs", "ts", "tsx", "go", "rb", "dart", "swift", "c", "h", "cc", "cpp", "hpp", "m", "mm", "java", "kt", "rs", "php", "scala"]
    static let limit = 150

    public struct Hit: Equatable {
        public let path: String
        public let line: Int
        public let kind: String
        public let text: String
    }

    static func isTest(_ path: String) -> Bool {
        path.range(of: #"(?:^|[/._-])tests?(?:[/._-]|$)|(?:Tests?|Checks|_test|\.spec|\.test)\.\w+$"#, options: [.regularExpression, .caseInsensitive]) != nil
    }

    /// Lines of the project's source that define, use or test `symbol`. These are text matches on whole words, not a
    /// compiler's answer, and are labelled so.
    public static func query(_ workspace: Workspace, directory: String, symbol: String, mode: String) throws -> (hits: [Hit], files: Int, partial: Bool) {
        guard ["definitions", "references", "tests"].contains(mode), symbol.count <= 100, symbol.range(of: #"^[\p{L}_][\p{L}\p{N}_]*$"#, options: .regularExpression) != nil else {
            throw MasonError("Choose definitions, references or tests, and a single identifier.")
        }
        let escaped = NSRegularExpression.escapedPattern(for: symbol)
        let word = try NSRegularExpression(pattern: "(?<![\\p{L}\\p{N}_])" + escaped + "(?![\\p{L}\\p{N}_])")
        let declaration = try NSRegularExpression(pattern: "\\b(?:class|interface|enum|def|func|function|fn|type|typealias|const|let|var|val|struct|protocol|trait|impl|module|namespace|record)\\s+" + escaped + "(?![\\p{L}\\p{N}_])"
                                                  + "|^[\\w:<>,*&\\s\\[\\]]+[\\s*&]" + escaped + "\\s*\\([^;]*\\)\\s*(?:const\\s*)?\\{?\\s*$")
        let listing = try workspace.files(under: directory.isEmpty ? "." : directory, limit: 4000)
        var hits: [Hit] = [], files = 0, total = 0, partial = listing.truncated
        for path in listing.paths where extensions.contains((path as NSString).pathExtension.lowercased()) {
            try Task.checkCancellation()
            guard hits.count < limit, total <= 24 * 1024 * 1024 else { partial = true; break }
            guard let data = try? workspace.data(path), data.count <= 2 * 1024 * 1024, let content = String(data: data, encoding: .utf8) else { continue }
            files += 1; total += data.count
            guard content.contains(symbol) else { continue }
            let test = isTest(path)
            for (index, line) in content.components(separatedBy: "\n").enumerated() where hits.count < limit {
                let range = NSRange(line.startIndex..., in: line)
                guard word.firstMatch(in: line, range: range) != nil else { continue }
                let trimmed = line.trimmingCharacters(in: .whitespaces)
                if trimmed.hasPrefix("//") || trimmed.hasPrefix("#") || trimmed.hasPrefix("*") { continue }
                let declared = declaration.firstMatch(in: line, range: range) != nil
                if mode == "definitions" ? !declared : mode == "tests" ? !test : declared { continue }
                hits.append(Hit(path: path, line: index + 1, kind: mode == "definitions" ? "definition candidate" : test ? "test reference candidate" : "reference candidate", text: String(trimmed.prefix(320))))
            }
        }
        return (hits, files, partial || hits.count >= limit)
    }

    /// The result as the model reads it.
    public static func report(_ result: (hits: [Hit], files: Int, partial: Bool), symbol: String, mode: String) -> String {
        var lines = ["\(result.hits.count) \(mode) of \(symbol) in \(result.files) source files\(result.partial ? " (not the whole project)" : ""). These are whole-word text matches, not a compiler's answer; read the lines before relying on them."]
        lines += result.hits.map { "\($0.path):\($0.line)  [\($0.kind)]  \($0.text)" }
        if result.hits.isEmpty { lines.append("Nothing matched. Check the spelling, or search with search_text.") }
        return lines.joined(separator: "\n")
    }
}

/// Jedi and the TypeScript compiler, run as helper programs on a copy of the project's source.
public enum SymbolService {
    /// The folder holding python-symbols.py and typescript-symbols.cjs: beside the app or command line as installed, or `MASON_LANGUAGE_TOOLS`.
    public static func folder() -> URL? {
        var candidates: [URL] = []
        if let named = ProcessInfo.processInfo.environment["MASON_LANGUAGE_TOOLS"], !named.isEmpty { candidates.append(URL(fileURLWithPath: named)) }
        if let resources = Bundle.main.resourceURL { candidates.append(resources.appendingPathComponent("language-tools")) }
        let program = (Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments.first ?? "")).resolvingSymlinksInPath().deletingLastPathComponent()
        candidates.append(program.deletingLastPathComponent().appendingPathComponent("language-tools"))
        candidates.append(program.deletingLastPathComponent().appendingPathComponent("share/mason-agent/language-tools"))
        return candidates.first { FileManager.default.fileExists(atPath: $0.appendingPathComponent("python-symbols.py").path) }
    }

    static func onPath(_ name: String) -> String? {
        let path = (ProcessInfo.processInfo.environment["PATH"] ?? "") + ":/opt/homebrew/bin:/usr/local/bin:/usr/bin"
        return path.split(separator: ":").map { $0 + "/" + name }.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static let python: Set<String> = ["py", "pyi"]
    static let script: Set<String> = ["ts", "tsx", "js", "jsx", "mts", "cts", "mjs", "cjs"]

    /// The program and arguments that answer for a file of this kind, or nil when what it needs is not on this computer.
    static func helper(for kind: String) -> [String]? {
        guard let folder = folder() else { return nil }
        if python.contains(kind) {
            // Jedi ships beside the helper; without it there is nothing to ask.
            guard let program = TransformersRuntime.python(), FileManager.default.fileExists(atPath: folder.appendingPathComponent("python/jedi").path) else { return nil }
            return [program, "-I", "-B", folder.appendingPathComponent("python-symbols.py").path]
        }
        if script.contains(kind) {
            guard let node = onPath("node"), FileManager.default.fileExists(atPath: folder.appendingPathComponent("typescript/typescript.js").path) else { return nil }
            return [node, folder.appendingPathComponent("typescript-symbols.cjs").path]
        }
        return nil
    }

    /// True when at least one language can be answered here, so the tool is worth offering.
    public static var available: Bool { helper(for: "py") != nil || helper(for: "ts") != nil }

    /// Definitions, references or diagnostics at a 1-based position, as the helper's JSON.
    public static func query(_ workspace: Workspace, path: String, line: Int, column: Int, mode: String) async throws -> String {
        guard ["definitions", "references", "diagnostics"].contains(mode), line > 0, column > 0 else { throw MasonError("Use definitions, references or diagnostics and a 1-based line and column.") }
        let kind = (path as NSString).pathExtension.lowercased()
        let family = python.contains(kind) ? python : script.contains(kind) ? script : []
        guard !family.isEmpty else { throw MasonError("inspect_symbol answers for Python, JavaScript and TypeScript files.") }
        guard let command = helper(for: kind) else {
            throw MasonError(python.contains(kind) ? "The Python language service is not available on this computer: it needs python3 and the Jedi that ships with Mason."
                                                   : "The TypeScript language service is not available on this computer: it needs Node.js (node on the PATH) and the TypeScript that ships with Mason.")
        }
        guard let main = try workspace.data(path), let source = String(data: main, encoding: .utf8) else { throw MasonError("\(path) is not a text file in the project.") }
        let prefix = workspace.root.path.hasSuffix("/") ? workspace.root.path : workspace.root.path + "/"
        let resolved = try workspace.resolve(path).path
        let relative = resolved.hasPrefix(prefix) ? String(resolved.dropFirst(prefix.count)) : path
        var files = [relative: source], size = source.count
        let listing = try workspace.files(limit: 1000)
        var partial = listing.truncated
        for other in listing.paths where other != relative && family.contains((other as NSString).pathExtension.lowercased()) {
            guard size <= 4_000_000, files.count < 400 else { partial = true; break }
            guard let data = try? workspace.data(other), let text = String(data: data, encoding: .utf8) else { partial = true; continue }
            files[other] = text; size += text.count
        }
        let input = try JSONSerialization.data(withJSONObject: ["path": relative, "line": line, "column": column, "mode": mode, "files": files, "partial": partial])
        return try await run(command, input: input)
    }

    /// Runs a helper with the request on its standard input and returns what it printed. It gets thirty seconds.
    static func run(_ command: [String], input: Data) async throws -> String {
        let process = Process(), stdin = Pipe(), stdout = Pipe()
        process.executableURL = URL(fileURLWithPath: command[0])
        process.arguments = Array(command.dropFirst())
        process.currentDirectoryURL = URL(fileURLWithPath: command.last ?? "/").deletingLastPathComponent()
        process.standardInput = stdin; process.standardOutput = stdout; process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { throw MasonError("The language service could not start: \(error.localizedDescription)") }
        // The request can be megabytes; it is written and the answer read on other threads so neither pipe fills up.
        DispatchQueue.global().async { try? stdin.fileHandleForWriting.write(contentsOf: input); try? stdin.fileHandleForWriting.close() }
        let answer = Task.detached { stdout.fileHandleForReading.readDataToEndOfFile() }
        let deadline = Date().addingTimeInterval(30)
        while process.isRunning {
            if Task.isCancelled || Date() > deadline { process.terminate(); if Task.isCancelled { throw CancellationError() }; throw MasonError("The language service did not answer in thirty seconds.") }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        let text = String(decoding: await answer.value, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        guard process.terminationStatus == 0, !text.isEmpty else { throw MasonError("The language service could not answer for this file.") }
        return String(text.prefix(40_000))
    }
}
