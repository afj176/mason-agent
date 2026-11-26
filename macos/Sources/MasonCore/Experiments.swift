import Foundation

/// An experiment is a copy of a project in its own Git worktree, started from the project exactly as it stands
/// (uncommitted and untracked files included). Work there does not touch the project; a hunk that turns out well is
/// applied back by the user. Creating one is the user's own operation: the model cannot choose another workspace.
public struct Experiment: Codable, Equatable {
    public var name: String
    /// The project it was started from, and the worktree it lives in.
    public var parent: String
    public var path: String
    public var branch: String
    /// The commit that records the project's state when the experiment began.
    public var revision: String
    public var created: Date
}

public struct ExperimentHunk: Equatable {
    public let id: String
    public let title: String
    public let patch: String
}

public enum Experiments {
    /// Nothing in the repository's own settings may start a program, and paths are never read as patterns.
    static let options = "-c core.hooksPath=/dev/null -c core.fsmonitor= -c core.quotepath=false -c commit.gpgsign=false --no-optional-locks --literal-pathspecs"
    static let ownIdentity = " -c user.name='Mason Experiment' -c user.email=mason@localhost"

    /// Runs git in `workspace` and returns what it printed. This is not sandboxed: a worktree lives outside the project.
    /// `identity` false leaves a commit to carry the user's own name.
    static func git(_ workspace: Workspace, _ arguments: [String], index: String? = nil, identity: Bool = true) async throws -> String {
        let printed = FileManager.default.temporaryDirectory.appendingPathComponent("mason-git-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: printed) }
        let command = (index.map { "GIT_INDEX_FILE=" + Shell.quote($0) + " " } ?? "") + "git " + options + (identity ? ownIdentity : "") + " " + arguments.map(Shell.quote).joined(separator: " ") + " > " + Shell.quote(printed.path)
        let result = try await Shell.run(command, in: workspace, sandboxed: false, timeout: 90)
        guard result.exitCode == 0, !result.timedOut else {
            throw MasonError("git \(arguments.first ?? "") failed: " + result.output.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return String(decoding: (try? Data(contentsOf: printed)) ?? Data(), as: UTF8.self)
    }

    /// The tree of everything in the checkout now, built in a private index so the user's own index is untouched.
    static func snapshot(_ workspace: Workspace) async throws -> String {
        let index = FileManager.default.temporaryDirectory.appendingPathComponent("mason-index-" + UUID().uuidString).path
        defer { try? FileManager.default.removeItem(atPath: index); try? FileManager.default.removeItem(atPath: index + ".lock") }
        _ = try await git(workspace, ["read-tree", "HEAD"], index: index)
        _ = try await git(workspace, ["add", "--all", "--", "."], index: index)
        return try await git(workspace, ["write-tree"], index: index).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Adds a worktree for a new branch at `destination`, which must be a new folder outside the project.
    public static func createWorktree(_ project: Workspace, destination: String, branch: String, revision: String = "HEAD") async throws -> String {
        guard revision == "HEAD" || revision.range(of: "^(?:[0-9a-f]{40}|[0-9a-f]{64})$", options: .regularExpression) != nil else { throw MasonError("Invalid base revision.") }
        guard branch.range(of: #"^\w[\w/-]{0,100}$"#, options: .regularExpression) != nil, !branch.contains(".."), !branch.contains("//"), !branch.hasSuffix("/") else { throw MasonError("Use a new Git branch name.") }
        let target = URL(fileURLWithPath: destination).standardizedFileURL
        let parent = target.deletingLastPathComponent()
        var folder: ObjCBool = false
        guard FileManager.default.fileExists(atPath: parent.path, isDirectory: &folder), folder.boolValue else { throw MasonError("The folder that would hold the worktree does not exist.") }
        let real = realPath(parent.path) + "/" + target.lastPathComponent, root = realPath(project.root.path)
        guard !FileManager.default.fileExists(atPath: real), real != root, !(real + "/").hasPrefix(root + "/"), !(root + "/").hasPrefix(real + "/") else {
            throw MasonError("Choose a new worktree path outside the project.")
        }
        _ = try await git(project, ["worktree", "add", "-b", branch, "--", real, revision])
        return realPath(real)
    }

    // MARK: the record of experiments

    static func file(_ home: URL) -> URL { home.appendingPathComponent("experiments.json") }

    public static func load(home: URL = DataHome.url) -> [Experiment] {
        guard let data = try? Data(contentsOf: file(home)) else { return [] }
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode([Experiment].self, from: data)) ?? []
    }

    static func save(_ all: [Experiment], home: URL) throws {
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601; encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try encoder.encode(all).write(to: file(home), options: .atomic)
    }

    /// This project's experiments whose worktrees still exist.
    public static func list(_ workspace: Workspace, home: URL = DataHome.url) -> [Experiment] {
        let root = realPath(workspace.root.path)
        return load(home: home).filter { $0.parent == root && FileManager.default.fileExists(atPath: $0.path) }
    }

    // MARK: starting, reading and applying

    /// Starts an experiment beside the project, in `PROJECT-NAME` on the branch `mason/NAME`.
    public static func fork(_ workspace: Workspace, name: String, home: URL = DataHome.url) async throws -> Experiment {
        guard name.range(of: "^[A-Za-z0-9][A-Za-z0-9_-]{0,40}$", options: .regularExpression) != nil else { throw MasonError("Name the experiment with letters, digits, - or _ (up to 41 characters).") }
        let root = realPath(workspace.root.path)
        guard !load(home: home).contains(where: { $0.parent == root && $0.name.lowercased() == name.lowercased() }) else { throw MasonError("This project already has an experiment named \(name).") }
        guard FileManager.default.fileExists(atPath: root + "/.git") else { throw MasonError("Experiments need a Git repository, and this project is not one.") }
        let head = try await git(workspace, ["rev-parse", "HEAD"]).trimmingCharacters(in: .whitespacesAndNewlines)
        let tree = try await snapshot(workspace)
        guard try await snapshot(workspace) == tree, try await git(workspace, ["rev-parse", "HEAD"]).trimmingCharacters(in: .whitespacesAndNewlines) == head else {
            throw MasonError("The project changed while the experiment was being captured. Try again.")
        }
        let revision = try await git(workspace, ["commit-tree", tree, "-p", head, "-m", "Mason experiment starting state"]).trimmingCharacters(in: .whitespacesAndNewlines)
        let destination = URL(fileURLWithPath: root).deletingLastPathComponent().appendingPathComponent(URL(fileURLWithPath: root).lastPathComponent + "-" + name).path
        let path = try await createWorktree(workspace, destination: destination, branch: "mason/" + name, revision: revision)
        let experiment = Experiment(name: name, parent: root, path: path, branch: "mason/" + name, revision: revision, created: Date())
        try save(load(home: home) + [experiment], home: home)
        return experiment
    }

    /// The files the experiment has changed since it began.
    public static func changed(_ experiment: Experiment) async throws -> [String] {
        let fork = try Workspace(path: experiment.path)
        let tree = try await snapshot(fork)
        return try await git(fork, ["diff", "--no-renames", "--name-only", experiment.revision, tree, "--"]).split(separator: "\n").map(String.init)
    }

    /// One file's changes, a hunk at a time. A file that was added, deleted or changed mode comes as one piece.
    public static func hunks(_ experiment: Experiment, path: String) async throws -> [ExperimentHunk] {
        let fork = try Workspace(path: experiment.path)
        guard !path.hasPrefix("/"), !path.split(separator: "/").contains("..") else { throw MasonError("Name a file inside the experiment.") }
        let tree = try await snapshot(fork)
        let diff = try await git(fork, ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "--binary", experiment.revision, tree, "--", path])
        if diff.isEmpty { return [] }
        func hunk(_ title: String, _ patch: String) -> ExperimentHunk { ExperimentHunk(id: fingerprint(patch), title: title, patch: patch) }
        let whole = diff as NSString
        let starts = try NSRegularExpression(pattern: "^@@ [-+0-9, ]+ @@[^\\n]*", options: .anchorsMatchLines).matches(in: diff, range: NSRange(location: 0, length: whole.length))
        if starts.isEmpty || diff.contains("\nnew file mode ") || diff.contains("\ndeleted file mode ") || diff.contains("\nold mode ") {
            return [hunk("Whole file · " + path, diff)]
        }
        let header = whole.substring(to: starts[0].range.location)
        return starts.enumerated().map { index, match in
            let end = index + 1 < starts.count ? starts[index + 1].range.location : whole.length
            return hunk(whole.substring(with: match.range), header + whole.substring(with: NSRange(location: match.range.location, length: end - match.range.location)))
        }
    }

    /// Applies one hunk to the project the experiment came from. Git checks the hunk's context first, and the user's
    /// index is left alone. The id is refused when the experiment has changed since the hunk was read.
    public static func apply(_ experiment: Experiment, path: String, hunk id: String) async throws {
        guard let chosen = try await hunks(experiment, path: path).first(where: { $0.id == id }) else { throw MasonError("The experiment changed, or there is no hunk \(id) in \(path). Read the diff again.") }
        let patch = FileManager.default.temporaryDirectory.appendingPathComponent("mason-patch-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: patch) }
        try Data(chosen.patch.utf8).write(to: patch)
        let parent = try Workspace(path: experiment.parent)
        do { _ = try await git(parent, ["apply", "--check", "--whitespace=nowarn", patch.path]) }
        catch { throw MasonError("The hunk does not fit the project as it is now, so nothing was changed. " + ((error as? MasonError)?.description ?? "")) }
        _ = try await git(parent, ["apply", "--whitespace=nowarn", patch.path])
    }

    /// A short name for a patch's exact text (FNV-1a), so a hunk that has changed is not applied by an old name.
    static func fingerprint(_ text: String) -> String {
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for byte in text.utf8 { hash ^= UInt64(byte); hash = hash &* 0x0000_0100_0000_01b3 }
        return String(String(hash, radix: 16).suffix(8))
    }

    // MARK: the /experiment command, shared by the command line and the app

    public static let usage = """
        /experiment                       List this project's experiments and what each has changed
        /experiment new NAME              Copy the project as it stands into a worktree beside it (PROJECT-NAME, branch mason/NAME)
        /experiment diff NAME [PATH]      The files an experiment changed, or one file's hunks with their ids
        /experiment apply NAME PATH ID    Apply one hunk to this project (or "all" for every hunk of the file)
        /experiment forget NAME           Drop the record; the worktree and branch are left for you to remove
        """

    public static func command(_ rest: String, workspace: Workspace, home: URL = DataHome.url) async throws -> String {
        let words = rest.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
        let all = list(workspace, home: home)
        func named(_ name: String?) throws -> Experiment {
            guard let name, let found = all.first(where: { $0.name.lowercased() == name.lowercased() }) else { throw MasonError("No experiment named \(name ?? "") in this project. /experiment lists them.") }
            return found
        }
        switch words.first?.lowercased() {
        case nil, "list":
            if all.isEmpty { return "No experiments. /experiment new NAME copies this project, as it stands, into a worktree beside it." }
            var lines: [String] = []
            for experiment in all {
                let files = (try? await changed(experiment)) ?? []
                lines.append("\(experiment.name)  ·  \(experiment.path)  ·  \(files.count) file\(files.count == 1 ? "" : "s") changed" + (files.isEmpty ? "" : ": " + files.prefix(8).joined(separator: ", ") + (files.count > 8 ? ", …" : "")))
            }
            return lines.joined(separator: "\n")
        case "new":
            guard words.count == 2 else { throw MasonError("Use /experiment new NAME.") }
            let made = try await fork(workspace, name: words[1], home: home)
            return "Started the experiment \(made.name) in \(made.path) on the branch \(made.branch), from this project as it stands.\nWork on it with: mason --workspace \(Shell.quote(made.path))"
        case "diff":
            let experiment = try named(words.count > 1 ? words[1] : nil)
            if words.count == 2 {
                let files = try await changed(experiment)
                return files.isEmpty ? "\(experiment.name) has changed nothing yet." : "\(experiment.name) changed:\n" + files.joined(separator: "\n") + "\n/experiment diff \(experiment.name) PATH shows a file's hunks."
            }
            let path = words.dropFirst(2).joined(separator: " ")
            let found = try await hunks(experiment, path: path)
            if found.isEmpty { return "\(experiment.name) has not changed \(path)." }
            return found.map { "hunk \($0.id)  \($0.title)\n" + String($0.patch.prefix(6000)) }.joined(separator: "\n") + "\nApply one with /experiment apply \(experiment.name) \(path) ID."
        case "apply":
            guard words.count >= 4 else { throw MasonError("Use /experiment apply NAME PATH ID.") }
            let experiment = try named(words[1])
            let path = words[2..<(words.count - 1)].joined(separator: " "), id = words[words.count - 1]
            if id.lowercased() == "all" {
                let found = try await hunks(experiment, path: path)
                guard !found.isEmpty else { throw MasonError("\(experiment.name) has not changed \(path).") }
                for hunk in found { try await apply(experiment, path: path, hunk: hunk.id) }
                return "Applied \(found.count) hunk\(found.count == 1 ? "" : "s") of \(path) from \(experiment.name) to this project."
            }
            try await apply(experiment, path: path, hunk: id)
            return "Applied hunk \(id) of \(path) from \(experiment.name) to this project."
        case "forget":
            let experiment = try named(words.count > 1 ? words[1] : nil)
            try save(load(home: home).filter { $0 != experiment }, home: home)
            return "Forgot \(experiment.name). Its worktree is still at \(experiment.path); remove it with: git worktree remove \(Shell.quote(experiment.path))"
        default:
            return usage
        }
    }
}
