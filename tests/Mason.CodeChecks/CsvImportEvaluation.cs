using System.Text.Json;
using Mason;
using Mason.Code;

static class CsvImportEvaluation
{
    public const string Request = """
        Implement offline CSV bulk import in this existing Ticketbox repository, end to end. Inspect the repository first. Use Python standard library only; preserve TicketStore.create/list, existing CLI commands, legacy tests, AGENTS.md and unrelated.cfg.
        Add TicketStore.import_csv(text, dry_run=False). CSV requires external_id and title headers; priority is optional and defaults to normal when absent/blank. Allow only these three column names; reject missing/duplicate/unknown headers and surplus fields. Support UTF-8 BOM, Unicode, quoted commas and quoted multiline titles. Strip surrounding whitespace from external_id/title; external IDs remain case-sensitive. Blank IDs/titles and priorities outside low/normal/high are errors. Report useful 1-based CSV record row numbers (header row 1).
        Return JSON-compatible {created: count, skipped: count, tickets: newly proposed/created tickets}. Imported tickets have id/title/priority/external_id. Preserve sequential IDs and legacy tickets. The dry-run result must equal the subsequent real import result for unchanged input/store, and dry-run must never create/change a store file.
        Matching external IDs with identical normalized title/priority are skipped, both within one CSV and against persisted tickets. Conflicting duplicates raise ValueError. Validate the entire batch before changing data. Any invalid/conflicting row must leave existing bytes unchanged and must not create a new store. Commit successful changes atomically using a same-directory temporary file and replacement, with cleanup on failure. An all-skipped import must not rewrite the file.
        Add CLI: python -m ticketbox --store PATH import CSV_FILE [--dry-run]. Print the result as JSON on stdout; validation/read errors go to stderr with exit 2 and no partial store. Preserve create/list behavior.
        Add meaningful regression tests, document the CSV contract, preview/commit examples, atomic behavior and duplicates in README, then run all tests, inspect actual output and fix failures. Open README.md when finished. Python is python.exe; the approved test command is run_command executable python.exe, arguments ["-m","unittest","discover","-s","tests","-v"], directory ".". No dependency installs or other shell commands are needed.
        """;
    public static async Task Resume(string endpoint, string model, string destination, string input)
    {
        var root = Path.GetFullPath(destination);
        using var store = new CodeStore(Path.Combine(root, "profile"));
        var session = store.Sessions.OrderByDescending(s => s.Updated).First(); var workspace = session.Workspace;
        var profiles = JsonSerializer.Deserialize<AiEndpointProfile[]>(File.ReadAllText(Path.Combine(root, "profile", "ai-connections.json")))!;
        var profile = profiles.Single(p => p.Id == session.Model);
        if (profile.Model != model || profile.Endpoint != endpoint) throw new InvalidOperationException("Continuation must use the same exact model/endpoint.");
        var python = CodeCommand.ResolveExecutable("python.exe", workspace);
        var reviews = new List<object>(); int emitted = session.Turns.Count; string detail = "";
        Task<bool> Review(CodeApproval approval, CancellationToken ct)
        {
            var activity = session.Activities.Last(); bool allowed = false;
            if (activity.Tool == "run_command")
            {
                using var json = JsonDocument.Parse(activity.Arguments); var a = json.RootElement;
                allowed = CodeCommand.ResolveExecutable(a.GetProperty("executable").GetString()!, workspace).Equals(python, StringComparison.OrdinalIgnoreCase) &&
                    a.GetProperty("directory").GetString() == "." && a.GetProperty("arguments").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "-m", "unittest", "discover", "-s", "tests", "-v" });
            }
            reviews.Add(new { approval.Tool, approval.Description, Allowed = allowed }); Console.WriteLine("REVIEW " + (allowed ? "ALLOW " : "DENY ") + approval.Description); return Task.FromResult(allowed);
        }
        void Changed()
        {
            foreach (var turn in session.Turns.Skip(emitted)) if (turn.Role == "assistant" && turn.Text.Length > 0) Console.WriteLine("AGENT " + turn.Text);
            emitted = session.Turns.Count;
            if (detail != session.Detail) { detail = session.Detail; Console.WriteLine("STATE " + detail); }
        }
        int originalMessages = session.Messages.Count;
        var priorHash = CodeContext.Hash(session.Messages.ToArray());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        using var client = new LocalAiClient(); var server = await client.ChatServer(profile, "", deadline.Token);
        await new CodeEngine(store, new CodeLocalModel(profile, "", session.Reasoning, text => Console.WriteLine("BUDGET " + text)), Review, Changed).Run(session, input, deadline.Token);
        var independent = await CodeCommand.Run(python, [Path.Combine(root, "acceptance.py"), workspace], workspace, default);
        var finalTests = await CodeCommand.Run(python, ["-m", "unittest", "discover", "-s", "tests", "-v"], workspace, default);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !Directory.Exists(Path.Combine(repo.FullName, "tests", "fixtures", "ticketbox"))) repo = repo.Parent;
        string seed = Path.Combine(repo!.FullName, "tests", "fixtures", "ticketbox");
        bool preserved = new[] { "AGENTS.md", "unrelated.cfg", "tests/test_legacy.py" }.All(name => File.ReadAllBytes(Path.Combine(seed, name)).SequenceEqual(File.ReadAllBytes(Path.Combine(workspace, name))));
        bool historyPreserved = priorHash == CodeContext.Hash(session.Messages.Take(originalMessages).ToArray());
        var report = new
        {
            Model = model, Endpoint = endpoint, LoadedContext = server.Context, session.Id, session.State, session.Detail,
            OriginalPrefixMessages = originalMessages, OriginalPrefixHash = priorHash, HistoryPrefixPreserved = historyPreserved,
            IndependentExit = independent.ExitCode, IndependentOutput = independent.Output, FinalTestsExit = finalTests.ExitCode,
            FinalTestsOutput = finalTests.Output, ProtectedFilesPreserved = preserved, session.Checkpoints, OriginalMessagesRetained = session.Messages.Count,
            Reviews = reviews, Commands = session.Activities.Where(a => a.Tool == "run_command"),
            Changes = session.Changes.Select(c => new { c.Path, c.AfterHash }),
            FinalFileHashes = Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories).Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is ".git" or "__pycache__")).ToDictionary(p => Path.GetRelativePath(workspace, p).Replace('\\', '/'), p => CodeProtocol.Hash(File.ReadAllBytes(p)))
        };
        File.WriteAllText(Path.Combine(root, "continuation-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("INDEPENDENT " + independent.Output); Console.WriteLine("FINAL " + finalTests.Output);
        if (session.State != "Completed" || independent.ExitCode != 0 || finalTests.ExitCode != 0 || !preserved || !historyPreserved) Environment.ExitCode = 1;
    }
    public static async Task Run(string endpoint, string model, string destination)
    {
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root)) throw new IOException("Use a fresh evaluation destination; existing work is retained.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !Directory.Exists(Path.Combine(repo.FullName, "tests", "fixtures", "ticketbox"))) repo = repo.Parent;
        if (repo == null) throw new IOException("Ticketbox evaluation seed was not found.");
        var workspace = Path.Combine(root, "ticketbox"); Directory.CreateDirectory(workspace);
        var seed = Path.Combine(repo.FullName, "tests", "fixtures", "ticketbox");
        foreach (var file in Directory.EnumerateFiles(seed, "*", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar).Any(p => p is "__pycache__" or ".git")) continue;
            string target = Path.Combine(workspace, Path.GetRelativePath(seed, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        var baselineHashes = Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(workspace, p).Replace('\\', '/'), p => CodeProtocol.Hash(File.ReadAllBytes(p)));
        File.Copy(Path.Combine(repo.FullName, "tests", "TicketImportAcceptance.py"), Path.Combine(root, "acceptance.py"));
        File.WriteAllText(Path.Combine(root, "request.txt"), Request);
        var python = CodeCommand.ResolveExecutable("python.exe", workspace);
        var git = CodeCommand.ResolveExecutable("git.exe", workspace);
        foreach (var argv in new[] { new[] { "init", "-b", "feature/csv-import" }, new[] { "add", "." }, new[] { "-c", "user.name=Mason QA", "-c", "user.email=qa@localhost", "commit", "-m", "Ticketbox baseline" } })
        { var command = await CodeCommand.Run(git, argv, workspace, default); if (command.ExitCode != 0) throw new Exception(command.Output); }
        var baseline = await CodeCommand.Run(python, ["-m", "unittest", "discover", "-s", "tests", "-v"], workspace, default);
        File.WriteAllText(Path.Combine(root, "baseline.txt"), baseline.Output); if (baseline.ExitCode != 0) throw new Exception("Invalid baseline: " + baseline.Output);
        using var store = new CodeStore(Path.Combine(root, "profile"));
        var session = store.New(workspace); session.Title = "CSV import · Qwen end-to-end"; session.Permission = CodePermission.WorkspaceEdits; session.Reasoning = "off";
        var profile = new AiEndpointProfile { Name = "Qwen 3.8 27B · coding compaction", Model = model, Endpoint = endpoint, ManagedCapability = AiCapability.Chat };
        session.Model = profile.Id;
        File.WriteAllText(Path.Combine(root, "profile", "ai-connections.json"), JsonSerializer.Serialize(new[] { profile }, new JsonSerializerOptions { WriteIndented = true }));
        var reviews = new List<object>();
        Task<bool> Review(CodeApproval approval, CancellationToken ct)
        {
            var pending = session.Activities.Last(); bool allowed = false;
            if (pending.Tool == "run_command")
            {
                using var json = JsonDocument.Parse(pending.Arguments); var a = json.RootElement;
                var executable = CodeCommand.ResolveExecutable(a.GetProperty("executable").GetString()!, workspace);
                var argv = a.GetProperty("arguments").EnumerateArray().Select(v => v.GetString()).ToArray();
                allowed = executable.Equals(python, StringComparison.OrdinalIgnoreCase) && a.GetProperty("directory").GetString() == "." && argv.SequenceEqual(new[] { "-m", "unittest", "discover", "-s", "tests", "-v" });
            }
            reviews.Add(new { approval.Tool, approval.Description, Allowed = allowed, Policy = "exact Ticketbox unittest command" });
            Console.WriteLine("REVIEW " + (allowed ? "ALLOW " : "DENY ") + approval.Description); return Task.FromResult(allowed);
        }
        string detail = ""; int emitted = 0;
        void Changed()
        {
            foreach (var turn in session.Turns.Skip(emitted)) if (turn.Role == "assistant" && turn.Text.Length > 0) Console.WriteLine("AGENT " + turn.Text);
            emitted = session.Turns.Count;
            if (detail != session.Detail) { detail = session.Detail; Console.WriteLine("STATE " + detail); }
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var client = new LocalAiClient(); var server = await client.ChatServer(profile, "", deadline.Token);
        File.WriteAllText(Path.Combine(root, "server-identity.json"), JsonSerializer.Serialize(server, new JsonSerializerOptions { WriteIndented = true }));
        await new CodeEngine(store, new CodeLocalModel(profile, "", "off", text => Console.WriteLine("BUDGET " + text)), Review, Changed).Run(session, Request, deadline.Token);
        var independent = await CodeCommand.Run(python, [Path.Combine(root, "acceptance.py"), workspace], workspace, default);
        var finalTests = await CodeCommand.Run(python, ["-m", "unittest", "discover", "-s", "tests", "-v"], workspace, default);
        string[] protectedFiles = ["unrelated.cfg", "AGENTS.md", "tests/test_legacy.py"];
        bool preserved = protectedFiles.All(name => CodeProtocol.Hash(File.ReadAllBytes(Path.Combine(workspace, name))) == baselineHashes[name]);
        var report = new
        {
            Model = model, Endpoint = endpoint, LoadedContext = server.Context, session.Id, session.State, session.Detail,
            BaselineExit = baseline.ExitCode, IndependentExit = independent.ExitCode, IndependentOutput = independent.Output,
            FinalTestsExit = finalTests.ExitCode, FinalTestsOutput = finalTests.Output, ProtectedFilesPreserved = preserved,
            session.Checkpoints, OriginalMessagesRetained = session.Messages.Count, Reviews = reviews,
            Commands = session.Activities.Where(a => a.Tool == "run_command"), Changes = session.Changes.Select(c => new { c.Path, c.AfterHash }),
            FinalFileHashes = Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories).Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is ".git" or "__pycache__")).ToDictionary(p => Path.GetRelativePath(workspace, p).Replace('\\', '/'), p => CodeProtocol.Hash(File.ReadAllBytes(p)))
        };
        File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("INDEPENDENT " + independent.Output); Console.WriteLine("FINAL " + finalTests.Output); Console.WriteLine("REPORT " + Path.Combine(root, "report.json"));
        if (session.State != "Completed" || independent.ExitCode != 0 || finalTests.ExitCode != 0 || !preserved || !session.Activities.Any(a => a.Tool == "run_command" && a.State == "Completed")) Environment.ExitCode = 1;
    }
}
