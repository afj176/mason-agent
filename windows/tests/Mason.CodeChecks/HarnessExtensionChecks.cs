using System.Diagnostics;
using System.Text.Json;
using Mason;
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
        var file=new CodeFile("index.html","<style>strong{color:red}</style><button>Count</button><script>old();</script>","observed-hash",false);
        var repair=new CodePageRepair(file,"Implement the counter","The count is incorrect.",""){RebuildGeneratedPage=true,PreservePassingScenarios=true};
        var script="document.querySelector('button').onclick=()=>document.title='2';";
        var local=new CodeLocalModel(new AiEndpointProfile{Model="test-coder",Endpoint="http://127.0.0.1:5179"},"","default",_=>{},()=>new LocalAiClient(new ModelHandler(script)));
        var action=await local.Next([new("system",""){AvailableTools=new HashSet<string>{"replace_file"},PageRepair=repair}],_=>{},default);
        check(action.Tool=="replace_file" && action.Arguments.GetProperty("expected_sha256").GetString()==file.Sha256 &&
            action.Arguments.GetProperty("content").GetString()==file.Content.Replace("old();","\n"+script+"\n"),
            "A fresh behavior repair preserves the generated shell and pins the observed source hash");
        var replacement=JsonSerializer.Serialize(new{Before="old();",After="update();"});
        var patcher=new CodeLocalModel(new AiEndpointProfile{Model="test-coder",Endpoint="http://127.0.0.1:5179"},"","default",_=>{},()=>new LocalAiClient(new ModelHandler(replacement)));
        var patched=await patcher.Next([new("system",""){AvailableTools=new HashSet<string>{"replace_file"},PageRepair=repair with{PreviousEditUnchanged=true}}],_=>{},default);
        check(patched.Arguments.GetProperty("content").GetString()==file.Content.Replace("old();","update();") &&
            patched.Arguments.GetProperty("expected_sha256").GetString()==file.Sha256 &&
            CodeLocalModel.ApplyExactRepair("x x","x","y")==null && CodeLocalModel.ApplyExactRepair("x","x","x")==null &&
            CodeLocalModel.ApplyExactRepair("one\r\ntwo","one\ntwo","one\nthree")=="one\r\nthree",
            "An unchanged edit switches to a unique, revision-pinned source replacement");
        var pricing=new CodeFile("price.js","function cost(n, unit) {\r\n  const amount = n * n * unit;\r\n  return amount;\r\n}","priced-hash",false);
        var calculator=new CodeLocalModel(new AiEndpointProfile{Model="test-coder",Endpoint="http://127.0.0.1:5179"},"","default",_=>{},
            ()=>new LocalAiClient(new ModelHandler(JsonSerializer.Serialize(new{Target="amount",Expression="n * unit"}))));
        var corrected=await calculator.Next([new("system",""){AvailableTools=new HashSet<string>{"replace_file"},
            PageRepair=new(pricing,"Multiply units by the unit price.","",""){NumericMismatch="expected 20, observed 40"}}],_=>{},default);
        check(corrected.Arguments.GetProperty("content").GetString()==pricing.Content.Replace("n * n * unit","n * unit") &&
            corrected.Arguments.GetProperty("expected_sha256").GetString()==pricing.Sha256,"A numeric counterexample changes only the selected expression");
        const string brief="Build a store. Rice costs $4 each, three or more cost 3 dollars each. Add Oats for $2. Show the cart total.";
        var catalog=await CodeStorefrontPlan.Prepare(brief,new PantryPlanner(),default);
        check(catalog!=null && catalog.Checks.Where(c=>c.Kind=="unverified").Select(c=>c.Feature).SequenceEqual(["Email the receipt","Display the cart total in euros"]) &&
            catalog.BuildFacts.Contains("q * (q >= 3 ? 3 : 4)"),"Covered catalog paraphrases retain real feature checks and unknown requirements remain outstanding");
        check(CatalogSpec.ExplicitReceiptPrefix("On checkout issue a new BOX_ receipt.")=="BOX_" &&
            CatalogSpec.ExplicitReceiptPrefix("Give each receipt a value starting with 'INV-'.")=="INV-" &&
            CatalogSpec.ExplicitReceiptPrefix("Create an offline food-store receipt.")==null,
            "Receipt IDs use the literal prefix supplied by the requester");
        var prices=CatalogPriceGrounding.Bind("Rice costs $4 each. Tea is $7 each. Charge $3 per Rice bag when buying two or more Rice bags.",
            [new("Rice",4,3,2),new("Tea",4,3,2)]);
        check(prices.SequenceEqual([new CatalogItem("Rice",4,3,2),new CatalogItem("Tea",7,7,0)]),
            "A reviewer cannot copy a named product's quantity promotion onto other shelf items");
        const string lookup="units * (offer.tiers[units] || offer.base)";
        var fixedTier=CatalogPriceGrounding.CorrectExactQuantityLookup(lookup,"const items = [{name:'Rice'}];",
            "Rice costs $4 each. Charge $3 per Rice bag when buying two or more Rice bags.");
        check(fixedTier?.Contains("units >= 2 ? 3 : offer.base")==true &&
            CatalogPriceGrounding.CorrectExactQuantityLookup(lookup,"const items = [{name:'Rice'}];","List the prices.")==null,
            "Repair an exact-quantity table only when the original request establishes the named open-ended tier");
        await CheckCatalogComponent(root,check);
        var reply=JsonSerializer.Serialize(new {Items=new[]{new{Name="Rice",UnitPrice=4,DiscountedPrice=3,Threshold=3,Button=1},
            new{Name="Oats",UnitPrice=2,DiscountedPrice=2,Threshold=0,Button=2}},Total="#amount-due div"});
        var reviewer=new CodeLocalModel(new AiEndpointProfile{Model="test-coder",Endpoint="http://127.0.0.1:5179"},"","default",_=>{},()=>new LocalAiClient(new ModelHandler(reply)));
        var scenarios=await reviewer.DesignReview(brief,"index.html",new("","",["#rice | Add","#oats | Add"]){ElementIds=["#amount-due"]},default);
        check(scenarios.Single().Steps.Where(s=>s.Op=="money").All(s=>s.Target=="#amount-due"),
            "Independent prices use the observed total element even if a model appends its HTML tag");
    }

    static async Task CheckCatalogComponent(string root,Action<bool,string> check)
    {
        var spec=new CatalogSpec([new("Muesli",2.45m,1.75m,2),new("Tea",1.20m,1.20m,0)],"BOX_",true,true,[]);
        var source=CodeLocalModel.RenderCatalog(spec,new("Pantry <special>","Choose a snack.","#123456","#fffef0",["Oats","Leaves"]));
        var directory=Directory.CreateDirectory(Path.Combine(root,"decimal-store")).FullName;
        var page=Path.Combine(directory,"index.html");
        File.WriteAllText(page,source);
        var flow=new AcceptanceCheck(1,"Fractional prices and repeat checkout","browser_flow","index.html","orders","Requested behavior"){Steps=[
            new("click","#product-1 button"),new("money","#cart-total","2.45"),new("click","#product-1 button"),new("money","#cart-total","3.50"),
            new("click","#product-1 button"),new("click","#product-2 button"),new("money","#cart-total","6.45"),
            new("click","#checkout"),new("capture","#receipt","BOX_"),new("reload"),new("same","#receipt"),
            new("click","#product-2 button"),new("click","#checkout"),new("new_id","#receipt","BOX_"),new("reload"),new("same","#receipt")]};
        var scope=new CodeWorkspace(directory);
        check((await CodeBrowserVerification.Run(scope,[flow],default))[flow].Passed,"Edge verifies fractional pricing, a different threshold, mixed cart and fresh receipts");
        var invalid=new CodeBrowserVerification.Candidate("index.html",System.Text.Encoding.UTF8.GetBytes(source.Replace("let total = 0;","let total = ;")));
        check(!(await CodeBrowserVerification.Run(scope,[flow],default,invalid))[flow].Passed && File.ReadAllText(page)==source,
            "Edge evaluates a defective candidate in memory and leaves the passing workspace revision intact");
        File.WriteAllText(page,source.Replace("const subtotal = quantity * unit;",
            "const subtotal = quantity * (quantity === 2 ? quantity * unit : unit);"));
        var mispriced=(await CodeBrowserVerification.Run(scope,[flow],default))[flow];
        check(!mispriced.Passed && mispriced.Detail.Contains("3.50") && mispriced.Detail.Contains("7.00"),
            "Edge rejects a line total used as a unit price at quantity two");
        File.WriteAllText(page,source.Replace("localStorage.setItem(receiptKey, id);","localStorage.setItem(receiptKey, prefix + 'fixed');")
            .Replace("receipt.textContent = id;","receipt.textContent = prefix + 'fixed';"));
        var failed=(await CodeBrowserVerification.Run(scope,[flow],default))[flow];
        check(!failed.Passed && failed.Detail.Contains("new_id"),"Edge rejects a reused receipt even when the first checkout persisted");
    }

    sealed class PantryPlanner : ICodeCatalogPlanner
    {
        public Task<CatalogSpec> ExtractCatalog(string request,CancellationToken ct)=>Task.FromResult(new CatalogSpec(
            [new("Rice",4,3,3),new("Oats",2,2,0)],"ORD-",true,true,
            ["A whimsical food store design for bears","Shoppers can add each food to the cart and see the cart total",
             "Other foods keep their own shelf prices","The cart total is the sum of the three food line totals",
             "Show the same receipt after the page is reloaded","Ability to add each food to the cart","Display the cart total",
             "Email the receipt","Display the cart total in euros"]));
    }

    sealed class ShelfReviewer : ICodeModel, ICodeBrowserOperator
    {
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> notify,CancellationToken ct)=>throw new Exception("Existing page should be verified directly");
        public Task<ComputerAction> ChooseAction(string task,ComputerObservation page,IReadOnlyList<string> history,CancellationToken ct)=>throw new NotSupportedException();
        public Task<BrowserScenario[]> DesignReview(string task,string path,ComputerObservation page,CancellationToken ct)=>
            Task.FromResult<BrowserScenario[]>([new("label-price","Check the displayed price",path,[new("shelf_price","#item","4.00")])]);
    }
}
