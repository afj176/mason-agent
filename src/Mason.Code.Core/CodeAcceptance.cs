using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason.Code;

public sealed record AcceptanceCheck(int Unit, string Feature, string Kind, string Path, string Target, string Expected)
{
    public BrowserStep[]? Steps {get;init;}
    public string? Application {get;init;}
}
public sealed record AcceptanceResult(AcceptanceCheck Check, bool Passed, string Detail)
{
    public string? ScriptPath {get;init;}
}
public sealed record CodePageRepair(CodeFile File, string Request, string Evidence, string Instructions)
{
    public AcceptanceCheck[] FailedClicks {get;init;}=[];
    public string RelatedSource {get;init;}="";
}
public sealed class CodeAcceptancePlan
{
    public Guid TaskId { get; set; }
    public int? FirstActivity { get; set; }
    public string? VerifiedRevision { get; set; }
    public bool LiteralRequirements {get;set;}
    public string[] RequirementIds => Units.Select((u,i)=>CodeProtocol.Hash(System.Text.Encoding.UTF8.GetBytes($"{TaskId:N}|{i+1}|{u}"))[..16]).ToArray();
    public string Request { get; set; } = "";
    public string[] Units { get; set; } = [];
    public List<AcceptanceCheck> Checks { get; set; } = [];
    public Dictionary<string,string> TestHashes { get; set; } = [];
    public List<AcceptanceResult> Results { get; set; } = [];
    public string? ContractHash { get; set; }
    public CodeAcceptancePlan Continue() => new() { TaskId=TaskId,FirstActivity=FirstActivity,Request=Request,Units=[..Units],
        Checks=[..Checks],ContractHash=ContractHash,LiteralRequirements=LiteralRequirements,TestHashes=new(TestHashes) };

    public void Append(CodeAcceptancePlan next)
    {
        if(Request.Length+next.Request.Length>60000 || Units.Length+next.Units.Length>128) throw new InvalidOperationException("Task exceeds the retained requirement budget. Start a new task with /new or split the request.");
        if(ContractHash!=null && next.ContractHash!=null && ContractHash!=next.ContractHash) throw new InvalidOperationException("The pinned acceptance contract changed. Start a new task to adopt it.");
        foreach(var pair in next.TestHashes)
            if(TestHashes.TryGetValue(pair.Key,out var hash) && hash!=pair.Value) throw new InvalidOperationException("Pinned acceptance test changed: "+pair.Key);
        for(int i=0;i<next.Units.Length;i++)
        {
            var index=Array.IndexOf(Units,next.Units[i]);
            if(index<0) {index=Units.Length;Units=[..Units,next.Units[i]];}
            var fresh=next.Checks.Where(c=>c.Unit==i+1).Select(c=>c with {Unit=index+1}).ToArray();
            if((next.LiteralRequirements || next.ContractHash!=null) && fresh.Length>0 && fresh.All(c=>c.Kind!="unverified"))
                Checks.RemoveAll(c=>c.Unit==index+1 && c.Kind=="unverified");
            foreach(var c in fresh) if(!Checks.Any(p=>p.Unit==c.Unit && JsonSerializer.Serialize(p)==JsonSerializer.Serialize(c)))Checks.Add(c);
        }
        Request+="\n"+next.Request; ContractHash??=next.ContractHash;
        LiteralRequirements &= next.LiteralRequirements;
        foreach(var entry in next.TestHashes) TestHashes[entry.Key]=entry.Value;
    }
}
public interface ICodeAcceptancePlanner
{
    Task<List<AcceptanceCheck>> PlanAcceptance(string request, string[] units, CodeFiles inventory, CancellationToken ct);
}
public static class CodeAcceptance
{
    public static bool IsContinuation(string input) => Regex.IsMatch(input.Trim(), @"^(?:(?:please|yes)[ ,]+)?(?:continue|resume|retry|try again|keep going|go on)(?:[ .!]| please)*$", RegexOptions.IgnoreCase);

