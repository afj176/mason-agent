using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Mason.Code;
using Microsoft.Win32;

if(args.Length!=1||args[0] is not ("install-powershell-logging" or "remove-powershell-logging" or "status"))throw new ArgumentException("Use install-powershell-logging, remove-powershell-logging or status.");
if(args[0]=="status"){Console.WriteLine(CodePowerShellLogging.Provisioned?"Installed":"Not installed");return;}
using var identity=WindowsIdentity.GetCurrent();
if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("This optional setup requires administrator approval.");
// Name kept: it is registered machine-wide together with CodePowerShellLogging.SetupKey.
using var mutex=new Mutex(false,@"Global\DbCowboy.PowerShellLoggingV1.Setup");
if(!mutex.WaitOne(TimeSpan.FromSeconds(10)))throw new IOException("Another logging setup is active.");
using var coordination=Registry.LocalMachine.CreateSubKey(CodePowerShellLogging.SetupKey,true);
try
{
    using var security=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\WMI\Security",true)??throw new IOException("ETW security store unavailable.");
    string? ValueName()=>security.GetValueNames().SingleOrDefault(n=>Guid.TryParse(n,out var guid)&&guid==CodePowerShellLogging.Provider);
    byte[] Query(){var guid=CodePowerShellLogging.Provider;uint bytes=0;uint error=Native.EventAccessQuery(ref guid,null,ref bytes);if(error is not (122 or 234)||bytes is <20 or >65536)throw new Win32Exception((int)error);var result=new byte[bytes];error=Native.EventAccessQuery(ref guid,result,ref bytes);if(error!=0)throw new Win32Exception((int)error);return result;}
    if(args[0]=="remove-powershell-logging")
    {
        if(coordination.GetValue("State") as string!="Installed")throw new IOException("No completed setup journal exists.");
        var after=(byte[])coordination.GetValue("After")!;
        if(!Query().SequenceEqual(after))throw new IOException("ETW policy changed after setup; refusing to overwrite it.");
        string? name=ValueName();if(name==null)throw new IOException("Provisioned provider value is missing.");
        if((int)coordination.GetValue("Existed")! ==1)security.SetValue(name,(byte[])coordination.GetValue("OriginalValue")!,RegistryValueKind.Binary);else security.DeleteValue(name);
        security.Flush();
        if(!Query().SequenceEqual((byte[])coordination.GetValue("Before")!))throw new IOException("Exact policy restoration did not verify.");
        coordination.SetValue("State","Removed");Console.WriteLine("Exact PowerShell provider policy restored.");return;
    }
    bool upgrading=coordination.GetValue("State") as string=="Installed";
    if(coordination.GetValue("State") as string=="Prepared")throw new IOException("An incomplete setup journal requires administrator recovery; it will not be overwritten.");
    var before=Query();var descriptor=new RawSecurityDescriptor(before,0);
    if(upgrading&&!before.SequenceEqual((byte[])coordination.GetValue("After")!))throw new IOException("ETW policy changed after setup; refusing upgrade.");
    var identities=new[]{new SecurityIdentifier(CodePowerShellLogging.CapabilitySid()),new SecurityIdentifier(CodePowerShellLogging.CapabilityGroupSid())};
    if(descriptor.DiscretionaryAcl==null)throw new IOException("Unsupported provider policy.");
    var additions=identities.Where(sid=>!descriptor.DiscretionaryAcl.OfType<KnownAce>().Any(a=>a.SecurityIdentifier==sid)).ToArray();
    foreach(var ace in descriptor.DiscretionaryAcl.OfType<KnownAce>().Where(a=>identities.Contains(a.SecurityIdentifier)))
        if(!upgrading||ace is not CommonAce known||known.AccessMask!=0x800||known.AceFlags!=AceFlags.None||known.AceQualifier!=AceQualifier.AccessAllowed)throw new IOException("Unsupported pre-existing logging grant.");
    if(additions.Length==0){Console.WriteLine("Logging capability already installed.");return;}
    string? existing=ValueName();var original=existing==null?Array.Empty<byte>():security.GetValue(existing) as byte[]??throw new IOException("Unsupported provider policy value.");
    if(!upgrading){coordination.SetValue("Before",before,RegistryValueKind.Binary);coordination.SetValue("OriginalValue",original,RegistryValueKind.Binary);coordination.SetValue("Existed",existing==null?0:1);}
    coordination.SetValue("Capability",identities[0].Value);coordination.SetValue("State","Prepared");coordination.Flush();
    try
    {
        foreach(var sid in additions)
        {
            var bytes=new byte[sid.BinaryLength];sid.GetBinaryForm(bytes,0);var memory=Marshal.AllocHGlobal(bytes.Length);
            try{Marshal.Copy(bytes,0,memory,bytes.Length);var guid=CodePowerShellLogging.Provider;uint error=Native.EventAccessControl(ref guid,2,memory,0x800,true);if(error!=0)throw new Win32Exception((int)error);}
            finally{Marshal.FreeHGlobal(memory);}
        }
        var after=Query();var changed=new RawSecurityDescriptor(after,0);
        foreach(var sid in identities){var matches=changed.DiscretionaryAcl?.OfType<CommonAce>().Where(a=>a.SecurityIdentifier==sid).ToArray()??[];if(matches.Length!=1||matches[0].AccessMask!=0x800||matches[0].AceQualifier!=AceQualifier.AccessAllowed||matches[0].AceFlags!=AceFlags.None)throw new IOException("Unexpected logging capability rights.");}
        var restored=new RawSecurityDescriptor(after,0);for(int i=restored.DiscretionaryAcl!.Count-1;i>=0;i--)if(restored.DiscretionaryAcl[i] is KnownAce ace&&additions.Contains(ace.SecurityIdentifier))restored.DiscretionaryAcl.RemoveAce(i);
        if(restored.GetSddlForm(AccessControlSections.All)!=descriptor.GetSddlForm(AccessControlSections.All))throw new IOException("Setup modified existing ETW policy.");
        coordination.SetValue("After",after,RegistryValueKind.Binary);coordination.SetValue("CapabilityGroup",identities[1].Value);coordination.SetValue("Revision",2);coordination.SetValue("State","Installed");coordination.DeleteValue("LastError",false);coordination.Flush();Console.WriteLine("PowerShell logging registration capability installed.");
    }
    catch
    {
        var current=Query();var rollback=new RawSecurityDescriptor(current,0);
        if(!current.SequenceEqual(before))
        {
            if(rollback.DiscretionaryAcl==null)throw new IOException("ETW policy changed concurrently; refusing rollback overwrite.");
            var ours=rollback.DiscretionaryAcl.OfType<CommonAce>().Where(a=>additions.Contains(a.SecurityIdentifier)).ToArray();
            if(ours.Any(a=>a.AccessMask!=0x800||a.AceQualifier!=AceQualifier.AccessAllowed||a.AceFlags!=AceFlags.None))throw new IOException("ETW policy changed concurrently; refusing rollback overwrite.");
            for(int i=rollback.DiscretionaryAcl.Count-1;i>=0;i--)if(rollback.DiscretionaryAcl[i] is KnownAce ace&&additions.Contains(ace.SecurityIdentifier))rollback.DiscretionaryAcl.RemoveAce(i);
            if(rollback.GetSddlForm(AccessControlSections.All)!=descriptor.GetSddlForm(AccessControlSections.All))throw new IOException("ETW policy changed concurrently; refusing rollback overwrite.");
        }
        var name=ValueName();if(name!=null){if(existing==null)security.DeleteValue(name);else security.SetValue(name,original,RegistryValueKind.Binary);security.Flush();}
        if(!Query().SequenceEqual(before))throw new IOException("PowerShell logging setup failed and exact rollback could not be verified.");
        coordination.SetValue("State",upgrading?"Installed":"RolledBack");throw;
    }
}
catch(Exception error){coordination.SetValue("LastError",error.ToString());coordination.Flush();throw;}

static class Native
{
    [DllImport("sechost.dll")]internal static extern uint EventAccessQuery(ref Guid provider,byte[]? descriptor,ref uint length);
    [DllImport("sechost.dll")]internal static extern uint EventAccessControl(ref Guid provider,uint operation,IntPtr sid,uint rights,[MarshalAs(UnmanagedType.U1)]bool allow);
}
