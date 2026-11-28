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

    public const string Help =
        "/milestone                                  List the plan and where each milestone stands\n" +
        "/milestone add TITLE | FILES [| after N,N] [| workflow NAME] [| decision TEXT]\n" +
        "                                            Add one. FILES are project paths separated by commas\n" +
        "/milestone check N [WHAT YOU SAW]           Run its workflow (in Auto), or record what you observed\n" +
        "/milestone remove N                         Drop one";

    /// <summary>The CLI's /milestone over the session's plan: list, add, check and remove. A check runs the
    /// milestone's workflow only when <paramref name="allowWorkflow"/> is set (Auto), as the app does.</summary>
    public static async Task<string> Command(string rest, CodeWorkspace workspace, CodeSession session, bool allowWorkflow, CancellationToken token)
    {
        rest = rest.Trim();
        int space = rest.IndexOf(' ');
        string verb = (space < 0 ? rest : rest[..space]).ToLowerInvariant(), after = space < 0 ? "" : rest[(space + 1)..].Trim();
        var plan = session.Milestones;
        int Numbered(string text) => int.TryParse(text.Trim(), out var n) && n >= 1 && n <= plan.Length ? n - 1 : throw new ArgumentException("Give the number of a milestone. /milestone lists them.");
        if (verb.Length == 0)
        {
            if (plan.Length == 0) return "No milestones yet.\n" + Help;
            var status = Inspect(workspace, plan);
            return string.Join("\n", status.Select((row, i) =>
            {
                var text = $"{i + 1}. [{row.State}] {row.Item.Title}  ·  {string.Join(", ", row.Item.Files)}";
                var afterIds = row.Item.DependsOn.Select(id => Array.FindIndex(plan, m => m.Id == id) + 1).Where(n => n > 0).ToArray();
                if (afterIds.Length > 0) text += "  ·  after " + string.Join(", ", afterIds);
                if (row.Item.Workflow.Length > 0) text += "  ·  workflow " + row.Item.Workflow;
                return text + "\n   " + row.Reason.ReplaceLineEndings("\n").Split('\n')[0];
            }));
        }
        if (verb == "help") return Help;
        if (verb == "add")
        {
            var fields = after.Split('|').Select(f => f.Trim()).ToArray();
            if (fields.Length < 2 || fields[1].Length == 0) throw new ArgumentException("Write it as /milestone add TITLE | FILES, for example /milestone add Totals are right | src/totals.py, tests/test_totals.py");
            var files = fields[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var depends = new List<Guid>(); string workflow = "", decision = "";
            foreach (var field in fields.Skip(2))
            {
                int gap = field.IndexOf(' ');
                string key = (gap < 0 ? field : field[..gap]).ToLowerInvariant(), value = gap < 0 ? "" : field[(gap + 1)..].Trim();
                if (key == "after") depends.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => plan[Numbered(n)].Id));
                else if (key == "workflow") { Workflow(workspace, value); workflow = value; }
                else if (key == "decision") decision = value;
                else throw new ArgumentException("After the files, use: after N,N · workflow NAME · decision TEXT.");
            }
            foreach (var file in files) workspace.Read(file);
            var item = new CodeMilestone(Guid.NewGuid(), fields[0], decision, files, [.. depends], workflow);
            session.Milestones = Put(workspace, plan, item);
            return "Added milestone " + session.Milestones.Length + ": " + item.Title.Trim();
        }
        if (verb == "remove")
        {
            int index = Numbered(after); var removed = plan[index];
            if (plan.Any(m => m.DependsOn.Contains(removed.Id))) throw new ArgumentException("Another milestone comes after " + removed.Title + ". Remove that one first.");
            session.Milestones = plan.Where(m => m.Id != removed.Id).ToArray();
            return "Removed " + removed.Title + ".";
        }
        if (verb == "check")
        {
            int gap = after.IndexOf(' ');
            int index = Numbered(gap < 0 ? after : after[..gap]);
            string observation = gap < 0 ? "" : after[(gap + 1)..];
            var item = plan[index];
            var receipt = await Check(workspace, item, plan, allowWorkflow, observation, token);
            session.Milestones = plan.Select(m => m.Id == item.Id ? m with { Receipt = receipt } : m).ToArray();
            return item.Title + ": " + Inspect(workspace, session.Milestones)[index].State + ".";
        }
        throw new ArgumentException("Use /milestone, /milestone add, /milestone check or /milestone remove. /milestone help shows how.");
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
