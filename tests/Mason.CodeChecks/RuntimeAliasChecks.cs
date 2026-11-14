using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Mason.Code;

[SupportedOSPlatform("windows")]
static class RuntimeAliasChecks
{
    public static async Task Run(string destination)
    {
        string root=Path.GetFullPath(destination);if(Directory.Exists(root))throw new IOException("Choose a fresh alias fixture.");
        string workspace=Path.Combine(root,"workspace"),outside=Path.Combine(root,"outside");Directory.CreateDirectory(workspace);Directory.CreateDirectory(outside);
        var access=new DirectoryInfo(outside).GetAccessControl(AccessControlSections.Access);
        access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-2"),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(outside).SetAccessControl(access);string sentinel=Path.Combine(outside,"sentinel.txt");File.WriteAllText(sentinel,"alias-sentinel");
        string Acl(string path)=>Directory.Exists(path)?new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access):new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        string beforeDirectory=Acl(outside),beforeFile=Acl(sentinel);var python=CodeCommand.ResolveExecutable("python.exe",workspace);
        var script="""
            import ctypes,json,os,pathlib,struct,sys
            outside=pathlib.Path(sys.argv[1]); source=outside/'sentinel.txt'
            hardlink=False
            try:os.link(source,'inside-hardlink');hardlink=True
            except OSError:pass
            k=ctypes.WinDLL('kernel32',use_last_error=True)
            k.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_ulong,ctypes.c_ulong,ctypes.c_void_p,ctypes.c_ulong,ctypes.c_ulong,ctypes.c_void_p];k.CreateFileW.restype=ctypes.c_void_p
            k.DeviceIoControl.argtypes=[ctypes.c_void_p,ctypes.c_ulong,ctypes.c_void_p,ctypes.c_ulong,ctypes.c_void_p,ctypes.c_ulong,ctypes.POINTER(ctypes.c_ulong),ctypes.c_void_p]
            k.CloseHandle.argtypes=[ctypes.c_void_p]
            junction=pathlib.Path('inside-junction');junction.mkdir()
            substitute=('\\??\\'+str(outside)).encode('utf-16le'); printable=str(outside).encode('utf-16le')
            names=substitute+b'\x00\x00'+printable+b'\x00\x00'
            data=struct.pack('<IHHHHHH',0xA0000003,8+len(names),0,0,len(substitute),len(substitute)+2,len(printable))+names
            handle=k.CreateFileW(str(junction),0x40000000,7,None,3,0x02200000,None);made=False;error=ctypes.get_last_error()
            if handle not in (None,ctypes.c_void_p(-1).value):
                buffer=ctypes.create_string_buffer(data);done=ctypes.c_ulong()
                made=bool(k.DeviceIoControl(handle,0x900a4,buffer,len(data),None,0,ctypes.byref(done),None));error=ctypes.get_last_error();k.CloseHandle(handle)
            def denied(action):
                try:action();return False
                except OSError:return True
            checks={'overwrite_denied':True,'append_denied':True,'create_denied':True}
            if made:
                checks={'overwrite_denied':denied(lambda:(junction/'sentinel.txt').write_text('bad')),
                        'append_denied':denied(lambda:(junction/'sentinel.txt').open('a').write('bad')),
                        'create_denied':denied(lambda:(junction/'created.txt').write_text('bad'))}
            print(json.dumps({'outside_hardlink_created':hardlink,'junction_created':made,'junction_error':error,**checks}))
            assert all(checks.values())
            """;
        var command=await CodeCommand.Run(python,["-c",script,outside],workspace,default,workspace);
        using var result=JsonDocument.Parse(command.Output.Split("\n[Owned runtime diagnostics]",StringSplitOptions.None)[0]);
        bool hardlink=result.RootElement.GetProperty("outside_hardlink_created").GetBoolean(),junction=result.RootElement.GetProperty("junction_created").GetBoolean();
        bool actualJunction=!junction||(File.GetAttributes(Path.Combine(workspace,"inside-junction"))&FileAttributes.ReparsePoint)!=0&&CodeSandbox.RuntimeWorkspaceIdentity(Path.Combine(workspace,"inside-junction"))==CodeSandbox.RuntimeWorkspaceIdentity(outside);
        bool preserved=File.ReadAllText(sentinel)=="alias-sentinel"&&!File.Exists(Path.Combine(outside,"created.txt"));
        bool aclUnchanged=Acl(outside)==beforeDirectory&&Acl(sentinel)==beforeFile,blocked=false;
        if(junction||hardlink)
        {
            try{await CodeCommand.Run(python,["-c","open('must-not-run.txt','w').write('bad')"],workspace,default,workspace);}
            catch(UnauthorizedAccessException){blocked=true;}
        }
        bool passed=command.ExitCode==0&&!hardlink&&actualJunction&&preserved&&aclUnchanged&&(!(junction||hardlink)||blocked)&&!File.Exists(Path.Combine(workspace,"must-not-run.txt"));
        File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{Passed=passed,OutsideHardlinkCreated=hardlink,JunctionCreated=junction,JunctionPhysicalTargetVerified=actualJunction,OutsidePreserved=preserved,OutsideAclUnchanged=aclUnchanged,SubsequentLaunchRejected=blocked,Command=command},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(command.Output);Console.WriteLine("PASS="+passed+"; outside ACL unchanged="+aclUnchanged);
        // Remove only the link itself; never recurse through an actor-created alias.
        if(junction)Directory.Delete(Path.Combine(workspace,"inside-junction"));
        if(hardlink)File.Delete(Path.Combine(workspace,"inside-hardlink"));
        if(!passed)throw new Exception("Post-launch alias confinement failed; inspect the fixture report before enabling Auto.");
    }
}
