using System.Text.RegularExpressions;

namespace Mason.Code;

/// <summary>
/// Recognises a command that failed because its own text could not be parsed: an inline <c>python -c</c> snippet with a
/// syntax error, or a shell or PowerShell parse error. Such a failure is not evidence about the project, so it does not
/// count as an outstanding failed check once the task otherwise verifies.
/// </summary>
public static partial class CodeCommandProbe
{
    public static bool Malformed(string result) => !string.IsNullOrEmpty(result) && Parse().IsMatch(result);

    // Receipts are JSON, so the quotes around <string> arrive as \u0022 or \" text, or as plain quotes in raw output.
    [GeneratedRegex(@"File (?:\\u0022|\\?"")<string>(?:\\u0022|\\?""), line \d+[\s\S]{0,400}?(?:SyntaxError|IndentationError|TabError)\b|syntax error near unexpected token|unexpected EOF while looking for matching|\bParserError\b|The string is missing the terminator")]
    private static partial Regex Parse();
}
