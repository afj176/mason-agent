using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mason.Code;

internal sealed partial class CodeRuntimeBroker
{
#if MASON_NATIVE_PIPES_QA
    bool NativeAnonymousPipes=>true;
#else
    bool NativeAnonymousPipes=>sandbox.Handoff!=null;
#endif
    sealed record AnonymousPending(uint Process,SafeFileHandle Witness,string Path,long Created);
    readonly List<AnonymousPending> anonymousPending=[];
    SafeFileHandle? pipeRoot;
    void CloseAnonymous(uint? process)
    {
        foreach(var entry in anonymousPending.Where(p=>process==null||p.Process==process).ToArray()){anonymousPending.Remove(entry);entry.Witness.Dispose();}
    }
    void ExpireAnonymous()
    {
        foreach(var entry in anonymousPending.Where(p=>Environment.TickCount64-p.Created>5000).ToArray()){anonymousPending.Remove(entry);entry.Witness.Dispose();Note("Expired an unpaired new anonymous-pipe witness.");}
    }
    bool SameObject(Target target,IntPtr candidate,SafeFileHandle expected)
    {
        if(candidate==IntPtr.Zero||!DuplicateHandle(target.Handle,candidate,GetCurrentProcess(),out var copy,0,false,0))return false;
        using var owned=new SafeFileHandle(copy,true);return CompareObjectHandles(owned,expected);
    }
    void OutputMemory(Target target,IntPtr pointer,int bytes)
    {
        var info=new byte[48];
        if(VirtualQueryEx(target.Handle,pointer,info,(UIntPtr)info.Length)==UIntPtr.Zero)throw new InvalidDataException("Invalid native IPC output storage.");
        ulong start=(ulong)BitConverter.ToInt64(info),length=(ulong)BitConverter.ToInt64(info,24),at=(ulong)(long)pointer;
        if(BitConverter.ToUInt32(info,32)!=0x1000||BitConverter.ToUInt32(info,40)!=0x20000||BitConverter.ToUInt32(info,36)!=4||at<start||at-start>length||(ulong)bytes>length-(at-start))
            throw new InvalidDataException("Native IPC outputs must be private, committed, non-executable read/write memory. pid="+target.Id+" pointer="+Location(target,pointer)+" state="+BitConverter.ToUInt32(info,32).ToString("x")+" type="+BitConverter.ToUInt32(info,40).ToString("x")+" protection="+BitConverter.ToUInt32(info,36).ToString("x"));
    }
    void AnonymousReturn(Target target,IntPtr context,int function,IntPtr handle,int information)
    {
        var output=Marshal.ReadIntPtr(context,128);var status=Marshal.ReadIntPtr(context,192);
        using var hold=HoldCommand();
        WritePrivateHeld(target,output,BitConverter.GetBytes((long)handle),hold);var io=new byte[16];BitConverter.GetBytes((long)information).CopyTo(io,8);WritePrivateHeld(target,status,io,hold);Return(target,context,function);
    }
    bool TryAnonymous(Target target,IntPtr context,byte[] attributes,string name,uint access,int function,bool fileOpen)
    {
        var root=(IntPtr)BitConverter.ToInt64(attributes,8);bool inherit=(BitConverter.ToUInt32(attributes,24)&2)!=0;
        if(fileOpen&&root==IntPtr.Zero&&name.Equals(@"\Device\NamedPipe\",StringComparison.OrdinalIgnoreCase))
        {
            if(access!=0x80100000||inherit)return false; // Other runtime root opens retain their original LPAC checks.
            if(pipeRoot==null)
            {
                pipeRoot=CreateFile(@"\\?\GLOBALROOT\Device\NamedPipe\",0,3,IntPtr.Zero,3,0,IntPtr.Zero);
                if(pipeRoot.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot acquire the zero-access pipe root.");
                if(ObjectText(pipeRoot.DangerousGetHandle(),2)!="File"||ObjectText(pipeRoot.DangerousGetHandle(),1)!=@"\Device\NamedPipe\")throw new InvalidDataException("Fixed pipe-root identity mismatch.");
            }
            AnonymousReturn(target,context,function,Duplicate(pipeRoot.DangerousGetHandle(),target.Handle,0),1);Note("Delegated fixed pipe-root object with zero granted access.");return true;
        }
        if(name.Length!=0||root==IntPtr.Zero)return false;
        if(function==1)
        {
            if(pipeRoot==null||!SameObject(target,root,pipeRoot)){Return(target,context,function,unchecked((int)0xc0000022));return true;}
            var args=Read(target.Handle,Marshal.ReadIntPtr(context,152)+40,72);
            uint incoming=BitConverter.ToUInt32(args,56),outgoing=BitConverter.ToUInt32(args,64);
            if(access!=0x80100100||BitConverter.ToUInt32(args)!=3||BitConverter.ToUInt32(args,8)!=2||BitConverter.ToUInt32(args,16)!=0x20||BitConverter.ToUInt32(args,24)!=0||BitConverter.ToUInt32(args,32)!=0||BitConverter.ToUInt32(args,40)!=0||BitConverter.ToUInt32(args,48)!=1||incoming>1024*1024||outgoing>1024*1024)
                throw new InvalidDataException("Unsupported native anonymous-pipe parameters.");
            pipeBudget.Admit(incoming,outgoing);
            string path=@"\\.\pipe\Mason.Code."+Guid.NewGuid().ToString("N");var security=new Security{Length=Marshal.SizeOf<Security>()};
            using var server=CreateNamedPipe(path,3|0x80000,8,1,Math.Max(outgoing,4096u),Math.Max(incoming,4096u),0,ref security);
            if(server.IsInvalid)throw new Win32Exception();
            var delegated=Duplicate(server.DangerousGetHandle(),target.Handle,0x120189,inherit);
            var witness=new SafeFileHandle(Duplicate(server.DangerousGetHandle(),GetCurrentProcess(),0),true);
            anonymousPending.Add(new(target.Id,witness,path,Environment.TickCount64));
            AnonymousReturn(target,context,function,delegated,2);RetainFileOpen(target,context);
            if(target.SectionHook){target.SectionHook=false;target.Functions[0]=target.OpenFile;target.Rearm=true;Marshal.WriteIntPtr(context,72,target.OpenFile);}
            Note("Created new native anonymous-pipe server; bounded witness awaits its client.");return true;
        }
        if(!fileOpen)return false;
        var pending=anonymousPending.FirstOrDefault(p=>p.Process==target.Id&&SameObject(target,root,p.Witness));
        if(pending==null)
        {
            // MSYS also opens its working directory with an empty relative
            // name. Do not turn those unrelated filesystem checks into IPC.
            if(!DuplicateHandle(target.Handle,root,GetCurrentProcess(),out var candidate,0,false,0))return false;
            using var owned=new SafeFileHandle(candidate,true);
            if(ObjectText(candidate,2)!="File"||GetFileType(owned)!=3)return false;
            Return(target,context,function,unchecked((int)0xc0000022));return true;
        }
        try
        {
            var args=Read(target.Handle,Marshal.ReadIntPtr(context,152)+40,16);
            if(access!=0x40100080||BitConverter.ToUInt32(args)!=3||BitConverter.ToUInt32(args,8)!=0x60)throw new InvalidDataException("Unsupported native anonymous-pipe client.");
            using var client=CreateFile(pending.Path,0x40000080,0,IntPtr.Zero,3,0x00100000,IntPtr.Zero);
            if(client.IsInvalid)throw new Win32Exception();
            AnonymousReturn(target,context,function,Duplicate(client.DangerousGetHandle(),target.Handle,0x120196,inherit),1);
            Note("Paired native anonymous pipe by actual object identity; no source endpoints retained.");
        }
        finally{anonymousPending.Remove(pending);pending.Witness.Dispose();}
        return true;
    }
    [DllImport("KernelBase.dll",SetLastError=true)]static extern bool CompareObjectHandles(SafeFileHandle first,SafeFileHandle second);
}
