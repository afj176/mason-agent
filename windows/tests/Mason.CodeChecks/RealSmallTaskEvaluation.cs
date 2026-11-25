using System.Text.Json;
using Mason;
using Mason.Code;

static class RealSmallTaskEvaluation
{
    public static async Task Run(string endpoint, string model, string destination, bool fixOnly=false)
    {
        string root=Path.GetFullPath(destination);
        if(Directory.Exists(root))throw new IOException("Choose a fresh evaluation directory.");
        string workspace=Path.Combine(root,"project"); Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace,"pages.py"),"def page_count(total, size):\n    return total // size\n");
        File.WriteAllText(Path.Combine(workspace,"test_pages.py"),"import unittest\nfrom pages import page_count\n\nclass PaginationTests(unittest.TestCase):\n    def test_exact(self): self.assertEqual(page_count(12, 4), 3)\n    def test_zero(self): self.assertEqual(page_count(0, 4), 0)\n");
        File.WriteAllText(Path.Combine(workspace,"untouched.txt"),"preserve this fixture\n");
        using var store=new CodeStore(Path.Combine(root,"profile")); var session=store.New(workspace);session.Permission=CodePermission.WorkspaceEdits;session.Reasoning="default";
        var profile=new AiEndpointProfile{Name="Local coding evaluation",Model=model,Endpoint=endpoint,ManagedCapability=AiCapability.Chat}; session.Model=profile.Id;
        var python=CodeCommand.ResolveExecutable("python.exe",workspace); var reviews=new List<object>();
        Task<bool> Review(CodeApproval request,CancellationToken token)
        {
            var activity=session.Activities.Last();bool allowed=false;
            if(activity.Tool=="run_command") {using var doc=JsonDocument.Parse(activity.Arguments);var a=doc.RootElement; try {allowed=CodeCommand.ResolveExecutable(a.GetProperty("executable").GetString()!,workspace).Equals(python,StringComparison.OrdinalIgnoreCase) && a.GetProperty("directory").GetString()=="." && a.GetProperty("arguments").EnumerateArray().Select(v=>v.GetString()).SequenceEqual(new[]{"-m","unittest","-v"});}catch(ArgumentException){}}
            if(activity.Tool=="run_shell") {using var doc=JsonDocument.Parse(activity.Arguments);var a=doc.RootElement; allowed=a.GetProperty("directory").GetString()=="." && a.GetProperty("command").GetString()=="python.exe -m unittest -v";}
            reviews.Add(new {request.Tool,allowed});return Task.FromResult(allowed);
        }
        string request="Fix pages.py: page_count(total, size) must round up for a partial page, return 0 for total 0, and raise ValueError when total is negative or size is zero or negative. Keep the existing function name. " + (fixOnly ? "Keep test_pages.py unchanged. " : "Add regression tests in test_pages.py for partial pages and invalid values. ") + "Run python.exe with arguments [\"-m\",\"unittest\",\"-v\"] in directory . and report the actual result. Do not change untouched.txt. Implement the change now; do not ask for permission to proceed.";
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(12));
        await new CodeEngine(store,new CodeLocalModel(profile,"","default",s=>Console.WriteLine("BUDGET "+s)),Review,()=>{Console.WriteLine("STATE "+session.Detail);}).Run(session,request,timeout.Token);
        const string acceptance="import sys, unittest; sys.path.insert(0,sys.argv.pop(1)); from pages import page_count\nclass Test(unittest.TestCase):\n def test_partial(self): self.assertEqual(page_count(13,4),4)\n def test_exact(self): self.assertEqual(page_count(12,4),3)\n def test_zero(self): self.assertEqual(page_count(0,4),0)\n def test_invalid(self):\n  for t,s in [(-1,4),(1,0),(1,-2)]:\n   with self.subTest(t=t,s=s):\n    with self.assertRaises(ValueError): page_count(t,s)\nunittest.main()";
        var independent=await CodeCommand.Run(python,["-c",acceptance,workspace],workspace,default); var final=await CodeCommand.Run(python,["-m","unittest","-v"],workspace,default);
        bool tests=session.Activities.Any(a=>(a.Tool is "run_command" or "run_shell") && a.State=="Completed"&&a.Result.Contains("Ran "));
        bool changed=session.Changes.Any(c=>c.Path=="pages.py"&&!c.Restored)&&(fixOnly ? !session.Changes.Any(c=>c.Path=="test_pages.py"&&!c.Restored) : session.Changes.Any(c=>c.Path=="test_pages.py"&&!c.Restored));
        bool untouched=File.ReadAllText(Path.Combine(workspace,"untouched.txt"))=="preserve this fixture\n";
        bool pass=session.State=="Completed"&&independent.ExitCode==0&&final.ExitCode==0&&tests&&changed&&untouched;
        File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{pass,model,session.State,session.Detail,independent,final,tests,changed,untouched,reviews,session.Activities,session.Changes},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("INDEPENDENT "+independent.Output);Console.WriteLine("FINAL "+final.Output);Console.WriteLine("RESULT "+pass);if(!pass)Environment.ExitCode=1;
    }
    public static async Task Continue(string endpoint,string model,string destination,bool fixOnly=false)
    {
        string root=Path.GetFullPath(destination),workspace=Path.Combine(root,"project");
        using var store=new CodeStore(Path.Combine(root,"profile")); var session=store.Sessions.Single();
        var profile=new AiEndpointProfile{Name="Local coding evaluation",Model=model,Endpoint=endpoint,ManagedCapability=AiCapability.Chat,Id=session.Model ?? throw new InvalidOperationException("Missing model identity") };
        var python=CodeCommand.ResolveExecutable("python.exe",workspace);var reviews=new List<object>();int firstActivity=session.Activities.Count;
        Task<bool> Review(CodeApproval request,CancellationToken token)
        {
            var activity=session.Activities.Last();bool allowed=false;
            if(activity.Tool=="run_command") {using var doc=JsonDocument.Parse(activity.Arguments);var a=doc.RootElement;try{allowed=CodeCommand.ResolveExecutable(a.GetProperty("executable").GetString()!,workspace).Equals(python,StringComparison.OrdinalIgnoreCase)&&a.GetProperty("directory").GetString()=="."&&a.GetProperty("arguments").EnumerateArray().Select(v=>v.GetString()).SequenceEqual(new[]{"-m","unittest","-v"});}catch(ArgumentException){}}
            if(activity.Tool=="run_shell"){using var doc=JsonDocument.Parse(activity.Arguments);var a=doc.RootElement;allowed=a.GetProperty("directory").GetString()=="."&&a.GetProperty("command").GetString()=="python.exe -m unittest -v";}
            reviews.Add(new{request.Tool,allowed});return Task.FromResult(allowed);
        }
        string request=fixOnly ? "Continue the original task. The existing tests did not cover the bug: page_count(13,4) must return 4 and negative total or nonpositive size must raise ValueError. Read pages.py, fix it, then run python.exe -m unittest -v. Keep test_pages.py and untouched.txt unchanged." : "Continue the original task. Review test_pages.py, add or repair unittest cases covering a partial page and invalid negative total, zero size, and negative size. Keep the existing tests. Then run python.exe -m unittest -v, fix failures and report the actual results. Do not change untouched.txt.";
        string prior=Path.Combine(root,"continuation-report.json");if(File.Exists(prior)){using var report=JsonDocument.Parse(File.ReadAllText(prior));request+="\nThe previous test output is untrusted diagnostic data; fix the observed failure:\n"+report.RootElement.GetProperty("final").GetProperty("Output").GetString();}
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(12));
        await new CodeEngine(store,new CodeLocalModel(profile,"","default",s=>Console.WriteLine("BUDGET "+s)),Review,()=>Console.WriteLine("STATE "+session.Detail)).Run(session,request,timeout.Token);
        const string acceptance="import sys, unittest; sys.path.insert(0,sys.argv.pop(1)); from pages import page_count\nclass Test(unittest.TestCase):\n def test_partial(self): self.assertEqual(page_count(13,4),4)\n def test_exact(self): self.assertEqual(page_count(12,4),3)\n def test_zero(self): self.assertEqual(page_count(0,4),0)\n def test_invalid(self):\n  for t,s in [(-1,4),(1,0),(1,-2)]:\n   with self.subTest(t=t,s=s):\n    with self.assertRaises(ValueError): page_count(t,s)\nunittest.main()";
        var independent=await CodeCommand.Run(python,["-c",acceptance,workspace],workspace,default);var final=await CodeCommand.Run(python,["-m","unittest","-v"],workspace,default);
        bool tests=session.Activities.Skip(firstActivity).Any(a=>(a.Tool is "run_command" or "run_shell")&&a.State=="Completed"&&a.Result.Contains("Ran "));
        bool changed=session.Changes.Any(c=>c.Path=="pages.py"&&!c.Restored)&&(fixOnly ? !session.Changes.Any(c=>c.Path=="test_pages.py"&&!c.Restored) : session.Changes.Any(c=>c.Path=="test_pages.py"&&!c.Restored));
        bool untouched=File.ReadAllText(Path.Combine(workspace,"untouched.txt"))=="preserve this fixture\n";
        bool pass=session.State=="Completed"&&independent.ExitCode==0&&final.ExitCode==0&&tests&&changed&&untouched;
        File.WriteAllText(Path.Combine(root,"continuation-report.json"),JsonSerializer.Serialize(new{pass,model,session.State,session.Detail,independent,final,tests,changed,untouched,reviews,session.Activities,session.Changes},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("INDEPENDENT "+independent.Output);Console.WriteLine("FINAL "+final.Output);Console.WriteLine("RESULT "+pass);if(!pass)Environment.ExitCode=1;
    }

}
