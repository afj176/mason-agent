import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// One MCP server the user registered. Servers and their tool allowlists live in Mason's data folder
/// (`mcp-servers.json`), never in a project, so a project cannot add a server or widen what one may do.
/// The file has the same shape as on Windows.
public struct McpServer: Codable, Equatable {
    public var name: String
    /// For an HTTP server: its address on this computer, such as http://127.0.0.1:8088/mcp.
    public var endpoint: String
    /// The tools the agent may call. A tool the server offers that is not listed here cannot be called.
    public var tools: [String]
    /// For a server started as a program: its full path, with `arguments`.
    public var executable: String?
    public var arguments: [String]?
    /// The resource URIs the agent may read.
    public var resources: [String]?
    public var enabled: Bool

    public init(name: String, endpoint: String = "", tools: [String] = [], executable: String? = nil, arguments: [String]? = nil, resources: [String]? = nil, enabled: Bool = true) {
        self.name = name; self.endpoint = endpoint; self.tools = tools; self.executable = executable; self.arguments = arguments; self.resources = resources; self.enabled = enabled
    }

    enum CodingKeys: String, CodingKey, CaseIterable { case name = "Name", endpoint = "Endpoint", tools = "Tools", executable = "Executable", arguments = "Arguments", resources = "Resources", enabled = "Enabled" }
    struct AnyKey: CodingKey { var stringValue: String; var intValue: Int? { nil }; init?(stringValue: String) { self.stringValue = stringValue }; init?(intValue: Int) { nil } }

    /// Keys are matched without regard to case, as the Windows app does.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: AnyKey.self)
        func key(_ wanted: CodingKeys) -> AnyKey? { container.allKeys.first { $0.stringValue.lowercased() == wanted.rawValue.lowercased() } }
        guard let nameKey = key(.name) else { throw MasonError("An MCP registration has no name.") }
        name = try container.decode(String.self, forKey: nameKey)
        endpoint = try key(.endpoint).flatMap { try container.decodeIfPresent(String.self, forKey: $0) } ?? ""
        tools = try key(.tools).flatMap { try container.decodeIfPresent([String].self, forKey: $0) } ?? []
        executable = try key(.executable).flatMap { try container.decodeIfPresent(String.self, forKey: $0) }
        arguments = try key(.arguments).flatMap { try container.decodeIfPresent([String].self, forKey: $0) }
        resources = try key(.resources).flatMap { try container.decodeIfPresent([String].self, forKey: $0) }
        enabled = try key(.enabled).flatMap { try container.decodeIfPresent(Bool.self, forKey: $0) } ?? true
    }

    /// How the server is reached, for display and approvals.
    public var transport: String { executable.map { ([$0] + (arguments ?? [])).joined(separator: " ") } ?? endpoint }
}

public struct McpTool: Equatable { public let name: String; public let description: String }

public enum Mcp {
    public static let protocolVersion = "2025-06-18"
    static let replyLimit = 128 * 1024
    static let deadline: TimeInterval = 20

    static func file(_ home: URL) -> URL { home.appendingPathComponent("mcp-servers.json") }

    /// Every registration, including servers that are switched off.
    public static func all(home: URL) throws -> [McpServer] {
        let path = file(home)
        guard FileManager.default.fileExists(atPath: path.path) else { return [] }
        let data = try Data(contentsOf: path)
        guard data.count <= 32768 else { throw MasonError("The MCP configuration is larger than 32 KiB.") }
        return try JSONDecoder().decode([McpServer].self, from: data)
    }

    /// The servers the agent may use: switched on and valid. A broken file gives no servers, not an error mid-request.
    public static func enabled(home: URL) -> [McpServer] {
        guard let servers = try? all(home: home), servers.count <= 16, Set(servers.map(\.name)).count == servers.count else { return [] }
        return servers.filter { $0.enabled && (try? validate($0)) != nil }
    }

    public static func save(_ servers: [McpServer], home: URL) throws {
        guard servers.count <= 16, Set(servers.map(\.name)).count == servers.count else { throw MasonError("Register up to 16 MCP servers with distinct names.") }
        for server in servers where server.enabled { try validate(server) }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(servers)
        guard data.count <= 32768 else { throw MasonError("The MCP configuration is larger than 32 KiB.") }
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try data.write(to: file(home), options: .atomic)
    }

    public static func validate(_ server: McpServer) throws {
        let name = server.name.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty, name.count <= 64, !name.contains("/"), server.tools.count <= 64 else { throw MasonError("An MCP server needs a short name without a slash and at most 64 tools.") }
        if let resources = server.resources, resources.count > 64 || resources.contains(where: { $0.trimmingCharacters(in: .whitespaces).isEmpty || $0.count > 2000 }) {
            throw MasonError("The resource list of \(server.name) is not valid.")
        }
        if let executable = server.executable {
            guard executable.hasPrefix("/"), FileManager.default.isExecutableFile(atPath: executable), (server.arguments ?? []).count <= 40, !(server.arguments ?? []).contains(where: { $0.count > 4000 }) else {
                throw MasonError("Register a program by its full path. \(executable) is not a program Mason can start.")
            }
            return
        }
        // A literal loopback address only: a name could be pointed somewhere else.
        guard let url = URLComponents(string: server.endpoint), url.scheme == "http", let host = url.host, url.user == nil, url.password == nil, url.fragment == nil,
              host == "::1" || host == "[::1]" || host.range(of: #"^127\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$"#, options: .regularExpression) != nil else {
            throw MasonError("Register an MCP server by its http address on this computer, such as http://127.0.0.1:8088/mcp.")
        }
    }

