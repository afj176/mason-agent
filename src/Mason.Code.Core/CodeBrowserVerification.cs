using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Mason.Code;

internal static class CodeBrowserVerification
{
    sealed class ScriptFailure(string message, string? path,int? line,string error) : Exception(message)
    {
        public string? Path {get;}=path;
        public int? Line {get;}=line;
        public string Error {get;}=error;
    }
    const string Origin = "https://workspace.mason.invalid";
    const string Policy = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; worker-src 'none'; sandbox allow-scripts allow-same-origin";
    static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"]="text/html", [".htm"]="text/html", [".js"]="text/javascript", [".mjs"]="text/javascript",
        [".css"]="text/css", [".png"]="image/png", [".jpg"]="image/jpeg", [".jpeg"]="image/jpeg",
        [".webp"]="image/webp", [".gif"]="image/gif", [".svg"]="image/svg+xml", [".ico"]="image/x-icon",
        [".woff"]="font/woff", [".woff2"]="font/woff2"
    };

    public static async Task<Dictionary<AcceptanceCheck, AcceptanceResult>> Run(CodeWorkspace scope, AcceptanceCheck[] checks, CancellationToken ct)
    {
        var results = new Dictionary<AcceptanceCheck, AcceptanceResult>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(checks.Any(c=>c.Application!=null)?TimeSpan.FromMinutes(6):TimeSpan.FromMinutes(2));
        try
        {
            var edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
                .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe")).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Microsoft Edge is required for browser acceptance checks.");
            using var driver = await Playwright.CreateAsync();
            using var stop = deadline.Token.Register(driver.Dispose);
            deadline.Token.ThrowIfCancellationRequested();
            await using var browser = await driver.Chromium.LaunchAsync(new() { ExecutablePath = edge, Headless = true, ChromiumSandbox = true, Timeout = 15000,
                Args = ["--disable-extensions", "--disable-background-networking", "--disable-sync", "--disable-quic", "--disable-features=ExternalProtocolDialogShowAlwaysOpenCheckbox",
                    "--force-webrtc-ip-handling-policy=disable_non_proxied_udp", "--proxy-server=http://discard.invalid:9", "--proxy-bypass-list=<-loopback>", "--host-resolver-rules=MAP * ~NOTFOUND"] });
            foreach (var check in checks)
            {
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var relative = scope.Relative(scope.Resolve(check.Path));
                    var url = Origin + "/" + string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
                    string observed="";
                    foreach (var width in new[] { 390, 1280 })
                        observed=await Observe(browser, scope, check, url, width,deadline.Token);
                    results[check] = new(check, true, $"{check.Path} · Edge {browser.Version} · 390px and 1280px · {observed}");
                }
                catch (Exception e) when (!deadline.IsCancellationRequested) { results[check] = Failed(check, e); }
            }
        }
        catch (Exception e)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var c in checks) results.TryAdd(c, Failed(c, deadline.IsCancellationRequested ? new TimeoutException("Browser verification exceeded its time budget.") : e));
        }
        return results;
    }
    static AcceptanceResult Failed(AcceptanceCheck c, Exception e)
    {
        var reason = e.Message.Split('\n')[0].Trim();
        var assertion = c.Kind == "browser_click" ? $"click '{c.Target}' and observe new visible text '{c.Expected}'" : c.Expected;
        var script=e as ScriptFailure;
        return new(c,false,$"{c.Path} · {assertion} · {reason[..Math.Min(350,reason.Length)]}") {ScriptPath=script?.Path,ScriptLine=script?.Line,ScriptError=script?.Error};
    }

    static async Task<string> Observe(IBrowser browser, CodeWorkspace scope, AcceptanceCheck check, string url, int width,CancellationToken ct)
    {
        await using var app=check.Application==null?null:await CodeVerificationApp.Start(scope,check.Application,ct);
        await using var context = await browser.NewContextAsync(new() { Offline = true, ServiceWorkers = ServiceWorkerPolicy.Block,
            AcceptDownloads = false, ViewportSize = new() { Width = width, Height = 850 } });
        var errors = new ConcurrentQueue<string>();
        var staticFiles = new ConcurrentDictionary<string,string>(StringComparer.Ordinal);
        ScriptFailure? scriptFailure = null;
        void Record(string error) { if (errors.IsEmpty) errors.Enqueue(error); }
        long served = 0;
        int requests = 0;
        await context.RouteWebSocketAsync("**/*", socket => socket.CloseAsync());
        await context.RouteAsync("**/*", async route =>
        {
            try
            {
                var request = route.Request;
                var uri = new Uri(request.Url);
                if (Interlocked.Increment(ref requests) > 150 || uri.GetLeftPart(UriPartial.Authority) != Origin ||
                    request.Frame.ParentFrame != null || app==null && (request.Method!="GET" || check.Kind!="browser_flow" && request.IsNavigationRequest && request.Url != url))
                    throw new InvalidDataException("Page requested a resource outside the isolated verification page.");
                if(app!=null)
                {
                    var response=await app.Request(request,uri,ct);
                    if(Interlocked.Add(ref served,response.Body.Length)>16*1024*1024) throw new InvalidDataException("App exceeded response budget.");
                    response.Headers["Content-Security-Policy"]=Policy.Replace("connect-src 'none'","connect-src 'self'").Replace("form-action 'none'","form-action 'self'").Replace("sandbox allow-scripts","sandbox allow-forms allow-scripts");
                    response.Headers["X-Content-Type-Options"]="nosniff";
                    await route.FulfillAsync(new(){Status=response.Status,BodyBytes=response.Body,Headers=response.Headers});
                    return;
                }
                var path = scope.Resolve(Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
                if (!Types.TryGetValue(Path.GetExtension(path), out var type)) throw new InvalidDataException("Unsupported browser asset type.");
                using var input = File.OpenRead(path);
                if (input.Length > 4*1024*1024 || Interlocked.Add(ref served, input.Length) > 16*1024*1024)
                    throw new InvalidDataException("Page exceeded the verification asset budget.");
                var bytes = new byte[checked((int)input.Length)];
                await input.ReadExactlyAsync(bytes);
                staticFiles[uri.GetLeftPart(UriPartial.Path)]=scope.Relative(path);
                await route.FulfillAsync(new() { Status = 200, BodyBytes = bytes, ContentType = type,
                    Headers = new Dictionary<string,string> { ["Content-Security-Policy"] = Policy, ["X-Content-Type-Options"]="nosniff", ["Referrer-Policy"]="no-referrer" } });
            }
            catch (Exception e)
            {
                Record(e.Message);
                try { await route.AbortAsync(); } catch (PlaywrightException) { }
            }
        });
        var page = await context.NewPageAsync();
        string? dialogMessage=null;
        string? suppressedDialog=null;
        page.Console += (_,entry) => {
            if(entry.Text.Contains("allow-modals",StringComparison.OrdinalIgnoreCase))
                suppressedDialog=entry.Text[..Math.Min(entry.Text.Length,250)];
        };
        page.Dialog += async (_,dialog) => { dialogMessage=dialog.Message[..Math.Min(160,dialog.Message.Length)]; try{await dialog.DismissAsync();}catch(PlaywrightException){} };
        // Edge can omit the stack for a parse error. The runtime event still carries the
        // resource URL and zero-based line/column separately from the exception stack.
        var runtime=await context.NewCDPSessionAsync(page);
        runtime.Event("Runtime.exceptionThrown").OnEvent+=(_,data)=>{
            if(data is not {} json || !json.TryGetProperty("exceptionDetails",out var details))return;
            var source=details;
            if(details.TryGetProperty("stackTrace",out var stack) && stack.TryGetProperty("callFrames",out var frames) && frames.GetArrayLength()>0)
                source=frames[0];
            string? path=null;
            if(source.TryGetProperty("url",out var name) && Uri.TryCreate(name.GetString(),UriKind.Absolute,out var uri))
                staticFiles.TryGetValue(uri.GetLeftPart(UriPartial.Path),out path);
            var where=path??check.Path;
            int? sourceLine=null;
            if(source.TryGetProperty("lineNumber",out var line) && source.TryGetProperty("columnNumber",out var column))
            {
                sourceLine=line.GetInt32()+1;
                where+=$":{sourceLine}:{column.GetInt32()+1}";
            }
            var message=details.TryGetProperty("exception",out var ex) && ex.TryGetProperty("description",out var description)
                ? description.GetString() : details.GetProperty("text").GetString();
            var reason=(message??"Uncaught error").Split('\n')[0].Trim();
            Interlocked.CompareExchange(ref scriptFailure,new ScriptFailure($"JavaScript error at {where}: {reason}",path??(app==null?check.Path:null),sourceLine,reason[..Math.Min(240,reason.Length)]),null);
        };
        await runtime.SendAsync("Runtime.enable");
        page.PageError+=(_,message)=>Record("JavaScript error: "+message);
        page.Popup += (_, _) => Record("Unexpected popup during browser verification.");
        page.SetDefaultTimeout(2200);
        try { await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 }); }
        catch(PlaywrightException e) when(errors.TryPeek(out var cause)) {
            throw new InvalidDataException("Page load failed: "+cause,e);
        }
        if(scriptFailure!=null)throw scriptFailure;
        if (errors.TryPeek(out var loadError)) throw new InvalidDataException(loadError);
        ILocator VisibleText() => page.GetByText(check.Expected, new() { Exact = true }).Filter(new() { Visible = true });
        string observed=check.Expected;
        switch (check.Kind)
        {
            case "browser_load": break;
            case "browser_flow":
                try { observed=await BrowserContract.Observe(page,check.Steps!,ct); }
                catch(Exception) when(!ct.IsCancellationRequested && scriptFailure!=null) {throw scriptFailure;}
                break;
            case "browser_layout":
                if (!await page.Locator("body").IsVisibleAsync() || string.IsNullOrWhiteSpace(await page.Locator("body").InnerTextAsync()) ||
                    !await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"))
                    throw new InvalidDataException($"Page was empty or overflowed horizontally at {width}px.");
                break;
            case "browser_heading":
                await page.GetByRole(AriaRole.Heading, new() { Name = check.Expected, Exact = true }).First.WaitForAsync();
                break;
            case "browser_click":
                if (await VisibleText().CountAsync() != 0) throw new InvalidDataException($"The expected result '{check.Expected}' was already visible before the click.");
                var control = page.GetByRole(AriaRole.Button, new() { Name = check.Target, Exact = true })
                    .Or(page.GetByRole(AriaRole.Link, new() { Name = check.Target, Exact = true }));
                try { await control.ClickAsync(); }
                catch (Exception e) when (e is TimeoutException or PlaywrightException) { throw new InvalidDataException($"No unique clickable button or link named '{check.Target}' at {width}px."); }
                try { await VisibleText().First.WaitForAsync(); }
                catch (TimeoutException) {
                    if(scriptFailure!=null)throw scriptFailure;
                    var body=System.Text.RegularExpressions.Regex.Replace(await page.Locator("body").InnerTextAsync(),@"\s+"," ").Trim();
                    throw new InvalidDataException($"Clicked '{check.Target}' at {width}px. The page never displayed '{check.Expected}'."+
                    (dialogMessage!=null?$" JavaScript dialog: '{dialogMessage}'. The check requires visible text in the page.":
                     suppressedDialog!=null?" JavaScript dialog blocked by browser: "+suppressedDialog:"")+
                    (errors.TryPeek(out var scriptError)?" Page error: "+scriptError:"")+
                    $" Visible page text: {body[..Math.Min(300,body.Length)]}."); }
                break;
            case "browser_text":
                await VisibleText().First.WaitForAsync();
                break;
            default: throw new InvalidDataException("Unknown browser acceptance operation.");
        }
        if (page.Url != url && (check.Kind!="browser_flow" || !Uri.TryCreate(page.Url,UriKind.Absolute,out var final) || final.GetLeftPart(UriPartial.Authority)!=Origin)) throw new InvalidDataException("The page navigated away from the verification target.");
        if(scriptFailure!=null)throw scriptFailure;
        if (errors.TryPeek(out var error)) throw new InvalidDataException(error);
        return app==null ? observed : observed+" · app: "+app.Observations;
    }
}

