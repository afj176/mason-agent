import Foundation

/// What Mason may do in one project, set by the user and kept outside the project so nothing in it can change it:
/// tools that are switched off, the workflows that may run, when the policy lapses, and whether the project's hooks run.
public struct ProjectPolicy: Codable, Equatable {
    public var name: String
    public var workspace: String
    public var disabledTools: [String]
    /// The only workflows that may run, or nil for any.
    public var workflows: [String]?
    public var expires: Date?
    /// Whether workflows marked with events (afterEdit, beforeVerify) run by themselves. They only ever run in Auto, sandboxed.
    public var hooks: Bool
    /// Skills switched off for this project, by name. They are left out of the prompt and read_skill refuses them.
    public var disabledSkills: [String]? = nil

    /// Refuses a tool the policy switches off, and a workflow it does not list.
    public func check(_ tool: String, workflow: String? = nil) throws {
        if disabledTools.contains(tool) || (tool == "run_workflow" && workflows != nil && !(workflows ?? []).contains(workflow ?? "")) {
            throw MasonError("The project policy \(name) does not allow \(tool == "run_workflow" ? "the workflow " + (workflow ?? "") : tool). The user set it; do not look for a way around it.")
        }
    }
}

public enum Policies {
    static func file(_ home: URL, _ workspace: String) -> URL {
        home.appendingPathComponent("project-policies").appendingPathComponent(Experiments.fingerprint(realPath(workspace).lowercased()) + ".json")
    }
    static var encoder: JSONEncoder { let made = JSONEncoder(); made.dateEncodingStrategy = .iso8601; made.outputFormatting = [.prettyPrinted, .sortedKeys]; return made }
    static var decoder: JSONDecoder { let made = JSONDecoder(); made.dateDecodingStrategy = .iso8601; return made }

    /// The project's policy while it is in force.
    public static func load(home: URL = DataHome.url, workspace: String, includeExpired: Bool = false) -> ProjectPolicy? {
        guard let data = try? Data(contentsOf: file(home, workspace)), data.count <= 32 * 1024, let policy = try? decoder.decode(ProjectPolicy.self, from: data), policy.workspace == realPath(workspace) else { return nil }
        if !includeExpired, let expires = policy.expires, expires <= Date() { return nil }
        return policy
    }

    public static func set(_ policy: ProjectPolicy?, home: URL = DataHome.url, workspace: String) throws {
        let target = file(home, workspace)
        guard !(realPath(home.path) + "/").hasPrefix(realPath(workspace) + "/") else { throw MasonError("Policies are kept outside the project.") }
        guard let policy else { try? FileManager.default.removeItem(at: target); return }
        guard (1...80).contains(policy.name.count), policy.disabledTools.count <= 128, (policy.workflows ?? []).count <= 32, (policy.disabledSkills ?? []).count <= 256,
              !(policy.disabledTools + (policy.workflows ?? []) + (policy.disabledSkills ?? [])).contains(where: { $0.isEmpty || $0.count > 80 }) else {
            throw MasonError("A policy has a name of up to 80 characters, up to 128 tools, up to 256 skills and up to 32 workflows.")
        }
        try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        try encoder.encode(policy).write(to: target, options: .atomic)
    }

    /// The project's policy, or a new one, changed by `edit` and saved.
    public static func update(home: URL = DataHome.url, workspace: Workspace, _ edit: (inout ProjectPolicy) throws -> Void) throws {
        let root = workspace.root.path
        var policy = load(home: home, workspace: root, includeExpired: true) ?? ProjectPolicy(name: "for " + workspace.root.lastPathComponent, workspace: realPath(root), disabledTools: [], workflows: nil, expires: nil, hooks: false)
        try edit(&policy)
        try set(policy, home: home, workspace: root)
    }

    public static let usage = """
        /policy                         This project's policy
        /policy disable TOOL…           Switch tools off for this project (enable TOOL… switches them back on)
        /policy workflows NAME…         The only workflows that may run (any: no limit)
        /policy hooks on|off            Whether workflows marked afterEdit or beforeVerify run by themselves, in Auto
        /policy expires DAYS            When the policy lapses (never: it stays)
        /policy name NAME               What the policy is called when it refuses something
        /policy clear                   Remove the policy
        """

    static func described(_ policy: ProjectPolicy) -> String {
        let formatter = DateFormatter(); formatter.dateStyle = .medium; formatter.timeStyle = .short
        let tools: String = policy.disabledTools.isEmpty ? "none" : policy.disabledTools.sorted().joined(separator: ", ")
        var workflows = "any"
        if let listed = policy.workflows { workflows = listed.isEmpty ? "none may run" : listed.sorted().joined(separator: ", ") }
        let hooks: String = policy.hooks ? "on: workflows marked afterEdit or beforeVerify run by themselves in Auto" : "off"
        var expires = "never"
        if let date = policy.expires { expires = formatter.string(from: date) + (date <= Date() ? " (lapsed)" : "") }
        let skills: String = (policy.disabledSkills ?? []).isEmpty ? "none" : (policy.disabledSkills ?? []).sorted().joined(separator: ", ")
        let lines: [String] = ["Policy \(policy.name)", "tools off   " + tools, "skills off  " + skills, "workflows   " + workflows, "hooks       " + hooks, "expires     " + expires]
        return lines.joined(separator: "\n")
    }

    /// The /policy command, shared by the command line and the app. Only the user can run it; the model has no tool for it.
    public static func command(_ rest: String, workspace: Workspace, home: URL = DataHome.url) throws -> String {
        let words = rest.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
        let root = workspace.root.path
        var policy = load(home: home, workspace: root, includeExpired: true) ?? ProjectPolicy(name: "for " + workspace.root.lastPathComponent, workspace: realPath(root), disabledTools: [], workflows: nil, expires: nil, hooks: false)
        guard let verb = words.first?.lowercased() else {
            return load(home: home, workspace: root, includeExpired: true).map(described) ?? "This project has no policy: every tool is available, any workflow may run, and hooks are off.\n" + usage
        }
        let rest = Array(words.dropFirst())
        switch verb {
        case "disable": guard !rest.isEmpty else { throw MasonError("Name the tools to switch off.") }; policy.disabledTools = Array(Set(policy.disabledTools + rest)).sorted()
        case "enable": guard !rest.isEmpty else { throw MasonError("Name the tools to switch back on.") }; policy.disabledTools.removeAll { rest.contains($0) }
        case "workflows": guard !rest.isEmpty else { throw MasonError("Name the workflows that may run, or any.") }; policy.workflows = rest.count == 1 && rest[0].lowercased() == "any" ? nil : rest
        case "hooks": guard rest.count == 1, ["on", "off"].contains(rest[0].lowercased()) else { throw MasonError("Use /policy hooks on or /policy hooks off.") }; policy.hooks = rest[0].lowercased() == "on"
        case "expires":
            guard rest.count == 1 else { throw MasonError("Use /policy expires DAYS, or /policy expires never.") }
            if rest[0].lowercased() == "never" { policy.expires = nil } else {
                guard let days = Int(rest[0]), (1...3650).contains(days) else { throw MasonError("Use a number of days from 1 to 3650, or never.") }
                policy.expires = Date().addingTimeInterval(Double(days) * 86_400)
            }
        case "name": guard !rest.isEmpty else { throw MasonError("Give the policy a name.") }; policy.name = rest.joined(separator: " ")
        case "clear":
            try set(nil, home: home, workspace: root)
            return "Removed this project's policy."
        default: return usage
        }
        try set(policy, home: home, workspace: root)
        return described(policy)
    }
}
