using System.Buffers.Binary;
using System.Security.Cryptography;
using Mason.Code;

static class RuntimeImageChecks
{
    public static void Run(Action<bool,string> check)
    {
        check(CodeNullDevice.Access(@"\Device\Null",0xc0000000)==0x12019f&&CodeNullDevice.Access(@"\??\NUL",0x100001)==0x100001,"fixed Null requests map only bounded read/write/synchronize rights");
        check(new[]{@"\Device\Null\host",@"\??\C:\NUL",@"\Device\PhysicalDrive0",@"\??\NUL.txt"}.All(n=>CodeNullDevice.Access(n,0x80000000)==null),"Null compatibility rejects arbitrary devices and alias-like paths");
        check(new uint[]{0x40000,0x80000,0x1000000,0x2000000,0x10000000,0x20000000,0x20}.All(a=>CodeNullDevice.Access(@"\Device\Null",a)==null),"Null compatibility rejects ACL, owner, security, maximum, generic-all and execute authority");
        check(CodePrivatePipes.RelativeName("0123456789abcdef-123-pipe-nt-0xA",123,true)!=null&&CodePrivatePipes.RelativeName("0123456789abcdef-124-pipe-nt-0x1",123,true)==null,"new MSYS relative pipe buffers require a bounded installation key, creating process ID and counter");
        check(new[]{"0123456789abcdef-123-pipe-nt-0x0","0123456789abcdef-123-pipe-nt-0x100000000","0123456789abcdef-123-pipe-nt-0x1/host","0123456789abcdef-123-pipe-nt-0x1\\host","0123456789abcdef-+123-pipe-nt-0x1","host-pipe"}.All(n=>CodePrivatePipes.RelativeName(n,123,true)==null),"relative pipe IDs reject traversal, arbitrary host names, signs, zero and overflow");
        const string space=@"\Sessions\1\AppContainerNamedObjects\S-1-15-2-1-2-3",user="S-1-5-21-1-2-3-1001";
        bool Allows(string? directory=space+@"\msys-test",string name="shared.5",string kind="Directory",long size=60344,uint protection=4,uint allocation=0x08000000,long file=0,string owned=space)=>CodePrivateSections.Allows(kind,directory,owned,name,user,size,protection,allocation,file);
        check(Allows()&&Allows(directory:space)&&Allows(name:user+".1"),"private section policy admits only known MSYS section names in the exact command namespace");
        check(!Allows(directory:@"\BaseNamedObjects\msys-test")&&!Allows(directory:space+"9\\msys-test")&&!Allows(directory:space+"\\msys-test\\host")&&!Allows(directory:null)&&!Allows(owned:""),"host namespaces, SID prefix collisions, nested paths and missing identities receive no section compatibility");
        check(!Allows(name:"host-section")&&!Allows(name:"../shared.5")&&!Allows(name:"S-1-5-21-9.1")&&!Allows(kind:"Section"),"unrelated names, traversal, another user and non-directory handles receive no section compatibility");
        check(!Allows(file:123)&&!Allows(protection:0x40)&&!Allows(allocation:0x01000000)&&!Allows(size:0)&&!Allows(size:1048577),"file-backed, executable, image and oversized memory requests receive no section compatibility");
        check(Allows(name:"cygpid.1")&&Allows(name:"cygpid.4294967295")&&new[]{"cygpid.0","cygpid.-1","cygpid.+1","cygpid. 1","cygpid.4294967296","cygpid.1/host","cygpid.1x"}.All(n=>!Allows(name:n)),"fork sections accept only nonzero uint32 decimal cygpid identifiers under the same private policy");
        if(!OperatingSystem.IsWindows())return;
        var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new Exception("Git Bash is required for the runtime acceptance fixture.");
        var path=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(bash))!,"usr","bin","msys-2.0.dll");
        var source=File.ReadAllBytes(path);var before=SHA256.HashData(source);var result=CodeRuntimeImage.EnableMsysAslr(source);
        int pe=(int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(60)),opt=pe+24;
        var flags=BinaryPrimitives.ReadUInt16LittleEndian(result.Image.AsSpan(opt+70));
        var changed=Enumerable.Range(0,source.Length).Where(i=>source[i]!=result.Image[i]).ToArray();
        check((flags&0x60)==0x60&&changed.All(i=>i==opt+70)&&changed.Length<=1,"staged MSYS transformation changes only the ASLR header byte for dynamic/high-entropy opt-in, preserving every code byte");
        check(result.SourceSha256==Convert.ToHexStringLower(before)&&result.SnapshotSha256==Convert.ToHexStringLower(SHA256.HashData(result.Image)),"snapshot audit hashes bind exact source and transformed image bytes");
        check(SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(before)&&SHA256.HashData(source).SequenceEqual(before),"snapshot preparation preserves the installed runtime and caller input");
        var again=CodeRuntimeImage.EnableMsysAslr(result.Image);
        check(!again.AslrOptIn&&again.SourceSha256==again.SnapshotSha256,"vendor ASLR-enabled runtime images remain byte-for-byte unchanged");
        void Reject(byte[] image,string name)
        {
            bool rejected=false;try{CodeRuntimeImage.EnableMsysAslr(image);}catch(InvalidDataException){rejected=true;}
            check(rejected,name);
        }
        var signed=source.ToArray();BinaryPrimitives.WriteUInt16LittleEndian(signed.AsSpan(opt+70),(ushort)(BinaryPrimitives.ReadUInt16LittleEndian(signed.AsSpan(opt+70))&~0x40));BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(opt+144),512);BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(opt+148),128);
        Reject(signed,"signature metadata prohibits modifying a staged runtime image");
        var architecture=source.ToArray();BinaryPrimitives.WriteUInt16LittleEndian(architecture.AsSpan(pe+4),0x14c);Reject(architecture,"non-x64 MSYS images fail closed");
        var relocations=source.ToArray();relocations.AsSpan(opt+152,8).Clear();Reject(relocations,"ASLR preparation rejects images missing validated relocations");
        Reject(source[..100],"truncated PE headers fail closed before any transformation");
        var narrow=source.ToArray();BinaryPrimitives.WriteUInt16LittleEndian(narrow.AsSpan(pe+22),(ushort)(BinaryPrimitives.ReadUInt16LittleEndian(narrow.AsSpan(pe+22))&~0x20));Reject(narrow,"64-bit ASLR requires existing large-address awareness; that characteristic is never added");
        var lowEntropy=result.Image.ToArray();BinaryPrimitives.WriteUInt16LittleEndian(lowEntropy.AsSpan(opt+70),(ushort)(flags&~0x20));var highEntropy=CodeRuntimeImage.EnableMsysAslr(lowEntropy);
        check(highEntropy.ChangedOffsets.SequenceEqual(new[]{opt+70})&&highEntropy.Image[opt+70]==(byte)(lowEntropy[opt+70]|0x20),"existing dynamic ASLR receives only the missing approved high-entropy bit");
        var helper=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"dbcw.dll"));var helperBefore=SHA256.HashData(helper);
        var redirected=CodeRuntimeImports.Redirect(result.Image,helper);
        var actualChanges=Enumerable.Range(0,source.Length).Where(i=>redirected.Image[i]!=result.Image[i]).ToArray();
        check(actualChanges.SequenceEqual(redirected.ChangedOffsets)&&actualChanges.Length is >0 and <=10&&actualChanges.Max()-actualChanges.Min()<10,"native import staging changes only the fixed module-name metadata bytes");
        check(redirected.Imports.Length>=50&&redirected.Imports.Contains("NtCreateMutant")&&redirected.Imports.Contains("NtCreateSection"),"every redirected native import has a validated wrapper or exact system forwarder");
        check(redirected.SourceSha256==result.SnapshotSha256&&redirected.CompatibilitySha256==Convert.ToHexStringLower(helperBefore)&&redirected.SnapshotSha256==Convert.ToHexStringLower(SHA256.HashData(redirected.Image)),"import audit binds source, final image and first-party helper hashes");
        check(SHA256.HashData(helper).SequenceEqual(helperBefore)&&SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(before),"import staging preserves installed MSYS and the trusted helper source");
        void RejectImports(byte[] input,byte[] dll,string label)
        {
            bool rejected=false;try{CodeRuntimeImports.Redirect(input,dll);}catch(InvalidDataException){rejected=true;}check(rejected,label);
        }
        RejectImports(result.Image,helper[..100],"truncated compatibility DLL fails closed");
        var bound=result.Image.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(bound.AsSpan(opt+200),1);RejectImports(bound,helper,"bound native import layouts are rejected");
        var delay=result.Image.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(delay.AsSpan(opt+216),1);RejectImports(delay,helper,"delayed native import layouts are rejected");
        var signedImports=result.Image.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(signedImports.AsSpan(opt+144),512);RejectImports(signedImports,helper,"import redirection rejects signed image metadata even after ASLR opt-in");
        var wrongForward=helper.ToArray();int forward=wrongForward.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes("ntdll.NtClose\0"));if(forward<0)throw new Exception("Missing forwarding fixture.");wrongForward[forward]=(byte)'x';RejectImports(result.Image,wrongForward,"changed system export-forwarding destinations are rejected");
        var bashPath=Path.Combine(Path.GetDirectoryName(path)!,"bash.exe");var bashSource=File.ReadAllBytes(bashPath);
        var bashPrepared=CodeRuntimeImage.PrepareMsysDependency(bashSource)??throw new Exception("Bash must directly import MSYS.");
        int bashOpt=(int)BinaryPrimitives.ReadUInt32LittleEndian(bashSource.AsSpan(60))+24;
        var bashChanges=Enumerable.Range(0,bashSource.Length).Where(i=>bashPrepared.Image[i]!=bashSource[i]).ToArray();
        check(bashChanges.SequenceEqual(bashPrepared.ChangedOffsets)&&bashChanges.All(i=>i==bashOpt+70)&&bashChanges.Length<=1,"staged Bash executable transformation changes only its ASLR header bit");
        check(CodeRuntimeImage.PrepareMsysDependency(bashPrepared.Image) is {AslrOptIn:false}&&File.ReadAllBytes(bashPath).SequenceEqual(bashSource),"executable ASLR staging is idempotent and preserves installed Bash");
        check(CodeRuntimeImage.PrepareMsysDependency(helper)==null,"non-MSYS dependencies receive no ASLR transformation");
        var bashSigned=bashSource.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(bashSigned.AsSpan(bashOpt+144),512);
        bool signedRejected=false;try{CodeRuntimeImage.PrepareMsysDependency(bashSigned);}catch(InvalidDataException){signedRejected=true;}check(signedRejected,"signed MSYS-dependent executables are rejected before transformation");
        var bashReloc=bashSource.ToArray();bashReloc.AsSpan(bashOpt+152,8).Clear();bool relocRejected=false;try{CodeRuntimeImage.PrepareMsysDependency(bashReloc);}catch(InvalidDataException){relocRejected=true;}check(relocRejected,"MSYS-dependent executables require supported relocation metadata");
    }
}
