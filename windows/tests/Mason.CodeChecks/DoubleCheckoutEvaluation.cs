using System.Text.Json;
using Mason;
using Mason.Code;

internal static class DoubleCheckoutEvaluation
{
    internal static async Task Run(string endpoint,string modelId,string area)
    {
        if(Directory.Exists(area))throw new IOException("Use a fresh double-checkout trial directory.");
        var root=Directory.CreateDirectory(Path.Combine(area,"project")).FullName;
        var scope=new CodeWorkspace(root);
        var fixtures=Path.Combine(AppContext.BaseDirectory,"fixtures","double-checkout");
        var html=scope.Create("index.html",File.ReadAllText(Path.Combine(fixtures,"index.html")));
        var good=File.ReadAllText(Path.Combine(fixtures,"store.py"));
        var scenario=new BrowserScenario("double","One committed order and matching receipts for two submissions","index.html",
            [new("double_click","#buy"),new("text","#done","2"),new("capture","#receipt","ORD-"),new("reload"),new("same","#receipt"),
             new("response_count","POST /checkout","2"),new("sqlite_count","orders.sqlite#orders","1"),new("response_same","POST /checkout","id")]);
        var policy=new BrowserScenario("policy","Keep the pickup policy working","index.html",[new("click","#policy"),new("text","#terms","Local pickup")]);
        var separate=new BrowserScenario("separate","Separate baskets get distinct orders","index.html",
            [new("click","#buy"),new("text","#done","1"),new("capture","#receipt","ORD-"),new("click","#next"),
             new("click","#buy"),new("text","#done","1"),new("response_count","POST /checkout","2"),
             new("sqlite_count","orders.sqlite#orders","2"),new("new_id","#receipt","ORD-")]);
        var contract=new BrowserContract(1,"store:application",[scenario,separate,policy]);
        var broken=scope.Create("store.py",good.Replace("WHERE request_key = ?","WHERE id = ?"));
        var request="Fix double checkout: two submissions of one basket must return the same receipt and commit one order. Separate baskets must get distinct orders. Preserve existing features. Clicking the button \"Show policy\" must show \"Local pickup\".\n```mason.acceptance.json\n"+JsonSerializer.Serialize(contract)+"\n```";
        using var store=new CodeStore(Path.Combine(area,"profile"));
        var session=store.New(root);
        session.Permission=CodePermission.Auto;
        session.ActionLimit=12;
        var model=new CodeLocalModel(new AiEndpointProfile{Endpoint=endpoint,Model=modelId,ManagedCapability=AiCapability.Chat},"","default",_=>{});
        int calls=0;
        model.InferenceCompleted+=r=>{if(r.CompletionTokens>0)calls++;};
        var label="Night pickup "+Guid.NewGuid().ToString("N")[..8];
        string? concurrentHash=null;
        void Change(){
            var a=session.Activities.LastOrDefault();
            if(concurrentHash!=null || a?.Tool!="replace_file" || a.State!="Pending")return;
            using var args=JsonDocument.Parse(a.Arguments);
            if(args.RootElement.GetProperty("path").GetString()=="store.py" && args.RootElement.GetProperty("expected_sha256").GetString()==broken.Sha256)
                concurrentHash=scope.Edit("store.py",broken.Sha256,"Trail Market",label,(_,_)=>{}).Sha256;
        }
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await new CodeEngine(store,model,(_,_)=>Task.FromResult(false),Change).Run(session,request,timeout.Token);
        var plan=session.Turns.First(t=>t.Acceptance!=null).Acceptance!;
        var extra=new AcceptanceCheck(1,"Two distinct baskets, four submissions","browser_flow","index.html","second-basket","Combined checkout")
        {Application=contract.Application, Steps=[new("text","h1",label),new("double_click","#buy"),new("text","#done","2"),new("capture","#receipt","ORD-"),
            new("click","#next"),new("double_click","#buy"),new("text","#done","2"),new("new_id","#receipt","ORD-"),new("reload"),new("same","#receipt"),
            new("response_count","POST /checkout","4"),new("sqlite_count","orders.sqlite#orders","2")]};
        var holdout=(await CodeBrowserVerification.Run(scope,[extra],timeout.Token))[extra];
        var collapsed=new CodeWorkspace(Directory.CreateDirectory(Path.Combine(area,"collapsed-control")).FullName);
        collapsed.Create("index.html",html.Content);
        collapsed.Create("store.py",good.Replace("WHERE request_key = ?","WHERE ? IS NOT NULL"));
        var separateCheck=plan.Checks.Single(c=>c.Target=="separate");
        var control=(await CodeBrowserVerification.Run(collapsed,[separateCheck],timeout.Token))[separateCheck];
        bool caughtReuse=!control.Passed && control.Detail.Contains("expected 2, observed 1 committed rows");
        var initial=session.Activities.First(a=>a.Tool=="acceptance_checks");
        bool conflict=session.Activities.Any(a=>a.Tool=="replace_file" && a.State=="Failed" && a.Result.Contains("file changed",StringComparison.OrdinalIgnoreCase));
        bool refreshed=session.Activities.Any(a=>a.Tool=="replace_file" && a.State=="Completed" && a.Arguments.Contains(concurrentHash??"no concurrent hash"));
        var result=new {Model=modelId,Calls=calls,session.State,Baseline=initial.Result,
            CaughtDuplicate=initial.State=="Failed" && initial.Result.Contains("observed 2 committed rows"),StaleRejected=conflict,FreshHashUsed=refreshed,
            LabelPreserved=scope.Read("store.py").Content.Contains(label),UntouchedPage=scope.Read("index.html").Sha256==html.Sha256,
            SourceReviewed=session.Activities.Any(a=>a.Tool=="independent_source_review" && a.State=="Completed"),
            Verified=plan.Results.All(r=>r.Passed) && plan.VerifiedRevision!=null && plan.IndependentReviewPage=="index.html",
            DatabaseIsolated=!File.Exists(Path.Combine(root,"orders.sqlite")),CaughtReusedOrder=caughtReuse,CollapsedControl=control.Detail,HoldoutPassed=holdout.Passed,Holdout=holdout.Detail,Results=plan.Results};
        File.WriteAllText(Path.Combine(area,"double-checkout.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        if(calls<1 || session.State!="Completed" || !result.CaughtDuplicate || !conflict || !refreshed || !result.LabelPreserved ||
            !result.UntouchedPage || !result.SourceReviewed || !result.Verified || !result.DatabaseIsolated || !holdout.Passed || !caughtReuse)
            throw new InvalidDataException("Double-checkout qualification failed. See "+area);
    }
}
