using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mason.Code;

internal sealed partial class CodeRuntimeBroker
{
    CommandHold? currentHold;
    int censusAudits=0,censusQueries=0,jobCensusChanges=0;
    sealed class CommandHold(CodeRuntimeBroker broker) : IDisposable
    {
        readonly Dictionary<uint,SafeFileHandle> processes=[];
        readonly List<(SafeFileHandle Handle,uint Process,uint Id,uint Before,bool OwnedCount)> threads=[];
        readonly List<Action> afterRelease=[];
        bool completed,disposed,aborted;
        int leases=1;
        long debugWitness;
        readonly Stopwatch elapsed=Stopwatch.StartNew();
        void Bound(){if(elapsed.Elapsed>TimeSpan.FromSeconds(2))throw new TimeoutException("Owned thread hold did not stabilize within its bound.");}
        HashSet<uint> Members()
        {
            var data=new byte[8+64*8];string lastCounts="";
            for(int attempt=0;attempt<8;attempt++)
            {
                Bound();
                if(!QueryHoldJob(broker.job,3,data,(uint)data.Length,out _))throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot enumerate the owned job.");
                uint assigned=BitConverter.ToUInt32(data),count=BitConverter.ToUInt32(data,4);
                lastCounts="assigned="+assigned+" listed="+count;
                if(assigned>64||count>64)throw new InvalidDataException("Owned job membership exceeded its complete snapshot bound: "+lastCounts);
                // An exiting process can change the two counts during the
                // kernel query. Never consume the partial list: obtain a new
                // complete snapshot within the same hold/deadline bounds.
                if(count!=assigned){broker.jobCensusChanges++;broker.TraceQa("job census changed assigned="+assigned+" listed="+count);continue;}
                var result=Enumerable.Range(0,(int)count).Select(i=>checked((uint)BitConverter.ToInt64(data,8+i*8))).ToHashSet();
                if(result.Count!=count||result.Contains(0))throw new InvalidDataException("Owned job snapshot contains invalid or duplicate process identities.");
                return result;
            }
            throw new InvalidDataException("Owned job membership did not stabilize into a complete snapshot ("+lastCounts+"); no data write is permitted.");
        }
        List<(uint Process,uint Thread)> Snapshot(HashSet<uint> members)
        {
#if !MASON_CENSUS_AUDIT
            if(broker.sandbox.Handoff!=null)
            {
                broker.TraceQa("native thread census begin");var result=CodeThreadCensus.Capture(members);broker.censusQueries++;broker.TraceQa("native thread census end owned="+result.Count);return result;
            }
#endif
            return ToolhelpSnapshot(members);
        }
        List<(uint Process,uint Thread)> ToolhelpSnapshot(HashSet<uint> members)
        {
            broker.TraceQa("thread census begin");
            using var snapshot=HoldSnapshot(4,0);if(snapshot.IsInvalid)throw new Win32Exception();
            var result=new List<(uint,uint)>();var entry=new HoldThreadEntry{Size=(uint)Marshal.SizeOf<HoldThreadEntry>()};
            bool available=HoldThreadFirst(snapshot,ref entry);
            if(!available&&Marshal.GetLastWin32Error()!=18)throw new Win32Exception();
            while(available)
            {
                if(members.Contains(entry.Owner))result.Add((entry.Owner,entry.Id));
                entry.Size=(uint)Marshal.SizeOf<HoldThreadEntry>();available=HoldThreadNext(snapshot,ref entry);
            }
            if(Marshal.GetLastWin32Error()!=18)throw new Win32Exception();
            if(result.GroupBy(t=>t.Item1).Any(g=>g.Count()>1024))throw new InvalidDataException("Owned thread snapshot exceeded the process bound.");
            broker.TraceQa("thread census end owned="+result.Count);return result;
        }
        public void Acquire()
        {
            try
            {
                if(IsProcessInJob(GetCurrentProcess(),broker.job,out bool self)&&self)throw new InvalidOperationException("The event-loop host cannot belong to its command job.");
                for(int pass=0;pass<8;pass++)
                {
                    Bound();var members=Members();int added=0;bool changed=false;
                    foreach(uint pid in members)
                    {
                        if(processes.ContainsKey(pid))continue;
                        var process=HoldOpenProcess(0x1400,false,pid);
                        if(process.IsInvalid)
                        {
                            int error=Marshal.GetLastWin32Error();process.Dispose();
                            // An exited PID can disappear between complete
                            // job snapshots. Restart; every remaining live
                            // member still requires actual identity checks.
                            if(error==87&&!Members().Contains(pid)){changed=true;break;}
                            throw new Win32Exception(error);
                        }
                        if(!IsProcessInJob(process.DangerousGetHandle(),broker.job,out bool member)||!member){process.Dispose();throw new InvalidDataException("Process identity changed during the owned hold.");}
                        try{broker.sandbox.VerifyIdentity(process.DangerousGetHandle());}catch{process.Dispose();throw;}
                        processes.Add(pid,process);added++;
                    }
                    if(changed)continue;
                    foreach(var item in Snapshot(members))
                    {
                        Bound();var thread=HoldOpenThread(0x10084a,false,item.Thread);
                        if(thread.IsInvalid)
                        {
                            int error=Marshal.GetLastWin32Error();thread.Dispose();
                            if(error==87&&!Snapshot(Members()).Any(t=>t.Process==item.Process&&t.Thread==item.Thread)){changed=true;break;}
                            throw new Win32Exception(error,"An owned thread could not be held; no data write is permitted.");
                        }
                        if(HoldThreadProcess(thread)!=item.Process){thread.Dispose();throw new InvalidDataException("Thread identity changed during the owned hold.");}
                        if(threads.Any(t=>t.Id==item.Thread&&t.Process==item.Process&&CompareObjectHandles(t.Handle,thread))){thread.Dispose();continue;}
                        // The thread which raised the currently uncontinued
                        // debug exception is already held by the kernel. It
                        // can be inside termination callbacks and refuse an
                        // additional SuspendThread. No termination flag is
                        // used as proof: the active event generation is.
                        bool ownedCount=!broker.IsActiveDebugStop(item.Process,item.Thread);
                        uint before=ownedCount?HoldSuspendThread(thread):0;
                        if(!ownedCount)debugWitness=broker.debugStopGeneration;
                        if(ownedCount&&before==uint.MaxValue)
                        {
                            int error=Marshal.GetLastWin32Error();bool exited=HoldThreadWait(thread,0)==0;
                            var info=Marshal.AllocHGlobal(4);
                            int terminalStatus,terminalValue;
                            try{Marshal.WriteInt32(info,0);terminalStatus=HandoffQueryThread(thread.DangerousGetHandle(),20,info,4,out _);terminalValue=Marshal.ReadInt32(info);broker.Note("Failed suspend state pid="+item.Process+" tid="+item.Thread+" signaled="+exited+" queryStatus="+terminalStatus.ToString("x8")+" kernelTerminated="+terminalValue);}
                            finally{Marshal.FreeHGlobal(info);}
                            if(!exited&&terminalStatus>=0&&terminalValue==1)throw new PendingThreadExit(thread,item.Process,item.Thread);
                            thread.Dispose();
                            // A permanently signaled thread can no longer
                            // mutate memory. Retry membership discovery;
                            // never skip a live thread on an access denial.
                            if(exited){added++;broker.Note("Observed an already-exited thread during the hold pid="+item.Process+" tid="+item.Thread);continue;}
                            throw new Win32Exception(error,"An unsignaled owned thread could not be suspended pid="+item.Process+" tid="+item.Thread+" queryStatus="+terminalStatus.ToString("x8")+" kernelTerminated="+terminalValue);
                        }
                        threads.Add((thread,item.Process,item.Thread,before,ownedCount));added++;
                        // Synchronize the asynchronous suspend before declaring
                        // a safe point. Never set a caller's register context.
                        var memory=Marshal.AllocHGlobal(1250);var context=(IntPtr)(((long)memory+15)&~15L);
                        try{broker.TraceQa("context begin pid="+item.Process+" tid="+item.Thread+" ownedCount="+ownedCount+" prior="+before);Marshal.WriteInt32(context,48,0x100001);broker.ContextStage("barrier GetThreadContext "+item.Process+"/"+item.Thread+" ownSuspend="+ownedCount);if(!GetThreadContext(thread,context))throw new Win32Exception();broker.TraceQa("context end pid="+item.Process+" tid="+item.Thread);}
                        finally{broker.ContextStage("none");Marshal.FreeHGlobal(memory);}
                    }
                    if(changed)continue;
                    if(added==0&&Members().SetEquals(members))
                    {
                        var current=Snapshot(members);
                        if(current.All(t=>threads.Any(h=>h.Process==t.Process&&h.Id==t.Thread)))
                        {
#if MASON_CENSUS_AUDIT
                            var native=CodeThreadCensus.Capture(members);
                            var toolhelp=Snapshot(members);
                            if(!native.ToHashSet().SetEquals(toolhelp))throw new InvalidDataException("Native and Toolhelp thread censuses disagree while the owned command is frozen.");
                            broker.censusAudits++;
#endif
                            completed=true;return;
                        }
                    }
                }
                throw new InvalidDataException("Owned job/thread membership did not stabilize; no data write is permitted.");
            }
            catch(PendingThreadExit){Dispose();throw;} // No write occurred; the event loop must wait for actual signaling.
            catch{Abort();Dispose();throw;}
        }
        public int ThreadCount=>threads.Count;
        public bool Owns(uint process)=>completed&&!disposed&&!aborted&&!broker.activeCancellation.IsCancellationRequested&&(debugWitness==0||broker.debugStopActive&&broker.debugStopGeneration==debugWitness)&&processes.ContainsKey(process);
        public uint[] ProcessIds=>processes.Keys.ToArray();
        public void AfterRelease(Action action){if(!completed||disposed||aborted)throw new InvalidOperationException("Cannot attach cleanup to an incomplete hold.");afterRelease.Add(action);}
        public CommandHold Retain(){if(!completed||disposed||aborted)throw new InvalidOperationException("Cannot borrow an incomplete hold.");broker.activeCancellation.ThrowIfCancellationRequested();if(leases>=32)throw new InvalidDataException("Nested owned hold bound exceeded.");leases++;return this;}
        public void Abort(){aborted=true;TerminateJobObject(broker.job,1);}
        public void Dispose()
        {
            if(disposed)return;if(--leases>0)return;disposed=true;Exception? failure=null;
            if(broker.activeCancellation.IsCancellationRequested)Abort();
            foreach(var item in threads.AsEnumerable().Reverse())
            {
                if(item.OwnedCount&&!aborted&&HoldThreadWait(item.Handle,0)!=0)
                {
                    uint before=HoldResumeThread(item.Handle);
                    if(completed&&before!=item.Before+1)failure??=new InvalidDataException("An owned suspension count changed unexpectedly.");
                }
                item.Handle.Dispose();
            }
            foreach(var process in processes.Values)process.Dispose();
            if(ReferenceEquals(broker.currentHold,this))broker.currentHold=null;
            if(failure!=null){TerminateJobObject(broker.job,1);throw failure;}
            if(broker.activeCancellation.IsCancellationRequested)Abort();
            if(!aborted)try{foreach(var action in afterRelease)action();}catch{TerminateJobObject(broker.job,1);throw;}
        }
    }
    CommandHold HoldCommand(){if(currentHold!=null)return currentHold.Retain();var hold=new CommandHold(this);hold.Acquire();currentHold=hold;return hold;}
    void WritePrivateHeld(Target target,IntPtr address,byte[] bytes,CommandHold hold)
    {
        activeCancellation.ThrowIfCancellationRequested();
        if(!hold.Owns(target.Id)||bytes.Length is <=0 or >16384)throw new InvalidDataException("A data write requires a live owned hold and bounded frame.");
        OutputMemory(target,address,bytes.Length);
#if MASON_BARRIER_QA
        Thread.Sleep(2); // Deliberately widen validation/write to expose an unfrozen remapper.
#endif
        int status=NtWriteHeldMemory(target.Handle,address,bytes,(UIntPtr)bytes.Length,out var written);
        if(status<0||(ulong)written!=(ulong)bytes.Length)throw new InvalidDataException("Raw private data write failed without changing page protection: "+status.ToString("x8"));
    }
    [DllImport("ntdll.dll",EntryPoint="NtWriteVirtualMemory")]static extern int NtWriteHeldMemory(IntPtr process,IntPtr address,byte[] data,UIntPtr size,out UIntPtr written);
    [StructLayout(LayoutKind.Sequential)]struct HoldThreadEntry {public uint Size,Usage,Id,Owner;public int Base,Delta;public uint Flags;}
    [DllImport("kernel32.dll",EntryPoint="QueryInformationJobObject",SetLastError=true)]static extern bool QueryHoldJob(IntPtr job,int information,[Out]byte[] buffer,uint size,out uint required);
    [DllImport("kernel32.dll",EntryPoint="CreateToolhelp32Snapshot",SetLastError=true)]static extern SafeFileHandle HoldSnapshot(uint flags,uint process);
    [DllImport("kernel32.dll",EntryPoint="Thread32First",SetLastError=true)]static extern bool HoldThreadFirst(SafeFileHandle snapshot,ref HoldThreadEntry entry);
    [DllImport("kernel32.dll",EntryPoint="Thread32Next",SetLastError=true)]static extern bool HoldThreadNext(SafeFileHandle snapshot,ref HoldThreadEntry entry);
    [DllImport("kernel32.dll",EntryPoint="OpenProcess",SetLastError=true)]static extern SafeFileHandle HoldOpenProcess(uint access,bool inherit,uint id);
    [DllImport("kernel32.dll",EntryPoint="OpenThread",SetLastError=true)]static extern SafeFileHandle HoldOpenThread(uint access,bool inherit,uint id);
    [DllImport("kernel32.dll",EntryPoint="GetProcessIdOfThread",SetLastError=true)]static extern uint HoldThreadProcess(SafeFileHandle thread);
    [DllImport("kernel32.dll",EntryPoint="SuspendThread",SetLastError=true)]static extern uint HoldSuspendThread(SafeFileHandle thread);
    [DllImport("kernel32.dll",EntryPoint="ResumeThread",SetLastError=true)]static extern uint HoldResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll",EntryPoint="WaitForSingleObject",SetLastError=true)]static extern uint HoldThreadWait(SafeFileHandle thread,uint timeout);
}
