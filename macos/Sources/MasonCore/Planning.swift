import Foundation

// Two ways the user directs the work beyond typing a request: comments pinned to lines of the project, which travel
// with the next request, and milestones, a saved plan whose steps are checked against the files they cover.

func sha256Hex(_ data: Data) -> String {
    var hash = Sha256()
    hash.update(data)
    return hash.hex()
}

/// A note on one line of a file. `revision` is the file's SHA-256 when the note was made; a note on a line the
/// request removed (`original`) quotes the old text and is located by its old line number.
public struct ReviewComment: Codable, Equatable, Identifiable {
    public var id = UUID()
    public var path: String
    public var revision: String
    public var original: Bool
    public var line: Int
    public var quote: String
    public var note: String
    public var location: String { "\(path) · \(original ? "original" : "updated") line \(line)" }
}

public enum Review {
    public static let limit = 40

    /// A comment on a line of the file as it is now, or (with `removed`) on a line a change took out.
    public static func comment(_ workspace: Workspace, path: String, line: Int, note: String, removed: String? = nil) throws -> ReviewComment {
        let trimmed = note.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 2000 else { throw MasonError("Write feedback of 1–2,000 characters.") }
        guard let data = try workspace.data(path) else { throw MasonError("\(path) does not exist.") }
        if let removed { return ReviewComment(path: path, revision: sha256Hex(data), original: true, line: line, quote: removed, note: trimmed) }
        var lines = String(decoding: data, as: UTF8.self).replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        if lines.last == "" { lines.removeLast() }
        guard line >= 1, line <= lines.count else { throw MasonError("Choose a line between 1 and \(lines.count) of \(path).") }
        return ReviewComment(path: path, revision: sha256Hex(data), original: false, line: line, quote: lines[line - 1], note: trimmed)
    }

    /// The request as the model reads it: what the user typed, then each comment with the line it is about.
    public static func request(_ draft: String, comments: [ReviewComment]) -> String {
        let typed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !comments.isEmpty else { return typed }
        let rows = comments.map { "\($0.location) (\($0.quote.trimmingCharacters(in: .whitespaces).prefix(160))) — \($0.note)" }
        return ((typed.isEmpty ? ["Address these review comments."] : [typed]) + rows).joined(separator: "\n")
    }

    /// What a reviewing model found in a change, kept only when it points at a real line.
    public struct Findings: Equatable {
        public var comments: [ReviewComment] = []
        /// Findings whose quoted evidence was not on the line they named; they are not shown as fact.
        public var dropped = 0
        public var files: [String] = []
    }

