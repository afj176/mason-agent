using System.Text.Json;
using Mason;
using Mason.Code;

/// <summary>Deterministic checks for the Goose/OpenCode gap features (TODO.md G3–G10). No model needed.</summary>
static class GapFeatureChecks
{
    public static void Run(string root)
    {
        void Check(bool passed, string name) { Console.WriteLine((passed ? "PASS " : "FAIL ") + name); if (!passed) throw new Exception(name); }
        root = Path.Combine(root, "gap-" + Guid.NewGuid().ToString("N")[..8]);
        var settings = Path.Combine(root, "profile"); var workspace = Path.Combine(root, "project");
        Directory.CreateDirectory(settings); Directory.CreateDirectory(Path.Combine(workspace, "src"));

        // G3: snapshots capture command-made changes and undo restores exactly those, leaving later edits alone.
        File.WriteAllText(Path.Combine(workspace, "src", "a.txt"), "one");
        File.WriteAllText(Path.Combine(workspace, "keep.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(workspace, "node_modules")); File.WriteAllText(Path.Combine(workspace, "node_modules", "x.js"), "ignored");
        var before = CodeSnapshots.Capture(settings, workspace);
        Check(before.Files.Length == 2 && before.Files.All(f => !f.Path.StartsWith("node_modules")), "snapshots skip excluded folders such as node_modules");
        File.WriteAllText(Path.Combine(workspace, "src", "a.txt"), "two");            // edited "by a command"
        File.WriteAllText(Path.Combine(workspace, "src", "new.txt"), "created");      // created
        File.Delete(Path.Combine(workspace, "keep.txt"));                             // deleted
        var after = CodeSnapshots.Capture(settings, workspace);
        var plan = CodeSnapshots.Plan(settings, workspace, after.Id, before.Id);
        Check(plan.Length == 3 && plan.All(p => !p.Conflict), "undo plan lists the edited, created and deleted files without conflicts");
        File.WriteAllText(Path.Combine(workspace, "src", "new.txt"), "user changed it later");
        var restored = CodeSnapshots.Restore(settings, workspace, after.Id, before.Id);
        Check(File.ReadAllText(Path.Combine(workspace, "src", "a.txt")) == "one" && File.ReadAllText(Path.Combine(workspace, "keep.txt")) == "keep",
            "undo restores an edited file and recreates a deleted one");
        Check(restored.Conflicts.SequenceEqual(["src/new.txt"]) && File.ReadAllText(Path.Combine(workspace, "src", "new.txt")) == "user changed it later",
            "undo leaves a file changed since the turn and reports it as a conflict");
        var redo = CodeSnapshots.Restore(settings, workspace, before.Id, after.Id);
        Check(File.ReadAllText(Path.Combine(workspace, "src", "a.txt")) == "two" && !File.Exists(Path.Combine(workspace, "keep.txt")), "redo reapplies the turn");
        var again = CodeSnapshots.Capture(settings, workspace);
        Check(again.Files.Single(f => f.Path == "src/a.txt").Hash == after.Files.Single(f => f.Path == "src/a.txt").Hash, "an unchanged file reuses its recorded hash");

        // G4: approval rules. Deny wins, prefix match, broad suggestions.
        CodeApprovalRules.Add(settings, workspace, new("run_command", "npm test", true, DateTimeOffset.UtcNow));
        CodeApprovalRules.Add(settings, workspace, new("run_command", "npm test --update", false, DateTimeOffset.UtcNow));
        var rules = CodeApprovalRules.Load(settings, workspace);
        Check(CodeApprovalRules.Match(rules, "run_command", "npm test -- --watch=false")?.Allow == true, "an allow rule matches commands by prefix");
        Check(CodeApprovalRules.Match(rules, "run_command", "npm test --update")?.Allow == false, "a deny rule wins over an allow rule");
        Check(CodeApprovalRules.Match(rules, "run_command", "npm install") == null && CodeApprovalRules.Match(rules, "edit_file", "npm test") == null, "other commands and tools still ask");
        Check(CodeApprovalRules.Suggest("run_command", "\"C:\\Program Files\\nodejs\\npm.cmd\" test --watch") == "\"C:\\Program Files\\nodejs\\npm.cmd\" test"
            && CodeApprovalRules.Suggest("edit_file", "src/app/main.ts") == "src/app/", "always-allow suggestions keep the program and first argument, or the folder");
        CodeApprovalRules.Remove(settings, workspace, rules[0]);
        Check(CodeApprovalRules.Load(settings, workspace).Count == 1, "a rule can be revoked");

        // G5: custom agents and commands.
        Directory.CreateDirectory(Path.Combine(workspace, ".mason", "commands")); Directory.CreateDirectory(Path.Combine(workspace, ".claude", "agents"));
        File.WriteAllText(Path.Combine(workspace, ".mason", "commands", "fix-issue.md"), "---\ndescription: Fix a numbered issue\n---\nFix issue $1. Context: $ARGUMENTS");
        File.WriteAllText(Path.Combine(workspace, ".mason", "commands", "new.md"), "reserved names are ignored");
        File.WriteAllText(Path.Combine(workspace, ".claude", "agents", "reviewer.md"), "---\nname: reviewer\ndescription: Reviews only\nmode: plan\n---\nNever edit files. Report risks.");
        var commands = CodeCustomization.Commands(settings, workspace);
        Check(commands.Select(c => c.Name).SequenceEqual(["fix-issue"]) && commands[0].Description == "Fix a numbered issue", "commands load with front matter and reserved names are skipped");
        Check(CodeCustomization.Expand(commands, "/fix-issue 42 \"login fails\"") == "Fix issue 42. Context: 42 \"login fails\"", "a command expands $1 and $ARGUMENTS");
        Check(CodeCustomization.Expand(commands, "/unknown 1") == null && CodeCustomization.Expand(commands, "fix-issue 1") == null, "unknown or non-slash input is left alone");
        var agent = CodeCustomization.Agents(settings, workspace).Single();
        Check(agent.Name == "reviewer" && agent.Mode == "ask" && agent.Source == ".claude/agents/reviewer.md", "agents from .claude/agents load and plan mode maps to Ask");
        var session = new CodeSession { Workspace = workspace, Agent = "reviewer" };
        var prepared = CodeCustomization.Prepare(settings, session, "/fix-issue 7");
        Check(prepared.Contains("Never edit files.") && prepared.EndsWith("Fix issue 7. Context: 7"), "the session agent frames an expanded command");

        // G7: schedules compute their next run in local time.
        var noon = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 27, 12, 0, 0))); // a Friday
        var weekday = new CodeSchedule(Guid.NewGuid(), "Nightly tests", workspace, "Run the tests", Guid.NewGuid(), false, ScheduleCadence.Weekdays, new TimeOnly(9, 30));
        Check(weekday.Next(noon).DayOfWeek == DayOfWeek.Monday && weekday.Next(noon).Hour == 9, "a weekday schedule skips the weekend");
        var hourly = weekday with { Cadence = ScheduleCadence.Hourly, At = new TimeOnly(0, 15) };
        Check(hourly.Next(noon).Hour == 12 && hourly.Next(noon).Minute == 15, "an hourly schedule runs at its minute");
        Check(CodeSchedules.Due([weekday with { LastRun = noon.AddDays(-7) }], noon, noon).Count() == 1 && !CodeSchedules.Due([weekday with { Enabled = false, LastRun = noon.AddDays(-7) }], noon, noon).Any(), "an overdue schedule is due once and a disabled one never");
        CodeSchedules.Save(settings, [weekday]);
        Check(CodeSchedules.Load(settings).Single().Name == "Nightly tests", "schedules persist in the profile");

