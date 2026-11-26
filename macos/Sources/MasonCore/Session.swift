import Foundation

/// What Mason may do without asking.
public enum Permission: String, Codable, CaseIterable {
    /// Reads and answers; no edits, no commands.
    case readOnly
    /// Asks before each edit and each command.
    case reviewEdits
    /// Edits apply without asking; commands still ask.
    case workspaceEdits
    /// Edits apply and commands run without asking, inside the macOS sandbox limited to the project.
    case auto

    public var label: String {
        switch self {
        case .readOnly: return "read-only"
        case .reviewEdits: return "review edits"
        case .workspaceEdits: return "workspace edits"
        case .auto: return "auto (sandboxed)"
        }
    }
}

public struct ToolCall: Codable, Equatable {
    public var id: String
    public var name: String
    /// JSON object text, exactly as the model produced it.
    public var arguments: String
    public init(id: String, name: String, arguments: String) { self.id = id; self.name = name; self.arguments = arguments }
}

public struct ChatMessage: Codable, Equatable {
    public var role: String
    public var content: String
    public var toolCalls: [ToolCall]?
    public var toolCallId: String?
    /// Pictures that go with a chat message, each as a data: address. The model receives them beside the text.
    public var images: [String]? = nil
    public init(role: String, content: String, toolCalls: [ToolCall]? = nil, toolCallId: String? = nil) {
        self.role = role; self.content = content; self.toolCalls = toolCalls; self.toolCallId = toolCallId
    }
}

public struct Activity: Codable, Equatable, Identifiable {
    public enum State: String, Codable { case running, completed, failed, denied }
    public var id = UUID()
    public var tool: String
    public var subject: String
    public var state: State
    public var result: String
    public var at = Date()
}

/// One file as it was before a request first touched it, so the request can be undone.
public struct FileChange: Codable, Equatable {
    public var path: String
    /// nil when the request created the file.
    public var before: String?
    public var after: String
    /// Set when a side is binary or too large to keep as text; the content is in the blob store under this SHA-256.
    public var beforeBlob: String? = nil
    public var afterBlob: String? = nil
    /// True when a command deleted the file.
    public var afterMissing: Bool? = nil
    /// True when a command, not an edit tool, made the change.
    public var byCommand: Bool? = nil
}

/// A message sent while a request was running. A steering message interrupts the request and is handled next;
/// a queued one waits until the request completes.
public struct FollowUp: Codable, Equatable, Identifiable {
    public var id = UUID()
    public var text: String
    public var steer: Bool
    public init(text: String, steer: Bool) { self.text = text; self.steer = steer }
}

/// What Mason itself found when it ran the project's `verify` workflow around a request that changed files.
/// The model does not write this; it is the result of the command.
public struct Verification: Codable, Equatable {
    public enum Verdict: String, Codable {
        case passed            ///< It passes after the changes (and passed, or could not be run, before).
        case fixed             ///< It failed before the request and passes after it.
        case regression        ///< It passed before the request and fails after it.
        case failingBefore     ///< It failed before the request and still fails.
        case failed            ///< It fails after the changes; there is no result from before to compare with.
        case notRun            ///< The workflow exists but Mason was not allowed to run it.
    }
    public var workflow: String
    public var command: String
    public var verdict: Verdict
    public var exitCode: Int32?
    public var baselineExitCode: Int32?
    /// The end of the output of the run after the changes, or why it was not run.
    public var output: String
    /// How many times the model was handed a regression to repair.
    public var repairs: Int

    public var summary: String {
        switch verdict {
        case .passed: return "\(workflow) passed"
        case .fixed: return "\(workflow) passes now; it failed before this request"
        case .regression: return "\(workflow) fails now; it passed before this request"
        case .failingBefore: return "\(workflow) fails, as it did before this request"
        case .failed: return "\(workflow) failed"
        case .notRun: return "\(workflow) was not run"
        }
    }
}

public struct Turn: Codable, Equatable, Identifiable {
    public var id = UUID()
    public var request: String
    public var answer: String = ""
    public var changes: [FileChange] = []
    public var undone = false
    public var firstActivity = 0
    /// Set when the project has a `verify` workflow and the request changed files.
    public var verification: Verification?
    /// Set in Auto when Mason worked out checks from the request and ran them itself.
    public var checks: DerivedReceipt?

