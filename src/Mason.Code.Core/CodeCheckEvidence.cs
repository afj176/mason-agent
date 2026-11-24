namespace Mason.Code;

public static class CodeCheckEvidence
{
    public static string Status(AcceptanceCheck check, AcceptanceResult? result)
    {
        if (result == null) return check.Kind == "unverified" ? "Unverified" : "Pending";
        if (result.Passed) return "Passed";
        return check.Kind == "unverified" || result.Detail.Contains("requires Auto or approval", StringComparison.Ordinal)
            || result.Detail.StartsWith("Workspace inputs changed during verification.", StringComparison.Ordinal)
            || result.Detail.StartsWith("Python sources changed during the test run.", StringComparison.Ordinal)
            || result.Detail is "Use Auto to run this isolated Python acceptance check." or "The acceptance test changed after planning." ? "Unverified" : "Failed";
    }

    public static AcceptanceResult? Result(CodeAcceptancePlan plan, AcceptanceCheck check) =>
        plan.Results.FirstOrDefault(r => r.Check == check ||
            (r.Check.Unit == check.Unit && r.Check.Kind == check.Kind && r.Check.Path == check.Path &&
             r.Check.FromContract == check.FromContract && r.Check.Feature == check.Feature && r.Check.Target == check.Target && r.Check.Expected == check.Expected &&
             r.Check.Application == check.Application && (r.Check.Steps ?? []).SequenceEqual(check.Steps ?? [])));

    public static string Summary(AcceptanceResult result)
    {
        var detail = result.Detail;
        int engine = detail.IndexOf(" · Edge ", StringComparison.Ordinal);
        if (engine >= 0 && result.Passed)
        {
            int start = detail.IndexOf("px · ", engine, StringComparison.Ordinal);
            if (start >= 0) detail = detail[(start + 5)..];
        }
        if (result.Passed && result.Check.Kind == "browser_flow" && result.Check.Steps is {} steps)
        {
            var observed = new List<string>();
            int index = 0;
            bool reloaded = false;
            string? clickTarget = null;
            int clickCount = 0;
            foreach (var step in steps)
            {
                if (step.Op == "reload") { reloaded = true; continue; }
                if (step.Op is "click" or "double_click" or "click_if_enabled" or "fill")
                {
                    reloaded = false;
                    if (step.Op == "click")
                    {
                        clickCount = clickTarget == step.Target ? clickCount + 1 : 1;
                        clickTarget = step.Target;
                    }
                    else { clickTarget = null; clickCount = 0; }
                    continue;
                }
                string marker = step.Op + " " + step.Target + " = ";
                if (!detail.AsSpan(index).StartsWith(marker, StringComparison.Ordinal)) return detail;
                int begin = index + marker.Length;
                int end = detail.IndexOf("; ", begin, StringComparison.Ordinal);
                string value = detail[begin..(end < 0 ? detail.Length : end)].Split(" · app: ", 2)[0];
                index = end < 0 ? detail.Length : end + 2;
                string observation = step.Op switch
                {
                    "money" when clickCount >= 2 => $"{clickCount} clicks · {value}",
                    "empty" => step.Target.TrimStart('#').Replace('-', ' ') + " empty",
                    "same" when reloaded => step.Target.TrimStart('#').Replace('-', ' ') + " persisted after reload",
                    "same" => step.Target + " matched its saved value",
                    "new_id" => "New identifier in " + step.Target,
                    "capture" => "",
                    "shelf_price" => value.Split(@"\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(s => s.Contains('$')) ?? value,
                    _ => value
                };
                if (observation.Length > 0 && !observed.Contains(observation)) observed.Add(observation);
            }
            if (index == detail.Length && observed.Count > 0) return string.Join(" → ", observed);
        }
        int hash = detail.IndexOf(" · SHA-256 ", StringComparison.Ordinal);
        if (hash >= 0 && result.Check.Kind == "document_text")
        {
            int tail = detail.IndexOf(" · ", hash + 11, StringComparison.Ordinal);
            if (tail >= 0) detail = detail[..hash] + detail[tail..];
        }
        return detail;
    }
}
