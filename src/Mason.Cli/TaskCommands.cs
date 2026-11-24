using Mason;
using Mason.Code;

internal static class TaskCommands
{
    public static async Task<int> Run(string action, Dictionary<string,string> args, bool auto, Action<string,object> emit, CancellationToken ct)
    {
        string Get(string key) => args.GetValueOrDefault(key) ?? throw new ArgumentException("--"+key+" is required.");
        string home=args.GetValueOrDefault("task-home") ?? Environment.GetEnvironmentVariable("MASON_DATA_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MasonAgent");
        var tasks=new LocalTasks(home);
        if(action=="terminal-start"){
            var launch=new TerminalLaunch(Get("executable"),System.Text.Json.JsonSerializer.Deserialize<string[]>(Get("arguments"))??[],args.GetValueOrDefault("directory")??".");
            var id=tasks.Start(Path.Combine(AppContext.BaseDirectory,"mason.exe"),Get("workspace"),"Terminal · "+launch.Executable,"","terminal",CodePermission.Auto,terminal:launch);
            emit("terminal.started",new{id});return 0;
        }
        if(action=="task-start") {
            string exe=Path.Combine(AppContext.BaseDirectory,"mason.exe");
            string prompt=args.ContainsKey("prompt-file") ? File.ReadAllText(Get("prompt-file")) : Get("prompt");
            var id=tasks.Start(exe,Get("workspace"),prompt,LocalAiClient.ValidateEndpoint(Get("endpoint")).AbsoluteUri,Get("model"),
                auto?CodePermission.Auto:CodePermission.ReviewEdits);
            emit("task.started",new{id}); return 0;
        }
        if(action=="task-list") {emit("tasks",tasks.All(new CodeWorkspace(Get("workspace")).Root).Select(id=>new{id,status=tasks.Status(id)}));return 0;}
        Guid task=Guid.Parse(Get("id"));
        switch(action) {
            case "terminal-send":tasks.SendLine(task,Get("line"));emit("terminal.input",task);return 0;
            case "terminal-eof":tasks.SendLine(task,null);emit("terminal.eof",task);return 0;
            case "task-show": emit("task",new {task,status=tasks.Status(task),events=tasks.Tail(task)});return 0;
            case "task-cancel": tasks.Cancel(task); emit("cancel.requested",task);return 0;
            case "task-allow": case "task-deny":
                tasks.Decide(task,Guid.Parse(Get("request")),action=="task-allow"); emit("decision.recorded",task);return 0;
            case "task-worker": break;
            default: throw new ArgumentException("Unknown background task command.");
        }
        using var lease=await tasks.Claim(task,ct);
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor=tasks.WatchCancellation(task,stop);
        var state=tasks.Status(task);
        void Update(string phase,string detail,TaskDecision? decision=null) {
            state=state with {State=phase,Detail=detail,Decision=decision};
            tasks.Update(task,state);
            tasks.Event(task,phase,new{detail,request=decision?.Id});
        }
        var runtimes=new List<IDisposable>();
        try {
            var spec=tasks.Spec(task);
            if(spec.Terminal!=null){
                Update("Running","Terminal · "+spec.Terminal.Executable);
                var result=await tasks.Terminal(task,spec,stop.Token);Update(result.ExitCode==0 && !result.Truncated?"Completed":"Failed","Terminal exit "+result.ExitCode);
                return result.ExitCode==0 && !result.Truncated?0:2;
            }
            var options=spec.Models;
            tasks.ForgetKeys(spec);
            var main=options?.Main??new TaskModelConnection(spec.Endpoint,spec.Model,"",spec.ModelLifetime);
            foreach(var id in new[]{main.Lifetime,options?.Reviewer?.Lifetime,options?.Visual?.Lifetime}.OfType<Guid>().Distinct())
                runtimes.Add(OwnedModelProcess.RetainForTask(id));
            using var store=new CodeStore(tasks.Journal(task),home);
            var session=store.New(spec.Workspace); session.Permission=spec.Permission; session.Mode=spec.Permission==CodePermission.ReadOnly?"ask":"build";
            state=state with {Session=session.Id};
            Update("Running","Model "+spec.Model);
            CodeLocalModel Connect(TaskModelConnection binding)
            {
                string key="";
                if(binding.ProtectedKey.Length!=0)
                {
                    if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
                    byte[] bytes=System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(binding.ProtectedKey),null,System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    try{key=System.Text.Encoding.UTF8.GetString(bytes);}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);}
                }
                return new(new AiEndpointProfile{Endpoint=LocalAiClient.ValidateEndpoint(binding.Endpoint).AbsoluteUri,Model=binding.Model,ManagedCapability=AiCapability.Chat},key,options?.Reasoning??"default",_=>{});
            }
            var model=Connect(main);
            int emitted=0;
            void Progress() {
                while(emitted<session.Activities.Count && session.Activities[emitted].State is not ("Pending" or "Running")) {
                    var a=session.Activities[emitted++];
                    tasks.Event(task,"tool.result",new{a.Id,a.Tool,a.State,result=a.Result[..Math.Min(800,a.Result.Length)]});
                }
            }
            async Task<bool> Approve(CodeApproval approval,CancellationToken token) {
                var decision=new TaskDecision(Guid.NewGuid(),approval);
                Update("Waiting for approval",approval.Description,decision);
                bool answer=await tasks.AwaitDecision(task,decision.Id,token);
                Update("Running",answer?"Approved once":"Declined");
                return answer;
            }
            var roles=new RoleModels(model,options?.Reviewer is {} review?Connect(review):model,
                options?.Visual is {} visual?Connect(visual):model,home,
                traceDirectory:options?.Trace==true?Path.Combine(home,"model-decisions",session.Id.ToString("N")):null,
                inspectBrowserFrames:options?.Visual!=null,roleLimit:options?.RoleLimit??64);
            await new CodeEngine(store,roles,Approve,Progress).Run(session,spec.Prompt,stop.Token); Progress();
            Update(stop.IsCancellationRequested?"Stopped":session.State,session.Detail);
            return session.State=="Completed"?0:2;
        } catch(OperationCanceledException) when(stop.IsCancellationRequested) {Update("Stopped","Task cancelled.");return 2;}
        catch(Exception e) {Update("Failed",e.Message); return 1;}
        finally {foreach(var runtime in runtimes)runtime.Dispose();stop.Cancel();await monitor;}
    }
}
