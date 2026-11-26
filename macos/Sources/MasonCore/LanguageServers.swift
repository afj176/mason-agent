import Foundation

// Mason speaks the Language Server Protocol to the language servers on this computer, the way editors do. A server
// is started the first time a request touches a file of its language and stays running until the request ends.
// After each edit the file is handed to it, and the errors it reports go back to the model with the edit's result.
// `inspect_symbol` asks the same server where a name is defined or used.
//
// A server is a program that reads the project, and some run the project's build, so it is started the way a command
// is: inside the sandbox in Auto, and after asking otherwise. Mason ships none of them. `/languages` shows which are
// installed and installs the ones a language's own tools can fetch.

public enum LanguageServers {
    public struct Server: Equatable {
        public let name: String
        public let command: [String]
        public let languageId: String
    }

    /// How a server that is missing is got.
    public enum Install: Equatable {
        case npm([String])
        case go(String)
        case dotnet(String)
        case rustup(String)
        case gem(String)
        /// Nothing Mason can run: what the user installs themselves.
        case hint(String)
    }

    public struct Language {
        public let key: String
        public let name: String
        public let extensions: [String]
        public let languageId: String
        public let candidates: [[String]]
        public let install: Install
        /// Extensions whose language id differs from the entry's.
        var others: [String: String] = [:]
    }

    static func packaged(_ brew: String, _ what: String) -> Install {
        #if os(macOS)
        return .hint("Install \(what), for example with `brew install \(brew)`.")
        #else
        return .hint("Install \(what) with your package manager.")
        #endif
    }

