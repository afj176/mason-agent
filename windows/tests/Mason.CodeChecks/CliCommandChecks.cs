using System.Text.Json;
using Mason;
using Mason.Code;

/// <summary>The core behind the CLI's session, settings and workflow commands: /schedule (and --run-due), /agent new and
/// /commands new, /theme light|dark|system, /fork, /export and /import, /comment, /milestone, /roles and /generation.
/// Each check writes only inside its own folder under the checks root.</summary>
internal static class CliCommandChecks
{
    static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

    internal static async Task Run(string root, Action<bool, string> check)
    {
        var area = Path.Combine(root, "cli-commands");
        if (Directory.Exists(area)) Directory.Delete(area, true);
        var profile = Directory.CreateDirectory(Path.Combine(area, "profile")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(area, "project")).FullName;
        File.WriteAllText(Path.Combine(project, "app.py"), "def total(a, b):\n    return a + b\n");

        // ── /schedule ────────────────────────────────────────────────────────────────────────────────────────────
        var now = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var connection = Guid.NewGuid();
        check(CodeSchedules.Command("", profile, project, null, now).StartsWith("No schedules. Add one with:\n/schedule"), "/schedule with none lists the usage");
        check(Throws<ArgumentException>(() => CodeSchedules.Command("add nightly daily 02:00 -- Run the tests", profile, project, null, now)), "/schedule add needs the session's model");
        var added = CodeSchedules.Command("add nightly daily 02:00 auto -- Run the tests", profile, project, (connection, "qwen-coder"), now);
        var saved = CodeSchedules.Load(profile).Single();
        check(added.StartsWith("Scheduled nightly: every day at 02:00, in Auto") && added.Contains("mason --run-due") && saved.Auto && saved.Connection == connection &&
            saved.Cadence == ScheduleCadence.Daily && saved.At == new TimeOnly(2, 0) && saved.Prompt == "Run the tests" && saved.LastRun == now,
            "/schedule add saves the cadence, time, Auto, the session's connection and counts from now");
        CodeSchedules.Command("add weekly-review weekly fri 09:30 -- Review dependencies", profile, project, (connection, "qwen-coder"), now);
        check(CodeSchedules.Load(profile)[1] is { Cadence: ScheduleCadence.Weekly, Day: DayOfWeek.Friday, Auto: false }, "A weekly schedule takes a day and is read-only without auto");
        check(Throws<ArgumentException>(() => CodeSchedules.Command("add bad daily 25:00 -- x", profile, project, (connection, "m"), now)) &&
              Throws<ArgumentException>(() => CodeSchedules.Command("add nightly hourly :05 -- x", profile, project, (connection, "m"), now)) &&
              Throws<ArgumentException>(() => CodeSchedules.Command("add hourly-one hourly 05 -- x", profile, project, (connection, "m"), now)),
            "/schedule add refuses a bad time and a repeated name");
        var listed = CodeSchedules.Command("list", profile, project, null, now);
        check(listed.Contains("nightly  ·  Every day at 02:00  ·  Auto") && listed.Contains("project: Run the tests") && listed.Contains("not run yet"), "/schedule lists each with its cadence, project, request and last run");
        check(CodeSchedules.Command("off NIGHTLY", profile, project, null, now) == "nightly is off." && !CodeSchedules.Load(profile)[0].Enabled, "/schedule off switches one off by name");
        CodeSchedules.Command("on nightly", profile, project, null, now.AddDays(3));
        check(CodeSchedules.Load(profile)[0] is { Enabled: true } on && on.LastRun == now.AddDays(3), "/schedule on counts from now, so a paused schedule does not fire for a time long past");
        check(CodeSchedules.Command("remove weekly-review", profile, project, null, now) == "Removed weekly-review." && CodeSchedules.Load(profile).Count == 1, "/schedule remove drops one");
        check(Throws<ArgumentException>(() => CodeSchedules.Command("remove nothing", profile, project, null, now)), "/schedule names a missing schedule");
        // --run-due: a due schedule whose connection is gone is skipped, and the outcome is recorded once.
        var later = now.AddDays(4);
        var ran = CodeSchedules.RunDue(profile, "mason.exe", _ => throw new InvalidOperationException("its model connection was removed"), later);
        check(ran.Count == 1 && ran[0].Outcome == "skipped: its model connection was removed" && CodeSchedules.Load(profile)[0].LastOutcome == ran[0].Outcome &&
            CodeSchedules.RunDue(profile, "mason.exe", _ => throw new InvalidOperationException("no"), later.AddMinutes(1)).Count == 0,
            "--run-due records why a schedule was skipped and does not repeat it");

        // ── /agent new and /commands new ──────────────────────────────────────────────────────────────────────────
        var example = CodeCustomization.Example(true);
        var agentPath = CodeCustomization.Create(project, true, "reviewer", example.Description, example.Body, askOnly: true);
        var agent = CodeCustomization.Agents(profile, project).Single(a => a.Name == "reviewer");
        check(agentPath == Path.Combine(project, ".mason", "agents", "reviewer.md") && agent.Description == example.Description && agent.Mode == "ask" && agent.Body.Contains("Never edit files"),
            "/agent new writes .mason/agents/NAME.md from the app's template, ask-only");
        var commandExample = CodeCustomization.Example(false);
        CodeCustomization.Create(project, false, "fix-issue", commandExample.Description, commandExample.Body, askOnly: false);
        check(CodeCustomization.Expand(CodeCustomization.Commands(profile, project), "/fix-issue 42 urgent")?.Contains("Fix issue 42") == true, "/commands new writes a command that runs with its arguments");
        check(Throws<IOException>(() => CodeCustomization.Create(project, true, "reviewer", "", "", true)) && Throws<ArgumentException>(() => CodeCustomization.Create(project, true, "../escape", "", "", true)),
            "Creating refuses an existing file and a name that is not a plain file name");

        // ── /theme light|dark|system ─────────────────────────────────────────────────────────────────────────────
        File.WriteAllText(Path.Combine(profile, "appearance.json"), "{\"Theme\":\"Default\",\"FontSize\":16,\"Family\":\"Kiln\"}");
        check(ThemeFamilies.SaveMode(profile, "dark") == "Dark" && ThemeFamilies.SavedMode(profile) == "Dark" && ThemeFamilies.Saved(profile).Name == "Kiln", "/theme dark saves the app's mode and keeps the family");
        using (var kept = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "appearance.json"))))
            check(kept.RootElement.GetProperty("FontSize").GetInt32() == 16, "/theme keeps every other appearance setting");
        check(ThemeFamilies.SaveMode(profile, "system") == "Default" && ThemeFamilies.SavedMode(profile) == "Default" && Throws<ArgumentException>(() => ThemeFamilies.SaveMode(profile, "blue")),
            "/theme system follows Windows; anything else is refused");

        // ── /fork, /export, /import ──────────────────────────────────────────────────────────────────────────────
        using (var store = new CodeStore(Path.Combine(area, "store"), profile))
        {
            var session = store.New(project);
            session.Messages.AddRange([new CodeMessage("system", "rules"), new CodeMessage("user", "First in " + project + " with api_key=abcdef123456"), new CodeMessage("assistant", "Done."),
                new CodeMessage("user", "Second"), new CodeMessage("assistant", "Also done.")]);
            session.Turns.AddRange([new CodeTurn { Role = "user", Text = "First", MessageIndex = 1 }, new CodeTurn { Role = "assistant", Text = "Done." },
                new CodeTurn { Role = "user", Text = "Second", MessageIndex = 3 }, new CodeTurn { Role = "assistant", Text = "Also done." }]);
            session.Permission = CodePermission.Auto; store.Save(session);
            var (fork, files) = CodeSessionTransfer.Fork(store, session, session.Turns[2].Id, restoreFiles: false);
            check(fork.Id != session.Id && fork.Messages.Count == 3 && fork.Turns.Count == 2 && fork.Draft == "Second" && files == null && fork.Permission == CodePermission.ReviewEdits,
                "/fork 2 keeps the conversation before the second request, puts it back in the draft and leaves Auto");
            var exported = CodeSessionTransfer.Export(session, sanitize: true);
            check(!exported.Contains(project.Replace("\\", "\\\\")) && !exported.Contains(project) && !exported.Contains("abcdef123456") && exported.Contains("<workspace>") && exported.Contains("<redacted>"),
                "/export sanitized removes the project's path and likely secrets");
            var plain = CodeSessionTransfer.Export(session, sanitize: false);
            var imported = CodeSessionTransfer.Import(store, plain, project);
            check(imported.Id != session.Id && imported.Turns.Count == 4 && imported.Title.EndsWith("imported") && store.Sessions.Contains(imported), "/import adds the session to this project under a new ID");
            check(Throws<InvalidDataException>(() => CodeSessionTransfer.Import(store, "{\"Format\":9}", project)), "/import refuses another format");
            // /sessions delete removes the journal from the store.
            check(store.Delete(imported.Id) && !store.Sessions.Contains(imported), "/sessions delete removes a session");

            // ── /comment ─────────────────────────────────────────────────────────────────────────────────────────
            var scope = new CodeWorkspace(project);
            var comments = new List<CodeReviewComment>();
            check(CodeReviewComment.Command("", scope, comments).StartsWith("No comments are waiting."), "/comment with none says so");
            var noted = CodeReviewComment.Command("app.py:2 Check for None first.", scope, comments);
            check(noted == "Noted on app.py · updated line 2: return a + b" && comments.Single().Quote == "    return a + b" && comments[0].Revision == scope.Read("app.py").Sha256,
                "/comment PATH:LINE pins a note to the line as it is now");
            check(CodeReviewComment.Request("", comments) == "app.py · updated line 2 — Check for None first.", "A pinned note becomes the next request's text");
            check(Throws<ArgumentException>(() => CodeReviewComment.Command("app.py:9 x", scope, comments)) && Throws<ArgumentException>(() => CodeReviewComment.Command("app.py Missing line", scope, comments)),
                "/comment refuses a line outside the file and a note with no line");
            CodeReviewComment.Command("app.py:1 Name it add.", scope, comments);
            check(CodeReviewComment.Command("remove 1", scope, comments).StartsWith("Dropped the comment on app.py · updated line 2") && comments.Count == 1 &&
                  CodeReviewComment.Command("clear", scope, comments) == "Dropped 1 comment." && comments.Count == 0, "/comment remove and clear drop them");

            // ── /milestone ───────────────────────────────────────────────────────────────────────────────────────
            var planned = await CodeMilestones.Command("add Totals add up | app.py | decision Integers only", scope, session, false, CancellationToken.None);
            check(planned == "Added milestone 1: Totals add up" && session.Milestones.Single().Decision == "Integers only", "/milestone add saves the title, files and decision");
            await CodeMilestones.Command("add Docs | app.py | after 1", scope, session, false, CancellationToken.None);
            var plan = await CodeMilestones.Command("", scope, session, false, CancellationToken.None);
            check(plan.Contains("1. [Ready] Totals add up") && plan.Contains("2. [Blocked] Docs  ·  app.py  ·  after 1"), "/milestone lists the state, files and order");
            var checkedOne = await CodeMilestones.Command("check 1 The totals match by hand.", scope, session, false, CancellationToken.None);
            check(checkedOne == "Totals add up: Reviewed." && session.Milestones[0].Receipt?.Manual == true, "/milestone check N records what you saw");
            check(Throws<ArgumentException>(() => CodeMilestones.Command("remove 1", scope, session, false, CancellationToken.None).GetAwaiter().GetResult()) &&
                  await CodeMilestones.Command("remove 2", scope, session, false, CancellationToken.None) == "Removed Docs." && session.Milestones.Length == 1,
                "/milestone remove drops one, but not one another depends on");

            // ── /roles ───────────────────────────────────────────────────────────────────────────────────────────
            var first = Guid.NewGuid(); var second = Guid.NewGuid();
            var connections = new List<(Guid, string)> { (first, "Qwen coder"), (second, "Gemma vision") };
            var roles = CodeRoles.Command("reviewer 1", session, connections);
            check(session.ReviewerModel == first && roles.StartsWith("reviewer  Qwen coder\nvisual    the model doing the work"), "/roles reviewer NUMBER uses a saved connection for reviews");
            CodeRoles.Command("visual gemma", session, connections);
            CodeRoles.Command("limit 12", session, connections); CodeRoles.Command("trace on", session, connections);
            check(session.VisualModel == second && session.RoleCallLimit == 12 && session.RecordModelDecisions, "/roles visual NAME, limit and trace set the session's roles");
            check(Throws<ArgumentException>(() => CodeRoles.Command("limit 500", session, connections)) && Throws<ArgumentException>(() => CodeRoles.Command("reviewer 3", session, connections)),
                "/roles refuses a limit over 128 and a connection that is not listed");
            CodeRoles.Command("reviewer off", session, connections);
            check(session.ReviewerModel == null, "/roles reviewer off goes back to the coding model");
        }

        // ── /generation ──────────────────────────────────────────────────────────────────────────────────────────
        var endpoint = new AiEndpointProfile { Name = "Qwen", Endpoint = "http://127.0.0.1:8099/", Model = "qwen" };
        CodeSampling? kept2 = endpoint.Sampling;
        check(CodeSampling.Command("temperature 0.3", endpoint, s => kept2 = s) == "Temperature is 0.3 for qwen." && kept2?.Temperature == 0.3, "/generation NAME VALUE sets one");
        endpoint = endpoint with { Sampling = kept2 };
        CodeSampling.Command("top-k 40", endpoint, s => kept2 = s); endpoint = endpoint with { Sampling = kept2 };
        check(CodeSampling.Command("", endpoint, _ => { }).Contains("top_k  ·  40") && kept2?.Temperature == 0.3, "/generation lists what is kept");
        CodeSampling.Command("temperature off", endpoint, s => kept2 = s);
        check(kept2 is { Temperature: null, TopK: 40 }, "/generation NAME off leaves one to the server");
        check(Throws<ArgumentException>(() => CodeSampling.Command("temperature 3", endpoint, _ => { })) && Throws<ArgumentException>(() => CodeSampling.Command("top_k 1.5", endpoint, _ => { })) &&
              Throws<ArgumentException>(() => CodeSampling.Command("warmth 1", endpoint, _ => { })), "/generation refuses values out of range, fractions for whole numbers and unknown names");
        CodeSampling.Command("clear", endpoint, s => kept2 = s);
        check(kept2 == null, "/generation clear leaves them all to the server");
    }
}
