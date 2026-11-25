import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
#if canImport(CryptoKit)
import CryptoKit
#endif

// Network pieces: small requests, verified downloads and the Hugging Face model hub.
// Mason reaches the internet only for these, and only when the user asks.

enum Net {
    static func data(_ request: URLRequest) async throws -> Data {
        try await withCheckedThrowingContinuation { continuation in
            URLSession.shared.dataTask(with: request) { data, response, error in
                if let error { continuation.resume(throwing: MasonError("No answer from \(request.url?.host ?? "the server"): \(error.localizedDescription)")); return }
                let status = (response as? HTTPURLResponse)?.statusCode ?? 0
                guard status == 200, let data else {
                    continuation.resume(throwing: MasonError(status == 401 || status == 403 ? "\(request.url?.host ?? "The server") refused access (HTTP \(status))." : "\(request.url?.host ?? "The server") returned HTTP \(status)."))
                    return
                }
                continuation.resume(returning: data)
            }.resume()
        }
    }
}

/// Incremental SHA-256. CryptoKit does the work on macOS; the portable implementation keeps Linux builds and tests working.
public struct Sha256 {
    #if canImport(CryptoKit)
    var hasher = SHA256()
    public init() {}
    public mutating func update(_ data: Data) { hasher.update(data: data) }
    public func hex() -> String { hasher.finalize().map { String(format: "%02x", $0) }.joined() }
    #else
    var state: [UInt32] = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]
    var pending = [UInt8]()
    var length: UInt64 = 0
    static let k: [UInt32] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ]
    public init() {}
    public mutating func update(_ data: Data) {
        length &+= UInt64(data.count)
        pending.append(contentsOf: data)
        var offset = 0
        while pending.count - offset >= 64 { compress(pending, offset); offset += 64 }
        pending.removeFirst(offset)
    }
    mutating func compress(_ block: [UInt8], _ at: Int) {
        var w = [UInt32](repeating: 0, count: 64)
        for i in 0..<16 { w[i] = UInt32(block[at + i * 4]) << 24 | UInt32(block[at + i * 4 + 1]) << 16 | UInt32(block[at + i * 4 + 2]) << 8 | UInt32(block[at + i * 4 + 3]) }
        func rotr(_ x: UInt32, _ n: UInt32) -> UInt32 { x >> n | x << (32 - n) }
        for i in 16..<64 {
            let s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3), s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10)
            w[i] = w[i - 16] &+ s0 &+ w[i - 7] &+ s1
        }
        var a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7]
        for i in 0..<64 {
            let s1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25), ch = (e & f) ^ (~e & g)
            let t1 = h &+ s1 &+ ch &+ Sha256.k[i] &+ w[i]
            let s0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22), maj = (a & b) ^ (a & c) ^ (b & c)
            let t2 = s0 &+ maj
            h = g; g = f; f = e; e = d &+ t1; d = c; c = b; b = a; a = t1 &+ t2
        }
        state[0] &+= a; state[1] &+= b; state[2] &+= c; state[3] &+= d; state[4] &+= e; state[5] &+= f; state[6] &+= g; state[7] &+= h
    }
    public func hex() -> String {
        var copy = self
        var tail = copy.pending + [0x80]
        while tail.count % 64 != 56 { tail.append(0) }
        let bits = length &* 8
        for shift in stride(from: 56, through: 0, by: -8) { tail.append(UInt8(truncatingIfNeeded: bits >> UInt64(shift))) }
        var offset = 0
        while offset < tail.count { copy.compress(tail, offset); offset += 64 }
        return copy.state.map { String(format: "%08x", $0) }.joined()
    }
    #endif
}

