namespace Mason.Code;

/// <summary>One built-in tool the agent can be offered: its group, what it does in plain words, and whether it changes
/// files or runs commands, or uses the internet. <see cref="AlwaysOn"/> tools cannot be switched off.</summary>
public sealed record CodeToolInfo(string Name, string Group, string Line, bool Changes = false, bool Internet = false)
{
    public bool AlwaysOn => Name is "finish" or "enable_tools";
}

/// <summary>Every built-in tool, in display order. The project policy's disabledTools is edited through this table, so
/// nobody has to type a tool name. A check fails when the engine can offer a tool that is missing here.</summary>
public static class CodeToolCatalog
{
    public static readonly string[] Groups = ["core", "code", "docs", "workflows", "jobs", "browser", "desktop", "mcp", "web"];

    public static readonly IReadOnlyList<CodeToolInfo> All =
    [
        new("finish", "core", "Ends the request with a summary"),
        new("enable_tools", "core", "Loads a group of extra tools when the work needs them"),
        new("list_files", "core", "Lists the project's files"),
        new("search_text", "core", "Searches the project for text"),
        new("search_next", "core", "Shows more results from the last search"),
        new("read_file", "core", "Reads a file"),
        new("read_lines", "core", "Reads a range of lines from a large file"),
        new("open_file", "core", "Opens a file in your viewer"),
        new("navigate_code", "core", "Finds definitions, references and tests for a name"),
        new("semantic_search", "core", "Finds code by meaning, when a search model is connected"),
        new("ask_user", "core", "Stops to ask you a question"),
        new("suggest_note", "core", "Suggests a note about the project for you to keep"),
        new("create_directory", "core", "Creates a folder", Changes: true),
        new("create_file", "core", "Creates a new file", Changes: true),
        new("edit_file", "core", "Changes part of a file", Changes: true),
        new("apply_edits", "core", "Makes several changes to one file at once", Changes: true),
        new("replace_file", "core", "Rewrites a whole file", Changes: true),
        new("run_command", "core", "Runs a program", Changes: true),
        new("run_shell", "core", "Runs a command in the chosen shell", Changes: true),
        new("open_preview", "core", "Opens a website preview on this computer"),
        new("verify_acceptance", "core", "Runs the request's feature checks"),
        new("repository_map", "code", "Finds where names are defined and used across the project"),
        new("inspect_symbol", "code", "Looks up a name at a position with the language's own tools"),
        new("git_status", "code", "Shows the Git status of the project"),
        new("delegate_readonly", "code", "Asks a separate read-only helper a question about a few files"),
        new("read_documentation", "docs", "Looks up a phrase in registered documentation", Internet: true),
        new("read_skill", "workflows", "Reads one of the project's skills"),
        new("run_workflow", "workflows", "Runs a named project workflow", Changes: true),
        new("start_job", "jobs", "Starts a long-running program in the background", Changes: true),
        new("poll_job", "jobs", "Reads a background program's output"),
        new("stop_job", "jobs", "Stops a background program"),
        new("start_terminal", "jobs", "Starts a terminal in the sandbox", Changes: true),
        new("terminal_input", "jobs", "Types a line into a terminal", Changes: true),
        new("terminal_status", "jobs", "Reads a terminal's recent output"),
        new("terminal_stop", "jobs", "Stops a terminal"),
        new("computer_use", "browser", "Uses the project's page in a private copy of Edge"),
        new("desktop_observe", "desktop", "Reads the controls of the window you attached"),
        new("desktop_visual", "desktop", "Looks at the window you attached with a vision model"),
        new("desktop_act", "desktop", "Asks to press or fill a control in the window you attached", Changes: true),
        new("mcp_tools", "mcp", "Lists the tools an MCP server allows"),
        new("mcp_call", "mcp", "Calls an allowed MCP tool, after you approve", Changes: true),
        new("mcp_resources", "mcp", "Lists an MCP server's resources"),
        new("mcp_read", "mcp", "Reads one MCP resource"),
        new("web_fetch", "web", "Reads a web page over HTTPS, after you allow its host", Internet: true),
        new("web_search", "web", "Searches the web with DuckDuckGo, after you allow it", Internet: true),
    ];

    public static CodeToolInfo? Find(string name) => All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public static IEnumerable<CodeToolInfo> InGroup(string group) => All.Where(t => t.Group == group);

    /// <summary>The disabled list with always-on tools left out, so nothing can switch them off.</summary>
    public static IEnumerable<string> Removable(IEnumerable<string> disabled) => disabled.Where(t => Find(t)?.AlwaysOn != true);

    /// <summary>/tools: the tools by group with on or off, then MCP servers as "mcp: SERVER (N tools allowed)".</summary>
    public static string Describe(string settingsRoot, string workspace)
    {
        var off = new HashSet<string>(CodeProjectPolicy.Load(settingsRoot, workspace)?.DisabledTools ?? [], StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var group in Groups)
        {
            lines.Add(group);
            foreach (var tool in InGroup(group))
                lines.Add("  " + tool.Name.PadRight(20) + " " + (tool.AlwaysOn ? "on " : off.Contains(tool.Name) ? "off" : "on ") + "  " + tool.Line + (tool.AlwaysOn ? " (always on)" : ""));
        }
        List<McpRegistration> servers;
        try { servers = CodeMcpRegistry.ReadAll(settingsRoot); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { servers = []; }
        foreach (var server in servers)
            lines.Add("mcp: " + server.Name + " (" + server.Tools.Length + " tool" + (server.Tools.Length == 1 ? "" : "s") + " allowed)" + (server.Enabled ? "" : " · off"));
        lines.Add("/tools on NAME or /tools off NAME switches a tool for this project.");
        return string.Join("\n", lines);
    }
}
