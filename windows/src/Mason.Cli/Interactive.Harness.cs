using Mason.Code;

/// <summary>/skills, /skill, /tools, /network, /mcp, /policy and /bundle: the same behaviour and words as macOS and Linux.</summary>
internal sealed partial class Interactive
{
    static readonly (string, string)[] HarnessHelp =
    [
        ("/skills", "Every skill: on, off or hidden, where it comes from and what it is for"),
        ("/skills show NAME", "Print a skill's path and its SKILL.md"),
        ("/skills new NAME [description]", "Make a skill in .mason/skills"),
        ("/skills on|off NAME", "Switch a skill on or off for this project"),
        ("/skill NAME [request]", "Run a request that follows a skill"),
        ("/tools", "Every tool by group, on or off for this project, then your MCP servers"),
        ("/tools on|off NAME", "Switch a tool on or off for this project"),
        ("/network [default|ask|off]", CodeNetworkSettings.Help),
        ("/mcp", "List your MCP servers and the tools Mason may call"),
        ("/mcp add NAME URL|PROGRAM [ARGS]", "Register a server on this computer, then choose its tools"),
        ("/mcp allow NAME TOOL…|all", "Choose which of a server's tools Mason may call"),
        ("/mcp tools NAME · on|off|remove NAME", "See what a server offers; switch it on or off; remove it"),
        ("/policy", "This project's limits: tools switched off, the workflows that may run, and whether its hooks run (see /policy help)"),
        ("/bundle", "Install a folder of skills and documentation for every project to use (see /bundle help)"),
    ];

    static void Lines(string text) { foreach (var line in text.ReplaceLineEndings("\n").Split('\n')) Term.Line("  " + line); }

