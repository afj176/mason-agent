import Foundation

/// Every built-in tool the agent can be offered, in one table: its group, what it does in plain words, and whether it
/// changes files or runs commands and whether it uses the internet. The settings and /tools switch tools off through
/// this table, so nobody has to type a tool's name; the project policy's disabledTools stays the single record.
public enum ToolCatalogue {
    public struct Entry: Equatable {
        public let name: String
        public let group: String
        public let summary: String
        public let changes: Bool
        public let internet: Bool
        /// The loop cannot run without it, so it cannot be switched off.
        public let alwaysOn: Bool
    }

    public static let groups = ["core", "code", "docs", "workflows", "jobs", "browser", "desktop", "mcp", "web"]

    static func tool(_ name: String, _ group: String, _ summary: String, changes: Bool = false, internet: Bool = false, alwaysOn: Bool = false) -> Entry {
        Entry(name: name, group: group, summary: summary, changes: changes, internet: internet, alwaysOn: alwaysOn)
    }

    public static let entries: [Entry] = [
        tool("enable_tools", "core", "Loads a group of tools when the model needs it", alwaysOn: true),
        tool("list_files", "core", "Lists the project's files"),
        tool("read_file", "core", "Reads a file with line numbers"),
        tool("read_lines", "core", "Reads part of a large file"),
        tool("search_files", "core", "Searches the project's files for text"),
        tool("search_next", "core", "Continues a search"),
        tool("edit_file", "core", "Replaces one passage in a file", changes: true),
        tool("apply_edits", "core", "Makes several replacements in one file", changes: true),
        tool("write_file", "core", "Creates or replaces a file", changes: true),
        tool("run_shell", "core", "Runs a shell command in the project", changes: true),
        tool("run_command", "core", "Runs one program with exact arguments", changes: true),
        tool("ask_user", "core", "Asks you a question and waits for the answer"),
        tool("suggest_note", "core", "Suggests a note about the project for you to keep"),
        tool("open_file", "core", "Shows you a file in the app at a line"),
        tool("repository_map", "code", "Outlines the project's files and what each defines"),
        tool("navigate_code", "code", "Finds where a name is defined, used or tested"),
        tool("inspect_symbol", "code", "Asks a language server about one position in a file", changes: true),
        tool("semantic_search", "code", "Finds code by meaning with the embeddings server"),
        tool("git_status", "code", "Shows the Git branch and changed files"),
        tool("delegate_readonly", "code", "Asks a separate read-only context about a few files"),
        tool("read_documentation", "docs", "Looks things up in the project's and bundles' documentation"),
        tool("read_skill", "workflows", "Reads a skill's instructions"),
        tool("run_workflow", "workflows", "Runs one of the project's named workflows", changes: true),
        tool("start_job", "jobs", "Starts a long-running command in the background", changes: true),
        tool("poll_job", "jobs", "Reads a background command's output"),
        tool("stop_job", "jobs", "Stops a background command"),
        tool("start_terminal", "jobs", "Starts a program to answer line by line", changes: true),
        tool("terminal_input", "jobs", "Sends a line to that program", changes: true),
        tool("terminal_status", "jobs", "Reads that program's output"),
        tool("terminal_stop", "jobs", "Stops that program"),
        tool("check_page", "browser", "Loads a project page in a browser and checks it"),
        tool("verify_acceptance", "browser", "Runs the project's acceptance scenarios in a browser"),
        tool("open_preview", "browser", "Shows you a project page in the Website tab"),
        tool("desktop_observe", "desktop", "Reads the controls of a window you attached"),
        tool("desktop_visual", "desktop", "Looks at a picture of that window"),
        tool("desktop_act", "desktop", "Presses or sets a control in that window, asking first", changes: true),
        tool("mcp_tools", "mcp", "Lists what an MCP server offers"),
        tool("mcp_call", "mcp", "Calls an allowed tool on an MCP server, asking first", changes: true, internet: true),
        tool("mcp_resources", "mcp", "Lists an MCP server's resources"),
        tool("mcp_read", "mcp", "Reads a resource from an MCP server"),
        tool("web_fetch", "web", "Reads a web page over HTTPS, asking before each new host", internet: true),
        tool("web_search", "web", "Searches the web with DuckDuckGo, asking first", internet: true),
    ]

    public static func entry(_ name: String) -> Entry? { entries.first { $0.name == name.lowercased() } }

    /// The tools switched off for a project.
    public static func disabled(_ workspace: Workspace, home: URL = DataHome.url) -> Set<String> {
        Set(Policies.load(home: home, workspace: workspace.root.path)?.disabledTools ?? [])
    }

    /// Switches a tool on or off for this project through its policy's disabledTools.
    public static func set(_ name: String, on: Bool, workspace: Workspace, home: URL = DataHome.url) throws -> String {
        guard let found = entry(name) else { throw MasonError("No tool named \(name). /tools lists them.") }
        if found.alwaysOn { throw MasonError("\(found.name) is always on: Mason cannot work without it.") }
        try Policies.update(home: home, workspace: workspace) { policy in
            policy.disabledTools.removeAll { $0 == found.name }
            if !on { policy.disabledTools.append(found.name); policy.disabledTools.sort() }
        }
        return "\(found.name) is \(on ? "on" : "off") for this project."
    }

    /// The rows /tools prints: each group's tools with on or off, then the MCP servers.
    public static func listing(_ workspace: Workspace, home: URL = DataHome.url) -> String {
        let off = disabled(workspace, home: home)
        var lines: [String] = []
        for group in groups {
            let members = entries.filter { $0.group == group }
            guard !members.isEmpty else { continue }
            lines.append(group)
            for tool in members {
                let state = tool.alwaysOn ? "on" : off.contains(tool.name) ? "off" : "on"
                lines.append("  " + tool.name.padding(toLength: 20, withPad: " ", startingAt: 0) + state.padding(toLength: 5, withPad: " ", startingAt: 0) + tool.summary + (tool.alwaysOn ? " (always on)" : ""))
            }
        }
        for server in Mcp.enabled(home: home) {
            lines.append("mcp: \(server.name) (\(server.tools.count) tool\(server.tools.count == 1 ? "" : "s") allowed)")
        }
        return lines.joined(separator: "\n")
    }

    public static let usage = """
        /tools                 Every tool, by group, on or off for this project
        /tools on NAME         Switch a tool back on
        /tools off NAME        Switch a tool off for this project
        """

    /// The /tools command for a project, shared by the command line and the app.
    public static func command(_ rest: String, workspace: Workspace, home: URL = DataHome.url) throws -> String {
        let words = rest.split(separator: " ").map(String.init)
        guard let verb = words.first?.lowercased() else { return listing(workspace, home: home) }
        guard ["on", "off"].contains(verb) else { return usage }
        guard words.count == 2 else { throw MasonError("Use /tools \(verb) NAME.") }
        return try set(words[1], on: verb == "on", workspace: workspace, home: home)
    }
}
