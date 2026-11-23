using System.Diagnostics;
using System.Text.Json;
using Mason.Code;

internal static class HarnessExtensionChecks
{
    internal static async Task Run(string root,Action<bool,string> check)
    {
        var test=Directory.CreateDirectory(Path.Combine(root,"extended-harness")).FullName;
        File.WriteAllText(Path.Combine(test,"price.ts"),"export function price(q: number){return q*10}");
        File.WriteAllText(Path.Combine(test,"cart.ts"),"import {price} from './price';\nprice('x');");
        File.WriteAllText(Path.Combine(test,"cost.py"),"def price(q):\n    return q * 10\n");
        File.WriteAllText(Path.Combine(test,"cart.py"),"from cost import price\nprice(2)\n");
        var scope=new CodeWorkspace(test);
        foreach(var (path,target) in new[]{("cart.ts","price.ts"),("cart.py","cost.py")}){
            var result=JsonDocument.Parse(await CodeSemantic.Query(scope,path,2,2,"definitions",default));
            check(result.RootElement.GetProperty("results")[0].GetProperty("file").GetString()==target,"Semantic definition crosses imports for "+path);
        }
        var diagnostics=await CodeSemantic.Query(scope,"cart.ts",2,2,"diagnostics",default);
        check(diagnostics.Contains("2345"),"TypeScript returns the observed type error");
        var skill=Directory.CreateDirectory(Path.Combine(test,".mason","skills","checkout")).FullName;
        File.WriteAllText(Path.Combine(skill,"SKILL.md"),"Verify the checkout");
        var cmd=CodeCommand.ResolveExecutable("cmd",test);
        var manifest=new{version=1,workflows=new{tests=new{executable=cmd,arguments=new[]{"/d","/c","echo workflow-ok"},directory=".",beforeVerify=true}}};
        File.WriteAllText(Path.Combine(test,"mason.workflows.json"),JsonSerializer.Serialize(manifest));
        var unverified=new Dictionary<string,string>();
        await using var services=new CodeRunServices(scope,default,unverified);
        check(services.Skills.Contains("checkout")&&services.Workflows.Workflows["tests"].BeforeVerify,"Project skill and pinned workflow hook discovered");
        var run=await services.Workflow("tests",default);
        check(run.ExitCode==0&&run.Output.Contains("workflow-ok"),"Named workflow runs inside Auto");
        var job=services.Start(cmd,["/d","/c","echo job-ok"],".");
        CodeToolResult resultJob;
        do{resultJob=await services.Observe(job,false,10000,default);}while(services.NeedsCollection);
        check(!resultJob.Failed&&resultJob.Content.Contains("job-ok")&&unverified.Count==0,"Background job collects sandboxed command result");
        var failed=services.Start(cmd,["/d","/c","exit /b 7"],".");
        do{resultJob=await services.Observe(failed,false,10000,default);}while(services.NeedsCollection);
        check(resultJob.Failed&&unverified.Count==1,"Failed background job remains an outstanding verification");
        var repo=Directory.CreateDirectory(Path.Combine(root,"extension-git")).FullName;
        var git=CodeCommand.ResolveExecutable("git",repo);
        async Task Git(params string[] args)=>await CodeOwnedHelper.Run(git,args,"",repo,default);
        await Git("init","-q");File.WriteAllText(Path.Combine(repo,"a.txt"),"base");
        await Git("add","a.txt");await Git("-c","user.name=Mason test","-c","user.email=test@localhost","-c","commit.gpgsign=false","-c","core.hooksPath=NUL","commit","-qm","test base");
        var tree=await CodeWorktree.Create(repo,Path.Combine(root,"extension-worktree"),"mason/experiment",default);
        File.WriteAllText(Path.Combine(tree,"a.txt"),"isolated");
        check(File.ReadAllText(Path.Combine(repo,"a.txt"))=="base","A Git worktree edit is isolated from its original checkout");
        await McpIntegration.Run(check);
        var shop=Directory.CreateDirectory(Path.Combine(root,"reviewed-shop")).FullName;
        File.WriteAllText(Path.Combine(shop,"index.html"),"<h1>Pantry</h1><p id='item'>Rice $4.00</p>");
        using var store=new CodeStore(Path.Combine(root,"review-profile"));
        var session=store.New(shop);
        session.Permission=CodePermission.Auto;
        await new CodeEngine(store,new ShelfReviewer(),(_,_)=>throw new Exception("Unexpected approval"),()=>{})
            .Run(session,"Show a heading \"Pantry\".",default);
        check(session.State=="Completed" && session.Turns[0].Acceptance?.Results.Any(r=>r.Check.Target=="label-price" && r.Passed)==true,
            "Auto completion accepts an independently observed shelf price");
    }

    sealed class ShelfReviewer : ICodeModel, ICodeBrowserOperator
    {
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> notify,CancellationToken ct)=>throw new Exception("Existing page should be verified directly");
        public Task<ComputerAction> ChooseAction(string task,ComputerObservation page,IReadOnlyList<string> history,CancellationToken ct)=>throw new NotSupportedException();
        public Task<BrowserScenario[]> DesignReview(string task,string path,ComputerObservation page,CancellationToken ct)=>
            Task.FromResult<BrowserScenario[]>([new("label-price","Check the displayed price",path,[new("shelf_price","#item","4.00")])]);
    }
}
