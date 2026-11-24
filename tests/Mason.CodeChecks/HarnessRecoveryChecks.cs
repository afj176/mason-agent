using System.Text.Json;
using Mason;
using Mason.Code;

static class HarnessRecoveryChecks
{
    static CodeAction Tool(string name, object arguments) => new("Fixture action.", name, JsonSerializer.SerializeToElement(arguments));
    static CodeAction Done() => new("Finished.", null, default);

    public static async Task Run(string root, Action<bool, string> check)
    {
        var area = Path.Combine(root, $"harness-recovery-{Guid.NewGuid():N}");
        var project = Directory.CreateDirectory(Path.Combine(area, "food-bear")).FullName;
        using var store = new CodeStore(Path.Combine(area, "state"));
        var session = store.New(project);
        session.Permission = CodePermission.Auto;
        var recovery = new Actions(
            messages =>
            {
                check(messages[0].Content.Contains("Workspace root '.' is an existing folder") && !messages[0].Content.Contains(project),
                    "empty workspace is grounded as an existing relative root");
                return Tool("list_files", new { path = ".}}", limit = 100 });
            },
            messages =>
            {
                CheckRootReceipt(messages, ".}}", check);
                return Tool("list_files", new { path = "food-bear}}", limit = 100 });
            },
            messages =>
            {
                CheckRootReceipt(messages, "food-bear}}", check);
                return Tool("create_file", new { path = "index.html", content = "<!doctype html><h1>Bear Food</h1>" });
            },
            messages =>
            {
                check(messages[0].Content.Contains("\"index.html\""), "the system inventory includes the file just written");
                return Done();
            });
        await new CodeEngine(store, recovery, (_, _) => throw new Exception("Unexpected approval"), () => { }).Run(session, "Create a small bear food store in HTML.", default);
        check(session.State == "Completed" && File.Exists(Path.Combine(project, "index.html")) && session.Detail.EndsWith("1 file changed"),
            "Build recovers the malformed root call and writes the requested file in the selected folder");

        var outside = Path.Combine(area, "other-project");
        var invalid = new Actions(
            _ => Tool("create_directory", new { path = outside }),
            messages =>
            {
                check(messages[^1].Content.Contains("Use a relative workspace path") && !messages[^1].Content.Contains("already declined"),
                    "absolute path denial supplies recovery guidance instead of inventing a user decline");
                return Tool("create_directory", new { path = project });
            },
            _ => throw new Exception("Invalid paths should stop after the correction attempt."));
        await new CodeEngine(store, invalid, (_, _) => throw new Exception("Invalid path reached review"), () => { }).Run(session, "Continue the site.", default);
        check(session.State == "Blocked" && invalid.Calls == 2 && !Directory.Exists(outside),
            "two invalid path attempts stop with a truthful blocker and create no alternate workspace");

        var reviewed = store.New(project);
        reviewed.Permission = CodePermission.ReviewEdits;
        int reviews = 0;
        var first = Tool("create_file", new { path = "declined.html", content = "example" });
        var reordered = Tool("create_file", new { content = "example", path = "declined.html" });
        var next = Tool("create_file", new { path = "style.css", content = "body { color: green; }" });
        await new CodeEngine(store, new Actions(_ => first, _ => reordered, _ => next, _ => Done()),
            (_, _) => Task.FromResult(++reviews == 2), () => { }).Run(reviewed, "Make the site green.", default);
        check(reviews == 2 && reviewed.Activities[1].Authorization == "Previously declined" && !File.Exists(Path.Combine(project, "declined.html")),
            "reordered arguments cannot reopen an actual decline and a distinct action can still be reviewed");

        await new CodeEngine(store, new Actions(_ => Tool("create_directory", new { path = "." }), _ => Done(), _ => Done(), _ => Done()),
            (_, _) => throw new Exception("Unexpected approval"), () => { }).Run(session, "Add an about page.", default);
        check(session.State == "Failed" && session.Activities.Any(a => a.Result.Contains("Directory already exists")) &&
            !File.Exists(Path.Combine(project, "about.html")), "an already-existing directory and an earlier run's files cannot satisfy Build completion");
        check(session.Turns.Last().Text.Contains("completion was rejected") && session.Messages.Any(m => m.Role == "assistant" && m.Content.Contains("Finished.")),
            "a rejected completion is labeled in the visible transcript and its raw response retained in the journal");

        await new CodeEngine(store, new Actions(_ => Tool("create_file", new { path = "about.html", content = "<h1>About</h1>" }), _ => Done()),
            (_, _) => throw new Exception("Unexpected approval"), () => { }).Run(session, "/new Create about.html.", default);
        check(session.State == "Completed" && session.Changes.Count == 2 && session.Detail.EndsWith("1 file changed"),
            "completion counts unique files changed by the current request");

        await new CodeEngine(store, new Actions(
            _ => Tool("create_file", new { path = "about.html", content = "replacement" }),
            messages =>
            {
                check(messages[^1].Content.Contains("already exists") && messages[^1].Content.Contains("read_file"),
                    "duplicate create receipts explain how to continue without overwriting");
                return Tool("create_file", new { path = "contact.html", content = "<h1>Contact</h1>" });
            },
            _ => Done()), (_, _) => throw new Exception("Unexpected approval"), () => { }).Run(session, "Keep about.html and add a contact page.", default);
        check(session.State == "Completed" && File.ReadAllText(Path.Combine(project, "about.html")) == "<h1>About</h1>",
            "a duplicate create preserves existing bytes and leaves room for the requested change");

        var ask = store.New(project);
        ask.Mode = "ask";
        ask.Permission = CodePermission.WorkspaceEdits;
        var askActions = new Actions(
            _ => Tool("open_preview", new { url = "http://127.0.0.1:5173/" }),
            _ => Tool("ask_user", new { question = " " }),
            _ => Tool("ask_user", new { question = "Which page should I inspect?" }));
        await new CodeEngine(store, askActions, (_, _) => throw new Exception("Unexpected approval"), () => { }).Run(ask, "Inspect the site.", default);
        check(ask.Activities[0].State == "Denied" && ask.Tabs.Count == 0, "Ask cannot open a preview through an unadvertised tool call");
        check(ask.State == "Waiting for input" && ask.Activities.Any(a => a.Tool == "model_response"),
            "a malformed clarification is repaired before a valid question pauses the request");

        var truncated = store.New(project);
        truncated.Permission = CodePermission.Auto;
        string fragment = string.Concat(Enumerable.Repeat("discarded-fragment ", 200));
        var repair = new Actions(
            _ => throw new CodeResponseException("Fixture reached its generation limit.", fragment),
            messages =>
            {
                check(messages[^1].Content.StartsWith(CodeProtocol.ResponseRepairPrefix, StringComparison.Ordinal) &&
                    !messages[^1].Content.Contains("discarded-fragment") && messages[^1].Content.Contains("smaller complete file"),
                    "response repair retains rejected bytes for inspection and sends concise correction guidance to the model");
                return Tool("create_file", new { path = "recovered.html", content = "<h1>Recovered</h1>" });
            },
            _ => Done());
        await new CodeEngine(store, repair, (_, _) => throw new Exception("Unexpected approval"), () => { }).Run(truncated, "Create a short page.", default);
        check(truncated.State == "Completed" && truncated.Activities[0].Result.Contains(fragment) &&
            File.Exists(Path.Combine(project, "recovered.html")), "truncated source is never written and a complete replacement can recover");
        Func<IReadOnlyList<CodeMessage>, CodeAction> clipped = _ => throw new CodeResponseException("Incomplete tool.", "partial");
        await new CodeEngine(store, new Actions(clipped, clipped, _ => Tool("list_files", new { path = ".", limit = 5 }), clipped, clipped, clipped),
            (_, _) => throw new Exception("Unexpected approval"), () => { }).Run(truncated, "Continue.", default);
        check(truncated.Activities.TakeLast(3).All(a => a.Tool == "model_response" && a.Result.Contains("partial")) &&
            truncated.Detail.Contains("Three consecutive") && truncated.Activities.Count(a => a.Tool == "model_response") == 6,
            "repair budget resets after a valid action and retains the final rejected response");
        var repairSchema = CodeSchema.Action(true, available: new HashSet<string> { "create_file" }, rootIsOnlyDirectory: true, compactNewFile: true);
        var fields = repairSchema.GetProperty("properties").GetProperty("tool").GetProperty("anyOf")[0].GetProperty("properties")
            .GetProperty("arguments").GetProperty("properties");
        var pattern = fields.GetProperty("path").GetProperty("pattern").GetString()!;
        check(fields.GetProperty("content").GetProperty("maxLength").GetInt32() == 2400 &&
            System.Text.RegularExpressions.Regex.IsMatch("./store.html", pattern) && !System.Text.RegularExpressions.Regex.IsMatch("food-bear/index.html", pattern),
            "response repair constrains new-file size and only offers reachable parents");

        var narrow = store.New(project);
        narrow.Permission = CodePermission.WorkspaceEdits;
        var model = new NarrowContext();
        await new CodeEngine(store, model, (_, _) => throw new Exception("Unexpected approval"), () => { }).Run(narrow, "Create a short page.", default);
        check(model.Measures == 2 && !model.Generated && narrow.Activities.Single().Tool == "context_preflight" &&
            narrow.Detail.Contains("2,048") && narrow.Detail.Contains("512"), "tight context records the loaded window and rejects a stranded Build before inference");
        await CheckInventoryAndFailures(area, store, check);
        await CheckAcceptance(area, store, check);
    }

