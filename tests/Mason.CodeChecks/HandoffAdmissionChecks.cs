using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mason.Code;

static class HandoffAdmissionChecks
{
    public static void Run()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        int count=0;void Check(bool value,string label){if(!value)throw new Exception("FAIL "+label);Console.WriteLine("PASS "+label);count++;}
        var hashes=new[]{"ntdll.dll","kernel32.dll","KernelBase.dll"}.ToDictionary(n=>n,n=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),n)))),StringComparer.OrdinalIgnoreCase);
        bool Matches(IReadOnlyDictionary<string,string>? input=null,int schema=CodeHandoffAdmission.Schema,int build=CodeHandoffAdmission.WindowsBuild,Architecture host=Architecture.X64,Architecture os=Architecture.X64)=>CodeHandoffAdmission.Matches(schema,build,host,os,input??hashes);
        Check(Matches(build:Environment.OSVersion.Version.Build,host:RuntimeInformation.ProcessArchitecture,os:RuntimeInformation.OSArchitecture),"actual host/native module identities match the explicit QA admission");
        Check(!Matches(schema:2)&&!Matches(build:CodeHandoffAdmission.WindowsBuild+1),"schema and Windows version changes require new admission");
        Check(!Matches(host:Architecture.Arm64)&&!Matches(os:Architecture.Arm64)&&!Matches(host:Architecture.X86),"emulated and unsupported architecture combinations fail admission");
        foreach(string name in hashes.Keys)
        {
            var changed=new Dictionary<string,string>(hashes,StringComparer.OrdinalIgnoreCase);changed[name]=new string('0',64);Check(!Matches(changed),"changed "+name+" hash invalidates admission");
        }
        var missing=new Dictionary<string,string>(hashes,StringComparer.OrdinalIgnoreCase);missing.Remove("ntdll.dll");Check(!Matches(missing),"missing native identity fails admission");
        var extra=new Dictionary<string,string>(hashes,StringComparer.OrdinalIgnoreCase){{"other.dll",new string('0',64)}};Check(!Matches(extra),"unexpected capability module set fails admission");
        var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new Exception("Git Bash fixture missing.");
        var msys=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(bash))!,"usr","bin","msys-2.0.dll"));
        Check(Convert.ToHexStringLower(SHA256.HashData(msys))==CodeHandoffAdmission.MsysSha256,"actual installed MSYS hash matches inspected call sites");
        CodeMsysHeapSites.Validate(msys);Check(true,"all root, small-fork and large-fork calls match the actual PE preferred base and import thunk");
        var availabilityType=typeof(CodeWorkspace).Assembly.GetType("Mason.Code.CodeHandoffAvailability",true)!;
        var availability=availabilityType.GetProperty("Current",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        bool available=(bool)availability.GetType().GetProperty("Available")!.GetValue(availability)!;
        string detail=(string)availability.GetType().GetProperty("Detail")!.GetValue(availability)!;
        Check(available,"installed Git launcher resolves to the actual usr/bin runtime and verifies the shipped native payload: "+detail);
        Console.WriteLine(count+" handoff admission checks passed.");
    }
}
