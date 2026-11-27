import Foundation

/// One line or card of the Moments feed, newest first: where a request began, or a scene of the run.
public enum MomentItem: Equatable {
    /// "You asked: …", placed just before the first moment of that request.
    case asked(String)
    case scene(ActivityScene)
}

/// Something the latest run made, for the side panel's shelf: a file it created or changed, or a page it checked.
public struct MadeThing: Equatable {
    public enum Kind: Equatable { case file, page }
    public let kind: Kind
    /// What the chip says: the file's name, or the page's path with "page".
    public let label: String
    /// The path of the change (as the session recorded it) or the page that was checked.
    public let target: String
    /// True for a file the session created, which opens as the file rather than as a change.
    public let created: Bool
    /// The call that made it, so a change can open as a moment.
    public let call: UUID
}

/// The Moments feed's logic, mirroring the Windows viewer: the order of the feed, the "You asked" lines, relative times,
/// what the run made so far, the end-of-run facts, and which moment is open.
public enum Moments {
    /// The most moments the feed shows.
    public static let feedLimit = 120

    // MARK: Time

    /// "just now", "5 m", "2 h", then the day ("Mar 4").
    public static func ago(_ at: Date, now: Date = Date()) -> String {
        let seconds = now.timeIntervalSince(at)
        if seconds < 45 { return "just now" }
        if seconds < 3600 { return "\(max(1, Int(seconds / 60))) m" }
        if seconds < 86_400 { return "\(Int(seconds / 3600)) h" }
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.dateFormat = "MMM d"
        return format.string(from: at)
    }

    // MARK: The feed

    /// Where each request starts in the call list and what was asked, clipped to 160 characters, oldest first.
    public static func requests(_ turns: [Turn], activityCount: Int) -> [(position: Int, text: String)] {
        turns.map { turn in
            var text = turn.request.trimmingCharacters(in: .whitespacesAndNewlines)
            if text.count > 160 { text = String(text.prefix(159)) + "…" }
            return (min(max(0, turn.firstActivity), activityCount), text)
        }
    }

    /// The feed, newest first: each scene, and each request's line just below its own first moment (so above the
    /// moments of the request before it). A request that has not reached a step yet sits on top.
    public static func feed(scenes: [ActivityScene], requests: [(position: Int, text: String)]) -> [MomentItem] {
        // (position, order, sequence): scenes sort above a request line at the same position, later items first.
        var timeline: [(position: Int, order: Int, sequence: Int, item: MomentItem)] = []
        for (offset, request) in requests.enumerated() where !request.text.isEmpty {
            timeline.append((request.position, 0, offset, .asked(request.text)))
        }
        for (offset, scene) in scenes.enumerated() { timeline.append((scene.first, 1, offset, .scene(scene))) }
        timeline.sort { a, b in
            if a.position != b.position { return a.position > b.position }
            if a.order != b.order { return a.order > b.order }
            return a.sequence > b.sequence
        }
        return timeline.map(\.item)
    }

    /// The files an edit scene worked on, newest first, each once.
    public static func editPaths(_ scene: ActivityScene, _ activities: [Activity]) -> [String] {
        var seen: Set<String> = [], ordered: [String] = []
        for index in scene.indices.reversed() where index < activities.count {
            if let path = ActivityScenes.path(of: activities[index]), seen.insert(path.lowercased()).inserted { ordered.append(path) }
        }
        return ordered
    }

    /// The last call of a scene that worked on a path.
    public static func lastCall(_ scene: ActivityScene, _ activities: [Activity], path: String) -> Activity? {
        scene.indices.reversed().lazy.filter { $0 < activities.count }.map { activities[$0] }
            .first { ActivityScenes.path(of: $0)?.lowercased() == path.lowercased() }
    }

    /// A scene's label with a capital first letter, for a quiet line.
    public static func sentence(_ label: String) -> String {
        guard let first = label.first else { return label }
        return first.uppercased() + label.dropFirst()
    }

    /// The top folders a read scene looked in, for its quiet line: "src/, tests/".
    public static func readPlaces(_ scene: ActivityScene, _ activities: [Activity]) -> String {
        guard scene.kind == .read else { return "" }
        let calls = scene.indices.filter { $0 < activities.count }.map { activities[$0] }
        return ActivityScenes.folders(calls).prefix(3).map(\.folder).joined(separator: ", ")
    }

    // MARK: Cards

    /// How many lines a file has.
    public static func lineCount(_ text: String) -> Int {
        let breaks = text.reduce(0) { $0 + ($1 == "\n" ? 1 : 0) }
        return breaks + (text.isEmpty || text.hasSuffix("\n") ? 0 : 1)
    }