    public static let known: [Language] = [
        Language(key: "csharp", name: "C#", extensions: ["cs"], languageId: "csharp", candidates: [["csharp-ls"], ["OmniSharp", "-lsp"], ["omnisharp", "-lsp"]], install: .dotnet("csharp-ls")),
        Language(key: "fsharp", name: "F#", extensions: ["fs", "fsi", "fsx"], languageId: "fsharp", candidates: [["fsautocomplete"]], install: .dotnet("fsautocomplete")),
        Language(key: "swift", name: "Swift", extensions: ["swift"], languageId: "swift", candidates: [["sourcekit-lsp"]], install: .hint("sourcekit-lsp comes with Swift: install Xcode or the Swift toolchain.")),
        Language(key: "c", name: "C", extensions: ["c", "h"], languageId: "c", candidates: [["clangd"]], install: packaged("llvm", "clangd")),
        Language(key: "cpp", name: "C++ and Objective-C", extensions: ["cc", "cpp", "cxx", "hpp", "hh", "hxx", "m", "mm"], languageId: "cpp", candidates: [["clangd"]], install: packaged("llvm", "clangd"),
                 others: ["m": "objective-c", "mm": "objective-cpp"]),
        Language(key: "go", name: "Go", extensions: ["go"], languageId: "go", candidates: [["gopls"]], install: .go("golang.org/x/tools/gopls@latest")),
        Language(key: "rust", name: "Rust", extensions: ["rs"], languageId: "rust", candidates: [["rust-analyzer"]], install: .rustup("rust-analyzer")),
        Language(key: "python", name: "Python", extensions: ["py", "pyi"], languageId: "python", candidates: [["pyright-langserver", "--stdio"], ["basedpyright-langserver", "--stdio"]], install: .npm(["pyright"])),
        Language(key: "java", name: "Java", extensions: ["java"], languageId: "java", candidates: [["jdtls"]], install: packaged("jdtls", "the Eclipse JDT language server (jdtls) and a JDK")),
        Language(key: "kotlin", name: "Kotlin", extensions: ["kt", "kts"], languageId: "kotlin", candidates: [["kotlin-lsp", "--stdio"], ["kotlin-language-server"]], install: packaged("kotlin-language-server", "kotlin-language-server")),
        Language(key: "php", name: "PHP", extensions: ["php"], languageId: "php", candidates: [["intelephense", "--stdio"], ["phpactor", "language-server"]], install: .npm(["intelephense"])),
        Language(key: "ruby", name: "Ruby", extensions: ["rb", "rake", "gemspec"], languageId: "ruby", candidates: [["ruby-lsp"], ["solargraph", "stdio"]], install: .gem("ruby-lsp")),
        Language(key: "lua", name: "Lua", extensions: ["lua"], languageId: "lua", candidates: [["lua-language-server"]], install: packaged("lua-language-server", "lua-language-server")),
        Language(key: "zig", name: "Zig", extensions: ["zig"], languageId: "zig", candidates: [["zls"]], install: packaged("zls", "zls")),
        Language(key: "dart", name: "Dart", extensions: ["dart"], languageId: "dart", candidates: [["dart", "language-server", "--protocol=lsp"]], install: .hint("The Dart language server comes with the Dart or Flutter SDK.")),
        Language(key: "elixir", name: "Elixir", extensions: ["ex", "exs"], languageId: "elixir", candidates: [["elixir-ls"], ["language_server.sh"]], install: packaged("elixir-ls", "elixir-ls")),
        Language(key: "erlang", name: "Erlang", extensions: ["erl", "hrl"], languageId: "erlang", candidates: [["elp", "server"], ["erlang_ls"]], install: packaged("erlang_ls", "erlang_ls")),
        Language(key: "haskell", name: "Haskell", extensions: ["hs", "lhs"], languageId: "haskell", candidates: [["haskell-language-server-wrapper", "--lsp"]], install: .hint("Install haskell-language-server with ghcup: `ghcup install hls`.")),
        Language(key: "ocaml", name: "OCaml", extensions: ["ml", "mli"], languageId: "ocaml", candidates: [["ocamllsp"]], install: .hint("Install it with opam: `opam install ocaml-lsp-server`, and start Mason from a shell where `ocamllsp` is on the PATH.")),
        Language(key: "scala", name: "Scala", extensions: ["scala", "sbt", "sc"], languageId: "scala", candidates: [["metals"]], install: .hint("Install Metals with Coursier: `cs install metals`.")),
        Language(key: "clojure", name: "Clojure", extensions: ["clj", "cljs", "cljc", "edn"], languageId: "clojure", candidates: [["clojure-lsp"]], install: packaged("clojure-lsp/brew/clojure-lsp-native", "clojure-lsp")),
        Language(key: "shell", name: "Shell scripts", extensions: ["sh", "bash"], languageId: "shellscript", candidates: [["bash-language-server", "start"]], install: .npm(["bash-language-server"])),
        Language(key: "yaml", name: "YAML", extensions: ["yaml", "yml"], languageId: "yaml", candidates: [["yaml-language-server", "--stdio"]], install: .npm(["yaml-language-server"])),
        Language(key: "json", name: "JSON", extensions: ["json", "jsonc"], languageId: "json", candidates: [["vscode-json-language-server", "--stdio"]], install: .npm(["vscode-langservers-extracted"]), others: ["jsonc": "jsonc"]),
        Language(key: "html", name: "HTML", extensions: ["html", "htm"], languageId: "html", candidates: [["vscode-html-language-server", "--stdio"]], install: .npm(["vscode-langservers-extracted"])),
        Language(key: "css", name: "CSS, SCSS and Less", extensions: ["css", "scss", "less"], languageId: "css", candidates: [["vscode-css-language-server", "--stdio"]], install: .npm(["vscode-langservers-extracted"]),
                 others: ["scss": "scss", "less": "less"]),
        Language(key: "svelte", name: "Svelte", extensions: ["svelte"], languageId: "svelte", candidates: [["svelteserver", "--stdio"]], install: .npm(["svelte-language-server"])),
        Language(key: "docker", name: "Dockerfiles", extensions: ["dockerfile"], languageId: "dockerfile", candidates: [["docker-langserver", "--stdio"]], install: .npm(["dockerfile-language-server-nodejs"])),
        Language(key: "terraform", name: "Terraform", extensions: ["tf", "tfvars"], languageId: "terraform", candidates: [["terraform-ls", "serve"]], install: packaged("hashicorp/tap/terraform-ls", "terraform-ls"),
                 others: ["tfvars": "terraform-vars"]),
        Language(key: "nix", name: "Nix", extensions: ["nix"], languageId: "nix", candidates: [["nixd"], ["nil"]], install: .hint("Install nixd or nil, for example with `nix profile install nixpkgs#nixd`.")),
        Language(key: "toml", name: "TOML", extensions: ["toml"], languageId: "toml", candidates: [["taplo", "lsp", "stdio"]], install: packaged("taplo", "taplo")),
        Language(key: "latex", name: "LaTeX", extensions: ["tex", "bib"], languageId: "latex", candidates: [["texlab"]], install: packaged("texlab", "texlab"), others: ["bib": "bibtex"]),
        Language(key: "typst", name: "Typst", extensions: ["typ"], languageId: "typst", candidates: [["tinymist"]], install: packaged("tinymist", "tinymist")),
        Language(key: "gleam", name: "Gleam", extensions: ["gleam"], languageId: "gleam", candidates: [["gleam", "lsp"]], install: .hint("The Gleam language server comes with Gleam.")),
        Language(key: "r", name: "R", extensions: ["r", "rmd"], languageId: "r", candidates: [["R", "--no-echo", "-e", "languageserver::run()"]], install: .hint("In R, run `install.packages(\"languageserver\")`."), others: ["rmd": "rmd"]),
    ]

    /// Languages whose missing server is not mentioned to the model: data and markup, where a note with every request would be noise.
    static let quiet: Set<String> = ["json", "yaml", "toml", "html", "css", "docker", "latex", "typst"]

    /// What a path's language is looked up by: its extension in lower case, or `dockerfile` for a Dockerfile.
    public static func kind(of path: String) -> String {
        let name = (path as NSString).lastPathComponent.lowercased()
        if name == "dockerfile" || name.hasPrefix("dockerfile.") || name == "containerfile" { return "dockerfile" }
        return (name as NSString).pathExtension
    }

    public static func language(for kind: String) -> Language? { known.first { $0.extensions.contains(kind) } }

    /// Where Mason puts the servers it installs.
    public static func folder(home: URL = DataHome.url) -> URL { home.appendingPathComponent("language-servers") }

