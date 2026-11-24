using System.Text.Json;
using Microsoft.Playwright;

namespace Mason.Code;

internal sealed class CodeVerificationApp : IAsyncDisposable
{
    readonly CodeWorkspace copy;
    readonly string mailbox;
    readonly CancellationTokenSource lifetime;
    Task<CodeCommandResult>? process;
    readonly List<string> log=[];
    readonly List<(string Route,byte[] Json)> responses=[];
    public string Observations { get { lock(log) return string.Join(", ",log.TakeLast(8)); } }
    CodeVerificationApp(string root,CancellationToken ct)
    {
        copy=new(root); mailbox="mason-bridge-"+Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(copy.Resolve(mailbox)); lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
    }
    public static async Task<CodeVerificationApp> Start(CodeWorkspace project,string entry,CancellationToken ct,CodeBrowserVerification.Candidate? candidate=null)
    {
        var root=Directory.CreateTempSubdirectory("mason-verify-").FullName;
        var app=new CodeVerificationApp(root,ct);
        try
        {
            var inventory=project.VerificationFiles();
            if(inventory.Truncated) throw new InvalidDataException("The verification app exceeds the 1,000-file snapshot bound.");
            long total=0;
            foreach(var path in inventory.Files)
            {
                ct.ThrowIfCancellationRequested();
                using var source=File.OpenRead(project.Resolve(path));
                if(source.Length>4*1024*1024 || (total+=source.Length)>32*1024*1024) throw new InvalidDataException("Application snapshot exceeds verification limits.");
                var dest=app.copy.Resolve(path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var file=File.Create(dest);
                if(candidate!=null && path.Equals(candidate.Path,StringComparison.OrdinalIgnoreCase))await file.WriteAsync(candidate.Content,ct);
                else await source.CopyToAsync(file,ct);
            }
            using(var source=typeof(CodeVerificationApp).Assembly.GetManifestResourceStream("Mason.Code.CodeWsgiBridge.py")!)
            using(var file=File.Create(app.copy.Resolve(app.mailbox+"/runner.py"))) await source.CopyToAsync(file,ct);
            app.process=CodeCommand.Run(CodeCommand.ResolveExecutable("python",root),["-B",app.mailbox+"/runner.py",entry],root,app.lifetime.Token,root);
            // First launch can stage the isolated toolchain before the bounded command starts.
            await app.Wait(app.mailbox+"/ready.json",ct,150);
            return app;
        }
        catch { await app.DisposeAsync(); throw; }
    }
    async Task<string> Wait(string relative,CancellationToken ct,int seconds)
    {
        var path=copy.Resolve(relative);
        var start=System.Diagnostics.Stopwatch.StartNew();
        while(!File.Exists(path))
        {
            ct.ThrowIfCancellationRequested();
            if(process!.IsCompleted) { var r=await process; throw new InvalidDataException($"Verification app exited {r.ExitCode}: {r.Output[..Math.Min(1200,r.Output.Length)]}"); }
            if(start.Elapsed.TotalSeconds>=seconds) throw new TimeoutException("Timed out waiting for the isolated verification app. "+Observations);
            await Task.Delay(30,ct);
        }
        return copy.Resolve(relative);
    }
    public async Task<(int Status,byte[] Body,Dictionary<string,string> Headers)> Request(IRequest request,Uri uri,CancellationToken ct)
    {
        if(request.Method is not ("GET" or "POST" or "HEAD" or "PUT" or "PATCH" or "DELETE")) throw new InvalidDataException("Unsupported local application request.");
        var id=mailbox+"/"+Guid.NewGuid().ToString("N");
        var headers=await request.AllHeadersAsync();
        var body=request.PostDataBuffer??[];
        if(body.Length>128*1024) throw new InvalidDataException("Application request exceeds 128 KiB.");
        var payload=JsonSerializer.SerializeToUtf8Bytes(new { method=request.Method,path=uri.PathAndQuery,body=Convert.ToBase64String(body),
            contentType=headers.GetValueOrDefault("content-type",""),cookie=headers.GetValueOrDefault("cookie","") });
        await File.WriteAllBytesAsync(copy.Resolve(id+".writing"),payload,ct);
        File.Move(copy.Resolve(id+".writing"),copy.Resolve(id+".request"));
        var path=await Wait(id+".response",ct,8);
        using var input=File.OpenRead(path);
        if(input.Length>3*1024*1024) throw new InvalidDataException("Oversized app response.");
        var bytes=new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes,ct);
        using var document=JsonDocument.Parse(bytes);
        var response=document.RootElement;
        var status=response.GetProperty("status").GetInt32();
        lock(log) log.Add($"{request.Method} {uri.AbsolutePath} {status}");
        if(status<200 || status>=400) throw new InvalidDataException($"Application returned {status} for {uri.AbsolutePath}. "+(response.TryGetProperty("error",out var error)?error.GetString():""));
        var allowed=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var h in response.GetProperty("headers").EnumerateObject())
            if(h.Name is "content-type" or "set-cookie" or "cache-control" or "location") allowed[h.Name]=h.Value.GetString()!;
        if(allowed.TryGetValue("location",out var redirect) && (!Uri.TryCreate(uri,redirect,out var target) || target.GetLeftPart(UriPartial.Authority)!=uri.GetLeftPart(UriPartial.Authority)))
            throw new InvalidDataException("Verification app redirected outside its isolated origin.");
        var bytesOut=response.GetProperty("body").GetBytesFromBase64();
        lock(responses)responses.Add(($"{request.Method} {uri.AbsolutePath}",allowed.GetValueOrDefault("content-type","").Contains("json",StringComparison.OrdinalIgnoreCase) && bytesOut.Length<=65536 ? bytesOut : []));
        return(status,bytesOut,allowed);
    }
    internal async Task<string> Assert(BrowserStep step,string? captured,CancellationToken ct)
    {
        if(step.Op=="sqlite_count"){
            var parts=step.Target.Split('#');
            var path=copy.Resolve(parts[0]);
            if(!File.Exists(path))throw new InvalidDataException("Missing verification database: "+parts[0]);
            // A separate isolated interpreter reads committed rows, independently of the
            // application's imports, HTTP responses, and connection-local transaction.
            const string query="""
                import pathlib, sqlite3, sys
                with sqlite3.connect(pathlib.Path(sys.argv[1]).resolve().as_uri() + '?mode=ro', uri=True, timeout=2) as db:
                    db.execute('PRAGMA trusted_schema=OFF')
                    name=sys.argv[2]
                    if not any(row[0]=='main' and row[1]==name and row[2]=='table' for row in db.execute('PRAGMA table_list')):
                        raise ValueError('Expected a regular stored table')
                    print(db.execute('SELECT count(*) FROM "' + name + '"').fetchone()[0])
                """;
            var result=await CodeCommand.Run(CodeCommand.ResolveExecutable("python",copy.Root),["-I","-B","-c",query,parts[0],parts[1]],copy.Root,ct,copy.Root);
            if(result.ExitCode!=0 || result.Truncated || !int.TryParse(result.Output.Trim(),out var rows))
                throw new InvalidDataException("Could not read committed row count from "+step.Target+": "+result.Output.Trim()[..Math.Min(result.Output.Trim().Length,240)]);
            if(rows!=int.Parse(step.Value))throw new InvalidDataException($"sqlite_count {step.Target}: expected {step.Value}, observed {rows} committed rows.");
            return $"sqlite_count {step.Target} = {rows}";
        }
        byte[][] matched;
        lock(responses)matched=responses.Where(r=>r.Route==step.Target).Select(r=>r.Json).ToArray();
        if(step.Op=="response_count"){
            if(matched.Length!=int.Parse(step.Value))throw new InvalidDataException($"response_count {step.Target}: expected {step.Value}, observed {matched.Length}.");
            return $"response_count {step.Target} = {matched.Length}";
        }
        if(matched.Length==0)throw new InvalidDataException("No response observed from "+step.Target);
        foreach(var json in matched){
            if(json.Length==0)throw new InvalidDataException("Expected a bounded JSON response from "+step.Target);
            using var data=JsonDocument.Parse(json);
            if(data.RootElement.ValueKind!=JsonValueKind.Object || !data.RootElement.TryGetProperty(step.Value,out var value) ||
                value.ValueKind!=JsonValueKind.String || value.GetString()!=captured)
                throw new InvalidDataException($"response_same {step.Target}: field {step.Value} differed from the captured receipt.");
        }
        return $"response_same {step.Target} · {matched.Length} responses matched {captured}";
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if(process!=null && !process.IsCompleted)
            {
                await File.WriteAllTextAsync(copy.Resolve(mailbox+"/stop"),"");
                await Task.WhenAny(process,Task.Delay(500));
            }
        }
        finally
        {
            lifetime.Cancel();
            try { if(process!=null) await process; } catch(OperationCanceledException) { }
            finally
            {
                lifetime.Dispose();
                if(Path.GetDirectoryName(copy.Root)!=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                    !Path.GetFileName(copy.Root).StartsWith("mason-verify-",StringComparison.Ordinal)) throw new IOException("Unexpected verification cleanup path.");
                Directory.Delete(copy.Root,true);
            }
        }
    }
}
