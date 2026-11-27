using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

public sealed partial class MainWindow
{
    sealed record EvidenceView(StackPanel Layout, Expander? Disclosure);
    static EvidenceView CheckEvidence(CodeAcceptancePlan plan, bool expanded, CodeWorkspaceView view)
    {
        var checks = new StackPanel { Spacing = 10 };
        int passed = 0;
        var issues = new List<string>();
        string? boundary = null, persistence = null;
        foreach (var criterion in plan.Checks)
        {
            var result = CodeCheckEvidence.Result(plan, criterion);
            var status = CodeCheckEvidence.Status(criterion, result);
            if (result?.Passed == true) passed++;
            string observation = result == null ? "" : CodeCheckEvidence.Summary(result);
            if (result?.Passed == false) issues.Add(status + " · " + criterion.Feature);
            else if (observation.Length > 0 && criterion.Steps?.Any(s => s.Op == "same") == true) persistence ??= observation;
            else if (observation.Length > 0 && criterion.Steps?.Count(s => s.Op == "money") > 1) boundary ??= observation;
            var raw = CodeNote(string.Join("\n", new[] { result?.Detail, $"{criterion.Kind} · {criterion.Path} {criterion.Target} {criterion.Expected}".TrimEnd(),
                plan.RequirementIds.ElementAtOrDefault(criterion.Unit - 1), plan.VerifiedRevision }.Where(s => !string.IsNullOrWhiteSpace(s))));
            raw.TextWrapping = TextWrapping.Wrap;
            if (view == CodeWorkspaceView.Review)
            {
                var receipt = Stack(Text(observation, 14), raw);
                checks.Children.Add(new Expander { Header = Text(status + " · " + criterion.Feature, 14), Content = receipt,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            else
            {
                var item = Stack(Text(status + " · " + criterion.Feature, 15));
                if (observation.Length > 0) item.Children.Add(Text(observation, 14));
                item.Children.Add(raw);
                checks.Children.Add(item);
            }
        }
        bool allPassed = plan.Checks.Count > 0 && passed == plan.Checks.Count;
        string count = $"{passed} of {plan.Checks.Count} checks passed";
        bool browserVerified = allPassed && plan.Results.Count > 0 && plan.Results.All(r =>
            r.Check.Kind.StartsWith("browser_", StringComparison.Ordinal) && r.Detail.Contains(" · Edge ", StringComparison.Ordinal));
        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var facts = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        string title = view == CodeWorkspaceView.Notebook && browserVerified ? "Verified in Edge" : count;
        facts.Children.Add(Heading(title, view == CodeWorkspaceView.Review ? 20 : 32));
        if (title != count) facts.Children.Add(Text(count, 15));
        if (view != CodeWorkspaceView.Review)
        {
            var sample = issues.Concat(new[] { boundary, persistence }.OfType<string>())
                .Concat(plan.Results.Where(r => r.Passed).Select(CodeCheckEvidence.Summary)).Distinct().Take(2);
            foreach (var s in sample)
            {
                var observed = Text(s, 16); observed.MaxLines = 3; observed.TextTrimming = TextTrimming.CharacterEllipsis;
                facts.Children.Add(observed);
            }
            if (view == CodeWorkspaceView.Workbench && allPassed)
                header.Children.Add(MascotImage(PoseChecks, 96, "checks passed"));
            else if (view == CodeWorkspaceView.Workbench && plan.Checks.Count > 0)
                header.Children.Add(MascotImage(PoseFailed, 80, "a check failed"));
            else if (view == CodeWorkspaceView.Notebook && allPassed)
            {
                var glyph = new FontIcon { Glyph = "\uE73E", FontSize = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 0, 0) };
                header.Children.Add(glyph);
            }
        }
        Grid.SetColumn(facts, 1); header.Children.Add(facts);
        var margin = view == CodeWorkspaceView.Review ? new Thickness(0, 5, 0, 5) : new Thickness(0, 8, 0, 0);
        var layout = Stack(header);
        layout.Spacing = 12; layout.Margin = margin;
        if (view == CodeWorkspaceView.Review)
        {
            checks.Spacing = 3;
            layout.Children.Add(checks);
            return new(layout, null);
        }
        var disclosure = new Expander { Header = $"View all {plan.Checks.Count} checks", Content = checks, IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        layout.Children.Add(disclosure);
        return new(layout, disclosure);
    }
}
