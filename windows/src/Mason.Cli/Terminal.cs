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
    /// <summary>The console understands escape sequences (cursor movement), whether or not NO_COLOR turned colour off.</summary>
    public static bool Ansi { get; private set; }
    public static bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public static int Width { get { try { return Math.Clamp(Console.WindowWidth, 40, 200); } catch (IOException) { return 100; } } }

    public static void Start()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (!Console.IsInputRedirected) Console.InputEncoding = Encoding.UTF8;
        Ansi = !Console.IsOutputRedirected;
        if (Ansi && OperatingSystem.IsWindows())
        {
            // Older console hosts only interpret escape sequences after this mode is set.
            var handle = GetStdHandle(-11);
            Ansi = GetConsoleMode(handle, out var mode) && (((mode & 4) != 0) || SetConsoleMode(handle, mode | 4));
        }
        Styled = Ansi && Environment.GetEnvironmentVariable("NO_COLOR") is null or "";
        Mode = Mason.Code.ThemeFamilies.SavedMode(global::Interactive.DataHome());
        UseTheme(Mason.Code.ThemeFamilies.Saved(global::Interactive.DataHome()));
    }

    /// <summary>Light, Dark or Default (follow the terminal), shared with Mason Agent's Appearance mode.</summary>
    public static string Mode { get; set; } = "Default";

    /// <summary>The theme family chosen in Mason Agent's Appearance (appearance.json), used for the accent colour.</summary>
    public static Mason.Code.ThemeFamily Theme { get; private set; } = Mason.Code.ThemeFamilies.Default;

    /// <summary>
    /// A light terminal background gets the family's light-mode accent. COLORFGBG ("15;0") is the only widely set hint;
    /// without it the terminal is taken to be dark, as most are.
    /// </summary>
    static bool LightBackground => Environment.GetEnvironmentVariable("COLORFGBG") is { } colors
        && int.TryParse(colors.Split(';')[^1], out var background) && background is 7 or 15;

    public static void UseTheme(Mason.Code.ThemeFamily family)
    {
        Theme = family;
        var tokens = family.For(dark: Mode switch { "Light" => false, "Dark" => true, _ => !LightBackground });
        static string Rgb(uint c) => $"{(c >> 16) & 0xFF};{(c >> 8) & 0xFF};{c & 0xFF}";
        Accent = $"\x1b[38;2;{Rgb(tokens.Accent)}m";
        Selected = $"\x1b[48;2;{Rgb(tokens.Accent)}m\x1b[38;2;{Rgb(tokens.OnAccent)}m\x1b[1m";
    }

    public const string Reset = "\x1b[0m", Bold = "\x1b[1m", Dim = "\x1b[2m", Italic = "\x1b[3m";
    public static string Accent { get; private set; } = "\x1b[38;2;180;184;255m";
    public static string Selected { get; private set; } = "\x1b[48;2;180;184;255m\x1b[38;2;21;27;48m\x1b[1m";
    public const string Green = "\x1b[38;2;120;200;140m", Red = "\x1b[38;2;240;120;120m", Yellow = "\x1b[38;2;230;190;110m", Cyan = "\x1b[38;2;130;190;220m";
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
        if (!Ansi) return;
        lock (Gate)
        {
            text = Clip(text.ReplaceLineEndings(" "), Width - 1);
            if (text == status) return;
            Erase(); status = text; Draw();
        }
    }
    /// <summary>Draws a block of lines, replacing the block of <paramref name="previous"/> lines drawn just above. Returns its height. Needs <see cref="Ansi"/>.</summary>
    public static int Block(IReadOnlyList<string> lines, int previous)
    {
        lock (Gate)
        {
            Erase();
            var output = Console.Out;
            if (previous > 0) output.Write($"\x1b[{previous}A");
            foreach (var line in lines) { output.Write("\r\x1b[2K"); output.WriteLine(line); }
            for (int i = lines.Count; i < previous; i++) output.Write("\r\x1b[2K\n");
            if (previous > lines.Count) output.Write($"\x1b[{previous - lines.Count}A");
            output.Flush();
            Draw();
            return lines.Count;
        }
    }

    /// <summary>
    /// The app icon drawn with upper half blocks (two pixel rows per line) in 24-bit colour, one string per line.
    /// Empty when colour is off or the terminal is narrower than 80 columns.
    /// </summary>
    public static string[] Picture()
    {
        if (!Styled || Width < 80) return [];
        var lines = new string[PixelBear.Size / 2];
        for (int row = 0; row < lines.Length; row++)
        {
            var line = new StringBuilder();
            string top = PixelBear.Pixels[row * 2], bottom = PixelBear.Pixels[row * 2 + 1];
            for (int x = 0; x < PixelBear.Size; x++)
            {
                string upper = top.Substring(x * 6, 6), lower = bottom.Substring(x * 6, 6);
                static string Rgb(string hex) => $"{Convert.ToInt32(hex[..2], 16)};{Convert.ToInt32(hex[2..4], 16)};{Convert.ToInt32(hex[4..], 16)}";
                if (upper == "......" && lower == "......") line.Append(Reset).Append(' ');
                else if (lower == "......") line.Append(Reset).Append($"\x1b[38;2;{Rgb(upper)}m▀");
                else if (upper == "......") line.Append(Reset).Append($"\x1b[38;2;{Rgb(lower)}m▄");
                else line.Append($"\x1b[38;2;{Rgb(upper)}m\x1b[48;2;{Rgb(lower)}m▀");
            }
            lines[row] = line.Append(Reset).ToString();
        }
        return lines;
    }
    static void Erase() { if (status.Length > 0) Console.Out.Write("\r\x1b[2K"); }
    static void Draw() { if (status.Length > 0) { Console.Out.Write(Styled ? Dim + status + Reset : status); Console.Out.Flush(); } }

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
