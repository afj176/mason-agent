using System.Diagnostics;
using System.Text.Json;
using Mason;
using Mason.Code;

/// <summary>Session, settings and workflow commands: fork, export and import, schedules, generation, roles, background
/// tasks, experiments, milestones, delivery, comments, acceptance, review, windows, embeddings and the small switches.
/// Each one calls the same core code the desktop app uses and keeps the wording of the macOS and Linux CLIs.</summary>
internal sealed partial class Interactive
{
    static readonly (string, string)[] WorkflowHelp =
    [
        ("/comment PATH:LINE NOTE", "Pin a note to a line, sent with your next request (see /comment help)"),
        ("/milestone", "A saved plan: milestones, the files each covers and how each is checked"),
        ("/generation", "Temperature, top P and the like, kept for the model in use (see /generation help)"),
        ("/roles", "A separate reviewer model, a separate model for pictures, and a trace of every model call (see /roles help)"),
        ("/reasoning LEVEL", "How much the model thinks for this session: off, low, medium, high or default"),
        ("/task", "Run a request in the background; it keeps going after you close Mason (see /task help)"),
        ("/deliver", "Build one commit from the hunks you choose and open a draft pull request; bring CI results back (see /deliver help)"),
        ("/experiment", "Try something in a copy of the project in its own worktree, then apply the hunks you want (see /experiment help)"),
        ("/review changes", "Have the model read what this session changed and pin what it finds as comments"),
        ("/accept", "Run the scenarios in mason.acceptance.json in a real browser"),
        ("/fork NUMBER [files]", "A new session from before an earlier request; \"files\" also puts the project back"),
        ("/export FILE [sanitized] · /import FILE", "Save this session as a file, or add an exported one to this project"),
        ("/window [NUMBER|off]", "List open windows, or attach one so Mason can read and (with your OK) operate it"),
        ("/embeddings [NUMBER|auto]", "Choose the embeddings connection Mason uses to search code by meaning"),
        ("/schedule", "List scheduled requests; add, switch, remove or run one (see /schedule help)"),
        ("/sessions delete NUMBER", "Delete one of this folder's sessions, after you confirm"),
        ("/agent new NAME · /commands new NAME", "Create a custom agent or command in .mason from the starting template"),
        ("/compact auto on|off", "Compact automatically when the context fills up, or never"),
    ];

    static string OwnProgram => Path.Combine(AppContext.BaseDirectory, "mason.exe");

    /// <summary>Runs work off the console thread with a spinner. Esc or Ctrl+C cancels it.</summary>
    async Task<T> Busy<T>(string label, Func<CancellationToken, Task<T>> work)
    {
        using var stop = new CancellationTokenSource(); run = stop;
        try
        {
            var task = Task.Run(() => work(stop.Token));
            var clock = Stopwatch.StartNew(); int frame = 0;
            while (!task.IsCompleted)
            {
                if (Term.Interactive && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) stop.Cancel();
                Term.Status($"  {"⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[frame++ / 2 % 10]} {label}{(progress.Length > 0 ? "  ·  " + Term.Clip(progress, 40) : "")}  ·  {clock.Elapsed.TotalSeconds:0}s  ·  Esc to stop");
                await Task.Delay(80);
            }
            Term.Status("");
            return await task;
        }
        finally { run = null; Term.Status(""); progress = ""; }
    }


    /// <summary>Saved chat connections in /model's order, with a model this session started first.</summary>
    List<AiEndpointProfile> Connections()
    {
        var saved = Saved();
        if (served is { Owner.IsRunning: true }) { saved.RemoveAll(p => p.Id == served.Profile.Id); saved.Insert(0, served.Profile); }
        return saved;
    }

    // ---- small switches ----------------------------------------------------------------------------------------

    void Reasoning(string rest)
    {
        var level = rest.Trim().ToLowerInvariant();
        if (level.Length == 0) { Term.Line("  Reasoning: " + Term.Paint(Term.Bold, session.Reasoning) + Term.Paint(Term.Dim, "  ·  /reasoning off | low | medium | high | default")); return; }
        if (level is not ("off" or "low" or "medium" or "high" or "default")) throw new ArgumentException("Use /reasoning off, low, medium, high or default.");
        session.Reasoning = level; store.Save(session);
        Term.Line(Term.Paint(Term.Dim, "  Reasoning: " + level + (level == "default" ? " (the model's own setting)" : "") + ". It applies from the next request."));
    }

    void AutoCompact(string rest)
    {
        var choice = rest.Trim().ToLowerInvariant();
        if (choice.Length == 0) { Term.Line("  Automatic compaction is " + Term.Paint(Term.Bold, session.AutomaticCompaction ? "on" : "off") + Term.Paint(Term.Dim, "  ·  /compact auto on | off")); return; }
        session.AutomaticCompaction = choice switch { "on" => true, "off" => false, _ => throw new ArgumentException("Use /compact auto on, or /compact auto off.") };
        store.Save(session);
        Term.Line(Term.Paint(Term.Dim, session.AutomaticCompaction ? "  Automatic compaction on: a long conversation is summarized when the context fills up." : "  Automatic compaction off: the full history is sent until you /compact."));
    }

