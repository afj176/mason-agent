using System.Diagnostics;
using System.Text.Json;
using Mason;
using Mason.Code;

internal static class ModelBenchmark
{
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    static readonly string[] Cases=["working","products","discount","order","fresh","existing","multifile","interrupted"];
    sealed record Trial(string Scenario,int Repetition,string State,bool OraclePass,bool Completed,bool Correct,int Calls,
        long? PromptTokens,long? OutputTokens,double Seconds,int Tools,string[] FailedFeatures,string Detail);

    public static async Task Run(string endpoint,string modelId,string root,int repetitions,string commit)
    {
        if(repetitions is <1 or >10 || Directory.Exists(root))throw new ArgumentException("Use a fresh output directory and 1 to 10 repetitions.");
        if(commit!="development" && !FileVersionInfo.GetVersionInfo(typeof(CodeEngine).Assembly.Location).ProductVersion!.EndsWith("+"+commit,StringComparison.Ordinal))
            throw new InvalidOperationException("The evaluated harness binary does not match the supplied source commit.");
        Directory.CreateDirectory(root);
        var trials=new List<Trial>();
        foreach(int repetition in Enumerable.Range(1,repetitions))
        foreach(string scenario in Cases)
        {
            var area=Directory.CreateDirectory(Path.Combine(root,$"{repetition}-{scenario}")).FullName;
            var workspace=Directory.CreateDirectory(Path.Combine(area,"workspace")).FullName;
            var profile=Path.Combine(area,"profile");
            bool checkout=Array.IndexOf(Cases,scenario)<4;
            var model=new CodeLocalModel(new AiEndpointProfile{Endpoint=endpoint,Model=modelId,ManagedCapability=AiCapability.Chat},"","default",_=>{});
            var usage=new List<AiChatResult>();model.InferenceCompleted+=usage.Add;
            var token=$"Honey {repetition}";
            string request=CheckoutEvaluation.Request;
            var scope=new CodeWorkspace(workspace);
            var timer=Stopwatch.StartNew();
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            if(checkout)CheckoutEvaluation.Seed(workspace,scenario);
            if(scenario=="fresh")request=$"In index.html show a heading named \"Trail Market {repetition}\". Clicking the button \"Add berries\" must show \"Berries added\".";
            if(scenario=="existing"){
                File.WriteAllText(Path.Combine(workspace,"index.html"),"<!doctype html><html><body><h1>Honey Market</h1><button>Add honey</button><output></output></body></html>");
                request="In index.html show a heading named \"Honey Market\". Clicking the button \"Add honey\" must show \"Added to cart\".";
            }
            if(scenario=="multifile")request=$"Create labels.txt containing \"{token}\" and create prices.csv containing \"Honey,10\".";
            string? retainedId=null;
            if(scenario=="interrupted"){
                // Persist and reopen a session at the boundary between two user requests.
                using var interrupted=new CodeStore(profile);
                var before=interrupted.New(workspace);
                var original=$"Create catalog.txt containing \"{token}\".";
                var contract=await CodeAcceptance.Prepare(original,model,scope,scope.Files(),deadline.Token);
                retainedId=contract.RequirementIds.Single();
                File.WriteAllText(Path.Combine(workspace,"catalog.txt"),token);
                before.Turns.Add(new(){Text=original,Acceptance=contract});
                before.Messages.Add(new("user",JsonSerializer.Serialize(new { request=original })));
                before.State="Running";interrupted.Save(before);
                request="Create stock.txt containing \"Available\".";
            }
            using var store=new CodeStore(profile);
            var session=store.Sessions.SingleOrDefault()??store.New(workspace);
            session.Permission=CodePermission.Auto;session.ActionLimit=18;
            await new CodeEngine(store,model,(_,_)=>Task.FromResult(false),()=>{}).Run(session,request,deadline.Token);
            var plan=session.Turns.LastOrDefault(t=>t.Acceptance!=null)?.Acceptance;
            var failed=plan?.Results.Where(r=>!r.Passed).Select(r=>r.Check.Target).ToArray()??[];
            bool Has(string file,string value)=>File.Exists(Path.Combine(workspace,file))&&File.ReadAllText(Path.Combine(workspace,file)).Contains(value,StringComparison.Ordinal);
            bool oracle;
            bool coverage=true;
            if(checkout){
                oracle=scenario=="working";
                coverage=plan?.Results.Count==3 && plan.Results.Select(r=>r.Check.Target).Order().SequenceEqual(new[]{"discount","order","products"})
                    && (oracle?failed.Length==0:failed.SequenceEqual([scenario])) && !File.Exists(Path.Combine(workspace,"orders.sqlite"));
            } else if(scenario is "existing" or "fresh"){
                var gold=new CodeAcceptancePlan{Checks=[new(1,"heading","browser_heading","index.html","",scenario=="fresh"?$"Trail Market {repetition}":"Honey Market"),
                    new(2,"interaction","browser_click","index.html",scenario=="fresh"?"Add berries":"Add honey",scenario=="fresh"?"Berries added":"Added to cart")]};
                await CodeAcceptance.Verify(gold,scope,CodePermission.Auto,CancellationToken.None);
                oracle=gold.Results.Count==2 && gold.Results.All(r=>r.Passed);
                File.WriteAllText(Path.Combine(area,"oracle.json"),JsonSerializer.Serialize(gold,Json));
            } else if(scenario=="multifile")oracle=Has("labels.txt",token)&&Has("prices.csv","Honey,10");
            else if(scenario=="interrupted"){
                oracle=Has("catalog.txt",token)&&Has("stock.txt","Available");
                coverage=plan?.RequirementIds.Contains(retainedId!)==true && plan.Results.Count==2;
            } else throw new InvalidOperationException("Scenario has no external oracle.");
            var done=session.State=="Completed";
            bool correct=(checkout?done==oracle && (done||session.State=="Needs verification"):done&&oracle) && coverage && usage.Count>0;
            var trial=new Trial(scenario,repetition,session.State,oracle,done,correct,usage.Count,
                usage.All(i=>i.PromptTokens.HasValue)?usage.Sum(i=>(long)i.PromptTokens!.Value):null,
                usage.All(i=>i.CompletionTokens.HasValue)?usage.Sum(i=>(long)i.CompletionTokens!.Value):null,
                timer.Elapsed.TotalSeconds,session.Activities.Count,failed,session.Detail);
            trials.Add(trial);
            File.WriteAllText(Path.Combine(area,"acceptance.json"),JsonSerializer.Serialize(plan,Json));
            File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new { Version=1,Commit=commit,Model=modelId,Repetitions=repetitions,
                Success=trials.Count(t=>t.Correct),FalseCompletions=trials.Count(t=>t.Completed&&!t.OraclePass),
                FalseRejections=trials.Count(t=>!t.Completed&&t.OraclePass),Trials=trials },Json));
            Console.WriteLine($"{modelId} {repetition}/{repetitions} {scenario}: {session.State}, correct={correct}, calls={usage.Count}, {timer.Elapsed.TotalSeconds:F1}s");
        }
        if(trials.Any(t=>!t.Correct))Environment.ExitCode=1;
    }
}
