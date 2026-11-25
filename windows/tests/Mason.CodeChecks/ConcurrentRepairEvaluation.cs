using System.Text.Json;
using Mason;
using Mason.Code;

internal static class ConcurrentRepairEvaluation
{
    internal static async Task Run(string endpoint,string modelId,string root)
    {
        if(Directory.Exists(root))throw new InvalidOperationException("Use a fresh repair trial directory.");
        var workspace=Directory.CreateDirectory(Path.Combine(root,"project")).FullName;
        const string html="""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Station log</title>
            <link rel="stylesheet" href="station.css"><script defer src="panel.js"></script>
            <h1>Station log</h1><p id="shift"></p><button id="inspect">Mark inspected</button>
            <button id="list">Show checklist</button><output id="status"></output><p id="checklist"></p></html>
            """;
        const string css="button { padding: .5rem; }";
        const string script="""
            document.getElementById('shift').textContent = 'Day watch';
            document.getElementById('list').onclick = () => {
                document.getElementById('checklist').textContent = 'Radio and ledger';
            };
            document.getElementById('inspect').onclick = () => {
                document.getElementById('statsu').textContent = 'Inspection saved';
            };
            """;
        var scope=new CodeWorkspace(workspace);
        scope.Create("index.html",html);
        scope.Create("station.css",css);
        var initial=scope.Create("panel.js",script);
        var inspect=new BrowserScenario("inspect","Mark an inspection","index.html",
            [new("click","#inspect"),new("text","#status","Inspection saved")]);
        var checklist=new BrowserScenario("checklist","Show the existing checklist","index.html",
            [new("click","#list"),new("text","#checklist","Radio and ledger")]);
        var request="In the station log, clicking the button \"Mark inspected\" must show \"Inspection saved\". Preserve the working checklist, shift label and styling.\n```mason.acceptance.json\n"+
            JsonSerializer.Serialize(new{Version=1,Scenarios=new[]{inspect,checklist}})+"\n```";
        using var store=new CodeStore(Path.Combine(root,"profile"));
        var session=store.New(workspace);
        session.Permission=CodePermission.Auto;
        session.ActionLimit=16;
        var model=new CodeLocalModel(new AiEndpointProfile{Endpoint=endpoint,Model=modelId,ManagedCapability=AiCapability.Chat},"","default",_=>{});
        int calls=0;
        model.InferenceCompleted+=result=>{if(result.CompletionTokens>0)calls++;};
        var newShift="Night watch "+Guid.NewGuid().ToString("N")[..8];
        string? concurrentHash=null;
        Guid? staleActivity=null;
        void OnChange()
        {
            if(concurrentHash!=null || session.Activities.LastOrDefault() is not {} a || a.Tool!="replace_file")return;
            using var args=JsonDocument.Parse(a.Arguments);
            if(args.RootElement.GetProperty("path").GetString()!="panel.js" || args.RootElement.GetProperty("expected_sha256").GetString()!=initial.Sha256)return;
            // The model has already emitted its write. Simulate a maintainer saving in
            // their own editor before the tool's compare-and-write executes.
            concurrentHash=scope.Edit("panel.js",initial.Sha256,"Day watch",newShift,(_,_)=>{}).Sha256;
            staleActivity=a.Id;
        }
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await new CodeEngine(store,model,(_,_)=>Task.FromResult(false),OnChange).Run(session,request,deadline.Token);
        var receipts=session.Activities;
        var stale=receipts.SingleOrDefault(a=>a.Id==staleActivity);
        bool refreshed=receipts.Any(a=>a.Tool=="replace_file" && a.State=="Completed" &&
            JsonSerializer.Deserialize<JsonElement>(a.Arguments).GetProperty("expected_sha256").GetString()==concurrentHash);
        var holdout=new AcceptanceCheck(1,"Retain concurrent state and both controls","browser_flow","index.html","holdout","Combined interaction"){
            Steps=[new("text","#shift",newShift),new("click","#inspect"),new("click","#list"),new("text","#status","Inspection saved"),
                new("text","#checklist","Radio and ledger"),new("reload"),new("text","#shift",newShift)]};
        var observed=(await CodeBrowserVerification.Run(scope,[holdout],deadline.Token))[holdout];
        var plan=session.Turns.First(t=>t.Acceptance!=null).Acceptance!;
        var baseline=receipts.FirstOrDefault(a=>a.Tool=="acceptance_checks");
        var report=new{Model=modelId,session.State,Calls=calls,InitialHash=initial.Sha256,ConcurrentHash=concurrentHash,FinalHash=scope.Read("panel.js").Sha256,
            InitialFailure=baseline?.State=="Failed" && baseline.Result.Contains("panel.js"),StaleRejected=stale?.State=="Failed" && stale.Result.Contains("file changed",StringComparison.OrdinalIgnoreCase),
            FreshHashUsed=refreshed,UntouchedFiles=scope.Read("index.html").Content==html && scope.Read("station.css").Content==css &&
                Directory.GetFiles(workspace,"*",SearchOption.AllDirectories).Length==3,
            ReviewPassed=plan.IndependentReviewPage=="index.html" && plan.Results.Count>=3 && plan.Results.All(r=>r.Passed),OraclePass=observed.Passed,observed.Detail};
        File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        if(report.State!="Completed" || calls==0 || !report.InitialFailure || !report.StaleRejected || !refreshed || !report.UntouchedFiles || !report.ReviewPassed || !observed.Passed)
            throw new InvalidDataException("Concurrent multi-file repair did not qualify. Inspect "+Path.Combine(root,"result.json"));
    }
}