    static void ThemeMode(string mode)
    {
        var saved = ThemeFamilies.SaveMode(DataHome(), mode);
        Term.Mode = saved; Term.UseTheme(Term.Theme);
        Term.Line("  " + Term.Paint(Term.Accent + Term.Bold, "✓") + " Mode set to " + Term.Paint(Term.Accent, saved == "Default" ? "system" : saved.ToLowerInvariant()) + Term.Paint(Term.Dim, ". Mason Agent uses it the next time it starts."));
    }

    void NewDefinition(bool agent, string name)
    {
        if (name.Length == 0) throw new ArgumentException(agent ? "Use /agent new NAME." : "Use /commands new NAME.");
        var example = CodeCustomization.Example(agent);
        var path = CodeCustomization.Create(scope.Root, agent, name, example.Description, example.Body, askOnly: agent);
        Term.Line("  Created " + Term.Paint(Term.Bold, path));
        Term.Line(Term.Paint(Term.Dim, agent ? "  Edit its description and instructions, then use it with /agent " + name.Trim() + "." : "  Edit its description and request template ($ARGUMENTS, $1–$9), then run it as /" + name.Trim() + "."));
    }

    void DeleteSession(string rest)
    {
        var mine = store.Sessions.Where(s => s.Workspace.Equals(scope.Root, StringComparison.OrdinalIgnoreCase) && (s.Turns.Count > 0 || s == session)).OrderByDescending(s => s.Updated).Take(20).ToList();
        if (!int.TryParse(rest.Trim(), out var number) || number < 1 || number > mine.Count) throw new ArgumentException("Use /sessions delete NUMBER from the /sessions list.");
        var doomed = mine[number - 1];
        if (!Confirm("Delete \"" + Term.Clip(doomed.Title, 50) + "\" and its history? This cannot be undone.")) { Term.Line(Term.Paint(Term.Dim, "  Kept.")); return; }
        bool current = doomed == session;
        if (!store.Delete(doomed.Id)) throw new InvalidOperationException("That session is already gone.");
        if (current)
        {
            session = Fresh(store, scope.Root, store.Sessions.Where(s => s.Workspace.Equals(scope.Root, StringComparison.OrdinalIgnoreCase)).ToList());
            session.Model = profile?.Id ?? session.Model; store.Save(session);
        }
        Term.Line(Term.Paint(Term.Green, "  ✓ Deleted ") + doomed.Title + (current ? Term.Paint(Term.Dim, "  ·  continuing in a new session") : ""));
    }

    // ---- fork, export, import ----------------------------------------------------------------------------------

    const string TransferHelp =
        "/fork NUMBER [files]     A new session from before request NUMBER; \"files\" also puts the project back as it was then\n" +
        "/export FILE [sanitized] Save this session as a file; \"sanitized\" removes the project's path and likely secrets\n" +
        "/import FILE             Add an exported session to this project";

    void Fork(string rest)
    {
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var requests = session.Turns.Where(t => t.Role == "user").ToList();
        if (words.Length == 0 || !int.TryParse(words[0], out var number))
        {
            for (int i = 0; i < requests.Count; i++) Term.Line($"  {Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(2))}  {Term.Clip(requests[i].Text.ReplaceLineEndings(" "), Math.Max(20, Term.Width - 8))}");
            foreach (var line in TransferHelp.Split('\n')) Term.Line(Term.Paint(Term.Dim, "  " + line));
            return;
        }
        if (number < 1 || number > requests.Count) throw new ArgumentException("Use a request number from /fork.");
        var (fork, _) = CodeSessionTransfer.Fork(store, session, requests[number - 1].Id, words.Length > 1 && words[1].Equals("files", StringComparison.OrdinalIgnoreCase));
        session = fork;
        Term.Line("  " + session.Detail);
        Term.Line(Term.Paint(Term.Dim, "  The request was: ") + session.Draft);
    }