    void Skills(string rest)
    {
        var words = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = words.FirstOrDefault()?.ToLowerInvariant() ?? "";
        var argument = words.Length > 1 ? words[1] : "";
        switch (verb)
        {
            case "":
            {
                var all = CodeSkills.List(host, scope.Root);
                if (all.Count == 0) { Term.Line(Term.Paint(Term.Dim, "  No skills yet. /skills new NAME makes one in .mason/skills.")); return; }
                Term.Line();
                int width = Math.Min(28, all.Max(s => s.Name.Length));
                foreach (var skill in all)
                {
                    var state = skill.State switch { "on" => Term.Paint(Term.Green, "on    "), "off" => Term.Paint(Term.Yellow, "off   "), _ => Term.Paint(Term.Dim, "hidden") };
                    var note = skill.Hidden ? "Hidden by " + skill.ShadowedBy : skill.Summary;
                    Term.Line($"  {Term.Paint(skill.Hidden ? Term.Dim : Term.Accent, skill.Name.PadRight(width))}  {state}  {Term.Paint(Term.Dim, skill.Source.PadRight(10))}  {Term.Clip(note, Math.Max(20, Term.Width - width - 26))}");
                }
                Term.Line(Term.Paint(Term.Dim, "  /skills show NAME prints one · /skills on|off NAME switches one · /skill NAME [request] uses one"));
                return;
            }
            case "show":
            {
                var skill = Find(argument);
                Term.Line("  " + Term.Paint(Term.Bold, skill.Path == null ? skill.Source + " · " + skill.Name : Path.Combine(scope.Root, skill.Path.Replace('/', Path.DirectorySeparatorChar))));
                Term.Line();
                var text = skill.Path == null ? skill.Body : File.ReadAllText(Path.Combine(scope.Root, skill.Path));
                foreach (var line in text.ReplaceLineEndings("\n").Split('\n')) Term.Line("  " + line);
                return;
            }
            case "new":
            {
                var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) throw new ArgumentException("Use /skills new NAME [description].");
                var file = CodeSkills.Create(scope.Root, parts[0], parts.Length > 1 ? parts[1] : "");
                Term.Line("  " + Term.Paint(Term.Green, "✓") + " Created " + file);
                if (!OpenInEditor(file)) Term.Line(Term.Paint(Term.Dim, "  Fill in when to use it and its steps. Mason lists it with every request in this project."));
                return;
            }
            case "on" or "off":
            {
                if (argument.Length == 0) throw new ArgumentException("Use /skills " + verb + " NAME.");
                CodeSkills.Switch(host, scope.Root, argument, verb == "on");
                Term.Line(Term.Paint(Term.Dim, "  " + Find(argument).Name + " is " + verb + " for this project."));
                return;
            }
            default:
                throw new ArgumentException("Use /skills, /skills show NAME, /skills new NAME [description], /skills on NAME or /skills off NAME.");
        }
    }

    /// <summary>Opens a file Mason just created in $VISUAL or $EDITOR, as the macOS and Linux CLIs do, and waits for it to close.
    /// False when neither is set, there is no terminal, or the editor could not start; the caller then prints its hint.</summary>
    internal static bool OpenInEditor(string path)
    {
        var editor = new[] { "VISUAL", "EDITOR" }.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        if (editor == null || !Term.Interactive) return false;
        try
        {
            // Through cmd, so an editor given with its own options ("code --wait") works. /s keeps the quoting as written.
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + editor + " \"" + path + "\"\"") { UseShellExecute = false };
            Term.Line(Term.Paint(Term.Dim, "  Opening it in " + editor + "…"));
            using var process = System.Diagnostics.Process.Start(start);
            process?.WaitForExit();
            return process != null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Term.Line(Term.Paint(Term.Yellow, "  " + editor + " did not start: " + e.Message));
            return false;
        }
    }

    CodeSkill Find(string name)
    {
        var all = CodeSkills.List(host, scope.Root);
        return all.FirstOrDefault(s => !s.Hidden && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("There is no skill named " + name + ". /skills lists them.");
    }

    /// <summary>/skill NAME [request]: the request with the skill's body in front of it, or null when nothing should be sent.</summary>
    string? SkillRequest(string rest)
    {
        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("Use /skill NAME [request]. /skills lists them.");
        var body = CodeSkills.Read(CodeSkills.List(host, scope.Root), parts[0]);
        var name = Find(parts[0]).Name;
        var request = parts.Length > 1 ? parts[1] : "";
        if (request.Length == 0)
        {
            if (!Term.Interactive) throw new ArgumentException("Use /skill " + name + " REQUEST.");
            Term.Write("  " + Term.Paint(Term.Bold, "Request") + " for " + name + ": ");
            request = Console.ReadLine()?.Trim() ?? "";
            if (request.Length == 0) { Term.Line(Term.Paint(Term.Dim, "  Nothing sent.")); return null; }
        }
        return "Follow the skill " + name + ":\n\n" + body + "\n\nRequest: " + request;
    }

    void Tools(string rest)
    {
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            Term.Line();
            foreach (var line in CodeToolCatalog.Describe(host, scope.Root).Split('\n'))
            {
                if (CodeToolCatalog.Groups.Contains(line)) Term.Line("  " + Term.Paint(Term.Bold, line));
                else if (line.StartsWith("  ", StringComparison.Ordinal))
                {
                    var name = line[2..22]; var state = line[23..26]; var text = line[28..];
                    Term.Line("    " + Term.Paint(Term.Accent, name) + " " + (state.Trim() == "on" ? Term.Paint(Term.Green, state) : Term.Paint(Term.Yellow, state)) + "  " + Term.Paint(Term.Dim, text));
                }
                else if (line.StartsWith("mcp: ", StringComparison.Ordinal)) Term.Line("  " + line);
                else Term.Line(Term.Paint(Term.Dim, "  " + line));
            }
            return;
        }
        if (words.Length != 2 || words[0].ToLowerInvariant() is not ("on" or "off")) throw new ArgumentException("Use /tools, /tools on NAME or /tools off NAME.");
        bool on = words[0].Equals("on", StringComparison.OrdinalIgnoreCase);
        CodeProjectPolicy.SwitchTool(host, scope.Root, words[1], on);
        Term.Line(Term.Paint(Term.Dim, "  " + CodeToolCatalog.Find(words[1])!.Name + " is " + (on ? "on" : "off") + " for this project."));
    }

    void Network(string rest)
    {
        Lines(CodeNetworkSettings.SessionCommand(rest, session, host));
        store.Save(session);
    }

    /// <summary>MCP servers are registered in Mason's data folder, for every project, and can only do what is allowed here.</summary>
    async Task Mcp(string rest)
    {
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var servers = CodeMcpRegistry.ReadAll(host);
        int Index(string? name)
        {
            int found = name == null ? -1 : servers.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return found >= 0 ? found : throw new ArgumentException("No MCP server named " + name + ".  /mcp lists them.");
        }
        async Task ShowOffered(McpRegistration server)
        {
            try
            {
                var offered = await CodeMcpRegistry.Discover(server with { Enabled = true }, CancellationToken.None);
                if (offered.Length == 0) { Term.Line(Term.Paint(Term.Dim, "  " + server.Name + " offers no tools.")); return; }
                foreach (var tool in offered)
                {
                    bool allowed = server.Tools.Contains(tool.Name);
                    Term.Line("    " + (allowed ? Term.Paint(Term.Green, "✓") : Term.Paint(Term.Dim, "·")) + " " + Term.Paint(allowed ? Term.Bold : "", tool.Name.PadRight(22)) + " " + Term.Paint(Term.Dim, Term.Clip(tool.Description, Math.Max(20, Term.Width - 34))));
                }
                Term.Line(Term.Paint(Term.Dim, "  ✓ marks what Mason may call. Change it with /mcp allow " + server.Name + " TOOL… or /mcp allow " + server.Name + " all."));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            { Term.Line(Term.Paint(Term.Yellow, "  " + server.Name + " did not answer: ") + e.Message); }
        }
        switch (words.FirstOrDefault()?.ToLowerInvariant())
        {
            case null or "list":
                if (servers.Count == 0) { Term.Line(Term.Paint(Term.Dim, @"  No MCP servers. Add one with /mcp add NAME http://127.0.0.1:PORT/mcp, or /mcp add NAME C:\full\path\to\program.exe [arguments].")); return; }
                Term.Line();
                foreach (var server in servers)
                {
                    Term.Line("  " + Term.Paint(server.Enabled ? Term.Accent : Term.Dim, server.Name.PadRight(16)) + " " + Term.Clip(server.Executable ?? server.Endpoint, Math.Max(20, Term.Width - 44)) + Term.Paint(Term.Dim, server.Enabled ? "" : "  off"));
                    Term.Line(Term.Paint(Term.Dim, "    " + (server.Tools.Length == 0 ? "no tools allowed yet" : "may call: " + string.Join(", ", server.Tools))));
                }
                Term.Line(Term.Paint(Term.Dim, "  Every call asks first unless a saved rule allows it. /mcp tools NAME shows what a server offers."));
                return;
            case "add":
            {
                if (words.Length < 3) throw new ArgumentException(@"Use /mcp add NAME http://127.0.0.1:PORT/mcp, or /mcp add NAME C:\full\path\to\program.exe [arguments].");
                if (servers.Any(s => s.Name.Equals(words[1], StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("An MCP server named " + words[1] + " is already registered.");
                var server = Path.IsPathFullyQualified(words[2]) ? new McpRegistration(words[1], "", [], words[2], words[3..]) : new McpRegistration(words[1], words[2], []);
                CodeMcpRegistry.Validate(server);
                servers.Add(server); CodeMcpRegistry.Save(host, servers);
                Term.Line("  Registered " + Term.Paint(Term.Bold, server.Name) + Term.Paint(Term.Dim, "  ·  " + (server.Executable ?? server.Endpoint) + "  ·  no tools allowed yet"));
                await ShowOffered(server);
                return;
            }
            case "tools":
                await ShowOffered(servers[Index(words.ElementAtOrDefault(1))]);
                return;
            case "allow":
            {
                int at = Index(words.ElementAtOrDefault(1));
                var wanted = words.Skip(2).ToArray();
                if (wanted.Length == 0) throw new ArgumentException("Name the tools to allow, or use all (or none).");
                string[] tools;
                if (wanted is ["none"]) tools = [];
                else
                {
                    var offered = (await CodeMcpRegistry.Discover(servers[at] with { Enabled = true }, CancellationToken.None)).Select(t => t.Name).ToArray();
                    if (wanted is ["all"]) tools = offered;
                    else
                    {
                        if (wanted.FirstOrDefault(w => !offered.Contains(w)) is { } unknown) throw new ArgumentException(servers[at].Name + " does not offer a tool named " + unknown + ".");
                        tools = wanted;
                    }
                }
                servers[at] = servers[at] with { Tools = tools };
                CodeMcpRegistry.Save(host, servers);
                Term.Line("  " + servers[at].Name + " may call: " + (tools.Length == 0 ? Term.Paint(Term.Dim, "nothing") : string.Join(", ", tools)));
                return;
            }
            case "on" or "off":
            {
                int at = Index(words.ElementAtOrDefault(1));
                servers[at] = servers[at] with { Enabled = words[0].Equals("on", StringComparison.OrdinalIgnoreCase) };
                CodeMcpRegistry.Save(host, servers);
                Term.Line(Term.Paint(Term.Dim, "  " + servers[at].Name + " is " + (servers[at].Enabled ? "on" : "off") + "."));
                return;
            }
            case "remove":
            {
                int at = Index(words.ElementAtOrDefault(1)); var removed = servers[at];
                servers.RemoveAt(at); CodeMcpRegistry.Save(host, servers);
                Term.Line(Term.Paint(Term.Dim, "  Removed " + removed.Name + "."));
                return;
            }
            default:
                throw new ArgumentException("Use /mcp, /mcp add, /mcp tools, /mcp allow, /mcp on, /mcp off or /mcp remove.");
        }
    }
}
