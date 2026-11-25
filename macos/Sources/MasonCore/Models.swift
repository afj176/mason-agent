import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Models on this computer: the files, the llama.cpp runtime that runs them, and a server process for one of them.

public struct LocalModel: Equatable, Identifiable {
    public enum Role: String { case coding, vision, projector, embeddings, reranker }
    public let name: String
    public let path: String
    public let bytes: Int64
    public let source: String
    public var id: String { path }
    public init(name: String, path: String, bytes: Int64, source: String) { self.name = name; self.path = path; self.bytes = bytes; self.source = source }

    public var role: Role {
        let lower = name.lowercased()
        if lower.hasPrefix("mmproj") || lower.contains("-mmproj") { return .projector }
        if lower.contains("rerank") { return .reranker }
        if lower.contains("embed") || lower.hasPrefix("bge-") || lower.contains("nomic-embed") { return .embeddings }
        let folder = URL(fileURLWithPath: path).deletingLastPathComponent()
        let siblings = (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []
        return siblings.contains { $0.lowercased().hasPrefix("mmproj") && $0.hasSuffix(".gguf") } ? .vision : .coding
    }
    /// What the agent can do with this file, in plain words. Every file is listed with one of these; none are hidden.
    public var verdict: String {
        switch role {
        case .coding: return "Coding agent · runs in llama.cpp as a chat model"
        case .vision: return "Coding agent · vision · runs in llama.cpp with its projector"
        case .projector: return "Vision companion file · loaded with the chat model beside it"
        case .embeddings: return "Code search · embeddings · not started from mason yet"
        case .reranker: return "Code search · reranker · not started from mason yet"
        }
    }
    public var startable: Bool { role == .coding || role == .vision }
    public var size: String { ByteText.size(bytes) }
}

public enum ByteText {
    public static func size(_ bytes: Int64) -> String {
        let value = Double(bytes)
        if value >= 1_073_741_824 { return String(format: "%.1f GiB", value / 1_073_741_824) }
        if value >= 1_048_576 { return String(format: "%.1f MiB", value / 1_048_576) }
        return "\(bytes) B"
    }
}

public enum ModelLibrary {
    public static var downloadFolder: URL { DataHome.url.appendingPathComponent("Models") }

    /// Folders where GGUF files usually are: Mason's own downloads, the Hugging Face cache, LM Studio and llama.cpp's cache.
    public static func commonFolders() -> [(name: String, url: URL)] {
        let home = URL(fileURLWithPath: NSHomeDirectory())
        var folders: [(String, URL)] = [
            ("Mason downloads", downloadFolder),
            ("Hugging Face cache", home.appendingPathComponent(".cache/huggingface/hub")),
            ("LM Studio", home.appendingPathComponent(".lmstudio/models")),
            ("LM Studio", home.appendingPathComponent(".cache/lm-studio/models")),
            ("llama.cpp cache", home.appendingPathComponent("Library/Caches/llama.cpp")),
            ("llama.cpp cache", home.appendingPathComponent(".cache/llama.cpp")),
            ("Models folder", home.appendingPathComponent("models")),
        ]
        if let cache = ProcessInfo.processInfo.environment["HF_HOME"] { folders.append(("Hugging Face cache", URL(fileURLWithPath: cache).appendingPathComponent("hub"))) }
        return folders
    }

    public static func scan(_ folders: [(name: String, url: URL)] = commonFolders(), limit: Int = 400) -> [LocalModel] {
        var found: [String: LocalModel] = [:]
        for (name, root) in folders where FileManager.default.fileExists(atPath: root.path) {
            guard let walker = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey], options: [.skipsPackageDescendants]) else { continue }
            for case let file as URL in walker where file.pathExtension.lowercased() == "gguf" {
                // The Hugging Face cache stores files as links to blobs; follow them to the real size and identity.
                let real = file.resolvingSymlinksInPath()
                guard let size = (try? FileManager.default.attributesOfItem(atPath: real.path))?[.size] as? NSNumber, size.int64Value > 1_000_000 else { continue }
                // Parts 2…n of a split model belong to part 1.
                if file.lastPathComponent.range(of: #"-0000[2-9]-of-\d{5}\.gguf$"#, options: .regularExpression) != nil { continue }
                found[real.path] = LocalModel(name: file.deletingPathExtension().lastPathComponent, path: file.path, bytes: size.int64Value, source: name)
                if found.count >= limit { break }
            }
        }
        return found.values.sorted { ($0.startable ? 0 : 1, $0.name.lowercased()) < ($1.startable ? 0 : 1, $1.name.lowercased()) }
    }
}

