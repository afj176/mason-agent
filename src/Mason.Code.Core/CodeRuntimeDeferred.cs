using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mason.Code;

internal sealed partial class CodeRuntimeBroker
{
    // A termination flag is not proof that all user/kernel execution ended.
    // It authorizes waiting, never bypassing the quiescence barrier.
    sealed class PendingThreadExit(SafeFileHandle witness,uint process,uint thread) : Exception,IDisposable
    {
        public SafeFileHandle Witness {get;}=witness;
        public uint Process {get;}=process;
        public uint Thread {get;}=thread;
        public void Dispose()=>Witness.Dispose();
    }
    sealed record DeferredStop(Target Owner,uint Id,SafeFileHandle Thread,uint Before,IntPtr Address,bool Software,PendingThreadExit Exit,long Started);
    readonly List<DeferredStop> deferredStops=[];
    static IntPtr EffectiveFunction(Target owner,uint thread,int slot)=>slot==0&&owner.DeviceReturns.TryGetValue(thread,out var device)?device.Continuation:slot==0&&owner.Creations.TryGetValue(thread,out var creation)?creation.Continuation:slot==0&&owner.StartupEntries.ContainsKey(thread)?owner.ContinueContext:owner.Functions[slot];
    void ParkDeferredStop(Target owner,uint id,IntPtr data,PendingThreadExit pending)
    {
        if(deferredStops.Count>=64||deferredStops.Any(s=>s.Owner==owner&&s.Id==id))throw new InvalidDataException("Deferred tool-boundary quota exceeded.");
        uint code=unchecked((uint)Marshal.ReadInt32(data));IntPtr address=Marshal.ReadIntPtr(data,16);
        bool software=code==0x80000003&&owner.HelperThunks.Contains(address);
        bool hardware=code==0x80000004&&owner.Initialized&&Enumerable.Range(0,4).Any(i=>EffectiveFunction(owner,id,i)==address);
        if(!software&&!hardware)throw new InvalidDataException("An unrelated exception cannot be converted into a deferred tool request.");
        var thread=HoldOpenThread(0x10085a,false,id);bool retained=false;
        try
        {
            if(thread.IsInvalid||HoldThreadProcess(thread)!=owner.Id)throw new InvalidDataException("Deferred request thread identity changed.");
            uint before=HoldSuspendThread(thread);if(before==uint.MaxValue)throw new Win32Exception();
            WithContext(thread,context=>
            {
                var rip=Marshal.ReadIntPtr(context,248);
                if(rip!=(software?address+1:address))throw new InvalidDataException("The deferred operation already advanced beyond its untouched boundary.");
                // Reexecute only the breakpoint, never an API/syscall. The
                // original CALL/RET and shadow stack remain untouched.
                Marshal.WriteIntPtr(context,248,address);
                Marshal.WriteInt32(context,68,Marshal.ReadInt32(context,68)&~0x10000);Marshal.WriteInt64(context,104,0);
            });
            deferredStops.Add(new(owner,id,thread,before,address,software,pending,Environment.TickCount64));retained=true;
            Note("Deferred untouched tool boundary pid="+owner.Id+" tid="+id+" until thread="+pending.Thread+" actually signals exit.");
        }
        finally{if(!retained)thread.Dispose();}
    }
    void ServiceDeferredStops()
    {
        foreach(var stop in deferredStops.ToArray())
        {
            activeCancellation.ThrowIfCancellationRequested();
            if(Environment.TickCount64-stop.Started>10000)throw new TimeoutException("A pending kernel thread exit prevented safe tool execution.");
            if(!targets.TryGetValue(stop.Owner.Id,out var actual)||!ReferenceEquals(actual,stop.Owner)||HoldThreadWait(stop.Thread,0)==0)
            {
                deferredStops.Remove(stop);stop.Exit.Dispose();stop.Thread.Dispose();continue;
            }
            if(HoldThreadWait(stop.Exit.Witness,0)!=0)continue;
            if(!IsProcessInJob(actual.Handle,job,out bool member)||!member||HoldThreadProcess(stop.Thread)!=actual.Id)throw new InvalidDataException("Deferred boundary lost its actual job/thread identity.");
            sandbox.VerifyIdentity(actual.Handle);
            if(stop.Software?!actual.HelperThunks.Contains(stop.Address):!Enumerable.Range(0,4).Any(i=>EffectiveFunction(actual,stop.Id,i)==stop.Address))throw new InvalidDataException("Deferred boundary's admitted hook plan changed.");
            WithContext(stop.Thread,context=>
            {
                if(Marshal.ReadIntPtr(context,248)!=stop.Address)throw new InvalidDataException("Deferred thread context changed while held.");
                for(int i=0;i<4;i++)Marshal.WriteIntPtr(context,72+i*8,EffectiveFunction(actual,stop.Id,i));
                Marshal.WriteInt64(context,104,0);Marshal.WriteInt64(context,112,0x55);Marshal.WriteInt32(context,68,Marshal.ReadInt32(context,68)&~0x10000);
            });
            if(HoldResumeThread(stop.Thread)!=stop.Before+1)throw new InvalidDataException("The deferred boundary's owned suspension changed.");
            deferredStops.Remove(stop);stop.Exit.Dispose();stop.Thread.Dispose();
            Note("Kernel exit signaled; returned untouched tool boundary to normal interception pid="+actual.Id+" tid="+stop.Id);
        }
    }
    void CloseDeferredStops()
    {
        // Cancellation/failure kills the job first. Closing records must not
        // resume a failed actor just to recover an acknowledgement.
        foreach(var stop in deferredStops){stop.Exit.Dispose();stop.Thread.Dispose();}deferredStops.Clear();
    }
}
