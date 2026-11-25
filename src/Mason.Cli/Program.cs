using System.Text.Json;
using Mason;
using Mason.Code;

static void Event(string type,object data)=>Console.WriteLine(JsonSerializer.Serialize(new{type,data}));
using var stop=new CancellationTokenSource();
if(!OperatingSystem.IsWindows()){Event("error",new{message="Mason requires Windows."});return 1;}
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
try{
    // No arguments in a terminal, "chat", or options alone open the interactive session. Scripts keep the JSON Lines operations below.
    bool help=args.Length>0&&args[0] is "--help" or "-h";
    if(!help&&(args.Length==0?!Console.IsInputRedirected:args[0]=="chat"||args[0].StartsWith("--",StringComparison.Ordinal))){
        try{return await Interactive.Start(args.Length>0&&args[0]=="chat"?args[1..]:args);}
        catch(Exception e) when(e is ArgumentException or DirectoryNotFoundException or UnauthorizedAccessException or InvalidDataException){Console.Error.WriteLine(e.Message);return 1;}
    }
    if(args.Length==0||help){
        Console.WriteLine(Interactive.Usage);
        Console.WriteLine("""
            mason exec --workspace PATH --endpoint LOOPBACK --model ID --prompt TEXT [--auto] [--profile PATH]
            mason review --workspace PATH --endpoint LOOPBACK --model ID --prompt TEXT [--page index.html]
            mason review --workspace PATH --endpoint LOOPBACK --model ID --prompt TEXT --path SOURCE_FILE
            mason browse --workspace PATH --endpoint LOOPBACK --model ID --prompt TEXT [--page index.html]
            mason serve --workspace PATH --workflow NAME --port PORT --endpoint LOOPBACK --model ID --prompt TEXT [--page index.html]
            serve explicitly launches the named development workflow under your Windows account and stops it after browser interaction.
            mason symbol --workspace PATH --path FILE --line N --column N --mode definitions|references|diagnostics
            mason worktree --workspace PATH --destination NEW_PATH --branch NEW_BRANCH
            mason map --workspace PATH --query SYMBOL
            mason task-start --workspace PATH --endpoint LOOPBACK --model ID --prompt TEXT [--auto]
            mason compare-decision --trace FILE --endpoint LOOPBACK --model ID
            exec accepts --review-model ID --review-endpoint LOOPBACK --visual-model ID --visual-endpoint LOOPBACK and --trace-dir LOCAL_FOLDER.
            mason terminal-start --workspace PATH --executable PROGRAM --arguments JSON_ARRAY [--directory RELATIVE_PATH]
            mason terminal-send --id TASK_ID --line TEXT
            mason terminal-eof --id TASK_ID
            mason task-list --workspace PATH
            mason task-show|task-cancel --id TASK_ID
            mason task-allow|task-deny --id TASK_ID --request APPROVAL_ID
            mason git-status --workspace PATH
            Use --prompt-file PATH to read a UTF-8 task. Output is JSON Lines. Exit 0 means completed.
            Each invocation uses a separate journal by default. Use --profile PATH --resume SESSION_ID to continue.
            """);return 0;
    }
    var values=new Dictionary<string,string>(StringComparer.Ordinal);bool auto=false;
    for(int i=1;i<args.Length;i++){
        if(args[i]=="--auto"){auto=true;continue;}
        if(!args[i].StartsWith("--")||i+1>=args.Length||!values.TryAdd(args[i][2..],args[++i]))throw new ArgumentException("Expected unique --option value pairs.");
    }
    string Get(string name)=>values.TryGetValue(name,out var value)?value:throw new ArgumentException("--"+name+" is required.");
    if(args[0]=="compare-decision"){Event("decision.comparison",await ModelTrace.Predict(Get("trace"),new AiEndpointProfile{Endpoint=Get("endpoint"),Model=Get("model")},"",stop.Token));return 0;}
    if(args[0].StartsWith("task-",StringComparison.Ordinal) || args[0].StartsWith("terminal-",StringComparison.Ordinal))return await TaskCommands.Run(args[0],values,auto,Event,stop.Token);
    var scope=new CodeWorkspace(Get("workspace"));var ct=stop.Token;
    if(args[0]=="worktree"){
        var path=await CodeWorktree.Create(scope.Root,Get("destination"),Get("branch"),ct);Event("worktree.created",new{path});return 0;
    }
    if(args[0]=="map"){ Event("map.result",await new CodeRepositoryMap(scope).Query(Get("query"),32,ct)); return 0; }
    if(args[0]=="git-status"){Event("git.status",await CodeGit.FullStatus(scope.Root,ct));return 0;}
    if(args[0]=="symbol"){
        var result=await CodeSemantic.Query(scope,Get("path"),int.Parse(Get("line")),int.Parse(Get("column")),Get("mode"),ct);
        Event("symbol.result",JsonSerializer.Deserialize<JsonElement>(result));return 0;
    }
    if(args[0] is not ("exec" or "review" or "browse" or "serve"))throw new ArgumentException("Unknown operation.");
    var prompt=values.ContainsKey("prompt-file")?File.ReadAllText(Get("prompt-file")):Get("prompt");
    string profile=values.GetValueOrDefault("profile")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MasonAgent","cli-runs",Guid.NewGuid().ToString("N"));
    if(CodeSandbox.ContainsHost(scope.Root,Path.GetFullPath(profile)))throw new ArgumentException("Keep the journal outside the project.");
    var model=new CodeLocalModel(new AiEndpointProfile{Endpoint=Get("endpoint"),Model=Get("model"),ManagedCapability=AiCapability.Chat},"","default",_=>{});
    if(args[0] is "browse" or "serve"){
        await using var live=args[0]=="serve"?await CodePreviewServer.Start(scope,Get("workflow"),int.Parse(Get("port")),ct):null;
        if(live!=null)Event("server.ready",new{workflow=Get("workflow"),port=int.Parse(Get("port"))});
        Event("browser.result",JsonSerializer.Deserialize<JsonElement>(await CodeComputerUse.Run(scope,values.GetValueOrDefault("page","index.html"),prompt,model,Path.Combine(profile,"browser"),ct,live)));
        if(live!=null)Event("server.output",new{output=live.Output});
        return 0;
    }
    if(args[0]=="review"){
        if(values.TryGetValue("path",out var sourcePath)){
            var file=scope.Read(sourcePath);
            var findings=await CodeChangeReview.Review(model,prompt,[new(file.Path,null,file.Content,file.Sha256)],ct);
            if(scope.Read(file.Path).Sha256!=file.Sha256)throw new InvalidDataException("File changed during independent review.");
            Event("review.result",new{file.Path,file.Sha256,findings});return findings.Length==0?0:2;
        }
        var page=values.GetValueOrDefault("page","index.html");
        var checks=await CodeComputerUse.Review(scope,page,prompt,model,ct);
        var plan=new CodeAcceptancePlan{Request=prompt,Units=[prompt],Checks=checks.Select(s=>new AcceptanceCheck(1,s.Requirement,"browser_flow",page,s.Id,"Independent review"){Steps=s.Steps}).ToList()};
        await CodeAcceptance.Verify(plan,scope,CodePermission.Auto,ct);
        Event("review.result",new{checks,plan.Results,plan.VerifiedRevision});return plan.Results.All(r=>r.Passed)?0:2;
    }
    var host=Environment.GetEnvironmentVariable("MASON_DATA_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MasonAgent");
    using var store=new CodeStore(profile,host);
    var session=values.TryGetValue("resume",out var id)?store.Sessions.Single(s=>s.Id==Guid.Parse(id)):store.New(scope.Root);
    if(!scope.Root.Equals(session.Workspace,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Resumed workspace mismatch.");
    session.Mode=auto?"build":"ask";session.Permission=auto?CodePermission.Auto:CodePermission.ReadOnly;
    Event("run.started",new{session.Id,workspace=scope.Root,profile,model=Get("model"),session.Mode});int emitted=0;
    void Progress(){
        while(emitted<session.Activities.Count && session.Activities[emitted].State is not ("Pending" or "Running")){
            var a=session.Activities[emitted++];Event("tool.result",new{a.Id,a.Tool,a.State,a.Result});
        }
    }
    CodeLocalModel Role(string role)=>values.TryGetValue(role+"-model",out var id)?new CodeLocalModel(new AiEndpointProfile{Endpoint=values.GetValueOrDefault(role+"-endpoint")??Get("endpoint"),Model=id,ManagedCapability=AiCapability.Chat},"","default",_=>{}):model;
    var trace=values.GetValueOrDefault("trace-dir");
    if(trace!=null && CodeSandbox.ContainsHost(scope.Root,Path.GetFullPath(trace)))throw new ArgumentException("Keep decision traces outside the project.");
    var roles=new RoleModels(model,Role("review"),Role("visual"),host,traceDirectory:trace,inspectBrowserFrames:values.ContainsKey("visual-model"));
    var engine=new CodeEngine(store,roles,(approval,_)=>{Event("approval.required",approval);return Task.FromResult(false);},Progress);
    await engine.Run(session,prompt,ct);Progress();
    Event("run.finished",new{session.Id,session.State,session.Detail,answer=session.Turns.LastOrDefault(t=>t.Role=="assistant")?.Text});
    return session.State=="Completed"?0:2;
}catch(Exception e){Event("error",new{message=e.Message});return 1;}
