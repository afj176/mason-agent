using System.Text.Json;

namespace Mason.Code;

/// <summary>One bounded, sequential agent loop. Tool intents are persisted before any side effect.</summary>
public sealed class CodeEngine(CodeStore store, ICodeModel model,
    Func<CodeApproval, CancellationToken, Task<bool>> approve, Action changed,Action<Exception>? diagnostic=null)
{
    static string S(JsonElement a, string key) => a.GetProperty(key).GetString() ?? throw new ArgumentException(key + " must be text.");
    public async Task Run(CodeSession session, string input, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 16_000) throw new ArgumentException("Describe a change in 1–16,000 characters.");
        if (session.Mode is not ("build" or "ask")) throw new ArgumentException("Choose Build or Ask mode.");
        if (session.State is "Running" or "Awaiting review") throw new InvalidOperationException("This session already has an active run.");
        if (!Enum.IsDefined(session.Permission) || !Enum.IsDefined(session.Shell)) throw new ArgumentException("Choose a supported permission mode and shell.");
        if(session.ActionLimit is <1 or >128)throw new ArgumentException("Choose an action budget from 1 to 128 responses.");
        if(OperatingSystem.IsWindows()&&session.Mode=="build"&&session.Permission==CodePermission.Auto&&(CodeSandbox.ContainsHost(session.Workspace,AppContext.BaseDirectory)||CodeSandbox.ContainsHost(session.Workspace,store.Root)||CodeSandbox.ContainsHost(session.Workspace,CodeSandboxTools.CacheRoot)||CodeSandbox.ContainsHost(session.Workspace,CodeHostCoordination.Root)))
            throw new UnauthorizedAccessException("Auto workspace cannot contain the harness binaries or session store. Select a project outside those protected locations.");
        var scope = new CodeWorkspace(session.Workspace);
        var listing = await Task.Run(() => scope.Files(".", 120), cancellation);
        var instructions = scope.Instructions();
        var references = session.References.Select(p => { var file = scope.Read(p); return new { file.Path, file.Content, file.Sha256, instructions = scope.Instructions(p) }; }).ToArray();
        int firstActivity = session.Activities.Count;
        var user = new CodeTurn { Text = input, FirstActivity = firstActivity }; session.Turns.Add(user);
        session.Messages.Add(new("user", JsonSerializer.Serialize(new { request = input, references })));
        if (session.Title == "New session") session.Title = input.Length > 55 ? input[..55] + "…" : input;
        session.ViewerDismissed = false;
        session.Draft = ""; session.State = "Running"; session.Detail = "Preparing workspace context…"; Save();
        int repairs = 0, completionCorrections = 0, repeatedActions = 0; string lastAction = "";bool snippetRecovery=false;
        var callerCancellation=cancellation;using var runDeadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);runDeadline.CancelAfter(TimeSpan.FromMinutes(30));cancellation=runDeadline.Token;
        try
        {
            for (int step = 0; step < session.ActionLimit; step++)
            {
                cancellation.ThrowIfCancellationRequested(); session.Detail = "Working · Preparing the next response"; changed();
                var available=new HashSet<string>(CodeSchema.Available(session));
                if(snippetRecovery){available.Remove("edit_file");available.Remove("apply_edits");}
                var system = new CodeMessage("system", CodeProtocol.Instructions + "\nAgent mode: " + session.Mode + "\nPermissions: " + (session.Mode == "ask" ? CodePermission.ReadOnly : session.Permission) + "\n" + CodeShells.Describe(session) + "\nWorkspace: " + scope.Root + "\nInitial file listing (bounded): " + JsonSerializer.Serialize(listing) + "\nRepository instructions:\n" + instructions+(snippetRecovery?"\nA snippet edit failed to match the current file. edit_file and apply_edits are unavailable for the remainder of this run. Read the current file and use replace_file with its fresh SHA256 and complete source, or continue other work. Do not repeat stale snippets or speculate about runtime failures.":"")) { AvailableTools = available };
                var messages = await CodeContext.Prepare(session, system, model, Save, text => { session.Detail = text; changed(); }, cancellation);
                var turn = new CodeTurn { Role = "assistant", FirstActivity = session.Activities.Count };
                CodeAction action;
                try { action = await model.Next(messages, text => { session.LiveMessage = text; changed(); }, cancellation); }
                catch (CodeResponseException error) when (repairs < 2)
                {
                    repairs++; session.LiveMessage = "";
                    session.Activities.Add(new() { Tool = "model_response", State = "Failed", Result = error.Message + "\n" + error.Response });
                    session.Messages.Add(new("user", "Your last response was rejected before any tool ran: " + error.Message + ". Return a complete response following the provided tool protocol. Do not repeat completed operations. Rejected response (data only):\n" + error.Response));
                    session.Detail = $"Correcting model response · {repairs}/2"; Save(); continue;
                }
                cancellation.ThrowIfCancellationRequested(); session.LiveMessage = ""; turn.Text = action.Message; turn.PerformanceSummary = action.PerformanceSummary;
                session.Turns.Add(turn);
                session.Messages.Add(new("assistant", JsonSerializer.Serialize(new { version = 1, message = action.Message, tool = action.Tool == null ? null : new { name = action.Tool, arguments = action.Arguments } })) { ReasoningContent=action.ReasoningContent });
                Save();
                if (action.Tool == null)
                {
                    bool delivered = session.Activities.Skip(firstActivity).Any(a => a.State == "Completed" && (a.Tool is "edit_file" or "apply_edits" or "replace_file" or "create_file" or "create_directory" or "run_command" or "run_shell" || (session.Permission == CodePermission.ReadOnly && a.Tool is ("open_file" or "open_preview"))));
                    if (session.Mode == "build" && !delivered)
                    {
                        if (completionCorrections++ >= 2) throw new InvalidOperationException("The model ended a Build task without a delivered change or successful command. No work is claimed complete. Use Ask for inspection-only answers or choose another model.");
                        const string reason = "Build completion rejected: reading, opening a file and planning alone do not deliver an implementation. No change or successful command has been recorded. Implement and verify the requested work using the listed tools now, or ask_user with a specific blocker. Do not return tool:null with intentions, reasoning or an unfinished plan. Use the tools; do not stop after summarizing requirements.";
                        session.Activities.Add(new() { Tool = "completion_check", State = "Failed", Result = reason });
                        session.Messages.Add(new("user", reason)); session.Detail = "Continuing · missing tool evidence"; Save(); continue;
                    }
                    session.State = "Completed"; int changedFiles = session.Changes.Count(c => !c.Restored); session.Detail = "Completed · " + changedFiles + (changedFiles == 1 ? " file changed" : " files changed"); Save(); return;
                }
                if (action.Tool == "ask_user")
                {
                    CodeProtocol.Exact(action.Arguments, "question"); var question = S(action.Arguments, "question");
                    if (string.IsNullOrWhiteSpace(question) || question.Length > 4000) throw new ArgumentException("Ask a specific question in 1–4,000 characters.");
                    if (!turn.Text.Contains(question, StringComparison.Ordinal)) turn.Text += "\n\n" + question;
                    session.Messages.Add(new("user", "The agent requested user input: " + question + ". Wait for the next user message."));
                    session.State = "Waiting for input"; session.Detail = "Waiting for your answer"; Save(); return;
                }
                var activity = new CodeActivity { Tool = action.Tool, Arguments = action.Arguments.GetRawText() }; session.Activities.Add(activity); turn.ActivityCount = 1;
                session.Detail = "Working · " + action.Tool.Replace('_', ' '); Save();
                string fingerprint=activity.Tool+":"+activity.Arguments;
                repeatedActions=fingerprint==lastAction?repeatedActions+1:1;lastAction=fingerprint;
                CodeToolResult result;
                try
                {
                    if(snippetRecovery&&action.Tool is "edit_file" or "apply_edits")throw new InvalidOperationException("Snippet edits are paused after a conflict. Read the current file and use replace_file with its fresh SHA256.");
                    if (session.Activities.Skip(firstActivity).Any(a => a != activity && a.State == "Denied" && a.Tool == activity.Tool && a.Arguments == activity.Arguments))
                        throw new UnauthorizedAccessException("This exact operation was already declined during this run. Ask the user for a new instruction instead of repeating it.");
                    if(repeatedActions>=3)throw new InvalidOperationException("The same tool request was repeated three times without an intervening action. Execution stopped before this repeat; inspect the receipts and change strategy.");
                    result = await Execute(scope, session, action, activity, cancellation);
                    activity.State = result.Failed ? "Failed" : "Completed"; activity.Result = result.Content;
                    if (result.ArtifactKind != null && result.ArtifactTarget != null)
                    {
                        var tab = new CodeTab { Kind = result.ArtifactKind, Target = result.ArtifactTarget };
                        CodePanels.Open(session, tab, user: false);
                    }
                }
                catch (OperationCanceledException) { activity.State = "Interrupted"; activity.Result = "Operation cancelled. Inspect workspace changes before retrying."; Save(); throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    if(action.Tool is "edit_file" or "apply_edits"&&error.Message.StartsWith("Each old_text must match exactly once",StringComparison.Ordinal))snippetRecovery=true;
                    activity.State = error is UnauthorizedAccessException ? "Denied" : "Failed"; activity.Result = error.Message;
                    result = new(JsonSerializer.Serialize(new { error = error.Message, state = activity.State }));
                }
                session.Messages.Add(new("user", JsonSerializer.Serialize(new { tool_result = action.Tool, activity_id = activity.Id, state = activity.State, result = result.Content })));
                Save();
                if(result.RuntimeFailure is {} failure)
                {
                    session.State="Blocked";session.Detail="Validation blocked · "+failure.Summary+" Completed files are retained; automatic retries stopped.";
                    Save();return;
                }
                if(repeatedActions>=3)throw new InvalidOperationException("Stopped a repeated-tool loop after three identical requests. Completed work and activity are retained; inspect the failure and continue with a different approach.");
            }
            throw new InvalidOperationException($"Stopped at the {session.ActionLimit}-response run limit. Inspect the activity and continue with a new instruction.");
        }
        catch (OperationCanceledException) { session.LiveMessage = ""; session.State = "Stopped"; session.Detail = callerCancellation.IsCancellationRequested?"Stopped · completed edits are retained. Pending operations will not be replayed.":"Stopped at the 30-minute run budget · completed edits and receipts are retained."; Save(); }
        catch (Exception error) { try{diagnostic?.Invoke(error);}catch{ /* Diagnostics cannot suppress the failure receipt. */ }session.LiveMessage = ""; session.State = "Failed"; session.Detail = error.Message; Save(); }
        void Save() { session.Updated = DateTimeOffset.UtcNow; store.Save(session); changed(); }
    }
    public async Task Compact(CodeSession session, CancellationToken cancellation)
    {
        if (session.State is "Running" or "Awaiting review") throw new InvalidOperationException("Stop the current run before compacting manually.");
        if (!Enum.IsDefined(session.Permission) || !Enum.IsDefined(session.Shell)) throw new ArgumentException("Choose a supported permission mode and shell.");
        var scope = new CodeWorkspace(session.Workspace);
        var listing = await Task.Run(() => scope.Files(".", 120), cancellation);
        var system = new CodeMessage("system", CodeProtocol.Instructions + "\nAgent mode: " + session.Mode + "\nPermissions: " + (session.Mode == "ask" ? CodePermission.ReadOnly : session.Permission) + "\n" + CodeShells.Describe(session) + "\nWorkspace: " + scope.Root + "\nInitial file listing (bounded): " + JsonSerializer.Serialize(listing) + "\nRepository instructions:\n" + scope.Instructions()) { AvailableTools = CodeSchema.Available(session) };
        string previousState = session.State, previousDetail = session.Detail;
        session.State = "Running"; session.Detail = "Compacting context · original history retained"; Save();
        try
        {
            if (session.Messages.Count == 0) throw new InvalidOperationException("There is no coding history to compact yet.");
            _ = await CodeContext.Prepare(session, system, model, Save, text => { session.Detail = text; changed(); }, cancellation, force: true);
            session.State = previousState; session.Detail = previousDetail; Save();
        }
        catch (OperationCanceledException) { session.State = previousState; session.Detail = "Compaction stopped · history and previous checkpoints retained"; Save(); }
        catch (Exception error) { session.State = previousState; session.Detail = error.Message; Save(); }
        void Save() { session.Updated = DateTimeOffset.UtcNow; store.Save(session); changed(); }
    }
    async Task<CodeToolResult> Execute(CodeWorkspace scope, CodeSession session, CodeAction action, CodeActivity activity, CancellationToken ct)
    {
        var a = action.Arguments; var tool = action.Tool!;
        switch (tool)
        {
            case "list_files": CodeProtocol.Exact(a, "path", "limit"); return new(JsonSerializer.Serialize(await Task.Run(() => scope.Files(S(a, "path"), a.GetProperty("limit").GetInt32()), ct)));
            case "search_text": CodeProtocol.Exact(a, "query", "path"); return new(JsonSerializer.Serialize(await Task.Run(() => scope.Search(S(a, "query"), S(a, "path")), ct)));
            case "read_file": case "open_file":
                CodeProtocol.Exact(a, "path"); var file = scope.Read(S(a, "path"));
                return new(JsonSerializer.Serialize(new { file.Path, file.Content, file.Sha256, instructions = scope.Instructions(file.Path) }), tool == "open_file" ? "file" : null, file.Path);
            case "open_preview":
                if (session.Permission == CodePermission.Auto) throw new UnauthorizedAccessException("Auto blocks agent-initiated website navigation. Use the Website button manually to inspect an approved local page; no preview was loaded.");
                CodeProtocol.Exact(a, "url"); var uri = CodeProtocol.PreviewUri(S(a, "url")); return new("Preview tab opened. Browser contents have not been inspected by the agent.", "web", uri.AbsoluteUri);
            case "create_directory":
                CodeProtocol.Exact(a, "path");
                if (session.Mode == "ask" || session.Permission == CodePermission.ReadOnly) throw new UnauthorizedAccessException("Ask/read-only mode does not allow directory creation.");
                var folder = scope.Relative(scope.Resolve(S(a, "path")));
                if (session.Permission == CodePermission.ReviewEdits) await Review(new(tool, "Create directory " + folder, "Create missing directory components inside the selected workspace. Existing files are preserved."));
                ct.ThrowIfCancellationRequested(); return new(JsonSerializer.Serialize(new { path = scope.CreateDirectory(folder), state = "Directory available" }));
            case "edit_file": case "apply_edits": case "replace_file": case "create_file":
                if (session.Mode == "ask" || session.Permission == CodePermission.ReadOnly) throw new UnauthorizedAccessException("Ask/read-only mode does not allow file writes. Ask the user to change the mode.");
                bool editing = tool != "create_file";
                if (tool == "edit_file") CodeProtocol.Exact(a, "path", "expected_sha256", "old_text", "new_text");
                else if (tool == "apply_edits") CodeProtocol.Exact(a, "path", "expected_sha256", "edits");
                else if (tool == "replace_file") CodeProtocol.Exact(a, "path", "expected_sha256", "content");
                else CodeProtocol.Exact(a, "path", "content");
                var replacements = tool == "apply_edits" ? a.GetProperty("edits").EnumerateArray().Select(e => { CodeProtocol.Exact(e, "old_text", "new_text"); return new CodeReplacement(S(e, "old_text"), S(e, "new_text")); }).ToArray() : tool == "edit_file" ? new[] { new CodeReplacement(S(a, "old_text"), S(a, "new_text")) } : [];
                string path = scope.Relative(scope.Resolve(S(a, "path")));
                var prior = editing ? scope.Read(path) : null;
                if (editing && prior!.Sha256 != S(a, "expected_sha256")) throw new IOException("The file changed; read it again before editing.");
                if (tool == "replace_file")
                {
                    var content = S(a, "content");
                    if (prior!.Content.Contains("\r\n") && !prior.Content.Replace("\r\n", "").Contains('\n')) content = content.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    else if (!prior.Content.Contains('\r')) content = content.Replace("\r\n", "\n");
                    replacements = [new(prior.Content, content)];
                }
                if (session.Permission == CodePermission.ReviewEdits)
                    await Review(new(tool, (editing ? "Edit " : "Create ") + path, editing ? string.Join("\n\n", replacements.Select(e => "Replace exactly:\n" + e.OldText + "\nWith:\n" + e.NewText)) : S(a, "content")));
                ct.ThrowIfCancellationRequested();
                var change = session.Changes.FirstOrDefault(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && !c.Restored);
                var created = change == null;
                if (change == null) { change = new() { Path = path, Before = prior?.Content, BeforeBom = prior?.Bom == true }; session.Changes.Add(change); }
                try
                {
                    var edited = editing ? scope.ApplyEdits(path, S(a, "expected_sha256"), replacements, (_, next) => { change.After = next; store.Save(session); }) : scope.Create(path, S(a, "content"));
                    change.After = edited.Content; change.AfterHash = edited.Sha256;
                    return new(JsonSerializer.Serialize(new { edited.Path, edited.Sha256, state = "Written" }), "changes", "");
                }
                catch { if (created && change.AfterHash.Length == 0) session.Changes.Remove(change); throw; }
            case "run_shell":
            case "run_command":
                if (tool == "run_shell") CodeProtocol.Exact(a, "command", "directory");
                else CodeProtocol.Exact(a, "executable", "arguments", "directory");
                if (session.Mode == "ask" || session.Permission == CodePermission.ReadOnly) throw new UnauthorizedAccessException("Ask/read-only mode does not allow commands.");
                var directory = scope.Resolve(S(a, "directory"), true);
                if (tool == "run_shell" && session.Permission == CodePermission.Auto && CodeShells.Unavailable(session) is { } unavailable)
                    throw new UnauthorizedAccessException(unavailable + " No unrestricted fallback was run.");
                var shellCommand = tool == "run_shell" ? CodeShells.Command(session, S(a, "command")) : null;
                var executable = shellCommand?.Executable ?? CodeCommand.ResolveExecutable(S(a, "executable"), scope.Root);
                var arguments = shellCommand?.Arguments ?? a.GetProperty("arguments").EnumerateArray().Select(p => p.GetString() ?? throw new ArgumentException("Command arguments must be strings.")).ToArray();
                if (arguments.Length > 40 || arguments.Sum(p => p.Length) > 8000) throw new ArgumentException("Command arguments exceed the resource bound.");
                activity.Authorization = session.Permission == CodePermission.Auto ? "Auto · Windows LPAC workspace sandbox · "+(session.Network==CodeNetworkMode.Ask?"network asks per destination":"network offline") : "Review required";
                if (session.Permission != CodePermission.Auto) await Review(new(tool, CodeCommand.CommandLine(executable, arguments), "Working directory: " + directory + "\n\nRuns with your Windows account and inherited environment. Workspace file restrictions do not isolate this command. Deadline: 120 seconds. Interactive input is unavailable."));
                ct.ThrowIfCancellationRequested(); activity.State = "Running"; store.Save(session); changed();
                if (session.Permission == CodePermission.Auto) { session.Detail = "Preparing isolated Auto command · workspace writes only"; changed(); }
                var context=SynchronizationContext.Current;
                async Task<bool> NetworkApproval(string host,CancellationToken token)
                {
                    async Task<bool> Ask()
                    {
                        session.State="Awaiting review";session.Detail="Network access · "+host+":443";store.Save(session);changed();
                        try{return await approve(new("network", "Allow "+host+":443 for this command?",CodeCommand.CommandLine(executable,arguments)+"\n\nThe command remains inside the workspace sandbox. This allows sending data to this destination for this command only; it does not grant access to other hosts, local services or files."),token);}
                        finally{session.State="Running";session.Detail="Running sandboxed command";changed();}
                    }
                    if(context==null)return await Ask();
                    var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    context.Post(async _=>{try{completion.TrySetResult(await Ask());}catch(Exception error){completion.TrySetException(error);}},null);
                    return await completion.Task.WaitAsync(token);
                }
                var networking=session.Permission==CodePermission.Auto&&session.Network==CodeNetworkMode.Ask?new CodeNetworkAccess(NetworkApproval):null;
                var command = await CodeCommand.Run(executable, arguments, directory, ct, session.Permission == CodePermission.Auto ? scope.Root : null,networking);
                if(networking!=null)activity.Authorization="Auto · workspace sandbox · "+string.Join("; ",networking.Receipts);
                return new(JsonSerializer.Serialize(command), Failed: command.ExitCode != 0 || command.Truncated,RuntimeFailure:command.Failure);
            default: throw new ArgumentException("Unsupported coding tool: " + tool);
        }
        async Task Review(CodeApproval request)
        {
            session.State = "Awaiting review"; session.Detail = "Review required · " + request.Tool.Replace('_', ' '); store.Save(session); changed();
            bool allowed = await approve(request, ct); ct.ThrowIfCancellationRequested(); session.State = "Running";
            activity.Authorization = allowed ? "Allowed once" : "Declined";
            if (!allowed) throw new UnauthorizedAccessException("The user declined this operation. Do not repeat it without new user instructions.");
        }
    }
}
