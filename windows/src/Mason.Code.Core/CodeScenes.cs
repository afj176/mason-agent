using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason.Code;

/// <summary>What kind of work a run of tool calls was, which picks how the app's Live Viewer shows it.</summary>
public enum CodeSceneKind { Read, Edit, Command, Page, Other }

/// <summary>Consecutive tool calls of one kind, shown together as one step of a run.</summary>
public sealed record CodeScene(CodeSceneKind Kind, int First, int Count)
{
    public int Last => First + Count - 1;
    public bool Contains(int index) => index >= First && index <= Last;
}

/// <summary>A command's result, read for the summary first: exit code, test counts and the lines that name failures.</summary>
public sealed record CodeCommandSummary(int? ExitCode, int? Passed, int? Failed, string[] Failures, string Output, bool Truncated);

public static partial class CodeScenes
{
    /// <summary>The longest run of calls kept in one scene; longer runs start a new scene so none grows without bound.</summary>
    public const int MaxSceneLength = 40;

    public static CodeSceneKind KindOf(string tool) => tool switch
    {
        "read_file" or "open_file" or "read_lines" or "list_files" or "search_text" or "search_next" or "semantic_search" or "repository_map"
            or "inspect_symbol" or "navigate_code" or "read_documentation" or "read_skill" or "git_status" or "mcp_read" or "mcp_resources" => CodeSceneKind.Read,
        "edit_file" or "apply_edits" or "replace_file" or "create_file" or "create_directory" => CodeSceneKind.Edit,
        "run_command" or "run_shell" or "start_job" or "poll_job" or "stop_job" or "start_terminal" or "terminal_input" or "terminal_status"
            or "terminal_stop" or "run_workflow" or "verify_acceptance" => CodeSceneKind.Command,
        "open_preview" or "browser_load" or "browser_text" or "browser_layout" or "browser_heading" or "browser_flow" or "browser_click"
            or "desktop_visual" or "desktop_observe" or "desktop_act" or "computer_use" => CodeSceneKind.Page,
        _ => CodeSceneKind.Other
    };

    /// <summary>Groups consecutive calls of the same kind. Every call belongs to exactly one scene, in order.</summary>
    public static IReadOnlyList<CodeScene> Group(IReadOnlyList<CodeActivity> activities)
    {
        var scenes = new List<CodeScene>();
        for (int i = 0; i < activities.Count; i++)
        {
            var kind = KindOf(activities[i].Tool);
            if (scenes.Count > 0 && scenes[^1] is var last && last.Kind == kind && last.Count < MaxSceneLength) scenes[^1] = last with { Count = last.Count + 1 };
            else scenes.Add(new(kind, i, 1));
        }
        return scenes;
    }

    /// <summary>The workspace path a call worked on, normalised to forward slashes, or null.</summary>
    public static string? PathOf(CodeActivity activity) => Argument(activity, "path") is { Length: > 0 } path ? Normalize(path) : null;
    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/') is { Length: > 0 } trimmed ? trimmed : path.Replace('\\', '/');

