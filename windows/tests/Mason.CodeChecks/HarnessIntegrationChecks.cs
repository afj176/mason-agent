using System.Text.Json;
using Mason;
using Mason.Code;

internal static class HarnessIntegrationChecks
{
    public static async Task Run(string area)
    {
        var root=Directory.CreateDirectory(Path.Combine(area,"repo")).FullName;
        var host=Directory.CreateDirectory(Path.Combine(area,"host")).FullName;
        var scope=new CodeWorkspace(root);
        var policy=new CodeProjectPolicy("Only reviewed workflows",root,["run_shell"],["check"],DateTimeOffset.UtcNow.AddHours(1),true);
        CodeProjectPolicy.Set(host,root,policy);
        var saved=CodeProjectPolicy.Load(host,root)!;saved.Check("run_workflow","check");
        try{saved.Check("run_workflow","other");throw new Exception("Unknown workflow permitted.");}catch(UnauthorizedAccessException){}
        try{saved.Check("run_shell");throw new Exception("Disabled tool permitted.");}catch(UnauthorizedAccessException){}
        CodeProjectPolicy.Set(host,root,policy with {Expires=DateTimeOffset.UtcNow.AddMinutes(-1)});
        if(CodeProjectPolicy.Load(host,root)!=null)throw new Exception("Expired policy remained active.");
        CodeProjectPolicy.Set(host,root,null);
        var docs=Directory.CreateDirectory(Path.Combine(area,"sdk")).FullName;
        File.WriteAllText(Path.Combine(docs,"guide.md"),"# Units\nRetain the revision before editing.");
        var server=Path.Combine(host,"server.cjs");
        File.WriteAllText(server,"""
            const io=require('node:readline').createInterface({input:process.stdin});
            io.on('line',line=>{
              const m=JSON.parse(line);if(m.id===undefined)return;
              const values={'initialize':{protocolVersion:'2025-06-18'},'resources/list':{resources:[{uri:'sdk://guide'},{uri:'sdk://private'}]},'resources/read':{contents:[{uri:'sdk://guide',text:'captured documentation'}]},'tools/list':{tools:[{name:'allowed'},{name:'excluded'}]},'tools/call':{content:[{type:'text',text:'done'}]}};
              console.log(JSON.stringify({jsonrpc:'2.0',id:m.id,result:values[m.method]}));
            });
            """);
        var node=CodeCommand.ResolveExecutable("node",root);
        using(var owner=OwnedModelProcess.Start(node,["-e","setInterval(()=>{},1000)"],Path.Combine(host,"lease.log")))
        using(var child=System.Diagnostics.Process.GetProcessById(owner.Process.Id))
        {
            using(var lease=OwnedModelProcess.RetainForTask(owner.LifetimeId))
            {
                owner.Dispose();
                if(child.WaitForExit(250))throw new Exception("The attached task lost its model on desktop close.");
            }
            if(!child.WaitForExit(10000))throw new Exception("A model survived its last task lease.");
        }
        Console.WriteLine("PASS owned model survives the desktop closing until the final task lease is released");
        var bundle=new HarnessBundle("qa","1.0.0",new(){["convention"]="Use the existing result type."},
            [new("sdk","",["allowed"],node,[server],["sdk://guide"])],[new("manual","3.0",docs)]);
        CodeBundles.Save(host,bundle,false);
        if(CodeMcp.Read(host).Length>0)throw new Exception("Disabled bundle exposed executable.");
        CodeBundles.Save(host,bundle,true);
        var registration=CodeMcp.Read(host).Single();
        var tools=await CodeMcp.Request(registration,null,default,default);
        var resources=await CodeMcp.Request(registration,null,default,default,resources:true);
        var data=await CodeMcp.Request(registration,null,default,default,uri:"sdk://guide");
        if(!tools.Contains("allowed")||tools.Contains("excluded")||!resources.Contains("sdk://guide")||resources.Contains("private")||!data.Contains("captured documentation"))throw new Exception("Stdio handshake, allowlists or resource reads failed.");
        try{await CodeMcp.Request(registration,null,default,default,uri:"sdk://private");throw new Exception("Unregistered resource permitted.");}catch(UnauthorizedAccessException){}
        var citation=JsonSerializer.SerializeToElement(await CodeBundles.ReadDocs(bundle.Docs[0],"guide.md","revision",_=>throw new Exception("Local docs used network"),default));
        if(citation.GetProperty("line").GetInt32()!=2||!citation.GetProperty("excerpt").GetString()!.Contains("before editing"))throw new Exception("Documentation lost the original citation.");
        await using(var services=new CodeRunServices(scope,default,[],host)){
            if(services.Skill("convention")!="Use the existing result type.")throw new Exception("Registered skill unavailable.");
            CodeBundles.Save(host,bundle,false);
            if(services.Skills.Contains("convention"))throw new Exception("Disabled skill remained accessible.");
        }
        CodeBundles.Remove(host,"qa");
        using var input=new CodeTerminalInput();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var shell=CodeCommand.Run(node,["-e","process.stdin.setEncoding('utf8');process.stdin.on('data',s=>{console.log('received '+s.trim());process.exit(0)});"],root,timeout.Token,root,terminal:input);
        input.Send("sandbox input");
        var run=await shell;
        if(run.ExitCode!=0||!run.Output.Contains("received sandbox input"))throw new Exception("Live sandbox terminal failed: "+run.Output);
        Console.WriteLine("PASS project policy expiry and revocation, package lifecycle, live stdio MCP with restricted discovery, offline doc citations and sandboxed live stdin");
    }
}
