import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Searching code by meaning. The project's source is cut into overlapping windows of lines, each window is embedded
// once per content hash by an embeddings server the user chose, and windows are ranked against the question.

/// The embeddings server used for code search, and optionally a reranker. Any server that speaks the OpenAI
/// `/v1/embeddings` shape works, for example `llama-server --embedding`.
public struct EmbeddingSettings: Codable, Equatable {
    public var url: String
    public var model: String
    public var key: String = ""
    /// A server that answers `/v1/rerank`, used to reorder the best matches. Empty for none.
    public var rerankUrl: String = ""
    public var rerankModel: String = ""
    public init(url: String, model: String, key: String = "", rerankUrl: String = "", rerankModel: String = "") {
        self.url = url; self.model = model; self.key = key; self.rerankUrl = rerankUrl; self.rerankModel = rerankModel
    }

    static func file(_ home: URL) -> URL { home.appendingPathComponent("embeddings.json") }

    /// The saved choice, or nil when code search by meaning is off.
    public static func load(home: URL = DataHome.url) -> EmbeddingSettings? {
        guard let data = try? Data(contentsOf: file(home)), let saved = try? JSONDecoder().decode(EmbeddingSettings.self, from: data), !saved.url.isEmpty else { return nil }
        return saved
    }

    /// Saves the choice after checking the addresses; nil turns code search by meaning off.
    public static func save(_ settings: EmbeddingSettings?, home: URL = DataHome.url) throws {
        guard let settings else { try? FileManager.default.removeItem(at: file(home)); return }
        _ = try ModelEndpoint.validated(settings.url)
        if !settings.rerankUrl.isEmpty { _ = try ModelEndpoint.validated(settings.rerankUrl) }
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try JSONEncoder().encode(settings).write(to: file(home), options: .atomic)
    }
}

public protocol SemanticModels {
    /// Names the embeddings model; saved vectors are keyed by it.
    var identity: String { get }
    var canRerank: Bool { get }
    func embed(_ texts: [String]) async throws -> [[Float]]
    func rerank(_ query: String, documents: [String]) async throws -> [Double]
}

/// The servers in `EmbeddingSettings`.
public struct EmbeddingServers: SemanticModels {
    public let settings: EmbeddingSettings
    public init(_ settings: EmbeddingSettings) { self.settings = settings }
    public var identity: String { settings.url + "|" + settings.model }
    public var canRerank: Bool { !settings.rerankUrl.isEmpty }

    func post(_ base: String, _ path: String, _ body: [String: Any]) async throws -> Any {
        var request = URLRequest(url: try ModelEndpoint.validated(base).appendingPathComponent(path))
        request.httpMethod = "POST"
        request.timeoutInterval = 120
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if !settings.key.isEmpty { request.setValue("Bearer " + settings.key, forHTTPHeaderField: "Authorization") }
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return try JSONSerialization.jsonObject(with: try await Net.data(request))
    }

    public func embed(_ texts: [String]) async throws -> [[Float]] {
        let answer = try await post(settings.url, "v1/embeddings", ["model": settings.model, "input": texts]) as? [String: Any]
        let rows = (answer?["data"] as? [[String: Any]] ?? []).sorted { ($0["index"] as? Int ?? 0) < ($1["index"] as? Int ?? 0) }
        let vectors = rows.compactMap { ($0["embedding"] as? [NSNumber])?.map(\.floatValue) }
        guard vectors.count == texts.count, vectors.allSatisfy({ !$0.isEmpty }) else { throw MasonError("The embeddings server answered with a different number of vectors than it was sent.") }
        return vectors
    }

    public func rerank(_ query: String, documents: [String]) async throws -> [Double] {
        let answer = try await post(settings.rerankUrl, "v1/rerank", ["model": settings.rerankModel, "query": query, "documents": documents]) as? [String: Any]
        var scores = [Double](repeating: -.infinity, count: documents.count)
        for row in answer?["results"] as? [[String: Any]] ?? [] {
            if let index = row["index"] as? Int, documents.indices.contains(index), let score = (row["relevance_score"] as? NSNumber)?.doubleValue { scores[index] = score }
        }
        return scores
    }

    /// Embeds one short text, to tell the user whether the server works.
    public func probe() async throws -> String {
        let vectors = try await embed(["func total(items) { return items.reduce(0, +) }"])
        return "The server returned \(vectors[0].count)-dimension embeddings. Code search by meaning is ready."
    }
}

public struct SemanticHit: Equatable {
    public let path: String
    public let startLine: Int, endLine: Int
    public let score: Double
    public let preview: String
}

public struct SemanticResult {
    public let hits: [SemanticHit]
    public let files: Int, chunks: Int
    public let partial: Bool, reranked: Bool
    public let model: String

    /// The result as the model reads it.
    public var report: String {
        var text = "Ranked by meaning, not exact text (\(files) files, \(chunks) passages\(partial ? ", not the whole project" : "")\(reranked ? ", reranked" : "")). Read the lines before editing; use search_text for exact strings."
        if hits.isEmpty { text += "\nNothing matched." }
        for hit in hits { text += "\n\n\(hit.path) lines \(hit.startLine)–\(hit.endLine) (score \(String(format: "%.3f", hit.score)))\n\(hit.preview)" }
        return text
    }
}

