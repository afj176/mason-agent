using System.Text.Json;
using System.Diagnostics;
using Mason.Code;

static class QuiescenceChecks
{
    public static async Task Run(string destination,bool denied=false,bool cancel=false)
    {
#if MASON_BARRIER_QA
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        string root=Path.GetFullPath(destination);if(Directory.Exists(root))throw new IOException("Choose a new quiescence fixture.");Directory.CreateDirectory(root);
        var source=Path.Combine(Environment.GetEnvironmentVariable("MASON_QUIESCENCE_PROBE")??throw new InvalidOperationException("Build the native quiescence probe and set its directory."),"QuiescenceProbe.exe");
        var executable=Path.Combine(root,"QuiescenceProbe.exe");File.Copy(source,executable);
        if(cancel)
        {
            // This fixture cancels an actual held operation. Cold runtime
            // staging is a separate phase, not part of its readiness window;
            // Run still independently verifies the prepared runtime again.
            using var preparation=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            CodeSandboxTools.Prepare(executable,root,preparation.Token);
        }
        var before=Environment.GetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS");Environment.SetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS","1");
        CodeCommandResult? result=null;bool cancellationObserved=false,stopped=false;
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(110));
        try
        {
            var running=CodeCommand.Run(executable,denied?["denied"]:cancel?["cancel"]:[],root,cancellation.Token,root);
            if(cancel)
            {
                try
                {
                    var ready=Path.Combine(root,"quiescence-hold-ready.json");var watch=Stopwatch.StartNew();
                    while(!File.Exists(ready)&&!running.IsCompleted&&watch.Elapsed<TimeSpan.FromSeconds(20))await Task.Delay(25);
                    if(!File.Exists(ready))throw new InvalidOperationException("The command never reached its held cancellation point.");
                    var pids=JsonSerializer.Deserialize<uint[]>(File.ReadAllText(ready))!;
                    var owned=pids.Select(p=>Process.GetProcessById((int)p)).ToArray();
                    try
                    {
                        if(owned.Length<2||owned.Any(p=>p.HasExited))throw new InvalidOperationException("Held cancellation requires live owned actors.");
                        cancellation.Cancel();try{result=await running;}catch(OperationCanceledException){cancellationObserved=true;}
                        stopped=owned.All(p=>p.WaitForExit(3000));
                    }
                    finally{foreach(var process in owned)process.Dispose();}
                }
                finally{cancellation.Cancel();try{await running;}catch(OperationCanceledException){} }
            }
            else result=await running;
        }
        finally{Environment.SetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS",before);}
        var canary=Path.Combine(root,"quiescence-mapped-canary.bin");bool untouched=File.Exists(canary)&&File.ReadAllBytes(canary).Length==65536&&File.ReadAllBytes(canary).All(b=>b==0x41);
        bool passed=untouched&&(cancel?cancellationObserved&&stopped:denied?result is {ExitCode:not 0,Truncated:false}&&result.Output.Contains("HOLD_DENIED ",StringComparison.Ordinal):
            result is {ExitCode:0,Truncated:false}&&result.Output.Contains("HOLD_WRITES ",StringComparison.Ordinal)&&result.Output.Contains("QUIESCENCE ",StringComparison.Ordinal)&&result.Output.Contains("\"ExplicitHoldsStopAll\":true",StringComparison.Ordinal)&&result.Output.Contains("\"PriorSuspensionPreserved\":true",StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{Passed=passed,DeniedThreadFixture=denied,CancellationFixture=cancel,CancellationObserved=cancellationObserved,AllHeldProcessesStopped=stopped,MappedCanaryUnchanged=untouched,Command=result},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(result?.Output??JsonSerializer.Serialize(new{cancellationObserved,stopped,untouched}));if(!passed)throw new Exception("Owned quiescence prerequisite failed.");
#else
        await Task.CompletedTask;throw new InvalidOperationException("Use the explicit quiescence QA build.");
#endif
    }
}
