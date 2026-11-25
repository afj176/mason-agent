using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text.Json;
using Mason.Code;

[SupportedOSPlatform("windows")]
static class BashCancellationChecks
{
    public static async Task Run(string destination)
    {
        string root=Path.GetFullPath(destination);
        if(Directory.Exists(root))throw new IOException("Use a fresh cancellation fixture.");
        string workspace=Path.Combine(root,"workspace");Directory.CreateDirectory(workspace);
        string bash=CodeShells.Find(CodeShellKind.GitBash)??throw new Exception("Git Bash is required.");
        if(!CodeSandboxTools.GitBashAutoAvailable)throw new Exception("The normal Bash admission gate must be available.");
        string Acl()=>new DirectoryInfo(workspace).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        string baseline=Acl();var cases=new List<object>();bool passed=false;
        try
        {
            for(int index=0;index<6;index++)
            {
                bool live=index>=4;string marker=Path.Combine(workspace,"cancel-live.pid");
                if(File.Exists(marker))File.Delete(marker);
                using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(60));
                if(!live)cancellation.CancelAfter(index%2==0?50:1500);
                string command=live?"python -c \"import os,time;open('cancel-live.pid','w').write(str(os.getpid()));time.sleep(60)\" | cat":"sleep 60 | cat";
                var running=CodeCommand.Run(bash,["--noprofile","--norc","-c",command],workspace,cancellation.Token,workspace);
                Process? child=null;bool cancelled=false,childStopped=!live;
                try
                {
                    if(live)
                    {
                        var readiness=Stopwatch.StartNew();
                        while(!File.Exists(marker)&&!running.IsCompleted&&readiness.Elapsed<TimeSpan.FromSeconds(45))await Task.Delay(25);
                        if(!File.Exists(marker))throw new Exception("Live pipeline never reached its child witness.");
                        child=Process.GetProcessById(int.Parse(File.ReadAllText(marker)));
                        if(child.HasExited)throw new Exception("Child exited before explicit cancellation.");
                        cancellation.Cancel();
                    }
                    try{await running;}catch(OperationCanceledException){cancelled=true;}
                    if(child!=null)childStopped=child.WaitForExit(3000);
                    bool restored=Acl()==baseline;
                    cases.Add(new{index,LivePipeline=live,Cancelled=cancelled,ChildStopped=childStopped,WorkspaceAclRestored=restored});
                    if(!cancelled||!childStopped||!restored)throw new Exception("Cancellation did not stop the command and restore its workspace ACL.");
                    Console.WriteLine("PASS Bash cancellation "+index+" ("+(live?"verified live pipeline":"startup deadline")+")");
                }
                finally
                {
                    cancellation.Cancel();try{await running;}catch(OperationCanceledException){}
                    child?.Dispose();
                }
            }
            passed=true;
        }
        finally{File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{Passed=passed,WorkspaceAclRestored=Acl()==baseline,Cases=cases},new JsonSerializerOptions{WriteIndented=true}));}
    }
}
