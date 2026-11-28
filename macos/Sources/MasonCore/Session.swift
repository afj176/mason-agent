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
    /// `skipped`: Mason did not run the call because the model had already made it twice in a row with the same result.
    public enum State: String, Codable { case running, completed, failed, denied, skipped }
    public var id = UUID()
    public var tool: String
    public var subject: String
    public var state: State
    public var result: String
    public var at = Date()

    /// How a tool is named on screen: "write_file" reads "write file".
    public static func label(_ tool: String) -> String { tool.replacingOccurrences(of: "_", with: " ") }
}

/// Set when Mason stopped a request because the model kept making the same call and getting the same result.
/// Cleared when the next request starts.
public struct RepeatStop: Codable, Equatable {
    /// The raw tool name, as the model called it.
    public var tool: String
    /// What the call was about, as its activity row shows it: a path, a command…
    public var subject: String
    /// How many times in a row the model asked for the call, the skipped one included.
    public var times: Int
    /// The display name of the model that repeated itself, or empty when it is not known.
    public var model: String
    public init(tool: String, subject: String, times: Int = 3, model: String) { self.tool = tool; self.subject = subject; self.times = times; self.model = model }

    public static let headline = "The model repeated itself"
    public static let result = "Not run: the model already made this exact call twice in a row and got the same result both times."
    /// Added to what the model sees after the second identical call came back the same, so the stop is no surprise.
    public static let warning = "\n\n[Mason: this is the same result as your previous identical call. Use it, try something different, or finish. Mason will not run this exact call a third time.]"
    public static let origin = "This came from the model, not Mason."
    public static let next = "Send another message to continue, or choose a larger model with /model."

    var speaker: String { model.trimmingCharacters(in: .whitespaces).isEmpty ? "The model" : model }
    var call: String { Activity.label(tool) + (subject.isEmpty ? "" : " " + subject.replacingOccurrences(of: "\n", with: " ")) }
    static func ending(kept: Bool) -> String { kept ? "Your work so far is kept." : "Nothing was changed." }

    /// The session's detail line.
    public func detail(kept: Bool) -> String {
        "\(speaker) kept repeating \(call) with the same result, so Mason stopped it. " + RepeatStop.ending(kept: kept)
    }
    /// The sentences the terminal and the app show under the outcome.
    public func explanation(kept: Bool) -> String {
        "\(speaker) asked for \(call) \(times) times in a row and got the same result each time. Mason skipped the last one and stopped here instead of looping. " + RepeatStop.ending(kept: kept)
    }
}

/// How a request ended, saved with the session and cleared when the next request starts. `code` is one of
/// `RunEnding.codes`; `kept` is how many files the request changed that are still changed when it ended.
public struct RunEnding: Codable, Equatable {
    public var code: String
    public var kept: Int
    public init(code: String, kept: Int = 0) { self.code = code; self.kept = kept }

    public enum Tone: String, Sendable { case success, warning, accent, neutral, danger }
    public struct Style: Equatable, Sendable {
        public let glyph: String
        public let tone: Tone
        public let headline: String
        /// Who or what ended the request, in a few words.
        public let cause: String
    }

    /// Every code, in the order of the table.
    public static let codes = ["done", "unverified", "question", "model_loop", "model_stuck", "step_limit", "time_limit", "blocked", "stopped", "model_unavailable", "failed"]

    /// The one table of endings: glyph, tone, headline and cause for a code, or nil for an unknown one (an older
    /// session), which then shows as its state did before.
    public static func style(_ code: String) -> Style? {
        switch code {
        case "done": return Style(glyph: "✓", tone: .success, headline: "Done", cause: "model finished")
        case "unverified": return Style(glyph: "◐", tone: .warning, headline: "Needs a check", cause: "checks")
        case "question": return Style(glyph: "?", tone: .accent, headline: "Needs your answer", cause: "model asked")
        case "model_loop": return Style(glyph: "↻", tone: .warning, headline: "Model looped", cause: "model")
        case "model_stuck": return Style(glyph: "⊘", tone: .warning, headline: "Model stuck", cause: "model")
        case "step_limit": return Style(glyph: "⏸", tone: .warning, headline: "Paused at the step limit", cause: "Mason limit")
        case "time_limit": return Style(glyph: "⏸", tone: .warning, headline: "Paused at the time limit", cause: "Mason limit")
        case "blocked": return Style(glyph: "⊘", tone: .warning, headline: "Blocked by checks", cause: "project")
        case "stopped": return Style(glyph: "■", tone: .neutral, headline: "Stopped by you", cause: "you")
        case "model_unavailable": return Style(glyph: "✗", tone: .danger, headline: "Model unavailable", cause: "model server")
        case "failed": return Style(glyph: "✗", tone: .danger, headline: "Failed", cause: "Mason")
        default: return nil
        }
    }
    public var style: Style? { RunEnding.style(code) }

    /// The line said after the detail, by code, in the terminal and on the end card; nil when there is none.
    /// The app has no slash commands, so with `terminal` false the hints name the place instead.
    public static func hint(_ code: String, terminal: Bool = true) -> String? {
        let larger = terminal ? "choose a larger model with /model." : "choose a larger model."
        switch code {
        case "model_loop": return RepeatStop.origin + " Send another message to continue, or " + larger
        case "model_stuck": return RepeatStop.origin + " Try rephrasing, or " + larger
        case "step_limit", "time_limit": return "Send another message to continue from here."
        case "model_unavailable": return terminal ? "Start it again with /models, or choose another with /model." : "Start it again in Models, or choose another model."
        default: return nil
        }
    }
    /// The endings that get a card at the end of their request in the app.
    public static let carded: Set<String> = ["model_loop", "model_stuck", "step_limit", "time_limit", "model_unavailable", "blocked"]
    /// "1 file kept", "3 files kept".
    public static func keptText(_ count: Int) -> String { "\(count) file\(count == 1 ? "" : "s") kept" }
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
    /// What Auto does about the network in this session: "ask", "off", or nil to follow the computer's setting.
    public var network: String?
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
    /// A note the model suggested with suggest_note, until the user remembers or dismisses it.
    public var suggestedNote: NoteSuggestion?
    /// Set when the latest request ended because the model repeated one call; cleared when the next request starts.
    public var repeatStop: RepeatStop?
    /// How the latest request ended; nil while one runs and in sessions saved before endings existed.
    public var ending: RunEnding?
    /// The reasoning level sent with this session's requests (off, low, medium, high); nil leaves it to the server.
    public var reasoning: String?
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