    public static string SubstantiveRequest(IEnumerable<CodeMessage> history)
    {
        foreach (var m in history.Reverse().Where(m => m.Role == "user" && m.Content.StartsWith("{\"request\":", StringComparison.Ordinal)))
        {
            using var doc = JsonDocument.Parse(m.Content);
            var request = doc.RootElement.GetProperty("request").GetString();
            if (!string.IsNullOrWhiteSpace(request) && !IsContinuation(request)) return request;
        }
        return "";
    }

    // Keep quoted UI literals intact, including punctuation and conjunctions inside the label.
    static string[] Units(string input)
    {
        var units = new List<string>();
        var delimiter = new Regex("""("(?:\\.|[^"\\])*"|‘[^’]*’|“[^”]*”|'[^'\r\n]+')|(?:[;\r\n]+|[.!?](?=\s|$)|,\s+|\s+(?:and(?: also| then)?|as well as|plus)\s+)""", RegexOptions.IgnoreCase);
        int start = 0;
        foreach (Match m in delimiter.Matches(input))
        {
            if (m.Groups[1].Success) continue;
            if (input[start..m.Index].Trim() is { Length: > 0 } unit) units.Add(unit);
            start = m.Index + m.Length;
        }
        if (input[start..].Trim() is { Length: > 0 } tail) units.Add(tail);
        return units.ToArray();
    }

