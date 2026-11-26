import Foundation

/// A pull request made from hunks the user chose. The draft is one commit on top of the target branch, built without
/// touching the checkout, its index or its branches. Publishing is a separate, explicit step that pushes that commit
/// to a new branch (never with force) and opens a draft pull request through `gh`. Feedback brings failed checks,
/// requested changes and review comments back.
public struct DeliveryDraft: Codable, Equatable {
    public var id: String
    public var workspace: String
    public var base: String
    public var baseCommit: String
    public var commit: String
    public var title: String
    public var body: String
    /// owner/name on GitHub, or empty when the checkout has no GitHub origin.
    public var repository: String
    public var branch: String
    public var paths: [String]
    public var url: String?
}

public struct DeliveryFeedback: Equatable {
    public let head: String
    public let url: String
    public let observations: [String]
}

public enum Delivery {
    static func checkBranch(_ name: String) throws {
        guard name.range(of: #"^[A-Za-z0-9][A-Za-z0-9_./-]{0,99}$"#, options: .regularExpression) != nil, !name.contains(".."), !name.contains("//"), !name.hasSuffix("/"), !name.hasSuffix(".lock") else {
            throw MasonError("Use the target branch name, such as main.")
        }
    }

    static func isCommit(_ value: String) -> Bool { value.range(of: "^(?:[0-9a-f]{40}|[0-9a-f]{64})$", options: .regularExpression) != nil }

