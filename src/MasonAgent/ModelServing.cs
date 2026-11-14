using System.Net;
using System.Net.Sockets;

namespace Mason;
public enum ModelStartupPhase { Inspecting, Loading, Checking, Ready }

public sealed record ServedModel(Guid Id, LocalModel Model, AiEndpointProfile Profile, OwnedModelProcess Owner, string Log, ModelLaunchOptions? Options=null)
{
    public override string ToString() => $"{Model.Name}\n{Model.Task} · {Profile.Endpoint} · {(!Owner.IsRunning ? "Stopped unexpectedly" : "Running · owned by Mason Agent")}";
}
public sealed class ModelServing : IDisposable
{
    readonly List<ServedModel> running = [];
    public IReadOnlyList<ServedModel> Running => running;
    public static string[] Arguments(LocalModel model, string alias, int port, bool gpu, string scriptDirectory, string manifest, ModelLaunchOptions? options=null)
    {
        if(model.Task!="Chat")throw new ArgumentException("This app serves language models only.");
        options??=new(4096);options.Validate();
        if (!model.Launchable || !Path.IsPathFullyQualified(model.Path) || string.IsNullOrWhiteSpace(alias) || port is < 1024 or > 65535) throw new ArgumentException("Invalid model launch plan.");
        if (!ModelAdapters.UsesPython(model))
        {
            using var input = File.OpenRead(model.Path); Span<byte> header = stackalloc byte[4];
            if (input.Read(header) != 4 || !header.SequenceEqual("GGUF"u8)) throw new InvalidDataException("Model is no longer a GGUF file.");
            return ["-m", model.Path, "--host", "127.0.0.1", "--port", port.ToString(), "--alias", alias, "-c", options.ContextTokens.ToString(), "--parallel", "1", "-b", "256", "-ub", "128", "--reasoning", "auto", "--spec-type", "none", "--device", gpu?"CUDA0":"none", "--split-mode", "none", "-ngl", gpu ? "99" : "0"];
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
        var runtime = ModelAdapters.IsQuantized(model) ? settings.QuantizedPython : ModelAdapters.UsesPython(model) ? settings.Python : settings.LlamaServer;
        if (!ModelAdapters.UsesPython(model) && !Path.GetFileName(runtime).Equals("llama-server.exe",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose llama-server.exe, not the llama launcher or Ollama application.");
        string? cudaLibraries=null;
        if(gpu&&!ModelAdapters.UsesPython(model)&&!string.IsNullOrWhiteSpace(settings.Python))
        {
            var pythonFolder=Path.GetDirectoryName(settings.Python)!;
            var pythonRoot=Path.GetFileName(pythonFolder).Equals("Scripts",StringComparison.OrdinalIgnoreCase)?Directory.GetParent(pythonFolder)!.FullName:pythonFolder;
            var libraries=Path.Combine(pythonRoot,"Lib","site-packages","torch","lib");
            if(File.Exists(Path.Combine(libraries,"cublas64_13.dll")))cudaLibraries=libraries;
        }
        File.WriteAllText(Path.Combine(folder,"launch.json"),System.Text.Json.JsonSerializer.Serialize(new { Runtime=runtime, Arguments=argv, CudaLibraries=cudaLibraries, Python=settings.Python, SettingsRead=SettingsReadDiagnostics(), AppVersion=typeof(ModelServing).Assembly.GetName().Version?.ToString() },new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        if(gpu&&!ModelAdapters.UsesPython(model))
        {
            progress?.Invoke("Checking CUDA device in the selected runtime…");
            var probeLog=Path.Combine(folder,"devices.log");
            using var probe=OwnedModelProcess.Start(runtime,["--list-devices"],probeLog,cudaLibraries);
            using var probeDeadline=CancellationTokenSource.CreateLinkedTokenSource(ct);probeDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            try { await probe.Process.WaitForExitAsync(probeDeadline.Token); }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new TimeoutException("Runtime device check exceeded 30 seconds. Log: "+probeLog);}
            if(probe.ExitCode!=0)throw new InvalidOperationException(RuntimeFailure(probeLog,probe.ExitCode));
            if(!System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(probeLog),@"(?m)^\s*CUDA0\s*:"))
                throw new InvalidOperationException("The selected runtime did not report CUDA0. "+RuntimeFailure(probeLog,probe.ExitCode,"Device probe completed"));
        }
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
            var served = new ServedModel(id, model, profile, owner, log,options??new(4096)); running.Add(served);if(inference.State=="Passed")phase?.Invoke(ModelStartupPhase.Ready); return served;
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) {owner.Dispose();throw new TimeoutException("Model startup exceeded five minutes. Try a smaller model or inspect the runtime log: "+log);}
        catch { owner.Dispose(); throw; }
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
    public void Stop(ServedModel server) { if (!running.Remove(server)) throw new InvalidOperationException("This server is not owned by this app session."); server.Owner.Dispose(); }
    public void Dispose() { foreach (var server in running) server.Owner.Dispose(); running.Clear(); }
}
