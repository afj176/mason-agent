using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace Mason.Code;

[SupportedOSPlatform("windows")]
public static class CodePowerShellLogging
{
    // The DbCowboy names are kept: they are registered machine-wide (capability SID, HKLM key, setup mutex) by the administrator logging setup.
    public const string CapabilityName="DbCowboy.PowerShell.Logging.v1";
    public const string SetupKey=@"SOFTWARE\DbCowboy\PowerShellLoggingV1";
    public static readonly Guid Provider=new("f90714a8-5509-434a-bf6d-b1624c8a19a2");
    public static string CapabilitySid()=>Derive(false);
    public static string CapabilityGroupSid()=>Derive(true);
    static string Derive(bool group)
    {
        if(!DeriveCapabilitySidsFromName(CapabilityName,out var groups,out uint groupCount,out var capabilities,out uint count))throw new Win32Exception();
        try
        {
            if(count!=1||groupCount!=1)throw new IOException("Unexpected PowerShell logging capability.");
            return new SecurityIdentifier(Marshal.ReadIntPtr(group?groups:capabilities)).Value;
        }
        finally
        {
            for(int i=0;i<groupCount;i++)LocalFree(Marshal.ReadIntPtr(groups,i*IntPtr.Size));
            for(int i=0;i<count;i++)LocalFree(Marshal.ReadIntPtr(capabilities,i*IntPtr.Size));
            LocalFree(groups);LocalFree(capabilities);
        }
    }
    public static bool Provisioned
    {
        get
        {
            using var key=Registry.LocalMachine.OpenSubKey(SetupKey);
            return key?.GetValue("State") as string=="Installed"&&key.GetValue("Capability") as string==CapabilitySid()&&key.GetValue("CapabilityGroup") as string==CapabilityGroupSid()&&key.GetValue("Revision") is int revision&&revision==2;
        }
    }
    [DllImport("kernelbase.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool DeriveCapabilitySidsFromName(string name,out IntPtr groups,out uint groupCount,out IntPtr capabilities,out uint capabilityCount);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
}
