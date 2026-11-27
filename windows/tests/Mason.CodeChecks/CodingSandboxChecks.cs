using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.Json;
using Mason.Code;

static class CodingSandboxChecks
{
    public static async Task Run(string root, Action<bool,string> check)
    {
        if(!OperatingSystem.IsWindows())return;
        var area=Path.Combine(root,"isolation");var workspace=Path.Combine(area,"workspace");var outside=Path.Combine(area,"outside");var cache=Path.Combine(area,"runtime-cache");Directory.CreateDirectory(workspace);Directory.CreateDirectory(outside);
        // A plain AppContainer could write this directory; LPAC must ignore this broad grant.
        var ordinaryPackages=new System.Security.Principal.SecurityIdentifier("S-1-15-2-1");
        var outsideAcl=new DirectoryInfo(outside).GetAccessControl();outsideAcl.AddAccessRule(new FileSystemAccessRule(ordinaryPackages,FileSystemRights.Modify,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));new DirectoryInfo(outside).SetAccessControl(outsideAcl);
        var sentinel=Path.Combine(outside,"sentinel.txt");File.WriteAllText(sentinel,"outside-protected");var before=File.ReadAllBytes(sentinel);
        var aclBefore=new FileInfo(sentinel).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var ambient=Path.Combine(area,"ambient");Directory.CreateDirectory(ambient);var ambientAcl=new DirectoryInfo(ambient).GetAccessControl();ambientAcl.AddAccessRule(new FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier("S-1-15-2-2"),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));new DirectoryInfo(ambient).SetAccessControl(ambientAcl);var ambientFile=Path.Combine(ambient,"ambient.txt");File.WriteAllText(ambientFile,"ambient-original");
        var probeSource=Environment.GetEnvironmentVariable("MASON_SANDBOX_PROBE");
        if(string.IsNullOrEmpty(probeSource)||!File.Exists(probeSource))throw new InvalidOperationException("Build scripts/Build-CodeSandboxProbe.ps1 and set MASON_SANDBOX_PROBE to its executable before running Windows isolation checks.");
        var probe=Path.Combine(workspace,"SandboxNativeProbe.exe");File.Copy(probeSource,probe);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var oldSecret=Environment.GetEnvironmentVariable("MASON_TEST_SECRET");Environment.SetEnvironmentVariable("MASON_TEST_SECRET","fixture-not-a-real-secret");
        CodeCommandResult result;
        try { result=await CodeCommand.Run(probe,[sentinel,ambientFile,port.ToString()],workspace,default,workspace); }
        finally { listener.Stop();Environment.SetEnvironmentVariable("MASON_TEST_SECRET",oldSecret); }
        check(result.ExitCode==0,"native Win32 probe executes inside the strict Windows sandbox: "+result.Output);
        var probeEnvironment=Path.Combine(workspace,"probe-env.txt");string folders=File.Exists(probeEnvironment)?File.ReadAllText(probeEnvironment,System.Text.Encoding.Unicode).ReplaceLineEndings(" | "):"no probe-env.txt";
        using(var data=JsonDocument.Parse(result.Output))foreach(var property in data.RootElement.EnumerateObject())check(property.Value.GetBoolean(),"OS sandbox: "+property.Name+(property.Value.GetBoolean()?"":" · "+folders));
        check(File.ReadAllBytes(sentinel).SequenceEqual(before)&&!File.Exists(Path.Combine(outside,"created.txt"))&&!File.Exists(Path.Combine(outside,"moved.txt")),"outside canary bytes and directory inventory remain unchanged");
        check(new FileInfo(sentinel).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access)==aclBefore,"outside canary ACL was not relaxed");
        var workspaceAcl=new DirectoryInfo(workspace).GetAccessControl().GetAccessRules(true,true,typeof(System.Security.Principal.SecurityIdentifier));
        check(!workspaceAcl.Cast<FileSystemAccessRule>().Any(IsPackageRule),"temporary sandbox identity grants are removed after completion");
        var link=Path.Combine(workspace,"hardlink.txt");
        if(!CreateHardLink(link,sentinel,IntPtr.Zero))throw new System.ComponentModel.Win32Exception();
        bool fileReadBlocked=false,fileWriteBlocked=false;
        try { new CodeWorkspace(workspace).Read("hardlink.txt"); } catch(UnauthorizedAccessException){fileReadBlocked=true;}
        try { new CodeWorkspace(workspace).Edit("hardlink.txt",CodeProtocol.Hash(before),"outside-protected","bad",(_,_)=>{}); } catch(UnauthorizedAccessException){fileWriteBlocked=true;}
        check(fileReadBlocked&&fileWriteBlocked,"brokered file tools reject hard-link aliases as well as sandbox launch");
        bool hardlinkRejected=false;
        try { await CodeCommand.Run(probe,["must-not-execute"],workspace,default,workspace); } catch(UnauthorizedAccessException){hardlinkRejected=true;}
        finally { File.Delete(link); }
        check(hardlinkRejected&&File.ReadAllBytes(sentinel).SequenceEqual(before),"pre-existing hard links are rejected before ACL propagation or process launch");
        var junction=Path.Combine(workspace,"junction");var cmd=CodeCommand.ResolveExecutable("cmd.exe",workspace);
        var made=await CodeCommand.Run(cmd,["/d","/c","mklink /J "+CodeCommand.Quote(junction)+" "+CodeCommand.Quote(outside)],workspace,default);
        if(made.ExitCode!=0)throw new Exception("Cannot prepare junction fixture: "+made.Output);
        bool junctionRejected=false;
        try { await CodeCommand.Run(probe,["must-not-execute"],workspace,default,workspace); } catch(UnauthorizedAccessException){junctionRejected=true;}
        finally { Directory.Delete(junction); }
        check(junctionRejected&&File.ReadAllBytes(sentinel).SequenceEqual(before),"pre-existing directory junctions are rejected without traversing their targets");
        var powerShell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        bool unsupportedPowerShell=false;
        try { await CodeCommand.Run(powerShell,["-NoLogo","-NoProfile","-NonInteractive","-Command","Write-Output 'must-not-run'"],workspace,default,workspace); } catch(UnauthorizedAccessException){unsupportedPowerShell=true;}
        check(unsupportedPowerShell,"unsupported PowerShell fails closed rather than disabling logging or bypassing Auto");
        await CodingRuntimeChecks.Run(root,check);
        check(File.ReadAllText(ambientFile)=="ambient-original","broad restricted-app ACL does not bypass workspace write isolation");
        using var cancelled=new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));bool stopped=false;
        try { await CodeCommand.Run(probe,["sleep"],workspace,cancelled.Token,workspace); } catch(OperationCanceledException){stopped=true;}
        check(stopped,"Auto command cancellation retains the job-managed process boundary");
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")] static bool IsPackageRule(FileSystemAccessRule rule)=>rule.IdentityReference.Value.StartsWith("S-1-15-2-");
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateHardLink(string link,string target,IntPtr security);
}

