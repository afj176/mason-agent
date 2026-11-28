using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mason;
using Mason.Code;

/// <summary>
/// The interactive terminal session: type a request, watch tool activity, approve edits and commands, inspect diffs.
/// It drives the same engine, model adapter, approval rules, snapshots and custom commands as the desktop app.
/// All console input is read on the calling thread; the engine runs on the thread pool and hands approvals back through a queue.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class Interactive
{
    readonly string host;
    readonly CodeWorkspace scope;
    readonly CodeStore store;
    readonly Queue<(CodeApproval Request, TaskCompletionSource<bool> Answer)> approvals = new();
    CodeSession session;
    AiEndpointProfile? profile;
    string key = "", context = "", progress = "";
    CancellationTokenSource? run;
    int interrupts;
    readonly Stopwatch warned = new();

    Interactive(string host, CodeWorkspace scope, CodeStore store, CodeSession session) { this.host = host; this.scope = scope; this.store = store; this.session = session; }

    public static string DataHome() => Environment.GetEnvironmentVariable("MASON_DATA_HOME") ??
        (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag")) ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MasonAgent"));

    public const string Usage = """
        mason [--workspace PATH] [--resume [SESSION]] [--endpoint LOOPBACK --model ID]
              [--read-only | --review | --edits | --auto]
          Opens the coding agent in the current folder (or --workspace). Without --endpoint and --model it
          uses the connections saved by Mason Agent. New sessions ask before each edit and command (--review).
        mason chat [--resume] [--endpoint LOOPBACK --model ID]
          Chat with a model, with web, skills and MCP tools. Shares its conversations with Mason Agent's Chat;
          --resume continues the latest one.
        mason theme [blueprint|evergreen|graphite|kiln|retro-neon]
          Lists the theme families, or chooses one for the CLI and Mason Agent.
        """;

    public static async Task<int> Start(string[] args)
    {
        Term.Start();
        var values = new Dictionary<string, string>(StringComparer.Ordinal); var flags = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected argument: " + args[i]);
            var name = args[i][2..];
            if (name is "auto" or "edits" or "review" or "read-only") { flags.Add(name); continue; }
            if (name == "resume" && (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))) { values["resume"] = ""; continue; }
            if (name is not ("workspace" or "endpoint" or "model" or "resume") || i + 1 >= args.Length || !values.TryAdd(name, args[++i]))
                throw new ArgumentException("Unknown or repeated option --" + name + ". Run mason --help.");
        }
        if (flags.Count > 1) throw new ArgumentException("Choose one of --read-only, --review, --edits or --auto.");
        if (values.ContainsKey("endpoint") != values.ContainsKey("model")) throw new ArgumentException("Use --endpoint and --model together.");

        var scope = new CodeWorkspace(values.GetValueOrDefault("workspace") ?? Environment.CurrentDirectory);
        string host = Path.GetFullPath(DataHome());
        if (CodeSandbox.ContainsHost(scope.Root, host)) throw new ArgumentException("Open a project folder that does not contain Mason's own data folder.");
        using var store = Open(host, scope.Root);
        var mine = store.Sessions.Where(s => s.Workspace.Equals(scope.Root, StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.Updated).ToList();
        CodeSession session; bool resumed = false;
        if (values.TryGetValue("resume", out var wanted))
        {
            session = (wanted.Length == 0 ? mine.FirstOrDefault(s => s.Turns.Count > 0) : mine.FirstOrDefault(s => s.Id.ToString("N").StartsWith(wanted.Replace("-", ""), StringComparison.OrdinalIgnoreCase)))
                ?? throw new ArgumentException("No saved session to resume in this folder.");
            resumed = true;
        }
        else session = Fresh(store, scope.Root, mine);

        var app = new Interactive(host, scope, store, session);
        if (flags.Count == 1) app.SetPermission(flags.Single() switch { "auto" => CodePermission.Auto, "edits" => CodePermission.WorkspaceEdits, "read-only" => CodePermission.ReadOnly, _ => CodePermission.ReviewEdits });
        if (values.TryGetValue("endpoint", out var endpoint))
            app.profile = new AiEndpointProfile { Name = values["model"], Endpoint = LocalAiClient.ValidateEndpoint(endpoint).AbsoluteUri, Model = values["model"], ManagedCapability = AiCapability.Chat };
        try { return await app.Loop(resumed); }
        finally { app.StopAllModels(); }
    }

    /// <summary>One store per project. A second terminal in the same project gets its own store because a store has a single writer.</summary>
    static CodeStore Open(string host, string workspace)
    {
        string name = CodeProtocol.Hash(Encoding.UTF8.GetBytes(workspace.ToLowerInvariant()))[..16];
        for (int slot = 1; ; slot++)
        {
            try { return new CodeStore(Path.Combine(host, "cli", slot == 1 ? name : name + "." + slot), host); }
            catch (IOException) when (slot < 8) { }
        }
    }
    /// <summary>Reuses an untouched session instead of leaving an empty one behind on every launch.</summary>
    static CodeSession Fresh(CodeStore store, string workspace, IEnumerable<CodeSession> mine)
    {
        if (mine.FirstOrDefault(s => s.Turns.Count == 0 && s.Activities.Count == 0) is { } empty) return empty;
        var session = store.New(workspace); session.Permission = CodePermission.ReviewEdits; session.Mode = "build"; store.Save(session);
        return session;
    }

    async Task<int> Loop(bool resumed)
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; if (run is { } active) { try { active.Cancel(); } catch (ObjectDisposedException) { } } else Interlocked.Increment(ref interrupts); };
        if (profile == null) await SelectModel(null, quiet: true);
        Banner(resumed);
        if (profile == null) await FirstRun();
        while (true)
        {
            var input = await ReadInput();
            if (input == null) { Term.Line(); return 0; }
            if (input.Length == 0) continue;
            try
            {
                if (input[0] == '/' && await Command(input) is { } handled) { if (!handled) return 0; continue; }
                await Send(input);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Term.Line(Term.Paint(Term.Red, "✗ " + error.Message)); }
        }
    }

    void Banner(bool resumed)
    {
        var lines = new List<string>
        {
            Term.Paint(Term.Bold + Term.Accent, "Mason Agent") + Term.Paint(Term.Dim, "  ·  " + scope.Root),
            Term.Paint(Term.Dim, "model ") + (profile?.DisplayName ?? Term.Paint(Term.Yellow, "none connected")) + Term.Paint(Term.Dim, "  ·  " + ModeName() + "  ·  /help for commands"),
        };
        if (resumed) lines.Add(Term.Paint(Term.Dim, "resumed ") + session.Title + Term.Paint(Term.Dim, $"  ·  {session.Turns.Count(t => t.Role == "user")} requests  ·  {session.State}"));
        Term.Line();
        // In a wide colour terminal the app icon sits at the left with the session lines beside its middle.
        var picture = Term.Picture();
        if (picture.Length == 0) foreach (var line in lines) Term.Line("  " + line);
        else
        {
            int first = (picture.Length - lines.Count) / 2;
            for (int row = 0; row < picture.Length; row++)
                Term.Line("  " + picture[row] + (row >= first && row - first < lines.Count ? "   " + lines[row - first] : ""));
        }
        if (profile == null && !Term.Interactive) NoModelHelp();
    }
    static void NoModelHelp()
    {
        Term.Line();
        Term.Line(Term.Paint(Term.Yellow, "  No local model is answering."));
        Term.Line("  /models lists the models on this computer and starts one · /models setup finds and downloads one that fits.");
        Term.Line("  Or start a model in Mason Agent or your own OpenAI-compatible server on localhost, then type /model.");
    }
    string ModeName() => session.Mode == "ask" ? "ask · read-only" : session.Permission switch
    {
        CodePermission.Auto => "build · auto (sandboxed)", CodePermission.WorkspaceEdits => "build · workspace edits",
        CodePermission.ReviewEdits => "build · review edits", _ => "build · read-only"
    };
    void SetPermission(CodePermission permission)
    {
        session.Permission = permission; session.Mode = permission == CodePermission.ReadOnly ? "ask" : "build"; store.Save(session);
    }

    // ---- input -------------------------------------------------------------------------------------------------

    /// <summary>Reads one request. A trailing backslash continues on the next line, and pasted multi-line text arrives as one request.</summary>
    async Task<string?> ReadInput(string? label = null)
    {
        var text = new StringBuilder();
        while (true)
        {
            Term.Line(text.Length == 0 ? ModelStatusLine() ?? "" : "");
            Term.Write(text.Length == 0 ? Term.Paint(Term.Dim, (label ?? ModeName().Split(" · ")[^1]) + " ") + Term.Paint(Term.Accent + Term.Bold, "› ") : Term.Paint(Term.Dim, "  … "));
            var line = Console.ReadLine();
            if (line == null)
            {
                // Ctrl+C at the prompt ends ReadLine; the handler runs on another thread, so give it a moment to count.
                for (int waited = 0; waited < 400 && Volatile.Read(ref interrupts) == 0 && !Console.IsInputRedirected; waited += 20) await Task.Delay(20);
                if (Interlocked.Exchange(ref interrupts, 0) == 0 || Console.IsInputRedirected) return text.Length > 0 ? text.ToString().Trim() : null;
                // A second Ctrl+C soon after the first leaves; the console can deliver an empty line in between, so this is timed rather than counted.
                if (warned.IsRunning && warned.Elapsed < TimeSpan.FromSeconds(5)) return null;
                warned.Restart();
                Term.Line(); Term.Line(Term.Paint(Term.Dim, "  Press Ctrl+C again or type /exit to leave."));
                text.Clear(); continue;
            }
            if (line.Length > 0) warned.Reset();
            bool more = line.EndsWith('\\');
            text.Append(more ? line[..^1] : line);
            // Lines already waiting in the buffer came from a paste: keep them in this request.
            if (!more && !Console.IsInputRedirected) { await Task.Delay(25); more = Console.KeyAvailable; }
            if (!more) return text.ToString().Trim();
            text.Append('\n');
        }
    }
    static void DrainKeys() { if (Console.IsInputRedirected) return; while (Console.KeyAvailable) Console.ReadKey(true); }
    /// <summary>Waits for one of the given keys. Returns '\0' when cancelled or when there is no terminal to ask.</summary>
    static char Choose(string keys, CancellationToken cancellation)
    {
        if (!Term.Interactive) return '\0';
        DrainKeys();
        while (!cancellation.IsCancellationRequested)
        {
            if (!Console.KeyAvailable) { Thread.Sleep(30); continue; }
            var pressed = Console.ReadKey(true);
            char value = pressed.Key == ConsoleKey.Escape ? '\x1b' : char.ToLowerInvariant(pressed.KeyChar);
            if (keys.Contains(value)) return value;
        }
        return '\0';
    }

    // ---- models ------------------------------------------------------------------------------------------------

    sealed record Candidate(AiEndpointProfile Profile, bool Alive);
    static readonly HttpClient Probe = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };

    List<AiEndpointProfile> Saved()
    {
        var path = Path.Combine(host, "ai-connections.json");
        if (!File.Exists(path)) return [];
        try { return (JsonSerializer.Deserialize<List<AiEndpointProfile>>(File.ReadAllText(path)) ?? []).Where(p => p.ForChat && p.Model.Length > 0 && p.Endpoint.Length > 0).ToList(); }
        catch (Exception e) when (e is JsonException or IOException) { return []; }
    }
    static string Secret(AiEndpointProfile saved)
    {
        if (saved.Secret.Length == 0 || !OperatingSystem.IsWindows()) return "";
        var bytes = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(saved.Secret), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    /// <summary>True when the endpoint answers and lists the model. Only loopback endpoints are ever contacted.</summary>
    static async Task<bool> Serves(AiEndpointProfile candidate, string secret)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(LocalAiClient.ValidateEndpoint(candidate.Endpoint), "v1/models"));
            if (secret.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var response = await Probe.SendAsync(request);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
                data.EnumerateArray().Any(m => m.TryGetProperty("id", out var id) && id.GetString() == candidate.Model);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or ArgumentException or System.Security.Cryptography.CryptographicException) { return false; }
    }
    async Task<List<Candidate>> Candidates()
    {
        var saved = Saved();
        // A model started by this session is a connection too, and the one for its file wins over a stale saved entry.
        if (served is { Owner.IsRunning: true }) { saved.RemoveAll(p => p.Id == served.Profile.Id); saved.Insert(0, served.Profile); }
        var alive = await Task.WhenAll(saved.Select(async p => { try { return await Serves(p, Secret(p)); } catch (System.Security.Cryptography.CryptographicException) { return false; } }));
        return saved.Select((p, i) => new Candidate(p, alive[i])).ToList();
    }
    /// <summary>Selects the session's model, a named one, or the first saved connection that is answering.</summary>
    async Task<bool> SelectModel(string? wanted, bool quiet = false)
    {
        var all = await Candidates();
        Candidate? pick;
        if (wanted is { Length: > 0 })
            pick = int.TryParse(wanted, out var number) && number >= 1 && number <= all.Count ? all[number - 1]
                : all.FirstOrDefault(c => c.Profile.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase) || c.Profile.Model.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        else pick = all.FirstOrDefault(c => c.Alive && c.Profile.Id == session.Model) ?? all.FirstOrDefault(c => c.Alive);
        if (pick == null)
        {
            if (!quiet) { if (wanted is { Length: > 0 }) Term.Line(Term.Paint(Term.Yellow, "  No saved connection matches \"" + wanted + "\".")); else NoModelHelp(); }
            return false;
        }
        if (!pick.Alive && !quiet) Term.Line(Term.Paint(Term.Yellow, "  " + pick.Profile.DisplayName + " is not answering yet; it will be used once its server is running."));
        profile = pick.Profile; key = Secret(pick.Profile); session.Model = pick.Profile.Id; store.Save(session);
        return true;
    }
    async Task ListModels()
    {
        var all = await Candidates();
        if (all.Count == 0) { NoModelHelp(); return; }
        Term.Line();
        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i]; bool current = profile?.Id == c.Profile.Id;
            Term.Line($"  {Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(2))}  {(c.Alive ? Term.Paint(Term.Green, "●") : Term.Paint(Term.Dim, "○"))} {(current ? Term.Paint(Term.Bold, c.Profile.DisplayName) : c.Profile.DisplayName)}" +
                Term.Paint(Term.Dim, "  " + c.Profile.Endpoint + (c.Alive ? "" : "  not running") + (current ? "  ← current" : "")));
        }
        Term.Line(Term.Paint(Term.Dim, "  Choose with /model NUMBER or /model NAME."));
    }
    RoleModels Models()
    {
        var model = new CodeLocalModel(profile!, key, session.Reasoning, value => context = value, progress: value => progress = value);
        return new RoleModels(model, model, model, host, progress: value => progress = value);
    }

    // ---- running a request -------------------------------------------------------------------------------------

    async Task Send(string input)
    {
        if (profile == null && !await SelectModel(null)) return;
        if (!await Serves(profile!, key)) { Term.Line(Term.Paint(Term.Yellow, "  " + profile!.DisplayName + " is not answering at " + profile.Endpoint + ". Start it, or choose another with /model.")); return; }
        using var cancellation = new CancellationTokenSource(); run = cancellation; progress = "";
        int shown = session.Activities.Count, first = shown, turns = session.Turns.Count;
        Exception? failure = null;
        var engine = new CodeEngine(store, Models(), (request, token) =>
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => answer.TrySetCanceled(token));
            lock (approvals) approvals.Enqueue((request, answer));
            return answer.Task;
        }, () => { }, error => failure = error) { ModelName = profile!.DisplayName };
        var clock = Stopwatch.StartNew(); int frame = 0; bool stopping = false;
        Term.Line();
        var work = Task.Run(() => engine.Run(session, input, cancellation.Token));
        try
        {
            while (!work.IsCompleted)
            {
                (CodeApproval Request, TaskCompletionSource<bool> Answer)? waiting = null;
                lock (approvals) if (approvals.Count > 0) waiting = approvals.Dequeue();
                if (waiting is { } ask)
                {
                    Flush(ref shown); Term.Status("");
                    ask.Answer.TrySetResult(Approve(ask.Request, cancellation.Token));
                    continue;
                }
                if (Term.Interactive && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape && !stopping) cancellation.Cancel();
                if (cancellation.IsCancellationRequested && !stopping) { stopping = true; Flush(ref shown); Term.Line(Term.Paint(Term.Dim, "  Stopping…")); }
                Flush(ref shown);
                Term.Status($"  {"⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[frame++ / 2 % 10]} {Doing()}  ·  {clock.Elapsed.TotalSeconds:0}s  ·  Esc to stop");
                await Task.Delay(60);
            }
        }
        finally { run = null; Term.Status(""); lock (approvals) approvals.Clear(); }
        try { await work; } catch (OperationCanceledException) { } catch (Exception error) { failure ??= error; }
        Flush(ref shown);
        Outcome(first, turns, clock.Elapsed, failure);
        OfferSuggestedNote();
    }

    /// <summary>What the status line says: the model's streamed words, then the running tool, then the engine's own detail.</summary>
    string Doing()
    {
        int room = Math.Max(20, Term.Width - 36);
        var live = session.LiveMessage;
        if (live.Length > 0) { live = live.ReplaceLineEndings(" ").Trim(); return live.Length > room ? "…" + live[^(room - 1)..] : live; }
        try
        {
            if (session.Activities.LastOrDefault(a => a.State == "Running") is { } active) return Term.Clip(Label(active) + " " + Subject(active), room);
        }
        catch (InvalidOperationException) { /* The engine appended an activity while this thread was reading the list. */ }
        return Term.Clip(progress.Length > 0 ? progress : session.Detail.Length > 0 && session.State == "Running" ? session.Detail : "Thinking", room);
    }
    static string Label(CodeActivity activity) => activity.Tool.Replace('_', ' ');
    static string Subject(CodeActivity activity)
    {
        var text = CodeActivityText.Command(activity).ReplaceLineEndings(" ");
        return text == Label(activity) ? "" : text;
    }

    /// <summary>Prints each finished tool call once, in order. Stops at the first one still running.</summary>
    void Flush(ref int shown)
    {
        while (shown < session.Activities.Count)
        {
            CodeActivity activity;
            try { activity = session.Activities[shown]; } catch (ArgumentOutOfRangeException) { return; }
            if (activity.State is "Pending" or "Running") return;
            shown++;
            Show(activity);
        }
    }
    static void Show(CodeActivity activity)
    {
        string mark = activity.State switch { "Completed" => Term.Paint(Term.Green, "✓"), "Failed" => Term.Paint(Term.Red, "✗"), "Denied" => Term.Paint(Term.Yellow, "⊘"), "Skipped" => Term.Paint(Term.Yellow, "↻"), _ => Term.Paint(Term.Dim, "·") };
        string label = Label(activity), subject = Subject(activity);
        string note = activity.State switch { "Completed" or "Failed" => "", "Skipped" => "  skipped · repeated", var other => "  " + other.ToLowerInvariant() };
        if (activity.Authorization.Contains("rule", StringComparison.OrdinalIgnoreCase)) note += "  " + activity.Authorization.ToLowerInvariant();
        Term.Line($"  {mark} {Term.Paint(Term.Bold, label)} {Term.Clip(subject, Math.Max(10, Term.Width - label.Length - note.Length - 8))}{Term.Paint(Term.Dim, note)}");
        bool command = activity.Tool is "run_command" or "run_shell" or "run_workflow";
        if (!command && activity.State != "Failed" || activity.State == "Skipped") return;
        var lines = CodeActivityText.Result(activity).ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToArray();
        int limit = activity.State == "Failed" ? 10 : 6;
        if (lines.Length > limit) Term.Line(Term.Paint(Term.Dim, $"    │ … {lines.Length - limit} earlier lines"));
        foreach (var line in lines.TakeLast(limit)) Term.Line(Term.Paint(Term.Dim, "    │ " + Term.Clip(line, Term.Width - 8)));
    }

    void Outcome(int firstActivity, int turnsBefore, TimeSpan elapsed, Exception? failure)
    {
        var added = session.Turns.Skip(turnsBefore).ToList();
        if (added.LastOrDefault(t => t.Role == "assistant") is { Text.Length: > 0 } answer)
        {
            Term.Line();
            foreach (var line in Term.Markdown(answer.Text)) Term.Line("  " + line);
        }
        var request = added.FirstOrDefault(t => t.Role == "user");
        if (request?.Edits.Count > 0)
        {
            Term.Line();
            foreach (var edit in request.Edits.Take(30))
            {
                string counts = "";
                try
                {
                    var rows = CodeProtocol.Diff(edit.Before, scope.Read(edit.Path).Content).ToList();
                    counts = Term.Paint(Term.Green, "+" + rows.Count(r => r.Kind == '+')) + " " + Term.Paint(Term.Red, "−" + rows.Count(r => r.Kind == '-'));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { counts = Term.Paint(Term.Dim, "removed or unreadable"); }
                Term.Line($"  {Term.Paint(Term.Accent, edit.Before == null ? "A" : "M")} {edit.Path}  {counts}");
            }
            Term.Line(Term.Paint(Term.Dim, "  /diff shows the changes · /undo puts everything from this request back"));
        }
        Term.Line();
        // One table decides the glyph, colour and words. Red only when something broke; what the model did is amber.
        var ending = session.Ending;
        string state = CodeRunEnding.Look(ending?.Code) is { } look
            ? Term.Paint(look.Tone switch { CodeEndingTone.Success => Term.Green, CodeEndingTone.Warning => Term.Yellow, CodeEndingTone.Accent => Term.Accent, CodeEndingTone.Danger => Term.Red, _ => Term.Dim }, look.Glyph + " " + look.Headline)
            : session.State switch
            {
                "Completed" => Term.Paint(Term.Green, "✓ Completed"), "Stopped" => Term.Paint(Term.Yellow, "■ Stopped"),
                "Failed" => Term.Paint(Term.Red, "✗ Failed"), var other => Term.Paint(Term.Yellow, "● " + other)
            };
        var activities = session.Activities.Skip(firstActivity).ToArray();
        var facts = new List<string>();
        if (activities.Length > 0) facts.Add(CodeProtocol.ActivitySummary(activities));
        if (ending?.KeptText is { } kept) facts.Add(kept);
        facts.Add(elapsed.TotalSeconds < 90 ? $"{elapsed.TotalSeconds:0}s" : $"{elapsed.TotalMinutes:0.#} min");
        if (context.Length > 0) facts.Add(context.Split(" · ")[0]);
        Term.Line("  " + state + Term.Paint(Term.Dim, "  ·  " + string.Join("  ·  ", facts)));
        string detail = failure?.Message ?? (session.State == "Completed" || added.Any(t => t.Role == "assistant" && t.Text.Contains(session.Detail, StringComparison.Ordinal)) ? "" : session.Detail);
        void Explain(string text) { foreach (var line in Wrap(text, Term.Width - 6)) Term.Line("    " + Term.Paint(Term.Dim, line)); }
        // The model looped: the outcome line already says so; underneath, what happened and that it was the model's doing.
        if (ending?.Code == CodeRunEnding.ModelLoop && session.RepeatStop is { } repeat) Explain(repeat.Explanation(ending.Kept > 0));
        else if (detail.Length > 0) Explain(detail);
        if (ending != null && CodeRunEnding.Hint(ending.Code) is { Length: > 0 } hint) Explain(hint);
        if (session.PendingInput.Count > 0) Term.Line(Term.Paint(Term.Dim, $"  {session.PendingInput.Count} queued message(s) · /queue resumes them"));
    }

    // ---- approvals ---------------------------------------------------------------------------------------------

    bool Approve(CodeApproval request, CancellationToken cancellation)
    {
        string tool = request.Tool.Replace('_', ' '), bar = Term.Paint(Term.Yellow, "  ▌ ");
        Term.Line();
        Term.Line(bar + Term.Paint(Term.Bold, "Review " + tool));
        foreach (var line in request.Description.ReplaceLineEndings("\n").Split('\n').Take(6)) Term.Line(bar + Term.Clip(line, Term.Width - 6));
        var detail = request.Detail.ReplaceLineEndings("\n").Split('\n');
        foreach (var line in detail.Take(40))
        {
            string style = line.StartsWith('+') ? Term.Green : line.StartsWith('-') ? Term.Red : Term.Dim;
            Term.Line(bar + Term.Paint(style, Term.Clip(line, Term.Width - 6)));
        }
        if (detail.Length > 40) Term.Line(bar + Term.Paint(Term.Dim, $"… {detail.Length - 40} more lines"));
        if (request.Jpeg != null) Term.Line(bar + Term.Paint(Term.Dim, "A screenshot is attached to this request; it can be viewed in the desktop app."));
        if (!Term.Interactive) { Term.Line(bar + "Declined: there is no terminal to ask."); return false; }
        while (true)
        {
            Term.Write($"  {Term.Paint(Term.Bold, "Allow?")}  {Term.Paint(Term.Accent, "y")} once   {Term.Paint(Term.Accent, "a")} always for this project…   {Term.Paint(Term.Accent, "n")} decline  › ");
            char choice = Choose("yan\x1b", cancellation);
            if (choice is '\0') { Term.Line("cancelled"); return false; }
            if (choice is 'n' or '\x1b') { Term.Line(Term.Paint(Term.Yellow, "declined")); return false; }
            if (choice == 'y') { Term.Line(Term.Paint(Term.Green, "allowed once")); return true; }
            Term.Line("always");
            // A host is kept exactly, never as a prefix: one list per project for commands and the web tools.
            if (request.Tool == "network")
            {
                try { CodeNetworkSettings.Allow(host, scope.Root, request.RuleSubject); Term.Line(Term.Paint(Term.Green, "  Saved: always allow " + request.RuleSubject + " in this project") + Term.Paint(Term.Dim, "  ·  /network lists them")); return true; }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { Term.Line(Term.Paint(Term.Red, "  " + e.Message)); continue; }
            }
            string suggested = CodeApprovalRules.Suggest(request.Tool, request.RuleSubject);
            Term.Write($"  Allow {tool} when it starts with [{Term.Paint(Term.Cyan, suggested)}] (Enter keeps it, a single - cancels): ");
            var typed = Console.ReadLine()?.Trim();
            if (typed is null or "-") continue;
            try
            {
                var rule = new CodeApprovalRule(request.Tool, typed.Length == 0 ? suggested : typed, true, DateTimeOffset.UtcNow);
                CodeApprovalRules.Add(host, scope.Root, rule);
                Term.Line(Term.Paint(Term.Green, "  Saved: " + rule.Label) + Term.Paint(Term.Dim, "  ·  /rules lists and revokes"));
                return true;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException) { Term.Line(Term.Paint(Term.Red, "  " + e.Message)); }
        }
    }

    // ---- slash commands ----------------------------------------------------------------------------------------

    /// <summary>Returns true when handled, false to exit, and null when the input is a request for the engine (custom command or /queue).</summary>
    async Task<bool?> Command(string input)
    {
        int space = input.IndexOf(' ');
        string name = (space < 0 ? input : input[..space]).ToLowerInvariant(), rest = space < 0 ? "" : input[(space + 1)..].Trim();
        if (name is "/read" or "/review" or "/edits" or "/auto") { rest = name[1..]; name = "/permission"; }
        switch (name)
        {
            case "/exit" or "/quit": return false;
            case "/help" or "/?": Help(); return true;
            case "/new":
                if (session.Turns.Count > 0 || session.Activities.Count > 0)
                {
                    var previous = session;
                    session = Fresh(store, scope.Root, store.Sessions.Where(s => s.Workspace.Equals(scope.Root, StringComparison.OrdinalIgnoreCase)).ToList());
                    session.Permission = previous.Permission; session.Mode = previous.Mode; session.Agent = previous.Agent; session.Model = profile?.Id ?? previous.Model;
                    store.Save(session);
                }
                Term.Line(Term.Paint(Term.Dim, "  New session · " + ModeName()));
                if (rest.Length > 0) await Send(rest);
                return true;
            case "/sessions" or "/resume": Resume(rest); return true;
            case "/model":
                if (rest.Length == 0) await ListModels();
                else if (await SelectModel(rest)) Term.Line("  Model: " + Term.Paint(Term.Bold, profile!.DisplayName));
                return true;
            case "/models": await ModelsCommand(rest); return true;
            case "/ask": session.Mode = "ask"; store.Save(session); Term.Line(Term.Paint(Term.Dim, "  Ask mode: Mason reads and answers; it does not edit or run commands.")); return true;
            case "/build":
                if (session.Permission == CodePermission.ReadOnly) session.Permission = CodePermission.ReviewEdits;
                session.Mode = "build"; store.Save(session); Term.Line(Term.Paint(Term.Dim, "  " + ModeName())); return true;
            case "/permission" or "/permissions":
                if (rest.Length == 0) { Term.Line("  " + ModeName() + Term.Paint(Term.Dim, "  ·  /permission read | review | edits | auto")); return true; }
                var permission = rest.ToLowerInvariant() switch
                {
                    "read" or "read-only" or "readonly" => CodePermission.ReadOnly, "review" => CodePermission.ReviewEdits, "edits" or "workspace" => CodePermission.WorkspaceEdits, "auto" => CodePermission.Auto,
                    _ => throw new ArgumentException("Use /permission read, review, edits or auto.")
                };
                SetPermission(permission);
                Term.Line(Term.Paint(Term.Dim, "  " + permission switch
                {
                    CodePermission.ReadOnly => "Read-only: Mason reads and answers.",
                    CodePermission.ReviewEdits => "Review edits: you approve each edit and command.",
                    CodePermission.WorkspaceEdits => "Workspace edits: edits apply without asking; commands still ask.",
                    _ => "Auto: commands run in the Windows sandbox, limited to this project, with network access off."
                }));
                if (permission == CodePermission.Auto && CodeShells.Unavailable(session) is { } reason) Term.Line(Term.Paint(Term.Yellow, "  " + reason));
                return true;
            case "/status": Status(); return true;
            case "/theme": Theme(rest); return true;
            case "/notes": Notes(rest); return true;
            case "/note": Notes(rest.Trim().Length == 0 ? "" : "add " + rest); return true;
            case "/diff" or "/changes": Diff(rest); return true;
            case "/undo" or "/redo": Rewind(name == "/undo"); return true;
            case "/rules": Rules(rest); return true;
            case "/languages" or "/language":
                try
                {
                    if (rest.StartsWith("install", StringComparison.OrdinalIgnoreCase)) Term.Line(Term.Paint(Term.Dim, "  Installing. This can take a few minutes."));
                    foreach (var line in (await CodeLanguageServers.Command(rest, scope, host, CancellationToken.None)).Split('\n')) Term.Line("  " + line.TrimEnd('\r'));
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException) { foreach (var line in e.Message.Split('\n')) Term.Line("  " + line.TrimEnd('\r')); }
                return true;
            case "/agent": Agent(rest); return true;
            case "/commands":
                var commands = CodeCustomization.Commands(host, scope.Root);
                if (commands.Count == 0) Term.Line(Term.Paint(Term.Dim, "  No custom commands. Add Markdown files to .mason/commands."));
                foreach (var c in commands) Term.Line($"  {Term.Paint(Term.Accent, "/" + c.Name)}  {Term.Paint(Term.Dim, c.Description)}");
                return true;
            case "/compact":
                if (profile == null && !await SelectModel(null)) return true;
                using (var stop = new CancellationTokenSource())
                {
                    run = stop;
                    try
                    {
                        var work = Task.Run(() => new CodeEngine(store, Models(), (_, _) => Task.FromResult(false), () => { }).Compact(session, stop.Token));
                        var clock = Stopwatch.StartNew();
                        while (!work.IsCompleted) { Term.Status($"  Compacting the conversation  ·  {clock.Elapsed.TotalSeconds:0}s"); await Task.Delay(100); }
                        Term.Status(""); await work;
                    }
                    finally { run = null; Term.Status(""); }
                }
                Term.Line(Term.Paint(Term.Dim, "  " + (session.Detail.Length > 0 ? session.Detail : "Compacted.")));
                return true;
            case "/queue": return null;
            case "/skills": Skills(rest); return true;
            case "/skill":
                if (SkillRequest(rest) is { } skillRequest) await Send(skillRequest);
                return true;
            case "/tools": Tools(rest); return true;
            case "/network": Network(rest); return true;
            case "/mcp": await Mcp(rest); return true;
            case "/policy": Lines(CodeProjectPolicy.Command(rest, host, scope.Root)); return true;
            case "/bundle" or "/bundles": Lines(CodeBundles.Command(rest, host)); return true;
        }
        if (CodeCustomization.Expand(CodeCustomization.Commands(host, scope.Root), input) != null) return null;
        Term.Line(Term.Paint(Term.Yellow, "  Unknown command " + name + ".") + Term.Paint(Term.Dim, " /help lists them."));
        return true;
    }

    /// <summary>/theme and "mason theme": lists the families, or saves one to appearance.json for the CLI and the app.</summary>
    internal static void Theme(string name)
    {
        name = name.Trim();
        if (name.Length > 0)
        {
            if (!ThemeFamilies.TryFind(name, out var family))
            {
                Term.Line(Term.Paint(Term.Red, "  No theme called " + name + ". ") + "Choose one of: " + string.Join(", ", ThemeFamilies.All.Select(f => f.Slug)) + ".");
                return;
            }
            ThemeFamilies.Save(DataHome(), family);
            Term.UseTheme(family);
            Term.Line("  " + Term.Paint(Term.Accent + Term.Bold, "✓") + " Theme set to " + Term.Paint(Term.Accent, family.Name) + Term.Paint(Term.Dim, ". Mason Agent uses it the next time it starts."));
            return;
        }
        Term.Line();
        foreach (var family in ThemeFamilies.All)
        {
            bool current = family == Term.Theme;
            string swatch = Term.Styled ? $"\x1b[38;2;{(family.Dark.Accent >> 16) & 0xFF};{(family.Dark.Accent >> 8) & 0xFF};{family.Dark.Accent & 0xFF}m■{Term.Reset} \x1b[38;2;{(family.Light.Accent >> 16) & 0xFF};{(family.Light.Accent >> 8) & 0xFF};{family.Light.Accent & 0xFF}m■{Term.Reset}" : "";
            Term.Line($"  {(current ? Term.Paint(Term.Accent + Term.Bold, "›") : " ")} {swatch} {family.Slug.PadRight(12)}{(current ? Term.Paint(Term.Dim, "current") : "")}");
        }
        Term.Line(Term.Paint(Term.Dim, "  /theme NAME chooses one. It is shared with Mason Agent's Appearance setting."));
    }

    static void Help()
    {
        (string, string)[] rows =
        [
            ("/new [request]", "Start a separate session, optionally with its first request"),
            ("/resume [number]", "List this folder's sessions, or continue one"),
            ("/model [number|name]", "List running and saved connections, or switch model"),
            ("/models", "List the models on this computer and what each is for"),
            ("/models start NUMBER|NAME [16k] [cpu]", "Choose a context and start a coding model here; a size skips the picker"),
            ("/models stop", "Stop the model this session started"),
            ("/models search TEXT", "Search Hugging Face"),
            ("/models download REPO [BUILD]", "List a repository's builds, or download one"),
            ("/models setup", "Suggest coding models that fit this computer and download one"),
            ("/models runtime", "Install or update llama.cpp for this computer's GPU"),
            ("/ask · /build", "Answer questions only, or make changes"),
            ("/permission read|review|edits|auto", "What Mason may do without asking (also /review, /edits, /auto)"),
            ("/diff [path]", "Show what this session changed"),
            ("/undo · /redo", "Put back, or reapply, everything the latest request changed"),
            ("/compact", "Summarize a long conversation to free context"),
            ("/rules [revoke NUMBER]", "Saved always-allow rules for this project"),
            ("/languages [all|install NAME]", "The language server that checks each language's edits; install one"),
            ("/agent [name|none]", "Use a custom agent from .mason/agents"),
            ("/commands", "Custom commands from .mason/commands (run as /name arguments)"),
            ("/queue", "Resume input queued before a stop"),
            ("/status", "Session, model, permission and storage"),
            ("/notes · /note TEXT", "Notes Mason reads with every request; add, remove N or clear them"),
            ("/theme [name]", "List the theme families, or choose one (shared with Mason Agent)"),
            ("/exit", "Leave (Ctrl+C twice also works)"),
        ];
        rows = [.. rows[..^2], .. HarnessHelp, .. rows[^2..]];
        Term.Line();
        foreach (var (command, text) in rows) Term.Line($"  {Term.Paint(Term.Accent, command.PadRight(36))} {Term.Paint(Term.Dim, text)}");
        Term.Line();
        Term.Line(Term.Paint(Term.Dim, "  While Mason works: Esc or Ctrl+C stops the run. End a line with \\ to continue on the next line."));
    }

    void Resume(string rest)
    {
        var mine = store.Sessions.Where(s => s.Workspace.Equals(scope.Root, StringComparison.OrdinalIgnoreCase) && (s.Turns.Count > 0 || s == session)).OrderByDescending(s => s.Updated).Take(20).ToList();
        if (rest.Length == 0)
        {
            Term.Line();
            for (int i = 0; i < mine.Count; i++)
                Term.Line($"  {Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(2))}  {(mine[i] == session ? Term.Paint(Term.Bold, Term.Clip(mine[i].Title, 60)) : Term.Clip(mine[i].Title, 60))}" +
                    Term.Paint(Term.Dim, $"  ·  {mine[i].State}  ·  {mine[i].Updated.LocalDateTime:g}" + (mine[i] == session ? "  ← current" : "")));
            Term.Line(Term.Paint(Term.Dim, "  Continue one with /resume NUMBER."));
            return;
        }
        if (!int.TryParse(rest, out var number) || number < 1 || number > mine.Count) throw new ArgumentException("Use a number from the /resume list.");
        session = mine[number - 1];
        Term.Line("  Resumed " + Term.Paint(Term.Bold, session.Title) + Term.Paint(Term.Dim, "  ·  " + ModeName()));
        if (session.Turns.LastOrDefault(t => t.Role == "assistant") is { Text.Length: > 0 } last)
        {
            Term.Line();
            foreach (var line in Term.Markdown(last.Text).Take(12)) Term.Line("  " + Term.Paint(Term.Dim, "│ ") + line);
        }
    }

    void Status()
    {
        void Row(string label, string value) => Term.Line($"  {Term.Paint(Term.Dim, label.PadRight(12))} {value}");
        Term.Line();
        Row("Project", scope.Root);
        Row("Session", session.Title + Term.Paint(Term.Dim, "  " + session.Id.ToString("N")[..8] + " · " + session.State));
        Row("Model", profile == null ? Term.Paint(Term.Yellow, "none connected") : profile.DisplayName + Term.Paint(Term.Dim, "  " + profile.Endpoint));
        Row("Mode", ModeName());
        Row("Shell", CodeShells.Name(CodeShells.Effective(session)) + (CodeShells.Unavailable(session) is { } reason ? Term.Paint(Term.Yellow, "  " + reason) : ""));
        Row("Network", session.Network.ToString());
        if (session.Agent is { Length: > 0 } agent) Row("Agent", agent);
        if (context.Length > 0) Row("Context", context);
        Row("Stored in", store.Root);
    }

    void Diff(string filter)
    {
        var changes = session.Changes.Where(c => !c.Restored && (filter.Length == 0 || c.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
        if (changes.Count == 0) { Term.Line(Term.Paint(Term.Dim, filter.Length == 0 ? "  This session has not changed any files." : "  No changed file matches " + filter + ".")); return; }
        int budget = 400;
        foreach (var change in changes)
        {
            var rows = CodeProtocol.Diff(change.Before, change.After).ToList();
            Term.Line();
            Term.Line("  " + Term.Paint(Term.Bold, change.Path) + "  " + Term.Paint(Term.Green, "+" + rows.Count(r => r.Kind == '+')) + " " + Term.Paint(Term.Red, "−" + rows.Count(r => r.Kind == '-')) + (change.Before == null ? Term.Paint(Term.Dim, "  new file") : ""));
            // Keep two lines of context around each change and mark the gaps.
            var keep = new bool[rows.Count];
            for (int i = 0; i < rows.Count; i++) if (rows[i].Kind != ' ') for (int j = Math.Max(0, i - 2); j <= Math.Min(rows.Count - 1, i + 2); j++) keep[j] = true;
            bool gap = false;
            for (int i = 0; i < rows.Count && budget > 0; i++)
            {
                if (!keep[i]) { gap = true; continue; }
                if (gap && i > 0) Term.Line(Term.Paint(Term.Dim, "       ⋯"));
                gap = false; budget--;
                var row = rows[i]; string number = (row.After ?? row.Before)?.ToString() ?? "";
                string text = Term.Clip(row.Text.Replace("\t", "    "), Term.Width - 12);
                Term.Line(Term.Paint(Term.Dim, number.PadLeft(6) + " ") + (row.Kind switch { '+' => Term.Paint(Term.Green, "+ " + text), '-' => Term.Paint(Term.Red, "- " + text), '@' => Term.Paint(Term.Dim, "  " + text), _ => "  " + text }));
            }
            if (budget <= 0) { Term.Line(Term.Paint(Term.Dim, "  … more changes not shown. Use /diff PATH for one file.")); return; }
        }
    }

    void Rewind(bool undo)
    {
        var turn = undo ? CodeRewind.LatestUndoable(session) : CodeRewind.LatestRedoable(session);
        if (turn == null) { Term.Line(Term.Paint(Term.Dim, undo ? "  Nothing to undo in this session." : "  Nothing to redo.")); return; }
        Term.Line("  " + (undo ? "Undo" : "Redo") + ": " + Term.Paint(Term.Bold, Term.Clip(turn.Text.ReplaceLineEndings(" "), Term.Width - 14)));
        if (undo)
        {
            var plan = CodeRewind.PreviewUndo(store, session, turn);
            if (plan.Length == 0) { Term.Line(Term.Paint(Term.Dim, "  That request changed no files.")); }
            foreach (var change in plan.Take(30))
                Term.Line("    " + (change.Conflict ? Term.Paint(Term.Yellow, "! " + change.Path + "  changed since; will be left alone") : (change.To == null ? Term.Paint(Term.Red, "- ") : change.From == null ? Term.Paint(Term.Green, "+ ") : Term.Paint(Term.Accent, "~ ")) + change.Path));
            if (plan.Length > 30) Term.Line(Term.Paint(Term.Dim, $"    … and {plan.Length - 30} more"));
            if (Term.Interactive)
            {
                Term.Write($"  Put these files back?  {Term.Paint(Term.Accent, "y")} yes   {Term.Paint(Term.Accent, "n")} no  › ");
                if (Choose("yn\x1b", CancellationToken.None) != 'y') { Term.Line("no"); return; }
                Term.Line("yes");
            }
        }
        var result = undo ? CodeRewind.Undo(store, session, turn.Id) : CodeRewind.Redo(store, session, turn.Id);
        Term.Line(Term.Paint(Term.Green, $"  ✓ {result.Restored} restored, {result.Created} created, {result.Deleted} deleted") +
            (result.Conflicts.Length > 0 ? Term.Paint(Term.Yellow, $"  ·  left alone: {string.Join(", ", result.Conflicts.Take(8))}") : ""));
    }

    void Rules(string rest)
    {
        var rules = CodeApprovalRules.Load(host, scope.Root);
        if (rest.StartsWith("revoke", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(rest[6..].Trim(), out var number) || number < 1 || number > rules.Count) throw new ArgumentException("Use /rules revoke NUMBER from the /rules list.");
            CodeApprovalRules.Remove(host, scope.Root, rules[number - 1]);
            Term.Line(Term.Paint(Term.Dim, "  Revoked: " + rules[number - 1].Label)); return;
        }
        if (rules.Count == 0) { Term.Line(Term.Paint(Term.Dim, "  No saved rules. Choose \"a\" on an approval to save one for this project.")); return; }
        Term.Line();
        for (int i = 0; i < rules.Count; i++) Term.Line($"  {Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(2))}  {(rules[i].Allow ? Term.Paint(Term.Green, "allow") : Term.Paint(Term.Red, "deny "))}  {rules[i].Tool.Replace('_', ' ')}  {Term.Paint(Term.Cyan, rules[i].Subject.Length == 0 ? "(any)" : rules[i].Subject)}");
        Term.Line(Term.Paint(Term.Dim, "  Remove one with /rules revoke NUMBER. Rules are shared with the desktop app and never widen the sandbox."));
    }

    void Agent(string rest)
    {
        var agents = CodeCustomization.Agents(host, scope.Root);
        if (rest.Length == 0)
        {
            if (agents.Count == 0) { Term.Line(Term.Paint(Term.Dim, "  No custom agents. Add Markdown files to .mason/agents.")); return; }
            foreach (var a in agents) Term.Line($"  {(a.Name.Equals(session.Agent, StringComparison.OrdinalIgnoreCase) ? Term.Paint(Term.Green, "●") : Term.Paint(Term.Dim, "○"))} {Term.Paint(Term.Accent, a.Name)}  {Term.Paint(Term.Dim, a.Description)}");
            Term.Line(Term.Paint(Term.Dim, "  Choose with /agent NAME, or /agent none."));
            return;
        }
        if (rest.Equals("none", StringComparison.OrdinalIgnoreCase)) { session.Agent = null; store.Save(session); Term.Line(Term.Paint(Term.Dim, "  No agent selected.")); return; }
        var agent = agents.FirstOrDefault(a => a.Name.Equals(rest, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("No agent named " + rest + ". /agent lists them.");
        session.Agent = agent.Name; if (agent.Mode == "ask") session.Mode = "ask";
        store.Save(session);
        Term.Line("  Agent: " + Term.Paint(Term.Bold, agent.Name) + Term.Paint(Term.Dim, "  ·  " + ModeName()));
    }
}
