using System.Text.Json;
using Mason.Code;

internal static class ProjectEvidenceChecks
{
    sealed class Direction(bool interchangeable):IProjectProbeReviewer
    {
        public Task<bool> ArgumentsInterchangeable(string request,ExampleProbe example,bool sourceSymmetric,CancellationToken ct)=>Task.FromResult(interchangeable);
    }
    public static async Task Run(string area)
    {
        var scope=new CodeWorkspace(Directory.CreateDirectory(area).FullName);
        scope.CreateDirectory(".mason");
        var price=scope.Create("price.cjs","module.exports = price => price;");
        scope.Create("price.test.cjs","const test=require('node:test'), assert=require('node:assert/strict'); const price=require('./price.cjs'); test('discount',()=>assert.equal(price(10),8)); test('legacy',()=>assert.equal(1,2));");
        scope.Create("mason.workflows.json","""{"version":1,"workflows":{"test":{"executable":"node","arguments":["--test","--test-reporter=tap","price.test.cjs"]}}}""");
        scope.Create(".mason/tests.json",JsonSerializer.Serialize(new[]{new ProjectTest("Fix discount","test","tap","","discount",["price.test.cjs"],["price.cjs"])}));
        var plan=new CodeAcceptancePlan{Units=["Fix discount"]};
        CodeProjectTests.Bind(scope,plan,plan.Checks);
        await CodeProjectTests.Baseline(scope,plan,default);
        var check=plan.Checks.Single();
        var failed=await CodeProjectTests.Verify(scope,check,plan,CodePermission.Auto,default);
        scope.Edit(price.Path,price.Sha256,price.Content,"module.exports = price => price * 0.8;",(_,_)=>{});
        var fixedResult=await CodeProjectTests.Verify(scope,check,plan,CodePermission.Auto,default);
        if(failed.Passed || !fixedResult.Passed || !fixedResult.Detail.Contains("failed → passed") || !fixedResult.Detail.Contains("legacy"))
            throw new InvalidDataException("TAP baseline comparison: "+failed.Detail+" / "+fixedResult.Detail);
        var test=scope.Read("price.test.cjs");scope.Edit(test.Path,test.Sha256,test.Content,"",(_,_)=>{});
        if((await CodeProjectTests.Verify(scope,check,plan,CodePermission.Auto,default)).Passed)throw new Exception("A modified test was accepted.");
        string[][] examples=[
            ["junit","<testsuite><testcase classname='Billing' name='Tax'/></testsuite>","Billing.Tax"],
            ["trx","<TestRun><Results><UnitTestResult testName='Billing.Tax' outcome='Passed'/></Results></TestRun>","Billing.Tax"],
            ["go-json","""{"Action":"pass","Package":"shop","Test":"TestTotal"}""","shop/TestTotal"],
            ["flutter-json","""{"type":"testStart","test":{"id":1,"name":"tax"}}"""+"\n"+"""{"type":"testDone","testID":1,"result":"success"}""","tax"]];
        if(examples.Any(t=>CodeProjectTests.Parse(t[0],t[1]).GetValueOrDefault(t[2])!="passed"))throw new Exception("Test reporter identity lost.");
        var profile=Path.Combine(Path.GetDirectoryName(area)!,"notes-profile");
        var noteId=Guid.NewGuid();
        using(var store=new CodeStore(profile)){var p=store.AddProject(scope.Root);store.SaveNote(p.Id,noteId,"contract in price",price.Path);}
        using(var store=new CodeStore(profile)){
            var p=store.ProjectForWorkspace(scope.Root)!;
            if(!CodeStore.NoteCurrent(scope,p.Notes.Single()))throw new Exception("Project note did not persist.");
            var file=scope.Read(price.Path);scope.Edit(file.Path,file.Sha256,file.Content,"module.exports = null;",(_,_)=>{});
            if(CodeStore.NoteCurrent(scope,p.Notes.Single()))throw new Exception("Stale project note remained current.");
            store.ForgetNote(p.Id,noteId);
        }
        using(var store=new CodeStore(profile))if(store.ProjectForWorkspace(scope.Root)!.Notes.Length!=0)throw new Exception("Forgotten project note restored.");
        var codec=new CodeWorkspace(Directory.CreateDirectory(Path.Combine(area,"codec")).FullName);
        codec.CreateDirectory(".mason");
        var impl=codec.Create("codec.cjs","exports.encode=s=>s.replaceAll(' ','%20'); exports.decode=s=>s.replaceAll('%20',' ');");
        codec.Create("test.cjs","const test=require('node:test'),assert=require('node:assert/strict'),api=require('./codec.cjs');test('codec',()=>assert.equal(api.decode(api.encode('a b')),'a b'));");
        codec.Create("mason.workflows.json","""{"version":1,"workflows":{"test":{"executable":"node","arguments":["--test","--test-reporter=tap","test.cjs"]}}}""");
        codec.Create(".mason/tests.json",JsonSerializer.Serialize(new[]{new ProjectTest("Round trip","test","tap","","codec",["test.cjs"],["codec.cjs"]){RoundTrip=new("codec.cjs","encode","decode"," %20")}}));
        var properties=new CodeAcceptancePlan{Units=["Round trip"]};
        CodeProjectTests.Bind(codec,properties,properties.Checks);
        await CodeProjectTests.Baseline(codec,properties,default);
        var broken=await CodeProjectTests.Verify(codec,properties.Checks.Single(),properties,CodePermission.Auto,default);
        if(broken.Passed || !broken.Detail.Contains("Round trip failed"))throw new Exception("The passing example concealed a non-reversible encoding.");
        codec.Edit(impl.Path,impl.Sha256,impl.Content,"exports.encode=encodeURIComponent;exports.decode=decodeURIComponent;",(_,_)=>{});
        if(!(await CodeProjectTests.Verify(codec,properties.Checks.Single(),properties,CodePermission.Auto,default)).Passed)throw new Exception("The valid codec failed the declared round-trip property.");
        Console.WriteLine("PASS sandboxed round-trip property catches collisions beyond the project's passing example");
        if(!CodeProjectProbes.HasSymmetricImplementation("exports.overlaps=(left,right)=>left.x < right.y && right.x < left.y;","overlaps") ||
            CodeProjectProbes.HasSymmetricImplementation("exports.overlaps=(left,right)=>left.x < right.y && right.x <= left.y;","overlaps") ||
            CodeProjectProbes.HasSymmetricImplementation("exports.compare=(a,b)=>a<b;","compare"))
            throw new Exception("Argument symmetry proof accepted unequal comparisons or dropped a mirrored projection.");
        var pair=new CodeWorkspace(Directory.CreateDirectory(Path.Combine(area,"pair")).FullName);
        pair.CreateDirectory(".mason");
        pair.Create("pair.cjs","exports.choose=(a,b)=>a;");
        pair.Create("case.cjs","const test=require('node:test'),assert=require('node:assert/strict'); const {choose:pick}=require('./pair.cjs');test('pair',()=>assert.equal(pick(2,3),2));");
        pair.Create("mason.workflows.json","""{"version":1,"workflows":{"test":{"executable":"node","arguments":["--test","--test-reporter=tap","case.cjs"]}}}""");
        pair.Create(".mason/tests.json",JsonSerializer.Serialize(new[]{new ProjectTest("Select the smaller value","test","tap","","pair",["case.cjs"],["pair.cjs"])}));
        foreach(bool symmetric in new[]{true,false}){
            var pairPlan=new CodeAcceptancePlan{Request=symmetric?"Select the smaller value":"Select the first value",Units=["Select the smaller value"]};
            CodeProjectTests.Bind(pair,pairPlan,pairPlan.Checks);
            await CodeProjectTests.Baseline(pair,pairPlan,default,new Direction(symmetric));
            var observed=await CodeProjectTests.Verify(pair,pairPlan.Checks.Single(),pairPlan,CodePermission.Auto,default);
            if(observed.Passed==symmetric || symmetric && !observed.Detail.Contains("choose(3, 2)"))
                throw new Exception("Independent example did not respect the reviewed argument roles.");
        }
        Console.WriteLine("PASS a grounded symmetric follow-up catches an asymmetric result and a directional function keeps its declared argument roles");
        Console.WriteLine("PASS named test baseline, pre-existing failure, pinned oracle, four machine reporters and project memory freshness");
    }
}