    /// The folders a server's program is looked for in: the PATH, where Mason installs them, and where the
    /// languages' own tools put programs.
    static func searchFolders(home: URL) -> [String] {
        let own = folder(home: home).path, user = NSHomeDirectory()
        var folders = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
        folders += [own + "/bin", own + "/npm/node_modules/.bin", user + "/.cargo/bin", user + "/go/bin", user + "/.dotnet/tools", user + "/.local/bin"]
        folders += ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]
        return folders
    }

    static func find(_ name: String, home: URL = DataHome.url) -> String? {
        if name.contains("/") { return FileManager.default.isExecutableFile(atPath: name) ? name : nil }
        for folder in searchFolders(home: home) {
            let candidate = folder + "/" + name
            var directory: ObjCBool = false
            if FileManager.default.fileExists(atPath: candidate, isDirectory: &directory), !directory.boolValue, FileManager.default.isExecutableFile(atPath: candidate) { return candidate }
        }
        return nil
    }

    /// Servers the user names themselves, by extension: `language-servers.json` in Mason's data folder and the
    /// `MASON_LANGUAGE_SERVERS` variable, each as JSON such as {"zig": ["/opt/zls/zls"]}. The variable wins.
    static func overrides(home: URL) -> [String: [String]] {
        var all: [String: [String]] = [:]
        if let data = try? Data(contentsOf: home.appendingPathComponent("language-servers.json")), let object = try? JSONSerialization.jsonObject(with: data) as? [String: [String]] { all = object }
        if let text = ProcessInfo.processInfo.environment["MASON_LANGUAGE_SERVERS"], let object = try? JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: [String]] {
            for (kind, command) in object { all[kind] = command }
        }
        return all
    }

    /// The language server for a file of this kind, when one is installed.
    public static func server(for kind: String, home: URL = DataHome.url) -> Server? {
        let entry = language(for: kind)
        let id = entry?.others[kind] ?? entry?.languageId ?? kind
        if let named = overrides(home: home)[kind], let first = named.first, let program = find(first, home: home) {
            return Server(name: URL(fileURLWithPath: program).lastPathComponent, command: [program] + named.dropFirst(), languageId: id)
        }
        guard let entry else { return nil }
        for candidate in entry.candidates {
            if let program = find(candidate[0], home: home) { return Server(name: candidate[0], command: [program] + candidate.dropFirst(), languageId: id) }
        }
        return nil
    }

    /// True when some language server is installed, so the tool is worth offering.
    public static var available: Bool {
        if !overrides(home: DataHome.url).isEmpty { return true }
        return known.contains { $0.key != "python" && server(for: $0.extensions[0]) != nil }
    }

    // MARK: the wire

    static func frame(_ object: [String: Any]) -> Data {
        let body = (try? JSONSerialization.data(withJSONObject: object)) ?? Data()
        return Data("Content-Length: \(body.count)\r\n\r\n".utf8) + body
    }

    /// The whole messages at the start of what a server wrote, and how many bytes they took. A message still
    /// arriving is left for the next call.
    static func frames(in output: Data) -> (messages: [[String: Any]], used: Int) {
        var found: [[String: Any]] = [], rest = output[...]
        let marker = Data("Content-Length:".utf8), blank = Data("\r\n\r\n".utf8)
        while let header = rest.range(of: marker), let end = rest.range(of: blank, in: header.upperBound..<rest.endIndex) {
            let length = Int(String(decoding: rest[header.upperBound..<end.lowerBound], as: UTF8.self).split(separator: "\r").first.map { $0.trimmingCharacters(in: .whitespaces) } ?? "") ?? 0
            guard length > 0 else { rest = rest[end.upperBound...]; continue }
            guard rest.distance(from: end.upperBound, to: rest.endIndex) >= length else { break }
            let stop = rest.index(end.upperBound, offsetBy: length)
            if let object = try? JSONSerialization.jsonObject(with: rest[end.upperBound..<stop]) as? [String: Any] { found.append(object) }
            rest = rest[stop...]
        }
        return (found, output.count - rest.count)
    }

    /// The messages in what the server wrote back.
    static func messages(in output: Data) -> [[String: Any]] { frames(in: output).messages }

    /// The file a URI names, with links resolved, so the same file always has the same key.
    static func path(of uri: String) -> String? {
        guard let url = URL(string: uri), url.isFileURL else { return nil }
        return url.resolvingSymlinksInPath().path
    }

    /// A file URI as a path relative to the project, or nil when it is outside it.
    static func relative(_ uri: String, root: URL) -> String? {
        guard let path = path(of: uri) else { return nil }
        let prefix = root.resolvingSymlinksInPath().path + "/"
        return path.hasPrefix(prefix) ? String(path.dropFirst(prefix.count)) : nil
    }

    static let severities = ["", "error", "warning", "information", "hint"]

    /// One diagnostic as a line: path:line:column, how serious, and the message.
    static func line(_ item: [String: Any], path: String) -> String {
        let start = (item["range"] as? [String: Any])?["start"] as? [String: Any]
        let severity = severities[min(4, max(0, item["severity"] as? Int ?? 0))]
        let message = (item["message"] as? String ?? "").replacingOccurrences(of: "\n", with: " ")
        return "\(path):\((start?["line"] as? Int ?? 0) + 1):\((start?["character"] as? Int ?? 0) + 1)  \(severity)  \(message)"
    }

    /// A file's diagnostics as the model reads them.
    static func report(diagnostics items: [[String: Any]], server: Server, file: String) -> String {
        var lines: [String] = []
        for item in items {
            let entry = line(item, path: file)
            if !lines.contains(entry) { lines.append(entry) }
        }
        return "\(server.name): \(lines.count) diagnostic\(lines.count == 1 ? "" : "s") for \(file)." + (lines.isEmpty ? "" : "\n" + lines.prefix(150).joined(separator: "\n"))
    }

    /// The answer to a definition or references request as the model reads it: places as path:line:column with the line's text.
    static func report(answer: [String: Any]?, mode: String, server: Server, workspace: Workspace) -> String {
        guard let answer else { return "\(server.name) did not answer. It may still have been reading the project; try again." }
        if let error = answer["error"] as? [String: Any] { return "\(server.name) could not answer: \(error["message"] as? String ?? "unknown error")" }
        var places = answer["result"] as? [[String: Any]] ?? []
        if let single = answer["result"] as? [String: Any] { places = [single] }
        var outside = 0
        var lines: [String] = []
        for place in places.prefix(150) {
            // A location, or a location link whose target is the place.
            let uri = place["uri"] as? String ?? place["targetUri"] as? String ?? ""
            let range = (place["range"] ?? place["targetSelectionRange"] ?? place["targetRange"]) as? [String: Any]
            let start = range?["start"] as? [String: Any]
            guard let path = relative(uri, root: workspace.root) else { outside += 1; continue }
            let line = (start?["line"] as? Int ?? 0) + 1
            let source = ((try? workspace.read(path)) ?? nil)?.components(separatedBy: "\n")
            let text = source.flatMap { line <= $0.count ? $0[line - 1].trimmingCharacters(in: .whitespaces) : nil } ?? ""
            lines.append("\(path):\(line):\((start?["character"] as? Int ?? 0) + 1)  \(text.prefix(200))")
        }
        var text = "\(server.name): \(lines.count) \(mode) in the project"
        if outside > 0 { text += ", and \(outside) outside it (libraries)" }
        text += "."
        if !lines.isEmpty { text += "\n" + lines.joined(separator: "\n") }
        return text
    }

    /// Asks a server about one position, starting it if this request has not yet. The server runs as a command
    /// does: `sandboxed` puts it in the sandbox.
    static func query(_ workspace: Workspace, server: Server, path: String, line: Int, column: Int, mode: String, sandboxed: Bool, sessions: LanguageSessions) async throws -> String {
        guard ["definitions", "references", "diagnostics"].contains(mode), line > 0, column > 0 else { throw MasonError("Use definitions, references or diagnostics and a 1-based line and column.") }
        guard let data = try workspace.data(path), let text = String(data: data, encoding: .utf8) else { throw MasonError("\(path) is not a text file in the project.") }
        guard data.count <= 1_000_000 else { throw MasonError("\(path) is too large to hand to the language server (1 MB at most).") }
        let file = try workspace.resolve(path)
        let (session, fresh) = try await sessions.session(server, workspace: workspace, sandboxed: sandboxed)
        let before = session.sync(file, text: text, languageId: server.languageId)
        if mode == "diagnostics" {
            let items = await session.diagnostics(for: file, after: before, wait: fresh ? 20 : 8)
            guard let items else { return "\(server.name) reported nothing for \(path) in time. It may still be reading the project; try again." }
            return report(diagnostics: items, server: server, file: path)
        }
        var params: [String: Any] = ["textDocument": ["uri": file.absoluteString], "position": ["line": line - 1, "character": column - 1]]
        if mode == "references" { params["context"] = ["includeDeclaration": true] }
        let method = mode == "definitions" ? "textDocument/definition" : "textDocument/references"
        var answer = await session.request(method, params, wait: 60)
        // A server that has only just started may answer with nothing while it is still reading the project.
        if fresh, let first = answer, first["error"] == nil, (first["result"] as? [Any])?.isEmpty ?? (first["result"] is NSNull) {
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            answer = await session.request(method, params, wait: 60)
        }
        return report(answer: answer, mode: mode, server: server, workspace: workspace)
    }

    /// One question to a server started for it and stopped after: what `mason symbol` uses.
    public static func query(_ workspace: Workspace, server: Server, path: String, line: Int, column: Int, mode: String, sandboxed: Bool) async throws -> String {
        let sessions = LanguageSessions()
        do {
            let answer = try await query(workspace, server: server, path: path, line: line, column: column, mode: mode, sandboxed: sandboxed, sessions: sessions)
            await sessions.stopAll()
            return answer
        } catch {
            await sessions.stopAll()
            throw error
        }
    }

    /// The errors a server reports for a file that was just written, as a note for the model, or nil when it
    /// reports none or does not answer in time.
    static func errors(_ workspace: Workspace, server: Server, path: String, text: String, sandboxed: Bool, sessions: LanguageSessions) async throws -> String? {
        guard text.utf8.count <= 1_000_000 else { return nil }
        let file = try workspace.resolve(path)
        let (session, fresh) = try await sessions.session(server, workspace: workspace, sandboxed: sandboxed)
        let before = session.sync(file, text: text, languageId: server.languageId)
        guard let items = await session.diagnostics(for: file, after: before, wait: fresh ? 15 : 6) else { return nil }
        var lines: [String] = []
        for item in items where (item["severity"] as? Int ?? 1) == 1 {
            let entry = line(item, path: path)
            if !lines.contains(entry) { lines.append(entry) }
        }
        return note(lines, from: server.name, path: path)
    }

    static func note(_ lines: [String], from name: String, path: String) -> String? {
        guard !lines.isEmpty else { return nil }
        var text = "\n\(name) reports \(lines.count) error\(lines.count == 1 ? "" : "s") in \(path) after this edit. Fix them unless they are what you intend:\n"
        text += lines.prefix(20).joined(separator: "\n")
        if lines.count > 20 { text += "\n[\(lines.count - 20) more.]" }
        return text
    }

    /// What Jedi or the TypeScript compiler, which ship with Mason, report for a file that was just written.
    static func builtInErrors(_ workspace: Workspace, path: String) async -> String? {
        guard let answer = try? await SymbolService.query(workspace, path: path, line: 1, column: 1, mode: "diagnostics"),
              let object = try? JSONSerialization.jsonObject(with: Data(answer.utf8)) as? [String: Any] else { return nil }
        var lines: [String] = []
        for item in object["results"] as? [[String: Any]] ?? [] where (item["file"] as? String ?? path) == path {
            let message = (item["message"] as? String ?? "").replacingOccurrences(of: "\n", with: " ")
            lines.append("\(path):\(item["line"] as? Int ?? 1):\(item["column"] as? Int ?? 1)  error  \(message)")
        }
        let engine = (object["engine"] as? String ?? "The language service").split(separator: " ").first.map(String.init) ?? "The language service"
        return note(lines, from: engine, path: path)
    }

    // MARK: installing

    /// What installing a language's server would run, or nil when Mason cannot install it.
    static func installer(_ language: Language, home: URL) -> (needs: String, arguments: [String], environment: [String: String])? {
        let own = folder(home: home).path
        switch language.install {
        case .npm(let packages): return ("npm", ["install", "--prefix", own + "/npm", "--no-fund", "--no-audit", "--loglevel=error"] + packages, [:])
        case .go(let module): return ("go", ["install", module], ["GOBIN": own + "/bin"])
        case .dotnet(let tool): return ("dotnet", ["tool", "update", "--tool-path", own + "/bin", tool], [:])
        case .rustup(let component): return ("rustup", ["component", "add", component], [:])
        case .gem(let name): return ("gem", ["install", "--user-install", "--no-document", "--bindir", own + "/bin", name], [:])
        case .hint: return nil
        }
    }

    static let toolNames = ["npm": "Node.js (npm)", "go": "Go", "dotnet": "the .NET SDK", "rustup": "rustup", "gem": "Ruby (gem)"]

    /// Installs a language's server with the language's own tool. Nothing is installed without this being called.
    public static func install(_ language: Language, home: URL = DataHome.url) async throws -> String {
        guard let plan = installer(language, home: home) else {
            if case .hint(let text) = language.install { throw MasonError("Mason cannot install the \(language.name) server itself. \(text)") }
            throw MasonError("Mason cannot install the \(language.name) server itself.")
        }
        guard let program = find(plan.needs, home: home) else {
            throw MasonError("Installing the \(language.name) server needs \(toolNames[plan.needs] ?? plan.needs), which was not found on this computer.")
        }
        try FileManager.default.createDirectory(at: folder(home: home).appendingPathComponent("bin"), withIntermediateDirectories: true)
        let result = await runProgram([program] + plan.arguments, environment: plan.environment, timeout: 900)
        if let server = server(for: language.extensions[0], home: home), result.status == 0 { return "Installed \(server.name) for \(language.name)." }
        let tail = String(result.output.trimmingCharacters(in: .whitespacesAndNewlines).suffix(1500))
        throw MasonError("Installing the \(language.name) server did not work (\(plan.needs) exit \(result.status))." + (tail.isEmpty ? "" : "\n" + tail))
    }

    /// Runs a program with this account, outside the sandbox, and returns what it printed.
    static func runProgram(_ arguments: [String], environment: [String: String], timeout: TimeInterval) async -> (status: Int32, output: String) {
        let process = Process(), pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: arguments[0])
        process.arguments = Array(arguments.dropFirst())
        var all = ProcessInfo.processInfo.environment
        for (name, value) in environment { all[name] = value }
        process.environment = all
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = pipe
        process.standardError = pipe
        let collected = OutputBuffer(limit: 256 * 1024)
        pipe.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if !data.isEmpty { collected.append(data) }
        }
        let finished = AsyncSignal()
        process.terminationHandler = { _ in finished.fire() }
        do { try process.run() } catch { return (-1, "It could not start: \(error.localizedDescription)") }
        let deadline = Task {
            try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
            if !Task.isCancelled, process.isRunning { process.terminate() }
        }
        await finished.wait()
        deadline.cancel()
        pipe.fileHandleForReading.readabilityHandler = nil
        if let rest = try? pipe.fileHandleForReading.readToEnd(), !rest.isEmpty { collected.append(rest) }
        return (process.terminationStatus, collected.snapshot().0)
    }

    // MARK: the /languages command, shared by the command line and the app

    public static let help = """
        /languages                 The languages in this project and the language server for each
        /languages all             Every language Mason knows a server for
        /languages install NAME    Install a language's server with that language's own tool (npm, go, dotnet, rustup or gem)
        A server you installed yourself is found on the PATH. To name one for any file extension, put JSON such as
        {"zig": ["/opt/zls/zls"]} in language-servers.json in Mason's data folder.
        """

    /// One language's state: its server when installed, otherwise how to get one.
    public static func state(_ language: Language, home: URL = DataHome.url) -> (installed: String?, action: String) {
        if let server = server(for: language.extensions[0], home: home) { return (server.name, "") }
        // Python is still checked without a server, by the Jedi that ships with Mason.
        let before = language.key == "python" ? "syntax errors only, from the Jedi that ships with Mason. For type errors: " : ""
        if let plan = installer(language, home: home) {
            let tool = toolNames[plan.needs] ?? plan.needs
            return (nil, before + (find(plan.needs, home: home) == nil ? "needs \(tool) first, then /languages install \(language.key)" : "/languages install \(language.key) (uses \(plan.needs))"))
        }
        if case .hint(let text) = language.install { return (nil, text) }
        return (nil, "")
    }

    /// The same state in words for a settings row, and whether an Install button belongs beside it.
    public static func described(_ language: Language, home: URL = DataHome.url) -> (text: String, canInstall: Bool) {
        if let server = server(for: language.extensions[0], home: home) { return ("\(server.name), installed", false) }
        let before = language.key == "python" ? "Checked for syntax errors by the Jedi that ships with Mason. Pyright adds type errors. " : "Not installed. "
        if let plan = installer(language, home: home) {
            if find(plan.needs, home: home) != nil { return (language.key == "python" ? before.trimmingCharacters(in: .whitespaces) : "Not installed", true) }
            return (before + "Installing it needs \(toolNames[plan.needs] ?? plan.needs) first.", false)
        }
        if case .hint(let text) = language.install { return (before + text, false) }
        return ("Not installed", false)
    }

    /// The languages with files in the project, by the entry that covers them.
    public static func present(in workspace: Workspace) -> [Language] {
        let paths = (try? workspace.files(limit: 5000).paths) ?? []
        var kinds: Set<String> = []
        for path in paths { kinds.insert(kind(of: path)) }
        return known.filter { entry in entry.extensions.contains { kinds.contains($0) } }
    }

    public static func command(_ rest: String, workspace: Workspace?, home: URL = DataHome.url) async throws -> String {
        let words = rest.split(separator: " ").map(String.init)
        switch words.first ?? "" {
        case "help": return help
        case "install":
            guard words.count == 2, let language = known.first(where: { $0.key == words[1].lowercased() || $0.name.lowercased() == words[1].lowercased() }) else {
                throw MasonError("Use /languages install NAME, with a name from /languages all.")
            }
            return try await install(language, home: home)
        case "", "all":
            let everything = words.first == "all" || workspace == nil
            let listed = everything ? known : present(in: workspace!)
            var lines: [String] = []
            for language in listed {
                let state = state(language, home: home)
                var line = "\(language.name) [\(language.key)]  ·  "
                line += state.installed.map { "\($0), installed" } ?? ("not installed: " + state.action)
                lines.append(line)
            }
            if !everything {
                let scripts = workspace.map { present(scripts: $0) } ?? false
                if scripts { lines.append("TypeScript and JavaScript  ·  " + (SymbolService.helper(for: "ts") != nil ? "the TypeScript compiler that ships with Mason" : "needs Node.js (node on the PATH)")) }
            }
            for (kind, command) in overrides(home: home).sorted(by: { $0.key < $1.key }) { lines.append(".\(kind) files  ·  \(command.joined(separator: " ")), named by you") }
            if lines.isEmpty { return "No file in this project is in a language Mason knows a server for. /languages all lists them." }
            return lines.joined(separator: "\n") + (everything ? "" : "\nEdits to these files are checked by the installed servers. /languages all lists every language.")
        default: throw MasonError("Use /languages, /languages all or /languages install NAME.")
        }
    }

    static func present(scripts workspace: Workspace) -> Bool {
        let paths = (try? workspace.files(limit: 5000).paths) ?? []
        return paths.contains { SymbolService.script.contains(kind(of: $0)) }
    }
}

