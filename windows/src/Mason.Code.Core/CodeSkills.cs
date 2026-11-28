using System.Text.RegularExpressions;

namespace Mason.Code;

/// <summary>One skill: a SKILL.md the agent reads before work it covers. <see cref="ShadowedBy"/> names the skill
/// that hides this one when two share a name; <see cref="Path"/> is null for a skill that lives in a bundle.</summary>
public sealed record CodeSkill(string Name, string Description, string Body, string Source, string? Path, string? ShadowedBy, bool Enabled)
{
    public bool Hidden => ShadowedBy != null;
    /// <summary>on, off or hidden, as the command line prints it.</summary>
    public string State => Hidden ? "hidden" : Enabled ? "on" : "off";
    /// <summary>"No description" when the front matter gives none.</summary>
    public string Summary => Description.Length == 0 ? "No description" : Description;
}

/// <summary>
/// Skills come from the project's .mason/skills, .claude/skills and .agents/skills, in that order, then from enabled
/// bundles (named bundle/key). Each is NAME/SKILL.md with name and description front matter. When two share a name the
/// first wins and the others are kept as hidden. The project policy's disabledSkills switches a skill off for one project.
/// Chat, which has no project, reads bundle skills and the computer's own DATA_HOME/skills.
/// </summary>
public static partial class CodeSkills
{
    public static readonly string[] Folders = [".mason/skills", ".claude/skills", ".agents/skills"];
    public const int MaxPerFolder = 64, MaxFileBytes = 256 * 1024, MaxBody = 8000;
    public const string PromptRule = "Call read_skill with the name before doing work a skill covers.";
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,47}\z")] private static partial Regex NamePattern();
    public static bool ValidName(string name) => NamePattern().IsMatch(name) && name is not ("." or "..");

    static string Short(string folder) => folder switch { ".mason/skills" => "project", ".claude/skills" => ".claude", _ => ".agents" };

    /// <summary>Every skill for a project, hidden ones included, in precedence order.</summary>
    public static IReadOnlyList<CodeSkill> List(string? settingsRoot, string workspace)
    {
        var disabled = new HashSet<string>(settingsRoot == null ? [] : CodeProjectPolicy.Load(settingsRoot, workspace, true)?.DisabledSkills ?? [], StringComparer.Ordinal);
        var found = new List<CodeSkill>();
        var winners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Folders)
            foreach (var (name, path, text) in Scan(workspace, folder))
            {
                var parsed = CodeCustomization.Parse(name, text, path);
                string? shadow = winners.TryGetValue(name, out var winner) ? winner : null;
                if (shadow == null) winners[name] = path;
                found.Add(new(name, Describe(parsed), Clip(parsed.Body), Short(folder), path, shadow, !disabled.Contains(name)));
            }
        if (settingsRoot != null) foreach (var skill in BundleSkills(settingsRoot)) found.Add(skill with { Enabled = !disabled.Contains(skill.Name) });
        return found;
    }

    /// <summary>The skills the agent may read in this project: not hidden and not switched off.</summary>
    public static IReadOnlyList<CodeSkill> Active(string? settingsRoot, string workspace) => List(settingsRoot, workspace).Where(s => !s.Hidden && s.Enabled).ToArray();

    /// <summary>Skills for Chat: enabled bundles, then the computer's DATA_HOME/skills.</summary>
    public static IReadOnlyList<CodeSkill> Computer(string settingsRoot)
    {
        var found = BundleSkills(settingsRoot).ToList();
        foreach (var (name, path, text) in Scan(settingsRoot, "skills"))
        {
            if (found.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var parsed = CodeCustomization.Parse(name, text, path);
            found.Add(new(name, Describe(parsed), Clip(parsed.Body), "computer", path, null, true));
        }
        return found;
    }

    /// <summary>The system prompt lines for these skills, or "" when there are none.</summary>
    public static string Prompt(IEnumerable<CodeSkill> skills)
    {
        var lines = skills.Select(s => "- " + s.Name + ": " + s.Summary).ToArray();
        return lines.Length == 0 ? "" : "Skills:\n" + string.Join("\n", lines) + "\n" + PromptRule;
    }

    /// <summary>What read_skill returns, or the reason it refuses.</summary>
    public static string Read(IReadOnlyList<CodeSkill> all, string name)
    {
        var skill = all.FirstOrDefault(s => !s.Hidden && s.Name.Equals(name, StringComparison.Ordinal)) ?? all.FirstOrDefault(s => !s.Hidden && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("There is no skill named " + name + ". The skills are: " + string.Join(", ", all.Where(s => !s.Hidden && s.Enabled).Select(s => s.Name)) + ".");
        if (!skill.Enabled) throw new UnauthorizedAccessException("The skill " + skill.Name + " is switched off for this project.");
        return skill.Body;
    }

    /// <summary>Writes .mason/skills/NAME/SKILL.md from the template and returns its full path.</summary>
    public static string Create(string workspace, string name, string description)
    {
        if (!ValidName(name)) throw new ArgumentException("Use letters, numbers, dots, dashes or underscores for a skill name, starting with a letter or number (up to 48).");
        var folder = System.IO.Path.Combine(workspace, ".mason", "skills", name);
        var file = System.IO.Path.Combine(folder, "SKILL.md");
        if (Directory.Exists(folder) || File.Exists(file)) throw new IOException("A skill named " + name + " already exists in .mason/skills.");
        description = description.ReplaceLineEndings(" ").Trim();
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, Template(name, description));
        return file;
    }

    public static string Template(string name, string description) =>
        "---\nname: " + name + "\ndescription: " + description + "\n---\n\n# " + name + "\n\n## When to use\n\n## Steps\n\n";

    /// <summary>Switches a skill on or off for one project through the project policy's disabledSkills.</summary>
    public static void Switch(string settingsRoot, string workspace, string name, bool on)
    {
        var all = List(settingsRoot, workspace);
        var skill = all.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("There is no skill named " + name + ". /skills lists them.");
        CodeProjectPolicy.Edit(settingsRoot, workspace, policy =>
        {
            var off = new SortedSet<string>(policy.DisabledSkills ?? [], StringComparer.Ordinal);
            if (on) off.Remove(skill.Name); else off.Add(skill.Name);
            return policy with { DisabledSkills = off.ToArray() };
        });
    }

    static string Describe(CodeDefinition parsed)
    {
        var text = parsed.Field("description").ReplaceLineEndings(" ").Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }
    static string Clip(string body) => body.Length > MaxBody ? body[..MaxBody] : body;

    /// <summary>The NAME/SKILL.md files in one folder under root. A link that leads outside root is refused.</summary>
    static IEnumerable<(string Name, string Path, string Text)> Scan(string root, string relative)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!Directory.Exists(full) || !Inside(root, full)) yield break;
        string[] folders;
        try { folders = Directory.GetDirectories(full).Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        int count = 0;
        foreach (var folder in folders)
        {
            var name = System.IO.Path.GetFileName(folder);
            if (!ValidName(name)) continue;
            var file = System.IO.Path.Combine(folder, "SKILL.md");
            if (!File.Exists(file) || !Inside(root, folder) || !Inside(root, file)) continue;
            if (++count > MaxPerFolder) yield break;
            string text;
            try
            {
                if (new FileInfo(file).Length > MaxFileBytes) continue;
                text = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            yield return (name, relative.Replace('\\', '/') + "/" + name + "/SKILL.md", text);
        }
    }

    /// <summary>True when path, with any link along it followed, stays under root.</summary>
    static bool Inside(string root, string path)
    {
        var top = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        var current = System.IO.Path.GetFullPath(path);
        for (var probe = current; probe != null && probe.Length > top.Length; probe = System.IO.Path.GetDirectoryName(probe))
        {
            FileSystemInfo info = Directory.Exists(probe) ? new DirectoryInfo(probe) : new FileInfo(probe);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0) continue;
            var target = info.ResolveLinkTarget(true);
            if (target == null) return false;
            var resolved = System.IO.Path.GetFullPath(target.FullName);
            if (!resolved.StartsWith(top + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return current.StartsWith(top + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static IEnumerable<CodeSkill> BundleSkills(string settingsRoot)
    {
        IEnumerable<HarnessBundle> bundles;
        try { bundles = CodeBundles.Enabled(settingsRoot).ToArray(); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { yield break; }
        foreach (var bundle in bundles)
            foreach (var (key, text) in bundle.Skills.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var name = bundle.Name + "/" + key;
                var parsed = CodeCustomization.Parse(key, text, name);
                yield return new(name, Describe(parsed), Clip(parsed.Body), "bundle " + bundle.Name, null, null, true);
            }
    }
}
