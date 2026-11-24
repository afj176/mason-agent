using System.Net;
using System.Net.Sockets;

namespace Mason;
public enum ModelStartupPhase { Inspecting, Loading, Checking, Ready }

public sealed class ModelCudaUnavailableException(string message) : InvalidOperationException(message) { }

public sealed record ServedModel(Guid Id, LocalModel Model, AiEndpointProfile Profile, OwnedModelProcess Owner, string Log, ModelLaunchOptions? Options=null)
{
    public override string ToString() => $"{Model.Name}\n{Model.Task} · {Profile.Endpoint} · {(!Owner.IsRunning ? "Stopped unexpectedly" : "Running · owned by Mason Agent")}";
}
public sealed class ModelServing : IDisposable
{
    public sealed record LaunchRecipe(LocalModel Model,bool Gpu,ModelLaunchOptions Options);
    public static string RecipePath(Guid id)=>Path.Combine(Store.Root,"model-launches",id.ToString("N")+".json");
    public void ReleaseRole(ServedModel server){if(running.Remove(server))server.Owner.Dispose();}
    readonly List<ServedModel> running = [];
    public IReadOnlyList<ServedModel> Running => running;

    static string RuntimePath(LocalModel model, ModelLibrarySettings settings) =>
        ModelAdapters.IsQuantized(model) ? settings.QuantizedPython : ModelAdapters.UsesPython(model) ? settings.Python : settings.LlamaServer;

