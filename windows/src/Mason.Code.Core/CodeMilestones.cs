using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mason.Code;

public sealed record MilestoneReceipt(Guid Id, DateTimeOffset At, string Revision, string Detail, bool Manual);
public sealed record CodeMilestone(Guid Id, string Title, string Decision, string[] Files, Guid[] DependsOn,
    string Workflow, MilestoneReceipt? Receipt = null);
public sealed record MilestoneStatus(CodeMilestone Item, string State, string Reason);

public static class CodeMilestones
{
    public static CodeMilestone[] Put(CodeWorkspace workspace, IReadOnlyList<CodeMilestone> current, CodeMilestone item)
    {
        if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 240 ||
            item.Decision.Length > 2000 || item.Files.Length is < 1 or > 32 || item.DependsOn.Length > 32 ||
            item.Workflow.Length > 50) throw new ArgumentException("Use a title, 1–32 workspace files and a decision under 2,000 characters.");
        var files = item.Files.Select(p => workspace.Relative(workspace.Resolve(p))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var prior = current.SingleOrDefault(m => m.Id == item.Id);
        var dependencies = item.DependsOn.Distinct().Order().ToArray();
        var updated = item with { Title = item.Title.Trim(), Decision = item.Decision.Trim(), Files = files, DependsOn = dependencies, Workflow = item.Workflow.Trim(), Receipt = null };
        if (prior != null && prior.Title == updated.Title && prior.Decision == updated.Decision && prior.Workflow == updated.Workflow &&
            prior.Files.SequenceEqual(files) && prior.DependsOn.SequenceEqual(dependencies)) return current.ToArray();
        var result = prior == null ? current.Append(updated).ToArray() : current.Select(m => m.Id == item.Id ? updated : m).ToArray();
        if (result.Length > 64) throw new InvalidDataException("Each session supports up to 64 milestones.");
        var visited = new HashSet<Guid>();
        var pending = new HashSet<Guid>();
        var items = result.ToDictionary(m => m.Id);
        void Visit(Guid id)
        {
            if (visited.Contains(id)) return;
            if (!items.TryGetValue(id, out var node)) throw new ArgumentException("Choose a prerequisite in this plan.");
            if (!pending.Add(id)) throw new ArgumentException("Milestone dependencies form a cycle.");
            foreach (var dep in node.DependsOn) Visit(dep);
            pending.Remove(id); visited.Add(id);
        }
        foreach (var m in result) Visit(m.Id);
        return result;
    }

    static string Revision(CodeWorkspace scope, CodeMilestone m, IReadOnlyList<CodeMilestone> plan)
    {
        var inputs = m.Files.Select(path => (path, hash: scope.Read(path).Sha256)).ToArray();
        var prerequisites = m.DependsOn.Select(id => plan.Single(p => p.Id == id).Receipt?.Id).ToArray();
        string workflow = m.Workflow.Length == 0 ? "" : JsonSerializer.Serialize(Workflow(scope, m.Workflow));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { m.Title, m.Decision, files = inputs.Select(p => new { p.path, p.hash }), prerequisites, workflow });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    static ProjectWorkflow Workflow(CodeWorkspace scope, string name) =>
        ProjectWorkflows.Load(scope).Workflows.TryGetValue(name, out var value) ? value :
            throw new InvalidDataException("Workflow missing from mason.workflows.json: " + name);

    public static IReadOnlyList<MilestoneStatus> Inspect(CodeWorkspace workspace, IReadOnlyList<CodeMilestone> plan)
    {
        var states = new Dictionary<Guid, MilestoneStatus>();
        var items = plan.ToDictionary(m => m.Id);
        var visiting = new HashSet<Guid>();
        MilestoneStatus InspectOne(CodeMilestone item)
        {
            if (states.TryGetValue(item.Id, out var saved)) return saved;
            if (!visiting.Add(item.Id)) throw new InvalidDataException("Milestone dependency cycle.");
            MilestoneStatus status;
            if (item.DependsOn.Any(id => !items.TryGetValue(id, out var parent) || InspectOne(parent).State is not ("Verified" or "Reviewed")))
                status = new(item, "Blocked", "A prerequisite still needs attention.");
            else if (item.Receipt == null) status = new(item, "Ready", "Awaiting evidence.");
            else
            {
                try
                {
                    status = Revision(workspace, item, plan) == item.Receipt.Revision ?
                        new(item, item.Receipt.Manual ? "Reviewed" : "Verified", item.Receipt.Detail) :
                        new(item, "Needs recheck", "An input, workflow or prerequisite changed.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
                { status = new(item, "Needs recheck", e.Message); }
            }
            visiting.Remove(item.Id);
            return states[item.Id] = status;
        }
        return plan.Select(InspectOne).ToArray();
    }

    public static async Task<MilestoneReceipt> Check(CodeWorkspace workspace, CodeMilestone item,
        IReadOnlyList<CodeMilestone> plan, bool allowWorkflow, string manualObservation, CancellationToken token)
    {
        if (Inspect(workspace, plan).Single(s => s.Item.Id == item.Id).State == "Blocked")
            throw new InvalidOperationException("Complete the prerequisites first.");
        var revision = Revision(workspace, item, plan);
        string detail = manualObservation.Trim();
        if (item.Workflow.Length > 0)
        {
            if (!allowWorkflow) throw new UnauthorizedAccessException("Select Auto to run the project workflow in the workspace sandbox.");
            await using var services = new CodeRunServices(workspace, token, []);
            var result = await services.Workflow(item.Workflow, token);
            if (result.ExitCode != 0 || result.Truncated) throw new InvalidOperationException($"Workflow {item.Workflow} exited {result.ExitCode}. {result.Output[..Math.Min(result.Output.Length, 1800)]}");
            detail = $"{item.Workflow} · exit 0\n" + result.Output[..Math.Min(result.Output.Length, 2000)];
        }
        else if (detail.Length is < 1 or > 2000) throw new ArgumentException("Record what you observed in 1–2,000 characters.");
        token.ThrowIfCancellationRequested();
        if (Inspect(workspace, plan).Single(s => s.Item.Id == item.Id).State == "Blocked" || revision != Revision(workspace, item, plan)) throw new IOException("The milestone inputs changed during verification. Run the check again.");
        return new(Guid.NewGuid(), DateTimeOffset.UtcNow, revision, detail, item.Workflow.Length == 0);
    }

    public static string Context(CodeWorkspace scope, IReadOnlyList<CodeMilestone> plan)
    {
        var status = Inspect(scope, plan);
        var next = status.FirstOrDefault(s => s.State is "Ready" or "Needs recheck");
        var ordered = next == null ? status : new[] { next }.Concat(status.Where(s => s != next));
        var summary = new StringBuilder();
        foreach (var s in ordered)
        {
            var row = JsonSerializer.Serialize(new { s.Item.Title, s.State, s.Item.Decision, s.Item.Files });
            if (summary.Length + row.Length > 5000) break;
            summary.AppendLine(row);
        }
        return "User's saved milestones. Reviewed means a human recorded an observation. Next: " + (next?.Item.Title ?? "none ready") + "\n" + summary;
    }
}
