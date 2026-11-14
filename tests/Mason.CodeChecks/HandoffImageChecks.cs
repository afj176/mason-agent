using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Mason.Code;

static class HandoffImageChecks
{
    public static void Run(string directory)
    {
        var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new Exception("Installed Git Bash is required.");
        string msys=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(bash))!,"usr","bin","msys-2.0.dll");
        byte[] source=File.ReadAllBytes(msys),compat=File.ReadAllBytes(Path.Combine(directory,"dbcw.dll")),facade=File.ReadAllBytes(Path.Combine(directory,"dbck.dll"));
        var prepared=CodeRuntimeImage.EnableMsysAslr(source);
        CodeMsysHeapSites.Validate(source);CodeMsysHeapSites.Validate(prepared.Image);
        var result=CodeRuntimeImports.RedirectHandoff(prepared.Image,compat,facade);int count=0;
        void Check(bool pass,string name){if(!pass)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);count++;}
        Check(result.Imports.Contains("NtCreateDirectoryObject")&&result.Imports.Contains("NtCreateSection")&&result.Imports.Contains("VirtualAlloc"),"handoff imports contain both native adaptations and allocation facade");
        Check(result.ChangedOffsets.Length>0&&result.ChangedOffsets.Length<=23&&result.ChangedOffsets.All(i=>result.Image[i]!=prepared.Image[i]),"handoff changes are confined to two fixed import module names");
        Check(result.SourceSha256==prepared.SnapshotSha256&&result.SnapshotSha256==Convert.ToHexStringLower(SHA256.HashData(result.Image)),"handoff audit binds exact staged source and result");
        Check(source.SequenceEqual(File.ReadAllBytes(msys))&&compat.SequenceEqual(File.ReadAllBytes(Path.Combine(directory,"dbcw.dll")))&&facade.SequenceEqual(File.ReadAllBytes(Path.Combine(directory,"dbck.dll"))),"installed MSYS and helper input bytes remain unchanged");
        void Reject(byte[] input,byte[] helper,byte[] kernel,string label)
        {
            bool denied=false;try{CodeRuntimeImports.RedirectHandoff(input,helper,kernel);}catch(InvalidDataException){denied=true;}Check(denied,label);
        }
        byte[] Alter(byte[] original,string text,int index,char replacement)
        {
            var data=original.ToArray();var pattern=Encoding.ASCII.GetBytes(text+"\0");bool found=false;
            for(int scan=0;scan<=data.Length-pattern.Length;scan++)
                if(data.AsSpan(scan,pattern.Length).SequenceEqual(pattern)){data[scan+index]=(byte)replacement;found=true;}
            if(!found)throw new Exception("Missing fixture "+text);return data;
        }
        Reject(prepared.Image,compat[..100],facade,"truncated native helper fails admission");
        Reject(prepared.Image,compat,facade[..100],"truncated allocation facade fails admission");
        Reject(prepared.Image,compat,Alter(facade,"KERNEL32.IdnToAscii",0,'x'),"forwarder to another module fails admission");
        Reject(prepared.Image,compat,Alter(facade,"KERNEL32.IdnToAscii",9,'x'),"forwarder to another system symbol fails admission");
        Reject(prepared.Image,Alter(compat,"DbcBrokerCall",0,'x'),facade,"missing first-party scalar thunk fails admission");
        Reject(prepared.Image,Alter(compat,"NtCreateSection",0,'x'),facade,"missing native adaptation fails admission");
        Reject(Alter(prepared.Image,"KERNEL32.dll",0,'x'),compat,facade,"missing original system import module fails admission");
        Reject(prepared.Image,compat,Alter(facade,"VirtualAlloc",0,'x'),"missing local allocation wrapper fails admission");
        Reject(prepared.Image,compat,Alter(facade,"RtlRestoreContext",0,'x'),"missing local context-preservation wrapper fails admission");
        Reject(prepared.Image,compat,Alter(facade,"dbck.dll",0,'x'),"wrong facade module identity fails admission");
        int opt=(int)BinaryPrimitives.ReadUInt32LittleEndian(prepared.Image.AsSpan(60))+24;
        var signed=prepared.Image.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(opt+144),512);
        Reject(signed,compat,facade,"signed vendor images receive no import transformation");
        var old=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"dbcw.dll"));
        Reject(prepared.Image,old,facade,"legacy eight-wrapper helper cannot satisfy handoff ABI");
        bool reverse=false;try{CodeRuntimeImports.Redirect(prepared.Image,compat);}catch(InvalidDataException){reverse=true;}
        Check(reverse,"handoff helper cannot silently enter the normal eight-wrapper path");
        Console.WriteLine(count+" handoff image checks passed.");
    }
}
