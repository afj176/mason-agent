using System.Text.Json;
using Mason.Code;

static class CodingCompactionChecks
{
    public static async Task Run(string root, string workspace, Action<bool, string> check)
    {
        var system = new CodeMessage("system", CodeProtocol.Instructions + "\nRepository instructions: Preserve compatibility.");
        CodeSession Seed()
        {
            var session = new CodeSession { Workspace = workspace };
            session.Messages.Add(new("user", JsonSerializer.Serialize(new { request = "Preserve invoice_key ZX-9042, dry-run bytes, and Unicode café 😀.", references = Array.Empty<object>() })));
            for (int i = 0; i < 12; i++)
            {
                session.Messages.Add(new("assistant", JsonSerializer.Serialize(new { version = 1, message = "Inspect source.", tool = new { name = "read_file", arguments = new { path = "module" + i + ".py" } } })));
                session.Messages.Add(new("user", JsonSerializer.Serialize(new { tool_result = "read_file", activity_id = Guid.NewGuid(), state = "Completed", result = new string('x', 4200) })));
            }
            session.Activities.Add(new() { Tool = "run_command", State = "Failed", Arguments = "{\"executable\":\"python.exe\",\"arguments\":[\"-m\",\"unittest\"]}", Result = "{\"ExitCode\":1,\"Output\":\"FAIL duplicate invoice ZX-9042\"}" });
            session.Changes.Add(new() { Path = "invoice.py", After = "new content", AfterHash = new string('a', 64) });
            return session;
        }
        var session = Seed(); string original = JsonSerializer.Serialize(session.Messages); int receipts = session.Activities.Count;
        var model = new ContextFixture(); int saves = 0;
        var prepared = await CodeContext.Prepare(session, system, model, () => saves++, _ => { }, default);
        check(session.Checkpoints.Count == 1 && session.Checkpoints[0].AfterTokens < session.Checkpoints[0].BeforeTokens && saves >= 2, "automatic coding compaction frees exactly measured action space");
        check(JsonSerializer.Serialize(session.Messages) == original && session.Activities.Count == receipts + 1 && session.Activities.Last().State == "Completed", "compaction retains original journal and command failure receipts");
        using (var memory = JsonDocument.Parse(prepared[1].Content))
        {
            var ledger = memory.RootElement.GetProperty("ledger");
            check(ledger.GetProperty("retained_user_requests")[0].GetProperty("request").GetString() == "Preserve invoice_key ZX-9042, dry-run bytes, and Unicode café 😀.", "checkpoint protects exact user identifiers, constraints and Unicode outside the digest");
            check(ledger.GetProperty("recent_command_receipts")[0].GetProperty("state").GetString() == "Failed" && ledger.GetProperty("session_changes")[0].GetProperty("last_written_sha256").GetString() == new string('a', 64), "checkpoint carries actual command state and written hashes without fabricating success");
        }
        check(prepared[0] == system && prepared.Skip(2).SequenceEqual(session.Messages.Skip(session.Checkpoints[0].ThroughMessages)), "current protocol and repository instructions plus recent action/result pairs remain verbatim");
        check(session.Checkpoints[0].Batches > 1 && session.Messages[session.Checkpoints[0].ThroughMessages - 1].Role != "assistant", "oversized original observations are summarized in measured batches without splitting an action/receipt boundary");
        using (var store = new CodeStore(Path.Combine(root, "compaction-state"))) store.Save(session);
        using (var reopened = new CodeStore(Path.Combine(root, "compaction-state")))
            check(CodeContext.Active(reopened.Sessions.Single())?.Id == session.Checkpoints[0].Id && JsonSerializer.Serialize(reopened.Sessions[0].Messages) == original, "verified coding checkpoint and full journal survive restart");
        int summaries = model.Summaries;
        _ = await CodeContext.Prepare(session, system, model, () => { }, _ => { }, default);
        check(model.Summaries == summaries, "an adequate verified checkpoint is reused without another digest");
        int covered = session.Checkpoints[0].ThroughMessages;
        _ = await CodeContext.Prepare(session, system, new ContextFixture(4000), () => { }, _ => { }, default, force: true);
        check(session.Checkpoints.Count == 2 && session.Checkpoints[^1].ThroughMessages >= covered && session.Checkpoints[^1].AfterTokens < 3000, "manual re-compaction supports a smaller context without deleting earlier checkpoints");
        session.Messages[0] = new("user", JsonSerializer.Serialize(new { request = "Corrected invoice key ZX-9043", references = Array.Empty<object>() }));
        check(CodeContext.Active(session) == null, "modified source history invalidates stale checkpoints");
        var failed = Seed();
        try { _ = await CodeContext.Prepare(failed, system, new ContextFixture { EmptyDigest = true }, () => { }, _ => { }, default); } catch (InvalidDataException) { }
        check(failed.Checkpoints.Count == 0 && failed.Messages.Count == 25 && failed.Activities.Last().State == "Failed", "empty or incomplete digest never commits a checkpoint or removes history");
        var stopped = Seed(); using var cancellation = new CancellationTokenSource();
        try { _ = await CodeContext.Prepare(stopped, system, new ContextFixture { OnSummary = () => cancellation.Cancel() }, () => { }, _ => { }, cancellation.Token); } catch (OperationCanceledException) { }
        check(stopped.Checkpoints.Count == 0 && stopped.Messages.Count == 25 && stopped.Activities.Last().State == "Interrupted", "cancelled coding compaction preserves history and records interruption");
        var unsaved = Seed();
        try { _ = await CodeContext.Prepare(unsaved, system, new ContextFixture(), () => { if (unsaved.Checkpoints.Count > 0) throw new IOException("Fixture commit failure"); }, _ => { }, default); } catch (IOException) { }
        check(unsaved.Checkpoints.Count == 0 && unsaved.Messages.Count == 25 && unsaved.Activities.Last().State == "Failed", "checkpoint commit failure rolls back the in-memory projection and retains history");
        var tooLarge = Seed(); tooLarge.Messages.Add(new("user", JsonSerializer.Serialize(new { request = new string('q', 16_000), references = Array.Empty<object>() })));
        bool refused = false;
        try { _ = await CodeContext.Prepare(tooLarge, system, new ContextFixture(4000), () => { }, _ => { }, default); } catch (InvalidOperationException) { refused = true; }
        check(refused && tooLarge.Checkpoints.Count == 0 && tooLarge.Messages.Count == 26, "oversized protected requests stop explicitly instead of summarizing away constraints");
        var disabled = Seed(); disabled.AutomaticCompaction = false;
        refused = false;
        try { _ = await CodeContext.Prepare(disabled, system, new ContextFixture(), () => { }, _ => { }, default); } catch (InvalidOperationException) { refused = true; }
        check(refused && disabled.Checkpoints.Count == 0, "automatic compaction opt-out never silently shortens a full request");
        var badHash = session.Checkpoints[0]; badHash.Summary += " altered";
        bool invalid = false; try { badHash.Validate(); } catch (InvalidDataException) { invalid = true; }
        check(invalid, "checkpoint digest corruption is detected before reuse");
        using (var store = new CodeStore(Path.Combine(root, "compaction-engine")))
        {
            var engineSession = Seed(); engineSession.Permission = CodePermission.ReadOnly;
            var engineModel = new ContextFixture { Actions = [Tool("create_file", new { path = "compaction-escape.txt", content = "bad" }), Tool("create_file", new { path = "compaction-escape.txt", content = "bad" }), Tool("open_file", new { path = "README.md" }), new("Actual read-only artifact opened.", null, default)] };
            await new CodeEngine(store, engineModel, (_, _) => Task.FromResult(true), () => { }).Run(engineSession, "Inspect the README; do not write.", default);
            check(engineSession.State == "Needs verification" && engineSession.Checkpoints.Count > 0 && engineModel.NextCalls == 4 && !File.Exists(Path.Combine(workspace, "compaction-escape.txt")), "engine continues after compaction without weakening file permissions, replaying tools or claiming unsupported acceptance");
            check(engineSession.Activities.Count(a => a.Tool == "create_file" && a.State == "Denied") == 2 && engineSession.Activities.Count(a => a.Tool == "open_file") == 1, "denied-operation guard and exactly-once successful tool receipt survive compacted context");
        }
        static CodeAction Tool(string name, object arguments) => new("Observed fixture operation.", name, JsonSerializer.SerializeToElement(arguments));
    }
    sealed class ContextFixture(int window = 8000) : ICodeContextModel
    {
        public bool EmptyDigest;
        public Action? OnSummary;
        public int Summaries, NextCalls;
        public CodeAction[] Actions = [];
        public Task<CodeContextUsage> Measure(IReadOnlyList<CodeMessage> messages, bool digest, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); int tokens = messages.Sum(m => m.Content.Length) / 4;
            return Task.FromResult(new CodeContextUsage(tokens, window, Math.Max(0, window - tokens), "fixture-coder"));
        }
        public Task<CodeDigest> Summarize(IReadOnlyList<CodeMessage> messages, CancellationToken ct)
        {
            Summaries++; OnSummary?.Invoke(); ct.ThrowIfCancellationRequested();
            return Task.FromResult(new CodeDigest(EmptyDigest ? "" : "Goals: implement the requested change. Decisions: preserve compatibility. Completed work: source inspection. Open work: finish implementation and verify actual tests. Caveats: read current files before editing; previous command failed."));
        }
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation) => Task.FromResult(Actions[NextCalls++]);
    }
}
