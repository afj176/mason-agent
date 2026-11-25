using System.Globalization;
using System.Security;
using System.Text;

namespace Mason;

public sealed record ChartMark(string Kind, double X, double Y, double W, double H, string Color,
    string Text = "", string Path = "", string Tip = "", double Size = 12);
public sealed record ChartScene(double Width, double Height, List<ChartMark> Marks, string Background = "#171A2B")
{
    public ChartScene LightSurface()
    {
        var colors=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){["#F8F6F2"]="#20263C",["#3B4059"]="#D9DBE1",
            // Mason series colours: the bright token and its companions on night, deepened for white.
            ["#C3C2FF"]="#4849A8",["#F2B880"]="#A85E1B",["#7FD1C7"]="#12776C",["#F08FA5"]="#B03A58",["#A6D189"]="#4D792D",["#8FB8F0"]="#2A66A8",["#E9C971"]="#82631B",["#B59AE8"]="#6950B4",
            ["#F0E442"]="#807400",["#88CCEE"]="#28758F"};
        return this with{Background="#FFFFFF",Marks=Marks.Select(m=>colors.TryGetValue(m.Color,out var color)?m with{Color=color}:m).ToList()};
    }
    public string Svg()
    {
        static string E(string s) => SecurityElement.Escape(s) ?? "";
        static string N(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
        var svg = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(Width)}\" height=\"{N(Height)}\" viewBox=\"0 0 {N(Width)} {N(Height)}\" role=\"img\"><rect width=\"100%\" height=\"100%\" fill=\"{E(Background)}\"/>");
        foreach (var m in Marks)
        {
            svg.Append("<g><title>" + E(m.Tip) + "</title>");
            svg.Append(m.Kind switch
            {
                "Text" => $"<text x=\"{N(m.X)}\" y=\"{N(m.Y + m.Size)}\" fill=\"{m.Color}\" font-family=\"Segoe UI,sans-serif\" font-size=\"{N(m.Size)}\">{E(m.Text)}</text>",
                "Rect" => $"<rect x=\"{N(m.X)}\" y=\"{N(m.Y)}\" width=\"{N(m.W)}\" height=\"{N(m.H)}\" fill=\"{m.Color}\"/>",
                "Dot" => $"<ellipse cx=\"{N(m.X + m.W / 2)}\" cy=\"{N(m.Y + m.H / 2)}\" rx=\"{N(m.W / 2)}\" ry=\"{N(m.H / 2)}\" fill=\"{m.Color}\"/>",
                "Line" => $"<path d=\"{m.Path}\" fill=\"none\" stroke=\"{m.Color}\" stroke-width=\"2\"/>",
                _ => $"<path d=\"{m.Path}\" fill=\"{m.Color}\" fill-opacity=\"0.7\"/>"
            });
            svg.Append("</g>");
        }
        return svg.Append("</svg>").ToString();
    }
}