        // G10: tool calls written as text are recovered only for offered tools.
        var tools = new HashSet<string> { "read_file", "run_command" };
        var tagged = ToolCallRecovery.TryParse("<tools>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.txt\"}}\n</tools>", tools);
        Check(tagged is { Name: "read_file" } && JsonDocument.Parse(tagged.Arguments).RootElement.GetProperty("path").GetString() == "a.txt", "a call in the wrong tags is recovered");
        Check(ToolCallRecovery.TryParse("```json\n{\"tool\":\"run_command\",\"parameters\":{\"executable\":\"git\"}}\n```", tools)?.Name == "run_command", "a fenced call with alternate keys is recovered");
        Check(ToolCallRecovery.TryParse("{\"name\":\"delete_everything\",\"arguments\":{}}", tools) == null, "a tool that was not offered is not recovered");
        Check(ToolCallRecovery.TryParse("<tool_call>{\"name\":\"read_file\",\"arguments\":{}}</tool_call><tool_call>{\"name\":\"read_file\",\"arguments\":{}}</tool_call>", tools) == null, "parallel calls are not recovered");
        Check(ToolCallRecovery.TryParse("Use the read_file tool on a.txt.", tools) == null, "prose stays prose");

        // G2: quick setup picks the backend for the GPU and the best quantization that fits.
        var nvidia = new GpuSample(DateTimeOffset.Now, [new("0", "NVIDIA GeForce RTX 5090 Laptop GPU", 0, 24463, 300, 24163)], [], null, null);
        Check(QuickSetup.Backend(nvidia) == "cuda-13.5" && QuickSetup.Backend(new GpuSample(DateTimeOffset.Now, [new("0", "AMD Radeon 780M", 0, 8192, 0, 8192)], [], null, null)) == "vulkan" && QuickSetup.Backend(null) == "cpu", "quick setup picks CUDA for NVIDIA, Vulkan for other GPUs and CPU otherwise");
        Check(QuickSetup.Quant("Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf") == "Q4_K_M" && QuickSetup.Quant("model-UD-IQ4_XS") == "IQ4_XS" && QuickSetup.Quant("Q8_0") == "Q8_0", "quantization names are read from variant names");
        HubVariant V(string name, double gib) => new(name, "GGUF", [new(name + ".gguf", (long)(gib * 1073741824), new string('a', 64), true)], "");
        var repo = new HubRepository("owner/coder-GGUF", new string('b', 40), "apache-2.0", false, [V("Q8_0", 8.1), V("Q6_K", 6.3), V("Q4_K_M", 4.7), V("mmproj-F16", .6), V("Q3_K_M", 3.8)]);
        Check(QuickSetup.Fit(repo, 20.5)?.Variant.Name == "Q8_0" && QuickSetup.Fit(repo, 9.0)?.Variant.Name == "Q6_K" && QuickSetup.Fit(repo, 8.5)?.Variant.Name == "Q4_K_M" && QuickSetup.Fit(repo, 7)?.Variant.Name == "Q4_K_M" && QuickSetup.Fit(repo, 3) == null, "quick setup takes the highest quality that fits and never a projector");

