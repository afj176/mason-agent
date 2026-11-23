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
        check(BrowserContract.Amount("Order total: $27.00",out var price) && price==27m && !BrowserContract.Amount("$30.00: $27.00",out _) &&
            !BrowserContract.Amount("Total: $27.00 or $30.00",out _) && BrowserContract.HasIdentifier("Order number: ORD-5123","ORD-") &&
            !BrowserContract.HasIdentifier("Order number: ORD-","ORD-"),"labeled monetary values and order IDs are observable while ambiguous amounts and bare prefixes fail");
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
        var web=Directory.CreateDirectory(Path.Combine(area,"browser")).FullName;
        using(var store=new CodeStore(data)){
            var session=store.New(web);session.Permission=CodePermission.Auto;
            var flow=new BrowserRecovery();
            await new CodeEngine(store,flow,(_,_)=>throw new Exception("Unexpected approval"),()=>{}).Run(session,"Build a map viewer",default);
            check(flow.Repaired && session.State=="Needs verification" && session.Activities.Any(a=>a.Tool=="browser_load" && a.Result.Contains("index.html:")) &&
                session.Turns.Single(t=>t.Acceptance!=null).Acceptance!.Results.Any(r=>r.Passed && r.Check.Kind=="browser_text"),
                "an inline syntax error on a page over 4 KB is repaired autonomously while a subjective requirement stays open");
            File.WriteAllText(Path.Combine(web,"handler.js"),"document.querySelector('button').onclick=()=>{throw Error('broken click')};");
            File.WriteAllText(Path.Combine(web,"index.html"),"<button>Open</button><script src='handler.js'></script>");
            var checkedPage=new AcceptanceCheck(1,"Open map","browser_click","index.html","Open","Map opened");
            var outcome=(await CodeBrowserVerification.Run(new CodeWorkspace(web),[checkedPage],default))[checkedPage];
            check(!outcome.Passed && outcome.ScriptPath=="handler.js" && outcome.Detail.Contains("broken click"),"a handler exception identifies the actual served JavaScript dependency");
            var conflict=store.New(web);conflict.Permission=CodePermission.Auto;
            var edit=new ConflictRecovery();
            await new CodeEngine(store,edit,(_,_)=>throw new Exception("Unexpected approval"),()=>{}).Run(conflict,"Update the map handler",default);
            check(edit.SawFreshSource && File.ReadAllText(Path.Combine(web,"handler.js")).Contains("fixed click") && conflict.Activities.Any(a=>a.State=="Failed" && a.Tool=="edit_file"),
                "Auto handles stale edits with the current source and a hash-checked replacement");
            var build=store.New(Directory.CreateDirectory(Path.Combine(area,"generated")).FullName);
            build.Permission=CodePermission.Auto;
            await new CodeEngine(store,new BrokenBinding(),(_,_)=>throw new Exception("Unexpected approval"),()=>{}).Run(build,
                "Clicking the button \"Start\" must show \"Ready\".",default);
            check(build.State=="Completed" && build.Activities.Any(a=>a.Tool=="replace_file" && a.State=="Completed") &&
                build.Activities.Any(a=>a.Result.Contains("Assignment to constant variable")), "Edge identifies a reassigned binding and Auto repairs its own file before accepting the interaction");
            var desktop=store.New(Directory.CreateDirectory(Path.Combine(area,"desktop")).FullName);desktop.Permission=CodePermission.Auto;
            var specification=new BrowserContract(1,null,[new("start","Start shows Ready","index.html",[new("click","button"),new("text","output","Ready")])]);
            var form="Build this page.\r\r```mason.acceptance.json\r"+JsonSerializer.Serialize(specification)+"\r```";
            await new CodeEngine(store,new DesktopBuild(),(_,_)=>throw new Exception("Unexpected review"),()=>{}).Run(desktop,form,default);
            var desktopPlan=desktop.Turns.First().Acceptance!;
            check(desktop.State=="Completed" && desktopPlan.Checks.Count==1 && desktopPlan.Checks[0].Target=="start" && desktopPlan.Results.Single().Passed,
                "the installed WinUI CR-only composer preserves the pinned scenario and runs its actual browser steps");
        }
    }
    sealed class DesktopBuild:ICodeModel
    {
        bool created;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> stream,CancellationToken ct)=>
            Task.FromResult(created ? new CodeAction("Finished.",null,default) : Create());
        CodeAction Create(){
            created=true;
            return new("Create page","create_file",JsonSerializer.SerializeToElement(new{path="index.html",
                content="<button onclick=\"document.querySelector('output').textContent='Ready'\">Start</button><output></output>"}));
        }
    }
    sealed class BrokenBinding:ICodeModel
    {
        int calls;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> stream,CancellationToken ct)
        {
            if(calls++>0)throw new Exception("Expected a host repair of the observed binding");
            string content="<!doctype html><html><body><button>Start</button><output></output><script>\nconst value = '';\ndocument.querySelector('button').onclick=()=>{\nvalue = 'Ready';\ndocument.querySelector('output').textContent=value;\n};\n</script></body></html>";
            return Task.FromResult(new CodeAction("Create page","create_file",JsonSerializer.SerializeToElement(new{path="index.html",content})));
        }
    }

    sealed class BrowserRecovery:ICodeModel,ICodeAcceptancePlanner
    {
        int calls;
        public bool Repaired;
        public Task<List<AcceptanceCheck>> PlanAcceptance(string request,string[] units,CodeFiles files,CancellationToken ct)=>Task.FromResult<List<AcceptanceCheck>>([
            new(1,"Visible map","browser_text","index.html","","Map"),new(1,"Visual taste","unverified","","","Needs visual review")]);
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> stream,CancellationToken ct)
        {
            if(calls++==0)return Task.FromResult(new CodeAction("Create map","create_file",JsonSerializer.SerializeToElement(new{path="index.html",content="<!doctype html><html><body><!--"+new string('x',5000)+"--><p>Map</p><script>let = ;</script></body></html>"})));
            if(messages[0].PageRepair is {} repair){
                Repaired=repair.Evidence.Contains("JavaScript error") && repair.File.Content.Length>4000;
                return Task.FromResult(new CodeAction("Repair map","replace_file",JsonSerializer.SerializeToElement(new{path=repair.File.Path,expected_sha256=repair.File.Sha256,content=repair.File.Content.Replace("let = ;","let ready = true;")})));
            }
            return Task.FromResult(new CodeAction("Finished.",null,default));
        }
    }
    sealed class ConflictRecovery:ICodeModel
    {
        int calls;
        public bool SawFreshSource;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> stream,CancellationToken ct)
        {
            if(calls++==0)return Task.FromResult(new CodeAction("Edit handler","edit_file",JsonSerializer.SerializeToElement(new{path="handler.js",expected_sha256=new string('0',64),old_text="old",new_text="fixed"})));
            if(messages[0].PageRepair is {} repair){
                SawFreshSource=repair.File.Path=="handler.js" && repair.File.Content.Contains("broken click") && repair.Evidence.Contains("Rejected edit");
                return Task.FromResult(new CodeAction("Repair handler","replace_file",JsonSerializer.SerializeToElement(new{path=repair.File.Path,expected_sha256=repair.File.Sha256,content=repair.File.Content.Replace("broken click","fixed click")})));
            }
            return Task.FromResult(new CodeAction("Finished.",null,default));
        }
    }

    sealed class Finish:ICodeModel
    {
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> stream,CancellationToken ct)=>Task.FromResult(new CodeAction("Finished.",null,default));
    }
}
