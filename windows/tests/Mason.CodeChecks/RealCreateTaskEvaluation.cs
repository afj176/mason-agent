using System.Text.Json;
using Mason;
using Mason.Code;

static class RealCreateTaskEvaluation
{
    public static async Task Run(string endpoint,string model,string destination,bool auto=false)
    {
        string root=Path.GetFullPath(destination);if(Directory.Exists(root))throw new IOException("Choose a fresh evaluation directory.");
        string workspace=Path.Combine(root,"project");Directory.CreateDirectory(workspace);
        const string tests="import unittest\nfrom ranges import clamp\n\nclass ClampTests(unittest.TestCase):\n    def test_inside(self): self.assertEqual(clamp(4, 0, 10), 4)\n    def test_limits(self):\n        self.assertEqual(clamp(-2, 0, 10), 0)\n        self.assertEqual(clamp(15, 0, 10), 10)\n    def test_equal_limits(self): self.assertEqual(clamp(9, 3, 3), 3)\n    def test_invalid_limits(self):\n        with self.assertRaises(ValueError): clamp(4, 10, 0)\n";
        File.WriteAllText(Path.Combine(workspace,"test_ranges.py"),tests);File.WriteAllText(Path.Combine(workspace,"untouched.txt"),"preserve this fixture\n");
        using var store=new CodeStore(Path.Combine(root,"profile"));var session=store.New(workspace);session.Permission=auto ? CodePermission.Auto : CodePermission.WorkspaceEdits;session.Reasoning="default";
        var profile=new AiEndpointProfile{Name="Local coding evaluation",Model=model,Endpoint=endpoint,ManagedCapability=AiCapability.Chat};session.Model=profile.Id;
        var python=CodeCommand.ResolveExecutable("python.exe",workspace);var reviews=new List<object>();
        Task<bool> Review(CodeApproval request,CancellationToken cancellation)
        {
            if(auto)throw new InvalidOperationException("Auto unexpectedly requested review.");
            var activity=session.Activities.Last();bool allowed=false;using var doc=JsonDocument.Parse(activity.Arguments);var a=doc.RootElement;
            if(activity.Tool=="run_command")try{allowed=CodeCommand.ResolveExecutable(a.GetProperty("executable").GetString()!,workspace).Equals(python,StringComparison.OrdinalIgnoreCase)&&a.GetProperty("directory").GetString()=="."&&a.GetProperty("arguments").EnumerateArray().Select(v=>v.GetString()).SequenceEqual(new[]{"-m","unittest","-v"});}catch(ArgumentException){}
            if(activity.Tool=="run_shell")allowed=a.GetProperty("directory").GetString()=="."&&a.GetProperty("command").GetString()=="python.exe -m unittest -v";
            reviews.Add(new{request.Tool,allowed});return Task.FromResult(allowed);
        }
        const string request="Create ranges.py with a function clamp(value, minimum, maximum). Return value when it is within the inclusive bounds, the minimum when below it, and the maximum when above it. Raise ValueError if minimum is greater than maximum. Implement the file first, then run python.exe -m unittest -v in directory . and report the actual result. Preserve test_ranges.py and untouched.txt. Do not ask to proceed.";
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(12));
        await new CodeEngine(store,new CodeLocalModel(profile,"","default",s=>Console.WriteLine("BUDGET "+s)),Review,()=>Console.WriteLine("STATE "+session.Detail)).Run(session,request,timeout.Token);
        const string acceptance="import sys,unittest; sys.path.insert(0,sys.argv.pop(1)); from ranges import clamp\nclass T(unittest.TestCase):\n def test_inside(self): self.assertEqual(clamp(2,-5,5),2)\n def test_bounds(self): self.assertEqual(clamp(-9,-5,5),-5); self.assertEqual(clamp(9,-5,5),5)\n def test_equal(self): self.assertEqual(clamp(8,2,2),2)\n def test_invalid(self):\n  with self.assertRaises(ValueError): clamp(0,9,1)\nunittest.main()";
        var independent=await CodeCommand.Run(python,["-c",acceptance,workspace],workspace,default,auto?workspace:null);var final=await CodeCommand.Run(python,["-m","unittest","-v"],workspace,default,auto?workspace:null);
        bool testsRun=session.Activities.Any(a=>(a.Tool is "run_command" or "run_shell")&&a.State=="Completed"&&a.Result.Contains("Ran "));
        bool created=session.Changes.Any(c=>c.Path=="ranges.py"&&!c.Restored);
        bool untouched=File.ReadAllText(Path.Combine(workspace,"untouched.txt"))=="preserve this fixture\n"&&File.ReadAllText(Path.Combine(workspace,"test_ranges.py"))==tests;
        bool pass=session.State=="Completed"&&independent.ExitCode==0&&final.ExitCode==0&&testsRun&&created&&untouched;
        File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{pass,model,auto,session.State,session.Detail,independent,final,testsRun,created,untouched,reviews,session.Activities,session.Changes},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("INDEPENDENT "+independent.Output);Console.WriteLine("FINAL "+final.Output);Console.WriteLine("RESULT "+pass);if(!pass)Environment.ExitCode=1;
    }
}
