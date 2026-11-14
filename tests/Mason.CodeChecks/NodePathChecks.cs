using System.Text.Json;
using Mason.Code;

static class NodePathChecks
{
    public static async Task Run(string area)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        string workspace=Path.Combine(area,"workspace");Directory.CreateDirectory(Path.Combine(workspace,"lib"));Directory.CreateDirectory(Path.Combine(workspace,"js"));
        File.WriteAllText(Path.Combine(area,"outside.txt"),"protected");
        File.WriteAllText(Path.Combine(workspace,"js/main.js"),"document.querySelector('h1').textContent = 'Hello';");
        File.WriteAllText(Path.Combine(workspace,"lib/helper.cjs"),"module.exports=42;");
        File.WriteAllText(Path.Combine(workspace,"lib/helper.mjs"),"export default 42;");
        File.WriteAllText(Path.Combine(workspace,"child.cjs"),"console.log('child-ok');");
        File.WriteAllText(Path.Combine(workspace,"main.cjs"),"""
            const assert=require('node:assert/strict'), fs=require('node:fs'), cp=require('node:child_process');
            assert.equal(require('./lib/helper.cjs'),42);
            for(const action of [()=>fs.readFileSync('../outside.txt'),()=>fs.writeFileSync('../outside.txt','bad'),()=>fs.lstatSync(require('node:path').parse(process.cwd()).root)]){
              assert.throws(action,e=>e.code==='EPERM'||e.code==='EACCES');
            }
            const child=cp.spawnSync(process.execPath,['child.cjs'],{encoding:'utf8'});assert.equal(child.status,0,child.stderr);assert.match(child.stdout,/child-ok/);
            const socket=require('node:net').connect({host:'1.1.1.1',port:443});socket.on('connect',()=>process.exit(9));socket.on('error',e=>{assert.ok(['EPERM','EACCES'].includes(e.code));console.log('cjs-boundaries-ok')});
            """);
        File.WriteAllText(Path.Combine(workspace,"entry.mjs"),"import value from './lib/helper.mjs'; import assert from 'node:assert/strict'; assert.equal(value,42); console.log('esm-ok');");
        File.WriteAllText(Path.Combine(workspace,"invalid.js"),"function broken( {");
        var node=CodeCommand.ResolveExecutable("node.exe",workspace);
        async Task Good(string[] args,string marker="")
        {
            var result=await CodeCommand.Run(node,args,workspace,default,workspace);
            if(result.ExitCode!=0||result.Failure!=null||!result.Output.Contains(marker))throw new Exception(JsonSerializer.Serialize(result));
            Console.WriteLine("PASS offline Node "+string.Join(' ',args));
        }
        await Good(["--check","js/main.js"]);
        await Good(["--check",Path.Combine(workspace,"js/main.js")]);
        await Good(["main.cjs"],"cjs-boundaries-ok");await Good(["entry.mjs"],"esm-ok");
        var invalid=await CodeCommand.Run(node,["--check","invalid.js"],workspace,default,workspace);
        if(invalid.ExitCode==0||invalid.Failure!=null||!invalid.Output.Contains("SyntaxError"))throw new Exception("Genuine syntax errors must remain source failures");
        Console.WriteLine("PASS genuine JavaScript syntax error remains a source failure");
        var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new Exception("Git Bash required");
        var shell=await CodeCommand.Run(bash,["--noprofile","--norc","-c","set -e; node --check js/main.js && node main.cjs && node entry.mjs"],workspace,default,workspace);
        if(shell.ExitCode!=0||!shell.Output.Contains("esm-ok")||!shell.Output.Contains("cjs-boundaries-ok"))throw new Exception(shell.Output);
        Console.WriteLine("PASS Git Bash offline Node syntax, CJS/ESM, child process, root metadata denial and direct-socket denial");
        using var store=new CodeStore(Path.Combine(area,"profile"));var session=store.New(workspace);session.Permission=CodePermission.Auto;
        var failureArgs=new[]{"--no-preserve-symlinks","--no-preserve-symlinks-main","--check","js/main.js"};
        var model=new ScriptedModel(new("Checking syntax", "run_command",JsonSerializer.SerializeToElement(new{executable=node,arguments=failureArgs,directory="."})),new("Wrong retry","create_file",JsonSerializer.SerializeToElement(new{path="wrong-retry.txt",content="must not run"})));
        await new CodeEngine(store,model,(_,_)=>throw new Exception("Unexpected command review"),()=>{}).Run(session,"Check JavaScript syntax",default);
        if(session.State!="Blocked"||session.Activities.Count!=1||File.Exists(Path.Combine(workspace,"wrong-retry.txt"))||!session.Messages.Last().Content.Contains("sandbox_node_path_resolution"))throw new Exception("Runtime retry circuit breaker failed: "+session.Detail);
        var activity=session.Activities.Single();var rendered=CodeActivityText.Result(activity);
        if(!rendered.Contains("No JavaScript syntax result")||!rendered.Contains("Command output:")||!rendered.Contains("EPERM"))throw new Exception("Runtime error presentation lost diagnosis or raw evidence");
        store.Dispose();using var reopened=new CodeStore(Path.Combine(area,"profile"));
        if(reopened.Sessions.Single(s=>s.Id==session.Id).State!="Blocked")throw new Exception("Blocked state lost on reload");
        if(File.ReadAllText(Path.Combine(area,"outside.txt"))!="protected")throw new Exception("Outside canary changed");
        Console.WriteLine("PASS structured runtime failure, readable raw evidence, persisted blocked state and no model retry");
        foreach(var sample in new[]{"SyntaxError: invalid JavaScript", "Error: EPERM: operation not permitted, open 'D:\\outside.txt'", "Error: EPERM: operation not permitted, lstat 'D:\\'\n at Object.realpathSync (node:fs:2733:25)\n at app.js:1:1\n at node:internal/modules/cjs/loader:10:1"})
            if(CodeCommandFailure.Classify(1,sample,true)!=null)throw new Exception("Overbroad runtime classification");
        if(CodeActivityText.Result(new CodeActivity{Result="{\"Output\":\"raw failure\",\"Failure\":{}}"})!="raw failure")throw new Exception("Malformed optional diagnostic must retain raw output");
        Console.WriteLine("PASS ordinary permission and syntax errors are not misclassified");
    }
}
