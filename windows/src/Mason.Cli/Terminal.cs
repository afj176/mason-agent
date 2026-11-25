using System.Runtime.InteropServices;
using System.Text;

// This is the Windows CLI: it uses the Windows engine, sandbox and data protection throughout.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

/// <summary>Styled, thread-safe terminal output with one transient status line. Falls back to plain text when output is redirected or NO_COLOR is set.</summary>
internal static class Term
{
    static readonly object Gate = new();
    static string status = "";
    public static bool Styled { get; private set; }
    public static bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public static int Width { get { try { return Math.Clamp(Console.WindowWidth, 40, 200); } catch (IOException) { return 100; } } }

    public static void Start()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (!Console.IsInputRedirected) Console.InputEncoding = Encoding.UTF8;
        Styled = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null or "";
        if (Styled && OperatingSystem.IsWindows())
        {
            // Older console hosts only interpret escape sequences after this mode is set.
            var handle = GetStdHandle(-11);
            Styled = GetConsoleMode(handle, out var mode) && (((mode & 4) != 0) || SetConsoleMode(handle, mode | 4));
        }
    }

    public const string Reset = "\x1b[0m", Bold = "\x1b[1m", Dim = "\x1b[2m", Italic = "\x1b[3m";
    public const string Accent = "\x1b[38;2;195;194;255m", Green = "\x1b[38;2;120;200;140m", Red = "\x1b[38;2;240;120;120m", Yellow = "\x1b[38;2;230;190;110m", Cyan = "\x1b[38;2;130;190;220m";
    public static string Paint(string style, string text) => Styled && text.Length > 0 ? style + text + Reset : text;

    /// <summary>Writes complete lines above the status line.</summary>
    public static void Line(string text = "")
    {
        lock (Gate)
        {
            Erase();
            Console.Out.WriteLine(text);
            Draw();
        }
    }
    /// <summary>Writes text without a newline; the status line must be cleared by the caller's flow (prompts).</summary>
    public static void Write(string text)
    {
        lock (Gate) { Erase(); status = ""; Console.Out.Write(text); Console.Out.Flush(); }
    }
    /// <summary>Replaces the transient status line. An empty value removes it. Ignored when output is not a terminal.</summary>
    public static void Status(string text)
    {
        if (!Styled) return;
        lock (Gate)
        {
            text = Clip(text.ReplaceLineEndings(" "), Width - 1);
            if (text == status) return;
            Erase(); status = text; Draw();
        }
    }
    static void Erase() { if (status.Length > 0) Console.Out.Write("\r\x1b[2K"); }
    static void Draw() { if (status.Length > 0) { Console.Out.Write(Dim + status + Reset); Console.Out.Flush(); } }

    /// <summary>Clips to a display width, counting each UTF-16 unit as one column (sufficient for status and summaries).</summary>
    public static string Clip(string text, int width) => width < 2 || text.Length <= width ? text : text[..(width - 1)] + "…";

    /// <summary>Renders Markdown for a terminal: fenced code, headings, bullets, bold and inline code. Unknown syntax is left as written.</summary>
    public static IEnumerable<string> Markdown(string text)
    {
        bool code = false;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                code = !code;
                var language = line.TrimStart()[3..].Trim();
                yield return Paint(Dim, code ? "  ┌─ " + language : "  └─");
                continue;
            }
            if (code) { yield return Paint(Dim, "  │ ") + Paint(Cyan, line); continue; }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                yield return Paint(Bold + Accent, trimmed.TrimStart('#').Trim());
                continue;
            }
            string indent = line[..(line.Length - trimmed.Length)];
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                yield return indent + Paint(Accent, "•") + " " + Inline(trimmed[2..]);
                continue;
            }
            yield return indent + Inline(trimmed);
        }
    }
    static string Inline(string text)
    {
        if (!Styled) return text;
        var result = new StringBuilder(); int at = 0;
        while (at < text.Length)
        {
            if (text[at] == '`' && text.IndexOf('`', at + 1) is > 0 and var close)
            {
                result.Append(Cyan).Append(text, at + 1, close - at - 1).Append(Reset); at = close + 1; continue;
            }
            if (at + 1 < text.Length && text[at] == '*' && text[at + 1] == '*' && text.IndexOf("**", at + 2, StringComparison.Ordinal) is > 0 and var end)
            {
                result.Append(Bold).Append(text, at + 2, end - at - 2).Append(Reset); at = end + 2; continue;
            }
            result.Append(text[at++]);
        }
        return result.ToString();
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
