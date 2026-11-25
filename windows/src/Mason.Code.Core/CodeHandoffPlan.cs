using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Mason.Code;

/// <summary>Host-created snapshot inventory. Never constructed from model arguments or a workspace manifest.</summary>
internal sealed class CodeHandoffPlan
{
    internal sealed record Image(string Path,string Hash);
    public string Bootstrap {get;}
    public string LogicalShell {get;}
    public IReadOnlyDictionary<string,Image> MsysImages {get;}
    public IReadOnlyDictionary<string,Image> Helpers {get;}
    public Image Runtime {get;}
    public Image ConsoleHost {get;}
    public IReadOnlyDictionary<string,Image> WindowsModules {get;}
    CodeHandoffPlan(string root,string shell)
    {
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if(RuntimeInformation.OSArchitecture!=Architecture.X64||RuntimeInformation.ProcessArchitecture!=Architecture.X64)throw new PlatformNotSupportedException("The handoff candidate admits only a native x64 host and Windows installation.");
        WindowsModules=CodeHandoffAdmission.VerifyHost();
        LogicalShell=Path.GetFullPath(shell);Bootstrap=Path.Combine(root,"usr","bin","Mason.BashBootstrap.exe");
        using var snapshot=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"snapshot.json")));
        // The call-site contract below was inspected in this exact installed
        // MSYS image. A different runtime needs a new admission record.
        if(!snapshot.RootElement.GetProperty("msysAslr").GetProperty("SourceSha256").GetString()!.Equals(CodeHandoffAdmission.MsysSha256,StringComparison.Ordinal))throw new InvalidDataException("Unadmitted MSYS version for the heap handoff candidate.");
        var images=new Dictionary<string,Image>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in snapshot.RootElement.GetProperty("dependentImages").EnumerateArray())
        {
            string relative=item.GetProperty("path").GetString()??throw new InvalidDataException("Missing handoff image path.");
            string path=Path.GetFullPath(Path.Combine(root,relative));
            if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Path.IsPathFullyQualified(relative))throw new InvalidDataException("Invalid snapshot image path.");
            images.Add(path,new(path,item.GetProperty("preparation").GetProperty("SnapshotSha256").GetString()!));
        }
        if(!images.ContainsKey(LogicalShell))throw new InvalidDataException("The selected shell is absent from the handoff inventory.");
        MsysImages=images;
        var helpers=new Dictionary<string,Image>(StringComparer.OrdinalIgnoreCase);
        var payload=CodeHandoffAvailability.VerifyPayload(Path.Combine(AppContext.BaseDirectory,"native-handoff"));
        foreach(string name in new[]{"dbcw.dll","dbck.dll","Mason.BashBootstrap.exe"})
        {
            string expected=payload[name].Hash;
            string path=Path.Combine(root,"usr","bin",name);if(Hash(File.ReadAllBytes(path))!=expected)throw new InvalidDataException("Handoff helper hash mismatch.");
            helpers.Add(path,new(path,expected));
        }
        Helpers=helpers;
        Runtime=new(Path.Combine(root,"usr","bin","msys-2.0.dll"),snapshot.RootElement.GetProperty("msysImports").GetProperty("SnapshotSha256").GetString()!);
        CodeMsysHeapSites.Validate(File.ReadAllBytes(Runtime.Path));
        string console=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"conhost.exe");
        ConsoleHost=new(console,Hash(File.ReadAllBytes(console)));
    }
    internal static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static CodeHandoffPlan Load(string root,string shell)=>new(root,shell);
}