public static class ChartLayout
{
    // Default series palette. Leads with the brand accent; the rest are chosen to stay distinct beside it.
    public static readonly string[] Mason = ["#C3C2FF", "#F2B880", "#7FD1C7", "#F08FA5", "#A6D189", "#8FB8F0", "#E9C971", "#B59AE8"];
    public static readonly string[] Ocean = ["#56B4E9", "#E69F00", "#009E73", "#CC79A7", "#F0E442", "#D55E00", "#88CCEE", "#AA4499"];
    public static ChartScene Build(ChartDefinition definition, ChartDataset data, double width, double height, ISet<string>? hidden = null, bool compactSurface = false)
    {
        width = Math.Max(compactSurface ? 280 : 640, width); height = Math.Max(100, height);
        bool compact = height < 240 && definition.Kind is not ("Big number" or "Donut");
        if (!compact) height = Math.Max(height, 200 + (definition.Legend ? Math.Ceiling(data.Series.Count / 3d) * 24 : 0));
        if (definition.Kind == "Big number") height = Math.Max(height, 180 + Math.Ceiling(data.Series.Count / 3d) * 100);
        var marks = new List<ChartMark>(); var scene = new ChartScene(width, height, marks);
        var colors = definition.Palette == "Ocean" ? Ocean : Mason; // any other saved name uses the default
        string Color(int i) => colors[i % colors.Length];
        void Text(string text, double x, double y, double size = 12, string color = "#F8F6F2") => marks.Add(new("Text", x, y, 0, 0, color, text, Size: size));
        static string N(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
        static string P(double x, double y) => N(x) + "," + N(y);
        Text(definition.Title + (compact && definition.Unit.Length > 0 ? " · " + definition.Unit : ""), 24, compact ? 2 : 12, compact ? 12 : 20);
        if (!compact) Text($"{data.Rows:N0} visible loaded rows · {definition.Aggregate}" + (definition.Unit.Length > 0 ? " · " + definition.Unit : ""), 24, 40);
        if (data.Scope.Length > 0) Text(data.Scope, 24, compact ? 20 : 60, 10);
        if (!data.Valid) { Text(data.Message, 24, 90); return scene; }
        var series = data.Series.Select((s, i) => (Series: s, Index: i)).Where(s => hidden?.Contains(s.Series.Name) != true).ToList();
        if (series.Count == 0) { Text("All series hidden. Enable a series above.", 24, 90); return scene; }
        double legendHeight = definition.Legend && !compact ? Math.Ceiling(data.Series.Count / 3d) * 24 : 0;
        if (definition.Legend && !compact)
            foreach (var (s, i) in data.Series.Select((s, i) => (s, i))) Text((hidden?.Contains(s.Name) == true ? "○ " : "● ") + s.Name, 24 + i % 3 * (width - 48) / 3, height - legendHeight + i / 3 * 24, 12, Color(i));
        double left = compact ? 55 : 90, top = compact ? 44 : 84, w = width - left - 30, h = Math.Max(24, height - top - (compact ? 26 : 76) - legendHeight);
        string Format(double n) => n.ToString("0.###", CultureInfo.InvariantCulture) + (definition.Unit.Length == 0 ? "" : " " + definition.Unit);
        if (definition.Kind == "Big number")
        {
            int i = 0;
            foreach (var (s, index) in series)
            {
                double x = 24 + (i % 3) * (width - 48) / 3, y = 84 + i / 3 * 100;
                Text(s.Name, x, y, 14, Color(index)); Text(s.Points[0].Y is double v ? Format(v) : "∅", x, y + 24, 32); i++;
            }
            return scene;
        }
        if (definition.Kind == "Donut")
        {
            var points = series[0].Series.Points.Where(p => p.Y > 0).ToList(); double total = points.Sum(p => p.Y!.Value);
            double radius = Math.Min(w * .3, h * .48), inner = radius * .58, cx = left + w * .32, cy = top + h / 2, angle = -Math.PI / 2;
            int index = 0;
            foreach (var p in points)
            {
                double end = angle + p.Y!.Value / total * Math.PI * 2;
                // Two arcs per ring also handle the single-category full circle.
                double mid = (angle + end) / 2;
                string Point(double r, double a) => P(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
                string path = $"M {Point(radius, angle)} A {N(radius)},{N(radius)} 0 0 1 {Point(radius, mid)} A {N(radius)},{N(radius)} 0 0 1 {Point(radius, end)} L {Point(inner, end)} A {N(inner)},{N(inner)} 0 0 0 {Point(inner, mid)} A {N(inner)},{N(inner)} 0 0 0 {Point(inner, angle)} Z";
                marks.Add(new("Fill", 0, 0, 0, 0, Color(index), Path: path, Tip: $"{p.Label}: {Format(p.Y.Value)} ({p.Y / total:P1})"));
                if (index < Math.Max(1, (int)(h / 22))) Text($"{p.Label}: {Format(p.Y.Value)} ({p.Y / total:P1})", left + w * .65, top + index * 22, 12, Color(index));
                index++; angle = end;
            }
            Text("Total " + Format(total), left, top + h + 8); return scene;
        }
        var all = series.SelectMany(s => s.Series.Points).ToList(); var ys = all.Where(p => p.Y.HasValue).Select(p => p.Y!.Value).ToList();
        if (ys.Count == 0) { Text("No values in visible series.", 24, 90); return scene; }
        double min = Math.Min(0, ys.Min()), max = Math.Max(0, ys.Max());
        if (definition.Kind == "Stacked bar")
        {
            var totals = all.GroupBy(p => p.X).ToList();
            min = Math.Min(0, totals.Min(g => g.Sum(p => Math.Min(0, p.Y ?? 0))));
            max = Math.Max(0, totals.Max(g => g.Sum(p => Math.Max(0, p.Y ?? 0))));
        }
        if (min == max) max = min + 1;
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X); if (minX == maxX) { minX -= .5; maxX += .5; }
        bool descending = definition.Sort == "X descending" && data.Axis != "Category";
        static double Fraction(double value, double low, double high)
        {
            if (low == high) return .5;
            double range = high - low;
            return double.IsFinite(range) ? (value - low) / range : (value / 2 - low / 2) / (high / 2 - low / 2);
        }
        double X(double x) => left + w * (.04 + .92 * (descending ? 1 - Fraction(x, minX, maxX) : Fraction(x, minX, maxX)));
        double Y(double y) => top + h * (1 - Fraction(y, min, max));
        int intervals = compact ? 2 : 4;
        for (int i = 0; i <= intervals; i++)
        {
            double value = min * (1 - i / (double)intervals) + max * (i / (double)intervals), py = Y(value);
            marks.Add(new("Line", 0, 0, 0, 0, "#3B4059", Path: $"M {P(left, py)} L {P(left + w, py)}")); Text(value.ToString("G4", CultureInfo.InvariantCulture), 8, py - 8, compact ? 9 : 11);
        }
        var ticks = all.DistinctBy(p => p.X).OrderBy(p => p.X).ToList();
        foreach (var (p, i) in ticks.Select((p, i) => (p, i)).Where(x => x.i % Math.Max(1, (int)Math.Ceiling(ticks.Count / 8d)) == 0))
            Text(data.Axis == "Time" ? DateTimeOffset.FromUnixTimeMilliseconds((long)p.X).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : p.Label.Length > 18 ? p.Label[..17] + "…" : p.Label, X(p.X) - 24, top + h + 12, 11);
        double spacing = ticks.Count > 1 ? ticks.Zip(ticks.Skip(1), (a, b) => Math.Abs(X(b.X) - X(a.X))).Min() : w * .5;
        double barWidth = Math.Max(.5, Math.Min(90, spacing * .72));
        var positive = new Dictionary<double, double>(); var negative = new Dictionary<double, double>();
        foreach (var (s, index) in series)
        {
            var segment = new List<(double X, double Y)>();
            void Flush()
            {
                if (segment.Count < 2) { segment.Clear(); return; }
                var path = "M " + string.Join(" L ", segment.Select(p => P(p.X, p.Y)));
                if (definition.Kind == "Area") marks.Add(new("Fill", 0, 0, 0, 0, Color(index), Path: path + $" L {P(segment[^1].X, Y(0))} L {P(segment[0].X, Y(0))} Z"));
                marks.Add(new("Line", 0, 0, 0, 0, Color(index), Path: path)); segment.Clear();
            }
            foreach (var p in s.Points)
            {
                if (p.Y is not double value) { Flush(); continue; }
                double px = X(p.X), py = Y(value); string tip = $"{s.Name} · {p.Label}: {Format(value)}";
                if (definition.Kind is "Bar" or "Stacked bar")
                {
                    double start = 0, bw = barWidth;
                    if (definition.Kind == "Stacked bar") { var stack = value >= 0 ? positive : negative; stack.TryGetValue(p.X, out start); stack[p.X] = start + value; }
                    else { bw /= series.Count; px += (series.FindIndex(v => v.Index == index) - (series.Count - 1) / 2d) * bw; }
                    marks.Add(new("Rect", px - bw / 2, Math.Min(Y(start), Y(start + value)), bw, Math.Max(.5, Math.Abs(Y(start) - Y(start + value))), Color(index), Tip: tip));
                }
                else
                {
                    segment.Add((px, py));
                    // Keep every observation in the line path and row inspector. Dense live
                    // series do not need thousands of overlapping native point controls.
                    marks.Add(new("Dot", px - 3, py - 3, 6, 6, Color(index), Tip: tip));
                }
            }
            if (definition.Kind is "Line" or "Area") Flush();
        }
        return scene;
    }
}
