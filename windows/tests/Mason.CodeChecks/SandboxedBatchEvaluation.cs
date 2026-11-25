using System.Text.Json;
using Mason;
using Mason.Code;

static class SandboxedBatchEvaluation
{
    const string Request = """
        Extend this existing Windows Command Prompt version utility end to end. Preserve `version.cmd --version`, which prints 1.0.0 with exit 0. Add `version.cmd compare A B`: A and B must each have exactly three nonempty decimal components in 0..999999; leading zeros are allowed and must be interpreted as decimal, including 08/09. Compare major, minor and patch numerically, printing exactly -1, 0 or 1 followed by newline, with exit 0. Reject missing/extra arguments, negative numbers, letters, missing or extra components and components above 999999 with exit 2 and a nonempty error/usage message. No partial comparison result on invalid input. No dependencies: use CMD builtins only, no PowerShell, Python, Node, external executables, installations or network.
        Inspect the repository first. Implement meaningful regression tests in tests.cmd that invoke the actual utility and check results/exit codes, document usage and validation rules in README.md, run `call tests.cmd` with the selected Command Prompt shell, fix failures and open README.md for review. Preserve unrelated.txt exactly. Keep public updates short. Commands execute in the strict Auto sandbox; unsupported interpreters must not be retried. Finish with actual test evidence.
        """;
    public static async Task Run(string endpoint,string model,string destination,bool resume=false,string? reasoning=null)
    {
        var root=Path.GetFullPath(destination);var workspace=Path.Combine(root,"version-tool");var profileRoot=Path.Combine(root,"profile");
        if(!resume)
        {
            if(Directory.Exists(root))throw new IOException("Use a fresh evaluation directory.");Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace,"version.cmd"),"@echo off\r\nsetlocal\r\nif \"%~1\"==\"--version\" (\r\n echo 1.0.0\r\n exit /b 0\r\n)\r\n>&2 echo Usage: version.cmd --version\r\nexit /b 2\r\n");
            File.WriteAllText(Path.Combine(workspace,"README.md"),"# Version utility\n\nRun `version.cmd --version` in Command Prompt. No dependencies.\n");
            File.WriteAllText(Path.Combine(workspace,"AGENTS.md"),"Use Command Prompt builtins only. Preserve legacy --version, unrelated.txt and exact stdout/exit-code contracts. Use call when invoking another batch file. Run real tests; never claim an unexecuted test passed.\n");
            File.WriteAllText(Path.Combine(workspace,"unrelated.txt"),"Preserve these exact user-owned bytes.\n");
            File.WriteAllText(Path.Combine(root,"request.txt"),Request);
        }
        using var store=new CodeStore(profileRoot);var session=resume?store.Sessions.Single():store.New(workspace);
        AiEndpointProfile profile;
        if(resume)profile=JsonSerializer.Deserialize<AiEndpointProfile[]>(File.ReadAllText(Path.Combine(profileRoot,"ai-connections.json")))!.Single();
        else
        {
            profile=new AiEndpointProfile{Name="Flint · strict Auto CMD evaluation",Model=model,Endpoint=endpoint,ManagedCapability=AiCapability.Chat};
            session.Model=profile.Id;session.Permission=CodePermission.Auto;session.Shell=CodeShellKind.CommandPrompt;session.Reasoning="off";store.Save(session);
            File.WriteAllText(Path.Combine(profileRoot,"ai-connections.json"),JsonSerializer.Serialize(new[]{profile},new JsonSerializerOptions{WriteIndented=true}));
        }
        session.ShellExecutable=CodeShells.Find(CodeShellKind.CommandPrompt);
        session.Reasoning=reasoning??(resume?"default":"off");store.Save(session);
        int originalMessages=session.Messages.Count;string originalHash=CodeContext.Hash(session.Messages.ToArray());
        var before=await Accept(workspace);string request=resume?"Continue the exact version-tool task. The host CMD runner has now been corrected to preserve literal double quotes in /c scripts (it formerly used incorrect CRT escaping). Quoted commands now pass real regression tests. Read the existing broken source and implement a complete valid utility, then actually run tests. The previous malformed-control-character and no-op edits were rejected. The current version.cmd still has invalid syntax. Read it, then implement a complete valid CMD utility using correctly escaped JSON strings for double quotes and newlines; no placeholders, partial drafts or repeated identical replacements. These independent checks still fail; fix the implementation, add regressions to tests.cmd, run it, update README and open README. Preserve unrelated.txt. Keep public messages short. Observed untrusted test data:\n"+string.Join('\n',before.Where(r=>!r.Pass).Select(r=>$"{r.Arguments}: expected exit {r.ExpectedExit}, stdout {r.ExpectedOutput}; actual exit {r.ExitCode}, output {r.Output}")):Request;
        int seen=session.Turns.Count,reviews=0;string detail="";using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(20));
        if(resume&&reasoning!=null)request="Continue the original version utility task. Your first run reached the bounded 24-action limit after researching CMD behavior. The original version.cmd is still unchanged and tests.cmd is missing. Use the preserved probes/receipts; stop exploratory probes and implement the complete utility, regression tests and documentation now. Run call tests.cmd, fix failures, and open README.md. Do not claim success without passing real command output. Preserve unrelated.txt. Original contract:\n"+Request;
        using(var client=new LocalAiClient())File.WriteAllText(Path.Combine(root,"server-identity.json"),JsonSerializer.Serialize(await client.ChatServer(profile,"",deadline.Token),new JsonSerializerOptions{WriteIndented=true}));
        await new CodeEngine(store,new CodeLocalModel(profile,"",session.Reasoning,b=>Console.WriteLine("BUDGET "+b)),(_,_)=>{reviews++;throw new Exception("Auto unexpectedly requested review.");},()=>
        {
            foreach(var turn in session.Turns.Skip(seen))if(turn.Role=="assistant"&&turn.Text.Length>0)Console.WriteLine("AGENT "+turn.Text);
            seen=session.Turns.Count;if(detail!=session.Detail){detail=session.Detail;Console.WriteLine("STATE "+detail);}
        },error=>File.WriteAllText(Path.Combine(root,"harness-error.txt"),error.ToString())).Run(session,request,deadline.Token);
        var cases=await Accept(workspace);var cmd=CodeCommand.ResolveExecutable("cmd.exe",workspace);
        var project=File.Exists(Path.Combine(workspace,"tests.cmd"))?await CodeCommand.Run(cmd,["/d","/c","call tests.cmd"],workspace,default,workspace):new CodeCommandResult(-1,"Missing tests.cmd",false);
        bool untouched=File.ReadAllText(Path.Combine(workspace,"unrelated.txt"))=="Preserve these exact user-owned bytes.\n";
        bool history=CodeContext.Hash(session.Messages.Take(originalMessages).ToArray())==originalHash;
        bool opened=session.Tabs.Any(t=>t.Kind=="file"&&t.Target=="README.md");
        bool passed=session.State=="Completed"&&cases.All(c=>c.Pass)&&project.ExitCode==0&&untouched&&history&&opened&&reviews==0&&session.Activities.Any(a=>a.Tool is "run_command" or "run_shell"&&a.State=="Completed");
        var stage=resume?"continuation-"+DateTimeOffset.UtcNow.ToString("HHmmss"):"initial";
        var report=new{Passed=passed,Stage=stage,Reasoning=session.Reasoning,Model=model,Endpoint=endpoint,AutoSandbox=true,session.Id,session.State,session.Detail,IndependentPassed=cases.Count(c=>c.Pass),IndependentTotal=cases.Count,Cases=cases,ProjectExit=project.ExitCode,ProjectOutput=project.Output,UnrelatedPreserved=untouched,OriginalHistoryPreserved=history,ReadmeOpened=opened,Reviews=reviews,Commands=session.Activities.Where(a=>a.Tool is "run_shell" or "run_command"),Changes=session.Changes.Select(c=>new{c.Path,c.AfterHash}),Performance=session.Turns.Where(t=>!string.IsNullOrEmpty(t.PerformanceSummary)).Select(t=>t.PerformanceSummary)};
        File.WriteAllText(Path.Combine(root,stage+"-report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        foreach(var test in cases)Console.WriteLine($"{(test.Pass?"PASS":"FAIL")} {test.Arguments}: {test.ExitCode} {test.Output}");Console.WriteLine($"PROJECT {project.ExitCode}: {project.Output}");Console.WriteLine($"REPORT {passed}: {Path.Combine(root,stage+"-report.json")}");if(!passed)Environment.ExitCode=1;
    }
    record Case(string Arguments,int ExpectedExit,string? ExpectedOutput,int ExitCode,string Output,bool Pass);
    static async Task<List<Case>> Accept(string workspace)
    {
        var cmd=CodeCommand.ResolveExecutable("cmd.exe",workspace);var cases=new List<Case>();
        var tests=new (string Args,int Exit,string? Text)[]{("--version",0,"1.0.0"),("compare 1.2.3 1.2.3",0,"0"),("compare 1.2.3 1.2.4",0,"-1"),("compare 1.2.4 1.2.3",0,"1"),("compare 2.0.0 1.999999.999999",0,"1"),("compare 1.10.0 1.2.999",0,"1"),("compare 1.0.0 2.0.0",0,"-1"),("compare 000008.000009.000010 8.9.10",0,"0"),("compare 0.0.0 0.0.1",0,"-1"),("compare 999999.999999.999999 999999.999999.999999",0,"0"),("compare 1.2.3",2,null),("compare 1.2.3 1.2.3 extra",2,null),("compare 1.2 1.2.3",2,null),("compare 1.2.3.4 1.2.3",2,null),("compare 1..3 1.2.3",2,null),("compare -1.2.3 1.2.3",2,null),("compare a.b.c 1.2.3",2,null),("compare 1000000.0.0 1.2.3",2,null),("compare 1.2.3 1.2.-1",2,null),("compare 1.2.3 1.2.3x",2,null),("compare \"\" 1.2.3",2,null),("",2,null),("unknown",2,null)};
        foreach(var test in tests){var r=await CodeCommand.Run(cmd,["/d","/c","call version.cmd "+test.Args],workspace,default,workspace);string text=r.Output.Trim();cases.Add(new(test.Args,test.Exit,test.Text,r.ExitCode,text,r.ExitCode==test.Exit&&(test.Text==null?!string.IsNullOrWhiteSpace(text)&&text is not("-1" or "0" or "1"):text==test.Text)));}
        return cases;
    }
}
