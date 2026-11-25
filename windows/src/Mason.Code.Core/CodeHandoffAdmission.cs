using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Mason.Code;

/// <summary>Versioned QA admission, not a promise that future Windows builds share this private ABI.</summary>
public static class CodeHandoffAdmission
{
    public const int Schema=1,WindowsBuild=26300;
    public const string MsysSha256="2a89b7c31b323c42e2fb37900af087aeb27417694e630a210bc75b0369410bc7";
    static readonly Dictionary<string,string> modules=new(StringComparer.OrdinalIgnoreCase)
    {
        ["ntdll.dll"]="651ac9041af8e6b5ebd331d53163244c0a27d36d1433f0f15cac1f28c5f2e82c",
        ["kernel32.dll"]="e132eba4166256e2f414b91fe7d5386590bc5ea8626aee5af9c9738383cf2ae1",
        ["KernelBase.dll"]="5c29b73622ce0976e1ada566f31f2197ba0f8beaea89c3ec033366126172c3d0"
    };
    public static bool Matches(int schema,int build,Architecture host,Architecture os,IReadOnlyDictionary<string,string> hashes)
        =>schema==Schema&&build==WindowsBuild&&host==Architecture.X64&&os==Architecture.X64&&hashes.Count==modules.Count&&modules.All(m=>hashes.TryGetValue(m.Key,out var hash)&&StringComparer.OrdinalIgnoreCase.Equals(hash,m.Value));
    internal static IReadOnlyDictionary<string,CodeHandoffPlan.Image> VerifyHost()
    {
        string system=Environment.GetFolderPath(Environment.SpecialFolder.System);
        var actual=modules.Keys.ToDictionary(n=>n,n=>new CodeHandoffPlan.Image(Path.Combine(system,n),Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(system,n))))),StringComparer.OrdinalIgnoreCase);
        if(!Matches(Schema,Environment.OSVersion.Version.Build,RuntimeInformation.ProcessArchitecture,RuntimeInformation.OSArchitecture,actual.ToDictionary(p=>p.Key,p=>p.Value.Hash,StringComparer.OrdinalIgnoreCase)))throw new PlatformNotSupportedException("This Windows/native-module combination has not passed the Git Bash handoff candidate's acceptance tests. No compatibility fallback was run.");
        return actual.Values.ToDictionary(m=>m.Path,StringComparer.OrdinalIgnoreCase);
    }
}