/// Streams a file to disk and returns its SHA-256. Redirects are followed only to the hosts given.
final class Downloader: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    let allowedHosts: [String]
    var handle: FileHandle?
    var digest = Sha256()
    var received: Int64 = 0
    var expected: Int64 = 0
    var status = 0
    var lastReport = Date.distantPast
    var report: ((Int64, Int64) -> Void)?
    var continuation: CheckedContinuation<String, Error>?
    var task: URLSessionDataTask?
    var failure: Error?
    let lock = NSLock()

    init(allowedHosts: [String]) { self.allowedHosts = allowedHosts }

    func allowed(_ url: URL) -> Bool {
        guard url.scheme == "https", let host = url.host?.lowercased() else { return false }
        return allowedHosts.contains { host == $0 || host.hasSuffix("." + $0) }
    }

    func download(_ url: URL, to file: URL, expectedBytes: Int64, token: String = "", progress: @escaping (Int64, Int64) -> Void) async throws -> String {
        guard allowed(url) else { throw MasonError("Refusing to download from \(url.host ?? "an unknown host").") }
        _ = FileManager.default.createFile(atPath: file.path, contents: nil)
        handle = try FileHandle(forWritingTo: file)
        defer { try? handle?.close() }
        expected = expectedBytes; report = progress
        var request = URLRequest(url: url)
        request.timeoutInterval = 60
        request.setValue("MasonAgent", forHTTPHeaderField: "User-Agent")
        if !token.isEmpty { request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization") }
        let session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        let digest: String = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                lock.lock(); self.continuation = continuation; let task = session.dataTask(with: request); self.task = task; lock.unlock()
                task.resume()
            }
        } onCancel: { lock.lock(); let running = task; lock.unlock(); running?.cancel() }
        if expectedBytes > 0, received != expectedBytes { throw MasonError("The download ended early (\(received) of \(expectedBytes) bytes).") }
        return digest
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        guard let url = request.url, allowed(url) else { failure = MasonError("The download was redirected to an unexpected host."); completionHandler(nil); return }
        // A token is for the first host only; it is never forwarded to a storage host.
        var next = request
        if url.host != task.originalRequest?.url?.host { next.setValue(nil, forHTTPHeaderField: "Authorization") }
        completionHandler(next)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse, completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        status = (response as? HTTPURLResponse)?.statusCode ?? 0
        completionHandler(status == 200 ? .allow : .cancel)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        do { try handle?.write(contentsOf: data) } catch { failure = MasonError("Could not write the download: \(error.localizedDescription)"); dataTask.cancel(); return }
        digest.update(data); received += Int64(data.count)
        if expected > 0, received > expected { failure = MasonError("The download is larger than declared."); dataTask.cancel(); return }
        if Date().timeIntervalSince(lastReport) > 0.3 { lastReport = Date(); report?(received, max(expected, received)) }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock(); let pending = continuation; continuation = nil; lock.unlock()
        guard let pending else { return }
        if let failure { pending.resume(throwing: failure) }
        else if status != 200 && status != 0 {
            pending.resume(throwing: MasonError(status == 401 || status == 403 ? "Access was refused (HTTP \(status)). The repository may need you to accept its terms on its model page." : "The download failed (HTTP \(status))."))
        }
        else if let error { pending.resume(throwing: (error as NSError).code == NSURLErrorCancelled ? CancellationError() as Error : MasonError("The download failed: \(error.localizedDescription)")) }
        else { pending.resume(returning: digest.hex()) }
    }
}

