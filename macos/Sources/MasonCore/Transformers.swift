import Foundation

// Models kept as Hugging Face Transformers folders (config.json, a tokenizer and .safetensors weights). They are
// served by a small Python program that ships with Mason, in a Python environment the user already has with PyTorch
// and Transformers installed. Nothing is downloaded and no code from the model folder is run.

public enum TransformersRuntime {
    /// The folder holding Serve-Transformers.py: beside the app or command line as installed, or `MASON_TRANSFORMERS_DIR`.
    public static func scriptFolder() -> URL? {
        var candidates: [URL] = []
        if let named = ProcessInfo.processInfo.environment["MASON_TRANSFORMERS_DIR"], !named.isEmpty { candidates.append(URL(fileURLWithPath: named)) }
        if let resources = Bundle.main.resourceURL { candidates.append(resources.appendingPathComponent("transformers")) }
        // The command line tool: Mason Agent.app/Contents/Resources/bin/mason on a Mac, <prefix>/bin/mason elsewhere.
        // The program's own location, not how it was typed: "mason" found on the PATH is a link into the app.
        let program = (Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments.first ?? "")).resolvingSymlinksInPath().deletingLastPathComponent()
        candidates.append(program.deletingLastPathComponent().appendingPathComponent("transformers"))
        candidates.append(program.deletingLastPathComponent().appendingPathComponent("share/mason-agent/transformers"))
        return candidates.first { FileManager.default.fileExists(atPath: $0.appendingPathComponent("Serve-Transformers.py").path) }
    }

    /// The Python to use: `MASON_PYTHON`, the one the user chose, else the first python3 on the PATH.
    public static func python() -> String? {
        var candidates: [String] = []
        if let named = ProcessInfo.processInfo.environment["MASON_PYTHON"], !named.isEmpty { candidates.append(named) }
        if let chosen = Preferences.load().python, !chosen.isEmpty { candidates.append(chosen) }
        let path = (ProcessInfo.processInfo.environment["PATH"] ?? "") + ":/opt/homebrew/bin:/usr/local/bin:/usr/bin"
        candidates += path.split(separator: ":").map { $0 + "/python3" }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// Where PyTorch runs the model: Apple's GPU on Apple silicon, an NVIDIA GPU where its driver is installed, else the CPU.
    public static func device(cpuOnly: Bool = false) -> String {
        if cpuOnly { return "cpu" }
        #if os(macOS) && arch(arm64)
        return "mps"
        #else
        return ["/usr/bin/nvidia-smi", "/usr/local/bin/nvidia-smi"].contains { FileManager.default.isExecutableFile(atPath: $0) } ? "cuda" : "cpu"
        #endif
    }

    /// True for a folder with a model config, a tokenizer and safetensors weights.
    public static func isModelFolder(_ folder: URL) -> Bool {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []
        return names.contains("config.json") && names.contains("tokenizer_config.json") && names.contains { $0.hasSuffix(".safetensors") }
    }

    /// The model as listed: its repository name in the Hugging Face cache (models--org--name/snapshots/…), else the folder's name.
    static func describe(_ folder: URL, source: String) -> LocalModel {
        var name = folder.lastPathComponent
        let parts = folder.pathComponents
        if let index = parts.lastIndex(where: { $0.hasPrefix("models--") }) { name = parts[index].dropFirst(8).replacingOccurrences(of: "--", with: "/") }
        var bytes: Int64 = 0
        for file in (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? [] where file.pathExtension == "safetensors" {
            bytes += ((try? FileManager.default.attributesOfItem(atPath: file.resolvingSymlinksInPath().path))?[.size] as? NSNumber)?.int64Value ?? 0
        }
        return LocalModel(name: name, path: folder.path, bytes: bytes, source: source, transformers: true)
    }

    /// A model folder the user names, checked before it is started.
    public static func model(at path: String) throws -> LocalModel {
        let folder = URL(fileURLWithPath: (path as NSString).expandingTildeInPath).standardizedFileURL
        guard isModelFolder(folder) else { throw MasonError("\(folder.path) is not a Transformers model folder: it needs config.json, tokenizer files and .safetensors weights.") }
        return describe(folder, source: "Folder")
    }

    /// The command that serves a model folder.
    static func command(_ model: LocalModel, python: String, script: URL, alias: String, device: String, port: Int, context: Int, manifest: URL, checkOnly: Bool = false) -> [String] {
        [python, "-I", "-B", script.appendingPathComponent("Serve-Transformers.py").path, "--model-dir", model.path, "--model-id", alias, "--device", device, "--port", String(port),
         "--context", String(context), "--manifest", manifest.path] + (checkOnly ? ["--check-only"] : [])
    }

    static func status(_ manifest: URL) -> (text: String, failed: Bool, context: Int?) {
        guard let data = try? Data(contentsOf: manifest), let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return ("", false, nil) }
        return (object["status"] as? String ?? "", object["error"] as? Bool ?? false, object["contextTokens"] as? Int)
    }

    /// Checks the folder and the Python environment without loading the weights, and says what was found.
    public static func check(_ model: LocalModel, cpuOnly: Bool = false) async throws -> String {
        guard let script = scriptFolder() else { throw MasonError("The Transformers server that ships with Mason was not found.") }
        guard let python = python() else { throw MasonError("No python3 was found. Install Python with PyTorch and Transformers, or choose one with /models python PATH.") }
        let manifest = FileManager.default.temporaryDirectory.appendingPathComponent("mason-transformers-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: manifest) }
        let process = Process()
        let arguments = command(model, python: python, script: script, alias: "check", device: device(cpuOnly: cpuOnly), port: 18399, context: 8192, manifest: manifest, checkOnly: true)
        process.executableURL = URL(fileURLWithPath: arguments[0])
        process.arguments = Array(arguments.dropFirst())
        process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice; process.standardInput = FileHandle.nullDevice
        try process.run()
        let deadline = Date().addingTimeInterval(120)
        while process.isRunning {
            if Task.isCancelled || Date() > deadline { process.terminate(); throw Task.isCancelled ? CancellationError() : MasonError("The check did not finish in two minutes.") }
            try? await Task.sleep(nanoseconds: 200_000_000)
        }
        let found = status(manifest)
        if process.terminationStatus != 0 || found.failed { throw MasonError(found.text.isEmpty ? "\(python) could not run the check." : found.text) }
        return found.text + (found.context.map { " Context: \($0) tokens." } ?? "") + " Python: \(python)."
    }
}
