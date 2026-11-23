using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mason.Code;

internal sealed record ProjectWorkflow(string Executable,string[] Arguments,string Directory=".",bool BeforeVerify=false);
internal sealed record ProjectWorkflows(int Version,Dictionary<string,ProjectWorkflow> Workflows)
{
    internal static ProjectWorkflows Load(CodeWorkspace scope)
    {
        const string path="mason.workflows.json";
        if(!File.Exists(scope.Resolve(path)))return new(1,[]);
        var options=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
        var config=JsonSerializer.Deserialize<ProjectWorkflows>(scope.Read(path).Content,options);
        if(config is not {Version:1,Workflows.Count:<=24})throw new InvalidDataException("Use workflow manifest version 1 with at most 24 workflows.");
        foreach(var (name,flow) in config.Workflows){
            if(!Regex.IsMatch(name,@"\A[a-zA-Z][\w-]{0,49}\z")||flow is null||string.IsNullOrWhiteSpace(flow.Executable)||flow.Arguments is not {Length:<=40}||flow.Arguments.Any(a=>a==null||a.Length>4000))
                throw new InvalidDataException("Invalid named project workflow.");
            scope.Resolve(flow.Directory,true);
        }
        return config;
    }
}

internal sealed class CodeRunServices(CodeWorkspace scope,CancellationToken lifetime,Dictionary<string,string> unverified):IAsyncDisposable
{
    sealed class Job(CancellationTokenSource stop,string key)
    {
        public CancellationTokenSource Stop {get;}=stop;
        public Task<CodeCommandResult> Task {get;set;}=null!;
        public Func<string>? Tail;
        public bool Collected;
        public string Key {get;}=key;
    }
    readonly Dictionary<string,Job> jobs=[];
    readonly Lazy<ProjectWorkflows> manifest=new(()=>ProjectWorkflows.Load(scope));
    public ProjectWorkflows Workflows=>manifest.Value;
    public string Start(string executable,string[] arguments,string directory)
    {
        if(jobs.Count>=24||jobs.Values.Count(j=>!j.Task.IsCompleted)>=3)throw new InvalidOperationException("Background job limit reached.");
        var resolved=CodeCommand.ResolveExecutable(executable,scope.Root);
        var working=scope.Resolve(directory,true);
        if(arguments.Length>40||arguments.Any(a=>a.Length>4000))throw new ArgumentException("Invalid job arguments.");
        var key=JsonSerializer.Serialize(new{executable=resolved,arguments,directory=working});
        if(jobs.Values.Any(j=>j.Key==key&&!j.Collected))throw new InvalidOperationException("Collect the existing job before starting the same command.");
        var job=new Job(CancellationTokenSource.CreateLinkedTokenSource(lifetime),key);
        unverified.TryAdd(key,"Outcome pending");
        string id=Guid.NewGuid().ToString("N");
        job.Task=CodeCommand.Run(resolved,arguments,working,job.Stop.Token,scope.Root,timeout:TimeSpan.FromMinutes(30),observeLog:tail=>Volatile.Write(ref job.Tail,tail));
        jobs.Add(id,job);
        return id;
    }
    public async Task<CodeToolResult> Observe(string id,bool stop,int waitMs,CancellationToken ct)
    {
        if(!jobs.TryGetValue(id,out var job))throw new ArgumentException("Unknown job for this run.");
        if(waitMs is <0 or >10000)throw new ArgumentException("Wait must be between 0 and 10000 ms.");
        if(stop)job.Stop.Cancel();
        if(!stop && !job.Task.IsCompleted && waitMs>0)await Task.WhenAny(job.Task,Task.Delay(waitMs,ct));
        ct.ThrowIfCancellationRequested();
        if(!stop&&!job.Task.IsCompleted)return new(JsonSerializer.Serialize(new{id,state="Running",output=Volatile.Read(ref job.Tail)?.Invoke()??""}));
        job.Collected=true;
        try{
            var result=await job.Task;
            if(result.ExitCode==0&&!result.Truncated)unverified.Remove(job.Key);
            else unverified[job.Key]="Command failed";
            return new(JsonSerializer.Serialize(new{id,state="Exited",result.ExitCode,result.Output,result.Truncated}),Failed:result.ExitCode!=0||result.Truncated);
        }catch(OperationCanceledException)when(stop&&!ct.IsCancellationRequested){if(unverified.GetValueOrDefault(job.Key)=="Outcome pending")unverified.Remove(job.Key);return new(JsonSerializer.Serialize(new{id,state="Stopped"}));}
    }
    public bool NeedsCollection=>jobs.Values.Any(j=>!j.Collected);
    public HashSet<string> Enabled {get;}=new(StringComparer.Ordinal);
    readonly Lazy<string[]> skills=new(()=>Directory.Exists(scope.Resolve(".mason/skills"))?
        scope.Files(".mason/skills",200).Files.Where(p=>Regex.IsMatch(p,@"^\.mason/skills/[\w-]+/SKILL\.md$")).Select(p=>p.Split('/')[2]).ToArray():[]);
    public string[] Skills=>skills.Value;
    public void Enable(string group,CodeSession session)
    {
        bool auto=session.Mode=="build" && session.Permission==CodePermission.Auto;
        string[] tools=group switch{
            "code"=>["inspect_symbol"],
            "workflows"=>auto?["read_skill","run_workflow"]:["read_skill"],
            "jobs" when auto=>["start_job","poll_job","stop_job"],
            "browser" when auto=>["computer_use"],
            "mcp" when auto=>["mcp_tools","mcp_call"],
            _=>throw new UnauthorizedAccessException("Capability is unavailable in this session mode.")
        };
        Enabled.UnionWith(tools);
    }
    public Task<CodeCommandResult> Workflow(string name,CancellationToken ct)
    {
        if(!Workflows.Workflows.TryGetValue(name,out var flow))throw new ArgumentException("Unknown project workflow.");
        return CodeCommand.Run(CodeCommand.ResolveExecutable(flow.Executable,scope.Root),flow.Arguments,scope.Resolve(flow.Directory,true),ct,scope.Root);
    }
    public async ValueTask DisposeAsync()
    {
        foreach(var job in jobs.Values)job.Stop.Cancel();
        foreach(var job in jobs.Values){try{await job.Task;}catch(Exception){/* The foreground task records explicit failures. */}finally{job.Stop.Dispose();}}
    }
}