    /// owner/name from a GitHub remote address.
    public static func repository(_ remote: String) throws -> String {
        let text = remote.trimmingCharacters(in: .whitespacesAndNewlines)
        let pattern = try NSRegularExpression(pattern: #"^(?:https://github\.com/|git@github\.com:)([\w.-]+/[\w.-]+?)(?:\.git)?/?$"#, options: .caseInsensitive)
        guard let match = pattern.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)), let range = Range(match.range(at: 1), in: text),
              !text[range].split(separator: "/").contains(where: { $0 == "." || $0 == ".." }) else { throw MasonError("Choose a checkout with a GitHub origin.") }
        return String(text[range])
    }

    /// Everything the checkout has that the target branch does not, a hunk at a time, and the commit they were read against.
    public static func review(_ workspace: Workspace, base: String) async throws -> (revision: String, patches: [(path: String, hunk: ExperimentHunk)]) {
        try checkBranch(base)
        var revision = ""
        for ref in ["refs/remotes/origin/" + base, "refs/heads/" + base] where revision.isEmpty {
            revision = ((try? await Experiments.git(workspace, ["rev-parse", "--verify", ref + "^{commit}"])) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        guard isCommit(revision) else { throw MasonError("There is no branch \(base) here, as origin/\(base) or locally.") }
        let root = realPath(workspace.root.path)
        let against = Experiment(name: "", parent: root, path: root, branch: "", revision: revision, created: Date())
        var patches: [(path: String, hunk: ExperimentHunk)] = []
        for path in try await Experiments.changed(against) {
            for hunk in try await Experiments.hunks(against, path: path) { patches.append((path, hunk)) }
            guard patches.count <= 128, patches.reduce(0, { $0 + $1.hunk.patch.utf8.count }) <= 1_500_000 else { throw MasonError("That is more than 128 hunks or 1.5 MB. Narrow the delivery to a smaller change.") }
        }
        return (revision, patches)
    }

    /// Builds the commit for the chosen hunks on top of `pinned`, the revision the user reviewed against.
    /// `receipt` is what Mason's own checks found on the latest request; it goes into the description when the draft is the whole change.
    public static func draft(_ workspace: Workspace, base: String, pinned: String, title: String, selected: Set<String>, receipt: String? = nil, home: URL = DataHome.url) async throws -> DeliveryDraft {
        let title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (1...120).contains(title.count), !title.contains("\n"), (1...128).contains(selected.count) else { throw MasonError("Choose reviewed hunks and a pull request title of up to 120 characters.") }
        let reviewed = try await review(workspace, base: base)
        guard reviewed.revision == pinned else { throw MasonError("\(base) moved since you reviewed. Review again.") }
        let patches = reviewed.patches.filter { selected.contains($0.hunk.id) }
        guard patches.count == selected.count else { throw MasonError("A chosen hunk changed. Review it again.") }
        let index = FileManager.default.temporaryDirectory.appendingPathComponent("mason-delivery-" + UUID().uuidString).path
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("mason-patch-" + UUID().uuidString)
        defer { for path in [index, index + ".lock", file.path] { try? FileManager.default.removeItem(atPath: path) } }
        _ = try await Experiments.git(workspace, ["read-tree", pinned], index: index)
        // Hunks of one file share its header, so the file's later hunks are added without theirs.
        var combined = "", paths: [String] = []
        for patch in patches {
            if paths.last == patch.path {
                guard let at = patch.hunk.patch.range(of: "\n@@ ") else { throw MasonError("A hunk of \(patch.path) could not be combined with the others.") }
                combined += patch.hunk.patch[patch.hunk.patch.index(after: at.lowerBound)...]
            } else {
                combined += patch.hunk.patch
                paths.append(patch.path)
            }
        }
        try Data(combined.utf8).write(to: file)
        _ = try await Experiments.git(workspace, ["apply", "--cached", "--whitespace=nowarn", file.path], index: index)
        let tree = try await Experiments.git(workspace, ["write-tree"], index: index).trimmingCharacters(in: .whitespacesAndNewlines)
        // The commit carries the user's own name, as any commit of theirs would.
        let commit = try await Experiments.git(workspace, ["commit-tree", tree, "-p", pinned, "-m", title], identity: false).trimmingCharacters(in: .whitespacesAndNewlines)
        let remote = ((try? await Experiments.git(workspace, ["remote", "get-url", "origin"])) ?? "")
        let id = UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: "")
        let whole = patches.count == reviewed.patches.count
        let listed: String = paths.map { "- " + $0 }.joined(separator: "\n")
        var verification = "This is a selection of the checkout's changes, which was not verified on its own."
        if whole {
            verification = "This is every change in the checkout against \(base). No verification receipt is attached."
            if let receipt { verification = "This is every change in the checkout against \(base). Mason's own checks on the latest request found:\n\n" + String(receipt.prefix(12_000)) }
        }
        let body: String = "## Changes\n\n" + listed + "\n\n## Verification\n\n" + verification
        let made = DeliveryDraft(id: id, workspace: realPath(workspace.root.path), base: base, baseCommit: pinned, commit: commit, title: title, body: body,
                                 repository: (try? repository(remote)) ?? "", branch: "mason/review-" + id.prefix(12), paths: paths, url: nil)
        try save(load(home: home).filter { $0.workspace != made.workspace } + [made], home: home)
        return made
    }

    static func gh(_ workspace: Workspace, _ arguments: [String]) async throws -> String {
        let printed = FileManager.default.temporaryDirectory.appendingPathComponent("mason-gh-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: printed) }
        let result = try await Shell.run("gh " + arguments.map(Shell.quote).joined(separator: " ") + " > " + Shell.quote(printed.path), in: workspace, sandboxed: false, timeout: 60)
        guard result.exitCode == 0, !result.timedOut else {
            throw MasonError(result.exitCode == 127 ? "The GitHub command line (gh) is not installed. Install it and sign in with: gh auth login" : "gh \(arguments.prefix(2).joined(separator: " ")) failed: " + result.output.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return String(decoding: (try? Data(contentsOf: printed)) ?? Data(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Pushes the draft's commit to its own new branch and opens a draft pull request. Returns the pull request's address.
    public static func publish(_ workspace: Workspace, _ draft: DeliveryDraft, home: URL = DataHome.url) async throws -> String {
        try checkBranch(draft.base); try checkBranch(draft.branch)
        let origin = try repository(try await Experiments.git(workspace, ["remote", "get-url", "origin"]))
        guard isCommit(draft.commit), isCommit(draft.baseCommit), !draft.repository.isEmpty, draft.repository == origin else { throw MasonError("The checkout's GitHub repository is not the one this draft was made for.") }
        let tip = try await gh(workspace, ["api", "repos/" + draft.repository + "/commits/" + (draft.base.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? draft.base), "--jq", ".sha"])
        guard tip == draft.baseCommit else { throw MasonError("\(draft.base) on GitHub has moved. Fetch it and review again.") }
        // No force: a retry can only publish the same commit again.
        _ = try await Experiments.git(workspace, ["push", "https://github.com/" + draft.repository + ".git", draft.commit + ":refs/heads/" + draft.branch])
        let earlier = try await gh(workspace, ["pr", "list", "--repo", draft.repository, "--head", draft.branch, "--base", draft.base, "--state", "all", "--json", "url,headRefOid"])
        var url = ""
        if let list = try? JSONSerialization.jsonObject(with: Data(earlier.utf8)) as? [[String: Any]], let first = list.first {
            guard first["headRefOid"] as? String == draft.commit else { throw MasonError("A pull request already uses this branch with other content.") }
            url = first["url"] as? String ?? ""
        }
        if url.isEmpty { url = try await gh(workspace, ["pr", "create", "--draft", "--repo", draft.repository, "--base", draft.base, "--head", draft.branch, "--title", draft.title, "--body", draft.body]) }
        var all = load(home: home)
        if let index = all.firstIndex(where: { $0.id == draft.id }) { all[index].url = url; try save(all, home: home) }
        return url
    }

    /// Failed checks, requested changes and review comments on the draft's pull request.
    public static func feedback(_ workspace: Workspace, _ draft: DeliveryDraft) async throws -> DeliveryFeedback {
        guard let url = draft.url, url.range(of: "^https://github\\.com/" + NSRegularExpression.escapedPattern(for: draft.repository) + "/pull/\\d+$", options: [.regularExpression, .caseInsensitive]) != nil else {
            throw MasonError("Publish this draft first.")
        }
        let number = String(url[url.index(after: url.lastIndex(of: "/")!)...])
        let state = try await gh(workspace, ["pr", "view", number, "--repo", draft.repository, "--json", "url,headRefOid,statusCheckRollup,reviews"])
        let comments = try await gh(workspace, ["api", "repos/" + draft.repository + "/pulls/" + number + "/comments?per_page=100"])
        return try parseFeedback(state: state, comments: comments)
    }

    public static func parseFeedback(state: String, comments: String) throws -> DeliveryFeedback {
        guard let root = try? JSONSerialization.jsonObject(with: Data(state.utf8)) as? [String: Any] else { throw MasonError("GitHub's answer about the pull request could not be read.") }
        let notes = (try? JSONSerialization.jsonObject(with: Data(comments.utf8)) as? [[String: Any]]) ?? []
        func field(_ object: [String: Any], _ name: String) -> String {
            switch object[name] { case let text as String: return text; case let number as NSNumber: return number.stringValue; default: return "" }
        }
        var found: [String] = []
        func add(_ text: String) { if !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, found.count < 64 { found.append(String(text.prefix(1200))) } }
        for check in root["statusCheckRollup"] as? [[String: Any]] ?? [] {
            let outcome = field(check, "conclusion").isEmpty ? field(check, "state") : field(check, "conclusion")
            if ["FAILURE", "ERROR", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED"].contains(outcome) { add("CI · " + field(check, "name") + field(check, "context") + " · " + outcome + " · " + field(check, "detailsUrl")) }
        }
        for review in root["reviews"] as? [[String: Any]] ?? [] where field(review, "state") == "CHANGES_REQUESTED" { add("Review · " + field(review, "body")) }
        for note in notes { add(field(note, "path") + ":" + field(note, "line") + " · " + field(note, "body")) }
        return DeliveryFeedback(head: field(root, "headRefOid"), url: field(root, "url"), observations: found)
    }

    // MARK: the record of drafts

    public static func load(home: URL = DataHome.url) -> [DeliveryDraft] {
        guard let data = try? Data(contentsOf: home.appendingPathComponent("deliveries.json")) else { return [] }
        return (try? JSONDecoder().decode([DeliveryDraft].self, from: data)) ?? []
    }

    static func save(_ all: [DeliveryDraft], home: URL) throws {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try encoder.encode(all).write(to: home.appendingPathComponent("deliveries.json"), options: .atomic)
    }

    /// The project's current draft, if it has one.
    public static func current(_ workspace: Workspace, home: URL = DataHome.url) -> DeliveryDraft? {
        let root = realPath(workspace.root.path)
        return load(home: home).last { $0.workspace == root }
    }

    // MARK: the /deliver command, shared by the command line and the app

    public static let usage = """
        /deliver review BASE                 What this checkout has that BASE does not, a hunk at a time
        /deliver draft BASE HUNKS TITLE      Build one commit from hunks (ids separated by commas, or "all") on top of BASE
        /deliver                             The current draft
        /deliver publish                     Push the draft's commit to a new branch and open a draft pull request (needs gh)
        /deliver feedback                    Failed checks, requested changes and review comments on that pull request
        /deliver feedback send               The same, sent to the model as your next request
        """

    /// Returns what to tell the user. For "feedback send" with something to act on, `request` is what to send the model.
    public static func command(_ rest: String, workspace: Workspace, receipt: String? = nil, home: URL = DataHome.url) async throws -> (text: String, request: String?) {
        let words = rest.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
        func described(_ draft: DeliveryDraft) -> String {
            "\"\(draft.title)\" onto \(draft.base): commit \(draft.commit.prefix(12)), \(draft.paths.count) file\(draft.paths.count == 1 ? "" : "s") (\(draft.paths.prefix(6).joined(separator: ", "))\(draft.paths.count > 6 ? ", …" : ""))"
                + (draft.url.map { "\nPublished: " + $0 } ?? "\nNot published. /deliver publish pushes it to \(draft.repository.isEmpty ? "GitHub (this checkout has no GitHub origin yet)" : draft.repository) as \(draft.branch) and opens a draft pull request.")
        }
        switch words.first?.lowercased() {
        case nil:
            guard let draft = current(workspace, home: home) else { return ("No draft. /deliver review BASE shows what this checkout would deliver.", nil) }
            return (described(draft), nil)
        case "review":
            guard words.count == 2 else { throw MasonError("Use /deliver review BASE, for example /deliver review main.") }
            let reviewed = try await review(workspace, base: words[1])
            if reviewed.patches.isEmpty { return ("This checkout has nothing that \(words[1]) does not.", nil) }
            let lines = reviewed.patches.map { "\($0.hunk.id)  \($0.path)  \($0.hunk.title)" }
            return ("\(reviewed.patches.count) hunk\(reviewed.patches.count == 1 ? "" : "s") against \(words[1]) at \(reviewed.revision.prefix(12)):\n" + lines.joined(separator: "\n")
                    + "\nNext: /deliver draft \(words[1]) all TITLE takes them all, or list ids separated by commas.", nil)
        case "draft":
            guard words.count >= 4 else { throw MasonError("Use /deliver draft BASE HUNKS TITLE, for example /deliver draft main all Fix the cart total.") }
            let reviewed = try await review(workspace, base: words[1])
            let chosen: Set<String> = words[2].lowercased() == "all" ? Set(reviewed.patches.map(\.hunk.id)) : Set(words[2].split(separator: ",").map(String.init))
            let made = try await draft(workspace, base: words[1], pinned: reviewed.revision, title: words.dropFirst(3).joined(separator: " "), selected: chosen, receipt: receipt, home: home)
            return ("Drafted " + described(made), nil)
        case "publish":
            guard let draft = current(workspace, home: home) else { throw MasonError("No draft. Make one with /deliver draft.") }
            return ("Published as a draft pull request: " + (try await publish(workspace, draft, home: home)), nil)
        case "feedback":
            guard let draft = current(workspace, home: home) else { throw MasonError("No draft. Make one with /deliver draft.") }
            let found = try await feedback(workspace, draft)
            if found.observations.isEmpty { return ("Nothing to act on at \(found.url): no failed checks, requested changes or review comments.", nil) }
            let text = "\(found.observations.count) thing\(found.observations.count == 1 ? "" : "s") to act on at \(found.url):\n" + found.observations.joined(separator: "\n")
            let send = words.count > 1 && words[1].lowercased() == "send"
            return (text, send ? "The pull request \(found.url) has this feedback. Address each item, and say which you could not:\n" + found.observations.map { "- " + $0 }.joined(separator: "\n") : nil)
        default:
            return (usage, nil)
        }
    }
}

extension Session {
    /// What Mason's own checks found on the latest request that had any, when they all held: for a pull request's description.
    public var passedReceipt: String? {
        guard let receipt = turns.last(where: { !$0.undone && $0.checks != nil })?.checks, receipt.passed, !receipt.results.isEmpty else { return nil }
        return receipt.report
    }
}
