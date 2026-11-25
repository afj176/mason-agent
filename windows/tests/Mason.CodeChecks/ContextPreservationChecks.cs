using System.Security.AccessControl;
using System.Text.Json;
using Mason.Code;

static class ContextPreservationChecks
{
    public static async Task Run(string destination)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(!CodeSandboxTools.GitBashAutoAvailable)throw new InvalidOperationException("Use the isolated Bash candidate.");
        string root=Path.GetFullPath(destination);if(Directory.Exists(root))throw new IOException("Choose a fresh context fixture.");Directory.CreateDirectory(root);
        string source=Environment.GetEnvironmentVariable("MASON_CONTEXT_PROBE")??throw new InvalidOperationException("Build and select the native context probe.");
        string Acl()=>new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        string initial=Acl();var cases=new List<object>();bool all=true;
        var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new InvalidOperationException("Git Bash is required.");
        string? previousDiagnostics=Environment.GetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS");
        Environment.SetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS","2");
        try
        {
        foreach(string mode in new[]{"positive","continuation","mapped","readonly","wrong-facade","bad-flags"})
        {
            string name="ContextProbe-"+mode+".dll";File.Copy(source,Path.Combine(root,name));
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));CodeCommandResult? result=null;string? error=null;
            try{result=await CodeCommand.Run(bash,["--noprofile","--norc","-c","set -e; perl -e 'DynaLoader::boot_DynaLoader(\"DynaLoader\"); my $h=DynaLoader::dl_load_file($ARGV[0], 0) or die DynaLoader::dl_error(); my $s=DynaLoader::dl_find_symbol($h,\"ContextProbeRun\") or die DynaLoader::dl_error(); my $f=DynaLoader::dl_install_xsub(\"ContextProbe::run\",$s,$ARGV[0]); $f->()' ./"+name],root,deadline.Token,root);}
            catch(InvalidDataException failed){error=failed.Message;}
            bool passed=mode=="positive"?result is {ExitCode:0,Truncated:false}&&result.Output.Contains("context-canonical-only-ok",StringComparison.Ordinal):
                mode=="continuation"?result is {ExitCode:0,Truncated:false}&&result.Output.Contains("context-continuation-repaired-ok",StringComparison.Ordinal)&&System.Text.RegularExpressions.Regex.IsMatch(result.Output,@"repaired continuation hooks: [1-9]\d*"):
                error?.Contains(mode is "mapped" or "readonly"?"private, committed":"wrong-facade"==mode?"verified facade":"AMD64 context",StringComparison.Ordinal)==true;
            bool? canaryUnchanged=null;
            if(mode=="mapped")
            {
                var expected=Enumerable.Repeat((byte)0x41,1232).ToArray();BitConverter.GetBytes(0x10000bu).CopyTo(expected,48);
                string canary=Path.Combine(root,"context-canary.bin");canaryUnchanged=File.Exists(canary)&&File.ReadAllBytes(canary).SequenceEqual(expected);passed&=canaryUnchanged.Value;
            }
            cases.Add(new{Mode=mode,Passed=passed,CanaryUnchanged=canaryUnchanged,Error=error,Command=result});all&=passed;
            Console.WriteLine((passed?"PASS ":"FAIL ")+mode+" context preservation boundary");
            if(!passed)Console.WriteLine(JsonSerializer.Serialize(new{mode,result,error}));
        }
        }
        finally{Environment.SetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS",previousDiagnostics);}
        bool aclRestored=Acl()==initial;all&=aclRestored;
        File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{Passed=all,WorkspaceAclRestored=aclRestored,Cases=cases},new JsonSerializerOptions{WriteIndented=true}));
        if(!all)throw new InvalidDataException("Context preservation acceptance failed.");
    }
}
