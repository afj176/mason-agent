using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mason;
using Mason.Code;

/// <summary>
/// mason chat: a conversation, not the coding agent. It uses the same conversation store as the app's Chat, so a chat
/// started in either place continues in the other, and the same tools: the web, skills and MCP, each switchable per
/// conversation. Chat is not tied to a project, so it never gets file, shell or code tools.
/// </summary>
internal sealed partial class Interactive
{
    ChatLibrary? chats;
    ChatConversation? conversation;
    readonly Queue<(ChatApprovalRequest Request, TaskCompletionSource<ChatApprovalAnswer> Answer)> chatApprovals = new();
    static readonly string[] ReasoningLevels = ["off", "low", "medium", "high", "default"];

    static readonly (string, string)[] ChatHelp =
    [
        ("/new", "Start a new conversation"),
        ("/resume [number]", "List recent chats, or continue one"),
        ("/attach FILE", "Attach a file to your next message (text, Markdown, CSV, JSON, HTML, code, DOCX)"),
        ("/reasoning off|low|medium|high|default", "How much the model thinks before answering"),
        ("/tools", "The tools this conversation offers: web, skills and MCP"),
        ("/tools on|off web|skills|mcp", "Switch a kind of tool on or off for this conversation"),
        ("/model [number|name]", "List running and saved connections, or switch model"),
        ("/help", "This list"),
        ("/exit", "Leave (Ctrl+C twice also works)"),
    ];