/// One running language server: what it has been sent, and what it has said.
final class LanguageSession: @unchecked Sendable {
    let server: LanguageServers.Server
    private let lock = NSLock(), writing = NSLock()
    private var buffer = Data()
    private var replies: [Int: [String: Any]] = [:]
    /// What the server last published for each file, and how many times it has published for it.
    private var published: [String: (count: Int, at: Date, items: [[String: Any]])] = [:]
    private var versions: [String: Int] = [:]
    private var texts: [String: String] = [:]
    private var nextId = 1
    private var input: FileHandle?
    private var task: Task<Void, Never>?
    private var ended = false
    /// True when the server answers a request for a file's diagnostics rather than only publishing them.
    private var pulls = false

    init(server: LanguageServers.Server) { self.server = server }

    private func locked<T>(_ body: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return body() }

    var hasEnded: Bool { locked { ended } }

    /// Starts the server in the project folder and greets it. Throws when it does not answer.
    func start(workspace: Workspace, sandboxed: Bool) async throws {
        let pipe = Pipe()
        let descriptor = pipe.fileHandleForWriting.fileDescriptor
        #if os(Linux)
        signal(SIGPIPE, SIG_IGN)
        #else
        _ = fcntl(descriptor, F_SETNOSIGPIPE, 1)
        #endif
        _ = descriptor
        locked { input = pipe.fileHandleForWriting }
        let command = server.command.map(ProjectWorkflows.quoted).joined(separator: " ") + " 2>/dev/null"
        let reading = pipe.fileHandleForReading
        task = Task { [self] in
            _ = try? await Shell.run(command, in: workspace, sandboxed: sandboxed, timeout: 60 * 60, input: reading) { [self] data in receive(data) }
            locked { ended = true }
        }
        let root = workspace.root
        let capabilities: [String: Any] = [
            "textDocument": ["publishDiagnostics": [:] as [String: Any], "diagnostic": [:] as [String: Any], "synchronization": ["didSave": true]] as [String: Any],
            "workspace": ["workspaceFolders": true],
        ]
        let params: [String: Any] = ["processId": NSNull(), "rootUri": root.absoluteString, "rootPath": root.path, "capabilities": capabilities,
                                     "workspaceFolders": [["uri": root.absoluteString, "name": root.lastPathComponent]]]
        guard let greeting = await request("initialize", params, wait: 60), greeting["error"] == nil else {
            await stop()
            throw MasonError("\(server.name) did not start: it gave no answer to the first message.")
        }
        let offered = (greeting["result"] as? [String: Any])?["capabilities"] as? [String: Any]
        locked { pulls = offered?["diagnosticProvider"] != nil && !(offered?["diagnosticProvider"] is NSNull) }
        send(["jsonrpc": "2.0", "method": "initialized", "params": [:] as [String: Any]])
    }