        // G8: fork keeps the conversation before the request; export redacts the workspace and secrets.
        using var store = new CodeStore(Path.Combine(root, "sessions"), settings);
        var original = store.New(workspace);
        original.Messages.Add(new("user", "first")); original.Messages.Add(new("assistant", "done"));
        var second = new CodeTurn { Text = "second request", MessageIndex = 2, SnapshotBefore = before.Id };
        original.Turns.Add(new CodeTurn { Text = "first", MessageIndex = 0 }); original.Turns.Add(second);
        original.Messages.Add(new("user", "second with key sk-abcdefghijklmnopqrstu in " + workspace)); store.Save(original);
        var (fork, _) = CodeSessionTransfer.Fork(store, original, second.Id, restoreFiles: false);
        Check(fork.Messages.Count == 2 && fork.Turns.Count == 1 && fork.Draft == "second request", "a fork keeps earlier messages and puts the request in the composer");
        var exported = CodeSessionTransfer.Export(original, sanitize: true);
        Check(!exported.Contains("sk-abcdefghijklmnopqrstu") && !exported.Contains(workspace.Replace("\\", "\\\\")) && exported.Contains("<workspace>"), "a sanitized export removes the workspace path and keys");
        var imported = CodeSessionTransfer.Import(store, exported, workspace);
        Check(imported.Messages.Count == 3 && imported.Turns.All(t => t.SnapshotBefore == null), "an export imports as a new session without undo history");

        // Line endings: a model's "\n" replacement still edits a CRLF checkout, and keeps the file CRLF.
        var crlf = Path.Combine(workspace, "crlf.py"); File.WriteAllText(crlf, "def f():\r\n    return 1\r\n\r\ndef g():\r\n    return 2\r\n");
        var scope = new CodeWorkspace(workspace); var read = scope.Read("crlf.py");
        scope.ApplyEdits("crlf.py", read.Sha256, [new("def f():\n    return 1\n", "def f():\n    return 10\n")], (_, _) => { });
        Check(File.ReadAllText(crlf) == "def f():\r\n    return 10\r\n\r\ndef g():\r\n    return 2\r\n", "an LF replacement edits a CRLF file and keeps its line endings");
        try { scope.ApplyEdits("crlf.py", scope.Read("crlf.py").Sha256, [new("def missing():\n", "x")], (_, _) => { }); Check(false, "a replacement that matches nowhere is still rejected"); }
        catch (CodeSnippetMismatchException) { Check(true, "a replacement that matches nowhere is still rejected"); }

