import Foundation

/// What kind of work a run of tool calls was, which picks how the side panel's Moments feed shows it.
public enum ActivitySceneKind: String, Equatable, Sendable { case read, edit, command, page, other }

/// Consecutive tool calls of one kind, shown together as one step of a run. `first` indexes the session's activities.
public struct ActivityScene: Equatable, Sendable {
    public let kind: ActivitySceneKind
    public let first: Int
    public let count: Int
    public init(kind: ActivitySceneKind, first: Int, count: Int) { self.kind = kind; self.first = first; self.count = count }
    public var last: Int { first + count - 1 }
    public var indices: Range<Int> { first..<(first + count) }
    public func contains(_ index: Int) -> Bool { index >= first && index <= last }
    /// The key the viewer and its step strip know this scene by.
    public var key: String { "s\(first)" }
}

/// A command's result, read for the summary first: exit code, test counts and the lines that name failures.
public struct CommandSummary: Equatable, Sendable {
    public var exitCode: Int?
    public var passed: Int?
    public var failed: Int?
    public var failures: [String]
    /// The output without Mason's own "exit N" line.
    public var output: String
    /// True when Mason kept only part of the output.
    public var truncated: Bool
}

/// How many calls of a scene touched one top folder.
public struct FolderCount: Equatable, Sendable {
    public let folder: String
    public let count: Int
    public init(_ folder: String, _ count: Int) { self.folder = folder; self.count = count }
}

/// The colour a step of the strip takes, by kind and outcome.
public enum SceneTone: String, Equatable, Sendable { case neutral, accent, good, search, bad, warn }

/// What the latest request amounted to, for the receipt shown when a run ends.
public struct RunReceipt: Equatable {
    public struct File: Equatable {
        public let path: String
        public let added: Int
        public let removed: Int
        public let created: Bool
        public let deleted: Bool
    }
    public var request: String
    public var steps: Int
    /// Seconds from the request's first step to its last.
    public var seconds: Int
    public var files: [File]
    public var added: Int
    public var removed: Int
    public var commands: Int
    public var commandsFailed: Int
    public var notes: [String]

    /// "45 s", "2 min 5 s".
    public var duration: String { seconds >= 60 ? "\(seconds / 60) min \(seconds % 60) s" : "\(max(1, seconds)) s" }
}

public enum ActivityScenes {
    /// The longest run of calls kept in one scene; longer runs start a new scene so none grows without bound.
    public static let maxSceneLength = 40
    /// Keys of the two scenes that are not tool calls: the saved plan and the run's receipt.
    public static let planKey = "plan", endKey = "end"

    static let reads: Set<String> = ["read_file", "read_lines", "list_files", "search_files", "search_next", "semantic_search", "repository_map", "inspect_symbol",
                                     "navigate_code", "read_documentation", "read_skill", "git_status", "delegate_readonly", "open_file", "mcp_read", "mcp_resources", "mcp_tools", "web_fetch", "web_search"]
    static let edits: Set<String> = ["edit_file", "write_file", "apply_edits"]
    static let commands: Set<String> = ["run_shell", "run_command", "run_workflow", "start_job", "poll_job", "stop_job", "start_terminal", "terminal_input",
                                        "terminal_status", "terminal_stop", "verify_acceptance"]
    static let pages: Set<String> = ["check_page", "open_preview", "desktop_observe", "desktop_visual", "desktop_act"]
    static let searches: Set<String> = ["search_files", "search_next", "semantic_search", "list_files"]
    /// The tools whose activity subject is a path in the project.
    static let pathTools: Set<String> = ["read_file", "read_lines", "list_files", "repository_map", "inspect_symbol", "navigate_code", "open_file", "semantic_search",
                                         "edit_file", "write_file", "apply_edits", "check_page", "open_preview"]

    public static func kind(of tool: String) -> ActivitySceneKind {
        if reads.contains(tool) { return .read }
        if edits.contains(tool) { return .edit }
        if commands.contains(tool) { return .command }
        if pages.contains(tool) { return .page }
        return .other
    }

