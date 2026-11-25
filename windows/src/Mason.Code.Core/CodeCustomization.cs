using System.Text;
using System.Text.RegularExpressions;

namespace Mason.Code;

/// <summary>A Markdown definition with simple "key: value" front matter.</summary>
public sealed record CodeDefinition(string Name, string Description, string Body, string Source, IReadOnlyDictionary<string, string> Fields)
{
    public string Field(string key, string fallback = "") => Fields.TryGetValue(key, out var value) ? value : fallback;
    /// <summary>Agents: "build" or "ask". Commands: the agent they run with, if any.</summary>
    public string Mode => Field("mode", "build").Equals("ask", StringComparison.OrdinalIgnoreCase) || Field("mode").Equals("plan", StringComparison.OrdinalIgnoreCase) ? "ask" : "build";
}

/// <summary>
/// Custom agents and slash commands defined as Markdown, compatible with the layouts other harnesses use:
/// agents in .mason/agents, .agents/agents or .claude/agents; commands in .mason/commands, .claude/commands or
/// .opencode/command; plus the same folders under the Mason profile for every project. Project files win.
/// A command body may use $ARGUMENTS and $1–$9. An agent body becomes standing instructions for its session.
/// </summary>
public static partial class CodeCustomization
{
    public static readonly string[] Reserved = ["new", "queue", "compact", "undo", "redo", "help"];
    const int MaxBody = 8000, MaxFiles = 64;
    static readonly string[] AgentFolders = [".mason/agents", ".agents/agents", ".claude/agents"];
    static readonly string[] CommandFolders = [".mason/commands", ".claude/commands", ".opencode/command", ".opencode/commands"];

    public static IReadOnlyList<CodeDefinition> Agents(string settingsRoot, string workspace) => Load(settingsRoot, workspace, AgentFolders, "agents");
    public static IReadOnlyList<CodeDefinition> Commands(string settingsRoot, string workspace) => Load(settingsRoot, workspace, CommandFolders, "commands");

    static IReadOnlyList<CodeDefinition> Load(string settingsRoot, string workspace, string[] projectFolders, string profileFolder)
    {
        var found = new Dictionary<string, CodeDefinition>(StringComparer.OrdinalIgnoreCase);
        var roots = projectFolders.Select(f => Path.Combine(workspace, f)).Append(Path.Combine(settingsRoot, profileFolder));
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.md").Order(StringComparer.OrdinalIgnoreCase).Take(MaxFiles))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!NamePattern().IsMatch(name) || found.ContainsKey(name) || Reserved.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                if (info.Length > 64 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                found[name] = Parse(name, File.ReadAllText(file), Path.GetRelativePath(workspace, file).Replace('\\', '/'));
            }
        }
        return found.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static CodeDefinition Parse(string name, string text, string source)
    {
        text = text.ReplaceLineEndings("\n").TrimStart('﻿');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = text;
        if (text.StartsWith("---\n", StringComparison.Ordinal) && text.IndexOf("\n---", 4, StringComparison.Ordinal) is int end and > 0)
        {
            foreach (var line in text[4..end].Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || char.IsWhiteSpace(line[0])) continue;
                fields[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"', '\'');
            }
            body = text[(end + 4)..].TrimStart('\n');
        }
        body = body.Trim();
        if (body.Length > MaxBody) body = body[..MaxBody];
        var description = fields.GetValueOrDefault("description") ?? body.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim().TrimStart('#', ' ') ?? "";
        return new(fields.GetValueOrDefault("name") is { Length: > 0 } n && NamePattern().IsMatch(n) ? n : name, description.Length > 160 ? description[..160] + "…" : description, body, source, fields);
    }

    /// <summary>Expands "/name arguments" when a custom command has that name; otherwise returns null.</summary>
    public static string? Expand(IReadOnlyList<CodeDefinition> commands, string input)
    {
        var match = InvocationPattern().Match(input.TrimStart());
        if (!match.Success) return null;
        var command = commands.FirstOrDefault(c => c.Name.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        if (command == null) return null;
        var arguments = match.Groups[2].Value.Trim();
        var words = Words(arguments).ToArray();
        var expanded = command.Body.Replace("$ARGUMENTS", arguments, StringComparison.Ordinal);
        for (int i = 9; i >= 1; i--) expanded = expanded.Replace("$" + i, i <= words.Length ? words[i - 1] : "", StringComparison.Ordinal);
        if (!command.Body.Contains('$') && arguments.Length > 0) expanded += "\n\n" + arguments;
        return expanded.Trim();
    }

    /// <summary>The request sent to the model: a custom command expanded, framed by the session agent's instructions.</summary>
    public static string Prepare(string settingsRoot, CodeSession session, string input)
    {
        var request = Expand(Commands(settingsRoot, session.Workspace), input) ?? input;
        if (session.Agent is { Length: > 0 } name && Agents(settingsRoot, session.Workspace).FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } agent)
            request = "Standing instructions from the selected agent \"" + agent.Name + "\" (" + agent.Source + "):\n" + agent.Body + "\n\nRequest:\n" + request;
        return request;
    }

    static IEnumerable<string> Words(string text)
    {
        var current = new StringBuilder(); char? quote = null;
        foreach (var c in text)
        {
            if (quote == null && c is '"' or '\'') { quote = c; continue; }
            if (c == quote) { quote = null; continue; }
            if (quote == null && char.IsWhiteSpace(c)) { if (current.Length > 0) { yield return current.ToString(); current.Clear(); } continue; }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,47}$")] private static partial Regex NamePattern();
    [GeneratedRegex(@"^/([A-Za-z0-9][A-Za-z0-9_.-]{0,47})(?:\s+([\s\S]*))?$")] private static partial Regex InvocationPattern();
}
