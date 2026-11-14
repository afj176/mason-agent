using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Mason.Code;

[SupportedOSPlatform("windows")]
static class RuntimeHostChecks
{
    public static async Task Worker(string workspace,string id)
    {
        var python=CodeCommand.ResolveExecutable("python.exe",workspace);var tools=CodeSandboxTools.Prepare(python,workspace);
        var git=tools.Roots.Single(r=>Path.GetFileName(r).StartsWith("git-",StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(workspace)!,"host-"+id+"-runtime.json"),JsonSerializer.Serialize(new{git}));
        var script="""
            import pathlib,sys,time
            marker,manifest,outside=sys.argv[1:]
            pathlib.Path('ready-'+marker).write_text('ready')
            deadline=time.monotonic()+80
            while not pathlib.Path('release-'+marker).exists():
                if time.monotonic()>deadline:raise TimeoutError('QA host release was not received')
                time.sleep(.025)
            assert pathlib.Path(manifest).read_text().startswith('{')
            assert pathlib.Path.cwd().parent.stat().st_mode
            try:list(pathlib.Path.cwd().parent.iterdir())
            except PermissionError:pass
            else:raise AssertionError('parent directory listing was permitted')
            try:pathlib.Path(outside).read_text()
            except PermissionError:pass
            else:raise AssertionError('sibling file contents were readable')
            try:pathlib.Path(outside).write_text('forbidden')
            except PermissionError:pass
            else:raise AssertionError('outside write was permitted')
            print('snapshot-readable-after-release;outside-denied')
            """;
        var result=await CodeCommand.Run(python,["-c",script,id,Path.Combine(git,"snapshot.json"),Path.Combine(Path.GetDirectoryName(workspace)!,"outside.txt")],workspace,default,workspace);
        Console.WriteLine(JsonSerializer.Serialize(result));Environment.ExitCode=result.ExitCode;
    }
    public static async Task Run(string destination,bool separateProfiles=false)
    {
        if(!CodeSandboxTools.GitBashAutoAvailable)throw new InvalidOperationException("Use an explicitly isolated Git Bash QA build.");
        var root=Path.GetFullPath(destination);if(Directory.Exists(root))throw new IOException("Use a fresh host-concurrency fixture.");
        var workspace=Path.Combine(root,"workspace");Directory.CreateDirectory(workspace);File.WriteAllText(Path.Combine(root,"outside.txt"),"preserve");
        string Acl(string path)=>new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        HashSet<string> Identities(string path)=>new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(r=>r.IdentityReference.Value).ToHashSet();
        string baseline=Acl(workspace),ancestorBaseline=Acl(root);var baselineIdentities=Identities(workspace);
        string outsideBaseline=new FileInfo(Path.Combine(root,"outside.txt")).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var processes=new List<Process>();var outputs=new List<Task<string>>();var errors=new List<Task<string>>();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(100));
        try
        {
            foreach(var id in new[]{"a","b"})
            {
                var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                if(separateProfiles)start.Environment["MASON_DATA_HOME"]=Path.Combine(root,"host-"+id+"-profile");
                start.Environment.Remove("MASON_SANDBOX_DIAGNOSTICS");foreach(var arg in new[]{"--runtime-host-worker",workspace,id})start.ArgumentList.Add(arg);
                var child=Process.Start(start)??throw new IOException("Cannot start the owned QA host.");processes.Add(child);outputs.Add(child.StandardOutput.ReadToEndAsync());errors.Add(child.StandardError.ReadToEndAsync());
            }
            while(!File.Exists(Path.Combine(workspace,"ready-a"))||!File.Exists(Path.Combine(workspace,"ready-b")))
            {
                if(processes.Any(p=>p.HasExited))throw new Exception("A coordinated host exited before readiness: "+string.Join('\n',await Task.WhenAll(outputs.Where((_,i)=>processes[i].HasExited)))+string.Join('\n',await Task.WhenAll(errors.Where((_,i)=>processes[i].HasExited))));
                await Task.Delay(50,deadline.Token);
            }
            var temporary=Identities(workspace);temporary.ExceptWith(baselineIdentities);
            var ancestorEntries=new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access).GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(r=>temporary.Contains(r.IdentityReference.Value)).ToArray();
            bool ancestorMetadataBounded=ancestorEntries.Length==4&&ancestorEntries.All(r=>r.AccessControlType==AccessControlType.Allow&&r.InheritanceFlags==InheritanceFlags.None&&r.PropagationFlags==PropagationFlags.None&&r.FileSystemRights==(FileSystemRights.ReadAttributes|FileSystemRights.Traverse|FileSystemRights.Synchronize));
            bool siblingAclUntouched=new FileInfo(Path.Combine(root,"outside.txt")).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)==outsideBaseline;
            var witness=Path.Combine(root,"host-created-during-command");Directory.CreateDirectory(witness);
            var witnessAcl=new DirectoryInfo(witness).GetAccessControl(AccessControlSections.Access);string witnessBefore=witnessAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            bool newChildSafe=witnessAcl.AreAccessRulesCanonical&&!witnessAcl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r=>temporary.Contains(r.IdentityReference.Value));
            File.WriteAllText(Path.Combine(workspace,"release-a"),"");await processes[0].WaitForExitAsync(deadline.Token);
            if(processes[1].HasExited)throw new Exception("The second host did not remain active during peer cleanup.");
            File.WriteAllText(Path.Combine(workspace,"release-b"),"");await processes[1].WaitForExitAsync(deadline.Token);
            var text=await Task.WhenAll(outputs);var stderr=await Task.WhenAll(errors);
            string Runtime(string id){using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"host-"+id+"-runtime.json")));return json.RootElement.GetProperty("git").GetString()!;}
            string ga=Runtime("a"),gb=Runtime("b");var remaining=Identities(ga);remaining.UnionWith(Identities(gb));
            bool completed=processes.All(p=>p.ExitCode==0)&&text.All(t=>t.Contains("snapshot-readable-after-release;outside-denied",StringComparison.Ordinal));
            string afterAcl=Acl(workspace);var beforeDescriptor=new RawSecurityDescriptor(baseline);var afterDescriptor=new RawSecurityDescriptor(afterAcl);
            byte[] Rules(RawSecurityDescriptor descriptor){var bytes=new byte[descriptor.DiscretionaryAcl!.BinaryLength];descriptor.DiscretionaryAcl.GetBinaryForm(bytes,0);return bytes;}
            bool samePermissions=Rules(beforeDescriptor).SequenceEqual(Rules(afterDescriptor))&&((beforeDescriptor.ControlFlags^afterDescriptor.ControlFlags)&~ControlFlags.DiscretionaryAclAutoInherited)==0;
            var ancestorBefore=new RawSecurityDescriptor(ancestorBaseline);var ancestorAfter=new RawSecurityDescriptor(Acl(root));
            bool ancestorRestored=Rules(ancestorBefore).SequenceEqual(Rules(ancestorAfter))&&ancestorBefore.ControlFlags==ancestorAfter.ControlFlags;
            bool newChildUnchanged=Acl(witness)==witnessBefore;
            var report=new{BothHostsCompleted=completed,SameWorkspaceRuntime=ga==gb,SeparateProfiles=separateProfiles,SeparateTemporaryIdentities=temporary.Count==4,WorkspaceAclRestored=samePermissions,WorkspaceAclBefore=baseline,WorkspaceAclAfter=afterAcl,AncestorMetadataBounded=ancestorMetadataBounded,ChildAclUntouched=siblingAclUntouched,NewChildCanonicalWithoutCommandGrants=newChildSafe,NewChildAclUnchangedAfterCleanup=newChildUnchanged,AncestorAclRestored=ancestorRestored,RuntimeTemporaryRulesRemoved=!remaining.Overlaps(temporary),OutsidePreserved=File.ReadAllText(Path.Combine(root,"outside.txt"))=="preserve",Outputs=text,Errors=stderr};
            File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            if(!report.BothHostsCompleted||report.SameWorkspaceRuntime==separateProfiles||!report.SeparateTemporaryIdentities||!report.WorkspaceAclRestored||!report.RuntimeTemporaryRulesRemoved||!report.OutsidePreserved||!ancestorMetadataBounded||!siblingAclUntouched||!ancestorRestored||!newChildSafe||!newChildUnchanged)throw new Exception(JsonSerializer.Serialize(report));
            Console.WriteLine(separateProfiles?"PASS independent data profiles coordinate shared workspace permissions with separate runtime caches":"PASS concurrent independent hosts publish/reuse one workspace runtime without a staging race");
            Console.WriteLine("PASS peer cleanup preserves the still-running command's runtime access and outside-write denial");
            Console.WriteLine("PASS all four ephemeral identities are removed from workspace/runtime ACLs after both hosts exit");
            Console.WriteLine("PASS exact non-inheriting ancestor metadata grants preserve child ACLs, deny listing/sibling contents and clean up after overlapping hosts");
        }
        finally{foreach(var process in processes){if(!process.HasExited)process.Kill(true);process.Dispose();}}
    }
}
