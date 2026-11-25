using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason.Code;

/// <summary>Forking a session from an earlier request, and moving a session between machines as a JSON file.</summary>
public static partial class CodeSessionTransfer
{
    public const int FormatVersion = 1;
    public sealed record Exported(int Format, string App, DateTimeOffset At, bool Sanitized, CodeSession Session);

    public static bool CanFork(CodeTurn turn) => turn.Role == "user" && turn.MessageIndex != null;

    /// <summary>A new session holding the conversation before <paramref name="turnId"/>, with that request in the
    /// composer so it can be edited and sent again. When the request recorded a snapshot and
    /// <paramref name="restoreFiles"/> is set, the workspace returns to its state before that request; files changed
    /// since are left alone and reported.</summary>
    public static (CodeSession Session, SnapshotRestore? Files) Fork(CodeStore store, CodeSession source, Guid turnId, bool restoreFiles)
    {
        if (source.State is "Running" or "Awaiting review") throw new InvalidOperationException("Stop the active turn before forking.");
        var index = source.Turns.FindIndex(t => t.Id == turnId);
        if (index < 0 || !CanFork(source.Turns[index])) throw new InvalidOperationException("This request was recorded before forking was available.");
        var turn = source.Turns[index];
        SnapshotRestore? files = null;
        if (restoreFiles && turn.SnapshotBefore is { } before)
        {
            var latest = CodeSnapshots.Capture(store.SettingsRoot, source.Workspace);
            files = CodeSnapshots.Restore(store.SettingsRoot, source.Workspace, latest.Id, before);
        }
        var fork = store.New(source.Workspace);
        var copy = Clone(source);
        fork.Title = (source.Title.Length > 46 ? source.Title[..46] + "…" : source.Title) + " · fork";
        fork.Model = copy.Model; fork.ReviewerModel = copy.ReviewerModel; fork.VisualModel = copy.VisualModel;
        fork.Mode = copy.Mode; fork.Permission = copy.Permission == CodePermission.Auto ? CodePermission.ReviewEdits : copy.Permission;
        fork.Network = copy.Network; fork.Shell = copy.Shell; fork.ShellExecutable = copy.ShellExecutable; fork.Reasoning = copy.Reasoning; fork.Agent = copy.Agent;
        fork.Messages = copy.Messages.Take(turn.MessageIndex!.Value).ToList();
        fork.Turns = copy.Turns.Take(index).ToList();
        int activities = turn.FirstActivity;
        fork.Activities = copy.Activities.Take(Math.Min(activities, copy.Activities.Count)).ToList();
        fork.Checkpoints = [];
        fork.Draft = turn.Text;
        fork.State = "Ready";
        fork.Detail = "Forked before “" + (turn.Text.Length > 50 ? turn.Text[..50] + "…" : turn.Text) + "”. Edit the request and send it." + (files is { } f ? $" Files restored: {f.Restored + f.Created + f.Deleted}" + (f.Conflicts.Length > 0 ? $", {f.Conflicts.Length} kept because they changed since." : ".") : "");
        store.Save(fork);
        return (fork, files);
    }

    public static string Export(CodeSession session, bool sanitize)
    {
        var copy = Clone(session);
        copy.Desktop = null; copy.Terminals = []; copy.Delivery = null; copy.Experiment = null;
        if (sanitize)
        {
            var root = session.Workspace;
            string Clean(string text) => Secrets().Replace(text.Replace(root, "<workspace>", StringComparison.OrdinalIgnoreCase), "<redacted>");
            copy.Workspace = "<workspace>";
            copy.Messages = copy.Messages.Select(m => m with { Content = Clean(m.Content), ReasoningContent = m.ReasoningContent is { } r ? Clean(r) : null }).ToList();
            foreach (var a in copy.Activities) { a.Arguments = Clean(a.Arguments); a.Result = Clean(a.Result); }
            foreach (var t in copy.Turns) t.Text = Clean(t.Text);
            copy.Draft = Clean(copy.Draft);
        }
        return JsonSerializer.Serialize(new Exported(FormatVersion, "Mason Agent", DateTimeOffset.UtcNow, sanitize, copy), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Adds an exported session to <paramref name="workspace"/> under a new ID. Snapshots are not carried over.</summary>
    public static CodeSession Import(CodeStore store, string json, string workspace)
    {
        if (json.Length > 24 * 1024 * 1024) throw new InvalidDataException("The session file exceeds 24 MiB.");
        var exported = JsonSerializer.Deserialize<Exported>(json) ?? throw new InvalidDataException("Not a Mason session export.");
        if (exported.Format != FormatVersion || exported.Session == null) throw new InvalidDataException("Unsupported session export format.");
        var target = store.New(workspace);
        var s = exported.Session;
        target.Title = s.Title + " · imported";
        target.Messages = s.Messages; target.Activities = s.Activities; target.Changes = [];
        target.Turns = s.Turns.Select(t => { t.SnapshotBefore = t.SnapshotAfter = null; t.Edits = []; return t; }).ToList();
        target.Mode = s.Mode; target.Reasoning = s.Reasoning; target.Agent = s.Agent;
        target.Detail = "Imported " + (exported.Sanitized ? "a sanitized" : "a") + " session from " + exported.At.ToLocalTime().ToString("g") + ". File history and undo stay with the original.";
        store.Save(target);
        return target;
    }

    static CodeSession Clone(CodeSession session) => JsonSerializer.Deserialize<CodeSession>(JsonSerializer.Serialize(session))!;

    [GeneratedRegex(@"(?i)(?:sk-[a-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[abpr]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|(?<=(?:api[_-]?key|token|secret|password)\s*[:=]\s*[""']?)[^\s""']{6,})")]
    private static partial Regex Secrets();
}
