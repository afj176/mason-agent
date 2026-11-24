using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

public sealed partial class MainWindow
{
    static Expander CheckEvidence(CodeAcceptancePlan plan, bool expanded)
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
            var item = Stack(Text(status + " · " + criterion.Feature, 14));
            item.Spacing = 3;
            if (observation.Length > 0)
            {
                var observed = Text(observation, 13);
                observed.TextWrapping = TextWrapping.Wrap;
                observed.MaxLines = 4;
                observed.TextTrimming = TextTrimming.CharacterEllipsis;
                item.Children.Add(observed);
                if (result?.Passed == false) issues.Add(status + " · " + criterion.Feature);
                else if (criterion.Steps?.Any(s => s.Op == "same") == true) persistence ??= observation;
                else if (criterion.Steps?.Count(s => s.Op == "money") > 1) boundary ??= observation;
            }
            var raw = CodeNote(string.Join("\n", new[] { result?.Detail, $"{criterion.Kind} · {criterion.Path} {criterion.Target} {criterion.Expected}".TrimEnd(),
                plan.RequirementIds.ElementAtOrDefault(criterion.Unit - 1), plan.VerifiedRevision }.Where(s => !string.IsNullOrWhiteSpace(s))));
            raw.TextWrapping = TextWrapping.Wrap;
            item.Children.Add(new Expander { Header = "Evidence", Content = raw, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch });
            checks.Children.Add(item);
        }
        var header = Stack(Text($"Feature checks · {passed}/{plan.Checks.Count} passed", 14));
        header.Spacing = 3;
        // Keep the summary short. Every observation remains available in its own row.
        var sample = issues.Concat(new[] { boundary, persistence }.OfType<string>())
            .Concat(plan.Results.Where(r => r.Passed).Select(CodeCheckEvidence.Summary)).Distinct().Take(2);
        foreach (var s in sample)
        {
            var text = Text(s, 13);
            text.TextWrapping = TextWrapping.Wrap; text.MaxLines = 2; text.TextTrimming = TextTrimming.CharacterEllipsis;
            header.Children.Add(text);
        }
        return new Expander { Header = header, Content = checks, IsExpanded = expanded, Margin = new Thickness(40, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }
}