    private func receive(_ data: Data) {
        var answers: [[String: Any]] = []
        lock.lock()
        buffer.append(data)
        let (messages, used) = LanguageServers.frames(in: buffer)
        buffer.removeFirst(used)
        // Output that never becomes a message is not kept.
        if buffer.count > 64_000_000 { buffer.removeAll() }
        for message in messages {
            let method = message["method"] as? String
            if let method, let id = message["id"] {
                // The server asks something of its own. It gets an empty answer, so it does not wait for one.
                var result: Any = NSNull()
                if method == "workspace/configuration" {
                    let items = (message["params"] as? [String: Any])?["items"] as? [Any] ?? []
                    result = items.map { _ in NSNull() }
                }
                answers.append(["jsonrpc": "2.0", "id": id, "result": result])
            } else if let id = message["id"] as? Int {
                replies[id] = message
            } else if method == "textDocument/publishDiagnostics", let params = message["params"] as? [String: Any], let path = LanguageServers.path(of: params["uri"] as? String ?? "") {
                published[path] = ((published[path]?.count ?? 0) + 1, Date(), params["diagnostics"] as? [[String: Any]] ?? [])
            }
        }
        lock.unlock()
        for answer in answers { send(answer) }
    }

    func send(_ object: [String: Any]) {
        guard let handle = locked({ input }) else { return }
        writing.lock(); defer { writing.unlock() }
        do { try handle.write(contentsOf: LanguageServers.frame(object)) } catch { locked { ended = true } }
    }

