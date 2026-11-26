using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mason.Code;

/// <summary>Read-only admission of the installed runtime and the app's own native payload.</summary>
internal static class CodeHandoffAvailability
{
    internal sealed record Result(bool Available,string Detail);
    static readonly Lazy<Result> current=new(Probe);
    internal static Result Current=>current.Value;

    static Result Probe()
    {
        if(RuntimeInformation.ProcessArchitecture!=Architecture.X64||RuntimeInformation.OSArchitecture!=Architecture.X64)
            return new(false,"Git Bash Auto requires native x64 Windows.");
        try
        {
            CodeHandoffAdmission.VerifyHost();
            string shell=CodeShells.Find(CodeShellKind.GitBash)??throw new FileNotFoundException("Git Bash was not found.");
            // CodeShells selects Git's bin launcher; the actual MSYS runtime
            // and executable staged by Prepare live under usr/bin.
            string gitRoot=Path.GetDirectoryName(Path.GetDirectoryName(shell)!)!;
            string runtime=Path.Combine(gitRoot,"usr","bin","msys-2.0.dll");
            string runtimeShell=Path.Combine(gitRoot,"usr","bin","bash.exe");
            byte[] source=ReadBounded(runtime,64*1024*1024);
            if(CodeHandoffPlan.Hash(source)!=CodeHandoffAdmission.MsysSha256)throw new InvalidDataException("This installed MSYS runtime has not passed handoff acceptance.");
            CodeMsysHeapSites.Validate(source);
            var payload=VerifyPayload(Path.Combine(AppContext.BaseDirectory,"native-handoff"));
            CodeRuntimeImports.RedirectHandoff(CodeRuntimeImage.EnableMsysAslr(source).Image,
                ReadBounded(payload["dbcw.dll"].Path,16*1024*1024),ReadBounded(payload["dbck.dll"].Path,16*1024*1024));
            if(CodeRuntimeImage.PrepareMsysDependency(ReadBounded(runtimeShell,64*1024*1024))==null)throw new InvalidDataException("The selected shell does not match the MSYS image contract.");
            return new(true,"Validated local Windows, Git Bash and first-party handoff payload. Each command is admitted again before execution.");
        }
        catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or PlatformNotSupportedException or Win32Exception or ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or BadImageFormatException)
        {return new(false,error.Message);}
    }
    internal static Dictionary<string,CodeHandoffPlan.Image> VerifyPayload(string directory)
    {
        using var manifest=JsonDocument.Parse(ReadBounded(Path.Combine(directory,"handoff-manifest.json"),256*1024));
        var root=manifest.RootElement;
        if(root.GetProperty("Abi").GetUInt32()!=CodeHandoffRequest.AbiVersion||root.GetProperty("FrameBytes").GetInt32()!=CodeHandoffRequest.FrameBytes)
            throw new InvalidDataException("The native handoff payload ABI does not match this app.");
        string[] names=["dbcw.dll","dbck.dll","Mason.BashBootstrap.exe"];
        var files=root.GetProperty("Files");if(files.GetArrayLength()!=names.Length)throw new InvalidDataException("Incomplete native handoff manifest.");
        var result=new Dictionary<string,CodeHandoffPlan.Image>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in files.EnumerateArray())
        {
            string name=file.GetProperty("Name").GetString()??"";
            if(!names.Contains(name,StringComparer.Ordinal)||result.ContainsKey(name))throw new InvalidDataException("Unexpected or duplicate native handoff payload entry.");
            string path=Path.Combine(directory,name);byte[] bytes=ReadBounded(path,16*1024*1024);
            string hash=CodeHandoffPlan.Hash(bytes);
            if(bytes.LongLength!=file.GetProperty("Bytes").GetInt64()||!hash.Equals(file.GetProperty("Sha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Native handoff payload hash mismatch: "+name);
            result.Add(name,new(path,hash));
        }
        return result;
    }
    static byte[] ReadBounded(string path,int maximum)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        if(input.Length is <=0||input.Length>maximum)throw new InvalidDataException("Unsupported native runtime file size: "+Path.GetFileName(path));
        var bytes=new byte[checked((int)input.Length)];input.ReadExactly(bytes);return bytes;
    }
}