    // MARK: talking to a server

    public enum Operation { case tools(all: Bool), call(tool: String, arguments: [String: Any]), resources(all: Bool), read(uri: String) }

    /// Connects, does one thing and disconnects. Returns the result as JSON text.
    public static func request(_ server: McpServer, _ operation: Operation) async throws -> String {
        try validate(server)
        if case .call(let tool, _) = operation, !server.tools.contains(tool) { throw MasonError("\(tool) is not in the tools allowed for \(server.name).") }
        if case .read(let uri) = operation, server.resources?.contains(uri) != true { throw MasonError("\(uri) is not in the resources allowed for \(server.name).") }
        let link: McpLink = server.executable != nil ? try McpStdio(server) : McpHttp(server)
        defer { link.close() }
        let hello = try await link.send("initialize", ["protocolVersion": protocolVersion, "capabilities": [String: Any](), "clientInfo": ["name": "Mason Agent", "version": "2.0"]], id: 1)
        guard (hello as? [String: Any])?["protocolVersion"] as? String == protocolVersion else { throw MasonError("\(server.name) does not speak MCP \(protocolVersion).") }
        _ = try await link.send("notifications/initialized", [:], id: nil)
        switch operation {
        case .read(let uri):
            return try text(try await link.send("resources/read", ["uri": uri], id: 2))
        case .call(let tool, let arguments):
            let result = try await link.send("tools/call", ["name": tool, "arguments": arguments], id: 2)
            if (result as? [String: Any])?["isError"] as? Bool == true { throw MasonError("The MCP tool reported a failure: " + (try text(result))) }
            return try text(result)
        case .tools(let all), .resources(let all):
            var isResources = false
            if case .resources = operation { isResources = true }
            var found: [Any] = [], cursor: String?
            for page in 0..<16 {
                let listed = try await link.send(isResources ? "resources/list" : "tools/list", cursor.map { ["cursor": $0] } ?? [:], id: 2 + page) as? [String: Any] ?? [:]
                for item in listed[isResources ? "resources" : "tools"] as? [[String: Any]] ?? [] {
                    let key = item[isResources ? "uri" : "name"] as? String ?? ""
                    if all || (isResources ? server.resources?.contains(key) == true : server.tools.contains(key)) { found.append(item) }
                }
                cursor = listed["nextCursor"] as? String
                if cursor?.isEmpty != false { return try text(found) }
            }
            throw MasonError("\(server.name) listed more pages than Mason reads.")
        }
    }

    /// Every tool the server offers, whatever the allowlist says, so the user can choose.
    public static func discover(_ server: McpServer) async throws -> [McpTool] {
        let listed = try JSONSerialization.jsonObject(with: Data(try await request(server, .tools(all: true)).utf8)) as? [[String: Any]] ?? []
        return listed.map { McpTool(name: $0["name"] as? String ?? "", description: $0["description"] as? String ?? "") }
    }

    static func text(_ value: Any) throws -> String {
        String(decoding: try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .fragmentsAllowed]), as: UTF8.self)
    }

    /// The reply with this id out of one JSON-RPC message; nil when the message is something else.
    static func match(_ json: String, id: Int) throws -> Any? {
        guard let object = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any], (object["id"] as? Int) == id else { return nil }
        if let error = object["error"] { throw MasonError("The MCP server answered with an error: " + ((try? text(error)) ?? "unknown")) }
        guard let result = object["result"] else { throw MasonError("The MCP server sent a reply without a result.") }
        return result
    }
}

protocol McpLink {
    /// Sends one message. With an id it waits for and returns that reply's result; without one it is a notification.
    func send(_ method: String, _ parameters: [String: Any], id: Int?) async throws -> Any
    func close()
}

