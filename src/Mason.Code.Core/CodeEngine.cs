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
        bool refreshInventory = false;
        var references = session.References.Select(p => { var file = scope.Read(p); return new { file.Path, file.Content, file.Sha256, instructions = scope.Instructions(p) }; }).ToArray();
        int firstActivity = session.Activities.Count;
        var user = new CodeTurn { Text = input, FirstActivity = firstActivity }; session.Turns.Add(user);
        var priorState = session.State;
        string? handoff = session.State is "Blocked" or "Interrupted" or "Failed" or "Stopped" or "Needs verification"
            ? $"Previous run: {session.State}. Current workspace root: '.'. Use the current file inventory to resume. " +
              (listing.RootIsOnlyDirectory && listing.Files.Length == 0 ? "The selected project exists and is empty. Create the requested files here using bare filenames." : "")
            : null;
        session.Messages.Add(new("user", handoff == null ? JsonSerializer.Serialize(new { request = input, references })
            : JsonSerializer.Serialize(new { request = input, references, host_resume_context = handoff })));
        if (session.Title == "New session") session.Title = input.Length > 55 ? input[..55] + "…" : input;
        session.ViewerDismissed = false;
        session.Draft = ""; session.State = "Running"; session.Detail = "Preparing workspace context…"; Save();
        int repairs = 0, completionCorrections = 0, repeatedActions = 0, pathErrors = 0;
        CodeAction? lastAction = null;
        string? previousReadResult = null;
        Guid? previousFileRead = null;
        bool revisitRead = false;
        var declined = new List<CodeAction>();
        var failedCommands = new List<CodeAction>();
        var failedAttempts = new List<(CodeAction Action, string Result)>();
        bool snippetRecovery = false, delivered = false, commandSucceeded = false;
        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedHashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        bool repairingAcceptance = false;
        CodeFile? repairPage = null;
        var callerCancellation=cancellation;using var runDeadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);runDeadline.CancelAfter(TimeSpan.FromMinutes(30));cancellation=runDeadline.Token;
        try
        {
            CodeAcceptancePlan? acceptance = null;
            int acceptanceAttempts = 0;
            bool? browserApproved = null;
            if (session.Mode == "build")
            {
                session.Detail = "Defining acceptance checks for the request"; changed();
                var previous = session.Turns.Take(session.Turns.Count - 1).LastOrDefault(t => t.Acceptance != null)?.Acceptance;
                bool resume = CodeAcceptance.IsContinuation(input);
                if (resume && previous != null)
                    acceptance = new() { Request = previous.Request, Units = previous.Units, Checks = [..previous.Checks], TestHashes = new(previous.TestHashes) };
                else
                {
                    var request = resume ? CodeAcceptance.SubstantiveRequest(session.Messages) : input;
                    bool addsToTask = previous != null && (priorState == "Waiting for input" ||
                        System.Text.RegularExpressions.Regex.IsMatch(input, @"^\s*(?:also\b|and\b|additionally\b|continue\b|resume\b|retry\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    if (addsToTask) request = previous!.Request + "\n" + input;
                    acceptance = await CodeAcceptance.Prepare(request, model, scope, listing, cancellation);
                    if (addsToTask)
                    {
                        // Previously executable checks stay pinned across additive instructions.
                        var pinned = previous!.Checks.Where(c => c.Kind != "unverified").ToList();
                        acceptance.Checks.RemoveAll(c => pinned.Any(p=>p.Unit==c.Unit && p.Kind==c.Kind && p.Feature==c.Feature));
                        acceptance.Checks.InsertRange(0,pinned);
                        foreach (var entry in previous.TestHashes) acceptance.TestHashes[entry.Key]=entry.Value;
                    }
                }
                if (resume && previous?.Results.Count > 0) delivered = true;
                user.Acceptance = acceptance;
                Save();
            }
            for (int step = 0; step < session.ActionLimit; step++)
            {
                cancellation.ThrowIfCancellationRequested(); session.Detail = "Working · Preparing the next response"; changed();
                var available=new HashSet<string>(CodeSchema.Available(session));
                if (repairingAcceptance && session.Permission != CodePermission.ReadOnly)
                {
                    available.Remove("open_file"); available.Remove("list_files"); available.Remove("verify_acceptance");
                }
                if (repairPage != null) available.IntersectWith(["replace_file","ask_user"]);
                if(snippetRecovery){available.Remove("edit_file");available.Remove("apply_edits");}
                if (revisitRead && lastAction != null)
                {
                    if (lastAction.Tool is "read_file" or "open_file") available.ExceptWith(["read_file", "open_file"]);
                    else available.Remove(lastAction.Tool!);
                }
                if (refreshInventory)
                {
                    listing = await Task.Run(() => scope.Files(".", 120), cancellation);
                    instructions = scope.Instructions();
                    refreshInventory = false;
                }
                var system = WorkspacePrompt(session, listing, instructions, available, snippetRecovery);
                if (observedHashes.Count > 0) system = system with { ObservedHashes=observedHashes.Values.Distinct().TakeLast(32).ToArray() };
                if (acceptance != null) system = system with { Content = system.Content +
                    "\nThe host runs these checks at finish. A browser_click clicks the button named Target and waits for Expected to appear as newly visible page text. Use the exact labels and update the DOM. After writing the implementation, finish so the host can run the checks. " +
                    JsonSerializer.Serialize(acceptance.Checks) };
                if(repairingAcceptance) system = system with { RepairingAcceptance=true, Content = system.Content + "\nThe previous implementation failed a host check. Inspect the failed receipt and repair the page. For a small page with broken HTML, use replace_file with the observed hash and complete valid HTML." };
                if (repairPage is {} page && acceptance!=null) system = system with { ObservedHashes=[page.Sha256],
                    PageRepair=new(page,acceptance.Request,CodeAcceptance.Receipt(acceptance),scope.Instructions(page.Path)), Content=system.Content+
                    $"\nRepair the small self-contained page {page.Path} with replace_file now. Its current SHA-256 is {page.Sha256}. Write a complete HTML document. For click interactions start the output element empty and set its textContent inside an event listener when the button is clicked." };
                if (revisitRead)
                    system = system with { Content = system.Content + "\nThe previous two reads returned identical evidence. That tool is paused for this response. If the request is satisfied, return your final answer now. Otherwise take the next distinct action needed for the user's request." };
                var messages = await CodeContext.Prepare(session, system, model, Save, text => { session.Detail = text; changed(); }, cancellation);
                if (step == 0 && session.Mode == "build")
                    await CodeContext.CheckBuildHeadroom(session, messages, model, cancellation);
                var turn = new CodeTurn { Role = "assistant", FirstActivity = session.Activities.Count };
                CodeAction action;
                string? question = null;
                try
                {
                    action = await model.Next(messages, text => { session.LiveMessage = text; changed(); }, cancellation);
                    if (action.Tool == "ask_user") question = ReadQuestion(action.Arguments);
                }
                catch (CodeResponseException error)
                {
                    repairs++; session.LiveMessage = "";
                    session.Activities.Add(new() { Tool = "model_response", State = "Failed", Result = error.Message + "\n" + error.Response });
                    if (repairs > 2) throw new InvalidOperationException("Three consecutive model responses were invalid or incomplete. " + error.Message, error);
                    session.Messages.Add(new("user", CodeProtocol.ResponseRepairPrefix + error.Message +
                        " Return one complete action. If the output was incomplete, use a smaller complete file or edit and continue in separate actions. " +
                        "Avoid repeated text and resume from saved tool receipts."));
                    session.Detail = $"Correcting model response · {repairs}/2"; Save(); continue;
                }
                repairs = 0;
                repairPage = null;
                cancellation.ThrowIfCancellationRequested();
                bool prematureCompletion = action.Tool == null && session.Mode == "build" && !delivered && failedCommands.Count == 0;
                session.LiveMessage = "";
                turn.Text = prematureCompletion ? "Build completion was rejected because this request has no delivery evidence yet." :
                    action.Tool == null && acceptance != null ? "Checking the requested features." : action.Message;
                turn.PerformanceSummary = action.PerformanceSummary;
                session.Turns.Add(turn);
                session.Messages.Add(new("assistant", JsonSerializer.Serialize(new { version = 1, message = action.Message, tool = action.Tool == null ? null : new { name = action.Tool, arguments = action.Arguments } })) { ReasoningContent=action.ReasoningContent });
                Save();
                if (action.Tool == null)
                {
                    if (failedCommands.Count > 0)
                    {
                        session.State = "Needs verification";
                        session.Detail = failedCommands.Count == 1 ? "One command failed without a successful rerun. See activity."
                            : $"{failedCommands.Count} commands failed without successful reruns. See activity.";
                        turn.Text = session.Detail;
                        Save(); return;
                    }
                    if (prematureCompletion)
                    {
                        if (completionCorrections++ >= 2) throw new InvalidOperationException("The model ended a Build task without a delivered change or successful command. No work is claimed complete. Use Ask for inspection-only answers or choose another model.");
                        const string reason = "Build completion rejected: reading, opening a file and planning alone do not deliver an implementation. No change or successful command has been recorded. Implement and verify the requested work using the listed tools now, or ask_user with a specific blocker. Do not return tool:null with intentions, reasoning or an unfinished plan. Use the tools; do not stop after summarizing requirements.";
                        session.Activities.Add(new() { Tool = "completion_check", State = "Failed", Result = reason });
                        session.Messages.Add(new("user", reason)); session.Detail = "Continuing · missing tool evidence"; Save(); continue;
                    }
                    if (acceptance != null)
                    {
                        session.Detail = "Verifying requested features"; changed();
                        var proof = new CodeActivity { Tool = "acceptance_checks", State = "Running" };
                        session.Activities.Add(proof); turn.ActivityCount++; Save();
                        if (browserApproved == null && session.Permission is CodePermission.WorkspaceEdits or CodePermission.ReviewEdits &&
                            acceptance.Checks.Any(c=>c.Kind.StartsWith("browser_",StringComparison.Ordinal)))
                        {
                            session.State="Awaiting review"; session.Detail="Review local browser verification"; Save();
                            browserApproved = await approve(new("acceptance_checks","Verify the local page in Edge?",
                                "Run the requested page checks in a disposable offline browser.\n"+string.Join("\n",acceptance.Checks.Select(c=>c.Feature))),cancellation);
                            cancellation.ThrowIfCancellationRequested(); session.State="Running";
                        }
                        try { await CodeAcceptance.Verify(acceptance, scope, session.Permission, cancellation, browserApproved==true); }
                        catch (OperationCanceledException) { proof.State="Interrupted"; proof.Result="Acceptance verification was interrupted. Resume to check the current workspace."; throw; }
                        var passed = acceptance.Results.Count(r => r.Passed);
                        bool complete = passed > 0 && passed == acceptance.Checks.Count;
                        proof.State = complete ? "Completed" : "Failed"; proof.Result = CodeAcceptance.Receipt(acceptance);
                        if (!complete)
                        {
                            turn.Text = $"{passed}/{acceptance.Checks.Count} feature checks passed. See activity for each result.";
                            if (session.Permission!=CodePermission.ReadOnly && browserApproved!=false && !acceptance.Results.Any(r => r.Check.Kind == "unverified") && ++acceptanceAttempts < 3 && step+1 < session.ActionLimit)
                            {
                                repairingAcceptance = true;
                                var sources = new List<object>();
                                foreach (var path in acceptance.Results.Where(r=>!r.Passed).Select(r=>r.Check.Path).Distinct().Take(2))
                                {
                                    try { var f=scope.Read(path); observedHashes[f.Path]=f.Sha256;
                                        if(f.Content.Length<=4000) {
                                            sources.Add(new { path=f.Path, expected_sha256=f.Sha256, current_content=f.Content });
                                            if (acceptance.Results.Where(r=>!r.Passed).All(r=>r.Check.Kind.StartsWith("browser_") && r.Check.Path==path)
                                                && !System.Text.RegularExpressions.Regex.IsMatch(f.Content,@"<(?:script|link)\b[^>]*(?:src|href)\s*=",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                                                repairPage=f;
                                        } }
                                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
                                }
                                session.Messages.Add(new("user", "Build completion rejected: repair the failed feature with an edit or replacement. Then finish to rerun the checks.\n"+proof.Result+"\n"+JsonSerializer.Serialize(sources)));
                                session.Detail = turn.Text; Save(); continue;
                            }
                            session.State = "Needs verification"; session.Detail = turn.Text; Save(); return;
                        }
                        turn.Text = action.Message;
                    }
                    session.State = "Completed";
                    session.Detail = commandSucceeded
                        ? changedFiles.Count == 0 ? "Completed · command ran" : $"Completed · {changedFiles.Count} file-tool changes · command ran"
                        : "Completed · " + changedFiles.Count + (changedFiles.Count == 1 ? " file changed" : " files changed");
                    if(acceptance!=null) session.Detail=session.Detail.Replace("Completed · ",$"Completed · {acceptance.Checks.Count} feature {(acceptance.Checks.Count==1 ? "check" : "checks")} passed · ");
                    Save(); return;
                }
                if (question != null)
                {
                    if (!turn.Text.Contains(question, StringComparison.Ordinal)) turn.Text += "\n\n" + question;
                    session.Messages.Add(new("user", "The agent requested user input: " + question + ". Wait for the next user message."));
                    session.State = "Waiting for input"; session.Detail = "Waiting for your answer"; Save(); return;
                }
                var activity = new CodeActivity { Tool = action.Tool, Arguments = action.Arguments.GetRawText() }; session.Activities.Add(activity); turn.ActivityCount = 1;
                session.Detail = "Working · " + action.Tool.Replace('_', ' '); Save();
                repeatedActions = lastAction != null && SameOperation(lastAction, action) ? repeatedActions + 1 : 1;
                lastAction = action;
                CodeToolResult result;
                bool invalidPath = false;
                try
                {
                    if(snippetRecovery&&action.Tool is "edit_file" or "apply_edits")throw new InvalidOperationException("Snippet edits are paused after a conflict. Read the current file and use replace_file with its fresh SHA256.");
                    if (declined.Any(previous => SameOperation(previous, action)))
                    {
                        activity.Authorization = "Previously declined";
                        throw new UnauthorizedAccessException("This exact operation was already declined during this run. Ask the user for a new instruction instead of repeating it.");
                    }
                    if(repeatedActions>=3)throw new InvalidOperationException("The same tool request was repeated three times without an intervening action. Execution stopped before this repeat; inspect the receipts and change strategy.");
                    result = await Execute(scope, session, action, activity, cancellation);
                    // Commands can create files even when they exit with an error.
                    refreshInventory = result.ChangedFile != null || result.CreatedDirectory || action.Tool is "run_command" or "run_shell";
                    activity.State = result.Failed ? "Failed" : "Completed"; activity.Result = result.Content;
                    if (result.ChangedFile is { } path) changedFiles.Add(path);
                    if (result.ChangedFile != null || action.Tool is "run_command" or "run_shell") repairingAcceptance = false;
                    bool commandExecuted = !result.Failed && action.Tool is ("run_command" or "run_shell");
                    commandSucceeded |= commandExecuted;
                    delivered |= !result.Failed && (result.ChangedFile != null || result.CreatedDirectory || commandExecuted ||
                        (session.Permission == CodePermission.ReadOnly && action.Tool is ("open_file" or "open_preview")));
                    if (result.ArtifactKind != null && result.ArtifactTarget != null)
                    {
                        var tab = new CodeTab { Kind = result.ArtifactKind, Target = result.ArtifactTarget };
                        CodePanels.Open(session, tab, user: false);
                    }
                }
                catch (OperationCanceledException) { activity.State = "Interrupted"; activity.Result = "Operation cancelled. Inspect workspace changes before retrying."; Save(); throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception)
                {
                    if(action.Tool is "edit_file" or "apply_edits"&&error.Message.StartsWith("Each old_text must match exactly once",StringComparison.Ordinal))snippetRecovery=true;
                    if (activity.Authorization == "Declined") declined.Add(action);
                    invalidPath = error is CodeWorkspacePathException or CodeWorkspaceDirectoryException;
                    activity.State = error is UnauthorizedAccessException ? "Denied" : "Failed";
                    activity.Result = invalidPath
                        ? error.Message + " The selected workspace root is '.'. Correct the argument using a workspace-relative path. For a new root-level file, use its filename directly."
                        : error.Message;
                    result = new(activity.Result);
                }
                bool successfulRead = activity.State == "Completed" && action.Tool is ("read_file" or "open_file" or "list_files" or "search_text");
                bool fileRead = successfulRead && action.Tool is ("read_file" or "open_file");
                if (fileRead)
                {
                    using var snapshot = JsonDocument.Parse(result.Content);
                    observedHashes[snapshot.RootElement.GetProperty("Path").GetString()!] = snapshot.RootElement.GetProperty("Sha256").GetString()!;
                }
                bool sameFileRead = fileRead && previousFileRead.HasValue && previousReadResult == result.Content;
                revisitRead = sameFileRead || (successfulRead && repeatedActions >= 2 && previousReadResult == result.Content);
                var modelReceipt = result.Content;
                if (sameFileRead)
                {
                    using var file = JsonDocument.Parse(result.Content);
                    modelReceipt = JsonSerializer.Serialize(new { path = file.RootElement.GetProperty("Path"), sha256 = file.RootElement.GetProperty("Sha256"),
                        unchanged_since_activity = previousFileRead, guidance = "The same file and hash were already returned. Use the previous contents for a change, or finish now if the request is satisfied." });
                }
                previousFileRead = fileRead ? activity.Id : null;
                previousReadResult = successfulRead ? result.Content : null;
                if (action.Tool is ("run_command" or "run_shell") && activity.State is ("Completed" or "Failed"))
                {
                    failedCommands.RemoveAll(previous => SameOperation(previous, action));
                    if (activity.State == "Failed") failedCommands.Add(action);
                }
                session.Messages.Add(new("user", JsonSerializer.Serialize(new { tool_result = action.Tool, activity_id = activity.Id, state = activity.State, result = modelReceipt })));
                Save();
                if (activity.State == "Completed") failedAttempts.Clear();
                else
                {
                    failedAttempts.Add((action, activity.Result));
                    bool SameFailure((CodeAction Action, string Result) prior) =>
                        SameOperation(prior.Action, action) || (prior.Action.Tool == action.Tool && prior.Result == activity.Result &&
                            prior.Action.Arguments.TryGetProperty("path", out var oldPath) && action.Arguments.TryGetProperty("path", out var path) &&
                            JsonElement.DeepEquals(oldPath, path));
                    if (failedAttempts.Count(SameFailure) >= 3)
                    {
                        session.State = "Blocked";
                        session.Detail = "The same operation failed three times without a successful tool action. See activity for the last error.";
                        Save(); return;
                    }
                }
                pathErrors = invalidPath ? pathErrors + 1 : 0;
                if (pathErrors >= 2)
                {
                    session.State = "Blocked";
                    session.Detail = "The model could not produce a valid path after correction. The selected workspace root is '.'. Use a relative filename or try another model.";
                    Save(); return;
                }
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
        var system = WorkspacePrompt(session, listing, scope.Instructions(), CodeSchema.Available(session));
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
    static bool SameOperation(CodeAction left, CodeAction right) =>
        left.Tool == right.Tool && JsonElement.DeepEquals(left.Arguments, right.Arguments);

    static string ReadQuestion(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.EnumerateObject().Count() == 1 &&
            arguments.TryGetProperty("question", out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: <= 4000 } question && !string.IsNullOrWhiteSpace(question))
            return question;
        throw new CodeResponseException("ask_user requires one nonblank question of at most 4,000 characters.",
            arguments.ValueKind == JsonValueKind.Undefined ? "" : arguments.GetRawText());
    }

    static CodeMessage WorkspacePrompt(CodeSession session, CodeFiles listing, string instructions,
        IReadOnlySet<string> available, bool snippetRecovery = false)
    {
        string recovery = snippetRecovery
            ? "\nA snippet edit failed to match the current file. edit_file and apply_edits are unavailable for the remainder of this run. Read the current file and use replace_file with its fresh SHA256 and complete source, or continue other work. Do not repeat stale snippets or speculate about runtime failures."
            : "";
        return new("system", CodeProtocol.Instructions +
            "\nAgent mode: " + session.Mode +
            "\nPermissions: " + (session.Mode == "ask" ? CodePermission.ReadOnly : session.Permission) +
            "\n" + CodeShells.Describe(session) +
            "\nWorkspace root '.' is an existing folder. Use a relative filename to create a file here." +
            (listing.RootIsOnlyDirectory && listing.Files.Length == 0 ? " The project has no visible files or subdirectories. Begin new projects by creating the requested files directly." : "") +
            "\nEnabled tools for this action:\n" + CodeSchema.ToolGuide(available) +
            "\nWorkspace files (bounded): " + JsonSerializer.Serialize(listing) +
            "\nRepository instructions:\n" + instructions + recovery) { AvailableTools = available, RootIsOnlyDirectory = listing.RootIsOnlyDirectory };
    }

    async Task<CodeToolResult> Execute(CodeWorkspace scope, CodeSession session, CodeAction action, CodeActivity activity, CancellationToken ct)
    {
        var a = action.Arguments; var tool = action.Tool!;
        switch (tool)
        {
            case "verify_acceptance":
                CodeProtocol.Exact(a);
                var contract = session.Turns.LastOrDefault(t=>t.Role=="user")?.Acceptance;
                if (session.Mode!="build" || session.Permission!=CodePermission.Auto || contract==null)
                    throw new UnauthorizedAccessException("Feature verification requires an active Auto build contract.");
                await CodeAcceptance.Verify(contract,scope,session.Permission,ct);
                return new(CodeAcceptance.Receipt(contract), Failed: contract.Results.Count==0 || contract.Results.Any(r=>!r.Passed));
            case "list_files": CodeProtocol.Exact(a, "path", "limit"); return new(JsonSerializer.Serialize(await Task.Run(() => scope.ListForAgent(S(a, "path"), a.GetProperty("limit").GetInt32()), ct)));
            case "search_text": CodeProtocol.Exact(a, "query", "path"); return new(JsonSerializer.Serialize(await Task.Run(() => scope.Search(S(a, "query"), S(a, "path")), ct)));
            case "read_file": case "open_file":
                CodeProtocol.Exact(a, "path"); var file = scope.Read(S(a, "path"));
                return new(JsonSerializer.Serialize(new { file.Path, file.Content, file.Sha256, instructions = scope.Instructions(file.Path) }), tool == "open_file" ? "file" : null, file.Path);
            case "open_preview":
                if (session.Mode == "ask") throw new UnauthorizedAccessException("Ask mode does not open browser previews. Use the Website button to open a local page manually.");
                if (session.Permission == CodePermission.Auto) throw new UnauthorizedAccessException("Auto blocks agent-initiated website navigation. Use the Website button manually to inspect an approved local page; no preview was loaded.");
                CodeProtocol.Exact(a, "url"); var uri = CodeProtocol.PreviewUri(S(a, "url")); return new("Preview tab opened. Browser contents have not been inspected by the agent.", "web", uri.AbsoluteUri);
            case "create_directory":
                CodeProtocol.Exact(a, "path");
                if (session.Mode == "ask" || session.Permission == CodePermission.ReadOnly) throw new UnauthorizedAccessException("Ask/read-only mode does not allow directory creation.");
                var folder = scope.Relative(scope.ResolveAgentCreate(S(a, "path"), directory: true));
                if (session.Permission == CodePermission.ReviewEdits) await Review(new(tool, "Create directory " + folder, "Create missing directory components inside the selected workspace. Existing files are preserved."));
                ct.ThrowIfCancellationRequested();
                var pathCreated = scope.CreateDirectory(folder, out bool directoryCreated);
                return new(JsonSerializer.Serialize(new { path = pathCreated, state = directoryCreated ? "Directory created" : "Directory already exists" }))
                { CreatedDirectory = directoryCreated };
            case "edit_file": case "apply_edits": case "replace_file": case "create_file":
                if (session.Mode == "ask" || session.Permission == CodePermission.ReadOnly) throw new UnauthorizedAccessException("Ask/read-only mode does not allow file writes. Ask the user to change the mode.");
                bool editing = tool != "create_file";
                if (tool == "edit_file") CodeProtocol.Exact(a, "path", "expected_sha256", "old_text", "new_text");
                else if (tool == "apply_edits") CodeProtocol.Exact(a, "path", "expected_sha256", "edits");
                else if (tool == "replace_file") CodeProtocol.Exact(a, "path", "expected_sha256", "content");
                else CodeProtocol.Exact(a, "path", "content");
                var replacements = tool == "apply_edits" ? a.GetProperty("edits").EnumerateArray().Select(e => { CodeProtocol.Exact(e, "old_text", "new_text"); return new CodeReplacement(S(e, "old_text"), S(e, "new_text")); }).ToArray() : tool == "edit_file" ? new[] { new CodeReplacement(S(a, "old_text"), S(a, "new_text")) } : [];
                var requestedPath = S(a, "path");
                string path = scope.Relative(editing ? scope.Resolve(requestedPath) : scope.ResolveAgentCreate(requestedPath, directory: false));
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
                    return new(JsonSerializer.Serialize(new { edited.Path, edited.Sha256, state = "Written" }), "changes", "") { ChangedFile = edited.Path };
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
