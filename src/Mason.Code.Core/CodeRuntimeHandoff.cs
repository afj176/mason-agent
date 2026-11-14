using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mason.Code;

internal sealed partial class CodeRuntimeBroker
{
    const long HeapBase=0x800000000,HeapBytes=0x200000000;
    const long UserHeapBase=0xa00000000,UserHeapBytes=0x20000000;
    sealed record Creation(IntPtr Continuation,IntPtr Stack,IntPtr OriginalAttributes,IntPtr ProcessOutput,IntPtr ThreadOutput,CodeHandoffPlan.Image? Image,long Started);
    sealed record ReturnedBirth(uint Parent,SafeFileHandle Process,SafeFileHandle Thread,CodeHandoffPlan.Image? Image,long Started);
    readonly List<ReturnedBirth> returnedBirths=[];
    sealed partial class Target
    {
        public readonly Dictionary<uint,Creation> Creations=[];
        public readonly Dictionary<uint,IntPtr> StartupEntries=[];
        public CodeHandoffPlan.Image? MsysImage;
        public readonly HashSet<IntPtr> HelperThunks=[],AllocationFacades=[],ContextFacades=[],RuntimeMappings=[];
        public readonly Dictionary<uint,IntPtr> CryptoRoots=[];
        public IntPtr RuntimeBase;
        public bool Bootstrap,SystemConsole,Guarded,Bound,Adopted,HeapReady,ReadySeen,UserHeapAdopted;
        public uint Parent;
        public SafeFileHandle? BirthThread;
        public long BirthStarted;
        public uint BirthBefore;
        public long AdoptSite;
    }
    readonly CodePrivateObjectBudget handoffBudget=new();
    void TrackStartup(Target target,uint tid,IntPtr handle)
    {
        if(sandbox.Handoff==null)return;
        var memory=Marshal.AllocHGlobal(1250);var context=(IntPtr)(((long)memory+15)&~15L);
        try
        {
            Marshal.WriteInt32(context,48,0x100001);
            if(!GetThreadContext(new SafeFileHandle(handle,false),context))throw new Win32Exception();
            var entry=Marshal.ReadIntPtr(context,248);if((long)entry<=0)throw new InvalidDataException("Missing actual initial thread entry.");
            target.StartupEntries.Add(tid,entry);Note("Tracked initial thread entry pid="+target.Id+" tid="+tid+" entry="+entry.ToString("x"));
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    void PreserveStartup(Target target,uint tid,IntPtr context)
    {
        IntPtr incoming=Marshal.ReadIntPtr(context,128);OutputMemory(target,incoming,1232);
        uint flags=BitConverter.ToUInt32(Read(target.Handle,incoming+48,4));
        if((flags&0x100000)==0)throw new InvalidDataException("Startup continuation requires an AMD64 context.");
        IntPtr entry=(IntPtr)BitConverter.ToInt64(Read(target.Handle,incoming+248,8));
        Note("Observed startup continuation pid="+target.Id+" tid="+tid+" next="+entry.ToString("x")+" expected="+target.StartupEntries[tid].ToString("x"));
        if(entry==target.StartupEntries[tid]){target.StartupEntries.Remove(tid);Note("Completed actual initial thread context handoff pid="+target.Id+" tid="+tid);}
        var debug=new byte[48];for(int i=0;i<4;i++)BitConverter.GetBytes((long)EffectiveFunction(target,tid,i)).CopyTo(debug,i*8);BitConverter.GetBytes(0x55L).CopyTo(debug,40);
        SafeWrite(target,incoming+72,debug);SafeWrite(target,incoming+48,BitConverter.GetBytes(flags|0x10u));Marshal.Copy(debug,0,context+72,debug.Length);
    }
    void HandoffUnload(Target target,IntPtr image)
    {
        if(sandbox.Handoff==null)return;
        var module=target.Modules.FirstOrDefault(m=>m.Start==(long)image);
        target.HelperThunks.RemoveWhere(p=>(long)p>=module.Start&&(long)p<module.End);
        target.AllocationFacades.RemoveWhere(p=>(long)p>=module.Start&&(long)p<module.End);
        target.ContextFacades.RemoveWhere(p=>(long)p>=module.Start&&(long)p<module.End);
        target.RuntimeMappings.Remove(image);
        if(target.RuntimeBase==image)throw new InvalidDataException("Unloading the admitted MSYS runtime during a command is unsupported.");
    }
    void HandoffDeadlines()
    {
        if(targets.Values.Any(t=>t.DeviceReturns.Values.Any(r=>Environment.TickCount64-r.Started>10000)))throw new TimeoutException("Fixed-device native return exceeded its ten-second bound.");
        if(sandbox.Handoff==null)return;
        long now=Environment.TickCount64;
        if(returnedBirths.Any(r=>now-r.Started>10000)||targets.Values.Any(t=>t.BirthThread!=null&&now-t.BirthStarted>10000||t.Creations.Values.Any(c=>now-c.Started>10000)))
            throw new TimeoutException("Native process birth/return binding exceeded its ten-second bound.");
    }
    static string PhysicalImage(string path)=>path.StartsWith(@"\\?\",StringComparison.Ordinal)&&path.Length>6&&path[5]==':'?path[4..]:path;
    void HandoffModule(Target target,IntPtr image,IntPtr file,string actualPath)
    {
        var plan=sandbox.Handoff;if(plan==null)return;
        string path=PhysicalImage(actualPath),name=Path.GetFileName(path);
        CodeHandoffPlan.Image? expected=null;
        if(plan.Helpers.TryGetValue(path,out var helper))expected=helper;
        else if(plan.MsysImages.TryGetValue(path,out var executable)){expected=executable;if(Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase))target.MsysImage=executable;}
        else if(path.Equals(plan.Runtime.Path,StringComparison.OrdinalIgnoreCase))expected=plan.Runtime;
        else if(path.Equals(plan.ConsoleHost.Path,StringComparison.OrdinalIgnoreCase)){expected=plan.ConsoleHost;target.SystemConsole=true;}
        else if(plan.WindowsModules.TryGetValue(path,out var system))expected=system;
        if(expected==null)
        {
            if(name is "dbcw.dll" or "dbck.dll" or "msys-2.0.dll" or "Mason.BashBootstrap.exe")throw new InvalidDataException("Unadmitted compatibility image load.");
            return;
        }
        if(file==IntPtr.Zero||file==(IntPtr)(-1))throw new InvalidDataException("Missing actual handoff image handle.");
        TraceQa("module hash begin pid="+target.Id+" name="+name);
        using(var borrowed=new SafeFileHandle(file,false))
        using(var stream=new FileStream(borrowed,FileAccess.Read))
            if(Convert.ToHexStringLower(SHA256.HashData(stream))!=expected.Hash)throw new InvalidDataException("Actual loaded handoff image failed its snapshot hash.");
        TraceQa("module hash end pid="+target.Id+" name="+name);
        bool Executable(IntPtr address)
        {
            var memory=new byte[48];return VirtualQueryEx(target.Handle,address,memory,(UIntPtr)48)==(UIntPtr)48&&BitConverter.ToUInt32(memory,32)==0x1000&&(BitConverter.ToUInt32(memory,36)&0xf0)!=0;
        }
        if(path.Equals(plan.Runtime.Path,StringComparison.OrdinalIgnoreCase)&&Executable(image+CodeMsysHeapSites.Thunk))
        {
            var layout=new byte[24];int query=HandoffQueryMemory(target.Handle,image,6,layout,(UIntPtr)24,out _);
            Note("MSYS executable mapping pid="+target.Id+" base="+image.ToString("x")+" previous="+target.RuntimeBase.ToString("x")+" adopted="+target.Adopted+" ready="+target.HeapReady+" imageQuery="+query.ToString("x8")+" flags="+BitConverter.ToUInt32(layout,16).ToString("x"));
            if(!target.Guarded||!target.Bound)throw new InvalidDataException("MSYS loaded without a bound birth reservation.");
            if(target.RuntimeBase!=IntPtr.Zero&&target.RuntimeBase!=image)throw new InvalidDataException("A new executable MSYS mapping appeared after active-runtime selection.");
            if(!target.RuntimeMappings.Contains(image)&&target.RuntimeMappings.Count>=4)throw new InvalidDataException("MSYS initialization mapping bound exceeded.");
            target.RuntimeMappings.Add(image);
        }
        if(path.Equals(plan.Bootstrap,StringComparison.OrdinalIgnoreCase))target.Bootstrap=true;
        if(name=="dbcw.dll")
        {
            var thunk=Export(target.Handle,image,"DbcBrokerCall");
            if(thunk==IntPtr.Zero||!Read(target.Handle,thunk,2).SequenceEqual(new byte[]{0xcc,0xc3}))throw new InvalidDataException("Unexpected first-party thunk instructions.");
            if(Executable(thunk)){if(target.HelperThunks.Count>=16)throw new InvalidDataException("Too many executable helper instances.");target.HelperThunks.Add(thunk);Note("Verified executable handoff thunk pid="+target.Id+" address="+thunk.ToString("x"));}
            else Note("Read-only helper resource mapping receives no call role pid="+target.Id);
        }
        if(name=="dbck.dll")
        {
            var facade=Export(target.Handle,image,"VirtualAlloc");
            if(facade!=IntPtr.Zero&&Executable(facade)){if(target.AllocationFacades.Count>=16)throw new InvalidDataException("Too many executable allocation facades.");target.AllocationFacades.Add(facade);}
            var restore=Export(target.Handle,image,"RtlRestoreContext");
            if(restore!=IntPtr.Zero&&Executable(restore)){if(target.ContextFacades.Count>=16)throw new InvalidDataException("Too many executable context facades.");target.ContextFacades.Add(restore);}
        }
    }
    void Guard(Target target,bool committed=false,long initial=0)
    {
        GuardUserHeap(target);
        var memory=new byte[48];long address=HeapBase;
        if(VirtualQueryEx(target.Handle,(IntPtr)address,memory,(UIntPtr)48)!=(UIntPtr)48)throw new InvalidDataException("Cannot query the birth reservation.");
        long allocation=BitConverter.ToInt64(memory,8),length=BitConverter.ToInt64(memory,24);
        if(BitConverter.ToInt64(memory)!=address||allocation!=HeapBase||BitConverter.ToUInt32(memory,40)!=0x20000||BitConverter.ToUInt32(memory,16) is not (1 or 4))throw new InvalidDataException("Birth reservation allocation identity mismatch.");
        if(!committed)
        {
            if(BitConverter.ToUInt32(memory,32)!=0x2000||length!=HeapBytes)throw new InvalidDataException("MSYS requires the exact uncommitted private birth reservation.");
        }
        else
        {
            if(BitConverter.ToUInt32(memory,32)!=0x1000||BitConverter.ToUInt32(memory,36)!=4||length!=initial||initial<=0||initial>=HeapBytes)throw new InvalidDataException("MSYS first commitment differs from its receipt.");
            address+=initial;
            if(VirtualQueryEx(target.Handle,(IntPtr)address,memory,(UIntPtr)48)!=(UIntPtr)48||BitConverter.ToInt64(memory)!=address||BitConverter.ToInt64(memory,8)!=HeapBase||BitConverter.ToInt64(memory,24)!=HeapBytes-initial||BitConverter.ToUInt32(memory,32)!=0x2000||BitConverter.ToUInt32(memory,40)!=0x20000)throw new InvalidDataException("The remainder of the MSYS birth reserve changed.");
        }
    }
    void GuardUserHeap(Target target)
    {
        var memory=new byte[48];
        if(VirtualQueryEx(target.Handle,new IntPtr(UserHeapBase),memory,(UIntPtr)48)!=(UIntPtr)48||BitConverter.ToInt64(memory)!=UserHeapBase||BitConverter.ToInt64(memory,8)!=UserHeapBase||BitConverter.ToInt64(memory,24)!=UserHeapBytes||BitConverter.ToUInt32(memory,32)!=0x2000||BitConverter.ToUInt32(memory,40)!=0x20000||BitConverter.ToUInt32(memory,16) is not (1 or 4))throw new InvalidDataException("MSYS requires its exact independent uncommitted user-heap birth reservation.");
    }
    void BirthLayout(Target target,IntPtr thread)
    {
        var basic=Marshal.AllocHGlobal(48);var threadInfo=Marshal.AllocHGlobal(48);var block=Marshal.AllocHGlobal(1250);var context=(IntPtr)(((long)block+15)&~15L);
        try
        {
            if(NtQueryInformationProcess(target.Handle,0,basic,48,out _) <0||HandoffQueryThread(thread,0,threadInfo,48,out _) <0)throw new InvalidDataException("Cannot query initial PEB/TEB layout.");
            var peb=Marshal.ReadIntPtr(basic,8);var teb=Marshal.ReadIntPtr(threadInfo,8);
            TraceUserHeap(target,"birth",peb);
            Marshal.WriteInt32(context,48,0x100001);
            if(!GetThreadContext(new SafeFileHandle(thread,false),context))throw new Win32Exception();
            var tib=Read(target.Handle,teb,24);long stack=Marshal.ReadInt64(context,152),high=BitConverter.ToInt64(tib,8),low=BitConverter.ToInt64(tib,16);
            bool Outside(long address)=>address>0&&(address<HeapBase||address>=UserHeapBase+UserHeapBytes);
            if(!Outside((long)peb)||!Outside((long)teb)||!Outside(stack)||!Outside(high)||!Outside(low)||low>=high||stack<low||stack>=high)throw new InvalidDataException("Initial PEB, TEB or stack overlaps the MSYS birth reservation.");
            Note("Verified initial PEB/TEB/stack outside the exact MSYS reserve pid="+target.Id);
        }
        finally{Marshal.FreeHGlobal(basic);Marshal.FreeHGlobal(threadInfo);Marshal.FreeHGlobal(block);}
    }
    [System.Diagnostics.Conditional("MASON_HANDOFF_QA")]
    void TraceUserHeap(Target target,string stage,IntPtr peb=default)
    {
        if(Environment.GetEnvironmentVariable("MASON_HOLD_TRACE")!="1")return;
        var memory=new byte[48];
        if(VirtualQueryEx(target.Handle,new IntPtr(0xa00000000L),memory,(UIntPtr)48)==(UIntPtr)48)
            TraceQa("userheap "+stage+" pid="+target.Id+" base="+BitConverter.ToInt64(memory).ToString("x")+" allocation="+BitConverter.ToInt64(memory,8).ToString("x")+" bytes="+BitConverter.ToInt64(memory,24).ToString("x")+" state="+BitConverter.ToUInt32(memory,32).ToString("x")+" type="+BitConverter.ToUInt32(memory,40).ToString("x"));
        if(peb!=IntPtr.Zero){var bytes=TryRead(target.Handle,peb+0x30,8);if(bytes!=null)TraceQa("userheap native process heap pid="+target.Id+" address="+BitConverter.ToInt64(bytes).ToString("x"));}
        if(target.HeapReady){var state=TryRead(target.Handle,new IntPtr(HeapBase+0x47a0),40);if(state!=null)TraceQa("userheap MSYS state "+stage+" pid="+target.Id+" fields="+string.Join(',',Enumerable.Range(0,5).Select(i=>BitConverter.ToUInt64(state,i*8).ToString("x"))));}
    }
    void HandoffBirth(Target target,uint tid,IntPtr initialThread,bool root)
    {
        if(sandbox.Handoff==null)return;
        if(root){if(target.MsysImage!=null)throw new InvalidDataException("MSYS root bypassed the restricted bootstrap.");target.Bound=true;return;}
        var memory=Marshal.AllocHGlobal(48);
        try
        {
            if(NtQueryInformationProcess(target.Handle,0,memory,48,out _) <0)throw new InvalidDataException("Cannot identify the actual creating parent.");
            target.Parent=checked((uint)Marshal.ReadInt64(memory,40));
        }
        finally{Marshal.FreeHGlobal(memory);}
        // CSRSS creates the console host on Windows' internal path rather
        // than through a user-mode NtCreateUserProcess in the command. It
        // receives no reservation/adoption role or delegated extra rights.
        if(target.SystemConsole&&targets.ContainsKey(target.Parent)){target.Bound=true;Note("Verified OS-created restricted console host pid="+target.Id+" parent="+target.Parent);return;}
        var returned=returnedBirths.Where(r=>CompareObjectHandles(r.Process,new SafeFileHandle(target.Handle,false))).ToArray();
        if(returned.Length>0)
        {
            if(returned.Length!=1)throw new InvalidDataException("Process birth matches multiple native returns.");
            var record=returned[0];
            if(record.Parent!=target.Parent||record.Image?.Hash!=target.MsysImage?.Hash||!CompareObjectHandles(record.Thread,new SafeFileHandle(initialThread,false)))throw new InvalidDataException("Native return/birth image, parent or initial thread mismatch.");
            if(target.MsysImage!=null){TraceQa("guard begin pid="+target.Id);Guard(target);TraceQa("layout begin pid="+target.Id);BirthLayout(target,initialThread);TraceQa("layout end pid="+target.Id);target.Guarded=true;}
            target.Bound=true;returnedBirths.Remove(record);record.Process.Dispose();record.Thread.Dispose();
            Note("Bound return-first native process and thread objects pid="+target.Id+" guarded="+target.Guarded);return;
        }
        if(!targets.TryGetValue(target.Parent,out var parent)||!parent.Creations.Values.Any(c=>c.Image?.Hash==target.MsysImage?.Hash))throw new InvalidDataException("Observed process birth has no compatible pending creation.");
        if(target.MsysImage!=null){Guard(target);BirthLayout(target,initialThread);target.Guarded=true;}
        var thread=HoldOpenThread(0x100842,false,tid);
        try
        {
            if(thread.IsInvalid||HoldThreadProcess(thread)!=target.Id||!CompareObjectHandles(thread,new SafeFileHandle(initialThread,false)))throw new InvalidDataException("Initial thread object identity mismatch.");
            target.BirthBefore=HoldSuspendThread(thread);if(target.BirthBefore==uint.MaxValue)throw new Win32Exception();
            target.BirthThread=thread;target.BirthStarted=Environment.TickCount64;
        }
        catch{thread.Dispose();throw;}
        Note("Held actual process birth pid="+target.Id+" parent="+target.Parent+" guarded="+target.Guarded);
    }
    void HandoffCreation(Target target,uint tid,IntPtr context)
    {
        TraceUserHeap(target,"creation");
        if(target.Creations.Remove(tid,out var pending))
        {
            Target? bound=null;
            {
            using var hold=HoldCommand();
            WritePrivateHeld(target,pending.Stack+88,BitConverter.GetBytes((long)pending.OriginalAttributes),hold);
            Marshal.WriteIntPtr(context,72,target.Functions[0]);
            int result=Marshal.ReadInt32(context,120);
            if(result<0){Note("Native creation failed before binding status="+result.ToString("x8"));return;}
            IntPtr process=(IntPtr)BitConverter.ToInt64(Read(target.Handle,pending.ProcessOutput,8)),thread=(IntPtr)BitConverter.ToInt64(Read(target.Handle,pending.ThreadOutput,8));
            if(!DuplicateHandle(target.Handle,process,GetCurrentProcess(),out var p,0x1410,false,0))throw new InvalidDataException("Returned process handle cannot be bound.");
            var returnedProcess=new SafeFileHandle(p,true);SafeFileHandle? returnedThread=null;bool retained=false;
            try
            {
            if(!IsProcessInJob(p,job,out bool member)||!member)throw new InvalidDataException("Native return selected a process outside the command job.");
            sandbox.VerifyIdentity(p);
            if(!DuplicateHandle(target.Handle,thread,GetCurrentProcess(),out var t,0x100840,false,0))throw new InvalidDataException("Returned thread handle cannot be bound.");
            returnedThread=new SafeFileHandle(t,true);
            uint childId=HandoffProcessId(returnedProcess);
            if(childId==0||HoldThreadProcess(returnedThread)!=childId)throw new InvalidDataException("Returned process/thread ownership mismatch.");
            var births=targets.Values.Where(child=>child.Parent==target.Id&&child.BirthThread!=null&&!child.Bound&&child.MsysImage?.Hash==pending.Image?.Hash&&CompareObjectHandles(returnedProcess,new SafeFileHandle(child.Handle,false))&&CompareObjectHandles(returnedThread,child.BirthThread)).ToArray();
            if(births.Length==0)
            {
                if(targets.ContainsKey(childId)||returnedBirths.Count>=64||returnedBirths.Any(r=>CompareObjectHandles(r.Process,returnedProcess)))throw new InvalidDataException("Native return is duplicated or has no pending birth generation.");
                if(pending.Image!=null)Guard(new Target(p,childId));
                returnedBirths.Add(new(target.Id,returnedProcess,returnedThread,pending.Image,Environment.TickCount64));retained=true;
                Note("Retained native return before birth event child="+childId+" parent="+target.Id+" guarded="+(pending.Image!=null));return;
            }
            if(births.Length!=1)throw new InvalidDataException("Native return does not bind exactly one held process/thread pair.");
            bound=births[0];bound.Bound=true;
            }
            finally{if(!retained){returnedThread?.Dispose();returnedProcess.Dispose();}}
            }
            // Nested writes borrow the outer event's hold. Drop the birth
            // hold only after that actual outer hold balances its own count.
            currentHold!.AfterRelease(()=>
            {
                if(HoldResumeThread(bound.BirthThread!)!=bound.BirthBefore+1)throw new InvalidDataException("The owned birth suspension count changed.");
                bound.BirthThread!.Dispose();bound.BirthThread=null;
            });
            Note("Bound returned process and thread objects pid="+bound.Id+" guarded="+bound.Guarded);return;
        }
        using(var hold=HoldCommand())
        {
            if(returnedBirths.Count+targets.Values.Sum(t=>t.Creations.Count+(t.BirthThread!=null?1:0))>=64)throw new InvalidDataException("Outstanding native creation records exceeded the command bound.");
            IntPtr stack=Marshal.ReadIntPtr(context,152);var arguments=Read(target.Handle,stack+40,56);
            uint flags=BitConverter.ToUInt32(arguments,16),threadFlags=BitConverter.ToUInt32(arguments,24);
            if((flags&~0x204u)!=0||(threadFlags&~1u)!=0)throw new InvalidDataException("Unsupported native creation flags; no process was launched.");
            IntPtr list=(IntPtr)BitConverter.ToInt64(arguments,48);if(list==IntPtr.Zero)throw new InvalidDataException("Missing bounded native creation attributes.");
            long bytes=BitConverter.ToInt64(Read(target.Handle,list,8));if(bytes<40||bytes>8+32*32||(bytes-8)%32!=0)throw new InvalidDataException("Invalid native creation attribute list.");
            var original=Read(target.Handle,list,(int)bytes);var attributes=original.ToArray();string? image=null;int imageOffset=-1;
            Note("Native attribute contract="+string.Join(',',Enumerable.Range(0,((int)bytes-8)/32).Select(i=>BitConverter.ToUInt64(original,8+i*32).ToString("x")+":"+BitConverter.ToUInt64(original,16+i*32))));
            var copiedInputs=new List<(int Attribute,byte[] Bytes)>();
            var seen=new HashSet<ulong>();
            for(int offset=8;offset<bytes;offset+=32)
            {
                ulong attribute=BitConverter.ToUInt64(original,offset),size=BitConverter.ToUInt64(original,offset+8);if(!seen.Add(attribute))throw new InvalidDataException("Duplicate native creation attribute.");
                // Closed set. Parent/token/debug-port/job/child-policy and
                // explicit memory reservations are intentionally unsupported.
                if(attribute is not (0x20005 or 0x10003 or 0x10004 or 6 or 0x20008 or 0x2000a or 0x2000b or 0x20010 or 0x2001b or 0x6001a))throw new InvalidDataException("Unadmitted native creation attribute "+attribute.ToString("x"));
                if(size is 0 or >8192)throw new InvalidDataException("Native attribute size exceeded its bound.");
                // Windows supplies TRUE here even for an ordinary x64 image.
                // Preserve its original scalar boolean. Actual OS, host and
                // child image architecture are independently required x64.
                if(attribute==0x6001a&&(size!=1||BitConverter.ToUInt64(original,offset+16)>1||BitConverter.ToUInt64(original,offset+24)!=0))throw new InvalidDataException("Unsupported native architecture scalar.");
                if(attribute==0x20008)
                {
                    // Non-additive priority input points to one UCHAR,
                    // unlike the separate ProcessPriorityClass query struct.
                    if(size!=1||BitConverter.ToUInt64(original,offset+24)!=0)throw new InvalidDataException("Unsupported original priority input.");
                    var priority=Read(target.Handle,(IntPtr)BitConverter.ToInt64(original,offset+16),1);
                    if(priority[0] is <1 or >6)throw new InvalidDataException("Unsupported original priority class.");
                    copiedInputs.Add((offset,priority));
                }
                if(attribute==0x20005)
                {
                    if((size&1)!=0||size>6000||BitConverter.ToInt64(original,offset+24)!=0)throw new InvalidDataException("Invalid native image-name attribute.");
                    var name=Read(target.Handle,(IntPtr)BitConverter.ToInt64(original,offset+16),(int)size);image=Encoding.Unicode.GetString(name);imageOffset=offset;
                    if(image.Contains('\0'))throw new InvalidDataException("Native image name contains NUL.");
                }
            }
            if(image==null)throw new InvalidDataException("Native creation lacks an explicit image identity.");
            string path=image.StartsWith(@"\??\",StringComparison.Ordinal)?image[4..]:image;
            var plan=sandbox.Handoff!;plan.MsysImages.TryGetValue(path,out var admitted);
            IntPtr scratch=Scratch(target,tid);int expanded=checked((int)bytes+(admitted==null?0:32)),nameAt=(expanded+15)&~15,reserveAt=(nameAt+image.Length*2+15)&~15;
            var frame=new byte[reserveAt+32+copiedInputs.Sum(i=>(i.Bytes.Length+7)&~7)];attributes.CopyTo(frame,0);Encoding.Unicode.GetBytes(image).CopyTo(frame,nameAt);
            BitConverter.GetBytes((long)(scratch+nameAt)).CopyTo(frame,imageOffset+16);
            int copyAt=reserveAt+32;
            foreach(var input in copiedInputs){input.Bytes.CopyTo(frame,copyAt);BitConverter.GetBytes((long)(scratch+copyAt)).CopyTo(frame,input.Attribute+16);copyAt+=(input.Bytes.Length+7)&~7;}
            if(admitted!=null)
            {
                BitConverter.GetBytes((long)expanded).CopyTo(frame,0);int next=(int)bytes;
                BitConverter.GetBytes(0x20007L).CopyTo(frame,next);BitConverter.GetBytes(32L).CopyTo(frame,next+8);BitConverter.GetBytes((long)(scratch+reserveAt)).CopyTo(frame,next+16);
                BitConverter.GetBytes(HeapBase).CopyTo(frame,reserveAt);BitConverter.GetBytes(HeapBytes).CopyTo(frame,reserveAt+8);
                BitConverter.GetBytes(UserHeapBase).CopyTo(frame,reserveAt+16);BitConverter.GetBytes(UserHeapBytes).CopyTo(frame,reserveAt+24);
            }
            IntPtr continuation=(IntPtr)BitConverter.ToInt64(Read(target.Handle,stack,8));
            var mapping=new byte[48];if(VirtualQueryEx(target.Handle,continuation,mapping,(UIntPtr)48)!=(UIntPtr)48||BitConverter.ToUInt32(mapping,32)!=0x1000||(BitConverter.ToUInt32(mapping,36)&0xf0)==0)throw new InvalidDataException("Native return continuation is not executable.");
            WritePrivateHeld(target,scratch,frame,hold);WritePrivateHeld(target,stack+88,BitConverter.GetBytes((long)scratch),hold);
            target.Creations.Add(tid,new(continuation,stack,list,Marshal.ReadIntPtr(context,128),Marshal.ReadIntPtr(context,136),admitted,Environment.TickCount64));
            Marshal.WriteIntPtr(context,72,continuation);Note("Observed native creation parent="+target.Id+" thread="+tid+" attributes="+string.Join(',',seen.Select(a=>a.ToString("x")))+" guarded="+(admitted!=null));
        }
    }
    bool HandoffCall(Target target,uint tid,IntPtr data)
    {
        var boundary=Marshal.ReadIntPtr(data,16);
        if(sandbox.Handoff==null||!target.HelperThunks.Contains(boundary)||Marshal.ReadInt32(data,152)!=1)return false;
        using var thread=OpenThread(0x1a,false,tid);if(thread.IsInvalid)throw new Win32Exception();
        CommandHold? hold=null;
        try
        {
        WithContext(thread,context=>
        {
            hold=HoldCommand();
            if(Marshal.ReadIntPtr(context,248)!=boundary+1||Marshal.ReadInt64(context,184)!=512)throw new InvalidDataException("Invalid first-party call boundary.");
            uint operation=checked((uint)Marshal.ReadInt64(context,128));var request=CodeHandoffRequest.Decode(operation,Read(target.Handle,Marshal.ReadIntPtr(context,136),512));
            ulong result=0;var a=request.Arguments;
            if(operation==5)
            {
                if(!target.Bootstrap||!target.Bound||target.ReadySeen||target.StartupEntries.ContainsKey(tid))throw new InvalidDataException("Ready requires the bound bootstrap after its actual initial context handoff.");
                target.ReadySeen=true;
                for(int i=0;i<4;i++)Marshal.WriteIntPtr(context,72+i*8,EffectiveFunction(target,tid,i));Marshal.WriteInt64(context,112,0x55);
            }
            else if(operation==7)
            {
                bool facadeCaller=target.Modules.Any(m=>request.Caller>=(ulong)m.Start&&request.Caller<(ulong)m.End&&target.ContextFacades.Any(f=>(long)f>=m.Start&&(long)f<m.End));
                bool msysCaller=target.Modules.Any(m=>target.RuntimeMappings.Contains(new IntPtr(m.Start))&&request.Caller>=(ulong)m.Start&&request.Caller<(ulong)m.End);
                if(!target.Guarded||!target.Bound||target.MsysImage==null||!target.ContextFacades.Contains((IntPtr)(long)a[1])||!msysCaller&&!facadeCaller)throw new InvalidDataException("Context preservation requires the bound MSYS process and its verified facade.");
                IntPtr incoming=(IntPtr)(long)a[0];OutputMemory(target,incoming,1232);
                uint flags=BitConverter.ToUInt32(Read(target.Handle,incoming+48,4));
                if((flags&0x100000)==0)throw new InvalidDataException("Context preservation requires an AMD64 context.");
                var previous=Read(target.Handle,incoming+72,48);var debug=new byte[48];
                for(int i=0;i<4;i++)BitConverter.GetBytes((long)EffectiveFunction(target,tid,i)).CopyTo(debug,i*8);
                BitConverter.GetBytes(0x55L).CopyTo(debug,40);
                SafeWrite(target,incoming+72,debug);SafeWrite(target,incoming+48,BitConverter.GetBytes(flags|0x10u));
                Marshal.Copy(debug,0,context+72,debug.Length);
                preservedMsysContexts++;
                if(facadeCaller){preservedContinuationContexts++;if((BitConverter.ToUInt64(previous,40)&0x55)!=0x55)repairedContinuationContexts++;}
                Note("Preserved canonical debugger hooks across MSYS RtlRestoreContext pid="+target.Id+" tid="+tid+" flags="+flags.ToString("x")+" previousDR7="+BitConverter.ToUInt64(previous,40).ToString("x"));
            }
            else if(operation==1)
            {
                string? relative=PrivateName(request.Name);
                if(relative==null||relative.Length>220||relative.Length>0&&(!relative.StartsWith("msys-",StringComparison.Ordinal)||relative.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('-' or '_' or '.')))||(a[0]&~0x2000ful)!=0||(a[1]&~0xc2ul)!=0)throw new InvalidDataException("Directory request is outside the bounded private namespace policy.");
                handoffBudget.Admit(target.Id);
                result=(ulong)(long)Duplicate(sandbox.ObjectDirectory,target.Handle,relative.Length==0?(uint)a[0]:0x2000f);
            }
            else if(operation==2)
            {
                using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
                if(!DuplicateHandle(target.Handle,(IntPtr)(long)a[0],GetCurrentProcess(),out var copy,0,false,0))throw new InvalidDataException("Invalid section directory handle.");
                using var owned=new SafeFileHandle(copy,true);string? kind=ObjectText(copy,2);string? name=kind=="Directory"?ObjectText(copy,1):null;
                if(a[1]>long.MaxValue||a[2]>uint.MaxValue||a[3]>uint.MaxValue||!CodePrivateSections.Allows(kind??"",name,sandbox.ObjectDirectoryName,request.Name,identity.User!.Value,(long)a[1],(uint)a[2],(uint)a[3],0))throw new InvalidDataException("Section request is outside the bounded private section policy.");
                handoffBudget.Admit(target.Id,request.Name,(long)a[1]);
                Note("Section request pid="+target.Id+" name="+request.Name+" bytes="+a[1]);
            }
            else
            {
                if(!target.Guarded||!target.Bound||target.AllocationFacades.Count==0||a[0]!=(ulong)(operation==6?UserHeapBase:HeapBase))throw new InvalidDataException("Heap receipt has no bound MSYS birth.");
                var mappings=target.Modules.Where(m=>target.RuntimeMappings.Contains(new IntPtr(m.Start))&&request.Caller>=(ulong)m.Start&&request.Caller<(ulong)m.End).ToArray();
                if(mappings.Length!=1)throw new InvalidDataException("Heap receipt does not select one actual admitted executable MSYS mapping.");
                var runtime=mappings[0];long site=checked((long)request.Caller-runtime.Start);
                if(operation!=3&&target.RuntimeBase!=new IntPtr(runtime.Start))throw new InvalidDataException("Heap receipt changed the selected active MSYS mapping.");
                bool validSite=operation==6?site is CodeMsysHeapSites.RootUserReserve or CodeMsysHeapSites.ForkUserReserve:operation==3?site is CodeMsysHeapSites.RootReserve or CodeMsysHeapSites.ForkReserve or CodeMsysHeapSites.SmallForkReserve:site==(target.AdoptSite==CodeMsysHeapSites.RootReserve?CodeMsysHeapSites.RootCommit:CodeMsysHeapSites.ForkCommit);
                var call=Read(target.Handle,(IntPtr)(long)request.Caller-5,5);long thunk=checked((long)request.Caller+BitConverter.ToInt32(call,1));
                var jump=Read(target.Handle,(IntPtr)(runtime.Start+CodeMsysHeapSites.Thunk),6);
                long iat=checked(runtime.Start+CodeMsysHeapSites.Thunk+6+BitConverter.ToInt32(jump,2));
                if(!validSite||call[0]!=0xe8||thunk!=runtime.Start+CodeMsysHeapSites.Thunk||jump[0]!=0xff||jump[1]!=0x25||iat!=runtime.Start+CodeMsysHeapSites.Import||!target.AllocationFacades.Contains((IntPtr)BitConverter.ToInt64(Read(target.Handle,(IntPtr)iat,8))))throw new InvalidDataException("Heap receipt is not an admitted exact MSYS facade call site.");
                if(operation==6)
                {
                    uint protection=site==CodeMsysHeapSites.RootUserReserve?1u:4u;
                    if(!target.HeapReady||target.UserHeapAdopted||a[1]!=(ulong)UserHeapBytes||a[2]!=0x2000||a[3]!=protection)throw new InvalidDataException("Unsupported or repeated user-heap adoption; this candidate supports a 512 MiB initial MSYS user heap.");
                    GuardUserHeap(target);target.UserHeapAdopted=true;result=(ulong)UserHeapBase;
                }
                else if(operation==3)
                {
                    if(target.Adopted||a[1]!=(ulong)HeapBytes||a[2]!=0x2000||a[3]!=1)throw new InvalidDataException("Unexpected or repeated reserve adoption.");
                    Guard(target);target.RuntimeBase=new IntPtr(runtime.Start);target.Adopted=true;target.AdoptSite=site;result=(ulong)HeapBase;
                }
                else
                {
                    if(!target.Adopted||target.HeapReady||a[1] is 0 or >=(ulong)HeapBytes||(a[1]&4095)!=0||a[2]!=0x1000||a[3]!=4||a[4]!=(ulong)HeapBase)throw new InvalidDataException("Invalid initial heap commitment.");
                    Guard(target,true,(long)a[1]);target.HeapReady=true;
                }
                Note("MSYS heap receipt pid="+target.Id+" operation="+operation+" bytes="+a[1]);
                TraceUserHeap(target,"receipt-"+operation);
            }
            Marshal.WriteInt64(context,120,unchecked((long)result));
        });
        return true;
        }
        catch(PendingThreadExit){throw;}
        catch{hold?.Abort();throw;}
        finally{hold?.Dispose();}
    }
    void CloseReturnedBirths(){foreach(var item in returnedBirths){item.Process.Dispose();item.Thread.Dispose();}returnedBirths.Clear();}
    [DllImport("kernel32.dll",EntryPoint="GetProcessId",SetLastError=true)]static extern uint HandoffProcessId(SafeFileHandle process);
    [DllImport("ntdll.dll",EntryPoint="NtQueryInformationThread")]static extern int HandoffQueryThread(IntPtr thread,int information,IntPtr buffer,int bytes,out int needed);
    [DllImport("ntdll.dll",EntryPoint="NtQueryVirtualMemory")]static extern int HandoffQueryMemory(IntPtr process,IntPtr address,int information,[Out]byte[] buffer,UIntPtr bytes,out UIntPtr needed);
}