    public static async Task<int> StartChat(string[] args)
    {
        Term.Start();
        var values = new Dictionary<string, string>(StringComparer.Ordinal); bool resume = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--resume") { resume = true; continue; }
            var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : "";
            if (name is not ("endpoint" or "model") || i + 1 >= args.Length || !values.TryAdd(name, args[++i]))
                throw new ArgumentException("Unknown or repeated option " + args[i] + ". Run mason --help.");
        }
        if (values.ContainsKey("endpoint") != values.ContainsKey("model")) throw new ArgumentException("Use --endpoint and --model together.");
        string host = Path.GetFullPath(DataHome());
        // Chat has no project. The engine's store still remembers which model the chat used last.
        var place = Directory.CreateDirectory(Path.Combine(host, "cli", "chat")).FullName;
        ChatLibrary library;
        try { library = new ChatLibrary(host); }
        catch (IOException)
        {
            Console.Error.WriteLine("The chat history is busy. Try mason chat again in a moment.");
            return 1;
        }
        using (library)
        {
            using var store = Open(host, place);
            var session = store.Sessions.FirstOrDefault(s => s.Workspace.Equals(place, StringComparison.OrdinalIgnoreCase)) ?? store.New(place);
            var app = new Interactive(host, new CodeWorkspace(place), store, session) { chats = library };
            if (values.TryGetValue("endpoint", out var endpoint))
                app.profile = new AiEndpointProfile { Name = values["model"], Endpoint = LocalAiClient.ValidateEndpoint(endpoint).AbsoluteUri, Model = values["model"], ManagedCapability = AiCapability.Chat };
            try { return await app.ChatLoop(resume); }
            finally { app.StopAllModels(); }
        }
    }

    async Task<int> ChatLoop(bool resume)
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; if (run is { } active) { try { active.Cancel(); } catch (ObjectDisposedException) { } } else Interlocked.Increment(ref interrupts); };
        var library = chats!;
        if (resume)
        {
            conversation = library.State.Conversations.Where(c => c.Turns.Count > 0).OrderByDescending(c => c.Updated).FirstOrDefault();
            if (conversation == null) Term.Line(Term.Paint(Term.Dim, "  There is no earlier chat to continue; starting a new one."));
        }
        if (conversation == null) { conversation = library.New(); SaveChats(); }
        else library.State.Selected = conversation.Id;
        // The conversation keeps the model it last used; otherwise the first saved connection that answers.
        if (profile == null && Saved().FirstOrDefault(p => p.Id == conversation.ProfileId) is { } saved) await SelectModel(saved.Model, quiet: true);
        if (profile == null) await SelectModel(null, quiet: true);
        Term.Line();
        Term.Line("  " + Term.Paint(Term.Bold + Term.Accent, "Mason Chat") + Term.Paint(Term.Dim, "  ·  model ") + (profile?.DisplayName ?? Term.Paint(Term.Yellow, "none connected")) + Term.Paint(Term.Dim, "  ·  /help for commands"));
        if (resume && conversation.Turns.Count > 0) ShowRecent(conversation);
        if (profile == null) NoModelHelp();
        while (true)
        {
            var input = await ReadInput("chat");
            if (input == null) { Term.Line(); return 0; }
            if (input.Length == 0) continue;
            try
            {
                if (input[0] == '/') { if (!await ChatCommand(input)) return 0; continue; }
                await ChatSend(input);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Term.Line(Term.Paint(Term.Red, "✗ " + error.Message)); }
        }
    }

    void SaveChats()
    {
        try { chats!.Save(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { Term.Line(Term.Paint(Term.Yellow, "  History was not saved: " + e.Message)); }
    }

    static void ShowRecent(ChatConversation chat)
    {
        Term.Line(Term.Paint(Term.Dim, "  Continuing ") + chat.Title);
        foreach (var turn in chat.Turns.TakeLast(4))
        {
            Term.Line();
            Term.Line("  " + Term.Paint(Term.Bold, turn.Role == "user" ? "You" : turn.Model.Length > 0 ? turn.Model : "Assistant"));
            foreach (var line in Term.Markdown(turn.Text).Take(12)) Term.Line("  " + Term.Paint(Term.Dim, "│ ") + line);
        }
    }

    async Task<bool> ChatCommand(string input)
    {
        int space = input.IndexOf(' ');
        string name = (space < 0 ? input : input[..space]).ToLowerInvariant(), rest = space < 0 ? "" : input[(space + 1)..].Trim();
        var library = chats!; var chat = conversation!;
        switch (name)
        {
            case "/exit" or "/quit": return false;
            case "/help" or "/?":
                Term.Line();
                foreach (var (command, text) in ChatHelp) Term.Line($"  {Term.Paint(Term.Accent, command.PadRight(40))} {Term.Paint(Term.Dim, text)}");
                Term.Line(); Term.Line(Term.Paint(Term.Dim, "  While Mason answers: Esc or Ctrl+C stops it. Approvals take one key: y once, a always, n no."));
                return true;
            case "/new":
                conversation = library.New(profile?.Id); SaveChats();
                Term.Line(Term.Paint(Term.Dim, "  New conversation."));
                return true;
            case "/resume":
            {
                library.Refresh();
                var recent = library.State.Conversations.Where(c => c.Turns.Count > 0).OrderByDescending(c => c.Updated).Take(20).ToList();
                if (recent.Count == 0) { Term.Line(Term.Paint(Term.Dim, "  There are no earlier chats yet.")); return true; }
                if (rest.Length == 0)
                {
                    Term.Line();
                    for (int i = 0; i < recent.Count; i++)
                        Term.Line($"  {Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(2))}  {(recent[i] == chat ? Term.Paint(Term.Bold, Term.Clip(recent[i].Title, 60)) : Term.Clip(recent[i].Title, 60))}" +
                            Term.Paint(Term.Dim, $"  ·  {recent[i].Turns.Count(t => t.Role == "user")} messages  ·  {recent[i].Updated.LocalDateTime:g}" + (recent[i] == chat ? "  ← current" : "")));
                    if (!Term.Interactive) { Term.Line(Term.Paint(Term.Dim, "  Continue one with /resume NUMBER.")); return true; }
                    Term.Write("  Number to continue (Enter keeps this one): ");
                    rest = Console.ReadLine()?.Trim() ?? "";
                    if (rest.Length == 0) return true;
                }
                if (!int.TryParse(rest, out var number) || number < 1 || number > recent.Count) throw new ArgumentException("Use a number from the /resume list.");
                conversation = recent[number - 1]; library.State.Selected = conversation.Id; SaveChats();
                ShowRecent(conversation);
                return true;
            }
            case "/attach":
            {
                if (rest.Length == 0) throw new ArgumentException("Use /attach FILE.");
                if (chat.DraftAttachments.Count >= 6) throw new ArgumentException("Attach up to six files per message.");
                var attachment = library.Import(Path.GetFullPath(rest.Trim('"')));
                chat.DraftAttachments.Add(attachment); SaveChats();
                Term.Line(Term.Paint(Term.Dim, "  Attached " + attachment.Label + ". It goes with your next message."));
                return true;
            }
            case "/reasoning":
                if (rest.Length == 0) { Term.Line("  Reasoning: " + chat.Generation.Reasoning + Term.Paint(Term.Dim, "  ·  /reasoning off|low|medium|high|default")); return true; }
                if (!ReasoningLevels.Contains(rest.ToLowerInvariant())) throw new ArgumentException("Use /reasoning off, low, medium, high or default.");
                chat.Generation.Reasoning = rest.ToLowerInvariant(); SaveChats();
                Term.Line(Term.Paint(Term.Dim, "  Reasoning: " + chat.Generation.Reasoning + "."));
                return true;
            case "/tools":
            {
                var words = rest.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 2 && words[0] is "on" or "off" && words[1] is "web" or "skills" or "mcp")
                {
                    bool on = words[0] == "on";
                    if (words[1] == "web") chat.Tools.Web = on; else if (words[1] == "skills") chat.Tools.Skills = on; else chat.Tools.Mcp = on;
                    SaveChats();
                }
                else if (words.Length != 0) throw new ArgumentException("Use /tools, or /tools on|off web|skills|mcp.");
                ShowChatTools(chat);
                return true;
            }
            case "/model":
                if (rest.Length == 0) await ListModels();
                else if (await SelectModel(rest)) { Term.Line("  Model: " + Term.Paint(Term.Bold, profile!.DisplayName)); chat.ProfileId = profile.Id; SaveChats(); }
                return true;
            case "/models": await ModelsCommand(rest); return true;
        }
        Term.Line(Term.Paint(Term.Yellow, "  Unknown command " + name + ".") + Term.Paint(Term.Dim, " /help lists them."));
        return true;
    }

    void ShowChatTools(ChatConversation chat)
    {
        string State(bool on) => on ? Term.Paint(Term.Green, "on ") : Term.Paint(Term.Yellow, "off");
        bool internet = ChatTools.InternetOn(host);
        int skills = CodeSkills.Computer(host).Count, servers = CodeMcpRegistry.Active(host).Count(s => s.Tools.Length > 0);
        Term.Line("  " + "web".PadRight(8) + State(chat.Tools.Web) + Term.Paint(Term.Dim, "  web_fetch and web_search; every new host asks" + (internet ? "" : " · the computer's internet setting is off (/network computer ask in mason)")));
        Term.Line("  " + "skills".PadRight(8) + State(chat.Tools.Skills) + Term.Paint(Term.Dim, "  read_skill · " + skills + " skill" + (skills == 1 ? "" : "s") + " from bundles and " + Path.Combine(host, "skills")));
        Term.Line("  " + "mcp".PadRight(8) + State(chat.Tools.Mcp) + Term.Paint(Term.Dim, "  " + servers + " MCP server" + (servers == 1 ? "" : "s") + " with allowed tools; every call asks"));
        Term.Line(Term.Paint(Term.Dim, "  Tools are offered only to a llama.cpp connection; otherwise: " + ChatTools.Unsupported));
    }

    async Task ChatSend(string input)
    {
        var library = chats!; var chat = conversation!;
        if (profile == null && !await SelectModel(null)) return;
        if (!await Serves(profile!, key)) { Term.Line(Term.Paint(Term.Yellow, "  " + profile!.DisplayName + " is not answering at " + profile.Endpoint + ". Start it, or choose another with /model.")); return; }
        using var cancellation = new CancellationTokenSource(); run = cancellation;
        using var client = new LocalAiClient();
        var attached = chat.DraftAttachments.ToList();
        var content = ChatRequests.UserContent(input, attached);
        var options = JsonSerializer.Deserialize<ChatGenerationOptions>(JsonSerializer.Serialize(chat.Generation))!;
        string status = "Checking the model";
        ChatServerInfo server; ChatPreparedRequest prepared;
        try
        {
            Term.Status("  " + status + "…");
            server = await client.ChatServer(profile!, key, cancellation.Token);
            if (options.Reasoning != "default" && !server.Reasoning) throw new ArgumentException("This model has not reported reasoning controls. Use /reasoning default.");
            prepared = await ChatCompactor.Prepare(client, profile!, key, chat, content, chat.Turns.Count, options, server, note => Term.Status("  " + note), cancellation.Token);
        }
        finally { Term.Status(""); }
        if (prepared.Summary is { } summary) { chat.Compactions.Add(summary); SaveChats(); }
        if (prepared.Notice.Length > 0) Term.Line(Term.Paint(Term.Dim, "  " + prepared.Notice));
        if (!ChatTools.Supported(server) && ChatTools.Offered(host, chat.Tools).Length > 0) Term.Line(Term.Paint(Term.Dim, "  " + ChatTools.Unsupported + " This message is sent without tools."));
        var user = new ChatTurn { Text = input, RequestText = content, Attachments = attached };
        var answer = new ChatTurn { Role = "assistant", State = "Streaming", Model = profile!.DisplayName, BudgetSummary = prepared.Plan.Summary };
        chat.Turns.Add(user); chat.Turns.Add(answer); chat.Updated = DateTimeOffset.UtcNow; chat.ProfileId = profile.Id;
        if (chat.Title == "New conversation") chat.Title = input.Length > 54 ? input[..54] + "…" : input;
        chat.DraftAttachments.Clear(); SaveChats();

        var text = new StringBuilder(); var reasoning = new StringBuilder(); var steps = new Queue<ChatToolStep>();
        var tools = new ChatTools.Session(host, (request, token) =>
        {
            var reply = new TaskCompletionSource<ChatApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => reply.TrySetCanceled(token));
            lock (chatApprovals) chatApprovals.Enqueue((request, reply));
            return reply.Task;
        });
        var work = Task.Run(() => ChatTools.Run(client, profile, key, prepared.Messages, prepared.Plan.Output, chat.MaxTokens, options, server, chat.Tools, tools,
            step => { lock (steps) steps.Enqueue(step); },
            chunk => { lock (text) text.Append(chunk); }, chunk => { lock (reasoning) reasoning.Append(chunk); },
            () => { lock (text) text.Clear(); }, cancellation.Token));
        var clock = Stopwatch.StartNew(); int frame = 0; bool stopping = false;
        Term.Line();
        void ShowSteps()
        {
            while (true)
            {
                ChatToolStep? step; lock (steps) step = steps.Count > 0 ? steps.Dequeue() : null;
                if (step == null) return;
                answer.Steps.Add(step);
                Term.Status("");
                Term.Line("  " + (step.Failed ? Term.Paint(Term.Yellow, "⊘") : Term.Paint(Term.Green, "✓")) + " " + Term.Paint(Term.Bold, step.Tool.Replace('_', ' ')) + " " + Term.Clip(step.Subject, Math.Max(10, Term.Width - 40)) + Term.Paint(Term.Dim, step.Outcome.Length > 0 ? "  " + step.Outcome : ""));
            }
        }
        try
        {
            while (!work.IsCompleted)
            {
                (ChatApprovalRequest Request, TaskCompletionSource<ChatApprovalAnswer> Answer)? waiting = null;
                lock (chatApprovals) if (chatApprovals.Count > 0) waiting = chatApprovals.Dequeue();
                if (waiting is { } ask) { ShowSteps(); Term.Status(""); ask.Answer.TrySetResult(ChatApprove(ask.Request, cancellation.Token)); continue; }
                if (Term.Interactive && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape && !stopping) cancellation.Cancel();
                if (cancellation.IsCancellationRequested && !stopping) { stopping = true; Term.Status(""); Term.Line(Term.Paint(Term.Dim, "  Stopping…")); }
                ShowSteps();
                string live; lock (text) live = text.ToString(); if (live.Length == 0) lock (reasoning) live = reasoning.Length > 0 ? "Thinking" : "";
                live = live.ReplaceLineEndings(" ").Trim(); int room = Math.Max(20, Term.Width - 36);
                Term.Status($"  {"⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[frame++ / 2 % 10]} {(live.Length == 0 ? "Waiting for the model" : live.Length > room ? "…" + live[^(room - 1)..] : live)}  ·  {clock.Elapsed.TotalSeconds:0}s  ·  Esc to stop");
                await Task.Delay(60);
            }
        }
        finally { run = null; Term.Status(""); lock (chatApprovals) chatApprovals.Clear(); }
        ShowSteps();
        try
        {
            var result = await work;
            var feedback = ChatCompletionFeedback.From(result, prepared.Plan);
            answer.State = feedback.State; answer.StatusDetail = feedback.Detail; answer.PerformanceSummary = result.PerformanceSummary;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            answer.State = error is OperationCanceledException ? "Cancelled" : error is ChatNoFinalAnswerException ? "No final answer" : "Failed";
            answer.StatusDetail = error is OperationCanceledException ? "Stopped before the answer finished." : error.Message;
        }
        lock (text) answer.Text = text.ToString();
        lock (reasoning) answer.Reasoning = reasoning.ToString();
        chat.Updated = DateTimeOffset.UtcNow; SaveChats();
        if (answer.Text.Length > 0) { foreach (var line in Term.Markdown(answer.Text)) Term.Line("  " + line); Term.Line(); }
        var mark = answer.State == "Complete" ? Term.Paint(Term.Green, "✓") : Term.Paint(Term.Yellow, "●");
        Term.Line("  " + mark + " " + Term.Paint(Term.Dim, answer.State + (answer.PerformanceSummary.Length > 0 ? "  ·  " + answer.PerformanceSummary : "") + $"  ·  {clock.Elapsed.TotalSeconds:0}s"));
        if (answer.StatusDetail.Length > 0) foreach (var line in Wrap(answer.StatusDetail, Term.Width - 6)) Term.Line("    " + Term.Paint(Term.Dim, line));
    }

    /// <summary>One key: y once, a always, n no.</summary>
    static ChatApprovalAnswer ChatApprove(ChatApprovalRequest request, CancellationToken cancellation)
    {
        string bar = Term.Paint(Term.Yellow, "  ▌ ");
        Term.Line();
        Term.Line(bar + Term.Paint(Term.Bold, request.Title));
        foreach (var line in request.Detail.ReplaceLineEndings("\n").Split('\n').Take(12)) Term.Line(bar + Term.Paint(Term.Dim, Term.Clip(line, Term.Width - 6)));
        if (!Term.Interactive) { Term.Line(bar + "Declined: there is no terminal to ask."); return ChatApprovalAnswer.No; }
        string always = request.Kind == "host" ? "always (Chat)" : "always";
        Term.Write($"  {Term.Paint(Term.Bold, "Allow?")}  {Term.Paint(Term.Accent, "y")} once   {Term.Paint(Term.Accent, "a")} {always}   {Term.Paint(Term.Accent, "n")} no  › ");
        char choice = Choose("yan\x1b", cancellation);
        switch (choice)
        {
            case 'y': Term.Line(Term.Paint(Term.Green, "allowed once")); return ChatApprovalAnswer.Once;
            case 'a': Term.Line(Term.Paint(Term.Green, "always allowed")); return ChatApprovalAnswer.Always;
            case '\0': Term.Line("cancelled"); return ChatApprovalAnswer.No;
            default: Term.Line(Term.Paint(Term.Yellow, "declined")); return ChatApprovalAnswer.No;
        }
    }
}
