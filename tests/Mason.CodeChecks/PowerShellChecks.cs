using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Mason.Code;

static class PowerShellChecks
{
    public static async Task Run(string root)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("PowerShell Auto checks require Windows.");
        var workspace=Path.Combine(root,"workspace");var ambient=Path.Combine(root,"ambient");
        Directory.CreateDirectory(workspace);Directory.CreateDirectory(ambient);
        var directory=new DirectoryInfo(ambient);var acl=directory.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-2"),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));directory.SetAccessControl(acl);
        var sentinel=Path.Combine(ambient,"sentinel.txt");File.WriteAllText(sentinel,"original");
        var before=new FileInfo(sentinel).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        var shell=CodeShells.Find(CodeShellKind.PowerShell)??throw new Exception("PowerShell 7 required");
        var script="""
            $ErrorActionPreference='Stop'
            function Denied([scriptblock]$action) {
                $blocked=$false
                try { & $action } catch [UnauthorizedAccessException] {$blocked=$true} catch [IO.FileNotFoundException] {$blocked=$true}
                if(-not $blocked){throw ('Outside operation succeeded: '+$action.ToString())}
            }
            $p=[IO.Path]::GetFullPath('../ambient/sentinel.txt')
            Denied {[IO.File]::WriteAllText($p,'bad')}
            Denied {[IO.File]::AppendAllText($p,'bad')}
            Denied {[IO.File]::Delete($p)}
            Denied {[IO.File]::Move($p,$p+'.moved')}
            Denied {[IO.File]::WriteAllText($p+'.created','bad')}
            Denied {$file=[IO.FileInfo]::new($p);$acl=[IO.FileSystemAclExtensions]::GetAccessControl($file);$acl.SetAccessRuleProtection($true,$true);[IO.FileSystemAclExtensions]::SetAccessControl($file,$acl)}
            Denied {[IO.File]::WriteAllText((Join-Path $PSHOME 'agent-created.tmp'),'bad')}
            & "$PSHOME/pwsh.exe" -NoProfile -NonInteractive -Command 'try {[IO.File]::WriteAllText("../ambient/sentinel.txt","bad");exit 9}catch [UnauthorizedAccessException]{[IO.File]::WriteAllText("child-ok.txt","ok");exit 0}'
            if($LASTEXITCODE -ne 0){throw 'Child containment failed'}
            [Console]::WriteLine('powershell-adversarial-ok')
            """;
        var result=await CodeCommand.Run(shell,["-NoLogo","-NoProfile","-NonInteractive","-Command",script],workspace,default,workspace);
        if(result.ExitCode!=0||!result.Output.Contains("powershell-adversarial-ok"))throw new Exception(result.Output);
        if(File.ReadAllText(sentinel)!="original"||File.Exists(sentinel+".created")||File.Exists(sentinel+".moved")||new FileInfo(sentinel).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All)!=before)throw new Exception("Outside sentinel or ACL changed");
        Console.WriteLine("PASS PowerShell parent/child permissive outside mutation denials, runtime protection and exact sentinel/ACL preservation");
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var running=CodeCommand.Run(shell,["-NoProfile","-NonInteractive","-Command","& \"$PSHOME/pwsh.exe\" -NoProfile -NonInteractive -Command '[IO.File]::WriteAllText(\"child.pid\",[string]$PID); Start-Sleep 60'"],workspace,cancellation.Token,workspace);
        try
        {
            var pidFile=Path.Combine(workspace,"child.pid");var wait=Stopwatch.StartNew();
            while(!File.Exists(pidFile)&&!running.IsCompleted&&wait.Elapsed<TimeSpan.FromSeconds(30))await Task.Delay(50);
            if(!File.Exists(pidFile))throw new Exception("Cancellation child did not become ready");
            using var child=Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));var elapsed=Stopwatch.StartNew();cancellation.Cancel();
            bool cancelled=false;try{await running;}catch(OperationCanceledException){cancelled=true;}
            if(!cancelled||!child.WaitForExit(3000)||elapsed.Elapsed>TimeSpan.FromSeconds(10))throw new Exception("PowerShell cancellation failed");
            Console.WriteLine("PASS PowerShell cancellation drains child: "+elapsed.ElapsedMilliseconds+" ms");
        }
        finally {cancellation.Cancel();try{await running;}catch(OperationCanceledException){} }
    }
}
