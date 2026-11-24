using System.Text.Json;
using Mason.Code;

internal static class ProjectEvidenceChecks
{
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
        var attachment=DeveloperEvidence.Editor(scope,JsonSerializer.Serialize(new{version=1,workspace=scope.Root,path=price.Path,selection="unsaved",line=1,endLine=1,bufferVersion=2,dirty=true,diskHash=price.Sha256,diagnostics="old signature"}));
        var info=JsonSerializer.SerializeToElement(attachment.ForModel(scope));
        if(!attachment.Unsaved || info.GetProperty("disk_revision_current").GetBoolean())throw new Exception("Unsaved or stale buffer evidence misreported.");
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
        Console.WriteLine("PASS named test baseline, pre-existing failure, pinned oracle, four machine reporters, unsaved buffer and project memory freshness");
    }
}
