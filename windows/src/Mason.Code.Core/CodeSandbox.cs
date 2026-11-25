using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Mason.Code;

/// <summary>Fresh Windows LPAC identity, workspace-only writes, isolated environment and no network capabilities.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class CodeSandbox : IDisposable
{
    public IntPtr Sid { get; private set; }
    public IntPtr WriteSid { get; private set; }
    public IntPtr RestrictedToken { get; private set; }
    public IntPtr Capabilities { get; private set; }
    public string DesktopName { get; private set; } = "";
    internal IntPtr ObjectDirectory { get; private set; }
    internal string ObjectDirectoryName { get; private set; }="";
    internal string DefaultAclDiagnostic { get; private set; }="";
    IntPtr privateDesktop, windowStation;
    static readonly object desktopAclLock = new();
    static readonly object fileAclLock = new();
    public uint CapabilityCount => loggingSid==IntPtr.Zero?1u:2u;
    IntPtr registryReadSid,loggingSid,loggingGroupSid;
    public string Root { get; }
    public string Scratch { get; }
    public string Executable { get; }
    internal CodeHandoffPlan? Handoff {get;}
    readonly string[] runtimeRoots,runtimePath;
    readonly SecurityIdentifier identity;
    readonly SecurityIdentifier writeIdentity;
    readonly string profileName = "Mason.Code." + Guid.NewGuid().ToString("N");
    readonly List<string> grants = [];
    [System.Diagnostics.Conditional("MASON_HANDOFF_QA")]
    void TraceSetup(string stage){if(Environment.GetEnvironmentVariable("MASON_SETUP_TRACE")=="1")Console.Error.WriteLine("QA_SETUP "+Environment.TickCount64+" "+profileName+" "+stage);}
    readonly HashSet<string> metadataGrants=new(StringComparer.OrdinalIgnoreCase);
    public CodeSandbox(string workspace, string executable, CancellationToken cancellation)
    {
        TraceSetup("constructor begin");
        Root = new CodeWorkspace(workspace).Root;
        if (Root.StartsWith(@"\\",StringComparison.Ordinal) || Root.Equals(Path.GetPathRoot(Root), StringComparison.OrdinalIgnoreCase) || Root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase) || Root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Auto requires a project folder, not a drive, user-profile or Windows root.");
        if(ContainsHost(Root,AppContext.BaseDirectory))throw new UnauthorizedAccessException("Auto workspace cannot contain the running harness binaries. Choose a project outside the application folder.");
        if(ContainsHost(Root,CodeHostCoordination.Root))throw new UnauthorizedAccessException("Auto workspace cannot contain trusted host coordination files. Choose a project outside that directory.");
        // ACL propagation must never grant this identity access through an existing link/hard link.
        ValidateTree(Root, cancellation);
        TraceSetup("workspace validated");
        var tools=CodeSandboxTools.Prepare(executable,Root,cancellation);Executable=tools.Executable;runtimeRoots=tools.Roots;runtimePath=tools.SearchPath;Handoff=tools.Handoff;
        TraceSetup("runtime prepared");
        Scratch = Path.Combine(Root, CodeWorkspace.SandboxFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Scratch);
        cancellation.ThrowIfCancellationRequested();
        int hr = CreateAppContainerProfile(profileName, "Mason Agent isolated command", "Ephemeral coding command sandbox", IntPtr.Zero, 0, out var sid);
        if(hr<0)throw new InvalidOperationException("Windows could not establish the Auto sandbox. No unrestricted fallback was run.",Marshal.GetExceptionForHR(hr)); Sid = sid; identity = new SecurityIdentifier(sid);
        var random=System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        writeIdentity=new SecurityIdentifier($"S-1-5-21-{BitConverter.ToUInt32(random,0)}-{BitConverter.ToUInt32(random,4)}-{BitConverter.ToUInt32(random,8)}-{BitConverter.ToUInt32(random,12)}");
        WriteSid=Marshal.AllocHGlobal(writeIdentity.BinaryLength);var sidBytes=new byte[writeIdentity.BinaryLength];writeIdentity.GetBinaryForm(sidBytes,0);Marshal.Copy(sidBytes,0,WriteSid,sidBytes.Length);
        try
        {
            if(Path.GetFileName(executable).Equals("pwsh.exe",StringComparison.OrdinalIgnoreCase)&&string.Equals(Path.GetFullPath(executable),CodeShells.Find(CodeShellKind.PowerShell),StringComparison.OrdinalIgnoreCase))
            {
                if(!CodePowerShellLogging.Provisioned)throw new UnauthorizedAccessException("PowerShell Auto requires the optional administrator logging setup.");
                var loggingIdentity=new SecurityIdentifier(CodePowerShellLogging.CapabilitySid());var loggingBytes=new byte[loggingIdentity.BinaryLength];loggingIdentity.GetBinaryForm(loggingBytes,0);
                loggingSid=Marshal.AllocHGlobal(loggingBytes.Length);Marshal.Copy(loggingBytes,0,loggingSid,loggingBytes.Length);
                var loggingGroup=new SecurityIdentifier(CodePowerShellLogging.CapabilityGroupSid());var groupBytes=new byte[loggingGroup.BinaryLength];loggingGroup.GetBinaryForm(groupBytes,0);
                loggingGroupSid=Marshal.AllocHGlobal(groupBytes.Length);Marshal.Copy(groupBytes,0,loggingGroupSid,groupBytes.Length);
            }
            if(!DeriveCapabilitySidsFromName("registryRead",out var groups,out uint groupCount,out var capabilities,out uint capabilityCount))throw new Win32Exception();
            try
            {
                for(int i=0;i<groupCount;i++)LocalFree(Marshal.ReadIntPtr(groups,i*IntPtr.Size));
                if(capabilityCount!=1)throw new InvalidOperationException("Windows returned an unexpected registry-read capability.");
                registryReadSid=Marshal.ReadIntPtr(capabilities);
                Capabilities=Marshal.AllocHGlobal(Marshal.SizeOf<SidAttributes>()*(int)CapabilityCount);
                Marshal.StructureToPtr(new SidAttributes { Sid=registryReadSid, Attributes=4 },Capabilities,false);
                if(loggingSid!=IntPtr.Zero)Marshal.StructureToPtr(new SidAttributes { Sid=loggingSid, Attributes=4 },Capabilities+Marshal.SizeOf<SidAttributes>(),false);
            }
            finally { LocalFree(groups);LocalFree(capabilities); }
            if(!OpenProcessToken(GetCurrentProcess(),0x008b,out var currentToken))throw new Win32Exception();

            uint restrictionCount=loggingSid==IntPtr.Zero?1u:2u;
            var restriction=Marshal.AllocHGlobal(Marshal.SizeOf<SidAttributes>()*(int)restrictionCount);
            try
            {
                Marshal.StructureToPtr(new SidAttributes { Sid=WriteSid,Attributes=0 },restriction,false);
                if(loggingGroupSid!=IntPtr.Zero)Marshal.StructureToPtr(new SidAttributes { Sid=loggingGroupSid,Attributes=0 },restriction+Marshal.SizeOf<SidAttributes>(),false);
                if(!CreateRestrictedToken(currentToken,9,0,IntPtr.Zero,0,IntPtr.Zero,restrictionCount,restriction,out var restricted))throw new Win32Exception();
                RestrictedToken=restricted;
                GetTokenInformationBuffer(restricted,6,IntPtr.Zero,0,out int daclSize);var defaultDacl=Marshal.AllocHGlobal(daclSize);
                try
                {
                    if(!GetTokenInformationBuffer(restricted,6,defaultDacl,daclSize,out _))throw new Win32Exception();
                    var oldAcl=Marshal.ReadIntPtr(defaultDacl);var oldBytes=new byte[(ushort)Marshal.ReadInt16(oldAcl,2)];Marshal.Copy(oldAcl,oldBytes,0,oldBytes.Length);
                    var newAcl=new RawAcl(oldBytes,0);newAcl.InsertAce(newAcl.Count,new CommonAce(AceFlags.None,AceQualifier.AccessAllowed,0x10000000,identity,false,null));newAcl.InsertAce(newAcl.Count,new CommonAce(AceFlags.None,AceQualifier.AccessAllowed,0x10000000,writeIdentity,false,null));
                    var aclBytes=new byte[newAcl.BinaryLength];newAcl.GetBinaryForm(aclBytes,0);var aclMemory=Marshal.AllocHGlobal(aclBytes.Length);var daclPointer=Marshal.AllocHGlobal(IntPtr.Size);
                    try { Marshal.Copy(aclBytes,0,aclMemory,aclBytes.Length);Marshal.WriteIntPtr(daclPointer,aclMemory);if(!SetTokenInformation(restricted,6,daclPointer,IntPtr.Size))throw new Win32Exception(); }
                    finally { Marshal.FreeHGlobal(daclPointer);Marshal.FreeHGlobal(aclMemory); }
                }
                finally { Marshal.FreeHGlobal(defaultDacl); }
            }
            finally { Marshal.FreeHGlobal(restriction);CloseHandle(currentToken); }
            Marshal.ThrowExceptionForHR(GetAppContainerFolderPath(identity.Value, out var profilePath));
            try
            {
                var folder = new DirectoryInfo(Marshal.PtrToStringUni(profilePath)!);
                var acl = folder.GetAccessControl(AccessControlSections.Access);
                // Windows needs a registered profile, but agent file writes there are prohibited.
                acl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
                folder.SetAccessControl(acl);
            }
            finally { Marshal.FreeCoTaskMem(profilePath); }
            // Resolve only our private desktop; do not grant station clipboard/screen/desktop-creation rights.
            windowStation=GetProcessWindowStation();
            lock(desktopAclLock)UpdateObjectGrant(windowStation,remove:false);
            var descriptor=new RawSecurityDescriptor($"D:(A;;GA;;;{WindowsIdentity.GetCurrent().User!.Value})(A;;GA;;;{identity.Value})(A;;GA;;;{writeIdentity.Value})S:(ML;;NW;;;LW)");
            var descriptorBytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(descriptorBytes,0);
            var descriptorMemory=Marshal.AllocHGlobal(descriptorBytes.Length);
            try
            {
                Marshal.Copy(descriptorBytes,0,descriptorMemory,descriptorBytes.Length);
                var security=new ObjectSecurity { Length=Marshal.SizeOf<ObjectSecurity>(),Descriptor=descriptorMemory };
                var name="Mason_"+Guid.NewGuid().ToString("N");
                privateDesktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x000f01ff,ref security);
                if(privateDesktop==IntPtr.Zero)throw new Win32Exception();
                var stationName=new System.Text.StringBuilder(256);
                if(!GetUserObjectInformation(windowStation,2,stationName,512,out _))throw new Win32Exception();
                DesktopName=stationName+"\\"+name;
            }
            finally { Marshal.FreeHGlobal(descriptorMemory); }
            GrantAncestorMetadata();
            TraceSetup("identity desktop ancestors ready");
            Grant(Root, FileSystemRights.Modify | FileSystemRights.Synchronize);
            TraceSetup("workspace granted");
            foreach(var runtime in runtimeRoots){TraceSetup("validate runtime "+Path.GetFileName(runtime));ValidateTree(runtime,cancellation);TraceSetup("grant runtime "+Path.GetFileName(runtime));GrantRuntimeRead(runtime);TraceSetup("granted runtime "+Path.GetFileName(runtime));}
            TraceSetup("constructor complete");

        }
        catch { Dispose(); throw; }
    }
    void UpdateObjectGrant(IntPtr handle,bool remove)
    {
        using var coordination=CodeHostCoordination.Acquire();
        uint information=4;GetUserObjectSecurity(handle,ref information,IntPtr.Zero,0,out uint required);
        var buffer=Marshal.AllocHGlobal((int)required);
        try
        {
            if(!GetUserObjectSecurity(handle,ref information,buffer,required,out _))throw new Win32Exception();
            var bytes=new byte[required];Marshal.Copy(buffer,bytes,0,bytes.Length);
            var descriptor=new RawSecurityDescriptor(bytes,0);var acl=descriptor.DiscretionaryAcl!;
            for(int i=acl.Count-1;i>=0;i--)if(acl[i] is CommonAce ace&&ace.SecurityIdentifier==writeIdentity)acl.RemoveAce(i);
            if(!remove)acl.InsertAce(acl.Count,new CommonAce(AceFlags.None,AceQualifier.AccessAllowed,0x00020003,writeIdentity,false,null));
            bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);
            Marshal.FreeHGlobal(buffer);buffer=Marshal.AllocHGlobal(bytes.Length);Marshal.Copy(bytes,0,buffer,bytes.Length);
            if(!SetUserObjectSecurity(handle,ref information,buffer))throw new Win32Exception();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static bool ContainsHost(string workspace,string protectedPath)
    {
        var declaredWorkspace=Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));var declaredProtected=Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedPath));
        if(declaredProtected.Equals(declaredWorkspace,StringComparison.OrdinalIgnoreCase)||declaredProtected.StartsWith(declaredWorkspace+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return true;
        string Normalize(string path)
        {
            path=Path.GetFullPath(path);var suffix=new Stack<string>();var existing=path;
            while(!Directory.Exists(existing))
            {
                suffix.Push(Path.GetFileName(existing));existing=Path.GetDirectoryName(existing)??throw new DirectoryNotFoundException("Cannot resolve protected host path.");
            }
            {
                using var directory=OpenDirectory(existing,0x80,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
                if(directory.IsInvalid)throw new Win32Exception();
                var physical=new System.Text.StringBuilder(32768);uint length=GetFinalPathNameByHandle(directory,physical,(uint)physical.Capacity,1);
                if(length==0||length>=physical.Capacity)throw new Win32Exception();path=physical.ToString();
            }
            while(suffix.Count>0)path=Path.Combine(path,suffix.Pop());
            return Path.TrimEndingDirectorySeparator(path);
        }
        workspace=Normalize(workspace);protectedPath=Normalize(protectedPath);
        return protectedPath.Equals(workspace,StringComparison.OrdinalIgnoreCase)||protectedPath.StartsWith(workspace+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
    public static string RuntimeWorkspaceIdentity(string workspace)
    {
        using var directory=OpenDirectory(Path.GetFullPath(workspace),0x80,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        if(directory.IsInvalid||!GetFileInformationByHandle(directory.DangerousGetHandle(),out var info))throw new Win32Exception();
        var physical=new System.Text.StringBuilder(32768);uint length=GetFinalPathNameByHandle(directory,physical,(uint)physical.Capacity,1);
        if(length==0||length>=physical.Capacity)throw new Win32Exception();
        return Path.TrimEndingDirectorySeparator(physical.ToString()).ToUpperInvariant()+$"|{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}:{info.Created.dwHighDateTime:x8}{info.Created.dwLowDateTime:x8}";
    }
    static void ValidateTree(string root, CancellationToken cancellation)
    {
        int count = 0; var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 200000) throw new InvalidDataException("Auto sandbox inspection exceeded 200,000 entries. Choose a smaller project workspace.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Auto refuses workspace links/junctions: " + Path.GetRelativePath(root,path));
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else
                {
                    using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (!GetFileInformationByHandle(file.DangerousGetHandle(), out var information)) throw new Win32Exception();
                    if (information.Links > 1) throw new UnauthorizedAccessException("Auto refuses files with multiple hard links: " + Path.GetRelativePath(root,path));
                }
            }
        }
    }
    void GrantAncestorMetadata()
    {
        using var handle=OpenDirectory(Root,0x80,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        if(handle.IsInvalid)throw new Win32Exception();
        var path=new System.Text.StringBuilder(32768);uint length=GetFinalPathNameByHandle(handle,path,(uint)path.Capacity,0);
        if(length==0||length>=path.Capacity)throw new Win32Exception();
        string physical=path.ToString(),volume=Path.GetPathRoot(physical)??throw new IOException("Cannot resolve the workspace volume.");
        if(physical.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Auto ancestor metadata requires a local physical workspace.");
        var rights=FileSystemRights.ReadAttributes|FileSystemRights.Traverse|FileSystemRights.Synchronize;int depth=0;
        // Native leading-directory checks skip the volume prefix. Never modify
        // a volume-root ACL or add inherited/list/content/write permissions.
        for(string? parent=Path.GetDirectoryName(physical);parent!=null&&!Path.TrimEndingDirectorySeparator(parent).Equals(Path.TrimEndingDirectorySeparator(volume),StringComparison.OrdinalIgnoreCase);parent=Path.GetDirectoryName(parent))
        {
            if(++depth>256)throw new IOException("Workspace ancestor metadata exceeded the supported depth.");
            if((File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("Auto physical ancestor directories cannot be links.");
            var acl=new DirectoryInfo(parent).GetAccessControl(AccessControlSections.Access);
            var rules=acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            bool publicMetadata=!rules.Any(r=>r.AccessControlType==AccessControlType.Deny)&&rules.Any(r=>r.AccessControlType==AccessControlType.Allow&&(r.PropagationFlags&PropagationFlags.InheritOnly)==0&&r.IdentityReference.Value=="S-1-1-0"&&(r.FileSystemRights&rights)==rights);
            if(!publicMetadata)Grant(parent,rights,inherit:false);
        }
    }
    void Grant(string directory, FileSystemRights rights,bool inherit=true)
    {
        lock(fileAclLock)
        {
        using var coordination=CodeHostCoordination.Acquire();
        var folder = new DirectoryInfo(directory); var acl = folder.GetAccessControl(AccessControlSections.Access);
        var inheritance=inherit?InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit:InheritanceFlags.None;
        acl.AddAccessRule(new FileSystemAccessRule(identity, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(writeIdentity, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        grants.Add(directory);
        if(inherit)folder.SetAccessControl(acl);
        else { metadataGrants.Add(directory);SetDirectoryAclOnly(directory,acl); }
        }
    }
    static void SetDirectoryAclOnly(string directory,DirectorySecurity security)
    {
        // The low-level descriptor setter updates this directory alone; the
        // high-level SetSecurityInfo family propagates existing inheritable ACEs.
        // Request only READ_CONTROL/WRITE_DAC: MAXIMUM_ALLOWED can also request
        // DELETE and conflict with another process's current-directory handle.
        using var handle=OpenDirectory(directory,0x00060000,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot open ancestor metadata ACL: "+directory);
        var descriptor=new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(),0);
        if(descriptor.DiscretionaryAcl==null)throw new InvalidDataException("Ancestor metadata cannot use a null DACL.");
        // Without AUTO_INHERIT_REQ the native setter drops AUTO_INHERITED,
        // causing later child creation to use the legacy, noncanonical model.
        // Preserve an existing modern model on this object only;
        // unlike the high-level setter, no existing child ACL is traversed.
        if((descriptor.ControlFlags&ControlFlags.DiscretionaryAclAutoInherited)!=0)
            descriptor.SetFlags(descriptor.ControlFlags|ControlFlags.DiscretionaryAclAutoInheritRequired);
        var bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);var memory=Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes,0,memory,bytes.Length);int status=NtSetSecurityObject(handle.DangerousGetHandle(),4,memory);
            if(status<0)throw new Win32Exception((int)RtlNtStatusToDosError(status),"Cannot update ancestor metadata ACL: "+directory);
        }
        finally {Marshal.FreeHGlobal(memory);}
    }
    void GrantRuntimeRead(string directory)
    {
        if(Path.GetFileName(directory).StartsWith("git-",StringComparison.OrdinalIgnoreCase))
        {
            // Shared writable PE state must not be reachable by another command identity/workspace.
            Grant(directory,FileSystemRights.ReadAndExecute|FileSystemRights.Synchronize);return;
        }
        // Reuse the existing read capability for public runtime snapshots only. A stable read-only ACE
        // avoids propagating/removing thousands of per-command ACL entries. No restricting SID is broadened.
        lock(fileAclLock)
        {
        using var coordination=CodeHostCoordination.Acquire();
        var reader=new SecurityIdentifier(registryReadSid);var folder=new DirectoryInfo(directory);var acl=folder.GetAccessControl(AccessControlSections.Access);
        var rights=FileSystemRights.ReadAndExecute|FileSystemRights.Synchronize;
        if(acl.GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r=>r.IdentityReference==reader&&r.AccessControlType==AccessControlType.Allow&&(r.FileSystemRights&rights)==rights))return;
        acl.AddAccessRule(new FileSystemAccessRule(reader,rights,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));folder.SetAccessControl(acl);
        }
    }
    string? networkPipe,networkPreload;
    internal void ConfigureNetwork(string pipe)
    {
        networkPipe=pipe;networkPreload=Path.Combine(Scratch,"network.cjs");
        using var source=typeof(CodeSandbox).Assembly.GetManifestResourceStream("Mason.Code.CodeNetworkNode.cjs")??throw new IOException("Network adapter is missing.");
        using var output=File.Create(networkPreload);source.CopyTo(output);
          File.Copy(Path.Combine(AppContext.BaseDirectory,"Mason.NetworkHook.dll"),Path.Combine(Scratch,"Mason.NetworkHook.dll"));
    }
    public IntPtr EnvironmentBlock()
    {
        using(var python=typeof(CodeSandbox).Assembly.GetManifestResourceStream("Mason.Code.CodeNetworkPython.py")??throw new IOException("Python runtime adapter is missing."))
        using(var pythonOutput=File.Create(Path.Combine(Scratch,"sitecustomize.py")))python.CopyTo(pythonOutput);
        string home = Path.Combine(Scratch, "home"), temp = Path.Combine(Scratch, "tmp"); Directory.CreateDirectory(home); Directory.CreateDirectory(temp);
        Directory.CreateDirectory(Path.Combine(home,"Packages",profileName.ToLowerInvariant(),"AC","Temp"));
        // Windows constructs an AppContainer temp path beneath LOCALAPPDATA when a
        // child process starts. Its legacy MAX_PATH handling can leave the child
        // suspended indefinitely when the workspace has a deep path. An 8.3 alias
        // names the same per-command directory without changing the security boundary.
        string localAppData=home;
        if(Path.Combine(home,"Packages",profileName,"AC","Temp").Length>=248)
        {
            var shortPath=new System.Text.StringBuilder(32768);
            uint length=GetShortPathName(home,shortPath,(uint)shortPath.Capacity);
            if(length==0||length>=shortPath.Capacity)throw new IOException("Auto cannot start child processes because the sandbox home path is too long and this volume has no usable short path. Choose a shorter workspace path.");
            string candidate=shortPath.ToString();
            if(Path.Combine(candidate,"Packages",profileName,"AC","Temp").Length>=248||!RuntimeWorkspaceIdentity(candidate).Equals(RuntimeWorkspaceIdentity(home),StringComparison.Ordinal))
                throw new IOException("Auto cannot safely resolve a sufficiently short sandbox home. Choose a shorter workspace path.");
            localAppData=candidate;
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var toolDirectory = Path.GetDirectoryName(Executable)!;
        var values = new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows, ["WINDIR"] = windows, ["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\'),
            ["COMSPEC"] = Path.Combine(windows,"System32","cmd.exe"), ["PATH"] = string.Join(';',runtimePath.Prepend(toolDirectory).Append(Path.Combine(windows,"System32")).Distinct(StringComparer.OrdinalIgnoreCase)),
            // PowerShell uses PATHEXT even for a fully qualified executable. Never inherit host associations.
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            // Node otherwise realpaths every ancestor up to the drive root before loading a workspace script.
            // These loader flags do not change Windows file access checks or authorize links.
            ["NODE_OPTIONS"] = "--preserve-symlinks --preserve-symlinks-main",
            ["HOME"] = home, ["USERPROFILE"] = home, ["LOCALAPPDATA"] = localAppData, ["APPDATA"] = home,
            ["ALLUSERSPROFILE"] = home, ["ProgramData"] = home,
            ["ProgramFiles"] = Path.Combine(home,"programs"), ["ProgramFiles(x86)"] = Path.Combine(home,"programs"),
            ["TEMP"] = temp, ["TMP"] = temp, ["PYTHONNOUSERSITE"] = "1", ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONUTF8"] = "1",["PYTHONPATH"]=Scratch,
            ["TMPDIR"]=temp,["BASH_ENV"]="",["ENV"]="",["MSYS2_PATH_TYPE"]="strict",
            ["_NO_DEBUG_HEAP"]="1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"]="1",["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]="1",["DOTNET_NOLOGO"]="1",["DOTNET_MULTILEVEL_LOOKUP"]="0",["DOTNET_EnableDiagnostics"]="0",["MSBUILDDISABLENODEREUSE"]="1",["MSBuildEnableWorkloadResolver"]="false",["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"]="true",
            ["DOTNET_CLI_HOME"] = home, ["NUGET_PACKAGES"] = Path.Combine(home,"nuget"), ["PIP_CACHE_DIR"] = Path.Combine(home,"pip"),
            ["XDG_CACHE_HOME"] = Path.Combine(home,"cache"), ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = Path.Combine(home,"gitconfig")
        };
        var dotnetRoot=runtimePath.FirstOrDefault(p=>File.Exists(Path.Combine(p,"dotnet.exe")));
        if(dotnetRoot!=null){values["DOTNET_ROOT"]=dotnetRoot;values["DOTNET_ROOT_X64"]=dotnetRoot;}
        values["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"]="false";
        if(networkPipe!=null){values["MASON_NETWORK_PIPE"]=networkPipe;values["NODE_OPTIONS"]+=" --require \""+networkPreload!.Replace('\\','/')+"\"";values["PYTHONPATH"]=Scratch;values["DOTNET_STARTUP_HOOKS"]=Path.Combine(Scratch,"Mason.NetworkHook.dll");values["PIP_DISABLE_PIP_VERSION_CHECK"]="1";values["PIP_CONFIG_FILE"]="NUL";}
        return Marshal.StringToHGlobalUni(string.Join('\0', values.Select(p=>p.Key+"="+p.Value))+"\0\0");
    }
    public void VerifyToken(IntPtr process)=>VerifyToken(process,true);
    internal void VerifyIdentity(IntPtr process)=>VerifyToken(process,false);
    void VerifyToken(IntPtr process,bool prepareObjects)
    {
        if (!OpenProcessToken(process, 8, out var token)) throw new Win32Exception();
        try
        {

            if (!GetTokenInformation(token, 29, out int container, 4, out _) || container != 1) throw new UnauthorizedAccessException("Windows did not create an AppContainer token; Auto was stopped before execution.");
            GetTokenInformationBuffer(token,31,IntPtr.Zero,0,out int required);
            var data=Marshal.AllocHGlobal(required);
            try
            {
                if (!GetTokenInformationBuffer(token,31,data,required,out _)) throw new Win32Exception();
                if (!EqualSid(Sid,Marshal.ReadIntPtr(data))) throw new UnauthorizedAccessException("Sandbox token identity mismatch; Auto was stopped before execution.");
            }
            finally { Marshal.FreeHGlobal(data); }
            GetTokenInformationBuffer(token,11,IntPtr.Zero,0,out required);data=Marshal.AllocHGlobal(required);
            try
            {
                if(!GetTokenInformationBuffer(token,11,data,required,out _))throw new Win32Exception();
                VerifySidSet(data,WriteSid,loggingGroupSid,"Workspace write restriction was not preserved; Auto was stopped before execution.");
            }
            finally { Marshal.FreeHGlobal(data); }
            VerifyPermanentPolicy(token);
            if(!prepareObjects)return;
            GrantObjectNamespace(token);
            GetTokenInformationBuffer(token,6,IntPtr.Zero,0,out required);data=Marshal.AllocHGlobal(required);
            try
            {
                if(!GetTokenInformationBuffer(token,6,data,required,out _))throw new Win32Exception();var dacl=Marshal.ReadIntPtr(data);
                var bytes=new byte[(ushort)Marshal.ReadInt16(dacl,2)];Marshal.Copy(dacl,bytes,0,bytes.Length);
                DefaultAclDiagnostic=string.Join(";",new RawAcl(bytes,0).OfType<CommonAce>().Select(a=>a.SecurityIdentifier.Value+"="+a.AccessMask.ToString("x")));
            }
            finally{Marshal.FreeHGlobal(data);}
        }
        finally { CloseHandle(token); }
    }
    void GrantObjectNamespace(IntPtr token)
    {
        if(ObjectDirectory!=IntPtr.Zero)return;
        var path=new System.Text.StringBuilder(1024);
        if(!GetAppContainerNamedObjectPath(token,IntPtr.Zero,1024,path,out _))throw new Win32Exception();
        var name=path.ToString();if(!name.StartsWith('\\'))name=$"\\Sessions\\{System.Diagnostics.Process.GetCurrentProcess().SessionId}\\"+name;
        var nameMemory=Marshal.StringToHGlobalUni(name);var unicode=new UnicodeString { Length=(ushort)(name.Length*2),MaximumLength=(ushort)((name.Length+1)*2),Buffer=nameMemory };
        var unicodeMemory=Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());IntPtr handle=IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(unicode,unicodeMemory,false);var attributes=new ObjectAttributes { Length=Marshal.SizeOf<ObjectAttributes>(),Name=unicodeMemory,Attributes=64 };
            int status=NtOpenDirectoryObject(out handle,0x0006000f,ref attributes);if(status<0)throw new Win32Exception((int)RtlNtStatusToDosError(status),"Cannot secure private sandbox object namespace: "+name);
            uint error=GetSecurityInfo(handle,6,4,out _,out _,out var dacl,out _,out var descriptor);if(error!=0)throw new Win32Exception((int)error);
            try
            {
                var bytes=new byte[(ushort)Marshal.ReadInt16(dacl,2)];Marshal.Copy(dacl,bytes,0,bytes.Length);var acl=new RawAcl(bytes,0);
                acl.InsertAce(acl.Count,new CommonAce(AceFlags.ContainerInherit|AceFlags.ObjectInherit,AceQualifier.AccessAllowed,0x10000000,writeIdentity,false,null));
                bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);var memory=Marshal.AllocHGlobal(bytes.Length);
                try { Marshal.Copy(bytes,0,memory,bytes.Length);error=SetSecurityInfo(handle,6,4,IntPtr.Zero,IntPtr.Zero,memory,IntPtr.Zero);if(error!=0)throw new Win32Exception((int)error); }
                finally { Marshal.FreeHGlobal(memory); }
            }
            finally { LocalFree(descriptor); }
        }
        catch { if(handle!=IntPtr.Zero)CloseHandle(handle);throw; }
        finally { Marshal.FreeHGlobal(unicodeMemory);Marshal.FreeHGlobal(nameMemory); }
        ObjectDirectory=handle;ObjectDirectoryName=name;
    }
    public void Dispose()
    {
        TraceSetup("dispose begin");
        var errors = new List<Exception>();
        foreach (var directory in grants.AsEnumerable().Reverse())
            try { lock(fileAclLock) { using var coordination=CodeHostCoordination.Acquire();if (Directory.Exists(directory)) { var folder = new DirectoryInfo(directory); var acl = folder.GetAccessControl(AccessControlSections.Access); acl.PurgeAccessRules(identity); acl.PurgeAccessRules(writeIdentity);if(metadataGrants.Contains(directory))SetDirectoryAclOnly(directory,acl);else folder.SetAccessControl(acl); } } } catch(Exception error) { errors.Add(error); }
        grants.Clear();
        TraceSetup("ACL cleanup complete");
        if(ObjectDirectory!=IntPtr.Zero){CloseHandle(ObjectDirectory);ObjectDirectory=IntPtr.Zero;}
        if(privateDesktop!=IntPtr.Zero){if(!CloseDesktop(privateDesktop))errors.Add(new Win32Exception());privateDesktop=IntPtr.Zero;}
        if(windowStation!=IntPtr.Zero){try { lock(desktopAclLock)UpdateObjectGrant(windowStation,remove:true); }catch(Exception error){errors.Add(error);}windowStation=IntPtr.Zero;}
        if(WriteSid!=IntPtr.Zero){Marshal.FreeHGlobal(WriteSid);WriteSid=IntPtr.Zero;}
        if(loggingSid!=IntPtr.Zero){Marshal.FreeHGlobal(loggingSid);loggingSid=IntPtr.Zero;}
        if(loggingGroupSid!=IntPtr.Zero){Marshal.FreeHGlobal(loggingGroupSid);loggingGroupSid=IntPtr.Zero;}
        if(RestrictedToken!=IntPtr.Zero){CloseHandle(RestrictedToken);RestrictedToken=IntPtr.Zero;}
        if(Capabilities!=IntPtr.Zero){Marshal.FreeHGlobal(Capabilities);Capabilities=IntPtr.Zero;}
        if(registryReadSid!=IntPtr.Zero){LocalFree(registryReadSid);registryReadSid=IntPtr.Zero;}
        if (Sid != IntPtr.Zero)
        {
            FreeSid(Sid); Sid = IntPtr.Zero; int result=0;
            for(int attempt=0;attempt<10;attempt++) { result=DeleteAppContainerProfile(profileName);if(result>=0)break;Thread.Sleep(20); }
            if(result<0)errors.Add(Marshal.GetExceptionForHR(result)!);
        }
        if(errors.Count>0)throw new IOException("Sandbox cleanup failed; inspect the workspace ACL/profile before retrying. No unsandboxed fallback was used.",new AggregateException(errors));
        TraceSetup("dispose complete");
    }
    [StructLayout(LayoutKind.Sequential)] struct UnicodeString { public ushort Length,MaximumLength;public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct ObjectAttributes { public int Length;public IntPtr Root,Name;public uint Attributes;public IntPtr Security,Quality; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetAppContainerNamedObjectPath(IntPtr token,IntPtr sid,uint length,System.Text.StringBuilder path,out uint required);
    [DllImport("ntdll.dll")] static extern int NtOpenDirectoryObject(out IntPtr handle,uint access,ref ObjectAttributes attributes);
    [DllImport("ntdll.dll")] static extern uint RtlNtStatusToDosError(int status);
    [DllImport("advapi32.dll")] static extern uint GetSecurityInfo(IntPtr handle,int type,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")] static extern uint SetSecurityInfo(IntPtr handle,int type,uint information,IntPtr owner,IntPtr group,IntPtr dacl,IntPtr sacl);
    [DllImport("ntdll.dll")] static extern int NtSetSecurityObject(IntPtr handle,uint information,IntPtr descriptor);
    [StructLayout(LayoutKind.Sequential)] struct ObjectSecurity { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle,System.Text.StringBuilder path,uint length,uint flags);
    [DllImport("kernel32.dll",EntryPoint="GetShortPathNameW",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetShortPathName(string path,System.Text.StringBuilder shortPath,uint length);
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetUserObjectInformation(IntPtr obj,int index,System.Text.StringBuilder data,uint length,out uint required);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,ref ObjectSecurity security);
    [DllImport("user32.dll",SetLastError=true)] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll",SetLastError=true)] static extern bool GetUserObjectSecurity(IntPtr obj,ref uint information,IntPtr descriptor,uint size,out uint required);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetUserObjectSecurity(IntPtr obj,ref uint information,IntPtr descriptor);
    [StructLayout(LayoutKind.Sequential)] struct SidAttributes { public IntPtr Sid; public uint Attributes; }
    [DllImport("kernelbase.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool DeriveCapabilitySidsFromName(string name,out IntPtr groups,out uint groupCount,out IntPtr capabilities,out uint capabilityCount);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);
    [StructLayout(LayoutKind.Sequential)] struct FileInformation { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written; public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(IntPtr file,out FileInformation information);
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] static extern int GetAppContainerFolderPath(string sid, out IntPtr path);
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool CreateRestrictedToken(IntPtr original,uint flags,uint disabledCount,IntPtr disabled,uint removedCount,IntPtr removed,uint restrictedCount,IntPtr restricted,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetTokenInformation(IntPtr token,int kind,IntPtr value,int length);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int kind,out int value,int length,out int required);
    [DllImport("advapi32.dll",EntryPoint="GetTokenInformation",SetLastError=true)] static extern bool GetTokenInformationBuffer(IntPtr token,int kind,IntPtr value,int length,out int required);
    [DllImport("advapi32.dll")] static extern bool EqualSid(IntPtr first,IntPtr second);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}

