using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Mason.Code;

/// <summary>Explicit CLI-only feasibility experiment; never runs in the app or ordinary checks.</summary>
[SupportedOSPlatform("windows")]
static class MemoryReservationChecks
{
    public static async Task Run(string destination,bool cancel=false,bool adopt=false,bool multipleReservations=false,long secondReservationBytes=0x200000)
    {
        var root=Path.GetFullPath(destination);
        if(Directory.Exists(root))throw new IOException("Choose a new memory-reservation QA fixture.");
        var source=Path.GetFullPath(Environment.GetEnvironmentVariable("MASON_MEMORY_PROBE")??throw new InvalidOperationException("Build scripts/Build-CodeMemoryProbe.ps1 and set MASON_MEMORY_PROBE to its directory."));
        var workspace=Path.Combine(root,"workspace");Directory.CreateDirectory(workspace);
        var files=new List<object>();
        string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        foreach(var name in new[]{"MemoryReservationLauncher.exe","MemoryReservationSentinel.exe"})
        {
            var original=Path.Combine(source,name);var copy=Path.Combine(workspace,name);
            if((File.GetAttributes(original)&FileAttributes.ReparsePoint)!=0)throw new IOException("Probe executable cannot be a reparse point.");
            var hash=Hash(original);File.Copy(original,copy);if(Hash(copy)!=hash)throw new IOException("Probe staging hash mismatch.");
            files.Add(new{Source=original,Path=copy,Sha256=hash});
        }
        var outside=Path.Combine(root,"outside.txt");File.WriteAllText(outside,"preserve");
        var ambient=Path.Combine(root,"permissive");Directory.CreateDirectory(ambient);
        var ambientAcl=new DirectoryInfo(ambient).GetAccessControl();
        ambientAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-2"),FileSystemRights.Modify,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(ambient).SetAccessControl(ambientAcl);
        var broad=Path.Combine(ambient,"outside.txt");File.WriteAllText(broad,"preserve");
        string Acl(string path)=>File.GetAttributes(path).HasFlag(FileAttributes.Directory)
            ?new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)
            :new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var watched=new[]{workspace,root,outside,ambient,broad}.ToDictionary(p=>p,Acl);
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(115));
        CodeCommandResult? result=null;string? error=null;bool cancelled=false,childrenStopped=false;
        JsonElement? cancellationWitness=null;
        try
        {
            var arguments=new List<string>{outside,broad,port.ToString()};if(cancel)arguments.Add("cancel");else if(adopt)arguments.Add("adopt");
            var running=CodeCommand.Run(Path.Combine(workspace,"MemoryReservationLauncher.exe"),arguments,workspace,cancellation.Token,workspace);
            if(cancel)
            {
                try
                {
                    var ready=Path.Combine(workspace,"reservation-ready.json");var timer=Stopwatch.StartNew();
                    while(!File.Exists(ready)&&!running.IsCompleted&&timer.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(25);
                    if(!File.Exists(ready))throw new InvalidOperationException("Reservation sentinel never reached its cancellation witness.");
                    using(var document=JsonDocument.Parse(File.ReadAllText(ready)))cancellationWitness=document.RootElement.Clone();
                    int child=cancellationWitness.Value.GetProperty("pid").GetInt32();int launcher=int.Parse(File.ReadAllText(Path.Combine(workspace,"reservation-launcher.pid")));
                    using var childProcess=Process.GetProcessById(child);using var launcherProcess=Process.GetProcessById(launcher);
                    if(childProcess.HasExited||launcherProcess.HasExited)throw new InvalidOperationException("Cancellation requires both owned processes to be alive.");
                    cancellation.Cancel();
                    try{result=await running;}catch(OperationCanceledException){cancelled=true;}
                    childrenStopped=childProcess.WaitForExit(3000)&&launcherProcess.WaitForExit(3000);
                }
                finally { cancellation.Cancel();try{await running;}catch(OperationCanceledException){} }
            }
            else result=await running;
            if(result!=null)File.WriteAllText(Path.Combine(root,"command-output.jsonl"),result.Output);
        }
        catch(Exception exception){error=exception.ToString();}
        var events=new List<JsonElement>();
        if(result!=null)
        {
            foreach(var line in result.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries))
            {
                if(!line.TrimStart().StartsWith('{'))continue;
                using var document=JsonDocument.Parse(line);events.Add(document.RootElement.Clone());
            }
        }
        bool ControlSucceeded()=>events.Any(e=>e.TryGetProperty("sentinel",out var mode)&&mode.GetString()=="control"&&e.GetProperty("boundary").GetBoolean()&&e.GetProperty("identityUnchanged").GetBoolean());
        var reserved=events.Where(e=>e.TryGetProperty("sentinel",out var mode)&&mode.GetString()=="reserved").ToArray();
        bool preserved=File.ReadAllText(outside)=="preserve"&&File.ReadAllText(broad)=="preserve";
        bool aclRestored=watched.All(p=>Acl(p.Key)==p.Value);
        var failingCreation=events.FirstOrDefault(e=>e.TryGetProperty("stage",out var stage)&&stage.GetString()=="create"&&e.GetProperty("status").GetString()!="00000000");
        var secondRanges=events.Where(e=>e.TryGetProperty("stage",out var stage)&&stage.GetString()=="second-reservation").ToArray();
        bool additionalRange=!multipleReservations||secondRanges.Length==1&&secondRanges[0].GetProperty("exact").GetBoolean()&&secondRanges[0].GetProperty("bytes").GetInt64()==secondReservationBytes;
        bool allReserved=reserved.Length==(multipleReservations?1:99)&&additionalRange&&reserved.All(e=>e.GetProperty("boundary").GetBoolean()&&e.GetProperty("loaderLayout").GetBoolean()&&e.GetProperty("handoff").GetBoolean()&&e.GetProperty("identityUnchanged").GetBoolean());
        bool execution=cancel?cancelled&&childrenStopped&&cancellationWitness?.GetProperty("boundary").GetBoolean()==true&&cancellationWitness?.GetProperty("exactReservation").GetBoolean()==true
            :result is {ExitCode:0,Truncated:false}&&ControlSucceeded()&&allReserved;
        bool passed=error==null&&execution&&preserved&&aclRestored&&!listener.Pending();
        var report=new{At=DateTimeOffset.UtcNow,Experiment="QA-only NtCreateUserProcess fixed uncommitted address reservation",InPlaceAdoptionFixture=adopt,CancellationFixture=cancel,CancellationObserved=cancelled,OwnedProcessesStopped=childrenStopped,CancellationWitness=cancellationWitness,Files=files,Error=error,Command=result,ControlSucceeded=ControlSucceeded(),ReservedChildren=reserved.Length,ReservationAndHandoffPassed=allReserved,OutsideCanariesPreserved=preserved,ExactFixtureAclRestored=aclRestored,NetworkAccepted=listener.Pending(),FailingCreation=failingCreation.ValueKind==JsonValueKind.Undefined?(JsonElement?)null:failingCreation,ProductionHooksChanged=false,QaBuildGitBashAvailable=CodeSandboxTools.GitBashAutoAvailable,Passed=passed};
        File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new{report.CancellationFixture,report.CancellationObserved,report.OwnedProcessesStopped,report.ControlSucceeded,report.ReservedChildren,report.ReservationAndHandoffPassed,report.OutsideCanariesPreserved,report.ExactFixtureAclRestored,report.FailingCreation,report.Error,report.Passed}));
        if(!passed)Environment.ExitCode=1;
    }
}