    /// A search or listing, as opposed to reading one file.
    public static func isSearch(_ tool: String) -> Bool { searches.contains(tool) }

    /// Groups consecutive calls of the same kind. Every call belongs to exactly one scene, in order.
    public static func group(_ activities: [Activity]) -> [ActivityScene] {
        var scenes: [ActivityScene] = []
        for (index, activity) in activities.enumerated() {
            let sort = kind(of: activity.tool)
            if let last = scenes.last, last.kind == sort, last.count < maxSceneLength {
                scenes[scenes.count - 1] = ActivityScene(kind: sort, first: last.first, count: last.count + 1)
            } else {
                scenes.append(ActivityScene(kind: sort, first: index, count: 1))
            }
        }
        return scenes
    }

    /// The project path a call worked on, normalised to forward slashes without a leading "./", or nil.
    public static func path(of activity: Activity) -> String? {
        guard pathTools.contains(activity.tool), !activity.subject.isEmpty else { return nil }
        return normalize(activity.subject)
    }

    public static func normalize(_ path: String) -> String {
        let slashed = path.replacingOccurrences(of: "\\", with: "/")
        let trimmed = String(slashed.drop { $0 == "." || $0 == "/" })
        return trimmed.isEmpty ? slashed : trimmed
    }

    /// A call that did not do what it was asked: it failed or was declined.
    public static func failed(_ activity: Activity) -> Bool { activity.state == .failed || activity.state == .denied }

    /// A scene with a failed call, or a command that exited with a code other than 0.
    public static func failed(_ scene: ActivityScene, _ activities: [Activity]) -> Bool {
        scene.indices.contains { index in
            let call = activities[index]
            return failed(call) || (scene.kind == .command && (command(call).exitCode ?? 0) != 0)
        }
    }

    /// "1 file", "3 files".
    static func plural(_ count: Int, _ one: String, _ many: String) -> String { "\(count) \(count == 1 ? one : many)" }

    /// A short label for a scene, written the way a person would say it.
    public static func label(_ scene: ActivityScene, _ activities: [Activity]) -> String {
        let items = scene.indices.map { activities[$0] }
        func distinct(_ calls: [Activity]) -> Int {
            Set(calls.enumerated().map { offset, call in path(of: call)?.lowercased() ?? "\u{0}\(offset)" }).count
        }
        switch scene.kind {
        case .read:
            let searchCount = items.filter { isSearch($0.tool) }.count
            let readCount = distinct(items.filter { !isSearch($0.tool) })
            var parts: [String] = []
            if readCount > 0 { parts.append("Read " + plural(readCount, "file", "files")) }
            if searchCount > 0 { parts.append((parts.isEmpty ? "Searched " : "searched ") + plural(searchCount, "time", "times")) }
            return parts.joined(separator: ", ")
        case .edit:
            if items.count == 1, let one = path(of: items[0]) { return "Edited " + (one as NSString).lastPathComponent }
            return "Edited " + plural(distinct(items), "file", "files")
        case .command:
            if items.count > 1 { return "Ran \(items.count) commands" }
            let call = items[0]
            switch call.tool {
            case "run_workflow": return "Ran the \(call.subject) workflow"
            case "verify_acceptance": return "Ran the acceptance checks"
            case "poll_job", "terminal_status": return "Checked \(call.tool == "poll_job" ? "job" : "terminal") \(call.subject)"
            case "stop_job", "terminal_stop": return "Stopped \(call.tool == "stop_job" ? "job" : "terminal") \(call.subject)"
            case "terminal_input": return "Typed into terminal \(call.subject)"
            default:
                var line = commandLine(call.subject)
                if line.count > 48 { line = String(line.prefix(47)) + "…" }
                return (call.tool == "start_job" || call.tool == "start_terminal" ? "Started " : "Ran ") + line
            }
        case .page:
            return items.count == 1 ? "Checked the page" : "Checked the page \(items.count) times"
        case .other:
            return items.count == 1 ? Activity.label(items[0].tool) : "\(items.count) other steps"
        }
    }

