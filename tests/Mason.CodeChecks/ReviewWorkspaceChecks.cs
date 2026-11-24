using System.Text.Json;
using Mason.Code;

internal static class ReviewWorkspaceChecks
{
    public static async Task Run(string area, Action<bool, string> check)
    {
        var root = Directory.CreateDirectory(Path.Combine(area, "project")).FullName;
        var workspace = new CodeWorkspace(root);
        var file = workspace.Create("sample.txt", "alpha\nupdated\n");
        var change = new CodeChange { Path = file.Path, Before = "alpha\nremoved\n", After = file.Content, AfterHash = file.Sha256 };
        var original = CodeReviewComment.OnDiff(change, new('-', 2, null, "removed"), " Explain the removal. ");
        var updated = CodeReviewComment.OnDiff(change, new('+', null, 2, "updated"), "Explain the addition.");
        var profile = Path.Combine(area, "profile");
        using (var writer = new CodeStore(profile))
        {
            var draft = writer.New(root);
            draft.Mode = "ask";
            check(draft.WorkspaceView == CodeWorkspaceView.Workbench &&
                JsonSerializer.Deserialize<CodeSession>("{}")?.WorkspaceView == CodeWorkspaceView.Workbench,
                "New and legacy sessions start in Workbench");
            draft.WorkspaceView = CodeWorkspaceView.Notebook;
            draft.Draft = "Preserve this note.";
            draft.ReviewComments.AddRange([original, updated]);
            writer.Save(draft);
        }
        using var store = new CodeStore(profile);
        var session = store.Sessions.Single();
        check(session.WorkspaceView == CodeWorkspaceView.Notebook && session.Draft == "Preserve this note.",
            "Notebook choice and the shared composer draft survive a restart");
        check(session.ReviewComments.SequenceEqual([original, updated]) && original.Original && !updated.Original &&
            original.Quote == "removed" && original.Note == "Explain the removal.", "Review drafts retain both diff sides, source lines, notes and revision after restart");
        var engine = new CodeEngine(store, new AnswerModel(), (_, _) => Task.FromResult(false), () => {});
        await engine.Run(session, "Read these together.", default, includeReview: true);
        var submission = session.Turns.Single(t => t.Role == "user");
        var sent = JsonDocument.Parse(session.Messages.First().Content).RootElement.GetProperty("review_comments");
        check(session.ReviewComments.Count == 0 && submission.SubmittedReview.SequenceEqual([original, updated]) &&
            sent.GetArrayLength() == 2 && sent[0].GetProperty("side").GetString() == "original" &&
            sent[1].GetProperty("revision_at_submission").GetString() == file.Sha256, "One accepted turn delivers both anchored comments to the model");
        session.ReviewComments.Add(updated);
        var source = Path.Combine(root, file.Path);
        File.WriteAllText(source, "inserted\n" + file.Content);
        try
        {
            await engine.Run(session, "", default, includeReview: true);
            throw new Exception("Stale feedback reached the model.");
        }
        catch (InvalidOperationException e) when (e.Message.Contains("choose its current line")) {}
        check(session.Turns.Count(t => t.Role == "user") == 1 && session.ReviewComments.Single() == updated, "A stale revision preserves the unsent review and creates no turn");
        var current = workspace.Read(file.Path);
        session.ReviewComments[0] = updated.OnCurrentLine(current, 3);
        var queued = new AnswerModel();
        var queueEngine = new CodeEngine(store, queued, (_, _) => Task.FromResult(false), () => {});
        queued.OnNext = () => queueEngine.SendWhileRunning(session, "", false, includeReview: true);
        await queueEngine.Run(session, "An independent question.", default);
        var delivery = session.Turns.Last(t => t.Role == "user");
        var anchor = delivery.SubmittedReview.Single();
        check(session.PendingInput.Count == 0 && session.ReviewComments.Count == 0 && anchor.Id == updated.Id &&
            anchor.Line == 3 && anchor.Quote == "updated" && anchor.Revision == current.Sha256 &&
            session.Turns.Count(t => t.Role == "user" && t.SubmittedReview.Contains(anchor)) == 1, "Reanchored comments-only follow-up drains once with the chosen line and current revision");

        var criterion = new AcceptanceCheck(1, "Report total", "browser_flow", "index.html", "", "")
        {
            Steps = [new("click", "#add"), new("click", "#add"), new("money", "#total", "20")]
        };
        var legacy = JsonSerializer.Deserialize<AcceptanceCheck>(JsonSerializer.Serialize(criterion))!;
        var receipt = new AcceptanceResult(legacy, true, "index.html · Edge 123 · 390px and 1280px · money #total = Basket $20.00");
        var plan = new CodeAcceptancePlan { Checks = [criterion], Results = [receipt] };
        check(CodeCheckEvidence.Result(plan, criterion) == receipt && CodeCheckEvidence.Summary(receipt) == "2 clicks · Basket $20.00",
            "Restored browser receipts show the observed amount with the completed interactions");
        var broken = new AcceptanceResult(criterion, false, "Step 3: expected 20, observed 22");
        var stale = broken with { Detail = "Workspace inputs changed during verification. Rerun against the current revision." };
        check(CodeCheckEvidence.Status(criterion, null) == "Pending" && CodeCheckEvidence.Status(criterion, broken) == "Failed" &&
            CodeCheckEvidence.Summary(broken) == broken.Detail && CodeCheckEvidence.Status(criterion, stale) == "Unverified",
            "Absent, failed and invalidated receipts retain distinct states and actual failure details");
        var complex = criterion with { Steps = [new("text", "#label", "ready"), new("same", "#receipt")] };
        var unmatched = new AcceptanceResult(complex, true, "text #label = ready; archived second observation");
        check(CodeCheckEvidence.Summary(unmatched) == unmatched.Detail, "An unfamiliar receipt format retains the complete observation");
    }

    sealed class AnswerModel : ICodeModel
    {
        public Action? OnNext;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> stream, CancellationToken ct)
        {
            var callback = OnNext;
            OnNext = null;
            callback?.Invoke();
            return Task.FromResult(new CodeAction("Answered.", null, default));
        }
    }
}