    void Export(string rest)
    {
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool sanitized = words.RemoveAll(w => w.Equals("sanitized", StringComparison.OrdinalIgnoreCase)) > 0;
        if (words.Count > 1) throw new ArgumentException("Use /export FILE, or /export FILE sanitized.");
        var name = words.Count == 1 ? words[0] : "mason-session-" + session.Id.ToString("N")[..8] + (sanitized ? "-sanitized" : "") + ".json";
        var file = Path.GetFullPath(Environment.ExpandEnvironmentVariables(name.StartsWith("~\\") || name.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name[2..]) : name));
        var temp = file + ".tmp";
        File.WriteAllText(temp, CodeSessionTransfer.Export(session, sanitized));
        File.Move(temp, file, true);
        Term.Line("  Saved " + (sanitized ? "a sanitized copy of " : "") + "this session to " + file);
    }

    void Import(string rest)
    {
        if (rest.Trim().Length == 0) throw new ArgumentException("Use /import FILE.");
        var name = rest.Trim().Trim('"');
        var file = Path.GetFullPath(name.StartsWith("~\\") || name.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name[2..]) : name);
        if (!File.Exists(file)) throw new ArgumentException(file + " could not be read.");
        session = CodeSessionTransfer.Import(store, File.ReadAllText(file), scope.Root);
        Term.Line("  " + session.Detail + Term.Paint(Term.Dim, $"  ·  {session.Turns.Count(t => t.Role == "user")} requests"));
    }

    // ---- schedules ---------------------------------------------------------------------------------------------

    async Task Schedule(string rest)
    {
        if (rest.StartsWith("run ", StringComparison.OrdinalIgnoreCase))
        {
            var name = rest[4..].Trim();
            var found = CodeSchedules.Load(host).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("No schedule named " + name + ". /schedule lists them.");
            Term.Line(Term.Paint(Term.Dim, "  Starting " + found.Name + " now as a background task…"));
            var outcome = CodeSchedules.Start(host, found, OwnProgram, BindSchedule(host));
            Term.Line("  " + outcome.Outcome + Term.Paint(Term.Dim, outcome.Task is { } id ? "  ·  /task show " + id.ToString("N")[..8] + " follows it" : ""));
            return;
        }
        if (rest.Trim().Equals("help", StringComparison.OrdinalIgnoreCase)) { Lines(CodeSchedules.Usage + "\nUse /schedule run NAME to start one now."); return; }
        if (profile == null && rest.TrimStart().StartsWith("add", StringComparison.OrdinalIgnoreCase)) await SelectModel(null, quiet: true);
        Lines(CodeSchedules.Command(rest, host, scope.Root, profile is { } used ? (used.Id, used.Model) : null, DateTimeOffset.Now));
    }

    /// <summary>Resolves a schedule's saved connection the way the app does: a managed model must be running.</summary>
    static Func<CodeSchedule, TaskModelConnection> BindSchedule(string home) => due =>
    {
        var path = Path.Combine(home, "ai-connections.json");
        var all = File.Exists(path) ? JsonSerializer.Deserialize<List<AiEndpointProfile>>(File.ReadAllText(path)) ?? [] : [];
        var saved = all.SingleOrDefault(p => p.Id == due.Connection) ?? throw new InvalidOperationException("its model connection was removed");
        string secret;
        try { secret = Secret(saved); } catch (System.Security.Cryptography.CryptographicException) { secret = ""; }
        if (saved.ManagedCapability != null && !Serves(saved, secret).GetAwaiter().GetResult()) throw new InvalidOperationException("its managed model is not running");
        return new TaskModelConnection(LocalAiClient.ValidateEndpoint(saved.Endpoint).AbsoluteUri, saved.Model, saved.Secret, null);
    };

    /// <summary>mason --run-due: starts every schedule that is due as a background task, then exits.</summary>
    public static int RunDue()
    {
        var home = Path.GetFullPath(DataHome());
        var started = CodeSchedules.RunDue(home, OwnProgram, BindSchedule(home), DateTimeOffset.Now);
        if (started.Count == 0) Console.WriteLine("No schedule is due.");
        foreach (var (name, outcome) in started) Console.WriteLine(name + ": " + outcome);
        return started.Any(s => s.Outcome != "started") ? 2 : 0;
    }

    // ---- generation and roles ----------------------------------------------------------------------------------

    void Generation(string rest)
    {
        if (rest.Trim().Equals("help", StringComparison.OrdinalIgnoreCase)) { Lines(CodeSampling.Help); return; }
        if (profile == null) throw new ArgumentException("No model is in use. Generation settings are kept for a connection, so start or choose one first.");
        var current = Saved().FirstOrDefault(p => p.Id == profile.Id) ?? profile;
        Lines(CodeSampling.Command(rest, current, sampling =>
        {
            var all = Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? [];
            int index = all.FindIndex(p => p.Id == profile.Id);
            if (index < 0) throw new InvalidOperationException(profile.DisplayName + " is not a saved connection, so its generation settings cannot be kept. Save it in Mason Agent's Models first.");
            all[index] = all[index] with { Sampling = sampling };
            Store.Write("ai-connections.json", all);
            profile = profile with { Sampling = sampling };
        }));
    }

    void Roles(string rest)
    {
        var connections = Connections().Select(p => (p.Id, p.DisplayName)).ToList();
        Lines(CodeRoles.Command(rest, session, connections));
        store.Save(session);
    }

    // ---- background tasks --------------------------------------------------------------------------------------

    const string TaskUsage =
        "/task                     This project's background tasks\n" +
        "/task start REQUEST       Run a request in the background; it keeps going after you close Mason\n" +
        "/task show ID             What a task has done, and the approval it is waiting on\n" +
        "/task allow ID            Allow what the task is waiting on (deny: /task deny ID)\n" +
        "/task cancel ID           Stop a task\n" +
        "A task started in Auto edits files and runs sandboxed commands without asking. Otherwise each edit and command waits for /task allow.";

    static string Short(Guid id) => id.ToString("N")[..8];

    string TaskLine(LocalTasks tasks, Guid id)
    {
        var status = tasks.Status(id);
        var prompt = tasks.Spec(id).Prompt.ReplaceLineEndings(" ");
        var text = Short(id) + "  " + status.State + (status.Detail.Length > 0 ? " (" + Term.Clip(status.Detail.ReplaceLineEndings(" "), 60) + ")" : "") + "  ·  " + Term.Clip(prompt, 70);
        if (status.Decision is { } waiting && status.Active) text += "\n    waiting on: " + waiting.Approval.Tool.Replace('_', ' ') + "  ·  /task allow " + Short(id) + " or /task deny " + Short(id);
        return text;
    }

    void TaskCommand(string rest)
    {
        var tasks = new LocalTasks(host);
        rest = rest.Trim();
        int space = rest.IndexOf(' ');
        string verb = (space < 0 ? rest : rest[..space]).ToLowerInvariant(), argument = space < 0 ? "" : rest[(space + 1)..].Trim();
        Guid Find()
        {
            var matches = tasks.All(scope.Root).Where(id => id.ToString("N").StartsWith(argument.Replace("-", ""), StringComparison.OrdinalIgnoreCase)).ToArray();
            return argument.Length > 0 && matches.Length == 1 ? matches[0] : throw new ArgumentException(argument.Length == 0 ? "Give the task's ID. /task lists them." : "No task " + argument + ".");
        }
        if (verb is "" or "list")
        {
            var mine = tasks.All(scope.Root).OrderByDescending(id => tasks.Status(id).Started).Take(20).ToArray();
            if (mine.Length == 0) { Lines("No background tasks in this project. /task start REQUEST runs one."); return; }
            Lines(string.Join("\n", mine.Select(id => TaskLine(tasks, id))));
            return;
        }
        if (verb == "help") { Lines(TaskUsage); return; }
        if (verb == "start")
        {
            if (argument.Length is 0 or > 16000) throw new ArgumentException("Describe the task in 1–16,000 characters.");
            if (profile == null) throw new ArgumentException("Choose a model first; a background task uses the one this session uses.");
            var all = Connections();
            TaskModelConnection Binding(Guid id)
            {
                var chosen = all.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("A selected model connection was removed. Choose again with /roles.");
                var live = served is { Owner.IsRunning: true } && served.Profile.Id == id ? served : null;
                return new(LocalAiClient.ValidateEndpoint(chosen.Endpoint).AbsoluteUri, chosen.Model, chosen.Secret, live?.Owner.Lifetime);
            }
            var main = all.Any(p => p.Id == profile.Id) ? Binding(profile.Id) : new TaskModelConnection(LocalAiClient.ValidateEndpoint(profile.Endpoint).AbsoluteUri, profile.Model, key.Length > 0 ? Store.Protect(key) : "", null);
            var models = new TaskModelOptions(main, session.ReviewerModel is { } reviewer ? Binding(reviewer) : null, session.VisualModel is { } visual ? Binding(visual) : null,
                session.Reasoning, session.RoleCallLimit, session.RecordModelDecisions);
            var permission = session.Mode == "ask" ? CodePermission.ReadOnly : session.Permission;
            var id = tasks.Start(OwnProgram, scope.Root, argument, main.Endpoint, main.Model, permission, main.Lifetime, models: models);
            Lines("Started task " + Short(id) + " in the background" + (permission == CodePermission.Auto ? ", in Auto." : permission == CodePermission.ReadOnly ? ", read-only." : "; it will wait for /task allow before each edit and command.") + " /task show " + Short(id) + " follows it.");
            return;
        }
        if (verb == "show")
        {
            var id = Find(); var status = tasks.Status(id);
            var text = TaskLine(tasks, id);
            if (status.Decision is { } waiting && status.Active) text += "\n" + waiting.Approval.Description + (waiting.Approval.Detail.Length > 0 ? "\n" + string.Join("\n", waiting.Approval.Detail.ReplaceLineEndings("\n").Split('\n').Take(20)) : "");
            foreach (var line in tasks.Tail(id).TakeLast(12))
            {
                try
                {
                    using var item = JsonDocument.Parse(line);
                    var type = item.RootElement.GetProperty("type").GetString();
                    var detail = item.RootElement.GetProperty("detail");
                    string said = type == "tool.result" && detail.ValueKind == JsonValueKind.Object
                        ? (detail.TryGetProperty("Tool", out var tool) ? tool.GetString() : "") + "  →  " + (detail.TryGetProperty("State", out var state) ? state.GetString() : "")
                        : detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("detail", out var words) ? words.ToString() : detail.ToString();
                    text += "\n  " + type + "  " + Term.Clip(said.ReplaceLineEndings(" "), 100);
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
            }
            if (status.Session is { } run && !status.Active) text += "\nIts journal is in " + tasks.Journal(id) + " (session " + Short(run) + ").";
            Lines(text);
            return;
        }
        if (verb is "allow" or "deny")
        {
            var id = Find(); var status = tasks.Status(id);
            if (status.Decision is not { } waiting || !status.Active) throw new ArgumentException("Task " + argument + " is not waiting for an approval.");
            tasks.Decide(id, waiting.Id, verb == "allow");
            Lines((verb == "allow" ? "Allowed: " : "Denied: ") + waiting.Approval.Tool.Replace('_', ' ') + "  ·  " + Term.Clip(waiting.Approval.Description.ReplaceLineEndings(" "), 80));
            return;
        }
        if (verb == "cancel")
        {
            var id = Find(); tasks.Cancel(id);
            Lines("Asked task " + Short(id) + " to stop.");
            return;
        }
        Lines(TaskUsage);
    }

    // ---- experiments -------------------------------------------------------------------------------------------

    const string ExperimentUsage =
        "/experiment                       List this project's experiments and what each has changed\n" +
        "/experiment new NAME              Copy the project as it stands into a worktree beside it (PROJECT-NAME, branch mason/NAME)\n" +
        "/experiment diff NAME [PATH]      The files an experiment changed, or one file's hunks with their ids\n" +
        "/experiment apply NAME PATH ID    Apply one hunk to this project (or \"all\" for every hunk of the file)\n" +
        "/experiment forget NAME           Drop the record; the worktree and branch are left for you to remove";

    async Task Experiment(string rest)
    {
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = words.Length == 0 ? "" : words[0].ToLowerInvariant();
        var mine = store.Sessions.Where(s => s.Experiment is { } origin && origin.Parent.Equals(scope.Root, StringComparison.OrdinalIgnoreCase)).ToList();
        CodeSession Named(int at)
        {
            var wanted = at < words.Length ? words[at] : "";
            return mine.FirstOrDefault(s => s.Title.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("No experiment named " + wanted + ". /experiment lists them.");
        }
        if (verb.Length == 0)
        {
            if (mine.Count == 0) { Lines("No experiments in this project.\n" + ExperimentUsage); return; }
            var lines = new List<string>();
            foreach (var item in mine)
            {
                string changed;
                try { changed = (await CodeExperiment.Changed(new CodeWorkspace(item.Workspace), item.Experiment!, CancellationToken.None)).Length + " changed files"; }
                catch (Exception e) when (e is IOException or InvalidDataException or DirectoryNotFoundException or ArgumentException) { changed = "unavailable: " + e.Message; }
                lines.Add(item.Title + "  ·  " + item.Workspace + "  ·  " + changed);
            }
            Lines(string.Join("\n", lines) + "\nWork in one with mason --workspace FOLDER; bring hunks back with /experiment apply.");
            return;
        }
        if (verb == "help") { Lines(ExperimentUsage); return; }
        if (verb == "new")
        {
            var slug = words.Length == 2 ? words[1] : "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(slug, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,48}\z")) throw new ArgumentException("Use a short task name with letters, digits, hyphens or underscores.");
            if (mine.Any(s => s.Title.Equals(slug, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("An experiment named " + slug + " already exists.");
            var destination = scope.Root.TrimEnd('\\') + "-" + slug;
            var origin = await Busy("Copying the project into a worktree", token => CodeExperiment.Fork(session, destination, "mason/" + slug, token));
            // The same fork the app makes: the conversation, roles and plan travel with it.
            var fork = store.New(destination); fork.Experiment = origin; fork.Model = session.Model; fork.ReviewerModel = session.ReviewerModel; fork.VisualModel = session.VisualModel;
            fork.Permission = session.Permission; fork.Mode = session.Mode; fork.Milestones = session.Milestones.ToArray();
            fork.Messages.AddRange(session.Messages);
            fork.Turns.AddRange(session.Turns.Where(t => !t.Rewound).Select(t => new CodeTurn { Role = t.Role, Text = t.Text, Evidence = t.Evidence }));
            fork.Shell = session.Shell; fork.ShellExecutable = session.ShellExecutable; fork.Reasoning = session.Reasoning;
            fork.ManageRoleMemory = session.ManageRoleMemory; fork.RoleCallLimit = session.RoleCallLimit; fork.RecordModelDecisions = session.RecordModelDecisions;
            fork.Title = slug; store.Save(fork);
            Lines("Created " + destination + " on branch mason/" + slug + ".\nWork in it with mason --workspace \"" + destination + "\"; /experiment diff " + slug + " shows what it changed.");
            return;
        }
        if (verb == "diff")
        {
            var item = Named(1); var fork = new CodeWorkspace(item.Workspace);
            if (words.Length < 3)
            {
                var changed = await CodeExperiment.Changed(fork, item.Experiment!, CancellationToken.None);
                Lines(changed.Length == 0 ? item.Title + " has not changed anything yet." : string.Join("\n", changed) + "\n/experiment diff " + item.Title + " PATH shows a file's hunks.");
                return;
            }
            var path = string.Join(' ', words.Skip(2));
            var hunks = await CodeExperiment.Hunks(fork, item.Experiment!, path, CancellationToken.None);
            if (hunks.Length == 0) { Lines(path + " is the same in both."); return; }
            foreach (var hunk in hunks)
            {
                Term.Line();
                Term.Line("  " + Term.Paint(Term.Accent, hunk.Id[..8]) + "  " + Term.Paint(Term.Bold, hunk.Title));
                foreach (var line in hunk.Patch.ReplaceLineEndings("\n").Split('\n').SkipWhile(l => !l.StartsWith("@@") && !hunk.Title.StartsWith("Whole", StringComparison.Ordinal)).Skip(1).Take(60))
                    Term.Line("    " + (line.StartsWith('+') ? Term.Paint(Term.Green, Term.Clip(line, Term.Width - 6)) : line.StartsWith('-') ? Term.Paint(Term.Red, Term.Clip(line, Term.Width - 6)) : Term.Paint(Term.Dim, Term.Clip(line, Term.Width - 6))));
            }
            Term.Line(Term.Paint(Term.Dim, "  /experiment apply " + item.Title + " " + path + " ID applies one (or all)."));
            return;
        }
        if (verb == "apply")
        {
            if (words.Length < 4) throw new ArgumentException("Use /experiment apply NAME PATH ID, or /experiment apply NAME PATH all.");
            var item = Named(1); var fork = new CodeWorkspace(item.Workspace);
            string path = string.Join(' ', words.Skip(2).SkipLast(1)), wanted = words[^1];
            var hunks = await CodeExperiment.Hunks(fork, item.Experiment!, path, CancellationToken.None);
            var chosen = wanted.Equals("all", StringComparison.OrdinalIgnoreCase) ? hunks : hunks.Where(h => h.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (chosen.Length == 0 || !wanted.Equals("all", StringComparison.OrdinalIgnoreCase) && chosen.Length != 1) throw new ArgumentException("No hunk " + wanted + " in " + path + ". /experiment diff " + item.Title + " " + path + " lists them.");
            // Applying a hunk changes the parent file, which shifts the hunks after it, so each is found again by its id.
            foreach (var hunk in chosen) await CodeExperiment.Apply(fork, item.Experiment!, path, hunk.Id, CancellationToken.None);
            Lines("Applied " + chosen.Length + (chosen.Length == 1 ? " hunk" : " hunks") + " of " + path + " to this project.");
            return;
        }
        if (verb == "forget")
        {
            var item = Named(1);
            store.Delete(item.Id);
            Lines("Forgot " + item.Title + ". The worktree " + item.Workspace + " and branch mason/" + item.Title + " are left for you to remove.");
            return;
        }
        Lines(ExperimentUsage);
    }

    // ---- milestones, comments, review, acceptance --------------------------------------------------------------

    async Task Milestone(string rest)
    {
        bool check = rest.TrimStart().StartsWith("check", StringComparison.OrdinalIgnoreCase);
        bool auto = session.Permission == CodePermission.Auto && session.Mode != "ask";
        string said = check
            ? await Busy("Checking the milestone", token => CodeMilestones.Command(rest, scope, session, auto, token))
            : await CodeMilestones.Command(rest, scope, session, auto, CancellationToken.None);
        store.Save(session);
        Lines(said);
    }

    async Task Comment(string rest)
    {
        if (rest.Trim().Equals("send", StringComparison.OrdinalIgnoreCase))
        {
            if (session.ReviewComments.Count == 0) Term.Line(Term.Paint(Term.Dim, "  No comments are waiting."));
            else await Send("");
            return;
        }
        var said = CodeReviewComment.Command(rest, scope, session.ReviewComments);
        store.Save(session);
        Lines(said);
    }

    async Task ReviewChanges()
    {
        if (profile == null && !await SelectModel(null)) return;
        var sources = new List<ReviewSource>();
        foreach (var path in session.Changes.Where(c => !c.Restored).Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!CodeChangeReview.Supported(path)) continue;
            try
            {
                var file = scope.Read(path);
                sources.Add(new ReviewSource(file.Path, session.Changes.First(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Before, file.Content, file.Sha256));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
        }
        if (sources.Count == 0) { Term.Line(Term.Paint(Term.Dim, "  This session has not changed any source files the reviewer reads.")); return; }
        var request = session.Turns.LastOrDefault(t => t.Role == "user")?.Text ?? "Review what this session changed.";
        var roles = Models();
        var findings = await Busy("Reviewing what this session changed", token => CodeChangeReview.Review(roles, request, sources, token));
        int pinned = 0;
        foreach (var finding in findings)
        {
            if (session.ReviewComments.Count >= CodeReviewComment.Limit) break;
            var source = sources.First(s => s.Path == finding.Path);
            var file = new CodeFile(source.Path, source.Current, source.Hash, false);
            session.ReviewComments.Add(new CodeReviewComment(Guid.NewGuid(), source.Path, source.Hash, false, finding.Line, "", finding.Problem).OnCurrentLine(file, finding.Line));
            pinned++;
        }
        store.Save(session);
        if (findings.Length == 0) { Lines("The reviewer found nothing to flag in " + sources.Count + (sources.Count == 1 ? " file." : " files.")); return; }
        Lines(string.Join("\n", findings.Select((f, i) => $"{i + 1}. {f.Path}:{f.Line} — {f.Problem}")) +
            $"\nPinned {pinned} as comments; they are sent with your next request (/comment lists them).");
    }

    async Task Accept()
    {
        if (!File.Exists(Path.Combine(scope.Root, BrowserContract.Filename))) { Lines("No " + BrowserContract.Filename + " in this project. It lists the scenarios to run: each a page and its steps."); return; }
        var contract = BrowserContract.Read(scope);
        var plan = new CodeAcceptancePlan
        {
            Request = "Run the scenarios in " + BrowserContract.Filename, Units = contract.Scenarios.Select(s => s.Requirement).ToArray(),
            Checks = contract.Scenarios.Select(s => new AcceptanceCheck(1, s.Requirement, "browser_flow", s.Page, s.Id, BrowserContract.Filename) { Steps = s.Steps, FromContract = true, Application = contract.Application }).ToList()
        };
        if (plan.Checks.Count == 0) { Lines(BrowserContract.Filename + " has no scenarios."); return; }
        await Busy("Running the acceptance scenarios", async token => { await CodeAcceptance.Verify(plan, scope, session.Permission, token); return true; });
        Lines(CodeAcceptance.Receipt(plan) + "\n" + plan.Results.Count(r => r.Passed) + " of " + plan.Results.Count + " passed.");
    }

    // ---- delivery ----------------------------------------------------------------------------------------------

    const string DeliverUsage =
        "/deliver review BASE                 What this checkout has that BASE does not, a hunk at a time\n" +
        "/deliver draft BASE HUNKS TITLE      Build one commit from hunks (ids separated by commas, or \"all\") on top of BASE\n" +
        "/deliver                             The current draft\n" +
        "/deliver publish                     Push the draft's commit to a new branch and open a draft pull request (needs gh)\n" +
        "/deliver feedback                    Failed checks, requested changes and review comments on that pull request\n" +
        "/deliver feedback send               The same, sent to the model as your next request";

    async Task Deliver(string rest)
    {
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = words.Length == 0 ? "" : words[0].ToLowerInvariant();
        if (verb.Length == 0)
        {
            if (session.Delivery is not { } current) { Lines("No draft yet.\n" + DeliverUsage); return; }
            Lines(current.Title + "\n" + current.Branch + " → " + current.Base + "  ·  commit " + current.Commit[..12] + "  ·  " + string.Join(", ", current.Paths) +
                (current.Url is { } url ? "\n" + url : "\nNot published. /deliver publish pushes it and opens a draft pull request."));
            return;
        }
        if (verb == "help") { Lines(DeliverUsage); return; }
        if (verb == "review")
        {
            if (words.Length != 2) throw new ArgumentException("Use /deliver review BASE, for example /deliver review main.");
            var (_, patches) = await Busy("Comparing with " + words[1], token => CodeDelivery.Review(scope, words[1], token));
            if (patches.Length == 0) { Lines("This checkout has nothing that " + words[1] + " does not."); return; }
            foreach (var group in patches.GroupBy(p => p.Path))
            {
                Term.Line(); Term.Line("  " + Term.Paint(Term.Bold, group.Key));
                foreach (var patch in group)
                {
                    var body = patch.Hunk.Patch.ReplaceLineEndings("\n").Split('\n').Where(l => (l.StartsWith('+') || l.StartsWith('-')) && !l.StartsWith("+++") && !l.StartsWith("---")).ToArray();
                    Term.Line("    " + Term.Paint(Term.Accent, patch.Hunk.Id[..8]) + "  " + Term.Clip(patch.Hunk.Title, Term.Width - 20) + Term.Paint(Term.Dim, $"  +{body.Count(l => l[0] == '+')} −{body.Count(l => l[0] == '-')}"));
                }
            }
            Term.Line(Term.Paint(Term.Dim, "  /deliver draft " + words[1] + " ID,ID TITLE builds one commit from the ones you choose (or all)."));
            return;
        }
        if (verb == "draft")
        {
            if (words.Length < 4) throw new ArgumentException("Use /deliver draft BASE HUNKS TITLE.");
            var (revision, patches) = await Busy("Comparing with " + words[1], token => CodeDelivery.Review(scope, words[1], token));
            var wanted = words[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
            var ids = words[2].Equals("all", StringComparison.OrdinalIgnoreCase) ? patches.Select(p => p.Hunk.Id).ToArray()
                : wanted.Select(w => patches.Where(p => p.Hunk.Id.StartsWith(w, StringComparison.OrdinalIgnoreCase)).Select(p => p.Hunk.Id).Distinct().ToArray() is { Length: 1 } one ? one[0] : throw new ArgumentException("No single hunk " + w + ". /deliver review " + words[1] + " lists them.")).ToArray();
            var draft = await Busy("Building the commit", token => CodeDelivery.Draft(scope, session, words[1], revision, string.Join(' ', words.Skip(3)), ids, token));
            var old = session.Delivery; session.Delivery = draft;
            try { store.Save(session); } catch { session.Delivery = old; throw; }
            Lines("Saved " + draft.Commit[..12] + " locally on " + draft.Branch + ".\n" + draft.Body + "\n/deliver publish pushes it and opens a draft pull request.");
            return;
        }
        var made = session.Delivery ?? throw new ArgumentException("Build a draft first with /deliver draft.");
        if (verb == "publish")
        {
            if (made.Repository.Length == 0) throw new InvalidOperationException("This draft has no GitHub origin.");
            Lines(made.Repository + " · " + made.Branch + " → " + made.Base + "\n" + made.Commit + "\n" + made.Title);
            if (!Confirm("Push and open a draft pull request?")) return;
            var url = await Busy("Publishing", token => CodeDelivery.Publish(scope, made, token));
            session.Delivery = made with { Url = url }; store.Save(session);
            Lines(url);
            return;
        }
        if (verb == "feedback")
        {
            var review = await Busy("Reading the pull request", token => CodeDelivery.Feedback(scope, made, token));
            if (review.Observations.Length == 0) { Lines("No failing check or review feedback returned."); return; }
            var text = "Feedback on " + review.Url + (review.Head == made.Commit ? "" : " (the pull request has moved past the draft's commit)") + ":\n" + string.Join("\n", review.Observations.Take(12).Select(s => "- " + (s.Length > 440 ? s[..440] : s)));
            if (words.Length > 1 && words[1].Equals("send", StringComparison.OrdinalIgnoreCase)) await Send("Address this pull request feedback.\n" + text);
            else Lines(text + "\n/deliver feedback send hands it to the model.");
            return;
        }
        Lines(DeliverUsage);
    }

    // ---- windows and embeddings --------------------------------------------------------------------------------

    void Window(string rest)
    {
        var word = rest.Trim().ToLowerInvariant();
        if (word is "off" or "none" or "detach") { session.Desktop = null; Lines("No window is attached to this session."); return; }
        var open = CodeDesktop.Windows();
        if (word.Length == 0)
        {
            var text = string.Concat(open.Select((w, i) => $"{i + 1}. {w}\n"));
            if (open.Count == 0) text += "No windows of other apps are open.\n";
            Lines(text + (session.Desktop is { } attached ? "Attached: " + attached.Target + ". /window off detaches it."
                : "Attach one with /window NUMBER. Mason then reads that window's controls, and asks before pressing or changing anything."));
            return;
        }
        if (!int.TryParse(word, out var number) || number < 1 || number > open.Count) throw new ArgumentException("Give the number of a window. /window lists them.");
        session.Desktop = new CodeDesktop(open[number - 1]);
        Lines("Attached " + open[number - 1] + ". Mason can read its controls, and asks before pressing or changing anything.");
    }

    void Embeddings(string rest)
    {
        var all = Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? [];
        bool Passed(AiEndpointProfile p, AiCapability capability) => p.Checks.Any(c => c.Capability == capability && c.State == "Passed");
        var embedders = all.Where(p => p.ManagedCapability == AiCapability.Embeddings || Passed(p, AiCapability.Embeddings)).ToArray();
        var rerankers = all.Where(p => p.ManagedCapability == AiCapability.Reranking || Passed(p, AiCapability.Reranking)).ToArray();
        var saved = CodeSearchSettings.Read();
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool rerank = words.Length > 0 && words[0].Equals("rerank", StringComparison.OrdinalIgnoreCase);
        var choices = rerank ? rerankers : embedders;
        var given = rerank ? words.Skip(1).FirstOrDefault() : words.FirstOrDefault();
        if (given == null)
        {
            var lines = new List<string>();
            string Name(Guid? id, AiEndpointProfile[] options, string automatic) => id is { } value ? options.FirstOrDefault(p => p.Id == value)?.DisplayName ?? "a removed connection" : automatic;
            lines.Add("Embeddings: " + Name(saved.Embeddings, embedders, "automatic (the one checked embeddings connection)") + "   Reranker: " + Name(saved.Reranker, rerankers, "automatic (the one checked reranker, if any)"));
            for (int i = 0; i < choices.Length; i++) lines.Add($"{i + 1}. {choices[i].DisplayName}  ·  {choices[i].Endpoint}{(Passed(choices[i], rerank ? AiCapability.Reranking : AiCapability.Embeddings) ? "" : "  ·  not checked yet")}");
            if (choices.Length == 0) lines.Add(rerank ? "No reranking connection is saved." : "No embeddings connection is saved. Start one in Mason Agent's Models (My models → Code search), or connect one there.");
            lines.Add(CodeSemanticIndex.Available ? "Mason offers the model semantic_search in every session." : "Code search by meaning is off until a checked embeddings connection is answering.");
            lines.Add("Choose with /embeddings NUMBER or /embeddings auto; the reranker with /embeddings rerank NUMBER|auto.");
            Lines(string.Join("\n", lines));
            return;
        }
        Guid? picked = given.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null
            : int.TryParse(given, out var number) && number >= 1 && number <= choices.Length ? choices[number - 1].Id
            : throw new ArgumentException("Use a number from /embeddings" + (rerank ? " rerank" : "") + ", or auto.");
        var next = rerank ? saved with { Reranker = picked } : saved with { Embeddings = picked };
        Store.Write(CodeSearchSettings.File, next);
        Lines((rerank ? "Reranker: " : "Embeddings: ") + (picked is { } id ? choices.First(p => p.Id == id).DisplayName : "automatic") + ". This applies to every session, in the app too.");
    }

    /// <summary>The semantic_search tool, routed to the assigned embeddings and reranking connections, as in the app.</summary>
    static void UseCodeSearch()
    {
        CodeSemanticIndex.Provider = () =>
        {
            List<AiEndpointProfile> profiles; try { profiles = Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []; } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
            var settings = CodeSearchSettings.Read();
            var embeddings = CodeSearchModels.Pick(profiles, settings.Embeddings, AiCapability.Embeddings); if (embeddings == null) return null;
            var reranker = CodeSearchModels.Pick(profiles, settings.Reranker, AiCapability.Reranking);
            static string Key(AiEndpointProfile p) { try { return Store.Unprotect(p.Secret); } catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException) { return ""; } }
            return new CodeSearchModels(embeddings, Key(embeddings), reranker, reranker == null ? "" : Key(reranker));
        };
    }
}
