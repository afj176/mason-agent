using System.Net;
using System.Text;
using System.Text.Json;
using Mason;
using Mason.Code;

/// <summary>Skills, the tool catalogue, the web tools and Chat's tools. No check here touches the network: the fetcher
/// and the resolver are injected, the way the relay checks inject theirs.</summary>
internal static class SkillsToolsWebChecks
{
    sealed class CapturingModel(params CodeAction[] actions) : ICodeModel
    {
        int index;
        public string System = "";
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation)
        {
            if (index == 0) System = messages.First(m => m.Role == "system").Content;
            return Task.FromResult(index < actions.Length ? actions[index++] : new CodeAction("Finished.", null, default));
        }
    }

    static CodeAction Call(string tool, object arguments) => new("Working.", tool, JsonSerializer.SerializeToElement(arguments));

    static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

    internal static async Task Run(string root, Action<bool, string> check)
    {
        var area = Directory.CreateDirectory(Path.Combine(root, "skills-tools-web")).FullName;
        var profile = Directory.CreateDirectory(Path.Combine(area, "profile")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(area, "project")).FullName;

        // ── Skills: precedence, shadowing, descriptions ───────────────────────────────────────────────
        Write(Path.Combine(project, ".mason", "skills", "deploy", "SKILL.md"), "---\nname: deploy\ndescription: Ship to staging\n---\n\n# deploy\nRun deploy.cmd.\n");
        Write(Path.Combine(project, ".claude", "skills", "deploy", "SKILL.md"), "---\ndescription: The older deploy\n---\nOld steps.\n");
        Write(Path.Combine(project, ".agents", "skills", "deploy", "SKILL.md"), "---\ndescription: Oldest deploy\n---\nOldest.\n");
        Write(Path.Combine(project, ".agents", "skills", "review", "SKILL.md"), "Review with no front matter.\n");
        Write(Path.Combine(project, ".claude", "skills", "bad name", "SKILL.md"), "---\ndescription: invalid folder name\n---\n");
        CodeBundles.Save(profile, new HarnessBundle("tools", "1.0", new() { ["lint"] = "---\ndescription: Lint the code\n---\nRun the linter." }, [], []), true);
        CodeBundles.Save(profile, new HarnessBundle("unused", "1.0", new() { ["hidden"] = "---\ndescription: Off bundle\n---\nNo." }, [], []), false);
        var skills = CodeSkills.List(profile, project);
        check(skills.Select(s => s.Name + "|" + s.Source + "|" + s.State).SequenceEqual(["deploy|project|on", "deploy|.claude|hidden", "deploy|.agents|hidden", "review|.agents|on", "tools/lint|bundle tools|on"]),
            "Skills load in order: .mason/skills, .claude/skills, .agents/skills, then enabled bundles named bundle/key");
        check(skills[1].ShadowedBy == ".mason/skills/deploy" && skills[2].ShadowedBy == ".mason/skills/deploy", "A shadowed skill names the one that hides it");
        check(skills[0].Description == "Ship to staging" && skills[3].Summary == "No description" && skills[4].Description == "Lint the code",
            "Descriptions come from front matter, bundle skills included; none reads No description");
        check(CodeSkills.Read(skills, "deploy").Contains("Run deploy.cmd.") && !CodeSkills.Read(skills, "deploy").Contains("Old steps"), "read_skill returns the winning skill's body");
        var prompt = CodeSkills.Prompt(CodeSkills.Active(profile, project));
        check(prompt.Contains("- deploy: Ship to staging") && prompt.Contains("- review: No description") && prompt.Contains("- tools/lint: Lint the code") && prompt.EndsWith(CodeSkills.PromptRule),
            "The prompt lists each enabled skill as NAME: description, then the read_skill line");

        // ── disabledSkills ────────────────────────────────────────────────────────────────────────────
        CodeSkills.Switch(profile, project, "review", false);
        check(CodeProjectPolicy.Load(profile, project)?.DisabledSkills?.SequenceEqual(["review"]) == true, "Switching a skill off writes the policy's disabledSkills");
        var active = CodeSkills.Active(profile, project);
        check(!CodeSkills.Prompt(active).Contains("review"), "A switched-off skill is left out of the prompt");
        try { CodeSkills.Read(CodeSkills.List(profile, project), "review"); check(false, "read_skill refuses a switched-off skill"); }
        catch (UnauthorizedAccessException e) { check(e.Message == "The skill review is switched off for this project.", "read_skill refuses a switched-off skill"); }

        using (var store = new CodeStore(Path.Combine(area, "store"), profile))
        {
            var session = store.New(project); session.Mode = "ask";
            var model = new CapturingModel(Call("read_skill", new { name = "review" }), Call("read_skill", new { name = "deploy" }));
            await new CodeEngine(store, model, (_, _) => throw new Exception("No approval expected"), () => { }).Run(session, "Deploy the site.", default);
            var reads = session.Activities.Where(a => a.Tool == "read_skill").ToArray();
            check(model.System.Contains("- deploy: Ship to staging") && !model.System.Contains("- review:") && model.System.Contains(CodeSkills.PromptRule),
                "The agent's system prompt lists enabled skills only");
            check(reads.Length == 2 && reads[0].State == "Denied" && reads[0].Result.Contains("switched off for this project") && reads[1].State == "Completed" && reads[1].Result.Contains("Run deploy.cmd."),
                "The engine's read_skill refuses a switched-off skill and reads an enabled one without enabling a group");
        }
        CodeSkills.Switch(profile, project, "review", true);
        check(CodeProjectPolicy.Load(profile, project)?.DisabledSkills is { Length: 0 }, "Switching a skill back on clears it from disabledSkills");

        // ── /skills new ───────────────────────────────────────────────────────────────────────────────
        var made = CodeSkills.Create(project, "release-notes", "Write the release notes");
        check(File.ReadAllText(made) == "---\nname: release-notes\ndescription: Write the release notes\n---\n\n# release-notes\n\n## When to use\n\n## Steps\n\n", "/skills new writes the SKILL.md template");
        bool Refused(string name) { try { CodeSkills.Create(project, name, ""); return false; } catch (Exception e) when (e is ArgumentException or IOException) { return true; } }
        check(Refused("release-notes") && Refused("deploy") && Refused("bad/name") && Refused("-dash") && Refused(new string('a', 49)), "/skills new refuses a duplicate or invalid name");

        // ── Tool catalogue ────────────────────────────────────────────────────────────────────────────
        var offered = new HashSet<string>(CodeSchema.Action().GetProperty("properties").GetProperty("tool").GetProperty("anyOf").EnumerateArray()
            .Where(t => t.TryGetProperty("properties", out _)).Select(t => t.GetProperty("properties").GetProperty("name").GetProperty("const").GetString()!));
        await using (var services = new CodeRunServices(new CodeWorkspace(project), default, [], profile))
        {
            var auto = new CodeSession { Workspace = project, Mode = "build", Permission = CodePermission.Auto, Network = CodeNetworkMode.Ask };
            foreach (var group in new[] { "code", "docs", "workflows", "jobs", "browser", "mcp", "web" }) services.Enable(group, auto);
            offered.UnionWith(services.Enabled);
        }
        var missing = offered.Where(t => CodeToolCatalog.Find(t) == null).ToArray();
        check(missing.Length == 0, "Every tool the engine can offer is in the catalogue" + (missing.Length > 0 ? ": missing " + string.Join(", ", missing) : ""));
        check(CodeToolCatalog.All.All(t => offered.Contains(t.Name) && CodeToolCatalog.Groups.Contains(t.Group) && t.Line.Length > 0) && CodeToolCatalog.All.Select(t => t.Name).Distinct().Count() == CodeToolCatalog.All.Count,
            "Each catalogue entry is a real tool with a group and a line");
        check(CodeToolCatalog.Find("web_fetch") is { Internet: true, Group: "web" } && CodeToolCatalog.Find("run_command") is { Changes: true } && CodeToolCatalog.Find("finish")!.AlwaysOn && CodeToolCatalog.Find("enable_tools")!.AlwaysOn,
            "The catalogue says which tools change things or use the internet, and which are always on");
        CodeProjectPolicy.SwitchTool(profile, project, "run_shell", false);
        check(CodeProjectPolicy.Load(profile, project)!.DisabledTools.Contains("run_shell"), "/tools off writes disabledTools");
        check(CodeToolCatalog.Describe(profile, project).Contains("run_shell") && CodeToolCatalog.Describe(profile, project).Split('\n').Any(l => l.Contains("run_shell") && l.Contains(" off ")), "/tools shows the tool as off");
        try { CodeProjectPolicy.SwitchTool(profile, project, "finish", false); check(false, "finish cannot be switched off"); }
        catch (InvalidOperationException) { check(true, "finish cannot be switched off"); }
        CodeProjectPolicy.SwitchTool(profile, project, "run_shell", true);
        check(!CodeProjectPolicy.Load(profile, project)!.DisabledTools.Contains("run_shell"), "/tools on removes it again");

        // ── Network off ───────────────────────────────────────────────────────────────────────────────
        await using (var services = new CodeRunServices(new CodeWorkspace(project), default, [], profile))
        {
            var offline = new CodeSession { Workspace = project, Mode = "ask", Permission = CodePermission.ReadOnly, Network = CodeNetworkMode.Ask };
            CodeNetworkSettings.SessionCommand("off", offline, profile);
            bool refused = false;
            try { services.Enable("web", offline); } catch (UnauthorizedAccessException) { refused = true; }
            check(offline.Network == CodeNetworkMode.Offline && refused && !services.Enabled.Contains("web_fetch"), "/network off stops the web group from being offered");
            CodeNetworkSettings.SessionCommand("ask", offline, profile);
            services.Enable("web", offline);
            check(services.Enabled.IsSupersetOf(["web_fetch", "web_search"]), "/network ask offers the web group in a read-only session");
        }
        CodeNetworkSettings.SetComputer(profile, CodeNetworkMode.Offline);
        check(ChatTools.Offered(profile, new ChatToolSwitches()).All(t => !t.StartsWith("web_", StringComparison.Ordinal)), "When the computer's setting is off, Chat has no web tools");
        using (var store = new CodeStore(Path.Combine(area, "store-offline"), profile)) check(store.New(project).Network == CodeNetworkMode.Offline, "A new session starts with the computer's setting");
        CodeNetworkSettings.SetComputer(profile, CodeNetworkMode.Ask);
        check(ChatTools.Offered(profile, new ChatToolSwitches()).Contains("web_fetch") && !ChatTools.Offered(profile, new ChatToolSwitches { Web = false }).Contains("web_fetch"), "Chat offers the web per conversation when the computer allows it");

        // ── Web hosts and redirects ───────────────────────────────────────────────────────────────────
        bool Denied(string url) { try { CodeWeb.Destination(url); return false; } catch (Exception e) when (e is UnauthorizedAccessException or ArgumentException) { return true; } }
        check(Denied("https://93.184.216.34/") && Denied("https://[2606:2800:220:1::1]/") && Denied("https://localhost/") && Denied("https://printer.local/") && Denied("https://example.com:8443/") &&
              Denied("http://example.com/") && Denied("https://user:pw@example.com/") && Denied("https://intranet/") && !Denied("https://example.com/docs"),
            "The host policy refuses IP literals, localhost, .local, non-443 ports, plain HTTP and credentials");
        var asked = new List<string>();
        Task<IPAddress[]> Public(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
        var privateWeb = new CodeWeb((h, _) => { asked.Add(h); return Task.FromResult(true); }, (_, _) => throw new Exception("Must not send to a private address"), (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") }));
        try { await privateWeb.Fetch("https://intranet.example.com/", 0, default); check(false, "A name that resolves to a private address is refused"); }
        catch (UnauthorizedAccessException e) { check(e.Message.Contains("private"), "A name that resolves to a private address is refused"); }
        var mixed = new CodeWeb((_, _) => Task.FromResult(true), (_, _) => throw new Exception("Must not send"), (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("127.0.0.1") }));
        try { await mixed.Fetch("https://rebind.example.com/", 0, default); check(false, "A name with any local address is refused"); } catch (UnauthorizedAccessException) { check(true, "A name with any local address is refused"); }

        asked.Clear();
        var requests = new List<string>();
        Task<HttpResponseMessage> Hop(HttpRequestMessage request, CancellationToken ct)
        {
            requests.Add(request.RequestUri!.AbsoluteUri + " " + request.Headers.UserAgent);
            if (request.Headers.Contains("Cookie") || request.Headers.Referrer != null || request.Headers.Authorization != null) throw new Exception("No cookies, Referer or credentials");
            HttpResponseMessage response = request.RequestUri!.Host switch
            {
                "start.example.com" when request.RequestUri.AbsolutePath == "/a" => new(HttpStatusCode.Found) { Headers = { Location = new Uri("/b", UriKind.Relative) } },
                "start.example.com" => new(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://docs.example.org/page") } },
                _ => new(HttpStatusCode.OK) { Content = new StringContent("<html><head><title>Docs</title></head><body><h1>Hello</h1><p>World</p></body></html>", Encoding.UTF8, "text/html") }
            };
            return Task.FromResult(response);
        }
        var web = new CodeWeb((h, _) => { asked.Add(h); return Task.FromResult(true); }, Hop, Public);
        var page = await web.Fetch("https://start.example.com/a", 0, default);
        check(asked.SequenceEqual(["start.example.com", "docs.example.org"]), "A redirect within a host does not ask again; a redirect to a new host asks again");
        check(page.StartsWith("Source: https://docs.example.org/page\nTitle: Docs\n") && page.Contains("# Hello") && page.Contains(CodeWeb.Untrusted), "web_fetch starts with the final address and title and marks the text untrusted");
        check(requests.All(r => r.EndsWith(" " + CodeWeb.UserAgent)) && CodeWeb.UserAgent.StartsWith("Mason-Agent/"), "Requests carry the Mason-Agent user agent and nothing else of the user's");
        check(web.Receipts.Any(r => r.Host == "docs.example.org" && r.Outcome.StartsWith("allowed · ")), "The receipt names the host and the size");
        var refusing = new CodeWeb((h, _) => Task.FromResult(h == "start.example.com"), Hop, Public);
        try { await refusing.Fetch("https://start.example.com/a", 0, default); check(false, "A denied host on a redirect stops the fetch"); }
        catch (UnauthorizedAccessException) { check(refusing.Receipts.Any(r => r.Host == "docs.example.org" && r.Outcome == "denied"), "A denied host on a redirect stops the fetch and is recorded"); }
        int hops = 0;
        var looping = new CodeWeb((_, _) => Task.FromResult(true), (r, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://loop.example.com/" + ++hops) } }), Public);
        try { await looping.Fetch("https://loop.example.com/0", 0, default); check(false, "More than five redirects stop"); } catch (IOException) { check(hops == 6, "More than five redirects stop"); }
        var binary = new CodeWeb((_, _) => Task.FromResult(true), (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("application/pdf") } } }), Public);
        try { await binary.Fetch("https://files.example.com/a.pdf", 0, default); check(false, "A refused content type says what it was"); }
        catch (InvalidDataException e) { check(e.Message == "The page at https://files.example.com/a.pdf is application/pdf, which web_fetch does not read.", "A refused content type says what it was"); }
        check(CodeWeb.Readable("text/plain") && CodeWeb.Readable("application/ld+json") && CodeWeb.Readable("application/atom+xml") && !CodeWeb.Readable("image/png"), "Text, JSON and XML types are read");
        var longText = string.Concat(Enumerable.Range(0, 5000).Select(i => "word" + i % 10 + " "));
        var first = CodeWeb.Page(new Uri("https://example.com/"), "", longText.Trim(), 0);
        check(first.EndsWith("(more: call web_fetch with offset 20000)") && CodeWeb.Page(new Uri("https://example.com/"), "", longText.Trim(), 20000).Contains("word0") && !CodeWeb.Page(new Uri("https://example.com/"), "", longText.Trim(), 20000).Contains("(more:"),
            "A long page is read in 20,000-character windows with the next offset");

        // ── HTML to text ──────────────────────────────────────────────────────────────────────────────
        const string fixture = """
            <!doctype html><html><head><title>Bread &amp; Butter</title><style>p{color:red}</style><script>alert('x')</script></head>
            <body><nav><a href="/home">Home</a> | <a href="/about">About</a></nav>
            <h1>Baking   bread</h1>
            <p>Flour, water &amp; salt.&nbsp;Knead for 10&#160;minutes &lt;not less&gt;.</p>
            <h2>Steps</h2>
            <ul><li>Mix</li><li>Rest <a href="steps/rest.html">longer</a></li></ul>
            <noscript>Enable JavaScript</noscript><svg><text>logo</text></svg>
            <p>See <a href="https://example.org/flour?a=1&amp;b=2">flour guide</a> or <a href="#top">top</a>.</p>
            <footer>Copyright 2026</footer><!-- hidden comment --></body></html>
            """;
        var text = CodeWeb.HtmlToText(fixture, new Uri("https://bake.example.com/guide/index.html"), out var title);
        check(title == "Bread & Butter", "The title is read and decoded");
        check(text == "# Baking bread\n\nFlour, water & salt. Knead for 10 minutes <not less>.\n\n## Steps\n\n- Mix\n- Rest [longer](https://bake.example.com/guide/steps/rest.html)\n\nSee [flour guide](https://example.org/flour?a=1&b=2) or top .",
            "HTML becomes text: dropped script, style, nav, noscript, svg and footer; # headings, - items, absolute links, decoded entities");

        // ── Search parsing ────────────────────────────────────────────────────────────────────────────
        const string results = """
            <div class="results">
            <div class="result results_links results_links_deep web-result">
              <div class="links_main links_deep result__body">
                <h2 class="result__title"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.rust-lang.org%2Flearn%3Fx%3D1%26y%3D2&amp;rut=abc">Learn <b>Rust</b></a></h2>
                <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.rust-lang.org%2Flearn">Get started with <b>Rust</b> &amp; Cargo.</a>
              </div></div>
            <div class="result results_links web-result">
              <h2 class="result__title"><a rel="nofollow" class="result__a" href="https://duckduckgo.com/l/?uddg=https%3A%2F%2Fdoc.rust-lang.org%2Fbook%2F&amp;rut=def">The Rust Programming Language</a></h2>
              <a class="result__snippet" href="#">The book.</a>
            </div>
            <div class="result result--ad"><a class="result__a" href="https://duckduckgo.com/y.js?ad_provider=x">Ad</a></div>
            </div>
            """;
        var parsed = CodeWeb.ParseSearch(results);
        check(parsed.Count == 2 && parsed[0] == new CodeWeb.Result("Learn Rust", "https://www.rust-lang.org/learn?x=1&y=2", "Get started with Rust & Cargo.") && parsed[1].Url == "https://doc.rust-lang.org/book/" && parsed[1].Snippet == "The book.",
            "DuckDuckGo results parse into title, decoded uddg address and snippet");
        check(CodeWeb.FormatResults(parsed).StartsWith("1. Learn Rust\n   https://www.rust-lang.org/learn?x=1&y=2\n   Get started with Rust & Cargo.\n\n2. "), "Results are a numbered list");
        check(CodeWeb.FormatResults(CodeWeb.ParseSearch("")) == CodeWeb.NoResults && CodeWeb.FormatResults(CodeWeb.ParseSearch("<html><body>Something new</body></html>")) == "No results, or DuckDuckGo changed its page.",
            "An empty or changed page gives the No results text");
        string searched = "";
        var searchWeb = new CodeWeb((_, _) => Task.FromResult(true), (r, _) => { searched = r.RequestUri!.AbsoluteUri; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(results, Encoding.UTF8, "text/html") }); }, Public);
        var found = await searchWeb.Search("learn rust & cargo", default);
        check(searched == "https://html.duckduckgo.com/html/?q=learn%20rust%20%26%20cargo" && found.Contains("1. Learn Rust"), "web_search asks DuckDuckGo's HTML page");

        // ── Chat tools ────────────────────────────────────────────────────────────────────────────────
        var chatHome = Directory.CreateDirectory(Path.Combine(area, "chat-home")).FullName;
        Write(Path.Combine(chatHome, "skills", "summarize", "SKILL.md"), "---\ndescription: Summarize a page\n---\nThree bullet points.\n");
        var answers = new Queue<ChatApprovalAnswer>([ChatApprovalAnswer.Always]);
        var questions = new List<string>();
        var chat = new ChatTools.Session(chatHome, (q, _) => { questions.Add(q.Subject); return Task.FromResult(answers.Count > 0 ? answers.Dequeue() : ChatApprovalAnswer.No); }) { Send = Hop, Resolve = Public };
        var switches = new ChatToolSwitches();
        check(ChatTools.Offered(chatHome, switches).SequenceEqual(["web_fetch", "web_search", "read_skill"]) && !ChatTools.Offered(chatHome, new ChatToolSwitches { Mcp = true }).Contains("mcp_call"),
            "Chat offers web and skills by default, and MCP only with a server that allows tools");
        var step = new ChatToolStep();
        var fetched = await ChatTools.Execute("web_fetch", JsonSerializer.Serialize(new { url = "https://docs.example.org/page" }), chat, switches, step, default);
        check(fetched.Contains("# Hello") && questions.SequenceEqual(["docs.example.org"]) && CodeNetworkSettings.ChatHosts(chatHome).Contains("docs.example.org") && step.Outcome.StartsWith("docs.example.org allowed"),
            "Always in Chat saves the host to Chat's own list and the step records the outcome");
        questions.Clear();
        var again = new ChatTools.Session(chatHome, (q, _) => { questions.Add(q.Subject); return Task.FromResult(ChatApprovalAnswer.No); }) { Send = Hop, Resolve = Public };
        await ChatTools.Execute("web_fetch", JsonSerializer.Serialize(new { url = "https://docs.example.org/page" }), again, switches, new ChatToolStep(), default);
        var deniedStep = new ChatToolStep();
        var denied = await ChatTools.Execute("web_fetch", JsonSerializer.Serialize(new { url = "https://start.example.com/b" }), again, switches, deniedStep, default);
        await ChatTools.Execute("web_fetch", JsonSerializer.Serialize(new { url = "https://start.example.com/b" }), again, switches, new ChatToolStep(), default);
        check(questions.SequenceEqual(["start.example.com"]) && denied.StartsWith("Error: ") && deniedStep.Failed && deniedStep.Outcome == "start.example.com denied",
            "A saved Chat host is not asked again, and a denied host stays denied for the message");
        var skill = await ChatTools.Execute("read_skill", "{\"name\":\"summarize\"}", chat, switches, new ChatToolStep(), default);
        var off = await ChatTools.Execute("read_skill", "{\"name\":\"summarize\"}", chat, new ChatToolSwitches { Skills = false }, new ChatToolStep(), default);
        check(skill == "Three bullet points." && off.StartsWith("Error: Skills are off"), "Chat reads computer-wide skills unless Skills is off");
        var mcpOff = await ChatTools.Execute("mcp_call", "{\"server\":\"x\",\"tool\":\"y\",\"arguments_json\":\"{}\"}", chat, switches, new ChatToolStep(), default);
        check(mcpOff.StartsWith("Error: MCP is off"), "MCP stays off in Chat until switched on");
        var definitions = ChatTools.Definitions(["web_fetch", "web_search", "read_skill", "mcp_tools", "mcp_call"]);
        check(definitions.GetArrayLength() == 5 && definitions[0].GetProperty("function").GetProperty("parameters").GetProperty("required").EnumerateArray().Single().GetString() == "url",
            "Chat tool definitions are native function definitions");
        check(!ChatTools.Supported(new ChatServerInfo(false, true, 8192, null, false, "")) && ChatTools.Supported(new ChatServerInfo(true, true, 8192, null, false, "")) &&
              !ChatTools.Supported(new ChatServerInfo(true, true, 8192, null, false, "", RestrictedTextAdapter: true)), "Chat offers tools only to a verified llama.cpp connection");

        // ── Bundle folders ────────────────────────────────────────────────────────────────────────────
        var folder = Directory.CreateDirectory(Path.Combine(area, "bundle-src")).FullName;
        Write(Path.Combine(folder, "bundle.json"), "{\"name\":\"Writing\",\"version\":\"1.2\",\"description\":\"House style\"}");
        Write(Path.Combine(folder, "skills", "style", "SKILL.md"), "---\ndescription: Apply the house style\n---\nShort sentences.\n");
        Write(Path.Combine(folder, "docs", "guide.md"), "# Guide\nUse plain words.\n");
        Write(Path.Combine(folder, "mcp.json"), "[{\"name\":\"notes\",\"endpoint\":\"http://127.0.0.1:9000/mcp\",\"tools\":[\"search\"]}]");
        var said = CodeBundles.Command("install " + folder, profile);
        check(said.StartsWith("Installed writing 1.2: 2 files of skills and docs.") && said.Contains("They are not in use: /bundle servers writing shows them."), "/bundle install FOLDER installs skills and docs and keeps its servers out of use");
        check(CodeSkills.List(profile, project).Any(s => s.Name == "writing/style" && s.Description == "Apply the house style") && !CodeMcpRegistry.Active(profile).Any(s => s.Name.Contains("notes")),
            "An installed bundle's skills are available and its servers are not");
        check(CodeBundles.Command("servers writing add", profile).StartsWith("Added, switched off: notes") && CodeMcpRegistry.ReadAll(profile).Single(s => s.Name == "notes").Enabled == false, "/bundle servers NAME add adds them switched off");
        check(CodeBundles.Command("remove writing", profile) == "Removed writing." && !CodeSkills.List(profile, project).Any(s => s.Name.StartsWith("writing/")), "/bundle remove NAME removes it");
    }
}