    /// <summary>A string argument of the call, or null when the arguments do not have it.</summary>
    public static string? Argument(CodeActivity activity, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(activity.Arguments);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public static bool Failed(CodeActivity activity) => activity.State is "Failed" or "Denied" or "Outcome unknown";

    /// <summary>A short label for a scene, written the way a person would say it.</summary>
    public static string Label(CodeScene scene, IReadOnlyList<CodeActivity> activities)
    {
        var items = Enumerable.Range(scene.First, scene.Count).Select(i => activities[i]).ToArray();
        int Distinct(IEnumerable<CodeActivity> calls) => calls.Select((a, i) => PathOf(a)?.ToLowerInvariant() ?? "\0" + i).Distinct().Count();
        string Plural(int n, string one, string many) => n + " " + (n == 1 ? one : many);
        switch (scene.Kind)
        {
            case CodeSceneKind.Read:
            {
                int searches = items.Count(a => a.Tool is "search_text" or "search_next" or "semantic_search" or "list_files");
                int reads = Distinct(items.Where(a => a.Tool is not ("search_text" or "search_next" or "semantic_search" or "list_files")));
                var parts = new List<string>();
                if (reads > 0) parts.Add("Read " + Plural(reads, "file", "files"));
                if (searches > 0) parts.Add((parts.Count == 0 ? "Searched " : "searched ") + Plural(searches, "time", "times"));
                return string.Join(", ", parts);
            }
            case CodeSceneKind.Edit:
                return items.Length == 1 && PathOf(items[0]) is { } one ? "Edited " + Path.GetFileName(one) : "Edited " + Plural(Distinct(items), "file", "files");
            case CodeSceneKind.Command:
            {
                var command = CodeActivityText.Command(items[^1]);
                // Name the program, not the folder it was found in: C:\Python314\python.exe -m unittest → python -m unittest.
                if (CodeScenes.Argument(items[^1], "executable") is { Length: > 0 } program && command.StartsWith(program, StringComparison.OrdinalIgnoreCase))
                    command = Path.GetFileNameWithoutExtension(program) + command[program.Length..];
                else if (command.StartsWith('"') && command.IndexOf('"', 1) is > 1 and var close)
                    command = Path.GetFileNameWithoutExtension(command[1..close]) + command[(close + 1)..];
                if (command.Length > 48) command = command[..47] + "…";
                return items.Length == 1 ? "Ran " + command : $"Ran {items.Length} commands";
            }
            case CodeSceneKind.Page:
                return items.Length == 1 ? "Checked the page" : $"Checked the page {items.Length} times";
            default:
                return items.Length == 1 ? items[0].Tool.Replace('_', ' ') : $"{items.Length} other steps";
        }
    }

    /// <summary>Reads a command call's result for its exit code, test counts and the lines that name failures.</summary>
    public static CodeCommandSummary Command(CodeActivity activity)
    {
        int? exit = null; string output = activity.Result; bool truncated = false;
        try
        {
            using var json = JsonDocument.Parse(activity.Result);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("ExitCode", out var code) && code.ValueKind == JsonValueKind.Number) exit = code.GetInt32();
                if (root.TryGetProperty("Truncated", out var clipped) && clipped.ValueKind is JsonValueKind.True) truncated = true;
                output = CodeActivityText.Result(activity);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        int? passed = Count(output, PassedCount()), failed = Count(output, FailedCount());
        // Python's unittest prints "Ran 6 tests" then "OK" or "FAILED (failures=1, errors=1)".
        if (passed == null && Count(output, UnittestRan()) is { } ran)
        {
            var broken = UnittestFailed().Match(output);
            int bad = broken.Success ? broken.Groups["n"].Captures.Sum(c => int.Parse(c.Value)) : 0;
            passed = ran - bad; failed ??= bad > 0 ? bad : null;
        }
        var failures = output.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && FailureLine().IsMatch(line) && !SummaryLine().IsMatch(line))
            .Select(line => line.Length > 220 ? line[..219] + "…" : line)
            .Distinct().Take(8).ToArray();
        return new(exit, passed, failed, failures, output, truncated);
    }

    static int? Count(string output, Regex pattern)
    {
        int? best = null;
        foreach (Match match in pattern.Matches(output))
            if (int.TryParse(match.Groups["n"].Value, out var value)) best = value;
        return best;
    }

    /// <summary>How many calls of a scene touched each top folder, most first.</summary>
    public static (string Folder, int Count)[] Folders(IEnumerable<CodeActivity> activities) => activities
        .Select(PathOf).OfType<string>()
        .Select(p => p.Contains('/') ? p[..p.IndexOf('/')] + "/" : p)
        .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
        .Select(g => (g.Key, g.Count()))
        .OrderByDescending(g => g.Item2).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Index of the first call of the latest request, so a run's summary covers only what that request did.</summary>
    public static int RequestStart(CodeSession session)
    {
        int lastUser = session.Turns.FindLastIndex(t => t.Role == "user");
        var first = session.Turns.Skip(lastUser + 1).Where(t => t.Role == "assistant" && t.ActivityCount > 0).Select(t => t.FirstActivity).DefaultIfEmpty(-1).Min();
        return first is >= 0 && first < session.Activities.Count ? first : lastUser < 0 ? 0 : session.Activities.Count;
    }

    [GeneratedRegex(@"(?m)^Ran (?<n>\d+) tests? in ")] private static partial Regex UnittestRan();
    [GeneratedRegex(@"(?m)^FAILED \((?:(?:failures|errors)=(?<n>\d+)|[a-z ]+=\d+)(?:, (?:(?:failures|errors)=(?<n>\d+)|[a-z ]+=\d+))*\)")] private static partial Regex UnittestFailed();
    [GeneratedRegex(@"(?im)(?<n>\d+)\s+(?:tests?\s+)?passed|passed:?\s+(?<n>\d+)")] private static partial Regex PassedCount();
    [GeneratedRegex(@"(?im)(?<n>\d+)\s+(?:tests?\s+)?failed|failed:?\s+(?<n>\d+)")] private static partial Regex FailedCount();
    [GeneratedRegex(@"^(?:[✗✕×✘]|●\s|FAIL\b|FAILED\b|ERROR\b|error(?:\s+[A-Z]+\d+)?:|[\w./\\-]+\(\d+(?:,\d+)?\):\s*error|Error:|AssertionError|Traceback|npm ERR!)", RegexOptions.IgnoreCase)] private static partial Regex FailureLine();
    [GeneratedRegex(@"^(?:Tests?:|Test Suites:|Failed!\s+-|FAILED \(|=+.*(?:passed|failed))", RegexOptions.IgnoreCase)] private static partial Regex SummaryLine();
}
