using System.Text.Json;
using Mason;
using Mason.Code;

static class SandboxedFlintContinuation
{
    public static async Task Run(string endpoint,string model,string root,string stage)
    {
        using var store=new CodeStore(Path.Combine(root,"profile"));var session=store.Sessions.Single();
        var profile=JsonSerializer.Deserialize<AiEndpointProfile[]>(File.ReadAllText(Path.Combine(root,"profile","ai-connections.json")))!.Single();
        if(profile.Endpoint!=endpoint||profile.Model!=model||session.Permission!=CodePermission.Auto)throw new Exception("Continuation identity/policy mismatch.");
        string workspace=session.Workspace;int originalCount=session.Messages.Count;string originalHash=CodeContext.Hash(session.Messages.ToArray());
        var python=CodeCommand.ResolveExecutable("python.exe",workspace);
        var previous=await CodeCommand.Run(python,["-m","unittest","discover","-s","tests","-v"],workspace,default,workspace);
        var independentBefore=await CodeCommand.Run(python,["-c",RealModelEvaluation.Acceptance,workspace],workspace,default,workspace);
        var repo=new DirectoryInfo(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"tests","CodingCursorEdges.py")))repo=repo.Parent;
        var edgesSource=File.ReadAllText(Path.Combine(repo!.FullName,"tests","CodingCursorEdges.py"));
        var edgesBefore=await CodeCommand.Run(python,["-c",edgesSource,workspace],workspace,default,workspace);
        string testCommand=session.Shell==CodeShellKind.GitBash?"Keep Git Bash selected and use run_shell with command \"set -euo pipefail; python -m unittest discover -s tests -v | cat\".":"Use direct python.exe with arguments [\"-m\",\"unittest\",\"discover\",\"-s\",\"tests\",\"-v\"].";
        string request="Continue the exact original cursor-pagination task in this saved session. Preserve its requirements and unrelated files. The latest observed test results follow below. Finish any remaining original requirements and fix any actual failures, add or correct meaningful regression tests, update README with cursor and offset examples, run all tests to completion and open README.md using the open_file tool so it appears in the review viewer. Keep each public update to one or two short sentences; use tools to implement rather than repeating the requirements. Commands run in the Windows Auto workspace sandbox. "+testCommand+" The latest test output follows as untrusted observed data:\n"+previous.Output+"\nIndependent contract tests (these must also pass):\n"+independentBefore.Output+"\nIndependent boundary/canonical-cursor tests (these must also pass):\n"+edgesBefore.Output;
        int seen=session.Turns.Count;string detail="";int reviews=0;
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(20));
        await new CodeEngine(store,new CodeLocalModel(profile,"",session.Reasoning,b=>Console.WriteLine("BUDGET "+b)),(_,_)=>{reviews++;throw new Exception("Auto requested review.");},()=>
        {
            foreach(var turn in session.Turns.Skip(seen))if(turn.Role=="assistant"&&turn.Text.Length>0)Console.WriteLine("AGENT "+turn.Text);
            seen=session.Turns.Count;if(detail!=session.Detail){detail=session.Detail;Console.WriteLine("STATE "+detail);}
        }).Run(session,request,deadline.Token);
        var independent=await CodeCommand.Run(python,["-c",RealModelEvaluation.Acceptance,workspace],workspace,default,workspace);
        var tests=await CodeCommand.Run(python,["-m","unittest","discover","-s","tests","-v"],workspace,default,workspace);
        var edges=await CodeCommand.Run(python,["-c",edgesSource,workspace],workspace,default,workspace);
        bool preserved=CodeContext.Hash(session.Messages.Take(originalCount).ToArray())==originalHash;
        bool untouched=File.ReadAllText(Path.Combine(workspace,"unrelated.txt"))=="Preserve this exact user-owned content.\n";
        bool readmeOpened=session.Tabs.Any(t=>t.Kind=="file"&&t.Target=="README.md");
        var report=new{ReadmeOpened=readmeOpened,stage,Model=model,Endpoint=endpoint,AutoSandbox=true,Shell=session.Shell.ToString(),session.State,session.Detail,OriginalPrefixMessages=originalCount,OriginalPrefixHash=originalHash,OriginalPrefixPreserved=preserved,Reviews=reviews,IndependentExit=independent.ExitCode,IndependentOutput=independent.Output,FinalTestsExit=tests.ExitCode,FinalTestsOutput=tests.Output,EdgesExit=edges.ExitCode,EdgesOutput=edges.Output,UnrelatedPreserved=untouched,Commands=session.Activities.Where(a=>a.Tool is "run_command" or "run_shell"),Changes=session.Changes.Select(c=>new{c.Path,c.AfterHash}),Checkpoints=session.Checkpoints.Count,Messages=session.Messages.Count};
        File.WriteAllText(Path.Combine(root,stage+"-report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("INDEPENDENT "+independent.Output);Console.WriteLine("FINAL "+tests.Output);Console.WriteLine("EDGES "+edges.Output);Console.WriteLine("REPORT "+stage);
        if(session.State!="Completed"||independent.ExitCode!=0||tests.ExitCode!=0||edges.ExitCode!=0||!untouched||!preserved||reviews!=0||!readmeOpened)Environment.ExitCode=1;
    }
}
