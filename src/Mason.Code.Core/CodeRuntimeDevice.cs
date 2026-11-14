using System.Runtime.InteropServices;

namespace Mason.Code;

internal sealed partial class CodeRuntimeBroker
{
    sealed record DeviceReturn(IntPtr Continuation,IntPtr Stack,byte[] Saved,IntPtr Output,IntPtr IoStatus,int OriginalFunction,long Started);
    sealed partial class Target
    {
        public IntPtr DuplicateObject;
        public readonly Dictionary<uint,DeviceReturn> DeviceReturns=[];
    }
    void BeginCryptoDuplicate(Target target,uint id,IntPtr context,int function,uint access)
    {
        if(target.DuplicateObject==IntPtr.Zero||target.DeviceReturns.ContainsKey(id)||targets.Values.Sum(t=>t.DeviceReturns.Count)>=64)throw new InvalidDataException("Fixed-device native return adapter is unavailable or exceeded its bound.");
        if(!target.CryptoRoots.TryGetValue(access,out var root)||!SameObject(target,root,crypto))
        {
            if(target.CryptoRoots.Count>=8&&!target.CryptoRoots.ContainsKey(access))throw new InvalidDataException("Fixed cryptography capability bound exceeded.");
            root=Duplicate(crypto.DangerousGetHandle(),target.Handle,access);target.CryptoRoots[access]=root;
        }
        IntPtr stack=Marshal.ReadIntPtr(context,152),output=Marshal.ReadIntPtr(context,128),io=Marshal.ReadIntPtr(context,192);
        OutputMemory(target,stack+40,24);OutputMemory(target,io,16);
        IntPtr continuation=(IntPtr)BitConverter.ToInt64(Read(target.Handle,stack,8));
        var memory=new byte[48];
        if(VirtualQueryEx(target.Handle,continuation,memory,(UIntPtr)48)!=(UIntPtr)48||BitConverter.ToUInt32(memory,32)!=0x1000||(BitConverter.ToUInt32(memory,36)&0xf0)==0)throw new InvalidDataException("Fixed-device return continuation is not executable.");
        var saved=Read(target.Handle,stack+40,24);var arguments=new byte[24];BitConverter.GetBytes((ulong)access).CopyTo(arguments,0);
        // No host write to the handle output, whatever its allocation type.
        // NtDuplicateObject executes in the original restricted actor.
        SafeWrite(target,stack+40,arguments);
        target.DeviceReturns.Add(id,new(continuation,stack,saved,output,io,function,Environment.TickCount64));
        Marshal.WriteIntPtr(context,128,(IntPtr)(-1)); // source process: self
        Marshal.WriteIntPtr(context,136,root);       // fixed, verified capability
        Marshal.WriteIntPtr(context,184,(IntPtr)(-1)); // target process: self
        Marshal.WriteIntPtr(context,192,output);    // actor/kernel owns this output
        Marshal.WriteIntPtr(context,248,target.DuplicateObject);
        Marshal.WriteIntPtr(context,72,continuation);
        Note("Original restricted actor duplicates only its fixed Ksec capability; rights="+access.ToString("x"));
    }
    void CompleteCryptoDuplicate(Target target,uint id,IntPtr context)
    {
        var pending=target.DeviceReturns[id];target.DeviceReturns.Remove(id);
        int status=Marshal.ReadInt32(context,120);var saved=pending.Saved.ToArray();
        if(status>=0)
        {
            var output=Read(target.Handle,pending.Output,8);IntPtr handle=(IntPtr)BitConverter.ToInt64(output);
            if(!SameObject(target,handle,crypto))throw new InvalidDataException("The native device result was not the identical fixed Ksec file object.");
            // A legitimate output can alias a caller-stack slot borrowed for
            // the seventh argument. Preserve the kernel-produced bytes while
            // restoring this PRIVATE frame; mapped outputs are never written.
            long start=(long)pending.Stack+40,at=(long)pending.Output;
            for(int i=0;i<8;i++)if(at+i>=start&&at+i<start+saved.Length)saved[checked((int)(at+i-start))]=output[i];
        }
        SafeWrite(target,pending.Stack+40,saved);
        var io=new byte[16];BitConverter.GetBytes(status).CopyTo(io,0);BitConverter.GetBytes(status>=0?1L:0L).CopyTo(io,8);
        SafeWrite(target,pending.IoStatus,io);
        Marshal.WriteIntPtr(context,72,EffectiveFunction(target,id,0));
        if(status>=0)CryptoReady(target,id,context,pending.OriginalFunction);
        Note("Fixed-device native result status="+status.ToString("x8")+"; private caller frame restored.");
    }
    void CryptoReady(Target target,uint id,IntPtr context,int originalFunction)
    {
        if(originalFunction==0&&target.AnonymousPipe!=IntPtr.Zero)
        {
            bool msys=target.Section!=IntPtr.Zero&&target.Modules.Any(m=>m.Name.Equals("msys-2.0.dll",StringComparison.OrdinalIgnoreCase));
            target.Functions[0]=msys?target.Section:NativeAnonymousPipes?target.OpenFile:target.AnonymousPipe;
            target.SectionHook=msys;target.AnonymousHook=!msys&&!NativeAnonymousPipes;target.Rearm=true;Marshal.WriteIntPtr(context,72,EffectiveFunction(target,id,0));
            if(!msys&&target.ContinueContext!=IntPtr.Zero){target.DirectoryRetired=true;target.StartupContextHook=true;target.Functions[3]=target.ContinueContext;Marshal.WriteIntPtr(context,96,target.ContinueContext);}
        }
    }
}
