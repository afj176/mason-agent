using Mason.Code;
using Mason;
using System.Text.Json;

internal static class HarnessContractChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        var area=Directory.CreateDirectory(Path.Combine(root,"contract-"+Guid.NewGuid().ToString("N"))).FullName;
        var project=Directory.CreateDirectory(Path.Combine(area,"code")).FullName;
        var scope=new CodeWorkspace(project);
        check(!BrowserContract.CoversBehavior("Calculate the discount for a quantity change",[new("count_distinct",".item","3")]) &&
            !BrowserContract.CoversBehavior("The order number survives a reload",[new("text","#status","Confirmed")]),
            "an unrelated numeric assertion or a static confirmation cannot certify a calculation or persisted order");
        var quant=HubClient.Variants(new[]{"q5.gguf","q5-00001-of-00002.gguf","q5-00002-of-00002.gguf"}.Select(p=>new HubFile(p,10,"",true)));
        check(quant.Length==2 && quant.Any(v=>v.Files.Length==1 && v.Name=="q5.gguf") && quant.Any(v=>v.Files.Length==2),"a standalone GGUF remains selectable when its repository also has the same split quantization");
        File.WriteAllText(Path.Combine(project,"Price.cs"),"public class Price { public static int Total(int units)=>units*10; }");
        File.WriteAllText(Path.Combine(project,"PriceTests.cs"),"class PriceTests { int Order()=>Price.Total(3); string Label=\"Total\"; /* Total */ }");
        File.WriteAllText(Path.Combine(project,"Broken.cs"),"class Broken { void F() { int x = ; } }");
        File.WriteAllText(Path.Combine(project,"inventory.py"),"def quantity(order):\n    return len(order)\n");
        var noise=Directory.CreateDirectory(Path.Combine(project,"assets"));
        for(int i=0;i<1001;i++) File.WriteAllBytes(Path.Combine(noise.FullName,$"{i}.bin"),[]);
        JsonElement Navigate(string name,string mode)=>JsonSerializer.SerializeToElement(CodeNavigation.Query(scope,".",name,mode,[],default));
        var definitions=Navigate("Total","definitions").GetProperty("results");
        check(definitions.GetArrayLength()==1 && definitions[0].GetProperty("Path").GetString()=="Price.cs","compiler resolves a declared method in a repository with more than 1,000 unrelated files");
        var references=Navigate("Total","references").GetProperty("results");
        check(references.GetArrayLength()==1 && references[0].GetProperty("Kind").GetString()=="test reference","bound C# references exclude strings and comments");
        check(Navigate("Total","tests").GetProperty("results").GetArrayLength()==1,"related test location comes from a bound reference");
        check(Navigate("","diagnostics").GetProperty("results").EnumerateArray().Any(d=>d.GetProperty("Path").GetString()=="Broken.cs"),"compiler diagnostics identify a broken source file");
        check(Navigate("quantity","definitions").GetProperty("results")[0].GetProperty("Kind").GetString()=="text candidate","non-C# navigation labels lexical candidates");

        string data=Path.Combine(area,"profile");
        string id;string revision;
        var model=new Finish();
        using(var store=new CodeStore(data)){
            var session=store.New(project);session.Permission=CodePermission.Auto;
            File.WriteAllText(Path.Combine(project,"first.txt"),"ready");
            await new CodeEngine(store,model,(_,_)=>Task.FromResult(false),()=>{}).Run(session,"Create first.txt containing \"ready\".",default);
            var plan=session.Turns.Single(t=>t.Acceptance!=null).Acceptance!;
            id=plan.RequirementIds.Single();revision=plan.VerifiedRevision!;
            check(session.State=="Completed" && revision.Length==64,"an existing artifact must pass its current observable requirement");
            session.State="Running";session.Activities.Add(new(){Tool="run_command",Arguments="""{"executable":"python","arguments":["-V"],"cwd":"."}""",State="Running"});store.Save(session);
        }
        using(var store=new CodeStore(data)){
            var session=store.Sessions.Single();File.WriteAllText(Path.Combine(project,"second.txt"),"shipped");
            await new CodeEngine(store,model,(_,_)=>Task.FromResult(false),()=>{}).Run(session,"Create second.txt containing \"shipped\".",default);
            var plan=session.Turns.Last(t=>t.Acceptance!=null).Acceptance!;
            check(session.State=="Needs verification" && session.Activities.Count(a=>a.Tool=="run_command")==1 && session.Activities.Any(a=>a.State=="Outcome unknown"),"unknown command outcome survives restart and blocks completion without replay");
            check(plan.RequirementIds.Length==2 && plan.RequirementIds[0]==id,"ordinary follow-up retains the first stable requirement ID");
            File.WriteAllText(Path.Combine(project,"first.txt"),"regressed");
            await CodeAcceptance.Verify(plan,scope,CodePermission.Auto,default);
            check(plan.Results.Count==2 && !plan.Results[0].Passed && plan.Results[1].Passed && plan.VerifiedRevision!=revision,"current revision independently rechecks the old and new features");
            await new CodeEngine(store,model,(_,_)=>Task.FromResult(false),()=>{}).Run(session,"/new Create second.txt containing \"shipped\".",default);
            check(session.State=="Completed" && session.Turns.Last(t=>t.Acceptance!=null).Acceptance!.RequirementIds.Length==1,"explicit new task has a fresh requirement set and command boundary");
        }
    }

    sealed class Finish:ICodeModel
    {
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> stream,CancellationToken ct)=>Task.FromResult(new CodeAction("Finished.",null,default));
    }
}