    public static async Task<CodeAcceptancePlan> Prepare(string request, ICodeModel model, CodeWorkspace scope, CodeFiles files, CancellationToken ct)
    {
        var plan = new CodeAcceptancePlan { TaskId=Guid.NewGuid(), Request = request, Units = Units(request) };
        // An explicitly supplied machine acceptance contract belongs to this user turn. This also
        // permits a reproducible build from an empty project without letting a file edit change its oracle.
        var inline=Regex.Match(request,@"(?m)^```mason\.acceptance\.json[ \t]*\r?$");
        if(inline.Success)
        {
            var block=request[(inline.Index+inline.Length)..].Trim();
            if(!block.EndsWith("\n```",StringComparison.Ordinal))throw new InvalidDataException("Close the acceptance contract with a final triple-backtick line.");
            var contract=BrowserContract.Parse(scope,block[..^3].Trim());
            plan.Units=contract.Scenarios.Select(s=>s.Requirement).ToArray();
            plan.Checks=contract.Scenarios.Select((s,i)=>new AcceptanceCheck(i+1,s.Requirement,"browser_flow",s.Page,s.Id,"All scenario assertions pass"){Steps=s.Steps,Application=contract.Application}).ToList();
            return plan;
        }
        if (plan.Units.Length == 0) plan.Units = ["Specify the requested features."];
        List<AcceptanceCheck> proposed = [];
        var repositoryChecks = new List<AcceptanceCheck>();
        if (files.Files.Contains(BrowserContract.Filename) || File.Exists(scope.Resolve(BrowserContract.Filename)))
        {
            try
            {
                var pinned = scope.Read(BrowserContract.Filename);
                var contract=BrowserContract.Read(scope);
                if(scope.Read(BrowserContract.Filename).Sha256!=pinned.Sha256) throw new IOException("The acceptance contract changed during planning.");
                bool whole=Regex.IsMatch(request.Trim(),@"^(?:verify|run|implement|fix|check)\s+(?:(?:the|all|features|checks|scenarios|in|from|specified|by|using)\s+)*mason\.acceptance\.json[.!]?$",RegexOptions.IgnoreCase);
                if(whole) plan.Units=contract.Scenarios.Select(s=>s.Requirement).ToArray();
                for(int i=0;i<plan.Units.Length;i++)
                    foreach(var scenario in contract.Scenarios.Where(s=>s.Requirement.TrimEnd('.').Equals(plan.Units[i].TrimEnd('.'),StringComparison.OrdinalIgnoreCase)))
                        repositoryChecks.Add(new(i+1,scenario.Requirement,"browser_flow",scenario.Page,scenario.Id,"All scenario assertions pass"){Steps=scenario.Steps,Application=contract.Application});
                if(repositoryChecks.Count>0) plan.ContractHash=pinned.Sha256;
            }
            catch(Exception e) when (e is JsonException or InvalidDataException or ArgumentException or IOException)
            {
                if(request.Contains(BrowserContract.Filename,StringComparison.OrdinalIgnoreCase))
                {
                    plan.Checks.Add(new(1,request,"unverified","","","Cannot pin project acceptance contract: "+e.Message));
                    return plan;
                }
            }
        }
        var reason = "A measurable acceptance criterion is needed.";
        var pages = Regex.Matches(request, @"(?<![\w/\\:.-])(?:[\w-]+/)*[\w-]+\.html?\b", RegexOptions.IgnoreCase)
            .Select(m=>m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var exact = plan.Units.Select((s,i)=>ExplicitCheck(s,i+1,pages)).Where(c=>c!=null).Cast<AcceptanceCheck>().ToList();
        if (exact.Concat(repositoryChecks).Select(c=>c.Unit).Distinct().Count() != plan.Units.Length && plan.Units.Length <= 32 && model is ICodeAcceptancePlanner planner)
        {
            try { proposed = await planner.PlanAcceptance(request, plan.Units, files, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { reason = "Acceptance planning failed: " + e.Message; }
        }
        else if (plan.Units.Length > 32) reason = "The request exceeds the 32-clause acceptance planning limit.";
        for (int i = 0; i < plan.Units.Length; i++)
        {
            if(repositoryChecks.Any(c=>c.Unit==i+1)) { plan.Checks.AddRange(repositoryChecks.Where(c=>c.Unit==i+1)); continue; }
            var matches = exact.Where(c => c.Unit == i+1).ToList();
            if (matches.Count == 0) matches = proposed.Where(c => c.Unit == i+1).Distinct().ToList();
            if (matches.Count == 0 || proposed.Count > 64)
                plan.Checks.Add(new(i+1, plan.Units[i], "unverified", "", "", reason));
            else foreach (var c in matches)
            {
                bool valid = !string.IsNullOrWhiteSpace(c.Feature) && c.Feature.Length <= 400 && c.Target is { Length: <= 200 } &&
                    c.Expected is { Length: > 0 and <= 500 } && c.Path is { Length: > 0 and <= 260 };
                bool browser = c.Kind is "browser_heading" or "browser_text" or "browser_click" or "browser_layout" or "browser_flow";
                if(c.Kind=="browser_flow") { try {BrowserContract.Validate(c.Steps);} catch(InvalidDataException){valid=false;} }
                bool page = browser && Path.GetExtension(c.Path)?.ToLowerInvariant() is ".html" or ".htm";
                bool document = c.Kind == "document_text" && Path.GetExtension(c.Path)?.ToLowerInvariant() is ".txt" or ".md" or ".csv";
                bool test = false;
                if (valid && c.Kind == "python_test" && files.Files.Contains(c.Path) &&
                    Regex.IsMatch(c.Path, @"^(?:[A-Za-z_]\w*/)*test\w*\.py$") &&
                    Regex.IsMatch(c.Target ?? "", @"^[A-Za-z_]\w*$") && Regex.IsMatch(c.Expected ?? "", @"^test_\w+$"))
                {
                    try { plan.TestHashes[c.Path] = scope.Read(c.Path).Sha256; test = true; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
                }
                var unit = plan.Units[i];
                if (document && !Regex.IsMatch(request,@"\.(?:txt|md|csv)\b|\b(?:documentation|document|readme|notes|file|text)\b",RegexOptions.IgnoreCase))
                    valid = false;
                if (c.Kind=="browser_layout" && !Regex.IsMatch(unit,@"\b(?:responsive|layout|mobile|desktop|width|overflow|viewport|screen)\b",RegexOptions.IgnoreCase))
                    valid = false;
                if (valid)
                {
                    try { scope.Resolve(c.Path); }
                    catch (Exception e) when (e is UnauthorizedAccessException or IOException or ArgumentException) { valid = false; }
                }
                if (browser && c.Kind != "browser_layout" && Regex.IsMatch(c.Expected ?? "", @"^(?:visible|true|passed|successfully displayed)$",RegexOptions.IgnoreCase) &&
                    !unit.Contains("\""+c.Expected+"\"",StringComparison.OrdinalIgnoreCase)) valid = false;
                bool interaction = Regex.IsMatch(unit, @"\b(click(?:ing|ed)?|press(?:ing|ed)?|tapp?(?:ing|ed)?|toggl(?:e|ing))\b", RegexOptions.IgnoreCase);
                if(browser && !exact.Contains(c) && !BrowserContract.CoversBehavior(unit,c.Steps??[]))valid=false;
                if (!valid || c.Application!=null || !(page || document || test) || interaction && c.Kind is not ("browser_click" or "browser_flow") || c.Kind == "browser_click" && string.IsNullOrWhiteSpace(c.Target))
                    plan.Checks.Add(new(i+1, c.Feature ?? unit, "unverified", "", "", c.Kind == "unverified" && !string.IsNullOrWhiteSpace(c.Expected) ? c.Expected : reason));
                else plan.Checks.Add(c);
            }
            var mapped = plan.Checks.Where(c=>c.Unit==i+1).ToList();
            if (!mapped.Any(c=>c.Kind is "unverified" or "python_test"))
            {
                var literals = Regex.Matches(plan.Units[i],"""["“]([^"”]+)["”]""").Select(m=>m.Groups[1].Value);
                foreach(var literal in literals.Where(s=>!mapped.Any(c=>c.Expected.Contains(s,StringComparison.Ordinal) || c.Target==s || c.Path==s)))
                    plan.Checks.Add(new(i+1, plan.Units[i],"unverified","","",$"No acceptance assertion covers the requested literal '{literal}'."));
            }
        }
        plan.LiteralRequirements=exact.Count==plan.Units.Length && plan.Checks.Count==exact.Count && plan.Checks.All(exact.Contains);
        return plan;
    }

    // For fully specified observable requirements bind the user's literal directly. Anchor the
    // entire clause so an additional condition can never disappear into a loosely matched prefix.
    static AcceptanceCheck? ExplicitCheck(string clause, int unit, string[] pages)
    {
        var document=Regex.Match(clause,"""^(?:create|update|write)\s+([\w./-]+\.(?:txt|md|csv))\s+(?:containing|to contain|with the text)\s+"([^"]+)"$""",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        if(document.Success)return new(unit,clause,"document_text",document.Groups[1].Value,"",document.Groups[2].Value);
        if (pages.Length > 1) return null;
        var page = pages.SingleOrDefault() ?? "index.html";
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var heading = Regex.Match(clause, """^(?:in\s+[\w./-]+\.html?\s+)?(?:show|display|add|create)\s+(?:a|the|an)\s+(?:page\s+)?heading\s+(?:(?:named|reading|labeled|saying|with (?:the )?text)\s+)?"([^"]+)"$""",options);
        if (heading.Success) return new(unit,clause,"browser_heading",page,"",heading.Groups[1].Value);
        var click = Regex.Match(clause, """^(?:clicking|pressing|tapping)\s+(?:(?:the|a)\s+)?(?:(?:button|link)\s+)?"([^"]+)"\s+(?:must\s+|should\s+)?(?:show|display|reveal)s?\s+"([^"]+)"(?:\s+on (?:the )?page)?$""",options);
        return click.Success ? new(unit,clause,"browser_click",page,click.Groups[1].Value,click.Groups[2].Value) : null;
    }

    public static async Task Verify(CodeAcceptancePlan plan, CodeWorkspace scope, CodePermission permission, CancellationToken ct, bool browserApproved = false)
    {
        plan.Results.Clear();
        plan.VerifiedRevision=null;
        if(plan.ContractHash!=null)
        {
            try { if(scope.Read(BrowserContract.Filename).Sha256!=plan.ContractHash) throw new InvalidDataException("Pinned project acceptance contract changed."); }
            catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException) { plan.Results.AddRange(plan.Checks.Select(c=>new AcceptanceResult(c,false,e.Message))); return; }
        }
        string? Revision(bool pythonOnly=false)
        {
            try { return CodeRevision.Measure(scope,plan,ct,pythonOnly); }
            catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { plan.Results=plan.Checks.Select(c=>new AcceptanceResult(c,false,e.Message)).ToList();return null; }
        }
        bool hasTests=plan.Checks.Any(c=>c.Kind=="python_test");
        string? pythonRevision=hasTests?Revision(true):null;
        if(hasTests && pythonRevision==null)return;
        var browserChecks = plan.Checks.Where(c => c.Kind.StartsWith("browser_", StringComparison.Ordinal)).Distinct().ToArray();
        // Workspace tests can write files. Observe document bytes and browser behavior only after
        // every test process has exited, including a process that failed or rewrote an artifact.
        foreach (var check in plan.Checks.Where(c=>c.Kind=="python_test"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (permission != CodePermission.Auto) throw new InvalidOperationException("Use Auto to run this isolated Python acceptance check.");
                if (!plan.TestHashes.TryGetValue(check.Path, out var hash) || scope.Read(check.Path).Sha256 != hash)
                    throw new InvalidDataException("The acceptance test changed after planning.");
                string name = check.Path[..^3].Replace('/','.') + "." + check.Target + "." + check.Expected;
                var run = await CodeCommand.Run(CodeCommand.ResolveExecutable("python",scope.Root), ["-B","-m","unittest","-v",name], scope.Root,ct,scope.Root);
                bool ok = run.ExitCode == 0 && !run.Truncated && Regex.IsMatch(run.Output, @"(?m)^"+Regex.Escape(check.Expected)+@" \([^\r\n]*\) \.\.\. ok\s*$") &&
                    Regex.IsMatch(run.Output, @"(?m)^Ran 1 test in ") && Regex.IsMatch(run.Output, @"(?m)^OK\s*$") && scope.Read(check.Path).Sha256 == hash;
                plan.Results.Add(new(check,ok,$"{name} · exit {run.ExitCode} · {run.Output[..Math.Min(run.Output.Length,800)]}"));
            }
            catch (Exception e) when (e is not OperationCanceledException) { plan.Results.Add(new(check,false,e.Message)); }
        }
        if(hasTests && pythonRevision!=Revision(true))
        { plan.Results=plan.Checks.Select(c=>new AcceptanceResult(c,false,"Python sources changed during the test run. Rerun against the current revision.")).ToList();return; }
        var revision=Revision();
        if(revision==null)return;
        var runnable=browserChecks.Where(c=>c.Application==null || permission==CodePermission.Auto).ToArray();
        var browser = runnable.Length > 0 && (permission == CodePermission.Auto || browserApproved) ? await CodeBrowserVerification.Run(scope, runnable, ct) : [];
        foreach (var check in plan.Checks.Where(c=>c.Kind!="python_test"))
        {
            ct.ThrowIfCancellationRequested();
            if (browser.TryGetValue(check, out var observed)) { plan.Results.Add(observed); continue; }
            if (check.Kind.StartsWith("browser_", StringComparison.Ordinal)) { plan.Results.Add(new(check,false,"Browser verification requires Auto or approval to run the local page.")); continue; }
            if (check.Kind != "document_text") { plan.Results.Add(new(check, false, check.Expected)); continue; }
            try
            {
                var file = scope.Read(check.Path);
                bool found = file.Content.Contains(check.Expected, StringComparison.Ordinal);
                plan.Results.Add(new(check, found, $"{file.Path} · SHA-256 {file.Sha256} · text {(found ? "found" : "missing")}: {check.Expected}"));
            }
            catch (Exception e) when (e is not OperationCanceledException) { plan.Results.Add(new(check, false, e.Message)); }
        }
        if(revision!=Revision())
        {
            plan.Results=plan.Checks.Select(c=>new AcceptanceResult(c,false,"Workspace inputs changed during verification. Rerun against the current revision.")).ToList();
            return;
        }
        plan.VerifiedRevision=revision;
    }
    public static string Receipt(CodeAcceptancePlan plan) => string.Join("\n", plan.Results.Select(r =>
        $"{(r.Passed ? "PASS" : "UNVERIFIED")} · {r.Check.Feature} [{r.Check.Kind}] · {r.Detail}"));
}

