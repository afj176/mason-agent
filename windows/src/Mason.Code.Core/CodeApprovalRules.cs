using System.Text;
using System.Text.Json;

namespace Mason.Code;

/// <summary>A saved answer to an approval: this tool, for subjects starting with <see cref="Subject"/>, in one project.</summary>
public sealed record CodeApprovalRule(string Tool, string Subject, bool Allow, DateTimeOffset Created)
{
    public string Label => (Allow ? "Always allow " : "Always deny ") + Tool.Replace('_', ' ') + (Subject.Length == 0 ? " (any)" : " · " + Subject);
}

/// <summary>
/// Per-project approval rules stored in the Mason profile, outside the project. Rules are matched by tool and subject
/// prefix: the command line for commands, the relative path for edits, the server and tool for MCP. A deny rule
/// always wins over an allow rule. Rules never widen the sandbox; they only skip or refuse the prompt.
/// </summary>
public static class CodeApprovalRules
{
    const int MaxRules = 200;
    static string Key(string workspace) => CodeProtocol.Hash(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)).ToLowerInvariant()));
    static string Location(string host, string workspace) => Path.Combine(host, "approval-rules", Key(workspace) + ".json");

    public static IReadOnlyList<CodeApprovalRule> Load(string host, string workspace)
    {
        var path = Location(host, workspace);
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<CodeApprovalRule>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { return []; }
    }

    public static void Save(string host, string workspace, IEnumerable<CodeApprovalRule> rules)
    {
        var list = rules.ToList();
        if (list.Count > MaxRules) throw new InvalidOperationException("A project can keep up to " + MaxRules + " approval rules.");
        if (list.Any(r => string.IsNullOrWhiteSpace(r.Tool) || r.Tool.Length > 64 || r.Subject.Length > 2000)) throw new ArgumentException("Invalid approval rule.");
        var path = Location(host, workspace);
        if (CodeSandbox.ContainsHost(workspace, path)) throw new InvalidOperationException("Store approval rules outside the project.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list));
        File.Move(temp, path, true);
    }

    public static void Add(string host, string workspace, CodeApprovalRule rule) =>
        Save(host, workspace, Load(host, workspace).Where(r => !(r.Tool == rule.Tool && r.Subject == rule.Subject)).Append(rule));

    public static void Remove(string host, string workspace, CodeApprovalRule rule) =>
        Save(host, workspace, Load(host, workspace).Where(r => !(r.Tool == rule.Tool && r.Subject == rule.Subject && r.Allow == rule.Allow)));

    /// <summary>The rule that decides this request, deny first, or null when the user must be asked.</summary>
    public static CodeApprovalRule? Match(IReadOnlyList<CodeApprovalRule> rules, string tool, string subject)
    {
        bool Applies(CodeApprovalRule r) => (r.Tool == tool || r.Tool == "*") && subject.StartsWith(r.Subject, StringComparison.OrdinalIgnoreCase);
        return rules.FirstOrDefault(r => !r.Allow && Applies(r)) ?? rules.FirstOrDefault(r => r.Allow && Applies(r));
    }

    /// <summary>The broadest sensible subject to offer for "always allow": the program and its first argument for a
    /// command ("git status", "npm test"), the folder for an edit, the server and tool for MCP.</summary>
    public static string Suggest(string tool, string subject)
    {
        if (tool is "run_command" or "run_shell")
        {
            var words = Tokens(subject).Take(2).ToArray();
            return words.Length == 0 ? subject : string.Join(" ", words);
        }
        if (tool is "edit_file" or "apply_edits" or "replace_file" or "create_file" or "create_directory")
        {
            var slash = subject.Replace('\\', '/').LastIndexOf('/');
            return slash > 0 ? subject[..(slash + 1)] : "";
        }
        return subject;
    }

    static IEnumerable<string> Tokens(string commandLine)
    {
        var current = new StringBuilder(); bool quoted = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; current.Append(c); continue; }
            if (char.IsWhiteSpace(c) && !quoted) { if (current.Length > 0) { yield return current.ToString(); current.Clear(); } continue; }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
