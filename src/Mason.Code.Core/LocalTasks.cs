using System.Diagnostics;
using System.Text.Json;

namespace Mason.Code;

public sealed record TerminalLaunch(string Executable,string[] Arguments,string Directory);
public sealed record TerminalLine(string? Text);
public sealed record TaskModelConnection(string Endpoint,string Model,string ProtectedKey,Guid? Lifetime);
public sealed record TaskModelOptions(TaskModelConnection Main,TaskModelConnection? Reviewer,TaskModelConnection? Visual,string Reasoning,int RoleLimit,bool Trace);
public sealed record LocalTaskSpec(Guid Id, string Workspace, string Prompt, string Endpoint, string Model, CodePermission Permission, Guid? ModelLifetime = null, TerminalLaunch? Terminal = null, TaskModelOptions? Models = null);
public sealed record TaskDecision(Guid Id, CodeApproval Approval);
public sealed record LocalTaskStatus(string State, string Detail, int Pid, long Started, Guid? Session = null, TaskDecision? Decision = null)
{
    public bool Active => State is "Starting" or "Running" or "Waiting for approval";
}

/// <summary>The detached worker is the sole journal writer. Clients submit one decision for a particular prompt.</summary>
public sealed class LocalTasks
{
    readonly string root;
    public LocalTasks(string profile) { root = Path.Combine(Path.GetFullPath(profile), "background-tasks"); }
    string Folder(Guid id) => Path.Combine(root, id.ToString("N"));
    public string Journal(Guid id) => Path.Combine(Folder(id), "run");
    static T Read<T>(string path)
    {
        using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("Invalid task record.");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Missing task record.");
    }
    static void Write<T>(string target, T data, bool replace)
    {
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { JsonSerializer.Serialize(stream, data); stream.Flush(true); }
            File.Move(temp,target,replace);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public LocalTaskSpec Spec(Guid id) => Read<LocalTaskSpec>(Path.Combine(Folder(id),"request.json"));
    public void ForgetKeys(LocalTaskSpec spec)
    {
        if(spec.Models is not {} options)return;
        static TaskModelConnection? Clear(TaskModelConnection? value)=>value is null?null:value with{ProtectedKey=""};
        Write(Path.Combine(Folder(spec.Id),"request.json"),spec with {Models=options with {Main=Clear(options.Main)!,Reviewer=Clear(options.Reviewer),Visual=Clear(options.Visual)}},true);
    }
    public LocalTaskStatus Status(Guid id)
    {
        var result = Read<LocalTaskStatus>(Path.Combine(Folder(id),"status.json"));
        if (!result.Active || result.Pid == 0) return result;
        try {
            using var process = Process.GetProcessById(result.Pid);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == result.Started) return result;
        } catch (ArgumentException) { } catch (InvalidOperationException) { }
        return result with {State="Interrupted", Detail="The worker exited. Its journal is retained. Tools were not replayed.", Decision=null};
    }
    public void Update(Guid id, LocalTaskStatus status) => Write(Path.Combine(Folder(id),"status.json"),status,true);
    public IEnumerable<Guid> All(string workspace) => Directory.Exists(root) ? Directory.EnumerateDirectories(root)
        .Select(Path.GetFileName).Where(x => Guid.TryParseExact(x,"N",out _)).Select(x => Guid.Parse(x!))
        .Where(id => File.Exists(Path.Combine(Folder(id),"status.json")) && string.Equals(Spec(id).Workspace,workspace,StringComparison.OrdinalIgnoreCase)).ToArray() : [];

