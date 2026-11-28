using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Mason;

public sealed record ChatBlock(string Kind, string Text, string Language = "");
public sealed record ChatArtifact(string Name, string Kind, string Content, ChatAttachment? Attachment = null)
{
    public override string ToString() => Name;
}

/// <summary>Bounded Markdown blocks and data artifacts; never evaluates generated code.</summary>
public static class ChatContent
{
    static readonly Regex Heading = new(@"^(#{1,6})\s+(.+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static List<ChatBlock> Blocks(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n'); var blocks = new List<ChatBlock>();
        for (int i = 0; i < lines.Length && blocks.Count < 500; i++)
        {
            var line = lines[i]; if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var language = line.Trim()[3..].Trim().ToLowerInvariant(); var code = new StringBuilder();
                while (++i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.AppendLine(lines[i]);
                blocks.Add(new("code", code.ToString().TrimEnd(), language)); continue;
            }
            if (Heading.Match(line) is { Success: true } match) { blocks.Add(new("h" + match.Groups[1].Length, match.Groups[2].Value)); continue; }
            if (i + 1 < lines.Length && line.Contains('|') && Regex.IsMatch(lines[i + 1], @"^\s*\|?\s*:?-{3,}:?\s*\|", RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                var table = new StringBuilder(line + "\n" + lines[++i]);
                while (i + 1 < lines.Length && lines[i + 1].Contains('|') && !string.IsNullOrWhiteSpace(lines[i + 1])) table.Append('\n').Append(lines[++i]);
                blocks.Add(new("table", table.ToString())); continue;
            }
            if (line.StartsWith('>')) { blocks.Add(new("quote", line.TrimStart('>', ' '))); continue; }
            var paragraph = new StringBuilder(line);
            while (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]) && !Regex.IsMatch(lines[i + 1], @"^\s*(```|#|>|[-*] |\d+\. )", RegexOptions.None, TimeSpan.FromSeconds(1)) && !lines[i + 1].Contains('|')) paragraph.Append('\n').Append(lines[++i]);
            blocks.Add(new("text", paragraph.ToString()));
        }
        return blocks;
    }
    public static List<ChatArtifact> Artifacts(ChatConversation conversation)
    {
        var result = new List<ChatArtifact>();
        foreach (var turn in conversation.Turns)
        {
            result.AddRange(turn.Attachments.Select(a => new ChatArtifact(a.Name, a.Kind, a.Text, a)));
            if (turn.Role != "assistant" || turn.Text.Length == 0) continue;
            result.Add(new($"Answer · {turn.At.ToLocalTime():t}", "markdown", turn.Text));
            int index = 0;
            foreach (var block in Blocks(turn.Text).Where(b => b.Kind is "code" or "table"))
            {
                var kind = block.Kind == "table" ? "table" : block.Language switch { "html" => "html", "md" or "markdown" => "markdown", "csv" => "csv", "tsv" => "tsv", "json" or "chart" => block.Language, _ => "code" };
                result.Add(new($"{(kind == "table" ? "Table" : block.Language.Length > 0 ? block.Language : "Code")} · {++index} · {turn.At.ToLocalTime():t}", kind, block.Text));
            }
        }
        result.AddRange(conversation.DraftAttachments.Select(a => new ChatArtifact(a.Name, a.Kind, a.Text, a)));
        return result;
    }
    public static DataTable Table(string text, string kind)
    {
        var table = new DataTable();
        if (kind is "json" or "chart")
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var rows = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement : json.RootElement.GetProperty("data");
            if (rows.GetArrayLength() > 2000) throw new ArgumentException("Tables are limited to 2,000 rows.");
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use an array of row objects.");
                foreach (var field in row.EnumerateObject()) if (!table.Columns.Contains(field.Name)) { if (table.Columns.Count >= 50) throw new ArgumentException("Tables are limited to 50 columns."); table.Columns.Add(field.Name); }
                var data = table.NewRow(); foreach (var field in row.EnumerateObject()) data[field.Name] = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText(); table.Rows.Add(data);
            }
            return table;
        }
        List<string[]> records;
        if (kind == "table") records = text.Replace("\r", "").Split('\n').Where((_, i) => i != 1).Select(l => Regex.Split(l.Trim().Trim('|'), @"(?<!\\)\|", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(c => c.Trim().Replace("\\|", "|")).ToArray()).ToList();
        else records = Delimited(text, kind == "tsv" ? '\t' : ',');
        if (records.Count == 0 || records[0].Length > 50 || records.Count > 2001) throw new ArgumentException("Use a table with at most 50 columns and 2,000 rows.");
        foreach (var header in records[0])
        {
            var name = header.Length == 0 ? "Column" : header; var unique = name; int suffix = 1; while (table.Columns.Contains(unique)) unique = name + " " + ++suffix; table.Columns.Add(unique);
        }
        foreach (var record in records.Skip(1)) { if (record.Length != table.Columns.Count) throw new ArgumentException("The table contains inconsistent row widths."); table.Rows.Add(record.Cast<object>().ToArray()); }
        return table;
    }
    public static List<string[]> Delimited(string text, char delimiter)
    {
        var rows = new List<string[]>(); var cells = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
            else if (ch == delimiter && !quoted) { cells.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n' && !quoted) { cells.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add(cells.ToArray()); cells.Clear(); if (rows.Count > 2001) throw new ArgumentException("Tables are limited to 2,000 rows."); }
            else cell.Append(ch);
        }
        if (quoted) throw new ArgumentException("The CSV contains an unterminated quoted field.");
        if (cell.Length > 0 || cells.Count > 0) { cells.Add(cell.ToString().TrimEnd('\r')); rows.Add(cells.ToArray()); }
        return rows;
    }
    public static ChartDefinition Chart(string content, string kind, DataTable table)
    {
        if (kind != "chart") return ChartModel.Suggest(table, 0);
        using var json = JsonDocument.Parse(content); var root = json.RootElement;
        var type = root.GetProperty("type").GetString() switch { "bar" => "Bar", "line" => "Line", "area" => "Area", "scatter" => "Scatter", "donut" => "Donut", _ => throw new ArgumentException("Unsupported chart type.") };
        var x = root.GetProperty("x").GetString()!; var values = root.GetProperty("y").EnumerateArray().Select(v => v.GetString()!).ToArray();
        if (!table.Columns.Contains(x) || values.Length == 0 || values.Any(v => !table.Columns.Contains(v))) throw new ArgumentException("Chart mappings must name columns present in data.");
        return new(Guid.NewGuid(), Kind: type, X: x, Values: values, Title: root.TryGetProperty("title", out var title) ? title.GetString() ?? "Chart" : "Chart", Aggregate: "Raw", Axis: type == "Scatter" ? "Numeric" : "Category");
    }
    public static string IsolatedHtml(string html, bool dark) => "<!doctype html><html><head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src 'none'; form-action 'none'; frame-src 'none'; base-uri 'none'; object-src 'none'\"><style>html{color-scheme:" + (dark ? "dark" : "light") + "}body{font:15px 'Segoe UI',sans-serif;margin:24px;overflow-wrap:anywhere}img{max-width:100%}table{border-collapse:collapse}td,th{padding:8px;border:1px solid #8885}pre{white-space:pre-wrap}</style></head><body>" + html + "</body></html>";
}