    static async Task CheckAcceptance(string area, CodeStore store, Action<bool,string> check)
    {
        var root = Directory.CreateDirectory(Path.Combine(area,"acceptance")).FullName;
        var scope = new CodeWorkspace(root);
        var task = store.New(root); task.Permission = CodePermission.Auto;
        const string page = "<!doctype html><meta name='viewport' content='width=device-width,initial-scale=1'><h1>Honey Shop</h1><button onclick=\"document.querySelector('output').textContent='Cart updated'\">Add honey</button><output></output>";
        var specs = new AcceptanceCheck[] { new(1,"Store heading","browser_heading","./index.html","","Honey Shop"), new(2,"Add honey to cart","browser_click","index.html","Add honey","Cart updated") };
        var model = new AcceptanceFixture(specs,Tool("create_file",new { path="index.html",content=page }),Done());
        await new CodeEngine(store,model,(_,_)=>throw new Exception("Unexpected review"),()=>{}).Run(task,"Show the store heading and add honey to the cart.",default);
        var plan = task.Turns[0].Acceptance!;
        check(task.State=="Completed" && plan.Results.Count==2 && plan.Results.All(r=>r.Passed && r.Detail.Contains("390px and 1280px")),"Auto observes the heading and a real click on two viewport sizes before completing");
        var broken = page.Replace("document.querySelector('output').textContent='Cart updated'","void 0");
        File.WriteAllText(Path.Combine(root,"index.html"),broken);
        await CodeAcceptance.Verify(plan,scope,CodePermission.Auto,default);
        check(plan.Results[0].Passed && !plan.Results[1].Passed,"a broken handler fails its feature independently on fresh page bytes");
        File.WriteAllText(Path.Combine(root,"index.html"),page.Replace("document.querySelector('output').textContent='Cart updated'","alert('Cart updated')"));
        await CodeAcceptance.Verify(plan,scope,CodePermission.Auto,default);
        check(!plan.Results[1].Passed && plan.Results[1].Detail.Contains("JavaScript dialog"),"a modal dialog cannot certify an in-page update and its browser diagnostic is returned for repair: "+plan.Results[1].Detail);
        File.WriteAllText(Path.Combine(root,"index.html"),broken.Replace("<output></output>","<output>Cart updated</output>"));
        var resume = new AcceptanceFixture([],Tool("create_file",new { path="notes.txt",content="delivery" }),Done(),Done(),Done());
        await new CodeEngine(store,resume,(_,_)=>throw new Exception("Unexpected review"),()=>{}).Run(task,"continue",default);
        check(task.State=="Needs verification" && resume.Plans==0 && task.Activities.Last().Result.Contains("already visible") && !task.Turns.Last().Text.Contains("Finished."),"resume rechecks the pinned interaction and rejects a pre-rendered result despite an unrelated file write");
        var missing = await CodeAcceptance.Prepare("Show the store heading and calculate sales tax.",new AcceptanceFixture([specs[0]]),scope,scope.Files(),default);
        check(missing.Checks.Count==2 && missing.Checks[1].Kind=="unverified","an omitted request clause fails closed");
        File.WriteAllText(Path.Combine(root,"index.html"),page.Replace("</button>", "</button><script src='broken.js'></script>"));
        await CodeAcceptance.Verify(plan,scope,CodePermission.Auto,default);
        check(plan.Results.All(r=>!r.Passed),"a missing script prevents a passing page check");
    }
    sealed class AcceptanceFixture(AcceptanceCheck[] specs, params CodeAction[] steps) : ICodeModel, ICodeAcceptancePlanner
    {
        int index;
        public int Plans { get; private set; }
        public Task<List<AcceptanceCheck>> PlanAcceptance(string request,string[] units,CodeFiles inventory,CancellationToken ct)
        { Plans++; return Task.FromResult(specs.ToList()); }
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> preview,CancellationToken ct) => Task.FromResult(steps[index++]);
    }
    public static async Task CheckNamedTests(string area, Action<bool,string> check)
    {
        var root = Directory.CreateDirectory(Path.Combine(area,"python-acceptance-"+Guid.NewGuid().ToString("N"))).FullName;
        var scope = new CodeWorkspace(root);
        File.WriteAllText(Path.Combine(root,"status.txt"),"before test");
        var suite = Path.Combine(root,"test_report.py");
        File.WriteAllText(suite,"import unittest\nfrom pathlib import Path\nclass Report(unittest.TestCase):\n    def test_report(self):\n        Path('status.txt').write_text('after test')\n        self.assertEqual(7, 3+4)\n");
        AcceptanceCheck doc = new(1,"Original report","document_text","status.txt","","before test");
        AcceptanceCheck test = new(2,"Report calculation","python_test","test_report.py","Report","test_report");
        var plan = await CodeAcceptance.Prepare("Read status.txt and check the calculation.",new AcceptanceFixture([doc,test]),scope,scope.Files(),default);
        await CodeAcceptance.Verify(plan,scope,CodePermission.Auto,default);
        check(plan.Results.Any(r=>r.Check==test && r.Passed) && plan.Results.Any(r=>r.Check==doc && !r.Passed),"an actual named Python test passes and a document changed by that test is verified against final bytes: "+CodeAcceptance.Receipt(plan));
        File.AppendAllText(suite,"\n# revision\n");
        await CodeAcceptance.Verify(plan,scope,CodePermission.Auto,default);
        check(plan.Results.Any(r=>r.Check==test && !r.Passed && r.Detail.Contains("changed after planning")),"editing a pinned acceptance test invalidates its result");
    }

    static async Task CheckInventoryAndFailures(string area, CodeStore store, Action<bool, string> check)
    {
        var project = Directory.CreateDirectory(Path.Combine(area, "inventory")).FullName;
        var scope = new CodeWorkspace(project);
        File.WriteAllText(Path.Combine(project, "first.txt"), "one");
        var listing = scope.Files(".", 1);
        check(!listing.Truncated && listing.RootIsOnlyDirectory, "an exact-limit flat inventory is complete");
        var session = store.New(project);
        session.Permission = CodePermission.WorkspaceEdits;
        var steps = new Actions(
            messages =>
            {
                var nativeListing = CodeSchema.NativeTools(rootIsOnlyDirectory: messages[0].RootIsOnlyDirectory)[0];
                check(nativeListing.GetProperty("function").GetProperty("parameters").GetProperty("properties").GetProperty("path")
                    .GetProperty("const").GetString() == ".", "a flat workspace exposes only the actual root for directory listings");
                return Tool("create_directory", new { path = "inventory}}," });
            },
            _ => Tool("create_file", new { path = "inventory/page.html", content = "nested accidentally" }),
            _ => Done());
        await new CodeEngine(store, steps, (_, _) => throw new Exception("Unexpected review"), () => { }).Run(session, "Create a page here.", default);
        check(session.State == "Blocked" && !Directory.EnumerateDirectories(project).Any(), "ambiguous root writes leave no accidental directory or file");
        await new CodeEngine(store, new Actions(
            _ => Tool("create_directory", new { path = "./inventory" }),
            messages =>
            {
                check(!messages[0].RootIsOnlyDirectory, "empty subdirectories are available immediately after creation");
                return Tool("create_file", new { path = "inventory/page.html", content = "intentional" });
            }, _ => Done()), (_, _) => throw new Exception("Unexpected review"), () => { }).Run(session, "Create a nested inventory directory.", default);
        check(File.ReadAllText(Path.Combine(project, "inventory", "page.html")) == "intentional", "explicit same-name child directories work normally");
        Directory.CreateDirectory(Path.Combine(project, "inventory}}"));
        File.WriteAllText(Path.Combine(project, "inventory}}", "literal"), "two");
        session.Mode = "ask";
        var literal = Tool("list_files", new { path = "inventory}}", limit = 10 });
        await new CodeEngine(store, new Actions(_ => literal, _ => Done()), (_, _) => throw new Exception("Unexpected review"), () => { }).Run(session, "List the literal directory.", default);
        var receipt = session.Activities.Last().Result;
        check(receipt.Contains("inventory}}/literal") && !receipt.Contains("requested_path"), "an actual directory ending in braces takes precedence over recovery");
        foreach (var p in new[] { "unrelated}}", ".}}" })
        {
            if (p == ".}}") File.WriteAllText(Path.Combine(project, p), "a file, not a directory");
            await new CodeEngine(store, new Actions(_ => Tool("list_files", new { path = p, limit = 5 }), _ => Done()), (_, _) => throw new Exception("Unexpected review"), () => { }).Run(session, "List the named directory.", default);
            check(session.Activities.Last().State == "Failed", $"listing {p} does not substitute an unrelated inventory");
        }
        var subtree = Directory.CreateDirectory(Path.Combine(project, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("sub", 14)))).FullName;
        File.WriteAllText(Path.Combine(subtree, "deep.txt"), "deep");
        check(scope.Files().Truncated && scope.Files(".", 1).Truncated, "both the recursion limit and omitted files mark the listing incomplete");
        var readA = Tool("read_file", new { path = "missing-a" });
        var readB = Tool("read_file", new { path = "missing-b" });
        await new CodeEngine(store, new Actions(_ => readA, _ => readB, _ => readA, _ => readB, _ => readA), (_, _) => throw new Exception("Unexpected review"), () => { }).Run(session, "Inspect the missing files.", default);
        check(session.State == "Blocked" && session.Detail.Contains("three times") && !session.Activities.Last().Result.Contains(project), "alternating failed reads stop early and report relative paths");
        var readExisting = Tool("read_file", new { path = "first.txt" });
        var finish = CodeProtocol.Parse("""{"version":1,"message":"","tool":{"name":"finish","arguments":{"summary":"The file contains one."}}}""");
        await new CodeEngine(store, new Actions(_ => readExisting, _ => Tool("open_file", new { path = "./first.txt" }), messages =>
        {
            check(messages[0].AvailableTools is { } tools && !tools.Contains("read_file") && !tools.Contains("open_file") && messages[^1].Content.Contains("unchanged_since_activity"),
                "equivalent read and open calls on the same bytes share a recovery path and return a concise receipt");
            return finish;
        }), (_, _) => throw new Exception("Unexpected approval"), () => { }).Run(session, "Inspect first.txt.", default);
        check(session.State == "Completed" && session.Turns.Last().Text == "The file contains one.", "explicit finish returns a normal final answer");
        session.Mode = "build";
        await new CodeEngine(store, new Actions(_ => finish, _ => finish, _ => finish), (_, _) => throw new Exception("Unexpected approval"), () => { }).Run(session, "Implement something new.", default);
        check(session.State == "Failed" && session.Detail.Contains("No work is claimed complete"), "explicit finish cannot bypass Build delivery evidence");
        if (OperatingSystem.IsWindows())
        {
            session.Mode = "build";
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            CodeAction Run(string script) => Tool("run_command", new { executable = cmd, arguments = new[] { "/d", "/c", script }, directory = "." });
            var verify = Run("if not exist verified.txt exit /b 7");
            await new CodeEngine(store, new Actions(_ => verify, _ => Run("exit /b 0"), _ => Done()), (_, _) => Task.FromResult(true), () => { }).Run(session, "/new Verify the verified.txt file.", default);
            check(session.State == "Needs verification" && session.Turns.Last().Text.Contains("failed"), "an unrelated successful command cannot clear a failed check");
            await new CodeEngine(store, new Actions(_ => verify, _ => Tool("create_file", new { path = "verified.txt", content = "ready" }), _ => verify, _ => Done()), (_, _) => Task.FromResult(true), () => { }).Run(session, "Correct the file and rerun its check.", default);
            check(session.State == "Completed", "a successful rerun of the failed command clears the outstanding check");
        }
    }

    static void CheckRootReceipt(IReadOnlyList<CodeMessage> messages, string requested, Action<bool, string> check)
    {
        using var envelope = JsonDocument.Parse(messages[^1].Content);
        var receipt = envelope.RootElement;
        check(receipt.GetProperty("state").GetString() == "Completed", $"list_files {requested} completed in the selected workspace");
        using var result = JsonDocument.Parse(receipt.GetProperty("result").GetString()!);
        var listing = result.RootElement;
        check(listing.GetProperty("requested_path").GetString() == requested && listing.GetProperty("path").GetString() == "." &&
            listing.GetProperty("Files").GetArrayLength() == 0 && !listing.GetProperty("Truncated").GetBoolean(),
            $"the receipt preserves {requested} and records the actual empty root listing");
    }

    sealed class Actions(params Func<IReadOnlyList<CodeMessage>, CodeAction>[] steps) : ICodeModel, ICodeAcceptancePlanner
    {
        public Task<List<AcceptanceCheck>> PlanAcceptance(string request, string[] units, CodeFiles files, CancellationToken ct) =>
            Task.FromResult<List<AcceptanceCheck>>(request switch
            {
                "Create a small bear food store in HTML." => [new(1,"Bear store heading","browser_heading","index.html","","Bear Food")],
                "Create about.html." => [new(1,"About page","browser_heading","about.html","","About")],
                "Keep about.html and add a contact page." => [new(1,"About page","browser_heading","about.html","","About"),new(2,"Contact page","browser_heading","contact.html","","Contact")],
                "Create a short page." => [new(1,"Recovered page","browser_heading","recovered.html","","Recovered")],
                "Verify the verified.txt file." => [new(1,"Verified content","document_text","verified.txt","","ready")],
                "Correct the file and rerun its check." => [new(1,"Corrected file","document_text","verified.txt","","ready"),new(2,"Verified content","document_text","verified.txt","","ready")],
                _ => []
            });
        public int Calls { get; private set; }
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation) =>
            Task.FromResult(steps[Calls++](messages));
    }

    sealed class NarrowContext : ICodeContextModel
    {
        public int Measures { get; private set; }
        public bool Generated { get; private set; }
        public Task<CodeContextUsage> Measure(IReadOnlyList<CodeMessage> messages, bool digest, CancellationToken cancellation)
        {
            int prompt = ++Measures == 1 ? 1510 : 1570;
            return Task.FromResult(new CodeContextUsage(prompt, 2048, 2048 - prompt, "fixture"));
        }
        public Task<CodeDigest> Summarize(IReadOnlyList<CodeMessage> messages, CancellationToken cancellation) => throw new Exception("Unexpected compaction");
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation)
        {
            Generated = true;
            return Task.FromResult(Done());
        }
    }

    public static async Task EvaluateEmptyProject(string endpoint, string modelId, string root, string? conversation = null, bool acceptanceTrial = false, string? requestText=null)
    {
        if (Directory.Exists(root)) throw new IOException("Choose a fresh evaluation directory.");
        var project = Directory.CreateDirectory(Path.Combine(root, "food-bear")).FullName;
        using var store = new CodeStore(Path.Combine(root, "profile"));
        var session = store.New(project);
        session.Permission = CodePermission.Auto;
        session.Shell = CodeShellKind.CommandPrompt;
        session.ShellExecutable = CodeShells.Find(session.Shell);
        session.ActionLimit = requestText==null?24:64;
        string request = requestText??"Build an HTML website in this directory. I want the website to be a store for bears to go shopping, make it whimsical.";
        if (acceptanceTrial) request = "In index.html show a heading named \"Honey Market\". Clicking the button \"Add honey\" must show \"Added to cart\".";
        if (conversation != null)
        {
            // Import only the model-visible history. All tool execution and journaling use the fresh project above.
            using var saved = JsonDocument.Parse(File.ReadAllBytes(conversation));
            session.Messages = saved.RootElement.GetProperty("Messages").Deserialize<List<CodeMessage>>()!;
            session.State = saved.RootElement.GetProperty("State").GetString()!;
            request = "continue";
        }
        var profile = new AiEndpointProfile { Endpoint = endpoint, Model = modelId, ManagedCapability = AiCapability.Chat };
        var model = new CodeLocalModel(profile, "", "default", Console.WriteLine);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(requestText==null?3:15));
        await new CodeEngine(store, model, (_, _) => throw new Exception("Unexpected approval"), () => { }, Console.WriteLine)
            .Run(session, request, deadline.Token);
        var pages = Directory.EnumerateFiles(project)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".html" or ".htm")
            .Where(p => File.ReadAllText(p).Contains("<html", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).ToArray();
        var duplicateRoots = Directory.EnumerateDirectories(project).Select(Path.GetFileName)
            .Where(n => n == "food-bear" || n!.Contains("}}", StringComparison.Ordinal)).ToArray();
        bool created = pages.Length > 0 && duplicateRoots.Length == 0;
        var acceptance = session.Turns.First(t => t.Acceptance != null).Acceptance!;
        bool negative = false;
        if (acceptanceTrial && created && session.State=="Completed" && acceptance.Results.Any(r=>r.Check.Kind=="browser_click"))
        {
            var mutant = Directory.CreateDirectory(Path.Combine(root,"broken-copy")).FullName;
            File.WriteAllText(Path.Combine(mutant,"index.html"),"<!doctype html><h1>Honey Market</h1><button>Add honey</button>");
            var copy = new CodeAcceptancePlan { Request=acceptance.Request, Units=acceptance.Units, Checks=[..acceptance.Checks] };
            await CodeAcceptance.Verify(copy,new CodeWorkspace(mutant),CodePermission.Auto,deadline.Token);
            File.WriteAllText(Path.Combine(root,"negative.json"),JsonSerializer.Serialize(copy,new JsonSerializerOptions { WriteIndented=true }));
            negative = copy.Results.Any(r=>r.Passed && r.Check.Kind=="browser_heading") && copy.Results.Any(r=>!r.Passed && r.Check.Kind=="browser_click");
            var scope=new CodeWorkspace(mutant);
            var broader=await CodeAcceptance.Prepare("List three distinct products in index.html, calculate the discounted total after a quantity change, and complete a checkout that shows an order number.",model,scope,scope.Files(),deadline.Token);
            await CodeAcceptance.Verify(broader,scope,CodePermission.Auto,deadline.Token);
            File.WriteAllText(Path.Combine(root,"coverage.json"),JsonSerializer.Serialize(broader,new JsonSerializerOptions { WriteIndented=true }));
            negative &= broader.Units.Length>=3 && Enumerable.Range(1,broader.Units.Length).All(i=>broader.Results.Any(r=>r.Check.Unit==i && !r.Passed)) &&
                !broader.Checks.Any(c=>c.Expected.StartsWith("Acceptance planning failed:",StringComparison.Ordinal));
        }
        File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { session.State, session.Detail, created, pages, duplicateRoots, acceptance, negative, session.Activities },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{session.State}: {session.Detail} · HTML in selected project: {created}");
        if (session.State != "Completed" || !created || acceptanceTrial && !negative) Environment.ExitCode = 1;
    }
}
