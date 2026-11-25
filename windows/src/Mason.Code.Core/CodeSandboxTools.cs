using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace Mason.Code;

/// <summary>Read-only, broker-owned installed-runtime snapshots. Auto never inherits the host PATH/startup files.</summary>
[SupportedOSPlatform("windows")]
public static class CodeSandboxTools
{
    public static bool PowerShellAutoAvailable=>CodePowerShellLogging.Provisioned;
    internal static readonly bool ProductionGitBashReady=true; // Exact OS/MSYS/payload admission still applies; RC8 forced-continuation gate passed 1,000 fresh identities.
    public sealed record Prepared(string Executable,string[] Roots,string[] SearchPath){internal CodeHandoffPlan? Handoff {get;init;}}
    internal static bool HandoffCandidate {
        get {
#if MASON_HANDOFF_QA
            return true;
#elif MASON_GIT_BASH_QA
            return false; // Preserve the separately compiled legacy compatibility fixture.
#else
            return GitBashAutoAvailable;
#endif
        }
    }
    static readonly object gate=new();
#if MASON_GIT_BASH_QA
    public static bool GitBashAutoAvailable=>true; // Isolated probe build only; never set by the agent's environment.
#else
    public static bool GitBashAutoAvailable=>ProductionGitBashReady&&CodeHandoffAvailability.Current.Available;
#endif
    public static string GitBashAutoUnavailableReason=>ProductionGitBashReady?CodeHandoffAvailability.Current.Detail:"Git Bash Auto startup reliability is under validation.";
    public static string CacheRoot=>Path.Combine(Environment.GetEnvironmentVariable("MASON_DATA_HOME")??(File.Exists(Path.Combine(AppContext.BaseDirectory,"portable.flag"))?Path.Combine(AppContext.BaseDirectory,"data"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MasonAgent")),"sandbox-tools-v2");
    public static Prepared Prepare(string executable,string workspace,CancellationToken cancellation=default)
    {
        executable=Path.GetFullPath(executable);
        if(CodeSandbox.ContainsHost(workspace,CacheRoot))throw new UnauthorizedAccessException("Auto workspace cannot contain the trusted runtime cache.");
        var cmd=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");
        bool native=executable.StartsWith(Path.TrimEndingDirectorySeparator(workspace)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        string? python=Find("python.exe"),node=Find("node.exe"),bash=CodeShells.Find(CodeShellKind.GitBash);
        string? dotnet=Find("dotnet.exe"),pwsh=PowerShellAutoAvailable?Find("pwsh.exe"):null;
        bool Is(string? path)=>path!=null&&Path.GetFullPath(path).Equals(executable,StringComparison.OrdinalIgnoreCase);
        if(Is(bash)&&!GitBashAutoAvailable)throw new UnauthorizedAccessException(GitBashAutoUnavailableReason+" Use direct Python/Node or Command Prompt in Auto. No unrestricted fallback was run.");
        if(!native&&!Is(python)&&!Is(node)&&!Is(bash)&&!Is(dotnet)&&!Is(pwsh)&&!executable.Equals(cmd,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("This executable has not been admitted to Auto. Available runtime families are x64 .NET, Python, Node, Command Prompt, validated Git Bash and compatible workspace-built programs. Preserve the project's language; no unrestricted fallback was run.");
        var roots=new List<string>();var paths=new List<string>();var selected=executable;CodeHandoffPlan? handoff=null;
        lock(gate)
        {
            foreach(var (kind,entry) in new[]{("python",python),("node",node),("git",bash),("dotnet",dotnet),("pwsh",pwsh)})
            {
                if(entry==null||kind=="git"&&!GitBashAutoAvailable)continue;
                var source=Path.GetDirectoryName(entry)!;if(kind=="git")source=Path.GetDirectoryName(source)!;
                if(CodeSandbox.ContainsHost(workspace,source))throw new UnauthorizedAccessException("Installed Auto runtimes must be outside the workspace.");
                string relative=kind=="git"?"usr/bin/bash.exe":Path.GetFileName(entry);
                var files=Inventory(source,kind,cancellation).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
                var stamp=string.Join('\n',files.Select(p=>{var file=new FileInfo(p);return Path.GetRelativePath(source,p)+":"+file.Length+":"+file.LastWriteTimeUtc.Ticks;}));
                using var currentIdentity=System.Security.Principal.WindowsIdentity.GetCurrent();
                var userSid=currentIdentity.User!.Value;
                // MSYS has writable shared PE sections. Different workspaces must use physically different DLL images.
                string? workspaceBinding=kind=="git"?Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CodeSandbox.RuntimeWorkspaceIdentity(workspace)))):null;
                byte[]? compatibility=kind=="git"?File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,HandoffCandidate?"native-handoff":"","dbcw.dll")):null;
                string? compatibilityHash=compatibility==null?null:Convert.ToHexStringLower(SHA256.HashData(compatibility));
                byte[]? facade=kind=="git"&&HandoffCandidate?File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"native-handoff","dbck.dll")):null;
                byte[]? bootstrap=kind=="git"&&HandoffCandidate?File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"native-handoff","Mason.BashBootstrap.exe")):null;
                string handoffKey=facade==null?"":"handoff-abi"+CodeHandoffRequest.AbiVersion+"-schema2:"+CodeHandoffPlan.Hash(facade)+":"+CodeHandoffPlan.Hash(bootstrap!)+":";
                var key=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("runtime-v2:"+handoffKey+(kind=="git"?"git-config-8-dependent-high-aslr-1:"+userSid+":"+workspaceBinding+":"+compatibilityHash:"")+source+"\n"+stamp)));
                var target=Path.Combine(CacheRoot,kind+"-"+key);
                if(!Directory.Exists(target))
                {
                    Directory.CreateDirectory(CacheRoot);var stage=target+".staging-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(stage);
                    try
                    {
                    foreach(var file in files)
                    {
                        cancellation.ThrowIfCancellationRequested();var output=Path.Combine(stage,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.Copy(file,output);
                    }
                    CodeRuntimeImage.Prepared? transformation=null;
                    CodeRuntimeImports.Prepared? imports=null;
                    var dependentImages=new List<object>();
                    if(kind=="git")
                    {
                        var imagePath=Path.Combine(stage,"usr","bin","msys-2.0.dll");
                        transformation=CodeRuntimeImage.EnableMsysAslr(File.ReadAllBytes(imagePath));
                        imports=facade==null?CodeRuntimeImports.Redirect(transformation.Image,compatibility!):CodeRuntimeImports.RedirectHandoff(transformation.Image,compatibility!,facade);
                        File.WriteAllBytes(imagePath,imports.Image);
                        File.WriteAllBytes(Path.Combine(stage,"usr","bin","dbcw.dll"),compatibility!);
                        if(facade!=null)
                        {
                            File.WriteAllBytes(Path.Combine(stage,"usr","bin","dbck.dll"),facade);
                            File.WriteAllBytes(Path.Combine(stage,"usr","bin","Mason.BashBootstrap.exe"),bootstrap!);
                        }
                        foreach(var file in files.Where(p=>new[]{".exe",".dll"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)&&!Path.GetFileName(p).Equals("msys-2.0.dll",StringComparison.OrdinalIgnoreCase)))
                        {
                            cancellation.ThrowIfCancellationRequested();var runtimeRelative=Path.GetRelativePath(source,file);var copied=Path.Combine(stage,runtimeRelative);
                            CodeRuntimeImage.Prepared? prepared;
                            try{prepared=CodeRuntimeImage.PrepareMsysDependency(File.ReadAllBytes(copied));}
                            catch(InvalidDataException error){throw new InvalidDataException("Unsupported installed runtime image: "+runtimeRelative,error);}
                            if(prepared==null)continue;
                            if(prepared.AslrOptIn)File.WriteAllBytes(copied,prepared.Image);
                            dependentImages.Add(new{path=runtimeRelative,preparation=prepared});
                        }
                        Directory.CreateDirectory(Path.Combine(stage,"etc"));File.WriteAllText(Path.Combine(stage,"etc","fstab"),"none / cygdrive binary,posix=0,noacl,user 0 0\nnone /tmp usertemp binary,posix=0,noacl 0 0\n");
                        File.WriteAllText(Path.Combine(stage,"etc","nsswitch.conf"),"passwd: files\ngroup: files\ndb_enum: cache\ndb_home: env\ndb_shell: /usr/bin/bash\ndb_gecos: none\n");
                        var name=Environment.UserName;var domain=Environment.UserDomainName;var prefix=userSid[..userSid.LastIndexOf('-')];var rid=uint.Parse(userSid[(userSid.LastIndexOf('-')+1)..]);
                        File.WriteAllText(Path.Combine(stage,"etc","passwd"),$"{name}:*: {196608u+rid}:197121:U-{domain}\\{name},{userSid}:/home/{name}:/usr/bin/bash\n".Replace("*: ","*:"));
                        File.WriteAllText(Path.Combine(stage,"etc","group"),$"None:{prefix}-513:197121:\nUsers:S-1-5-32-545:545:\n");
                    }
                    File.WriteAllText(Path.Combine(stage,"snapshot.json"),JsonSerializer.Serialize(new{source,kind,key,workspaceBinding,files=files.Length,msysAslr=transformation,msysImports=imports,dependentImages}));
                    // Slow inventory/copy/validation touches only this host's private
                    // staging folder. Serialize the atomic publication, not that work:
                    // another profile must still be able to update/clean its ACLs.
                    using(var coordination=CodeHostCoordination.Acquire(cancellation))
                    {
                        if(!Directory.Exists(target))Directory.Move(stage,target);
                    }
                    // A peer may have published the same validated cache first.
                    // Remove only our unpublished stage; preparation errors still
                    // follow the explicit catch/rethrow path below.
                    if(Directory.Exists(stage)&&Path.GetFullPath(stage).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(CacheRoot))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))Directory.Delete(stage,true);
                    }
                    catch
                    {
                        if(Directory.Exists(stage)&&Path.GetFullPath(stage).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(CacheRoot))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))Directory.Delete(stage,true);
                        throw;
                    }
                }
                if(!File.Exists(Path.Combine(target,relative)))throw new IOException("Incomplete Auto runtime snapshot: "+kind);
                roots.Add(target);
                if(kind=="git"){paths.Add(Path.Combine(target,"usr","bin"));paths.Add(Path.Combine(target,"ucrt64","bin"));paths.Add(Path.Combine(target,"mingw64","bin"));}
                else paths.Add(target);
                if(Is(entry))
                {
                    selected=Path.GetFullPath(Path.Combine(target,relative));
                }
                if(kind=="git"&&HandoffCandidate)handoff=CodeHandoffPlan.Load(target,Path.Combine(target,relative));
            }
        }
        return new(selected,roots.ToArray(),paths.Where(Directory.Exists).ToArray()){Handoff=handoff};
    }
    static string? Find(string name)
    {
        foreach(var path in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))
        {
            if(!Path.IsPathFullyQualified(path.Trim('"')))continue;var file=Path.Combine(path.Trim('"'),name);if(File.Exists(file))return Path.GetFullPath(file);
        }
        return null;
    }
    static IEnumerable<string> Inventory(string root,string kind,CancellationToken cancellation)
    {
        var pending=new Stack<string>();pending.Push(root);int count=0;long bytes=0;
        while(pending.Count>0)
        {
            var directory=pending.Pop();if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("Runtime source directories cannot be links.");
            foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellation.ThrowIfCancellationRequested();var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0)continue;
                var relative=Path.GetRelativePath(root,entry).Replace('\\','/');var parts=relative.Split('/');
                // Installed roots may contain user configuration. Only runtime files belong in snapshots.
                var leaf=Path.GetFileName(entry);
                // A read-only staged SDK is not the machine's MSI installation.
                // Carrying this marker makes even --info initialize the host installer.
                // Omit only the installation-mode marker; never alter installed files.
                if(kind=="dotnet"&&parts.Length==5&&parts[0]=="metadata"&&parts[1]=="workloads"&&parts[3]=="installertype"&&parts[4]=="msi")continue;
                if(leaf.StartsWith(".env",StringComparison.OrdinalIgnoreCase)||leaf.Equals(".npmrc",StringComparison.OrdinalIgnoreCase)||
                    leaf.StartsWith("id_rsa",StringComparison.OrdinalIgnoreCase)||leaf.StartsWith("id_ed25519",StringComparison.OrdinalIgnoreCase)||
                    new[]{".pem",".key",".pfx",".p12"}.Contains(Path.GetExtension(entry),StringComparer.OrdinalIgnoreCase)&&!(kind=="python"&&relative=="Lib/site-packages/pip/_vendor/certifi/cacert.pem"))continue;
                if(parts.Length==1&&(attributes&FileAttributes.Directory)==0&&kind is "python" or "node")
                {
                    var extension=Path.GetExtension(entry);
                    if(!new[]{".exe",".dll",".zip",".txt",".md",".cmd",".ps1"}.Contains(extension,StringComparer.OrdinalIgnoreCase)&&
                        !new[]{"LICENSE","npm","npx","corepack"}.Contains(parts[0],StringComparer.OrdinalIgnoreCase))continue;
                }
                bool allowed=kind switch
                {
                    "python"=>!parts.Any(p=>p is "__pycache__" or "test" or "idlelib" or "turtledemo")&&(parts.Length==1||parts[0] is "Lib" or "DLLs")&&(!parts.Contains("site-packages")||parts.Length==2||parts.Length>=3&&(parts[2]=="pip"||parts[2].StartsWith("pip-",StringComparison.Ordinal)&&parts[2].EndsWith(".dist-info",StringComparison.Ordinal))),
                    "node"=>parts.Length==1||parts[0]=="node_modules"&&(parts.Length<2||parts[1] is "npm" or "corepack"),
                    "git"=>(parts[0] is "usr" or "ucrt64" or "mingw64")&&(parts.Length<2||parts[1] is "bin" or "lib" or "libexec")||parts.Length==1&&parts[0].EndsWith(".txt",StringComparison.OrdinalIgnoreCase),
                    "dotnet"=>parts.Length==1&&(leaf=="dotnet.exe"||leaf.EndsWith(".txt",StringComparison.OrdinalIgnoreCase))||parts[0] is "sdk" or "shared" or "host" or "packs" or "templates" or "sdk-manifests" or "metadata",
                    "pwsh"=>!leaf.EndsWith("profile.ps1",StringComparison.OrdinalIgnoreCase)&&!leaf.Equals("powershell.config.json",StringComparison.OrdinalIgnoreCase)&&(parts.Length==1||parts[0] is "Modules" or "en-US" or "ref" or "Schemas"),
                    _=>false
                };
                if(!allowed)continue;
                if((attributes&FileAttributes.Directory)!=0){pending.Push(entry);continue;}
                if(++count>40000||(bytes+=new FileInfo(entry).Length)>2L*1024*1024*1024)throw new InvalidDataException("Installed runtime snapshot exceeds the bounded file/size limit.");
                yield return entry;
            }
        }
    }
}