    public static bool HasRuntime(LocalModel model, ModelLibrarySettings settings)
    {
        var path = RuntimePath(model, settings);
        return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path) &&
            (ModelAdapters.UsesPython(model) ? path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                : Path.GetFileName(path).Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase));
    }

    public static string[] Arguments(LocalModel model, string alias, int port, bool gpu, string scriptDirectory, string manifest, ModelLaunchOptions? options=null)
    {
        if(model.Task!="Chat")throw new ArgumentException("This app serves language models only.");
        options??=new(4096);options.Validate();
        if (!model.Launchable || !Path.IsPathFullyQualified(model.Path) || string.IsNullOrWhiteSpace(alias) || port is < 1024 or > 65535) throw new ArgumentException("Invalid model launch plan.");
        if (!ModelAdapters.UsesPython(model))
        {
            using var input = File.OpenRead(model.Path); Span<byte> header = stackalloc byte[4];
            if (input.Read(header) != 4 || !header.SequenceEqual("GGUF"u8)) throw new InvalidDataException("Model is no longer a GGUF file.");
            string Family(string path)=>System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(path),@"^(?:mmproj-)|-(?:BF16|F16|F32|Q\d[\w_]*)$","",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var candidates=Directory.EnumerateFiles(Path.GetDirectoryName(model.Path)!,"mmproj-*.gguf")
                .Where(p=>Family(p).Equals(Family(model.Path),StringComparison.OrdinalIgnoreCase)).ToArray();
            var companion=candidates.FirstOrDefault(p=>Path.GetFileName(p).Equals("mmproj-"+Path.GetFileName(model.Path),StringComparison.OrdinalIgnoreCase))??(candidates.Length==1?candidates[0]:null);
            return ["-m", model.Path, "--host", "127.0.0.1", "--port", port.ToString(), "--alias", alias, "-c", options.ContextTokens.ToString(), "--parallel", "1", "-b", "256", "-ub", "128", "--reasoning", "auto", "--spec-type", "none", "--device", gpu?"CUDA0":"none", "--split-mode", "none", "-ngl", gpu ? "99" : "0",..(companion==null?Array.Empty<string>():new[]{"--mmproj",companion})];
        }
        var script=Path.Combine(scriptDirectory,"Serve-Transformers.py");
        if(!File.Exists(script))throw new FileNotFoundException("The packaged chat serving adapter is missing.",script);
        var argv=new List<string>{script,"--model-dir",model.Path,"--model-id",alias,"--device",gpu?"cuda":"cpu","--port",port.ToString(),"--manifest",manifest,"--context",options.ContextTokens.ToString()};
        if(model.AdapterPath!=null){if(!Directory.Exists(model.AdapterPath)||!Path.IsPathFullyQualified(model.AdapterPath))throw new ArgumentException("Invalid local adapter selection.");argv.Add("--adapter-dir");argv.Add(model.AdapterPath);}
        return argv.ToArray();
    }
    public async Task<ServedModel> Start(LocalModel model, ModelLibrarySettings settings, bool gpu, string scriptDirectory, CancellationToken ct, Action<string>? progress = null, ModelLaunchOptions? options=null,Action<ModelStartupPhase>? phase=null)
    {
        phase?.Invoke(ModelStartupPhase.Inspecting);
        options ??= new(); options.Validate();
        if(model.Task=="Chat")
        {
            var metadata=await Task.Run(()=>ModelMemory.Read(model,ct),ct);
            if(metadata.ContextDeclared&&options.ContextTokens>metadata.MaxContext)throw new ArgumentException($"Requested context {options.ContextTokens:N0} exceeds this model's declared {metadata.MaxContext:N0}-token capacity.");
        }
        if (running.Any(x => x.Owner.IsRunning && x.Model.Path.Equals(model.Path, StringComparison.OrdinalIgnoreCase)&&x.Model.AdapterPath==model.AdapterPath)) throw new InvalidOperationException("This model/adapter is already running in Mason Agent.");
        if (running.Count(x => x.Owner.IsRunning) >= 3) throw new InvalidOperationException("Up to three managed servers can run at once. Stop one before starting another.");
        var id = Guid.NewGuid(); var alias = "mason-" + model.Task.ToLowerInvariant() + "-" + id.ToString("N")[..12];
        int port = Enumerable.Range(18310,20).FirstOrDefault(Available);
        if (port == 0) throw new InvalidOperationException("Managed server ports 18310–18329 are occupied.");
        var folder = Path.Combine(Store.Root, "model-servers", id.ToString("N"));
        Directory.CreateDirectory(folder); var log = Path.Combine(folder, "runtime.log");
        var argv = Arguments(model, alias, port, gpu, scriptDirectory, Path.Combine(folder, "manifest.json"),options);
        var runtime = RuntimePath(model, settings);
        if (!HasRuntime(model, settings))
            throw new ArgumentException(ModelAdapters.UsesPython(model) ? "Choose an existing Python executable in Setup." : "Choose an existing llama-server.exe in Setup.");
        var cudaLibraries = gpu && !ModelAdapters.UsesPython(model)
            ? await CheckCuda(runtime, settings.Python, folder, progress, ct)
            : null;
        File.WriteAllText(Path.Combine(folder,"launch.json"),System.Text.Json.JsonSerializer.Serialize(new { Runtime=runtime, Arguments=argv, CudaLibraries=cudaLibraries, Python=settings.Python, SettingsRead=SettingsReadDiagnostics(), AppVersion=typeof(ModelServing).Assembly.GetName().Version?.ToString() },new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        ct.ThrowIfCancellationRequested();
        phase?.Invoke(ModelStartupPhase.Loading);
        var owner = OwnedModelProcess.Start(runtime, argv, log,cudaLibraries);
        var profileId = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(model.Task + "\n" + Path.GetFullPath(model.Path).ToUpperInvariant()+(model.AdapterPath==null?"":"\n"+Path.GetFullPath(model.AdapterPath).ToUpperInvariant())))[..16]);
        var profile = new AiEndpointProfile { Id = profileId, Name = model.Name, ManagedCapability = AiCapability.Chat, Endpoint = $"http://127.0.0.1:{port}/", Model = alias };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
            using var client = new LocalAiClient(); AiCheck? listing = null;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var status=ReadStatus(Path.Combine(folder,"manifest.json"));
                if(status!=null)progress?.Invoke(status);
                if (owner.Process.HasExited) throw new InvalidOperationException((status==null?RuntimeFailure(log,owner.ExitCode):status+" "+RuntimeFailure(log,owner.ExitCode)));
                listing = await client.Check(profile, AiCapability.Models, "", deadline.Token);
                if (listing.State == "Passed" && listing.Models?.Contains(alias, StringComparer.Ordinal) == true) break;
                await Task.Delay(750, deadline.Token);
            }
            var capability=AiCapability.Chat;
            phase?.Invoke(ModelStartupPhase.Checking);
            var inference = await client.Check(profile, capability, "", deadline.Token);
            profile = profile with { Checks = [listing, inference] };
            var served = new ServedModel(id, model, profile, owner, log,options??new(4096));
            if(inference.State=="Passed"){
                var path=RecipePath(profile.Id);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{File.WriteAllText(tmp,System.Text.Json.JsonSerializer.Serialize(new LaunchRecipe(model,gpu,served.Options!)));File.Move(tmp,path,true);}
                finally{if(File.Exists(tmp))File.Delete(tmp);}
            }
            running.Add(served);if(inference.State=="Passed")phase?.Invoke(ModelStartupPhase.Ready); return served;
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) {owner.Dispose();throw new TimeoutException("Model startup exceeded five minutes. Try a smaller model or inspect the runtime log: "+log);}
        catch { owner.Dispose(); throw; }
    }
    static async Task<string?> CheckCuda(string runtime, string python, string folder, Action<string>? progress, CancellationToken ct)
    {
        progress?.Invoke("Checking whether the selected GGUF runtime can use CUDA…");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        string? detail = null;
        int attempt = 0;
        foreach (var libraries in RuntimeDiscovery.CudaLibraryCandidates(runtime, python))
        {
            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) break;
            var log = Path.Combine(folder, attempt++ == 0 ? "devices.log" : $"devices-retry-{attempt - 1}.log");
            using var probe = OwnedModelProcess.Start(runtime, ["--list-devices"], log, libraries);
            try { await probe.Process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ModelCudaUnavailableException("The CUDA device check exceeded 30 seconds. Log: " + log);
            }
            if (probe.ExitCode == 0 && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(log), @"(?m)^\s*CUDA0\s*:"))
                return libraries;
            detail = RuntimeFailure(log, probe.ExitCode, "CUDA device check");
        }
        throw new ModelCudaUnavailableException("The selected GGUF runtime could not use CUDA0. " + detail);
    }

    static object SettingsReadDiagnostics()
    {
        var path=Path.Combine(Store.Root,"model-library.json");
        try
        {
            var bytes=File.ReadAllBytes(path);var text=System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            var saved=System.Text.Json.JsonSerializer.Deserialize<ModelLibrarySettings>(text);
            return new { Path=path, Exists=File.Exists(path), Sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), Runtime=saved?.LlamaServer, Python=saved?.Python };
        }
        catch(Exception error){return new {Path=path,Exists=File.Exists(path),Error=error.GetType().Name,Detail=error.Message};}
    }
    public static string RuntimeFailure(string log,uint exitCode,string context="Runtime exited before readiness")
    {
        string detail="No runtime output was captured.";
        try
        {
            using var stream=new FileStream(log,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var offset=Math.Max(0,stream.Length-16384);stream.Position=offset;
            using var reader=new StreamReader(stream);var tail=reader.ReadToEnd();
            if(offset>0){var newline=tail.IndexOf('\n');tail=newline<0?"":tail[(newline+1)..];}
            var lines=tail.Split('\n').Select(x=>new string(x.TrimEnd('\r').Where(c=>!char.IsControl(c)||c=='\t').ToArray())).Where(x=>!string.IsNullOrWhiteSpace(x)).ToArray();
            var errors=lines.Where(x=>System.Text.RegularExpressions.Regex.IsMatch(x,@"(?i)error|failed|invalid|fatal|exception|\bE\s")).Take(3).ToArray();
            var selected=errors.Length>0?errors:lines.TakeLast(5).ToArray();
            if(selected.Length>0)detail=string.Join("\n",selected);
            if(detail.Length>1800)detail=detail[..1800]+"…";
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
        return $"{context} (exit {exitCode}, 0x{exitCode:X8}).\n{detail}\nLog: {log}";
    }
    static string? ReadStatus(string path)
    {
        try { using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));return doc.RootElement.TryGetProperty("status",out var status)?status.GetString():null; }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){return null;}
    }
    static bool Available(int port) { try { var listener = new TcpListener(IPAddress.Loopback,port); listener.Start(); listener.Stop(); return true; } catch (SocketException) { return false; } }
    public void Stop(ServedModel server) { if (!running.Remove(server)) throw new InvalidOperationException("This server is not owned by this app session."); server.Owner.Stop(); }
    public void Dispose() { foreach (var server in running) server.Owner.Dispose(); running.Clear(); }
}