/// Streamable HTTP: each message is a POST, answered with JSON or a short event stream.
final class McpHttp: NSObject, McpLink, URLSessionTaskDelegate, @unchecked Sendable {
    let server: McpServer
    var sessionId: String?
    lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = Mcp.deadline; configuration.timeoutIntervalForResource = Mcp.deadline
        return URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
    }()
    init(_ server: McpServer) { self.server = server }

    /// A registered server must answer itself; a redirect could lead off this computer.
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }

    func perform(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        try await withCheckedThrowingContinuation { continuation in
            session.dataTask(with: request) { data, response, error in
                if let response = response as? HTTPURLResponse { continuation.resume(returning: (data ?? Data(), response)) }
                else { continuation.resume(throwing: MasonError("\(self.server.name) did not answer at \(self.server.endpoint)" + (error.map { ": \($0.localizedDescription)" } ?? "."))) }
            }.resume()
        }
    }

    func send(_ method: String, _ parameters: [String: Any], id: Int?) async throws -> Any {
        guard let url = URL(string: server.endpoint) else { throw MasonError("\(server.endpoint) is not an address.") }
        var message: [String: Any] = ["jsonrpc": "2.0", "method": method, "params": parameters]
        if let id { message["id"] = id }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.httpBody = try JSONSerialization.data(withJSONObject: message)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json, text/event-stream", forHTTPHeaderField: "Accept")
        if let sessionId { request.setValue(sessionId, forHTTPHeaderField: "Mcp-Session-Id") }
        if method != "initialize" { request.setValue(Mcp.protocolVersion, forHTTPHeaderField: "MCP-Protocol-Version") }
        let (data, response) = try await perform(request)
        guard (200..<300).contains(response.statusCode) else { throw MasonError("\(server.name) answered \(method) with HTTP \(response.statusCode).") }
        if method == "initialize", let given = response.value(forHTTPHeaderField: "Mcp-Session-Id") { sessionId = given }
        guard let id else { return NSNull() }
        guard data.count <= Mcp.replyLimit else { throw MasonError("The reply from \(server.name) is larger than 128 KiB.") }
        let body = String(decoding: data, as: UTF8.self)
        if (response.value(forHTTPHeaderField: "Content-Type") ?? "").lowercased().hasPrefix("text/event-stream") {
            for event in body.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n\n") {
                let json = event.split(separator: "\n").filter { $0.hasPrefix("data:") }.map { $0.dropFirst(5).trimmingCharacters(in: .whitespaces) }.joined(separator: "\n")
                if !json.isEmpty, let result = try Mcp.match(json, id: id) { return result }
            }
        } else if let result = try Mcp.match(body, id: id) { return result }
        throw MasonError("\(server.name) did not answer \(method).")
    }

    func close() {
        if let sessionId, let url = URL(string: server.endpoint) {
            var request = URLRequest(url: url)
            request.httpMethod = "DELETE"
            request.setValue(sessionId, forHTTPHeaderField: "Mcp-Session-Id")
            request.setValue(Mcp.protocolVersion, forHTTPHeaderField: "MCP-Protocol-Version")
            session.dataTask(with: request).resume()
        }
        session.finishTasksAndInvalidate()
    }
}

/// A server started as a program: one JSON message per line on its standard input and output.
final class McpStdio: McpLink, @unchecked Sendable {
    let server: McpServer
    let process = Process()
    let input = Pipe(), output = Pipe()
    var pending = Data()
    var received = 0

    init(_ server: McpServer) throws {
        self.server = server
        let executable = server.executable ?? ""
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = server.arguments ?? []
        process.currentDirectoryURL = URL(fileURLWithPath: executable).deletingLastPathComponent()
        var environment = ProcessInfo.processInfo.environment
        for variable in ["NODE_OPTIONS", "NODE_PATH", "PYTHONPATH", "PYTHONHOME"] { environment.removeValue(forKey: variable) }
        process.environment = environment
        process.standardInput = input; process.standardOutput = output; process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { throw MasonError("\(server.name) could not be started: \(error.localizedDescription)") }
        // Close this side's copies of the server's ends, so its exit is seen as the end of its output.
        try? output.fileHandleForWriting.close()
        try? input.fileHandleForReading.close()
        // Whatever happens, the server does not outlive the deadline.
        let started = process
        DispatchQueue.global().asyncAfter(deadline: .now() + Mcp.deadline) { if started.isRunning { started.terminate() } }
    }

    func send(_ method: String, _ parameters: [String: Any], id: Int?) async throws -> Any {
        var message: [String: Any] = ["jsonrpc": "2.0", "method": method, "params": parameters]
        if let id { message["id"] = id }
        var line = try JSONSerialization.data(withJSONObject: message)
        guard line.count <= 32768 else { throw MasonError("The request to \(server.name) is larger than 32 KiB.") }
        line.append(0x0A)
        let payload = line
        let name = server.name, input = input, output = output
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Any, Error>) in
            DispatchQueue.global().async { [self] in
                do {
                    try input.fileHandleForWriting.write(contentsOf: payload)
                    guard let id else { continuation.resume(returning: NSNull()); return }
                    while true {
                        while let end = pending.firstIndex(of: 0x0A) {
                            let text = String(decoding: pending[pending.startIndex..<end], as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                            pending = Data(pending[pending.index(after: end)...])
                            if !text.isEmpty, let result = try Mcp.match(text, id: id) { continuation.resume(returning: result); return }
                        }
                        let more = output.fileHandleForReading.availableData
                        if more.isEmpty { throw MasonError("\(name) stopped before answering \(method).") }
                        received += more.count
                        if received > Mcp.replyLimit { throw MasonError("The reply from \(name) is larger than 128 KiB.") }
                        pending.append(more)
                    }
                } catch { continuation.resume(throwing: (error as? MasonError) ?? MasonError("\(name) could not be reached: \(error.localizedDescription)")) }
            }
        }
    }

    func close() {
        try? input.fileHandleForWriting.close()
        let started = process
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.25) { if started.isRunning { started.terminate() } }
    }
}
