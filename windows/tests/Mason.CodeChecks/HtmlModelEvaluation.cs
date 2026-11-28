using System.Text.Json;
using Mason;
using Mason.Code;

static class HtmlModelEvaluation
{
    public static async Task Resume(string destination,string requestPath)
    {
        string root=Path.GetFullPath(destination);using var store=new CodeStore(Path.Combine(root,"profile"));
        var session=store.Sessions.Single();var profile=JsonSerializer.Deserialize<AiEndpointProfile[]>(File.ReadAllText(Path.Combine(store.Root,"ai-connections.json")))!.Single();
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(15));
        await new CodeEngine(store,new CodeLocalModel(profile,"","off",Console.WriteLine),(_,_)=>throw new Exception("Unexpected review"),()=>Console.WriteLine("STATE "+session.Detail)).Run(session,File.ReadAllText(requestPath),deadline.Token);
        var syntax=await CodeCommand.Run(CodeCommand.ResolveExecutable("node.exe",session.Workspace),["--check","js/main.js"],session.Workspace,default,session.Workspace);
        bool files=new[]{"index.html","css/style.css","js/main.js","README.md"}.All(p=>File.Exists(Path.Combine(session.Workspace,p))&&new FileInfo(Path.Combine(session.Workspace,p)).Length>50);
        bool checkedSyntax=session.Activities.Any(a=>a.Tool=="run_shell"&&a.State=="Completed"&&a.Arguments.Contains("node --check js/main.js",StringComparison.Ordinal));
        bool preserved=File.ReadAllText(Path.Combine(session.Workspace,"unrelated.txt"))=="Preserve this exact user content.\n";
        File.WriteAllText(Path.Combine(root,"report-resumed.json"),JsonSerializer.Serialize(new{session.State,session.Detail,CompleteFiles=files,AgentCheckedSyntax=checkedSyntax,IndependentSyntax=syntax,UnrelatedPreserved=preserved,GuidedContinuation=true,NewTurns=session.Turns.TakeLast(12).Select(t=>new{t.Text,t.PerformanceSummary})},new JsonSerializerOptions{WriteIndented=true}));
        if(session.State!="Completed"||!files||!checkedSyntax||syntax.ExitCode!=0||!preserved)Environment.ExitCode=1;
    }
    public static async Task Run(string endpoint,string modelId,string destination,string? requestFile=null,string reasoning="off")
    {
        string root=Path.GetFullPath(destination);if(Directory.Exists(root))throw new IOException("Use a fresh evaluation directory");
        string workspace=Path.Combine(root,"space-window");Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace,"AGENTS.md"),"Build a static offline website with index.html, css/style.css and js/main.js. Use browser HTML/CSS/JavaScript and native WebGL, no external dependencies or packages. Preserve unrelated.txt. Use relative workspace directories.\n");
        File.WriteAllText(Path.Combine(workspace,"unrelated.txt"),"Preserve this exact user content.\n");
        using var store=new CodeStore(Path.Combine(root,"profile"));var session=store.New(workspace);
        var profile=new AiEndpointProfile{Name="Flint · HTML Auto evaluation",Model=modelId,Endpoint=endpoint,ManagedCapability=AiCapability.Chat};
        session.Model=profile.Id;session.Permission=CodePermission.Auto;session.Network=CodeNetworkMode.Off;session.Shell=CodeShellKind.GitBash;session.ShellExecutable=CodeShells.Find(CodeShellKind.GitBash);session.Reasoning=reasoning;session.ActionLimit=48;store.Save(session);
        File.WriteAllText(Path.Combine(store.Root,"ai-connections.json"),JsonSerializer.Serialize(new[]{profile},new JsonSerializerOptions{WriteIndented=true}));
        const string defaultRequest="""
            Build a polished responsive HTML website showing outer space viewed through an observation window. Use browser-native WebGL for an animated star field, with a pleasant CSS fallback if WebGL is unavailable. Include an accessible Pause/Resume button and a labelled star-density control that really changes the scene. Add clear status text for those controls. Work offline using index.html, css/style.css and js/main.js, without external dependencies, packages or a Node application. Preserve unrelated.txt. Inspect the workspace first, create needed directories, build complete files, and write README.md with how to open and use the page. Git Bash is selected. Validate JavaScript using `node --check js/main.js` through run_shell from directory `.` and inspect the result; this checks syntax only. Do not start a server or claim browser tests you did not perform. Finish with an honest concise summary and open index.html for review.
            """;
        var request=requestFile==null?defaultRequest:File.ReadAllText(requestFile);
        File.WriteAllText(Path.Combine(root,"request.txt"),request);
        int seen=0;string detail="";
        void Changed(){foreach(var turn in session.Turns.Skip(seen))if(turn.Role=="assistant"&&turn.Text.Length>0)Console.WriteLine("AGENT "+turn.Text);seen=session.Turns.Count;if(detail!=session.Detail){detail=session.Detail;Console.WriteLine("STATE "+detail);}}
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using(var client=new LocalAiClient()){var server=await client.ChatServer(profile,"",deadline.Token);File.WriteAllText(Path.Combine(root,"server-identity.json"),JsonSerializer.Serialize(server,new JsonSerializerOptions{WriteIndented=true}));}
        await new CodeEngine(store,new CodeLocalModel(profile,"",reasoning,b=>Console.WriteLine("BUDGET "+b)),(_,_)=>throw new Exception("Offline Auto unexpectedly requested approval"),Changed).Run(session,request,deadline.Token);
        var node=CodeCommand.ResolveExecutable("node.exe",workspace);
        var syntax=await CodeCommand.Run(node,["--check","js/main.js"],workspace,default,workspace);
        bool files=new[]{"index.html","css/style.css","js/main.js","README.md"}.All(p=>File.Exists(Path.Combine(workspace,p))&&new FileInfo(Path.Combine(workspace,p)).Length>50);
        bool agentChecked=session.Activities.Any(a=>a.Tool=="run_shell"&&a.State=="Completed"&&a.Arguments.Contains("node --check js/main.js",StringComparison.Ordinal));
        bool preserved=File.ReadAllText(Path.Combine(workspace,"unrelated.txt"))=="Preserve this exact user content.\n";
        var report=new{Model=modelId,Endpoint=endpoint,Permission=session.Permission.ToString(),Network=session.Network.ToString(),Shell=session.Shell.ToString(),session.State,session.Detail,CompleteFiles=files,AgentCheckedSyntax=agentChecked,IndependentSyntax=syntax,UnrelatedPreserved=preserved,Failures=session.Activities.Where(a=>a.State is "Failed" or "Denied"),Changes=session.Changes.Select(c=>c.Path),Performance=session.Turns.Where(t=>t.PerformanceSummary.Length>0).Select(t=>t.PerformanceSummary)};
        File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("REPORT "+Path.Combine(root,"report.json"));
        if(session.State!="Completed"||!files||!agentChecked||syntax.ExitCode!=0||!preserved)Environment.ExitCode=1;
    }
}
