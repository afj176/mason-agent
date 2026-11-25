using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Mason.Code;

[SupportedOSPlatform("windows")]
static class HandoffStressChecks
{
    public static async Task Run(string workspace,string destination,int count,int concurrency,int batches=1,bool continuation=false)
    {
#if MASON_HANDOFF_QA
        if(count is <1 or >1000||concurrency is <1 or >8||batches is <1 or >4)throw new ArgumentOutOfRangeException(nameof(count));
        if(continuation&&(batches>2||!File.Exists(Path.Combine(workspace,"ContextProbe-continuation.dll"))))throw new InvalidOperationException("Select the owned continuation fixture and at most two fork batches.");
        workspace=Path.GetFullPath(workspace);destination=Path.GetFullPath(destination);
        if(Directory.Exists(destination))throw new IOException("Choose a new stress report directory.");
        Directory.CreateDirectory(destination);
        var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new Exception("Git Bash is required.");
        string Acl()=>new DirectoryInfo(workspace).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var initialAcl=Acl();var identities=new ConcurrentDictionary<string,byte>();
        var samples=new ConcurrentBag<double>();int next=0,completed=0;long peakHandles=0,peakPrivate=0;
        using var host=Process.GetCurrentProcess();host.Refresh();int initialHandles=host.HandleCount;long initialPrivate=host.PrivateMemorySize64;
        using var stop=new CancellationTokenSource();var elapsed=Stopwatch.StartNew();
        var diagnostics=Environment.GetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS");
        Environment.SetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS","2");
        try
        {
            await Task.WhenAll(Enumerable.Range(0,concurrency).Select(async _=>
            {
                while(!stop.IsCancellationRequested)
                {
                    int attempt=Interlocked.Increment(ref next);if(attempt>count)return;
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(90));
                    var watch=Stopwatch.StartNew();CodeCommandResult? command=null;
                    try
                    {
                        string trigger=continuation?"perl -e 'DynaLoader::boot_DynaLoader(\"DynaLoader\"); my $h=DynaLoader::dl_load_file($ARGV[0], 0) or die DynaLoader::dl_error(); my $s=DynaLoader::dl_find_symbol($h,\"ContextProbeRun\") or die DynaLoader::dl_error(); my $f=DynaLoader::dl_install_xsub(\"ContextProbe::run\",$s,$ARGV[0]); $f->()' ./ContextProbe-continuation.dll; ":"";
                        string script="set -euo pipefail; "+trigger+"for ((iteration=0; iteration<"+batches+"; iteration++)); do value=$(printf 'repeat-ok\\n' | cat); [[ $value == repeat-ok ]]; done; printf 'repeat-ok\\n'";
                        command=await CodeCommand.Run(bash,["--noprofile","--norc","-c",script],workspace,deadline.Token,workspace);
                        string output=command.Output.Split("\n[Owned runtime diagnostics]",StringSplitOptions.None)[0];
                        var identity=Regex.Match(command.Output,@"S-1-15-2-(?:\d+-){6}\d+").Value;
                        string expected=continuation?"context-continuation-repaired-ok\nrepeat-ok":"repeat-ok";
                        if(command.ExitCode!=0||command.Truncated||output.Trim()!=expected||continuation&&!Regex.IsMatch(command.Output,@"repaired continuation hooks: [1-9]\d*")||identity.Length==0||!identities.TryAdd(identity,0))throw new InvalidDataException("Fresh identity fork/pipeline result failed or contained warnings: "+command.Output);
                        samples.Add(watch.Elapsed.TotalMilliseconds);
                        File.WriteAllText(Path.Combine(destination,$"attempt-{attempt:0000}.json"),JsonSerializer.Serialize(new{attempt,Identity=identity,Milliseconds=watch.Elapsed.TotalMilliseconds,Command=command}));
                        int done=Interlocked.Increment(ref completed);
                        lock(host){host.Refresh();peakHandles=Math.Max(peakHandles,host.HandleCount);peakPrivate=Math.Max(peakPrivate,host.PrivateMemorySize64);}
                        if(done%25==0||done==count)Console.WriteLine($"PASS {done}/{count} fresh identities; elapsed={elapsed.Elapsed.TotalSeconds:F1}s");
                    }
                    catch(Exception error)
                    {
                        File.WriteAllText(Path.Combine(destination,$"failure-{attempt:0000}.json"),JsonSerializer.Serialize(new{attempt,Milliseconds=watch.Elapsed.TotalMilliseconds,Error=error.ToString(),Command=command}));
                        stop.Cancel();throw;
                    }
                }
            }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS",diagnostics);
            GC.Collect();GC.WaitForPendingFinalizers();host.Refresh();
            var times=samples.Order().ToArray();bool aclRestored=Acl()==initialAcl;
            var report=new{Requested=count,BatchesPerCommand=batches,ForcedContinuation=continuation,Completed=completed,UniqueIdentities=identities.Count,Concurrency=concurrency,Seconds=elapsed.Elapsed.TotalSeconds,WorkspaceAclRestored=aclRestored,InitialHandles=initialHandles,FinalHandles=host.HandleCount,PeakHandles=peakHandles,InitialPrivateBytes=initialPrivate,FinalPrivateBytes=host.PrivateMemorySize64,PeakPrivateBytes=peakPrivate,MedianMilliseconds=times.Length>0?times[times.Length/2]:0,P95Milliseconds=times.Length>0?times[Math.Min(times.Length-1,(int)(times.Length*.95))]:0,Passed=completed==count&&identities.Count==count&&aclRestored};
            File.WriteAllText(Path.Combine(destination,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(JsonSerializer.Serialize(report));
            if(!report.Passed)Environment.ExitCode=1;
        }
#else
        await Task.CompletedTask;throw new InvalidOperationException("Use the explicit handoff QA build.");
#endif
    }
}
