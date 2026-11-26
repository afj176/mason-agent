import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public struct ToolSpec {
    public let name: String
    public let description: String
    /// JSON Schema of the arguments object.
    public let parameters: [String: Any]
}

public struct ModelReply {
    public var text = ""
    public var toolCalls: [ToolCall] = []
    public var promptTokens: Int?
    public var finishReason: String?
}

/// Anything that can produce the next assistant message. The engine and its tests depend only on this.
public protocol ChatModel {
    var name: String { get }
    func complete(messages: [ChatMessage], tools: [ToolSpec], onText: @escaping (String) -> Void) async throws -> ModelReply
    /// How many tokens the model can take in one request, when the server says. Automatic compaction needs it.
    func contextWindow() async -> Int?
}

extension ChatModel {
    public func contextWindow() async -> Int? { nil }
}

public struct ModelEndpoint: Codable, Equatable {
    public var url: String
    public var model: String
    public var key: String = ""
    public init(url: String, model: String, key: String = "") { self.url = url; self.model = model; self.key = key }

    /// Mason only talks to models on this computer: http(s) on localhost or a literal loopback address.
    public static func validated(_ text: String) throws -> URL {
        guard var parts = URLComponents(string: text.trimmingCharacters(in: .whitespaces)), let scheme = parts.scheme, scheme == "http" || scheme == "https",
              let host = parts.host, parts.user == nil, parts.query == nil, parts.fragment == nil else {
            throw MasonError("Use an http URL on this computer, such as http://127.0.0.1:8080.")
        }
        let bare = host.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        // A literal loopback address only: a name such as 127.0.0.1.example.com resolves wherever its owner likes.
        guard bare == "localhost" || bare == "::1" || bare.range(of: #"^127(\.\d{1,3}){3}$"#, options: .regularExpression) != nil else {
            throw MasonError("Local model connections require localhost or a loopback address.")
        }
        guard ["", "/", "/v1", "/v1/"].contains(parts.path) else { throw MasonError("Use the server's root URL or its /v1 base URL.") }
        if bare == "localhost" { parts.host = "127.0.0.1" }
        parts.path = "/"
        guard let url = parts.url else { throw MasonError("That URL is not valid.") }
        return url
    }
}

/// Assembles a streamed chat completion from server-sent events. Kept separate from the network so it can be tested with recorded lines.
public struct StreamAssembler {
    public private(set) var reply = ModelReply()
    var partial: [Int: ToolCall] = [:]
    public init() {}

    /// Feeds one line of the event stream. Returns newly received assistant text, if any.
    public mutating func feed(line: String) throws -> String? {
        guard line.hasPrefix("data:") else { return nil }
        let payload = line.dropFirst(5).trimmingCharacters(in: .whitespaces)
        guard payload != "[DONE]", let data = payload.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if let error = object["error"] as? [String: Any] { throw MasonError("The model server reported: \(error["message"] as? String ?? "an error")") }
        if let usage = object["usage"] as? [String: Any], let prompt = usage["prompt_tokens"] as? Int { reply.promptTokens = prompt }
        guard let choice = (object["choices"] as? [[String: Any]])?.first else { return nil }
        if let finish = choice["finish_reason"] as? String { reply.finishReason = finish }
        guard let delta = choice["delta"] as? [String: Any] else { return nil }
        for call in delta["tool_calls"] as? [[String: Any]] ?? [] {
            let index = call["index"] as? Int ?? 0
            var current = partial[index] ?? ToolCall(id: "", name: "", arguments: "")
            if let id = call["id"] as? String, !id.isEmpty { current.id = id }
            if let function = call["function"] as? [String: Any] {
                if let name = function["name"] as? String { current.name += name }
                if let arguments = function["arguments"] as? String { current.arguments += arguments }
            }
            partial[index] = current
        }
        if let text = delta["content"] as? String, !text.isEmpty { reply.text += text; return text }
        return nil
    }

    public mutating func finish() -> ModelReply {
        reply.toolCalls = partial.keys.sorted().compactMap { index in
            guard var call = partial[index], !call.name.isEmpty else { return nil }
            if call.id.isEmpty { call.id = "call_\(index)" }
            if call.arguments.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { call.arguments = "{}" }
            return call
        }
        return reply
    }
}

/// An OpenAI-compatible chat completions client (llama.cpp, LM Studio, Ollama and others) with streaming and native tool calls.
public final class OpenAIClient: ChatModel {
    public let endpoint: ModelEndpoint
    public var name: String { endpoint.model }
    let base: URL

    public init(_ endpoint: ModelEndpoint) throws {
        self.endpoint = endpoint
        base = try ModelEndpoint.validated(endpoint.url)
    }