public enum MemoryFit {
    /// Memory a model may use. Apple silicon shares memory between CPU and GPU, so this is a share of physical memory.
    public static var budgetBytes: Int64 { Int64(Double(ProcessInfo.processInfo.physicalMemory) * 0.6) }
    /// Weights plus working memory for an 8K context: about 15% of the weights plus 1.5 GiB.
    public static func neededBytes(weights: Int64) -> Int64 { Int64(Double(weights) * 1.15) + 1_610_612_736 }
    public static func fits(weights: Int64) -> Bool { neededBytes(weights: weights) <= budgetBytes }
    /// The largest context, up to 32K, that leaves room: each 8K of context is counted as roughly 0.5 GiB.
    public static func context(weights: Int64) -> Int {
        let spare = budgetBytes - neededBytes(weights: weights)
        for context in [32768, 16384, 8192] where spare >= Int64(context / 8192 - 1) * 536_870_912 { return context }
        return 4096
    }
}

/// Finds or installs llama.cpp's server, which runs GGUF models.
public enum LlamaRuntime {
    public static var managedFolder: URL { DataHome.url.appendingPathComponent("runtimes/llama.cpp") }

    public static func find() -> String? {
        var candidates = ["/opt/homebrew/bin/llama-server", "/usr/local/bin/llama-server", "/usr/bin/llama-server"]
        candidates += (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map { $0 + "/llama-server" }
        // Builds Mason installed come first, newest first. Release archives have changed their inner layout over time, so look for the program by name.
        var managed: [String] = []
        for build in ((try? FileManager.default.contentsOfDirectory(at: managedFolder, includingPropertiesForKeys: nil)) ?? []).sorted(by: { $0.lastPathComponent > $1.lastPathComponent }) {
            guard let walker = FileManager.default.enumerator(at: build, includingPropertiesForKeys: nil) else { continue }
            for case let file as URL in walker where file.lastPathComponent == "llama-server" { managed.append(file.path) }
        }
        return (managed + candidates).first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// The release asset name suffix for this computer, as published by ggml-org.
    public static var assetSuffix: String {
        #if os(macOS)
        #if arch(arm64)
        return "bin-macos-arm64.tar.gz"
        #else
        return "bin-macos-x64.tar.gz"
        #endif
        #elseif arch(arm64)
        return "bin-ubuntu-arm64.tar.gz"
        #else
        return "bin-ubuntu-x64.tar.gz"
        #endif
    }

    public struct Release { public let tag: String; public let asset: String; public let url: URL; public let size: Int64; public let sha256: String? }

    public static func latest() async throws -> Release {
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=5")!)
        request.setValue("MasonAgent", forHTTPHeaderField: "User-Agent")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        let data = try await Net.data(request)
        guard let releases = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { throw MasonError("GitHub did not return the llama.cpp releases.") }
        for release in releases {
            guard let tag = release["tag_name"] as? String else { continue }
            for asset in release["assets"] as? [[String: Any]] ?? [] {
                guard let name = asset["name"] as? String, name == "llama-\(tag)-\(assetSuffix)", let link = asset["browser_download_url"] as? String, let url = URL(string: link),
                      url.host == "github.com" else { continue }
                let digest = (asset["digest"] as? String).flatMap { $0.hasPrefix("sha256:") ? String($0.dropFirst(7)) : nil }
                return Release(tag: tag, asset: name, url: url, size: (asset["size"] as? NSNumber)?.int64Value ?? 0, sha256: digest)
            }
        }
        throw MasonError("No llama.cpp release has a build for this computer (\(assetSuffix)).")
    }

    /// Downloads the release, checks its SHA-256 when GitHub publishes one, unpacks it and returns the llama-server path.
    public static func install(_ release: Release, progress: @escaping (String) -> Void) async throws -> String {
        try FileManager.default.createDirectory(at: managedFolder, withIntermediateDirectories: true)
        let archive = managedFolder.appendingPathComponent(release.asset + ".partial")
        try? FileManager.default.removeItem(at: archive)
        defer { try? FileManager.default.removeItem(at: archive) }
        let digest = try await Downloader(allowedHosts: ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"]).download(release.url, to: archive, expectedBytes: release.size) { done, total in
            progress("Downloading \(release.asset) · \(ByteText.size(done)) of \(ByteText.size(total))")
        }
        if let expected = release.sha256, expected.lowercased() != digest { throw MasonError("SHA-256 check failed for \(release.asset). The file was discarded.") }
        let target = managedFolder.appendingPathComponent(release.tag)
        try? FileManager.default.removeItem(at: target)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        progress("Unpacking \(release.asset)")
        let unzip = Process()
        unzip.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        unzip.arguments = ["-xzf", archive.path, "-C", target.path]
        try unzip.run(); unzip.waitUntilExit()
        guard unzip.terminationStatus == 0, let server = find(), server.hasPrefix(target.path) else {
            try? FileManager.default.removeItem(at: target)
            throw MasonError("The llama.cpp release did not unpack to a usable llama-server.")
        }
        return server
    }
}

/// One llama-server process owned by this app or command line session. It stops when `stop` is called or the owner exits.
public final class ModelServer {
    public let model: LocalModel
    public let endpoint: ModelEndpoint
    public let context: Int
    public let log: URL
    let runFile: URL
    let process: Process

    init(model: LocalModel, endpoint: ModelEndpoint, context: Int, log: URL, runFile: URL, process: Process) {
        self.model = model; self.endpoint = endpoint; self.context = context; self.log = log; self.runFile = runFile; self.process = process
    }
    public var isRunning: Bool { process.isRunning }
    /// Removing the run file tells the watcher to stop the server; see `start`.
    public func stop() { try? FileManager.default.removeItem(at: runFile) }
    deinit { stop() }

    static func freePort() async -> Int? {
        for port in 18310..<18330 where await OpenAIClient.models(at: "http://127.0.0.1:\(port)", timeout: 0.4) == nil { return port }
        return nil
    }

    /// Starts the model and waits until the server lists it, up to five minutes. Progress carries the last log line while it loads.
    public static func start(_ model: LocalModel, runtime: String, context: Int? = nil, cpuOnly: Bool = false, progress: @escaping (String) -> Void) async throws -> ModelServer {
        guard model.startable else { throw MasonError("\(model.name) is not a chat model: \(model.verdict).") }
        guard let port = await freePort() else { throw MasonError("Ports 18310–18329 on this computer are all in use.") }
        let tokens = context ?? MemoryFit.context(weights: model.bytes)
        let alias = "mason-" + UUID().uuidString.prefix(8).lowercased()
        let folder = DataHome.url.appendingPathComponent("model-servers")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let log = folder.appendingPathComponent(alias + ".log")
        _ = FileManager.default.createFile(atPath: log.path, contents: nil)
        let process = Process()
        // The server runs under a small watcher shell. The watcher stops it within half a second when the run file is
        // removed (a normal stop) or when the app or command line that owns it goes away for any reason, including a
        // crash. It uses SIGKILL because a child can inherit a signal mask that blocks the polite signals.
        let runFile = folder.appendingPathComponent(alias + ".run")
        _ = FileManager.default.createFile(atPath: runFile.path, contents: nil)
        let watcher = "run=\"$1\"; shift; \"$@\" & child=$!; while kill -0 $PPID 2>/dev/null && kill -0 $child 2>/dev/null && [ -e \"$run\" ]; do sleep 0.5; done; kill -9 $child 2>/dev/null; rm -f \"$run\"; wait $child 2>/dev/null"
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        var arguments = ["-c", watcher, "mason-model", runFile.path, runtime, "-m", model.path, "--host", "127.0.0.1", "--port", String(port), "--alias", alias, "-c", String(tokens), "--jinja", "-ngl", cpuOnly ? "0" : "999"]
        if model.role == .vision, let projector = (try? FileManager.default.contentsOfDirectory(atPath: URL(fileURLWithPath: model.path).deletingLastPathComponent().path))?.first(where: { $0.lowercased().hasPrefix("mmproj") && $0.hasSuffix(".gguf") }) {
            arguments += ["--mmproj", URL(fileURLWithPath: model.path).deletingLastPathComponent().appendingPathComponent(projector).path]
        }
        process.arguments = arguments
        let output = try FileHandle(forWritingTo: log)
        process.standardOutput = output; process.standardError = output; process.standardInput = FileHandle.nullDevice
        do { try process.run() } catch { throw MasonError("llama-server could not start: \(error.localizedDescription)") }
        let server = ModelServer(model: model, endpoint: ModelEndpoint(url: "http://127.0.0.1:\(port)/", model: alias), context: tokens, log: log, runFile: runFile, process: process)
        let deadline = Date().addingTimeInterval(300)
        while Date() < deadline {
            if Task.isCancelled { server.stop(); throw CancellationError() }
            guard process.isRunning else { throw MasonError("llama-server stopped while loading \(model.name). " + lastLines(of: log)) }
            if let listed = await OpenAIClient.models(at: server.endpoint.url, timeout: 1), listed.contains(alias) { return server }
            progress(lastLines(of: log, count: 1))
            try? await Task.sleep(nanoseconds: 700_000_000)
        }
        server.stop()
        throw MasonError("\(model.name) did not finish loading in five minutes. Log: \(log.path)")
    }

    static func lastLines(of log: URL, count: Int = 4) -> String {
        guard let data = try? Data(contentsOf: log) else { return "" }
        return String(decoding: data.suffix(4000), as: UTF8.self).split(separator: "\n").suffix(count).joined(separator: " · ")
    }
}

/// Local OpenAI-compatible servers that are already running: llama.cpp, LM Studio, Ollama and Mason's own.
public enum LocalServers {
    public static let ports: [(name: String, port: Int)] = [("llama.cpp", 8080), ("LM Studio", 1234), ("Ollama", 11434)] + (18310..<18320).map { ("Mason", $0) }

    public static func detect() async -> [(name: String, endpoint: ModelEndpoint)] {
        await withTaskGroup(of: [(Int, String, ModelEndpoint)].self) { group in
            for (index, server) in ports.enumerated() {
                group.addTask {
                    let url = "http://127.0.0.1:\(server.port)/"
                    return (await OpenAIClient.models(at: url, timeout: 0.8) ?? []).prefix(8).map { (index, server.name, ModelEndpoint(url: url, model: $0)) }
                }
            }
            var all: [(Int, String, ModelEndpoint)] = []
            for await found in group { all += found }
            return all.sorted { $0.0 < $1.0 }.map { ($0.1, $0.2) }
        }
    }
}

/// What the app and command line remember between launches.
public struct Preferences: Codable {
    public var lastEndpoint: ModelEndpoint?
    public var lastModelPath: String?
    public var lastContext: Int?

    static var file: URL { DataHome.url.appendingPathComponent("preferences.json") }
    public static func load() -> Preferences {
        (try? JSONDecoder().decode(Preferences.self, from: Data(contentsOf: file))) ?? Preferences()
    }
    public func save() {
        try? FileManager.default.createDirectory(at: DataHome.url, withIntermediateDirectories: true)
        try? JSONEncoder().encode(self).write(to: Preferences.file, options: .atomic)
    }
}
