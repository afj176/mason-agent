import Foundation

/// Models beside the one that does the work. A reviewer reads what a session changed for `/review`; a visual model
/// looks at pictures of a window. Either can be a different model on a different local server. Roles only make
/// proposals: the engine keeps the workspace, the permissions and the checks.
public struct ModelRoles: Codable, Equatable {
    public var reviewer: ModelEndpoint?
    public var visual: ModelEndpoint?
    /// How many times one review may ask the reviewer.
    public var limit: Int = 64
    /// A folder outside any project where every model call is written down, for looking at a decision afterwards.
    public var trace: String?

    public init() {}
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        reviewer = try values.decodeIfPresent(ModelEndpoint.self, forKey: .reviewer)
        visual = try values.decodeIfPresent(ModelEndpoint.self, forKey: .visual)
        limit = try values.decodeIfPresent(Int.self, forKey: .limit) ?? 64
        trace = try values.decodeIfPresent(String.self, forKey: .trace)
    }
}

/// A model in a role: it counts its calls against a limit and, when tracing is on, writes each call down.
public final class RoleModel: ChatModel, @unchecked Sendable {
    public let base: ChatModel
    let role: String
    let limit: Int?
    let trace: URL?
    private let lock = NSLock()
    private var calls = 0

    public init(_ base: ChatModel, role: String, limit: Int? = nil, trace: URL? = nil) { self.base = base; self.role = role; self.limit = limit; self.trace = trace }
    public var name: String { base.name }
    public func contextWindow() async -> Int? { await base.contextWindow() }

    public func complete(messages: [ChatMessage], tools: [ToolSpec], onText: @escaping (String) -> Void) async throws -> ModelReply {
        lock.lock(); calls += 1; let number = calls; lock.unlock()
        if let limit, number > limit { throw MasonError("The \(role) has been asked \(limit) times, which is its limit (/roles limit N changes it).") }
        let reply = try await base.complete(messages: messages, tools: tools, onText: onText)
        if let trace {
            let record: [String: Any] = [
                "role": role, "model": base.name, "call": number, "at": ISO8601DateFormatter().string(from: Date()),
                "messages": messages.map { ["role": $0.role, "content": $0.content, "toolCalls": ($0.toolCalls ?? []).map { ["name": $0.name, "arguments": $0.arguments] }] as [String: Any] },
                "tools": tools.map(\.name),
                "reply": ["text": reply.text, "toolCalls": reply.toolCalls.map { ["name": $0.name, "arguments": $0.arguments] }, "finishReason": reply.finishReason ?? ""] as [String: Any],
            ]
            try? FileManager.default.createDirectory(at: trace, withIntermediateDirectories: true)
            let stamp = Int(Date().timeIntervalSince1970 * 1000)
            if let data = try? JSONSerialization.data(withJSONObject: record, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) {
                try? data.write(to: trace.appendingPathComponent("\(stamp)-\(role)-\(number).json"), options: .atomic)
            }
        }
        return reply
    }
}

public enum Roles {
    public static func load(home: URL = DataHome.url) -> ModelRoles {
        guard let data = try? Data(contentsOf: home.appendingPathComponent("roles.json")) else { return ModelRoles() }
        return (try? JSONDecoder().decode(ModelRoles.self, from: data)) ?? ModelRoles()
    }

    static func save(_ roles: ModelRoles, home: URL) throws {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try encoder.encode(roles).write(to: home.appendingPathComponent("roles.json"), options: .atomic)
    }

    /// Where traces go for work in `workspace`: nowhere when tracing is off or the folder is inside the project.
    static func traceFolder(_ roles: ModelRoles, workspace: Workspace?) -> URL? {
        guard let trace = roles.trace, !trace.isEmpty else { return nil }
        if let workspace, (realPath(trace) + "/").hasPrefix(realPath(workspace.root.path) + "/") { return nil }
        return URL(fileURLWithPath: trace)
    }

    /// The model that does the work, written down when tracing is on.
    public static func implementer(_ main: ChatModel, workspace: Workspace?, home: URL = DataHome.url) -> ChatModel {
        guard let trace = traceFolder(load(home: home), workspace: workspace) else { return main }
        return RoleModel(main, role: "implementer", trace: trace)
    }

    /// The model that reviews: the reviewer when one is chosen, otherwise the main model, held to the call limit.
    public static func reviewer(main: ChatModel, workspace: Workspace?, home: URL = DataHome.url) -> ChatModel {
        let roles = load(home: home)
        let chosen = roles.reviewer.flatMap { try? OpenAIClient($0) } ?? main
        return RoleModel(chosen, role: "reviewer", limit: roles.limit, trace: traceFolder(roles, workspace: workspace))
    }

    /// The endpoint of the model behind `model`, through any role wrapper.
    static func endpoint(of model: ChatModel) -> ModelEndpoint? {
        (model as? OpenAIClient)?.endpoint ?? ((model as? RoleModel)?.base as? OpenAIClient)?.endpoint
    }

    // MARK: the /roles command, shared by the command line and the app

    public static let usage = """
        /roles                          The models in each role
        /roles reviewer URL MODEL       Use another local model for /review (or: /roles reviewer off)
        /roles visual URL MODEL         Use another local model to look at pictures of a window (or: /roles visual off)
        /roles limit N                  How many times one review may ask the reviewer (1 to 500)
        /roles trace FOLDER             Write every model call down in a folder outside your projects (or: /roles trace off)
        """

    public static func command(_ rest: String, home: URL = DataHome.url) throws -> String {
        let words = rest.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
        var roles = load(home: home)
        func shown() -> String {
            ["reviewer  " + (roles.reviewer.map { "\($0.model) at \($0.url)" } ?? "the model doing the work"),
             "visual    " + (roles.visual.map { "\($0.model) at \($0.url)" } ?? "the model doing the work"),
             "limit     \(roles.limit) reviewer calls in one review",
             "trace     " + (roles.trace ?? "off")].joined(separator: "\n")
        }
        guard let verb = words.first?.lowercased() else { return shown() }
        switch verb {
        case "reviewer", "visual":
            let chosen: ModelEndpoint?
            if words.count == 2, words[1].lowercased() == "off" { chosen = nil }
            else if words.count == 3 { chosen = ModelEndpoint(url: try ModelEndpoint.validated(words[1]).absoluteString, model: words[2]) }
            else { throw MasonError("Use /roles \(verb) URL MODEL, or /roles \(verb) off.") }
            if verb == "reviewer" { roles.reviewer = chosen } else { roles.visual = chosen }
        case "limit":
            guard words.count == 2, let limit = Int(words[1]), (1...500).contains(limit) else { throw MasonError("Use /roles limit N, from 1 to 500.") }
            roles.limit = limit
        case "trace":
            guard words.count >= 2 else { throw MasonError("Use /roles trace FOLDER, or /roles trace off.") }
            let folder = words.dropFirst().joined(separator: " ")
            if folder.lowercased() == "off" { roles.trace = nil } else {
                let path = URL(fileURLWithPath: (folder as NSString).expandingTildeInPath).standardizedFileURL.path
                try FileManager.default.createDirectory(atPath: path, withIntermediateDirectories: true)
                roles.trace = realPath(path)
            }
        default:
            return usage
        }
        try save(roles, home: home)
        return shown()
    }
}