    /// llama.cpp reports the loaded context size at /props. Other servers do not, and then compaction stays manual.
    public func contextWindow() async -> Int? {
        var request = URLRequest(url: base.appendingPathComponent("props"))
        request.timeoutInterval = 3
        let fetched: Data? = await withCheckedContinuation { continuation in
            URLSession.shared.dataTask(with: request) { data, response, _ in
                continuation.resume(returning: (response as? HTTPURLResponse)?.statusCode == 200 ? data : nil)
            }.resume()
        }
        guard let fetched, let object = try? JSONSerialization.jsonObject(with: fetched) as? [String: Any] else { return nil }
        let settings = object["default_generation_settings"] as? [String: Any]
        let size = (settings?["n_ctx"] as? Int) ?? (object["n_ctx"] as? Int)
        return size.flatMap { $0 > 0 ? $0 : nil }
    }

    static func wire(_ message: ChatMessage) -> [String: Any] {
        var object: [String: Any] = ["role": message.role, "content": message.content]
        if let calls = message.toolCalls, !calls.isEmpty {
            object["tool_calls"] = calls.map { ["id": $0.id, "type": "function", "function": ["name": $0.name, "arguments": $0.arguments]] as [String: Any] }
        }
        if let id = message.toolCallId { object["tool_call_id"] = id }
        return object
    }

    public func complete(messages: [ChatMessage], tools: [ToolSpec], onText: @escaping (String) -> Void) async throws -> ModelReply {
        var body: [String: Any] = [
            "model": endpoint.model, "stream": true, "stream_options": ["include_usage": true],
            "messages": messages.map(OpenAIClient.wire),
        ]
        if !tools.isEmpty {
            body["tools"] = tools.map { ["type": "function", "function": ["name": $0.name, "description": $0.description, "parameters": $0.parameters] as [String: Any]] as [String: Any] }
        }
        var request = URLRequest(url: base.appendingPathComponent("v1/chat/completions"))
        request.httpMethod = "POST"
        request.timeoutInterval = 600
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if !endpoint.key.isEmpty { request.setValue("Bearer " + endpoint.key, forHTTPHeaderField: "Authorization") }
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let stream = EventStream(onText: onText)
        return try await stream.run(request)
    }

    /// Model IDs the server lists, or nil when nothing answers there.
    public static func models(at url: String, key: String = "", timeout: TimeInterval = 2) async -> [String]? {
        guard let base = try? ModelEndpoint.validated(url) else { return nil }
        var request = URLRequest(url: base.appendingPathComponent("v1/models"))
        request.timeoutInterval = timeout
        if !key.isEmpty { request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization") }
        let fetched: Data? = await withCheckedContinuation { continuation in
            URLSession.shared.dataTask(with: request) { data, response, _ in
                continuation.resume(returning: (response as? HTTPURLResponse)?.statusCode == 200 ? data : nil)
            }.resume()
        }
        guard let data = fetched, let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return (object["data"] as? [[String: Any]] ?? []).compactMap { $0["id"] as? String }
    }
}

/// One streaming request. A URLSession delegate is used because it behaves the same on macOS and Linux.
/// URLSession calls the delegate on one serial queue, so the mutable state below is only touched from there;
/// the continuation and task, shared with the caller, are guarded by the lock.
final class EventStream: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    let onText: (String) -> Void
    var assembler = StreamAssembler()
    var buffer = Data()
    var errorBody = Data()
    var status = 0
    var failure: Error?
    var continuation: CheckedContinuation<ModelReply, Error>?
    var task: URLSessionDataTask?
    let lock = NSLock()

    init(onText: @escaping (String) -> Void) { self.onText = onText }

    func run(_ request: URLRequest) async throws -> ModelReply {
        let session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<ModelReply, Error>) in
                lock.lock()
                self.continuation = continuation
                let task = session.dataTask(with: request)
                self.task = task
                lock.unlock()
                if Task.isCancelled { task.cancel() }
                task.resume()
            }
        } onCancel: {
            lock.lock(); let running = task; lock.unlock()
            running?.cancel()
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse, completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        status = (response as? HTTPURLResponse)?.statusCode ?? 0
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard status == 200 else { if errorBody.count < 4000 { errorBody.append(data) }; return }
        buffer.append(data)
        while let newline = buffer.firstIndex(of: 0x0A) {
            let line = String(decoding: buffer[buffer.startIndex..<newline], as: UTF8.self).trimmingCharacters(in: CharacterSet(charactersIn: "\r"))
            buffer.removeSubrange(buffer.startIndex...newline)
            do { if let text = try assembler.feed(line: line) { onText(text) } }
            catch { failure = error; dataTask.cancel(); return }
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock(); let pending = continuation; continuation = nil; lock.unlock()
        guard let pending else { return }
        if let failure { pending.resume(throwing: failure); return }
        if let error {
            let cancelled = (error as NSError).code == NSURLErrorCancelled
            pending.resume(throwing: cancelled ? CancellationError() as Error : MasonError("The model server did not answer: \(error.localizedDescription)"))
            return
        }
        guard status == 200 else {
            let detail = String(decoding: errorBody, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            pending.resume(throwing: MasonError("The model server returned HTTP \(status)." + (detail.isEmpty ? "" : " " + String(detail.prefix(600)))))
            return
        }
        if !buffer.isEmpty, let text = try? assembler.feed(line: String(decoding: buffer, as: UTF8.self)) { onText(text) }
        pending.resume(returning: assembler.finish())
    }
}
