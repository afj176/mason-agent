using System.Data;
using System.Globalization;

namespace Mason;

// Definitions contain mappings and presentation only. Result rows never enter workspace.json.
public sealed record ChartDefinition(Guid Id, int ResultSet = 0, string Kind = "Bar", string X = "",
    string[]? Values = null, string Split = "", string Aggregate = "Sum", string Axis = "Auto",
    string Sort = "X ascending", string Title = "Chart", string Unit = "", string Palette = "Mason",
    bool Legend = true);
public sealed record ChartDatum(string Label, double X, double? Y);
public sealed record ChartSeries(string Name, List<ChartDatum> Points);
public sealed record ChartDataset(List<ChartSeries> Series, string Axis, string Message, int Rows, int Skipped = 0, string Scope = "")
{
    public bool Valid => Message.Length == 0;
}

public static class ChartModel
{
    public static readonly string[] Kinds = ["Bar", "Stacked bar", "Line", "Area", "Scatter", "Donut", "Big number"];
    public static string Text(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    public static bool Number(object value, out double number) => double.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    public static bool Time(object value, out double number)
    {
        number = 0;
        // Avoid interpreting ordinary numeric/category strings as dates.
        var text = Text(value);
        if (value == DBNull.Value || !(value is DateTime or DateTimeOffset) && !(text.Contains('-') || text.Contains('/'))) return false;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return false;
        number = date.ToUnixTimeMilliseconds(); return true;
    }
    public static ChartDefinition Suggest(DataTable table, int resultSet)
    {
        string x = table.Columns.Cast<DataColumn>().FirstOrDefault()?.ColumnName ?? "";
        var numeric = table.Columns.Cast<DataColumn>().Where(c => table.Rows.Cast<DataRow>().Any(r => r[c] != DBNull.Value) &&
            table.Rows.Cast<DataRow>().Where(r => r[c] != DBNull.Value).All(r => Number(r[c], out _))).Select(c => c.ColumnName).ToArray();
        string? value = numeric.FirstOrDefault(n => n != x) ?? numeric.FirstOrDefault();
        return new(Guid.NewGuid(), resultSet, Kind: table.Columns.Count == 1 && table.Rows.Count == 1 && value != null ? "Big number" : "Bar", X: x, Values: value == null ? [] : [value]);
    }

    public static ChartDataset Build(DataTable table, ChartDefinition definition)
    {
        ChartDataset Error(string message) => new([], "Category", message, table.Rows.Count);
        var values = definition.Values ?? [];
        if (!Kinds.Contains(definition.Kind)) return Error("Choose a supported chart type.");
        if (values.Length == 0) return Error("Select at least one value column.");
        var required = values.Concat(definition.Kind == "Big number" ? [] : new[] { definition.X }).Concat(definition.Split.Length > 0 ? new[] { definition.Split } : []).Distinct();
        var missing = required.Where(c => !table.Columns.Contains(c)).ToArray();
        if (missing.Length > 0) return Error("Missing columns: " + string.Join(", ", missing) + ". Update the saved mappings.");
        if (definition.Kind == "Scatter" && definition.Aggregate != "Raw") return Error("Scatter requires Raw aggregation to preserve individual observations.");
        if (definition.Kind == "Donut" && (values.Length != 1 || definition.Split.Length > 0 || definition.Aggregate == "Raw")) return Error("Donut requires one aggregated measure and no series split.");
        var rows = table.Rows.Cast<DataRow>().ToList();
        var xValues = definition.Kind == "Big number" ? [] : rows.Where(r => r[definition.X] != DBNull.Value).Select(r => r[definition.X]).ToList();
        string axis = definition.Axis == "Auto" ? xValues.Count > 0 && xValues.All(v => Number(v, out _)) ? "Numeric" : xValues.Count > 0 && xValues.All(v => Time(v, out _)) ? "Time" : "Category" : definition.Axis;
        if (definition.Kind == "Scatter" && axis != "Numeric") return Error("Scatter requires a numeric X axis.");
        if (definition.Kind != "Big number" && axis != "Category" && xValues.Any(v => axis == "Time" ? !Time(v, out _) : !Number(v, out _))) return Error("X contains values incompatible with the selected axis.");
        var labels = xValues.Select(Text).Distinct().ToList();
        if (definition.Sort != "Result order") labels = axis == "Category" ? labels.OrderBy(x => x, StringComparer.Ordinal).ToList() : labels.OrderBy(x => axis == "Time" ? ParseTime(x) : double.Parse(x, CultureInfo.InvariantCulture)).ToList();
        if (definition.Sort == "X descending") labels.Reverse();
        var categoryPositions = labels.Select((label, index) => (label, index)).ToDictionary(p => p.label, p => p.index, StringComparer.Ordinal);
        double Coordinate(string label) => axis == "Time" ? ParseTime(label) : axis == "Numeric" ? double.Parse(label, CultureInfo.InvariantCulture) : categoryPositions[label];
        var splits = definition.Split.Length == 0 ? new[] { "" } : rows.Select(r => r[definition.Split] == DBNull.Value ? null : Text(r[definition.Split])).Distinct().ToArray();
        if (splits.Length * values.Length > 24) return Error("More than 24 series. Choose a lower-cardinality series column.");
        var series = new List<ChartSeries>(); int skipped = 0;
        foreach (var split in splits)
        foreach (var value in values)
        {
            var source = rows.Where(r => definition.Split.Length == 0 || (r[definition.Split] == DBNull.Value ? null : Text(r[definition.Split])) == split).ToList();
            var name = definition.Split.Length == 0 ? value : value + " · " + (split ?? "∅");
            var points = new List<ChartDatum>();
            double? Aggregate(IEnumerable<DataRow> group)
            {
                var items = group.ToList();
                if (definition.Aggregate == "Count") return items.Count;
                var numbers = new List<double>();
                foreach (var row in items) { if (row[value] == DBNull.Value) continue; if (Number(row[value], out var n)) numbers.Add(n); else skipped++; }
                if (numbers.Count == 0) return null;
                double result = definition.Aggregate == "Average" ? numbers.Sum(v => v / numbers.Count) : numbers.Sum();
                return double.IsFinite(result) ? result : null;
            }
            if (definition.Kind == "Big number")
            {
                if (definition.Aggregate == "Raw") return Error("Big number requires Sum, Average or Count.");
                points.Add(new(value, 0, Aggregate(source)));
            }
            else if (definition.Aggregate == "Raw")
            {
                foreach (var row in source.Where(r => r[definition.X] != DBNull.Value))
                {
                    var label = Text(row[definition.X]); double? y = null;
                    if (row[value] != DBNull.Value) { if (Number(row[value], out var n)) y = n; else skipped++; }
                    points.Add(new(label, Coordinate(label), y));
                }
                if (definition.Sort != "Result order") points = (definition.Sort == "X descending" ? points.OrderByDescending(p => p.X) : points.OrderBy(p => p.X)).ToList();
            }
            else
            {
                var groups = source.Where(r => r[definition.X] != DBNull.Value).GroupBy(r => Text(r[definition.X])).ToDictionary(g => g.Key, g => g.AsEnumerable());
                foreach (var label in labels) points.Add(new(label, Coordinate(label), groups.TryGetValue(label, out var group) ? Aggregate(group) : null));
            }
            series.Add(new(name, points));
        }
        if (series.All(s => s.Points.All(p => p.Y == null))) return Error("No plottable values. NULL values are gaps; choose numeric measures or Count rows.");
        if (definition.Kind == "Stacked bar" && series.SelectMany(s => s.Points).GroupBy(p => p.X).Any(g => !double.IsFinite(g.Sum(p => Math.Max(0, p.Y ?? 0))) || !double.IsFinite(g.Sum(p => Math.Min(0, p.Y ?? 0))))) return Error("Stacked totals exceed the numeric range. Rescale the values.");
        if (definition.Kind == "Donut" && (series[0].Points.Any(p => p.Y < 0) || series[0].Points.Sum(p => p.Y ?? 0) <= 0)) return Error("Donut requires nonnegative values and a positive total.");
        if (definition.Kind == "Donut" && !double.IsFinite(series[0].Points.Sum(p => p.Y ?? 0))) return Error("Donut total exceeds the numeric range. Rescale the values.");
        if (series.Sum(s => s.Points.Count) > 12000) return Error("More than 12,000 plotted values. Aggregate the data; no points were silently removed.");
        return new(series, axis, "", rows.Count, skipped);
    }
    static double ParseTime(string text) { Time(text, out var result); return result; }
}
