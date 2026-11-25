using System.Net;
using System.Net.Sockets;

namespace Mason;
public enum ModelStartupPhase { Inspecting, Loading, Checking, Ready }

public sealed class ModelCudaUnavailableException(string message) : InvalidOperationException(message) { }

public sealed record ServedModel(Guid Id, LocalModel Model, AiEndpointProfile Profile, IModelHost Owner, string Log, ModelLaunchOptions? Options=null)
{
    public override string ToString() => $"{Model.Name}\n{Model.Task} · {Profile.Endpoint} · {(!Owner.IsRunning ? "Stopped unexpectedly" : Owner.Embedded ? "Running inside Mason Agent" : "Running · owned by Mason Agent")}";
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
        if(!model.Launchable)throw new ArgumentException("Mason serves chat, embeddings and reranking models.");
        if(model.Task!="Chat"&&ModelAdapters.UsesPython(model))throw new ArgumentException("Embeddings and rerankers run in llama.cpp. Use a GGUF build.");
        options??=new(4096);options.Validate();
        if (!model.Launchable || !Path.IsPathFullyQualified(model.Path) || string.IsNullOrWhiteSpace(alias) || port is < 1024 or > 65535) throw new ArgumentException("Invalid model launch plan.");
        if (!ModelAdapters.UsesPython(model))
        {
            using var input = File.OpenRead(model.Path); Span<byte> header = stackalloc byte[4];
            if (input.Read(header) != 4 || !header.SequenceEqual("GGUF"u8)) throw new InvalidDataException("Model is no longer a GGUF file.");
            var companion=Projector(model.Path);
            var common=new List<string>{"-m", model.Path, "--host", "127.0.0.1", "--port", port.ToString(), "--alias", alias, "-c", options.ContextTokens.ToString(), "--parallel", "1", "--device", gpu?"CUDA0":"none", "--split-mode", "none", "-ngl", gpu ? "99" : "0"};
            // Embeddings and reranking batch whole inputs; the batch must cover the context.
            if(model.Task=="Embeddings")return [..common,"--embeddings","-b",options.ContextTokens.ToString(),"-ub",options.ContextTokens.ToString()];
            if(model.Task=="Reranking")return [..common,"--reranking","-b",options.ContextTokens.ToString(),"-ub",options.ContextTokens.ToString()];
            common.AddRange(new[] { "-b", "256", "-ub", "128", "--reasoning", "auto" });
            if(options.DraftPath is {} draft)
            {
                var head=GgufHeader.TryRead(draft)?.Architecture??"";
                var type=head.Contains("dspark",StringComparison.OrdinalIgnoreCase)?"draft-dspark":head.Contains("eagle",StringComparison.OrdinalIgnoreCase)?"draft-eagle3":head.Contains("dflash",StringComparison.OrdinalIgnoreCase)?"draft-dflash":"draft-simple";
                common.AddRange(new[] { "--spec-type", type, "-md", draft, "-ngld", gpu ? "99" : "0", "-devd", gpu?"CUDA0":"none" });
            }
            else common.AddRange(new[] { "--spec-type", "none" });
            if(options.LoraPath is {} lora)common.AddRange(new[] { "--lora", lora });
            if(companion!=null)common.AddRange(new[] { "--mmproj", companion });
            return common.ToArray();
        }
        var script=Path.Combine(scriptDirectory,"Serve-Transformers.py");
        if(!File.Exists(script))throw new FileNotFoundException("The packaged chat serving adapter is missing.",script);
        var argv=new List<string>{script,"--model-dir",model.Path,"--model-id",alias,"--device",gpu?"cuda":"cpu","--port",port.ToString(),"--manifest",manifest,"--context",options.ContextTokens.ToString()};
        if(model.AdapterPath!=null){if(!Directory.Exists(model.AdapterPath)||!Path.IsPathFullyQualified(model.AdapterPath))throw new ArgumentException("Invalid local adapter selection.");argv.Add("--adapter-dir");argv.Add(model.AdapterPath);}
        return argv.ToArray();
    }
    /// <summary>The vision projector beside a GGUF: an exact "mmproj-&lt;file&gt;" match, else the closest name, preferring F16, BF16, then F32.</summary>
    public static string? Projector(string modelPath)
    {
        var folder=Path.GetDirectoryName(modelPath)!;var file=Path.GetFileNameWithoutExtension(modelPath);
        var candidates=Directory.EnumerateFiles(folder,"*mmproj*.gguf").Where(p=>!p.Equals(modelPath,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(candidates.FirstOrDefault(p=>Path.GetFileName(p).Equals("mmproj-"+Path.GetFileName(modelPath),StringComparison.OrdinalIgnoreCase)) is {} exact)return exact;
        // A projector belongs to this model only if it is generic (mmproj-F16.gguf) or shares the model's name.
        bool Generic(string p)=>System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(p),@"(?i)^mmproj(-model)?(-(BF16|F16|F32|Q\d\w*))?$");
        candidates=candidates.Where(p=>Generic(p)||Shared(p)>=Math.Min(8,file.Length)).ToArray();
        if(candidates.Length<=1)return candidates.FirstOrDefault();
        int Shared(string p){var stem=System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(p),"(?i)-?mmproj-?","");int n=0;while(n<stem.Length&&n<file.Length&&char.ToLowerInvariant(stem[n])==char.ToLowerInvariant(file[n]))n++;return n;}
        int Precision(string p){var n=Path.GetFileNameWithoutExtension(p);return n.EndsWith("-F16",StringComparison.OrdinalIgnoreCase)?0:n.EndsWith("-BF16",StringComparison.OrdinalIgnoreCase)?1:n.EndsWith("-F32",StringComparison.OrdinalIgnoreCase)?2:3;}
        return candidates.OrderByDescending(Shared).ThenBy(Precision).First();
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
        // GGUF models run inside Mason unless the setting is off or this build cannot be embedded now.
        string? processReason = null;
        bool embed = !ModelAdapters.UsesPython(model) && settings.EmbedGguf;
        if (embed)
        {
            try { await EmbeddedLlamaServer.WaitForStopping(TimeSpan.FromSeconds(90), ct); }
            catch (TimeoutException) { }
            processReason = EmbeddedLlamaServer.Unavailable(runtime);
            if (processReason != null) { embed = false; progress?.Invoke(processReason + " Starting it as a separate process."); }
        }
        else if (!ModelAdapters.UsesPython(model)) processReason = "Run GGUF models inside Mason is off.";
        var cudaLibraries = !gpu || ModelAdapters.UsesPython(model) ? null
            : embed ? CheckEmbeddedCuda(runtime, settings.Python, progress)
            : await CheckCuda(runtime, settings.Python, folder, progress, ct);
        File.WriteAllText(Path.Combine(folder,"launch.json"),System.Text.Json.JsonSerializer.Serialize(new { Runtime=runtime, Host=embed?"embedded":"process", ProcessReason=processReason, Arguments=argv, CudaLibraries=cudaLibraries, Python=settings.Python, SettingsRead=SettingsReadDiagnostics(), AppVersion=typeof(ModelServing).Assembly.GetName().Version?.ToString() },new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        ct.ThrowIfCancellationRequested();
        phase?.Invoke(ModelStartupPhase.Loading);
        IModelHost owner = embed ? EmbeddedLlamaServer.Start(runtime, argv, log, cudaLibraries) : OwnedModelProcess.Start(runtime, argv, log,cudaLibraries);
        var profileId = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(model.Task + "\n" + Path.GetFullPath(model.Path).ToUpperInvariant()+(model.AdapterPath==null?"":"\n"+Path.GetFullPath(model.AdapterPath).ToUpperInvariant())))[..16]);
        var capability = model.Task switch { "Embeddings" => AiCapability.Embeddings, "Reranking" => AiCapability.Reranking, _ => AiCapability.Chat };
        var profile = new AiEndpointProfile { Id = profileId, Name = model.Name, ManagedCapability = capability, Endpoint = $"http://127.0.0.1:{port}/", Model = alias };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
            using var client = new LocalAiClient(); AiCheck? listing = null;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var status=ReadStatus(Path.Combine(folder,"manifest.json"));
                if(status!=null)progress?.Invoke(status);
                if (!owner.IsRunning) throw new InvalidOperationException((status==null?RuntimeFailure(log,owner.ExitCode):status+" "+RuntimeFailure(log,owner.ExitCode)));
                listing = await client.Check(profile, AiCapability.Models, "", deadline.Token);
                if (listing.State == "Passed" && listing.Models?.Contains(alias, StringComparer.Ordinal) == true) break;
                await Task.Delay(750, deadline.Token);
            }
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
    /// <summary>Loads the build into Mason and confirms it registered CUDA0, without starting a process.</summary>
    static string? CheckEmbeddedCuda(string runtime, string python, Action<string>? progress)
    {
        progress?.Invoke("Loading llama.cpp into Mason and checking for CUDA…");
        var folder = Path.GetDirectoryName(Path.GetFullPath(runtime))!;
        // CUDA runtime libraries beside the build win; otherwise use the selected PyTorch environment's copies.
        var libraries = Directory.EnumerateFiles(folder, "cudart64_*.dll").Any() ? null : RuntimeDiscovery.CudaLibraryCandidates(runtime, python).FirstOrDefault(x => x != null);
        IReadOnlyList<string> devices;
        try { devices = EmbeddedLlamaServer.Load(runtime, libraries); }
        catch (InvalidOperationException e) { throw new ModelCudaUnavailableException(e.Message); }
        if (devices.Contains("CUDA0")) return libraries;
        throw new ModelCudaUnavailableException("The selected GGUF runtime did not register CUDA0 inside Mason. Devices found: " + (devices.Count == 0 ? "none" : string.Join(", ", devices)) + ".");
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
            using var probe = OwnedModelProcess.Start(runtime, new[] { "--list-devices" }, log, libraries);
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
