#if MASON_BARRIER_QA
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Mason.Code;

internal sealed partial class CodeRuntimeBroker
{
    void CheckQuiescenceDenied()
    {
        try{using var hold=HoldCommand();TerminateJobObject(job,1);throw new InvalidDataException("The inaccessible owned thread was incorrectly admitted.");}
        catch(Win32Exception error)when(error.NativeErrorCode==5){Note("HOLD_DENIED {\"InaccessibleThreadRejected\":true,\"NoDataWrite\":true}");}
    }
    void CheckQuiescenceCancellation()
    {
        using var hold=HoldCommand();
        File.WriteAllText(Path.Combine(sandbox.Root,"quiescence-hold-ready.json"),JsonSerializer.Serialize(hold.ProcessIds));
        if(!activeCancellation.WaitHandle.WaitOne(15000)){hold.Abort();throw new TimeoutException("The held cancellation fixture received no cancellation.");}
        hold.Abort();Note("HOLD_CANCEL stopped the owned job without reviving held actors.");
    }
    void CheckQuiescence(IntPtr exception)
    {
        uint pid=checked((uint)Marshal.ReadInt64(exception,32));var page=Marshal.ReadIntPtr(exception,40);uint preTid=checked((uint)Marshal.ReadInt64(exception,48));
        if(!targets.TryGetValue(pid,out var child)||!IsProcessInJob(child.Handle,job,out bool member)||!member)throw new InvalidDataException("Quiescence fixture nominated a non-owned process.");
        using var suspendProcess=OpenQuiescenceProcess(0xc00,false,pid);using var knownProcess=new SafeFileHandle(child.Handle,false);
        if(suspendProcess.IsInvalid||!CompareObjectHandles(suspendProcess,knownProcess)||!IsProcessInJob(suspendProcess.DangerousGetHandle(),job,out member)||!member)throw new InvalidOperationException("Cannot obtain verified owned suspension handle: "+Marshal.GetLastWin32Error());
        OutputMemory(child,page,40); // Only first-party fixture private data is sampled.
        long[] Sample(){var bytes=Read(child.Handle,page,32);return Enumerable.Range(0,4).Select(i=>BitConverter.ToInt64(bytes,i*8)).ToArray();}
        var initial=Sample();Thread.Sleep(30);var moving=Sample();if(!Enumerable.Range(0,3).All(i=>moving[i]>initial[i])||moving[3]!=0)throw new InvalidDataException("Native counter actors did not initialize.");
        bool bypassContinues=false,hiddenContinues=false;
        int suspendStatus=NtSuspendProcess(suspendProcess.DangerousGetHandle());if(suspendStatus<0)throw new InvalidOperationException("Cannot test owned process suspension: "+suspendStatus.ToString("x8"));
        try{var before=Sample();Thread.Sleep(30);var after=Sample();if(after[0]!=before[0])throw new InvalidDataException("Normal thread did not suspend.");bypassContinues=after[1]>before[1];hiddenContinues=after[2]>before[2];}
        finally{if(NtResumeProcess(suspendProcess.DangerousGetHandle())<0){TerminateJobObject(job,1);throw new InvalidOperationException("Cannot balance owned process suspension.");}}
        var held=new List<(SafeFileHandle Handle,uint Id,uint Previous)>();bool success=false,extraThread=false,presuspended=false;
        try
        {
            for(int pass=0;pass<4;pass++)
            {
                int added=0;SafeFileHandle? cursor=null;
                try
                {
                    while(true)
                    {
                        int status=NtGetNextThread(suspendProcess.DangerousGetHandle(),cursor?.DangerousGetHandle()??IntPtr.Zero,0x100042,0,0,out var next);cursor?.Dispose();cursor=null;
                        if(status==unchecked((int)0x8000001a))break;if(status<0)throw new InvalidOperationException("Owned thread enumeration failed: "+status.ToString("x8"));
                        cursor=new SafeFileHandle(next,true);uint id=GetThreadId(cursor);
                        if(id==0||GetProcessIdOfThread(cursor)!=child.Id)throw new InvalidDataException("Owned thread identity mismatch.");
                        if(held.Any(h=>h.Id==id))continue;if(held.Count>=1024)throw new InvalidDataException("Thread hold quota exceeded.");
                        if(!DuplicateHandle(GetCurrentProcess(),next,GetCurrentProcess(),out var copy,0,false,2))throw new Win32Exception();
                        var owned=new SafeFileHandle(copy,true);uint previous=SuspendThread(owned);
                        if(previous==uint.MaxValue){owned.Dispose();throw new Win32Exception();}
                        held.Add((owned,id,previous));added++;
                        if(!child.Threads.Contains(id))extraThread=true;if(id==preTid&&previous==1)presuspended=true;
                    }
                }
                finally{cursor?.Dispose();}
                if(added==0)break;if(pass==3)throw new InvalidDataException("Owned thread set failed to stabilize.");
            }
            var before=Sample();Thread.Sleep(30);var after=Sample();
            if(held.Count<5||!before.SequenceEqual(after)||!presuspended||after[3]!=0)throw new InvalidDataException("Explicit holds failed to stop all actors or preserve prior suspension.");
            success=true;
        }
        catch{TerminateJobObject(job,1);throw;}
        finally
        {
            foreach(var item in held.AsEnumerable().Reverse())
            {
                uint previous=ResumeHeldThread(item.Handle);item.Handle.Dispose();
                if(success&&previous!=item.Previous+1){TerminateJobObject(job,1);throw new InvalidDataException("Unexpected thread suspend count while balancing the owned hold.");}
            }
        }
        var resumed=Sample();Thread.Sleep(30);var alive=Sample();if(!Enumerable.Range(0,3).All(i=>alive[i]>resumed[i])||alive[3]!=0)throw new InvalidDataException("Explicit hold cleanup changed original thread behavior.");
        var racePage=Marshal.ReadIntPtr(exception,56);int privateWrites=0,rejectedMappings=0,heldThreads=0;
        for(int attempt=0;attempt<100&&(privateWrites<8||rejectedMappings<8);attempt++)
        {
            using(var hold=HoldCommand())
            {
                heldThreads=Math.Max(heldThreads,hold.ThreadCount);var before=Sample();Thread.Sleep(2);if(!before.SequenceEqual(Sample()))throw new InvalidDataException("A hidden actor ran during the command-wide hold.");
                var info=new byte[48];if(VirtualQueryEx(child.Handle,racePage,info,(UIntPtr)info.Length)==UIntPtr.Zero)throw new Win32Exception();
                if(BitConverter.ToUInt32(info,32)==0x1000&&BitConverter.ToUInt32(info,40)==0x20000&&BitConverter.ToUInt32(info,36)==4)
                {
                    WritePrivateHeld(child,racePage+128,Enumerable.Repeat((byte)0x42,8).ToArray(),hold);privateWrites++;
                }
                else
                {
                    bool rejected=false;try{WritePrivateHeld(child,racePage+128,new byte[8],hold);}catch(InvalidDataException){rejected=true;}
                    if(!rejected)throw new InvalidDataException("Mapped/free storage received a host data write.");rejectedMappings++;
                }
            }
            Thread.Sleep(1+(int)(Environment.TickCount64%4));
        }
        if(privateWrites<8||rejectedMappings<8)throw new InvalidDataException("Remapping fixture did not cover both private and non-private states.");
        Note("HOLD_WRITES "+JsonSerializer.Serialize(new{PrivateWrites=privateWrites,MappedOrFreeRejected=rejectedMappings,HiddenRacerStopped=true,CommandThreads=heldThreads}));
        Note("QUIESCENCE "+JsonSerializer.Serialize(new{BulkSuspendSkipsBypass=bypassContinues,BulkSuspendSkipsHiddenBypass=hiddenContinues,FoundThreadAbsentFromDebugEvents=extraThread,ExplicitHoldsStopAll=true,PriorSuspensionPreserved=presuspended,ResumeCountsBalanced=true,Threads=held.Count}));
    }
    [DllImport("ntdll.dll")]static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")]static extern int NtResumeProcess(IntPtr process);
    [DllImport("ntdll.dll")]static extern int NtGetNextThread(IntPtr process,IntPtr cursor,uint access,uint attributes,uint flags,out IntPtr next);
    [DllImport("kernel32.dll",EntryPoint="OpenProcess",SetLastError=true)]static extern SafeFileHandle OpenQuiescenceProcess(uint access,bool inherit,uint id);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint SuspendThread(SafeFileHandle thread);
    [DllImport("kernel32.dll",EntryPoint="ResumeThread",SetLastError=true)]static extern uint ResumeHeldThread(SafeFileHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint GetThreadId(SafeFileHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint GetProcessIdOfThread(SafeFileHandle thread);
}
#endif