    public Guid Start(string executable, string workspace, string prompt, string endpoint, string model, CodePermission permission, Guid? modelLifetime = null, TerminalLaunch? terminal = null, TaskModelOptions? models = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new FileNotFoundException("The installed mason CLI is required.",executable);
        var scope = new CodeWorkspace(workspace);
        if (CodeSandbox.ContainsHost(scope.Root,root) || CodeSandbox.ContainsHost(root,scope.Root)) throw new ArgumentException("Task storage and workspace must be separate.");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length>16000 || string.IsNullOrWhiteSpace(model) || model.Length>256)
            throw new ArgumentException("Enter a task up to 16,000 characters and a local model.");
        if(terminal!=null){if(permission!=CodePermission.Auto || terminal.Arguments.Length>40 || terminal.Arguments.Any(a=>a.Length>4000))throw new ArgumentException("A terminal requires Auto and bounded arguments.");scope.Resolve(terminal.Directory,true);}
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Folder(id));
        Write(Path.Combine(Folder(id),"request.json"),new LocalTaskSpec(id,scope.Root,prompt,endpoint,model,permission,modelLifetime,terminal,models),false);
        string[] arguments=["task-worker","--id",id.ToString(),"--task-home",Path.GetDirectoryName(root)!];
        // Publish the initial identity before releasing the worker to claim the request.
        using var launch = new FileStream(Path.Combine(Folder(id),"launch.lock"),FileMode.CreateNew,FileAccess.Write,FileShare.None);
        try {
            using var child=CodeCommand.StartDetachedWorker(executable,arguments);
            Update(id,new("Starting","Opening journal",child.Id,child.StartTime.ToUniversalTime().Ticks));
            return id;
        } catch (Exception e) { Update(id,new("Failed",e.Message,0,0)); throw; }
    }
    public async Task<FileStream> Claim(Guid id, CancellationToken token)
    {
        for(int attempt=0;;attempt++) {
            token.ThrowIfCancellationRequested();
            try { using var gate=File.Open(Path.Combine(Folder(id),"launch.lock"),FileMode.Open,FileAccess.Read,FileShare.Read); break; }
            catch(IOException) when(attempt<100) {await Task.Delay(50,token);}
        }
        var lease = new FileStream(Path.Combine(Folder(id),"worker.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
        try {
            var status=Status(id);
            if(status.Pid!=Environment.ProcessId || status.State!="Starting") throw new InvalidOperationException("This worker does not own the task.");
            return lease;
        } catch { lease.Dispose(); throw; }
    }
    public void Decide(Guid task, Guid request, bool allow)
    {
        var state=Status(task);
        if (state.State!="Waiting for approval" || state.Decision?.Id!=request) throw new InvalidOperationException("The selected approval is no longer pending.");
        Write(Path.Combine(Folder(task),"decision-"+request.ToString("N")+".json"),allow,false);
    }
    public async Task<bool> AwaitDecision(Guid task, Guid request, CancellationToken token)
    {
        var path=Path.Combine(Folder(task),"decision-"+request.ToString("N")+".json");
        while(!File.Exists(path)) await Task.Delay(200,token);
        token.ThrowIfCancellationRequested(); return Read<bool>(path);
    }
    public void SendLine(Guid id,string? line)
    {
        if(Spec(id).Terminal==null || !Status(id).Active)throw new InvalidOperationException("Select an active terminal.");
        if(line!=null && (line.Length>4000 || line.IndexOfAny(['\r','\n','\0'])>=0))throw new ArgumentException("Send a single terminal input line.");
        string folder=Path.Combine(Folder(id),"input");Directory.CreateDirectory(folder);
        if(Directory.EnumerateFiles(folder,"*.json").Take(64).Count()>=64)throw new InvalidOperationException("The input queue is full.");
        Write(Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff")+"-"+Guid.NewGuid().ToString("N")+".json"),new TerminalLine(line),false);
    }
    public async Task<CodeCommandResult> Terminal(Guid id,LocalTaskSpec spec,CancellationToken token)
    {
        var command=spec.Terminal??throw new InvalidDataException("This task has no terminal.");
        if(spec.Permission!=CodePermission.Auto)throw new UnauthorizedAccessException("Terminal requires Auto.");
        var scope=new CodeWorkspace(spec.Workspace);using var input=new CodeTerminalInput();
        var folder=Path.Combine(Folder(id),"input");Directory.CreateDirectory(folder);
        Func<string>? tail=null;string previous="";
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);
        var run=CodeCommand.Run(CodeCommand.ResolveExecutable(command.Executable,scope.Root),command.Arguments,scope.Resolve(command.Directory,true),
            lifetime.Token,scope.Root,timeout:TimeSpan.FromMinutes(30),observeLog:read=>Volatile.Write(ref tail,read),terminal:input);
        try {
            while(!run.IsCompleted) {
                token.ThrowIfCancellationRequested();
                foreach(var file in Directory.EnumerateFiles(folder,"*.json").Order(StringComparer.Ordinal)) {
                    var item=Read<TerminalLine>(file);
                    if(item.Text==null)input.Eof();else if(!input.TrySend(item.Text))break;
                    File.Delete(file);
                }
                var output=Volatile.Read(ref tail)?.Invoke()??"";
                if(output!=previous){Event(id,"terminal.output",output[^Math.Min(8000,output.Length)..]);previous=output;}
                await Task.WhenAny(run,Task.Delay(180,token));
            }
            var result=await run;Event(id,"terminal.exit",new{result.ExitCode,result.Truncated,output=result.Output[^Math.Min(8000,result.Output.Length)..]});return result;
        } finally {lifetime.Cancel();try{await run;}catch(Exception) when(run.IsCanceled||run.IsFaulted){}}
    }
    public void Cancel(Guid id)
    {
        if (!Status(id).Active) return;
        Write(Path.Combine(Folder(id),"cancel.json"),true,true);
    }
    public async Task WatchCancellation(Guid id,CancellationTokenSource source)
    {
        try { while (!source.IsCancellationRequested && !File.Exists(Path.Combine(Folder(id),"cancel.json"))) await Task.Delay(200,source.Token); }
        catch (OperationCanceledException) when(source.IsCancellationRequested) {}
        source.Cancel();
    }
    public void Event(Guid id,string type,object detail)
    {
        string line=JsonSerializer.Serialize(new{at=DateTimeOffset.UtcNow,type,detail});
        using var stream=new FileStream(Path.Combine(Folder(id),"events.jsonl"),FileMode.Append,FileAccess.Write,FileShare.ReadWrite);
        using var writer=new StreamWriter(stream);
        writer.WriteLine(line);
    }
    public string[] Tail(Guid id)
    {
        string path=Path.Combine(Folder(id),"events.jsonl");
        if (!File.Exists(path)) return [];
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        bool partial=stream.Length>65536;
        if(partial)stream.Seek(-65536,SeekOrigin.End);
        using var reader=new StreamReader(stream);
        if(partial)reader.ReadLine();
        var content=reader.ReadToEnd();
        int complete=content.LastIndexOf('\n');
        return complete<0?[]:content[..complete].Split('\n',StringSplitOptions.RemoveEmptyEntries).TakeLast(40).ToArray();
    }
}