    /// The first lines of a file that are not blank.
    public static func head(_ text: String, lines: Int = 3) -> [String] {
        Array(text.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n", omittingEmptySubsequences: false)
            .map(String.init).filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }.prefix(lines))
    }

    /// The first line a change added, trimmed; nil for a binary or very large file or a change that only removed lines.
    public static func firstAdded(_ change: FileChange) -> String? {
        guard !change.isOpaque else { return nil }
        return Diff.lines(before: change.before, after: change.deleted ? "" : change.after)
            .first { $0.kind == .added }?.text.trimmingCharacters(in: .whitespaces)
    }

    /// A command that failed, exited with a code other than 0 or reported failing tests.
    public static func commandFailed(_ call: Activity) -> Bool {
        let summary = ActivityScenes.command(call)
        return ActivityScenes.failed(call) || (summary.exitCode ?? 0) != 0 || (summary.failed ?? 0) > 0
    }

    // MARK: The end-of-run card

    /// "2 files changed", "+18 −2", "1 command", "8 steps": what the session changed and what the latest request ran.
    public static func endFacts(_ session: Session) -> [String] {
        let changes = ActivityScenes.sessionChanges(session)
        let start = ActivityScenes.requestStart(session)
        let calls = session.activities[start...]
        let commands = calls.filter { ActivityScenes.kind(of: $0.tool) == .command }.count
        var facts = [changes.count == 1 ? "1 file changed" : "\(changes.count) files changed"]
        if !changes.isEmpty {
            let counts = changes.prefix(40).map(ActivityScenes.lineCounts)
            facts.append("+\(counts.reduce(0) { $0 + $1.added }) −\(counts.reduce(0) { $0 + $1.removed })")
        }
        if commands > 0 { facts.append(commands == 1 ? "1 command" : "\(commands) commands") }
        facts.append(calls.count == 1 ? "1 step" : "\(calls.count) steps")
        return facts
    }

    // MARK: Made so far

    /// How a checked page is named on its chip: "/login page", the host for a site's root, or the file's name.
    public static func pageLabel(_ subject: String) -> String {
        let trimmed = subject.trimmingCharacters(in: .whitespaces)
        if let url = URL(string: trimmed), url.scheme != nil, let host = url.host, !host.isEmpty {
            return url.path.count > 1 ? url.path + " page" : host
        }
        let name = (ActivityScenes.normalize(trimmed) as NSString).lastPathComponent
        return (name.isEmpty ? trimmed : name) + " page"
    }

    /// The files and pages the run made since `start`, newest first, each once: files that still have a change in the
    /// session, and pages it checked.
    public static func madeSoFar(_ activities: [Activity], from start: Int, changes: [FileChange], limit: Int = 12) -> [MadeThing] {
        var seen: Set<String> = [], made: [MadeThing] = []
        var index = activities.count - 1
        while index >= max(0, start), made.count < limit {
            let call = activities[index]
            index -= 1
            switch ActivityScenes.kind(of: call.tool) {
            case .edit:
                guard let path = ActivityScenes.path(of: call), let change = ActivityScenes.change(for: path, in: changes), !change.deleted,
                      seen.insert("f:" + path.lowercased()).inserted else { continue }
                made.append(MadeThing(kind: .file, label: (path as NSString).lastPathComponent, target: change.path, created: change.created, call: call.id))
            case .page:
                let subject = call.subject.trimmingCharacters(in: .whitespaces)
                guard call.tool == "check_page" || call.tool == "open_preview", !subject.isEmpty, seen.insert("p:" + subject.lowercased()).inserted else { continue }
                made.append(MadeThing(kind: .page, label: pageLabel(subject), target: subject, created: false, call: call.id))
            default: continue
            }
        }
        return made
    }

    // MARK: The open moment

    /// What the side panel remembers for an opened moment: the call's id, and the one file of an edit after a "|".
    public static func openKey(_ call: UUID, path: String? = nil) -> String {
        call.uuidString + (path.map { "|" + $0 } ?? "")
    }

    /// The scene (and the one file of it) an opened moment shows, or nil when it no longer exists.
    public static func opened(_ key: String?, activities: [Activity], scenes: [ActivityScene]) -> (scene: ActivityScene, path: String?)? {
        guard let key, key != ActivityScenes.endKey else { return nil }
        let parts = key.split(separator: "|", maxSplits: 1).map(String.init)
        guard let first = parts.first, let id = UUID(uuidString: first), let index = activities.firstIndex(where: { $0.id == id }),
              let scene = scenes.first(where: { $0.contains(index) }) else { return nil }
        return (scene, parts.count > 1 ? parts[1] : nil)
    }

    /// How many scenes have happened since an opened one.
    public static func newer(than scene: ActivityScene, in scenes: [ActivityScene]) -> Int {
        scenes.filter { $0.first > scene.last }.count
    }
}

/// Facts the session's header chips show: an open pull request and the project's running background tasks.
public enum SessionChips {
    /// The pull request's address when the project's delivery draft has been published, otherwise nil.
    public static func pullRequest(_ draft: DeliveryDraft?) -> String? {
        guard let url = draft?.url?.trimmingCharacters(in: .whitespacesAndNewlines), !url.isEmpty else { return nil }
        return url
    }

    /// How many background tasks are starting, running or waiting for an approval.
    public static func activeTasks(_ statuses: [TaskStatus]) -> Int { statuses.filter(\.active).count }

    /// "2 background", or nil when none is running.
    public static func backgroundLabel(_ active: Int) -> String? { active > 0 ? "\(active) background" : nil }
}
