using System.Text;
using System.Text.Json;
using Mason;
using Mason.Code;

internal static class ModernHarnessChecks
{
    static void Require(bool condition,string name)
    {
        if(!condition)throw new InvalidDataException(name);
        Console.WriteLine("PASS "+name);
    }
    public static async Task Run(string area)
    {
        if(Directory.Exists(area))throw new IOException("Select a fresh check directory.");
        var root=Directory.CreateDirectory(Path.Combine(area,"repo")).FullName;
        var scope=new CodeWorkspace(root);
        for(int i=0;i<1040;i++)File.WriteAllText(Path.Combine(root,$"sample-{i:D4}.cs"),"unused");
        File.WriteAllText(Path.Combine(root,"z-last.cs"),string.Concat(Enumerable.Repeat("hit needle\n",73)));
        File.WriteAllText(Path.Combine(root,".env"),"hit needle");
        using var search=new CodeRepositorySearch(scope);
        var page=search.Start("needle",".",default);
        var expired=page.Cursor!;int pages=1;
        var matches=page.Matches.ToList();
        while(page.Cursor!=null){page=search.Next(page.Cursor,default);matches.AddRange(page.Matches);pages++;}
        Require(pages>=5 && matches.Count==73 && matches.All(m=>m.Path=="z-last.cs") && matches.Last().Line==73,"Paged search reaches later files, resumes within a file and honors exclusions");
        try{search.Next(expired,default);throw new Exception("Expired cursor accepted");}catch(ArgumentException){}
        File.WriteAllText(Path.Combine(root,"long.txt"),string.Concat(Enumerable.Repeat(new string('x',160)+"\n",1500))+"last line");
        var lines=JsonSerializer.SerializeToElement(CodeRepositorySearch.Range(scope,"long.txt",1500,2));
        Require(lines.GetProperty("lines")[1].GetProperty("text").GetString()=="last line","Line reads reach beyond the ordinary file-read budget");

        var checkout=Directory.CreateDirectory(Path.Combine(area,"git")).FullName;
        Task<string> Git(params string[] args)=>CodeOwnedHelper.Run(CodeCommand.ResolveExecutable("git",checkout),args,"",checkout,default);
        await Git("init","-b","main");
        var tracked=Path.Combine(checkout,"tracked a.txt");File.WriteAllText(tracked,"base");
        await Git("add","--","tracked a.txt");
        await Git("-c","user.email=test@example.invalid","-c","user.name=Mason QA","-c","commit.gpgsign=false","-c","core.hooksPath=NUL","commit","-m","seed");
        File.WriteAllText(tracked,"staged");await Git("add","--","tracked a.txt");File.WriteAllText(tracked,"worktree");
        File.WriteAllText(Path.Combine(checkout,"new b.txt"),"untracked");
        var status=await CodeGit.FullStatus(checkout,default);
        Require(status.Branch.StartsWith("main") && status.Files.Any(f=>f.Path=="tracked a.txt" && f.Status=="MM") &&
            status.Files.Any(f=>f.Path=="new b.txt" && f.Status=="??") && File.ReadAllText(tracked)=="worktree","Git reports both index and working-tree changes plus untracked paths");

        var work=Directory.CreateDirectory(Path.Combine(area,"edit")).FullName;
        var edits=new CodeWorkspace(work);
        using var store=new CodeStore(Path.Combine(area,"profile"));
        var session=store.New(work);
        var first=new CodeTurn{Text="First edit"};session.Turns.Add(first);
        File.WriteAllText(Path.Combine(work,"a.txt"),"before",new UTF8Encoding(true));
        void Change(CodeTurn turn,string path,string before,string after)=>
            edits.ApplyEdits(path,edits.Read(path).Sha256,[new(before,after)],(_,_)=>{},(old,value)=>CodeRewind.Record(turn,path,old,value));
        Change(first,"a.txt","before","earlier");
        var second=new CodeTurn{Text="Second edit"};session.Turns.Add(second);
        Change(second,"a.txt","earlier","later");
        edits.Create("b.txt","created",()=>CodeRewind.Record(second,"b.txt",null,"created"));
        edits.Create("untouched.txt","outside turn");
        File.WriteAllText(Path.Combine(work,"b.txt"),"manual");
        try{CodeRewind.Apply(store,session,second.Id);throw new Exception("Conflict accepted");}catch(IOException){}
        Require(edits.Read("a.txt").Content=="later" && edits.Read("b.txt").Content=="manual" && !second.Rewound,"Conflicting sibling prevents every rewind mutation");
        File.WriteAllText(Path.Combine(work,"b.txt"),"created");
        CodeRewind.Apply(store,session,second.Id);
        Require(edits.Read("a.txt").Content=="earlier" && edits.Read("a.txt").Bom && !File.Exists(Path.Combine(work,"b.txt")) &&
            edits.Read("untouched.txt").Content=="outside turn" && CodeRewind.Latest(session)==first,"Rewind restores exact prior turn, preserves BOM and removes only the new file");
        first.RewindPending=true;
        File.WriteAllText(Path.Combine(work,"a.txt"),"before",new UTF8Encoding(true));
        CodeRewind.Apply(store,session,first.Id);
        Require(first.Rewound && !first.RewindPending && edits.Read("a.txt").Content=="before","Interrupted rewind resumes from an already restored file");

        var chat=store.New(work);chat.Mode="ask";
        var model=new Steered();
        var engine=new CodeEngine(store,model,(_,_)=>Task.FromResult(false),()=>{});
        model.Interrupt=()=>{engine.SendWhileRunning(chat,"later question",false);engine.SendWhileRunning(chat,"change direction",true);};
        await engine.Run(chat,"original question",default);
        Require(chat.State=="Completed" && chat.PendingInput.Count==0 && chat.Turns.Where(t=>t.Role=="user").Select(t=>t.Text)
            .SequenceEqual(["original question","change direction","later question"]) && !chat.Activities.Any(a=>a.Tool=="create_file"),"Steering drops a superseded model action and drains follow-ups in order");
        chat.PendingInput.Add(new(Guid.NewGuid(),"saved follow-up",false));store.Save(chat);
        await engine.Run(chat,"/queue",default);
        Require(chat.PendingInput.Count==0 && chat.Turns.Any(t=>t.Text=="saved follow-up"),"Explicit resume consumes a saved follow-up");

        await ReviewWorkspaceChecks.Run(Path.Combine(area, "review"), Require);
        var fixture=edits.Create("shift.js","// policy\nconst LIMIT = 17;\n");
        foreach(var finding in new[]{new WorkerFinding("17","untouched.txt",2,"17"),new WorkerFinding("17","shift.js",1,"17")}){
            try{await CodeInvestigation.Run(edits,new Worker(()=>[finding]),"What is the limit?",[fixture.Path],default);throw new Exception("Invalid citation accepted");}
            catch(InvalidDataException){}
        }
        var cite=new WorkerFinding("17","shift.js",2,"LIMIT = 17");
        try{
            await CodeInvestigation.Run(edits,new Worker(()=>{edits.Edit(fixture.Path,fixture.Sha256,"17","18",(_,_)=>{});return [cite];}),"What is the limit?",[fixture.Path],default);
            throw new Exception("Stale citation accepted");
        }catch(IOException){}
        Require(edits.Read(fixture.Path).Content.Contains("18"),"Delegated findings reject changed sources and out-of-scope or incorrect citations");
    }
    sealed class Steered:ICodeModel
    {
        public Action? Interrupt;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> onMessage,CancellationToken ct){
            if(Interrupt is {} steer){Interrupt=null;steer();return Task.FromResult(new CodeAction("obsolete","create_file",JsonSerializer.SerializeToElement(new{path="obsolete",content="outdated"})));}
            return Task.FromResult(new CodeAction("Answered.",null,default));
        }
    }
    sealed class Worker(Func<WorkerFinding[]> result):ICodeInvestigator
    {
        public Task<WorkerFinding[]> Investigate(string question,CodeFile[] files,CancellationToken ct)=>Task.FromResult(result());
    }
    sealed class DispatchWorker(ICodeInvestigator worker,string question,string path):ICodeModel,ICodeInvestigator
    {
        int step;
        public bool ReceivedFinding;
        public Task<WorkerFinding[]> Investigate(string assignment,CodeFile[] files,CancellationToken ct)=>worker.Investigate(assignment,files,ct);
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> stream,CancellationToken ct)
        {
            if(step++==0)return Task.FromResult(new CodeAction("Enable code tools","enable_tools",JsonSerializer.SerializeToElement(new{group="code"})));
            if(step==2)return Task.FromResult(new CodeAction("Read the assigned source","delegate_readonly",JsonSerializer.SerializeToElement(new{question,paths=new[]{path}})));
            ReceivedFinding=messages.Any(m=>m.Content.Contains("SHIFT_LIMIT") && m.Content.Contains("Answer"));
            return Task.FromResult(new CodeAction("Investigation received.",null,default));
        }
    }
    public static async Task LiveWorker(string endpoint,string id,string area)
    {
        if(Directory.Exists(area))throw new IOException("Select a fresh worker directory.");
        var root=Directory.CreateDirectory(Path.Combine(area,"source")).FullName;var scope=new CodeWorkspace(root);
        var limit=Random.Shared.Next(31,98);
        var file=scope.Create("policy.js",$"// Loading dock shift policy\nexport const SHIFT_LIMIT = {limit};\n");
        var model=new CodeLocalModel(new AiEndpointProfile{Endpoint=endpoint,Model=id,ManagedCapability=AiCapability.Chat},"","default",_=>{});
        int calls=0;model.InferenceCompleted+=r=>{if(r.CompletionTokens>0)calls++;};
        const string question="What numeric shift limit does policy.js configure? Cite its declaration.";
        var dispatcher=new DispatchWorker(model,question,file.Path);
        using var store=new CodeStore(Path.Combine(area,"profile"));
        var session=store.New(root);session.Mode="ask";
        await new CodeEngine(store,dispatcher,(_,_)=>Task.FromResult(false),()=>{}).Run(session,question,default);
        var job=session.Activities.Single(a=>a.Tool=="delegate_readonly");
        Require(job.State=="Completed" && dispatcher.ReceivedFinding && session.State=="Completed","Parent receives the delegated result through the normal tool loop");
        var findings=JsonSerializer.Deserialize<WorkerFinding[]>(job.Result)!;
        Require(calls>0 && findings.Any(f=>f.Path==file.Path && f.Line==2 && f.Quote.Contains(limit.ToString()) && f.Answer.Contains(limit.ToString())),"Live delegated investigator returns the assigned value at its exact source location");
        File.WriteAllText(Path.Combine(area,"worker.json"),JsonSerializer.Serialize(new{Model=id,Calls=calls,Limit=limit,SourceHash=file.Sha256,dispatcher.ReceivedFinding,Findings=findings}));
    }
}