        // Verification: a python_test check pins only its own test, so adding the requested test elsewhere in the file
        // keeps it valid; a misnamed class resolves to the real one; a test the request asks for is found and run.
        Directory.CreateDirectory(Path.Combine(workspace, "tests"));
        var testFile = Path.Combine(workspace, "tests", "test_ledger.py");
        File.WriteAllText(testFile, "import unittest\r\n\r\n\r\nclass ParseRowsTests(unittest.TestCase):\r\n    def test_one(self):\r\n        self.assertEqual(1, 1)\r\n");
        var one = CodePythonTests.Find(File.ReadAllText(testFile), "test_one");
        Check(one.Count == 1 && one[0].Class == "ParseRowsTests", "a test method is located with its real class");
        Check(CodePythonTests.RequestsNewTest("Fix the importer and add a regression test.") && !CodePythonTests.RequestsNewTest("Fix the importer; don't change the tests."), "a request to write a test is recognised");
        var pinnedPlan = new CodeAcceptancePlan { Request = "r", Units = ["r"] };
        pinnedPlan.Checks.Add(new(1, "one", "python_test", "tests/test_ledger.py", "ParseRowsTests", "test_one"));
        var testSource = File.ReadAllText(testFile); var testNames = CodePythonTests.Names(testSource);
        pinnedPlan.TestHashes[CodePythonTests.NamesKey("tests/test_ledger.py")] = string.Join(",", testNames);
        pinnedPlan.TestHashes[CodePythonTests.StructureKey("tests/test_ledger.py")] = CodePythonTests.Structure(testSource, testNames);
        File.AppendAllText(testFile, "\r\n    def test_two(self):\r\n        self.assertTrue(True)\r\n");
        CodeAcceptance.Verify(pinnedPlan, scope, CodePermission.Auto, default).GetAwaiter().GetResult();
        Check(pinnedPlan.Results.Single().Passed, "adding another test to the file keeps a pinned check valid");
        File.WriteAllText(testFile, File.ReadAllText(testFile).Replace("self.assertEqual(1, 1)", "self.assertTrue(True)"));
        CodeAcceptance.Verify(pinnedPlan, scope, CodePermission.Auto, default).GetAwaiter().GetResult();
        Check(!pinnedPlan.Results.Single().Passed && pinnedPlan.Results.Single().Detail.Contains("changed after planning"), "changing the pinned test itself still voids the check");
        File.WriteAllText(testFile, testSource + "\r\n\r\nclass ParseRowsTests(unittest.TestCase):\r\n    def test_one(self):\r\n        pass\r\n");
        CodeAcceptance.Verify(pinnedPlan, scope, CodePermission.Auto, default).GetAwaiter().GetResult();
        Check(!pinnedPlan.Results.Single().Passed, "a class that shadows the pinned test voids the check");
        File.WriteAllText(testFile, "import builtins\r\n" + testSource);
        CodeAcceptance.Verify(pinnedPlan, scope, CodePermission.Auto, default).GetAwaiter().GetResult();
        Check(!pinnedPlan.Results.Single().Passed, "a changed import in a pinned test file voids the check");
        File.WriteAllText(testFile, testSource + "\r\n\r\nclass ExtraTests(unittest.TestCase):\r\n    \"\"\"Added by the task.\"\"\"\r\n    def test_extra(self):\r\n        self.assertTrue(True)\r\n");
        CodeAcceptance.Verify(pinnedPlan, scope, CodePermission.Auto, default).GetAwaiter().GetResult();
        Check(pinnedPlan.Results.Single().Passed, "a new test-only class keeps a pinned check valid");
        File.WriteAllText(testFile, testSource);
        var newPlan = new CodeAcceptancePlan { Request = "r", Units = ["r"] };
        newPlan.Checks.Add(new(1, "three", "python_test", "tests/test_ledger.py", "TestImporter", "test_three") { NewTest = true });
        CodeAcceptance.Verify(newPlan, scope, CodePermission.Auto, default).GetAwaiter().GetResult();
        Check(!newPlan.Results.Single().Passed && newPlan.Results.Single().Detail.Contains("was not found"), "a requested test that was never written fails");
        File.AppendAllText(testFile, "\r\n    def test_three(self):\r\n        self.assertIn(',', 'a,b')\r\n");
        CodeAcceptance.Verify(newPlan, scope, CodePermission.Auto, default).GetAwaiter().GetResult();
        Check(newPlan.Results.Single().Passed && newPlan.Results.Single().Detail.Contains("ParseRowsTests.test_three · new test written in this task"), "a requested test runs under its real class and is reported as new");

        // A probe whose own text could not be parsed is not an outstanding failure; a real test failure still is.
        Check(CodeCommandProbe.Malformed("{\"ExitCode\":1,\"Output\":\"  File \\u0022<string>\\u0022, line 1\\r\\n    rows = parse_rows([\\u0027a\\r\\n                     ^\\r\\nSyntaxError: unterminated string literal\"}"), "an inline python -c syntax error is recognised as a malformed probe");
        Check(!CodeCommandProbe.Malformed("{\"ExitCode\":1,\"Output\":\"FAIL: test_one (tests.test_ledger.ParseRowsTests)\\r\\nAssertionError: 1 != 2\"}"), "a failing test is not treated as a malformed probe");
        try { Directory.Delete(root, true); } catch (IOException) { }
        Console.WriteLine("gap feature checks passed");
    }
}