    /// Sends a request and waits for its answer, or nil when none comes in time or the server has gone.
    func request(_ method: String, _ params: [String: Any]?, wait: TimeInterval) async -> [String: Any]? {
        let id: Int = locked { let next = nextId; nextId += 1; return next }
        var message: [String: Any] = ["jsonrpc": "2.0", "id": id, "method": method]
        if let params { message["params"] = params }
        send(message)
        let deadline = Date().addingTimeInterval(wait)
        while Date() < deadline {
            let (reply, gone): ([String: Any]?, Bool) = locked { (replies.removeValue(forKey: id), ended) }
            if let reply { return reply }
            if gone || Task.isCancelled { return nil }
            try? await Task.sleep(nanoseconds: 20_000_000)
        }
        return nil
    }

    /// Hands the server a file's text as it is now, and returns how many times it had published for the file before.
    func sync(_ file: URL, text: String, languageId: String) -> Int {
        let key = file.resolvingSymlinksInPath().path, uri = file.absoluteString
        // A server says nothing new about text it already has, so what it last said stands.
        let (version, before): (Int, Int) = locked {
            let count = published[key]?.count ?? 0
            if texts[key] == text { return (0, max(0, count - 1)) }
            texts[key] = text
            let next = (versions[key] ?? 0) + 1
            versions[key] = next
            return (next, count)
        }
        if version == 0 { return before }
        if version == 1 {
            send(["jsonrpc": "2.0", "method": "textDocument/didOpen", "params": ["textDocument": ["uri": uri, "languageId": languageId, "version": 1, "text": text] as [String: Any]]])
        } else {
            let document: [String: Any] = ["uri": uri, "version": version]
            send(["jsonrpc": "2.0", "method": "textDocument/didChange", "params": ["textDocument": document, "contentChanges": [["text": text]]] as [String: Any]])
        }
        send(["jsonrpc": "2.0", "method": "textDocument/didSave", "params": ["textDocument": ["uri": uri]]])
        return before
    }

