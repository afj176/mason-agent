using System.Text;
using System.Text.RegularExpressions;

namespace Mason.Code;

/// <summary>
/// Locates unittest methods in a Python test file and pins the file so that the only change allowed after planning is
/// adding tests. Running one named test never executes the bodies of other test methods, so new test methods (and new
/// test classes holding only test methods) cannot change its outcome; any other edit, including a class that shadows the
/// original, a module-level patch, a changed import, setUp or comment, voids the pin. Before this, the whole file was
/// pinned, so adding a test the request asked for made every check on that file unverifiable.
/// </summary>
internal static partial class CodePythonTests
{
    public sealed record Located(string Class, string Source);

    /// <summary>Every class in <paramref name="content"/> that defines <paramref name="method"/>.</summary>
    public static IReadOnlyList<Located> Find(string content, string method) =>
        Blocks(content).Where(b => b.Method == method && b.Class != null).Select(b => new Located(b.Class!, b.Source)).ToList();

    /// <summary>Class and test-method names in the file, recorded at planning.</summary>
    public static string[] Names(string content) => Blocks(content).Select(b => b.Method ?? b.Class!).Concat(Classes(content)).Distinct().Order(StringComparer.Ordinal).ToArray();

    /// <summary>Hash of the file with tests added since planning removed: equal to the planning hash exactly when the
    /// only edits were added test methods or added test-only classes.</summary>
    public static string Structure(string content, IReadOnlyCollection<string> originalNames)
    {
        var lines = Lines(content);
        var keep = new bool[lines.Length]; Array.Fill(keep, true);
        foreach (var block in Blocks(content).Where(b => b.Method != null && !originalNames.Contains(b.Method)))
            for (int i = block.Start; i < block.End; i++) keep[i] = false;
        // A new class is removable only when nothing but added tests, comments and a docstring remains in it.
        foreach (var (name, start, end) in ClassSpans(lines).Where(c => !originalNames.Contains(c.Name)))
            if (Enumerable.Range(start + 1, end - start - 1).All(i => !keep[i] || Trivial(lines[i])))
                for (int i = start; i < end; i++) keep[i] = false;
        return CodeProtocol.Hash(Encoding.UTF8.GetBytes(string.Join("\n", lines.Where((l, i) => keep[i] && l.Trim().Length > 0))));
    }

    /// <summary>Up to 200 existing tests across up to 20 test modules, as "path · Class · test_name".</summary>
    public static string[] Inventory(CodeWorkspace scope, CodeFiles files)
    {
        var tests = new List<string>();
        foreach (var path in files.Files.Where(p => Regex.IsMatch(p, @"^(?:[A-Za-z_]\w*/)*test\w*\.py$")).Take(20))
        {
            try { tests.AddRange(Blocks(scope.Read(path).Content).Where(b => b.Class != null).Select(b => $"{path} · {b.Class} · {b.Method}")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
            if (tests.Count >= 200) break;
        }
        return tests.Take(200).ToArray();
    }

    public static string Hash(string source) => CodeProtocol.Hash(Encoding.UTF8.GetBytes(source));

    /// <summary>TestHashes keys: the file structure pin and its recorded names (older plans keyed the whole file by path).</summary>
    public static string StructureKey(string path) => path + "::structure";
    public static string NamesKey(string path) => path + "::names";

    /// <summary>The request asks the agent to write a test, so a check may name a test that does not exist yet.</summary>
    public static bool RequestsNewTest(string request) => NewTestRequest().IsMatch(request);

    sealed record Block(string? Class, string? Method, int Start, int End, string Source);

    static string[] Lines(string content) => content.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToArray();
    static int Indent(string line) => line.Length - line.TrimStart().Length;
    static bool Trivial(string line) { var t = line.Trim(); return t.Length == 0 || t.StartsWith('#') || t.StartsWith("\"\"\"") || t.StartsWith("'''"); }

    static IEnumerable<string> Classes(string content) => ClassSpans(Lines(content)).Select(c => c.Name);

    static IEnumerable<(string Name, int Start, int End)> ClassSpans(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (ClassLine().Match(lines[i]) is not { Success: true } m) continue;
            int indent = Indent(lines[i]), end = i + 1;
            while (end < lines.Length && (lines[end].Trim().Length == 0 || Indent(lines[end]) > indent)) end++;
            yield return (m.Groups[2].Value, i, end);
        }
    }

    static List<Block> Blocks(string content)
    {
        var lines = Lines(content); var blocks = new List<Block>();
        var classes = ClassSpans(lines).ToList();
        for (int i = 0; i < lines.Length; i++)
        {
            if (DefLine().Match(lines[i]) is not { Success: true } def) continue;
            int indent = def.Groups[1].Length, end = i + 1;
            while (end < lines.Length && (lines[end].Trim().Length == 0 || Indent(lines[end]) > indent)) end++;
            int last = end; while (last > i + 1 && lines[last - 1].Trim().Length == 0) last--;
            var owner = classes.Where(c => c.Start < i && i < c.End && Indent(lines[c.Start]) < indent).OrderByDescending(c => c.Start).Select(c => c.Name).FirstOrDefault();
            blocks.Add(new(owner, def.Groups[2].Value, i, last, string.Join("\n", lines[i..last])));
        }
        return blocks;
    }

    [GeneratedRegex(@"^(\s*)class\s+([A-Za-z_]\w*)\s*[(:]")] private static partial Regex ClassLine();
    [GeneratedRegex(@"^(\s*)(?:async\s+)?def\s+(test_\w+)\s*\(")] private static partial Regex DefLine();
    [GeneratedRegex(@"(?i)\b(?:add|write|create|include)\b[^.\n]{0,60}\btests?\b|\bregression tests?\b")] private static partial Regex NewTestRequest();
}