    public init(request: String, firstActivity: Int = 0) { self.request = request; self.firstActivity = firstActivity }
}

public struct Session: Codable, Identifiable {
    public var id = UUID()
    public var title = "New session"
    public var workspace: String
    public var permission: Permission = .reviewEdits
    public var askOnly = false
    public var state = "Ready"
    public var detail = ""
    public var updated = Date()
    public var messages: [ChatMessage] = []
    public var activities: [Activity] = []
    public var turns: [Turn] = []
    public var promptTokens: Int?
    /// Told to the model with the next request: what the user undid or redid outside the conversation.
    public var pendingNote: String?
    /// The custom agent whose instructions frame each request, if one is selected.
    public var agent: String?
    /// Messages a compaction replaced with a summary, oldest first.
    public var archived: [ChatMessage]?
    public var compactions: Int?
    /// Follow-ups waiting their turn, oldest first. "/queue" resumes them after a stop.
    public var pendingInput: [FollowUp]?
    /// Comments on lines of the project, sent with the next request.
    public var comments: [ReviewComment]?
    /// The saved plan, if the user made one.
    public var milestones: [Milestone]?
    /// The window of another app the user attached, which Mason may read and, with approval, operate.
    public var desktop: DesktopWindow?
    public init(workspace: String) { self.workspace = workspace }
}

/// Where Mason keeps sessions and settings. `MASON_DATA_HOME` overrides the platform location.
public enum DataHome {
    public static var url: URL {
        if let custom = ProcessInfo.processInfo.environment["MASON_DATA_HOME"], !custom.isEmpty { return URL(fileURLWithPath: custom) }
        #if os(macOS)
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first ?? URL(fileURLWithPath: NSHomeDirectory() + "/Library/Application Support")
        #else
        let base = ProcessInfo.processInfo.environment["XDG_DATA_HOME"].map { URL(fileURLWithPath: $0) } ?? URL(fileURLWithPath: NSHomeDirectory() + "/.local/share")
        #endif
        return base.appendingPathComponent("MasonAgent")
    }
}

/// Sessions as one JSON file each, written atomically. A session interrupted mid-run is marked so on load; nothing is replayed.
public final class SessionStore {
    public let folder: URL
    public private(set) var sessions: [Session] = []
    let encoder: JSONEncoder = { let e = JSONEncoder(); e.outputFormatting = [.prettyPrinted, .sortedKeys]; e.dateEncodingStrategy = .iso8601; return e }()
    let decoder: JSONDecoder = { let d = JSONDecoder(); d.dateDecodingStrategy = .iso8601; return d }()

    public init(home: URL = DataHome.url) throws {
        folder = home.appendingPathComponent("sessions")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for file in (try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) where file.pathExtension == "json" {
            guard let data = try? Data(contentsOf: file), var session = try? decoder.decode(Session.self, from: data) else { continue }
            if session.state == "Running" || session.state == "Awaiting review" {
                session.state = "Interrupted"
                session.detail = "The previous run was interrupted. Check the changes before continuing; nothing has been replayed."
                for index in session.activities.indices where session.activities[index].state == .running { session.activities[index].state = .failed }
            }
            sessions.append(session)
        }
        sessions.sort { $0.updated > $1.updated }
    }

    public func sessions(in workspace: String) -> [Session] { sessions.filter { $0.workspace == workspace } }

    public func create(workspace: String, permission: Permission = .reviewEdits) throws -> Session {
        var session = Session(workspace: workspace)
        session.permission = permission
        try save(session)
        return session
    }

    public func save(_ session: Session) throws {
        var copy = session
        copy.updated = Date()
        let data = try encoder.encode(copy)
        try data.write(to: folder.appendingPathComponent(copy.id.uuidString + ".json"), options: .atomic)
        if let index = sessions.firstIndex(where: { $0.id == copy.id }) { sessions[index] = copy } else { sessions.insert(copy, at: 0) }
    }

    public func delete(_ id: UUID) throws {
        try? FileManager.default.removeItem(at: folder.appendingPathComponent(id.uuidString + ".json"))
        sessions.removeAll { $0.id == id }
    }
}