    /// The file's diagnostics once the server has read it again, or nil when it says nothing in time.
    func diagnostics(for file: URL, after before: Int, wait: TimeInterval) async -> [[String: Any]]? {
        let key = file.resolvingSymlinksInPath().path, uri = file.absoluteString
        let deadline = Date().addingTimeInterval(wait)
        // A request for the file's symbols is answered once the server has read the file, which is when it publishes.
        _ = await request("textDocument/documentSymbol", ["textDocument": ["uri": uri]], wait: wait)
        var pulled: [[String: Any]]?
        if locked({ pulls }) {
            let answer = await request("textDocument/diagnostic", ["textDocument": ["uri": uri]], wait: max(1, deadline.timeIntervalSinceNow))
            pulled = (answer?["result"] as? [String: Any])?["items"] as? [[String: Any]]
        }
        // What it publishes may come in more than one part; it is taken once it has been quiet for a moment.
        // A server that answered the request may publish nothing at all, so it is not waited on for long.
        let settle: TimeInterval = 0.4, grace = Date().addingTimeInterval(1.5)
        while true {
            let (state, gone): ((count: Int, at: Date, items: [[String: Any]])?, Bool) = locked { (published[key], ended) }
            var fresh: [[String: Any]]?
            var quiet = false
            if let state, state.count > before {
                fresh = state.items
                quiet = Date().timeIntervalSince(state.at) >= settle
            }
            if let fresh, quiet || Date() >= deadline { return fresh + (pulled ?? []) }
            if gone || Task.isCancelled || Date() >= deadline { return pulled }
            if fresh == nil, pulled != nil, Date() >= grace { return pulled }
            try? await Task.sleep(nanoseconds: 30_000_000)
        }
    }