public enum SemanticSearch {
    static let extensions: Set<String> = ["cs", "ts", "tsx", "js", "jsx", "mjs", "cjs", "py", "rb", "go", "rs", "java", "kt", "swift", "c", "h", "cpp", "hpp", "cc", "m", "mm", "php", "dart", "scala", "sql",
                                          "html", "css", "scss", "vue", "svelte", "md", "ps1", "sh", "json", "yaml", "yml", "toml", "xaml", "csproj"]
    static let chunkLines = 40, overlap = 8, maxFiles = 1000, maxChunks = 6000, batch = 32
    // Vectors last as long as the program: model | file hash | first line -> vector.
    private static var vectors: [String: [Float]] = [:]
    private static let lock = NSLock()
    private static func cached(_ key: String) -> [Float]? { lock.lock(); defer { lock.unlock() }; return vectors[key] }
    private static func store(_ key: String, _ vector: [Float]) { lock.lock(); defer { lock.unlock() }; vectors[key] = vector }

    static func skipped(_ path: String) -> Bool {
        let parts = path.split(separator: "/").map(String.init)
        let folders: Set<String> = ["node_modules", "bin", "obj", "dist", "build", ".build", ".git", ".venv", "venv", "__pycache__"]
        if parts.dropLast().contains(where: folders.contains) { return true }
        let name = parts.last ?? ""
        return name.hasSuffix(".min.js") || name.hasSuffix(".min.css") || name == "package-lock.json"
    }

    /// Overlapping windows of lines, each labelled with its file so the model that embeds it knows where it is from.
    public static func chunks(path: String, content: String) -> [(start: Int, end: Int, text: String)] {
        let lines = content.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        var result: [(Int, Int, String)] = []
        var start = 0
        while start < lines.count {
            let end = min(lines.count, start + chunkLines)
            let body = lines[start..<end].joined(separator: "\n")
            if body.trimmingCharacters(in: .whitespacesAndNewlines).count >= 20 { result.append((start + 1, end, "File: \(path)\n" + String(body.prefix(6000)))) }
            if end == lines.count { break }
            start += chunkLines - overlap
        }
        return result
    }

    static func normalized(_ vector: [Float]) -> [Float] {
        let length = vector.reduce(0.0) { $0 + Double($1) * Double($1) }.squareRoot()
        return length == 0 ? vector : vector.map { Float(Double($0) / length) }
    }

    public static func search(_ workspace: Workspace, models: SemanticModels, query: String, directory: String = ".", limit: Int = 8) async throws -> SemanticResult {
        let question = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !question.isEmpty, question.count <= 2000 else { throw MasonError("Describe what to find in 1–2,000 characters.") }
        let listing = try workspace.files(under: directory.isEmpty ? "." : directory, limit: maxFiles)
        var all: [(key: String, path: String, start: Int, end: Int, text: String)] = [], pending: [(key: String, text: String)] = []
        var partial = listing.truncated, files = 0
        for path in listing.paths where extensions.contains((path as NSString).pathExtension.lowercased()) && !skipped(path) {
            try Task.checkCancellation()
            guard all.count < maxChunks else { partial = true; break }
            guard let data = try? workspace.data(path), data.count <= 2 * 1024 * 1024, let content = String(data: data, encoding: .utf8) else { continue }
            files += 1
            let hash = sha256Hex(data)
            for chunk in chunks(path: path, content: content) {
                guard all.count < maxChunks else { partial = true; break }
                let key = models.identity + "|" + hash + "|\(chunk.start)"
                all.append((key, path, chunk.start, chunk.end, chunk.text))
                if cached(key) == nil { pending.append((key, chunk.text)) }
            }
        }
        var index = 0
        while index < pending.count {
            try Task.checkCancellation()
            let group = Array(pending[index..<min(pending.count, index + batch)])
            let embedded = try await models.embed(group.map(\.text))
            for (item, vector) in zip(group, embedded) { store(item.key, normalized(vector)) }
            index += batch
        }
        let target = normalized(try await models.embed([question])[0])
        var ranked: [(chunk: (key: String, path: String, start: Int, end: Int, text: String), score: Double)] = all.compactMap { chunk in
            guard let vector = cached(chunk.key), vector.count == target.count else { return nil }
            var score = 0.0
            for i in 0..<vector.count { score += Double(vector[i]) * Double(target[i]) }
            return (chunk, score)
        }
        ranked.sort { $0.score > $1.score }
        ranked = Array(ranked.prefix(max(limit * 4, 24)))
        var reranked = false
        if models.canRerank, ranked.count > 1, let scores = try? await models.rerank(question, documents: ranked.map(\.chunk.text)), scores.count == ranked.count {
            ranked = zip(ranked, scores).map { ($0.0.chunk, $0.1) }.sorted { $0.score > $1.score }
            reranked = true
        }
        var hits: [SemanticHit] = []
        for (chunk, score) in ranked {
            // Overlapping windows of one file collapse to the best one.
            if hits.contains(where: { $0.path == chunk.path && chunk.start <= $0.endLine && chunk.end >= $0.startLine }) { continue }
            let body = chunk.text.split(separator: "\n", maxSplits: 1, omittingEmptySubsequences: false).dropFirst().joined()
            hits.append(SemanticHit(path: chunk.path, startLine: chunk.start, endLine: chunk.end, score: score, preview: body.count > 700 ? String(body.prefix(700)) + "…" : body))
            if hits.count >= limit { break }
        }
        return SemanticResult(hits: hits, files: files, chunks: all.count, partial: partial, reranked: reranked, model: models.identity)
    }
}
