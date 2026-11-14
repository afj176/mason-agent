using Mason.Code;
using System.Text.Json;

static class CodingShellChecks
{
    static CodeAction Action(string name, object arguments) => new("Executing the requested fixture step.", name, JsonSerializer.SerializeToElement(arguments));
    public static async Task Run(string root, string workspace, Action<bool, string> check)
    {
        check(new CodeSession().Permission == CodePermission.ReadOnly && new CodeSession().Shell == CodeShellKind.Direct, "legacy/default sessions never opt into Auto or an implicit shell");
        if (!OperatingSystem.IsWindows()) return;
        var state = Path.Combine(root, "shell-policies");
        var bash = CodeShells.Find(CodeShellKind.GitBash) ?? throw new Exception("Git Bash is required for this Windows shell acceptance check.");
        using (var store = new CodeStore(state))
        {
            var session = store.New(workspace); session.Shell = CodeShellKind.GitBash; session.ShellExecutable = bash; session.Permission = CodePermission.WorkspaceEdits;
            int approvals = 0;
            var command = Action("run_shell", new { command = "printf '%s\\n' 'shell value with spaces' > 'shell output.txt'; pwd; cat 'shell output.txt'", directory = "." });
            await new CodeEngine(store, new Actions(command), (_, _) => { approvals++; return Task.FromResult(true); }, () => { }).Run(session, "Exercise Git Bash in this disposable workspace.", default);
            check(session.State == "Completed" && approvals == 1 && File.ReadAllText(Path.Combine(workspace, "shell output.txt")).Contains("shell value with spaces"), "selected Git Bash executes quoting, redirection and a real workspace write after one review: " + session.Detail + " / " + JsonSerializer.Serialize(session.Activities));
            check(session.Activities[0].Authorization == "Allowed once" && session.Activities[0].Result.Contains("shell value with spaces"), "review decision and actual shell stdout are retained");
            session.Permission = CodePermission.Auto;
            await new CodeEngine(store, new Actions(Action("run_shell", new { command = "printf 'auto-bash-ok' > 'auto bash output.txt'; cat 'auto bash output.txt'", directory = "." })), (_, _) => throw new Exception("Auto unexpectedly requested review"), () => { }).Run(session, "Verify admitted Git Bash Auto or fail-closed rejection.", default);
            var autoBash=session.Activities.Last(a=>a.Tool=="run_shell");
            check(CodeSandboxTools.GitBashAutoAvailable
                ? autoBash.State=="Completed" && autoBash.Result.Contains("auto-bash-ok") && File.ReadAllText(Path.Combine(workspace,"auto bash output.txt"))=="auto-bash-ok" && approvals==1
                : autoBash.State=="Denied" && autoBash.Result.Contains("No unrestricted fallback") && !File.Exists(Path.Combine(workspace,"auto bash output.txt")),
                "Git Bash Auto executes an admitted sandboxed shell without review, or fails closed without a file side effect");
            session.Shell = CodeShellKind.CommandPrompt; session.ShellExecutable = CodeShells.Find(CodeShellKind.CommandPrompt);
            await new CodeEngine(store, new Actions(Action("open_preview", new { url = "http://127.0.0.1:5173/" })), (_, _) => throw new Exception(), () => { }).Run(session, "Verify Auto preview policy.", default);
            check(session.Activities.Last(a=>a.Tool=="open_preview").State=="Denied"&&!session.Tabs.Any(t=>t.Kind=="web"), "Auto cannot bypass network isolation through agent-initiated web previews");
            var cmd = CodeCommand.ResolveExecutable("cmd.exe", workspace);
            await new CodeEngine(store, new Actions(Action("run_command", new { executable = cmd, arguments = new[] { "/d", "/c", "echo auto-direct-ok" }, directory = "." })), (_, _) => throw new Exception("Auto unexpectedly requested review"), () => { }).Run(session, "Run the Auto executable fixture.", default);
            check(session.State == "Completed" && session.Activities.Last().Result.Contains("auto-direct-ok"), "Auto also supports direct executable tools");
            await new CodeEngine(store, new Actions(Action("run_shell", new { command = "exit /b 7", directory = "." })), (_, _) => throw new Exception(), () => { }).Run(session, "Record a failing shell command.", default);
            check(session.Activities.Last(a => a.Tool == "run_shell").State == "Failed" && session.Activities.Last(a => a.Tool == "run_shell").Result.Contains("\"ExitCode\":7"), "Auto retains a failed exit code rather than claiming success");
            await new CodeEngine(store, new Actions(Action("create_file", new { path = "../escape-auto.txt", content = "must not write" }), Action("run_shell", new { command = "printf 'must-not-run'", directory = ".." })), (_, _) => throw new Exception(), () => { }).Run(session, "Verify Auto path validation.", default);
            check(session.Activities.Where(a => a.Tool is "run_shell" or "create_file").TakeLast(2).All(a => a.State == "Denied") && !File.Exists(Path.Combine(root, "escape-auto.txt")), "Auto does not bypass file-tool or command-working-directory traversal guards");
            session.Mode = "ask";
            await new CodeEngine(store, new Actions(Action("run_shell", new { command = "printf 'must-not-run'", directory = "." }), Action("create_file", new { path = "ask-must-not-write.txt", content = "bad" })), (_, _) => throw new Exception(), () => { }).Run(session, "Ask stays read-only with Auto retained.", default);
            check(session.Activities.Where(a => a.Tool is "run_shell" or "create_file").TakeLast(2).All(a => a.State == "Denied") && !File.Exists(Path.Combine(workspace, "ask-must-not-write.txt")), "Ask blocks commands and writes even when Auto was previously selected");
            session.Mode = "build"; session.Permission = CodePermission.ReadOnly;
            await new CodeEngine(store, new Actions(Action("run_shell", new { command = "printf 'must-not-run'", directory = "." })), (_, _) => throw new Exception(), () => { }).Run(session, "Read-only blocks shell commands.", default);
            check(session.Activities.Last(a => a.Tool == "run_shell").State == "Denied", "Read-only blocks selected shell execution before review");
            session.Permission = CodePermission.WorkspaceEdits;
            await new CodeEngine(store, new Actions(Action("run_shell", new { command = "touch 'declined-must-not-exist'", directory = "." })), (_, _) => Task.FromResult(false), () => { }).Run(session, "Decline this fixture command.", default);
            check(session.Activities.Last(a => a.Tool == "run_shell").State == "Denied" && session.Activities.Last(a => a.Tool == "run_shell").Authorization == "Declined" && !File.Exists(Path.Combine(workspace, "declined-must-not-exist")), "declined shell command has no side effects and records the decision");
            session.Permission = CodePermission.Auto; session.Shell = CodeShellKind.GitBash; session.ShellExecutable = bash; store.Save(session);
            var next = store.New(workspace);
            check(next.Shell == CodeShellKind.GitBash && next.ShellExecutable == bash && next.Permission == CodePermission.ReadOnly, "new workspace sessions inherit shell preference without inheriting Auto permission");
            var invalid = new CodeSession { Workspace = workspace, Permission = (CodePermission)999 };
            bool rejected = false;
            try { await new CodeEngine(store, new Actions(), (_, _) => Task.FromResult(true), () => { }).Run(invalid, "Reject invalid permission.", default); } catch (ArgumentException) { rejected = true; }
            check(rejected, "unknown permission values are rejected before any tool executes");
            foreach (var kind in new[] { CodeShellKind.PowerShell, CodeShellKind.CommandPrompt })
            {
                var executable = CodeShells.Find(kind) ?? throw new Exception("Missing Windows shell " + kind);
                var host = new CodeSession { Shell = kind, ShellExecutable = executable };
                var resolved = CodeShells.Command(host, kind == CodeShellKind.PowerShell ? "Write-Output 'powershell-ok'" : "echo cmd-shell-ok");
                var result = await CodeCommand.Run(resolved.Executable, resolved.Arguments, workspace, default);
                check(result.ExitCode == 0 && result.Output.Contains(kind == CodeShellKind.PowerShell ? "powershell-ok" : "cmd-shell-ok"), "actual selected " + CodeShells.Name(kind) + " receives its correct invocation arguments");
            }
            bool missing = false;
            try { CodeShells.Command(new() { Shell = CodeShellKind.GitBash, ShellExecutable = Path.Combine(workspace, "missing.exe") }, "echo nope"); } catch (InvalidOperationException) { missing = true; }
            check(missing, "missing selected shell fails explicitly without a fallback");
            bool absent = false;
            try { CodeShells.Command(new(), "ls"); } catch (InvalidOperationException) { absent = true; }
            check(absent, "Direct executables mode cannot silently invoke an implicit shell");
            check(CodeShells.Describe(session).Contains("Windows") && CodeShells.Describe(session).Contains(bash) && CodeSchema.Action().GetRawText().Contains("run_shell"), "agent receives platform, exact preferred shell and schema-supported shell tool");
        }
        using var reopened = new CodeStore(state);
        check(reopened.Sessions.Any(s => s.Permission == CodePermission.Auto && s.Shell == CodeShellKind.GitBash && s.Activities.Any(a => a.Authorization.StartsWith("Auto"))), "Auto, preferred shell and command authorization receipts survive restart");
    }
    sealed class Actions(params CodeAction[] actions) : ICodeModel
    {
        int index;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation)
            => Task.FromResult(index < actions.Length ? actions[index++] : new CodeAction("Fixture steps finished; inspect receipts for any failures.", null, default));
    }
}

