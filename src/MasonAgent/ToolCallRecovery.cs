using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason;

/// <summary>
/// Recovers a tool call that a local model wrote as text instead of in its template's tool format, for example
/// Qwen2.5-Coder answering with &lt;tools&gt;{"name":…}&lt;/tools&gt; or a fenced JSON block. Only a single call to a
/// tool that was actually offered is accepted; anything ambiguous stays an ordinary text answer.
/// </summary>
public static partial class ToolCallRecovery
{
    public static AiToolCall? TryParse(string text, IReadOnlySet<string> tools)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200_000 || tools.Count == 0) return null;
        var found = new List<AiToolCall>();
        foreach (var candidate in Candidates(text))
        {
            if (Parse(candidate, tools) is { } call) found.Add(call);
            if (found.Count > 1) return null; // Parallel calls are not accepted by the sequential harness.
        }
        return found.SingleOrDefault();
    }

    public static IReadOnlySet<string> Names(JsonElement definitions) =>
        definitions.ValueKind != JsonValueKind.Array ? new HashSet<string>() :
        definitions.EnumerateArray().Select(d => d.TryGetProperty("function", out var f) && f.TryGetProperty("name", out var n) ? n.GetString() : null)
            .Where(n => n != null).Select(n => n!).ToHashSet(StringComparer.Ordinal);

    static IEnumerable<string> Candidates(string text)
    {
        bool tagged = false;
        foreach (Match m in Tagged().Matches(text)) { tagged = true; yield return m.Groups["body"].Value; }
        if (tagged) yield break;
        bool fenced = false;
        foreach (Match m in Fenced().Matches(text)) { fenced = true; yield return m.Groups["body"].Value; }
        if (fenced) yield break;
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}')) yield return trimmed;
    }

    static AiToolCall? Parse(string json, IReadOnlySet<string> tools)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Trim(), new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) return root.GetArrayLength() == 1 ? Parse(root[0].GetRawText(), tools) : null;
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (var wrapper in new[] { "tool_call", "function_call", "function" })
                if (root.TryGetProperty(wrapper, out var inner) && inner.ValueKind == JsonValueKind.Object && !root.TryGetProperty("name", out _))
                    return Parse(inner.GetRawText(), tools);
            string? name = null;
            foreach (var key in new[] { "name", "tool", "tool_name" })
                if (root.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.String) { name = n.GetString(); break; }
            if (name == null || !tools.Contains(name)) return null;
            JsonElement arguments = default; bool has = false;
            foreach (var key in new[] { "arguments", "parameters", "args", "input" })
                if (root.TryGetProperty(key, out arguments)) { has = true; break; }
            string text = !has ? "{}" : arguments.ValueKind == JsonValueKind.String ? arguments.GetString() ?? "{}" : arguments.GetRawText();
            using (var check = JsonDocument.Parse(text)) if (check.RootElement.ValueKind != JsonValueKind.Object) return null;
            return new("recovered-" + Guid.NewGuid().ToString("N")[..12], name, text);
        }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"<(?<tag>tool_call|tools|tool|function_call|functioncall)>\s*(?<body>[\s\S]*?)\s*</\k<tag>>", RegexOptions.IgnoreCase)] private static partial Regex Tagged();
    [GeneratedRegex(@"```(?:json|tool_call)?\s*\n(?<body>\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase)] private static partial Regex Fenced();
}
