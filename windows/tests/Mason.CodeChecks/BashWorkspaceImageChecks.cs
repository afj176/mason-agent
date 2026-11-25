using System.Text.Json;
using System.Runtime.Versioning;
using Mason.Code;

[SupportedOSPlatform("windows")]
static class BashWorkspaceImageChecks
{
    public static async Task Run(string destination)
    {
        if(!CodeSandboxTools.GitBashAutoAvailable)throw new InvalidOperationException("Use the explicitly isolated MASON_GIT_BASH_QA probe build; the product gate remains disabled.");
        var root=Path.GetFullPath(destination);if(Directory.Exists(root))throw new IOException("Use a new probe directory.");
        var a=Path.Combine(root,"workspace-a");var b=Path.Combine(root,"workspace-b");Directory.CreateDirectory(a);Directory.CreateDirectory(b);
        var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new Exception("Git Bash is required.");
        var pa=CodeSandboxTools.Prepare(bash,a);var pb=CodeSandboxTools.Prepare(bash,b);
        var da=Path.Combine(Path.GetDirectoryName(pa.Executable)!,"msys-2.0.dll");var db=Path.Combine(Path.GetDirectoryName(pb.Executable)!,"msys-2.0.dll");
        string Manifest(CodeSandboxTools.Prepared prepared)=>Path.Combine(prepared.Roots.Single(r=>Path.GetFileName(r).StartsWith("git-",StringComparison.Ordinal)),"snapshot.json");
        var command="set -e; printf ready > ready; while [[ ! -e release ]]; do :; done; IFS= read -r -N 2 own < \"$2\"; if { IFS= read -r -N 2 bytes < \"$1\"; } 2> cross-image-read.err; then printf 'cross-workspace-image-read-allowed\\n'; exit 19; fi; if { printf bad >> \"$2\"; } 2> own-runtime-write.err; then exit 20; fi; printf 'cross-workspace-image-read-denied\\nown-runtime-read-only\\n'";
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ra=CodeCommand.Run(bash,["--noprofile","--norc","-c",command,"qa",db,Manifest(pa)],a,deadline.Token,a);
        var rb=CodeCommand.Run(bash,["--noprofile","--norc","-c",command,"qa",da,Manifest(pb)],b,deadline.Token,b);
        try
        {
            while(!File.Exists(Path.Combine(a,"ready"))||!File.Exists(Path.Combine(b,"ready")))
            {
                if(ra.IsCompleted||rb.IsCompleted)throw new Exception("A probe command exited before the concurrent image check: "+(ra.IsCompleted?(await ra).Output:(await rb).Output));
                await Task.Delay(50,deadline.Token);
            }
            var loadProbe="import ctypes,json,sys\ntry: ctypes.WinDLL(sys.argv[1])\nexcept OSError as error:\n print(json.dumps({'winerror':error.winerror}));sys.exit(0 if error.winerror==5 else 3)\nelse: sys.exit(19)";
            var loads=await Task.WhenAll(
                CodeCommand.Run(CodeCommand.ResolveExecutable("python.exe",a),["-c",loadProbe,db],a,deadline.Token,a),
                CodeCommand.Run(CodeCommand.ResolveExecutable("python.exe",b),["-c",loadProbe,da],b,deadline.Token,b));
            File.WriteAllText(Path.Combine(a,"release"),"");File.WriteAllText(Path.Combine(b,"release"),"");
            var results=await Task.WhenAll(ra,rb);
            var report=new {DifferentImages=da!=db,SameBytes=File.ReadAllBytes(da).SequenceEqual(File.ReadAllBytes(db)),A=results[0],B=results[1],CrossWorkspaceLoads=loads};
            File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            if(!report.DifferentImages||!report.SameBytes||loads.Any(r=>r.ExitCode!=0)||results.Any(r=>r.ExitCode!=0||!r.Output.Contains("cross-workspace-image-read-denied",StringComparison.Ordinal)||!r.Output.Contains("own-runtime-read-only",StringComparison.Ordinal)))throw new Exception("Cross-workspace image isolation failed: "+JsonSerializer.Serialize(report));
            Console.WriteLine("PASS physically distinct workspace MSYS snapshots retain identical audited code bytes");
            Console.WriteLine("PASS concurrent sandbox commands cannot read the other workspace's staged MSYS image");
            Console.WriteLine("PASS each command can read its own runtime metadata and cannot append to it");
            Console.WriteLine("PASS native LoadLibrary attempts against another workspace's image receive access denied");
        }
        finally
        {
            deadline.Cancel();try{await Task.WhenAll(ra,rb);}catch(OperationCanceledException){}
        }
    }
}