    /// A command line with its program named, not the folder it was found in: /usr/bin/python3 -m unittest → python3 -m unittest.
    public static func commandLine(_ command: String) -> String {
        let trimmed = command.trimmingCharacters(in: .whitespaces)
        var program: Substring, rest: Substring
        if let quote = trimmed.first, quote == "'" || quote == "\"", let close = trimmed.dropFirst().firstIndex(of: quote) {
            program = trimmed[trimmed.index(after: trimmed.startIndex)..<close]
            rest = trimmed[trimmed.index(after: close)...]
        } else if let space = trimmed.firstIndex(of: " ") {
            program = trimmed[..<space]; rest = trimmed[space...]
        } else {
            program = Substring(trimmed); rest = ""
        }
        guard program.contains("/") else { return trimmed }
        let name = (String(program) as NSString).lastPathComponent
        return (name.isEmpty ? String(program) : name) + rest
    }

    // MARK: Command results

    static func regex(_ pattern: String, _ options: NSRegularExpression.Options = []) -> NSRegularExpression {
        // The patterns are fixed, so a bad one is a programming error caught by the tests.
        try! NSRegularExpression(pattern: pattern, options: options)
    }
    static let passedCount = regex(#"(\d+)\s+(?:tests?\s+)?passed|passed:?\s+(\d+)"#, [.caseInsensitive])
    static let failedCount = regex(#"(\d+)\s+(?:tests?\s+)?failed|failed:?\s+(\d+)"#, [.caseInsensitive])
    static let unittestRan = regex(#"^Ran (\d+) tests? in "#, [.anchorsMatchLines])
    static let unittestFailed = regex(#"^FAILED \(([^)]*)\)"#, [.anchorsMatchLines])
    static let failureLine = regex(#"^(?:[✗✕×✘]|●\s|FAIL\b|FAILED\b|ERROR\b|error(?:\s+[A-Z]+\d+)?:|[\w./\\-]+\(\d+(?:,\d+)?\):\s*error|Error:|AssertionError|Traceback|npm ERR!)"#, [.caseInsensitive])
    static let summaryLine = regex(#"^(?:Tests?:|Test Suites:|Failed!\s+-|FAILED \(|=+.*(?:passed|failed))"#, [.caseInsensitive])
    static let exitLine = regex(#"^(?:exit|job \S+: exited) (-?\d+)"#)

    /// The number in the last match of a pattern with one or two alternative number groups.
    static func lastCount(_ text: String, _ pattern: NSRegularExpression) -> Int? {
        let range = NSRange(text.startIndex..., in: text)
        var best: Int?
        for match in pattern.matches(in: text, range: range) {
            for group in 1..<match.numberOfRanges {
                if let found = Range(match.range(at: group), in: text), let value = Int(text[found]) { best = value; break }
            }
        }
        return best
    }

    static func matches(_ text: String, _ pattern: NSRegularExpression) -> Bool {
        pattern.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) != nil
    }

    /// Reads a command call's result for its exit code, test counts and the lines that name failures. Mason's command
    /// results start with "exit N" (a job's with "job 1: exited N").
    public static func command(_ activity: Activity) -> CommandSummary {
        var output = activity.result.replacingOccurrences(of: "\r\n", with: "\n")
        var exit: Int?
        let firstLine = String(output.prefix { $0 != "\n" })
        if let match = exitLine.firstMatch(in: firstLine, range: NSRange(firstLine.startIndex..., in: firstLine)),
           let range = Range(match.range(at: 1), in: firstLine) {
            exit = Int(firstLine[range])
            output = String(output.dropFirst(firstLine.count).drop { $0 == "\n" })
        }
        let truncated = activity.result.count >= 8000 || output.contains("[… earlier output omitted]")
        var passed = lastCount(output, passedCount), failed = lastCount(output, failedCount)
        // Python's unittest prints "Ran 6 tests" then "OK" or "FAILED (failures=1, errors=1)".
        if passed == nil, let ran = lastCount(output, unittestRan) {
            var bad = 0
            let range = NSRange(output.startIndex..., in: output)
            if let match = unittestFailed.firstMatch(in: output, range: range), let inner = Range(match.range(at: 1), in: output) {
                for part in output[inner].split(separator: ",") {
                    let pair = part.trimmingCharacters(in: .whitespaces).split(separator: "=")
                    if pair.count == 2, pair[0] == "failures" || pair[0] == "errors", let value = Int(pair[1]) { bad += value }
                }
            }
            passed = ran - bad
            if failed == nil, bad > 0 { failed = bad }
        }
        var failures: [String] = []
        for raw in output.split(separator: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty, matches(line, failureLine), !matches(line, summaryLine) else { continue }
            let clipped = line.count > 220 ? String(line.prefix(219)) + "…" : line
            if !failures.contains(clipped) { failures.append(clipped) }
            if failures.count == 8 { break }
        }
        return CommandSummary(exitCode: exit, passed: passed, failed: failed, failures: failures, output: output, truncated: truncated)
    }

    /// How many calls touched each top folder, most first.
    public static func folders(_ activities: [Activity]) -> [FolderCount] {
        var counts: [String: (name: String, count: Int)] = [:]
        for case let found? in activities.map({ path(of: $0) }) {
            let folder = found.firstIndex(of: "/").map { String(found[..<$0]) + "/" } ?? found
            counts[folder.lowercased(), default: (folder, 0)].count += 1
        }
        return counts.values
            .sorted { $0.count != $1.count ? $0.count > $1.count : $0.name.lowercased() < $1.name.lowercased() }
            .map { FolderCount($0.name, $0.count) }
    }

    /// The tick a scene shows in the step strip: its short word and its tone.
    public static func tick(_ scene: ActivityScene, _ activities: [Activity]) -> (text: String, tone: SceneTone) {
        let text: String
        var shade: SceneTone
        switch scene.kind {
        case .read: text = scene.count > 1 ? "read×\(scene.count)" : "read"; shade = .neutral
        case .edit: text = "edit"; shade = .accent
        case .command: text = "run"; shade = .good
        case .page: text = "page"; shade = .search
        case .other: text = "step"; shade = .neutral
        }
        if failed(scene, activities) { shade = .bad }
        else if scene.indices.contains(where: { activities[$0].state == .skipped }) { shade = .warn }
        return (text, shade)
    }

    /// The tone of a run ending's tick.
    public static func tone(_ ending: RunEnding) -> SceneTone {
        switch ending.style?.tone ?? .neutral {
        case .success: return .good
        case .warning: return .warn
        case .accent: return .accent
        case .danger: return .bad
        case .neutral: return .neutral
        }
    }

    // MARK: Following the run

    /// The scene the viewer follows: the newest one while running, the receipt once the run ended.
    public static func live(_ scenes: [ActivityScene], running: Bool, hasPlan: Bool, hasEnd: Bool) -> String {
        if !running, hasEnd { return endKey }
        if let last = scenes.last { return last.key }
        return hasPlan ? planKey : ""
    }

    /// Which scene to show for what the user picked (nil follows the run): the plan, the receipt, or the scene holding
    /// the picked call's id. A pick that no longer exists follows the run again.
    public static func selection(pick: String?, activities: [Activity], scenes: [ActivityScene], running: Bool, hasPlan: Bool, hasEnd: Bool) -> (key: String, following: Bool) {
        let latest = live(scenes, running: running, hasPlan: hasPlan, hasEnd: hasEnd)
        guard let pick else { return (latest, true) }
        var key: String?
        if pick == endKey, hasEnd { key = endKey }
        else if pick == planKey, hasPlan { key = planKey }
        else if let id = UUID(uuidString: pick), let index = activities.firstIndex(where: { $0.id == id }), let scene = scenes.first(where: { $0.contains(index) }) { key = scene.key }
        guard let key else { return (latest, true) }
        return (key, key == latest)
    }

    /// What to remember when the user picks a scene: nil when it is the live one (follow again), otherwise the plan,
    /// the receipt, or the id of the scene's first call, which stays put as the run grows.
    public static func pick(_ key: String, activities: [Activity], scenes: [ActivityScene], running: Bool, hasPlan: Bool, hasEnd: Bool) -> String? {
        if key == live(scenes, running: running, hasPlan: hasPlan, hasEnd: hasEnd) { return nil }
        if key == planKey || key == endKey { return key }
        guard let scene = scenes.first(where: { $0.key == key }), scene.first < activities.count else { return nil }
        return activities[scene.first].id.uuidString
    }

    // MARK: Changes and the receipt

    /// Every file the session changed and has not undone, merged across requests: the original before, the latest after.
    public static func sessionChanges(_ session: Session) -> [FileChange] {
        var merged: [String: FileChange] = [:], order: [String] = []
        for turn in session.turns where !turn.undone {
            for change in turn.changes {
                if var existing = merged[change.path] {
                    existing.after = change.after; existing.afterBlob = change.afterBlob; existing.afterMissing = change.afterMissing
                    merged[change.path] = existing
                } else { merged[change.path] = change; order.append(change.path) }
            }
        }
        return order.compactMap { merged[$0] }
    }

    /// The session's change to a path, matched the way the activity named it.
    public static func change(for path: String, in changes: [FileChange]) -> FileChange? {
        changes.last { normalize($0.path).lowercased() == path.lowercased() }
    }

    /// Lines added and removed by a change; none for a binary or very large file.
    public static func lineCounts(_ change: FileChange) -> (added: Int, removed: Int) {
        if change.isOpaque { return (0, 0) }
        return Diff.counts(before: change.before, after: change.deleted ? "" : change.after)
    }

    /// Index of the first call of the latest request, so a run's receipt covers only what that request did.
    public static func requestStart(_ session: Session) -> Int {
        guard let last = session.turns.last else { return 0 }
        return min(max(0, last.firstActivity), session.activities.count)
    }

    /// What the latest request did, for the receipt shown when it ended.
    public static func receipt(_ session: Session) -> RunReceipt {
        let calls = Array(session.activities[requestStart(session)...])
        let turn = session.turns.last
        var request = (turn?.request ?? session.title).trimmingCharacters(in: .whitespacesAndNewlines)
        if request.count > 120 { request = String(request.prefix(119)) + "…" }
        let seconds = calls.count > 1 ? Int(calls[calls.count - 1].at.timeIntervalSince(calls[0].at)) : 0
        let changed = turn.map { $0.undone ? [] : $0.changes } ?? []
        let files = changed.map { change -> RunReceipt.File in
            let counts = lineCounts(change)
            return RunReceipt.File(path: change.path, added: counts.added, removed: counts.removed, created: change.created, deleted: change.deleted)
        }
        let commandCalls = calls.filter { kind(of: $0.tool) == .command }
        let failedCommands = commandCalls.filter { failed($0) || (command($0).exitCode ?? 0) != 0 }.count
        var notes: [String] = []
        let ending = session.ending
        if ending?.code == "model_loop", let repeated = session.repeatStop { notes.append(repeated.explanation(kept: (ending?.kept ?? 0) > 0)) }
        else if let ending, ending.code != "done", !session.detail.isEmpty { notes.append(session.detail) }
        if let ending, let hint = RunEnding.hint(ending.code, terminal: false) { notes.append(hint) }
        let skipped = calls.filter { $0.state == .skipped }.count
        if skipped > 0, ending?.code != "model_loop" { notes.append(skipped == 1 ? "Mason skipped one repeated call." : "Mason skipped \(skipped) repeated calls.") }
        if session.state == "Interrupted" { notes.append("The run was interrupted and steps that were still running are marked failed. Check Step details before continuing.") }
        return RunReceipt(request: request, steps: calls.count, seconds: max(0, seconds), files: files,
                          added: files.reduce(0) { $0 + $1.added }, removed: files.reduce(0) { $0 + $1.removed },
                          commands: commandCalls.count, commandsFailed: failedCommands, notes: notes)
    }
}