public struct HubModel: Equatable {
    public let id: String
    public let downloads: Int
    public let likes: Int
    public let tags: [String]
    public let task: String
    public let gated: Bool
    public var isGGUF: Bool { tags.contains("gguf") }
    /// What the agent could do with this repository. Shown for every result.
    public var verdict: String {
        let lower = id.lowercased()
        if tags.contains("mlx") && !isGGUF { return "Coding agent · MLX weights; mason runs GGUF builds, look for one of this model" }
        if ["text-to-image", "text-to-speech", "automatic-speech-recognition", "text-to-video", "image-to-image"].contains(task) { return "Not used by the agent · \(task.replacingOccurrences(of: "-", with: " ")) models don't take part in coding work" }
        if task == "feature-extraction" || task == "sentence-similarity" || lower.contains("embed") { return "Code search · embeddings · not started from mason yet" }
        if task == "text-ranking" || lower.contains("rerank") { return "Code search · reranker · not started from mason yet" }
        if !isGGUF { return "Coding agent · not a GGUF build; look for a GGUF build of this model" }
        return task == "image-text-to-text" ? "Coding agent · vision · GGUF, runs in llama.cpp" : "Coding agent · GGUF, runs in llama.cpp"
    }
}

public struct HubBuild: Equatable {
    public let file: String
    public let bytes: Int64
    public let sha256: String
    public var quant: String {
        let upper = file.uppercased()
        return ["Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q4_K_M", "Q4_K_S", "Q4_0", "IQ4_XS", "IQ4_NL", "Q3_K_L", "Q3_K_M", "Q3_K_S", "IQ3_M", "Q2_K", "F16", "FP16", "BF16"].first { upper.contains($0) } ?? ""
    }
    public var isProjector: Bool { file.lowercased().contains("mmproj") }
}

public struct HubRepository: Equatable {
    public let id: String
    public let revision: String
    public let license: String
    public let gated: Bool
    public let builds: [HubBuild]

    /// Best quality first; the first single-file build that fits the memory budget.
    public func suggested(budget: Int64 = MemoryFit.budgetBytes) -> HubBuild? {
        for quant in ["Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q4_K_M", "Q4_K_S", "IQ4_XS", "IQ4_NL", "Q3_K_L", "Q3_K_M"] {
            if let build = builds.first(where: { !$0.isProjector && $0.quant == quant }), MemoryFit.neededBytes(weights: build.bytes) <= budget { return build }
        }
        return nil
    }
}