    /// Asks the model to read the files a session changed and report defects. Each finding must quote text from the
    /// line it names; one that does not is dropped rather than shown. The findings come back as comments on those lines.
    public static func findings(_ session: Session, workspace: Workspace, model: ChatModel) async throws -> Findings {
        var paths: [String] = []
        for turn in session.turns where !turn.undone { for change in turn.changes where !paths.contains(change.path) && !change.isOpaque && !change.deleted { paths.append(change.path) } }
        var sources: [(path: String, lines: [String], data: Data)] = [], size = 0
        for path in paths.prefix(12) {
            guard let data = try? workspace.data(path), let text = String(data: data, encoding: .utf8), size + text.count <= 120_000 else { continue }
            size += text.count
            var lines = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
            if lines.last == "" { lines.removeLast() }
            sources.append((path, lines, data))
        }
        guard !sources.isEmpty else { throw MasonError("This session has not changed any text files to review.") }
        let request = session.turns.last { !$0.undone && !$0.changes.isEmpty }?.request ?? ""
        let listing = sources.map { source in "File: \(source.path)\n" + source.lines.enumerated().map { "\($0.offset + 1)\t\($0.element)" }.joined(separator: "\n") }.joined(separator: "\n\n")
        let system = """
        You review a change another engineer just made. Report only real defects: wrong results, crashes, broken edge cases, security problems. Not style.
        Reply with only a JSON array. Each finding is {"path": file, "line": line number, "evidence": text copied exactly from that line, "problem": one or two sentences}.
        Reply [] when you find nothing.
        """
        let reply = try await model.complete(messages: [ChatMessage(role: "system", content: system), ChatMessage(role: "user", content: "The request was: \(request.prefix(2000))\n\nThe changed files as they are now, with line numbers:\n\n\(listing)")], tools: [], onText: { _ in })
        guard let start = reply.text.firstIndex(of: "["), let end = reply.text.lastIndex(of: "]"), start < end,
              let items = try? JSONSerialization.jsonObject(with: Data(reply.text[start...end].utf8)) as? [[String: Any]] else { throw MasonError("The model did not answer with a list of findings.") }
        var found = Findings(files: sources.map(\.path))
        for item in items.prefix(limit) {
            let evidence = (item["evidence"] as? String ?? "").trimmingCharacters(in: .whitespaces), problem = (item["problem"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            let line = (item["line"] as? Int) ?? (item["line"] as? String).flatMap(Int.init) ?? 0
            guard let source = sources.first(where: { $0.path == item["path"] as? String }), !evidence.isEmpty, !problem.isEmpty, line >= 1, line <= source.lines.count, source.lines[line - 1].contains(evidence) else { found.dropped += 1; continue }
            found.comments.append(ReviewComment(path: source.path, revision: sha256Hex(source.data), original: false, line: line, quote: source.lines[line - 1], note: String(problem.prefix(2000))))
        }
        return found
    }

    /// What to tell the user about a review.
    public static func summary(_ found: Findings) -> String {
        var lines = found.comments.map { "\($0.location) — \($0.note)" }
        lines.insert(found.comments.isEmpty ? "The review of \(found.files.joined(separator: ", ")) found nothing to report." : "The review found \(found.comments.count) thing\(found.comments.count == 1 ? "" : "s") in \(found.files.joined(separator: ", ")):", at: 0)
        if found.dropped > 0 { lines.append("\(found.dropped) finding\(found.dropped == 1 ? " was" : "s were") left out because the text quoted was not on the line named.") }
        if !found.comments.isEmpty { lines.append("They are pinned as comments and go with your next request. A model wrote them: check each before acting on it.") }
        return lines.joined(separator: "\n")
    }

    /// A comment is only sent while the file is the one it was written on.
    public static func validate(_ workspace: Workspace, comments: [ReviewComment]) throws {
        for comment in comments {
            guard let data = try? workspace.data(comment.path) else { throw MasonError("\(comment.path) is missing. Remove its comment or restore the file.") }
            guard sha256Hex(data) == comment.revision else { throw MasonError("\(comment.location) changed since the comment was written. Remove the comment or write it again.") }
        }
    }
}

public struct MilestoneReceipt: Codable, Equatable {
    public var id = UUID()
    public var at = Date()
    public var revision: String
    public var detail: String
    /// True when a person recorded what they saw; false when a workflow passed.
    public var manual: Bool
}

/// One step of a saved plan: what it decides, the files it covers, what must come first, and how it is checked
/// (a workflow from mason.workflows.json, or by the user recording what they saw).
public struct Milestone: Codable, Equatable, Identifiable {
    public var id = UUID()
    public var title: String
    public var decision: String
    public var files: [String]
    public var dependsOn: [UUID]
    public var workflow: String
    public var receipt: MilestoneReceipt?
    public init(title: String, decision: String = "", files: [String], dependsOn: [UUID] = [], workflow: String = "") {
        self.title = title; self.decision = decision; self.files = files; self.dependsOn = dependsOn; self.workflow = workflow
    }
}

public struct MilestoneStatus: Equatable {
    public enum State: String { case blocked = "Blocked", ready = "Ready", verified = "Verified", reviewed = "Reviewed", recheck = "Needs recheck" }
    public let item: Milestone
    public let state: State
    public let reason: String
    public var done: Bool { state == .verified || state == .reviewed }
}

public enum Milestones {
    public static let limit = 64

    /// Adds or replaces a milestone. Changing one clears its receipt, since the evidence was for the old version.
    public static func put(_ workspace: Workspace, current: [Milestone], item: Milestone) throws -> [Milestone] {
        var updated = item
        updated.title = item.title.trimmingCharacters(in: .whitespacesAndNewlines)
        updated.decision = item.decision.trimmingCharacters(in: .whitespacesAndNewlines)
        updated.workflow = item.workflow.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !updated.title.isEmpty, updated.title.count <= 240, updated.decision.count <= 2000, (1...32).contains(item.files.count), item.dependsOn.count <= 32, updated.workflow.count <= 50 else {
            throw MasonError("Use a title, 1–32 project files and a decision under 2,000 characters.")
        }
        let prefix = workspace.root.path.hasSuffix("/") ? workspace.root.path : workspace.root.path + "/"
        updated.files = Array(Set(try item.files.map { path -> String in
            let resolved = try workspace.resolve(path).path
            guard resolved.hasPrefix(prefix) else { throw MasonError("\(path) is not a file in the project.") }
            return String(resolved.dropFirst(prefix.count))
        })).sorted()
        updated.dependsOn = Array(Set(item.dependsOn)).sorted { $0.uuidString < $1.uuidString }
        updated.receipt = nil
        var result = current
        if let index = current.firstIndex(where: { $0.id == item.id }) {
            let prior = current[index]
            if prior.title == updated.title, prior.decision == updated.decision, prior.workflow == updated.workflow, prior.files == updated.files, prior.dependsOn == updated.dependsOn { return current }
            result[index] = updated
        } else {
            result.append(updated)
        }
        guard result.count <= limit else { throw MasonError("A session holds up to \(limit) milestones.") }
        let items = Dictionary(uniqueKeysWithValues: result.map { ($0.id, $0) })
        var visited = Set<UUID>(), pending = Set<UUID>()
        func visit(_ id: UUID) throws {
            if visited.contains(id) { return }
            guard let node = items[id] else { throw MasonError("Choose a prerequisite that is in this plan.") }
            guard pending.insert(id).inserted else { throw MasonError("These milestones depend on each other in a circle.") }
            for dependency in node.dependsOn { try visit(dependency) }
            pending.remove(id); visited.insert(id)
        }
        for milestone in result { try visit(milestone.id) }
        return result
    }

    /// Removes a milestone, and it from the prerequisites of the others.
    public static func remove(_ id: UUID, from plan: [Milestone]) -> [Milestone] {
        plan.filter { $0.id != id }.map { item in
            guard item.dependsOn.contains(id) else { return item }
            var changed = item
            changed.dependsOn.removeAll { $0 == id }
            changed.receipt = nil
            return changed
        }
    }

    /// What a receipt vouches for: the milestone's wording, the content of its files, its prerequisites' receipts and
    /// its workflow. Any of them changing means the milestone needs checking again.
    static func revision(_ workspace: Workspace, _ item: Milestone, plan: [Milestone]) throws -> String {
        var parts = [item.title, item.decision]
        for path in item.files {
            guard let data = try workspace.data(path) else { throw MasonError("\(path) is missing.") }
            parts.append(path + "=" + sha256Hex(data))
        }
        for id in item.dependsOn { parts.append("after " + (plan.first { $0.id == id }?.receipt?.id.uuidString ?? "-")) }
        if !item.workflow.isEmpty { parts.append("workflow " + ProjectWorkflows.commandLine(try workflow(workspace, item.workflow))) }
        return sha256Hex(Data(parts.joined(separator: "\u{1}").utf8))
    }

    static func workflow(_ workspace: Workspace, _ name: String) throws -> ProjectWorkflows.Workflow {
        guard let found = try ProjectWorkflows.load(workspace).workflows[name] else { throw MasonError("mason.workflows.json has no workflow named \(name).") }
        return found
    }

    public static func inspect(_ workspace: Workspace, plan: [Milestone]) -> [MilestoneStatus] {
        let items = Dictionary(plan.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        var states: [UUID: MilestoneStatus] = [:], visiting = Set<UUID>()
        func one(_ item: Milestone) -> MilestoneStatus {
            if let saved = states[item.id] { return saved }
            guard visiting.insert(item.id).inserted else { return MilestoneStatus(item: item, state: .blocked, reason: "Its prerequisites form a circle.") }
            let status: MilestoneStatus
            if item.dependsOn.contains(where: { id in items[id].map { !one($0).done } ?? true }) {
                status = MilestoneStatus(item: item, state: .blocked, reason: "A prerequisite still needs attention.")
            } else if let receipt = item.receipt {
                do {
                    status = try revision(workspace, item, plan: plan) == receipt.revision
                        ? MilestoneStatus(item: item, state: receipt.manual ? .reviewed : .verified, reason: receipt.detail)
                        : MilestoneStatus(item: item, state: .recheck, reason: "A file, the workflow or a prerequisite changed.")
                } catch {
                    status = MilestoneStatus(item: item, state: .recheck, reason: "\(error)")
                }
            } else {
                status = MilestoneStatus(item: item, state: .ready, reason: "Awaiting evidence.")
            }
            visiting.remove(item.id)
            states[item.id] = status
            return status
        }
        return plan.map(one)
    }

    /// Checks one milestone and returns its receipt: the workflow must pass, or (with no workflow) the user records
    /// what they saw. `runWorkflow` says whether Mason may run the workflow, and whether sandboxed.
    public static func check(_ workspace: Workspace, item: Milestone, plan: [Milestone], runWorkflow: (allowed: Bool, sandboxed: Bool), observation: String) async throws -> MilestoneReceipt {
        func blocked() -> Bool { inspect(workspace, plan: plan).first { $0.item.id == item.id }?.state == .blocked }
        guard !blocked() else { throw MasonError("Complete the prerequisites first.") }
        let before = try revision(workspace, item, plan: plan)
        var detail = observation.trimmingCharacters(in: .whitespacesAndNewlines)
        if !item.workflow.isEmpty {
            guard runWorkflow.allowed else { throw MasonError("Mason runs a milestone's workflow in Auto, or when you have chosen Always Allow for the workflow \(item.workflow).") }
            let result = try await Shell.run(ProjectWorkflows.commandLine(try workflow(workspace, item.workflow)), in: workspace, sandboxed: runWorkflow.sandboxed)
            guard result.exitCode == 0, !result.timedOut else { throw MasonError("The workflow \(item.workflow) exited \(result.exitCode). \(result.output.suffix(1800))") }
            detail = "\(item.workflow) · exit 0\n" + result.output.suffix(2000)
        } else if detail.isEmpty || detail.count > 2000 {
            throw MasonError("Record what you observed in 1–2,000 characters.")
        }
        try Task.checkCancellation()
        guard !blocked(), try revision(workspace, item, plan: plan) == before else { throw MasonError("The milestone's files changed during the check. Check it again.") }
        return MilestoneReceipt(revision: before, detail: detail, manual: item.workflow.isEmpty)
    }

    /// The plan as the model is told it, with the milestone to work on next first.
    public static func context(_ workspace: Workspace, plan: [Milestone]) -> String {
        let status = inspect(workspace, plan: plan)
        let next = status.first { $0.state == .ready || $0.state == .recheck }
        var summary = ""
        for row in (next.map { [$0] } ?? []) + status.filter({ $0.item.id != next?.item.id }) {
            let line = "- \(row.item.title) [\(row.state.rawValue)]" + (row.item.decision.isEmpty ? "" : " Decision: \(row.item.decision)") + " Files: " + row.item.files.joined(separator: ", ") + "\n"
            if summary.count + line.count > 5000 { break }
            summary += line
        }
        return "The user's saved milestones. Verified means a workflow passed; Reviewed means the user recorded what they saw. Next: \(next?.item.title ?? "none ready")\n" + summary
    }
}