    func stop() async {
        if !hasEnded {
            _ = await request("shutdown", nil, wait: 1)
            send(["jsonrpc": "2.0", "method": "exit"])
        }
        let handle: FileHandle? = locked { let held = input; input = nil; return held }
        try? handle?.close()
        task?.cancel()
        await task?.value
    }
}

/// The language servers one request has started.
final class LanguageSessions: @unchecked Sendable {
    private let lock = NSLock()
    private var sessions: [String: LanguageSession] = [:]
    private var failed: [String: String] = [:]

    private func locked<T>(_ body: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return body() }

    /// The running server, started now if this request has not used it yet. `fresh` is true when it was started
    /// by this call, so the caller gives it longer.
    func session(_ server: LanguageServers.Server, workspace: Workspace, sandboxed: Bool) async throws -> (LanguageSession, fresh: Bool) {
        let key = server.command.joined(separator: " ")
        if let problem = locked({ failed[key] }) { throw MasonError(problem) }
        if let running = locked({ sessions[key] }) {
            if !running.hasEnded { return (running, false) }
            locked { sessions[key] = nil }
        }
        let session = LanguageSession(server: server)
        do { try await session.start(workspace: workspace, sandboxed: sandboxed) } catch {
            let problem = (error as? MasonError)?.description ?? error.localizedDescription
            locked { failed[key] = problem }
            throw error
        }
        locked { sessions[key] = session }
        return (session, true)
    }

    func stopAll() async {
        let all = locked { let held = Array(sessions.values); sessions = [:]; failed = [:]; return held }
        for session in all { await session.stop() }
    }
}

extension Agent {
    /// What the language's server says about a file this request just wrote, to go back with the edit's result.
    func languageNote(path: String, text: String, workspace: Workspace, session: inout Session) async -> String {
        let kind = LanguageServers.kind(of: path)
        if SymbolService.script.contains(kind) { return await LanguageServers.builtInErrors(workspace, path: path) ?? "" }
        guard let server = LanguageServers.server(for: kind) else {
            if SymbolService.python.contains(kind) { return await LanguageServers.builtInErrors(workspace, path: path) ?? "" }
            guard let language = LanguageServers.language(for: kind), !LanguageServers.quiet.contains(language.key), !languageTold.contains(language.key) else { return "" }
            languageTold.insert(language.key)
            emit(.status("No \(language.name) language server is installed"))
            return "\n[No language server for \(language.name) is installed on this computer, so this edit was not checked for errors. The user can see how to get one with /languages.]"
        }
        let sandboxed = session.permission == .auto
        if !sandboxed {
            if languageAnswers[server.name] == nil {
                var detail = "Starts \(server.command.joined(separator: " ")) on this project with your account and environment, until this request ends,"
                detail += " so the errors it finds in each edit go back to the model. It is not sandboxed, and some language servers run the project's build."
                let request = ApprovalRequest(tool: "inspect_symbol", title: "Start \(server.name) to check this request's edits", detail: detail, subject: "language server " + server.name)
                languageAnswers[server.name] = await allowed(request, session: &session)
            }
            guard languageAnswers[server.name] == true else { return "" }
        }
        emit(.status("Checking with \(server.name)"))
        do {
            return try await LanguageServers.errors(workspace, server: server, path: path, text: text, sandboxed: sandboxed, sessions: languages) ?? ""
        } catch {
            guard !languageTold.contains(server.name) else { return "" }
            languageTold.insert(server.name)
            let problem = (error as? MasonError)?.description ?? error.localizedDescription
            return "\n[\(server.name) could not check this edit: \(problem)]"
        }
    }
}