public enum Hub {
    static let trusted = ["Qwen", "unsloth", "bartowski", "ggml-org", "lmstudio-community", "mistralai", "google", "microsoft", "deepseek-ai", "ibm-granite", "LiquidAI", "openai"]
    static func checked(_ id: String) throws -> String {
        guard id.range(of: #"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$"#, options: .regularExpression) != nil, !id.contains("..") else { throw MasonError("Use a repository as OWNER/NAME.") }
        return id
    }
    static func get(_ path: String) async throws -> Any {
        var request = URLRequest(url: URL(string: "https://huggingface.co/api/" + path)!)
        request.timeoutInterval = 45
        request.setValue("MasonAgent", forHTTPHeaderField: "User-Agent")
        return try JSONSerialization.jsonObject(with: try await Net.data(request))
    }

    /// Every matching repository, GGUF builds first. Nothing is filtered out; each result carries a verdict.
    public static func search(_ query: String) async throws -> [HubModel] {
        let text = query.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty, text.count <= 200, let encoded = text.addingPercentEncoding(withAllowedCharacters: .alphanumerics) else { throw MasonError("Search for 1–200 characters.") }
        guard let list = try await get("models?search=\(encoded)&sort=downloads&direction=-1&limit=40") as? [[String: Any]] else { return [] }
        let models = list.compactMap { item -> HubModel? in
            guard let id = item["id"] as? String ?? item["modelId"] as? String else { return nil }
            let gated: Bool = { if let flag = item["gated"] as? Bool { return flag }; return item["gated"] is String }()
            return HubModel(id: id, downloads: item["downloads"] as? Int ?? 0, likes: item["likes"] as? Int ?? 0, tags: item["tags"] as? [String] ?? [], task: item["pipeline_tag"] as? String ?? "", gated: gated)
        }
        return models.filter(\.isGGUF) + models.filter { !$0.isGGUF }
    }

    /// The repository's GGUF files with sizes and hashes, pinned to its current commit.
    public static func inspect(_ id: String) async throws -> HubRepository {
        guard let info = try await get("models/\(try checked(id))?blobs=true") as? [String: Any], let revision = info["sha"] as? String,
              revision.range(of: "^[a-f0-9]{40}$", options: .regularExpression) != nil else { throw MasonError("Hugging Face did not return a pinned commit for \(id).") }
        let builds = (info["siblings"] as? [[String: Any]] ?? []).compactMap { file -> HubBuild? in
            guard let name = file["rfilename"] as? String, name.lowercased().hasSuffix(".gguf"), !name.contains(".."), !name.hasPrefix("/"),
                  let lfs = file["lfs"] as? [String: Any], let hash = lfs["sha256"] as? String, hash.range(of: "^[a-f0-9]{64}$", options: .regularExpression) != nil,
                  let size = (lfs["size"] as? NSNumber ?? file["size"] as? NSNumber)?.int64Value,
                  name.range(of: #"-\d{5}-of-\d{5}\.gguf$"#, options: .regularExpression) == nil else { return nil }
            return HubBuild(file: name, bytes: size, sha256: hash)
        }.sorted { $0.bytes < $1.bytes }
        let card = info["cardData"] as? [String: Any]
        let gated: Bool = { if let flag = info["gated"] as? Bool { return flag }; return info["gated"] is String }()
        return HubRepository(id: id, revision: revision, license: card?["license"] as? String ?? "", gated: gated, builds: builds)
    }

    /// Downloads one build into Mason's model folder and verifies it against the hash Hugging Face publishes. Returns the file.
    public static func download(_ build: HubBuild, from repository: HubRepository, progress: @escaping (Int64, Int64) -> Void) async throws -> URL {
        let folder = ModelLibrary.downloadFolder.appendingPathComponent(repository.id.replacingOccurrences(of: "/", with: "--"))
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = folder.appendingPathComponent(URL(fileURLWithPath: build.file).lastPathComponent)
        if let size = (try? FileManager.default.attributesOfItem(atPath: target.path))?[.size] as? NSNumber, size.int64Value == build.bytes { return target }
        let partial = target.appendingPathExtension("partial")
        let path = build.file.split(separator: "/").map { $0.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? String($0) }.joined(separator: "/")
        guard let url = URL(string: "https://huggingface.co/\(repository.id)/resolve/\(repository.revision)/\(path)") else { throw MasonError("That file name cannot be downloaded.") }
        do {
            let digest = try await Downloader(allowedHosts: ["huggingface.co", "hf.co"]).download(url, to: partial, expectedBytes: build.bytes, progress: progress)
            guard digest == build.sha256.lowercased() else { throw MasonError("SHA-256 check failed for \(build.file). The file was discarded.") }
        } catch { try? FileManager.default.removeItem(at: partial); throw error }
        try? FileManager.default.removeItem(at: target)
        try FileManager.default.moveItem(at: partial, to: target)
        return target
    }

    /// Coding models from well-known publishers whose best build fits this computer's memory, largest first.
    public static func suggestions(limit: Int = 4, progress: @escaping (String) -> Void = { _ in }) async throws -> [(repository: HubRepository, build: HubBuild)] {
        let candidates = try await search("coder").filter { $0.isGGUF && !$0.gated && $0.verdict.hasPrefix("Coding agent") && trusted.contains(where: $0.id.hasPrefix) && $0.id.range(of: "abliterat|uncensor", options: [.regularExpression, .caseInsensitive]) == nil }
        var picks: [(HubRepository, HubBuild)] = []
        for candidate in candidates.prefix(12) {
            progress("Checking \(candidate.id)")
            if let repository = try? await inspect(candidate.id), let build = repository.suggested() { picks.append((repository, build)) }
            if picks.count >= limit { break }
        }
        return picks.sorted { $0.1.bytes > $1.1.bytes }.map { (repository: $0.0, build: $0.1) }
    }
}
