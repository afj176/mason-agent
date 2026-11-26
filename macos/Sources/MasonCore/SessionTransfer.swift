import Foundation

// Forking a session from an earlier request, and moving a session between computers or projects as a file.

public enum SessionTransfer {
    public static let format = 1
    public static let maxBytes = 24 * 1024 * 1024

    public struct Fork {
        public var session: Session
        /// The request the fork stops before, for the user to edit and send again.
        public var request: String
        public var restored: [String] = []
        public var skipped: [String] = []
    }

    struct Exported: Codable {
        var format: Int
        var app: String
        var at: Date
        var sanitized: Bool
        var session: Session
    }

    /// The position of the user message that began turn `index`, counted from the end so that messages a compaction
    /// replaced do not shift it. Nil when that request has been summarized away.
    static func messageIndex(_ session: Session, turn index: Int) -> Int? {
        var remaining = session.turns.count - index
        for position in session.messages.indices.reversed() where session.messages[position].role == "user" {
            remaining -= 1
            if remaining == 0 { return position }
        }
        return nil
    }

    /// A new session holding the conversation before request number `index` (from 0). With `restoreFiles`, the project
    /// goes back to how it was before that request: each later request's changes are undone, newest first. A file
    /// changed since is left alone and reported.
    public static func fork(_ source: inout Session, store: SessionStore, turn index: Int, restoreFiles: Bool) throws -> Fork {
        guard source.state != "Running" else { throw MasonError("Stop the request that is running before forking.") }
        guard source.turns.indices.contains(index) else { throw MasonError("Give the number of a request in this session.") }
        guard let cut = messageIndex(source, turn: index) else { throw MasonError("That request was replaced by a summary when the session was compacted. Fork from a later one.") }
        let turn = source.turns[index]
        var restored: [String] = [], skipped: [String] = []
        if restoreFiles {
            while let latest = source.turns.lastIndex(where: { !$0.changes.isEmpty && !$0.undone }), latest >= index {
                guard let result = try Agent.undoLatest(&source, store: store) else { break }
                restored += result.restored; skipped += result.skipped
            }
        }
        var fork = try store.create(workspace: source.workspace, permission: source.permission == .auto ? .reviewEdits : source.permission)
        fork.title = (source.title.count > 46 ? String(source.title.prefix(46)) + "…" : source.title) + " · fork"
        fork.askOnly = source.askOnly
        fork.agent = source.agent
        fork.messages = Array(source.messages.prefix(cut))
        fork.turns = Array(source.turns.prefix(index))
        fork.activities = Array(source.activities.prefix(min(turn.firstActivity, source.activities.count)))
        fork.archived = source.archived
        fork.compactions = source.compactions
        fork.milestones = source.milestones
        fork.detail = "Forked before “\(turn.request.count > 50 ? String(turn.request.prefix(50)) + "…" : turn.request)”. Edit the request and send it."
            + (restoreFiles ? " Files put back: \(restored.count)" + (skipped.isEmpty ? "." : "; changed since and left alone: " + skipped.joined(separator: ", ") + ".") : "")
        try store.save(fork)
        return Fork(session: fork, request: turn.request, restored: restored, skipped: skipped)
    }

    static let secrets = try! NSRegularExpression(pattern: #"(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[abpr]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|(?<=(?:api[_-]?key|token|secret|password)\s{0,3}[:=]\s{0,3}["']?)[^\s"']{6,})"#, options: [.caseInsensitive])

    /// The text with the project's path and anything that looks like a key or password taken out.
    static func clean(_ text: String, root: String) -> String {
        let moved = text.replacingOccurrences(of: root, with: "<workspace>")
        return secrets.stringByReplacingMatches(in: moved, range: NSRange(moved.startIndex..., in: moved), withTemplate: "<redacted>")
    }

    /// The session as a file. With `sanitize`, the project's path and likely secrets are removed, and so are the
    /// contents of the files it changed.
    public static func export(_ session: Session, sanitize: Bool) throws -> Data {
        var copy = session
        copy.desktop = nil
        copy.pendingInput = nil
        if sanitize {
            let root = session.workspace
            copy.workspace = "<workspace>"
            copy.title = clean(copy.title, root: root)
            copy.detail = clean(copy.detail, root: root)
            for index in copy.messages.indices {
                copy.messages[index].content = clean(copy.messages[index].content, root: root)
                if let calls = copy.messages[index].toolCalls { copy.messages[index].toolCalls = calls.map { ToolCall(id: $0.id, name: $0.name, arguments: clean($0.arguments, root: root)) } }
            }
            if let archived = copy.archived { copy.archived = archived.map { ChatMessage(role: $0.role, content: clean($0.content, root: root), toolCalls: $0.toolCalls, toolCallId: $0.toolCallId) } }
            for index in copy.activities.indices {
                copy.activities[index].subject = clean(copy.activities[index].subject, root: root)
                copy.activities[index].result = clean(copy.activities[index].result, root: root)
            }
            for index in copy.turns.indices {
                copy.turns[index].request = clean(copy.turns[index].request, root: root)
                copy.turns[index].answer = clean(copy.turns[index].answer, root: root)
                copy.turns[index].changes = []
                if let output = copy.turns[index].verification?.output { copy.turns[index].verification?.output = clean(output, root: root) }
            }
            copy.comments = nil
            copy.pendingNote = nil
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(Exported(format: format, app: "Mason Agent", at: Date(), sanitized: sanitize, session: copy))
    }

    /// Adds an exported session to a project under a new identity. File history and undo stay with the original.
    public static func importSession(_ data: Data, store: SessionStore, workspace: String) throws -> Session {
        guard data.count <= maxBytes else { throw MasonError("The session file is larger than 24 MiB.") }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let exported = try? decoder.decode(Exported.self, from: data) else { throw MasonError("That file is not a Mason session export.") }
        guard exported.format == format else { throw MasonError("That session export is in a format this version does not read.") }
        var target = try store.create(workspace: workspace)
        let source = exported.session
        target.title = source.title + " · imported"
        target.messages = source.messages
        target.activities = source.activities
        target.turns = source.turns.map { var turn = $0; turn.changes = []; turn.undone = false; return turn }
        target.askOnly = source.askOnly
        target.agent = source.agent
        target.archived = source.archived
        target.compactions = source.compactions
        target.detail = "Imported \(exported.sanitized ? "a sanitized" : "a") session. File history and undo stay with the original."
        try store.save(target)
        return target
    }

    public static let help = """
    /fork NUMBER [files]     A new session from before request NUMBER; "files" also puts the project back as it was then
    /export FILE [sanitized] Save this session as a file; "sanitized" removes the project's path, likely secrets and file contents
    /import FILE             Add an exported session to this project
    """
}
