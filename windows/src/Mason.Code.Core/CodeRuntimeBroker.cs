using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.Versioning;

namespace Mason.Code;

/// <summary>
/// Compatibility for owned x64 LPAC children. The permanent Windows token is the security boundary.
/// No filesystem operation is brokered. Only fixed KsecDD and Null devices are delegated, with bounded rights;
/// MSYS object directories are redirected into this command's private AppContainer namespace.
/// Hardware breakpoints do not replace DLL files, patch executable code or disable mitigations.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class CodeRuntimeBroker : IDisposable
{
    static readonly string[] names=["NtOpenFile","NtCreateNamedPipeFile","NtCreateFile","NtCreateDirectoryObject"];
    readonly CodeSandbox sandbox;
    readonly IntPtr job;
    readonly SafeFileHandle crypto;
    readonly SafeFileHandle nullDevice; // Owned by the command launch, never supplied by the agent.
    readonly Dictionary<uint,Target> targets=[];
    readonly Dictionary<string,string> privatePipes=new(StringComparer.OrdinalIgnoreCase);
    readonly CodePrivatePipeBudget pipeBudget=new();
    int sectionRequests;
    long sectionBytes;
    readonly List<string> diagnostics=[];
    int diagnosticCharacters;
    int recoveredBreakpointStatus;
    int preservedMsysContexts,preservedContinuationContexts,repairedContinuationContexts;
    CancellationToken activeCancellation;
    bool debugStopActive;
    uint debugStopProcess,debugStopThread,debugStopKind;
    long debugStopGeneration;
    bool IsActiveDebugStop(uint process,uint thread)=>debugStopActive&&debugStopKind==1&&debugStopProcess==process&&debugStopThread==thread;
    [System.Diagnostics.Conditional("MASON_HANDOFF_QA")]
    void TraceQa(string stage){if(Environment.GetEnvironmentVariable("MASON_HOLD_TRACE")=="1")Console.Error.WriteLine("QA_NATIVE "+Environment.TickCount64+" "+stage);}
    void Note(string value)
    {
        TraceQa("note "+value);
        if(diagnostics.Count>=128||diagnosticCharacters>=32768)return;
        if(value.Length>512)value=value[..512]+"…";diagnosticCharacters+=value.Length;diagnostics.Add(value);
        if(Environment.GetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS")=="1")Console.Error.WriteLine("BROKER "+value);
    }
    internal string Diagnostics=>"New-object default ACL: "+sandbox.DefaultAclDiagnostic+"\nNative census audits: "+censusAudits+"; native census queries: "+censusQueries+"; complete job census repeats: "+jobCensusChanges+"; preserved MSYS contexts: "+preservedMsysContexts+"; continuation contexts: "+preservedContinuationContexts+"; repaired continuation hooks: "+repairedContinuationContexts+"; recovered breakpoint status: "+recoveredBreakpointStatus+"\n"+string.Join('\n',diagnostics);
    readonly IntPtr eventData;
    const uint Continue=0x10002,NotHandled=0x80010001;
    sealed partial class Target(IntPtr handle,uint id)
    {
        public uint Id=id;
        public IntPtr Handle=handle,Directory;
        public IntPtr AnonymousPipe,Section,OpenFile,ContinueContext;
        public bool AnonymousHook,SectionHook,Rearm,DirectoryRetired,StartupContextHook;
        public IntPtr[] Functions=[];
        public bool Initialized;
        public readonly HashSet<uint> Threads=[];
        public readonly Dictionary<uint,IntPtr> Scratch=[];
        public readonly List<(long Start,long End,string Name)> Modules=[];
    }
    public CodeRuntimeBroker(CodeSandbox sandbox,IntPtr job,SafeFileHandle nullDevice)
    {
        this.sandbox=sandbox;this.job=job;
        if(!Environment.Is64BitProcess)throw new PlatformNotSupportedException("Auto runtime compatibility requires the x64 command host.");
        if(GetFileType(nullDevice)!=2||ObjectText(nullDevice.DangerousGetHandle(),1)!=@"\Device\Null")throw new InvalidDataException("The command's fixed Null handle failed identity validation.");
        this.nullDevice=nullDevice;
        crypto=CreateFile(@"\\?\GLOBALROOT\Device\KsecDD",0x100003,7,IntPtr.Zero,3,0,IntPtr.Zero);
        if(crypto.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot acquire the fixed cryptography device for isolated runtimes.");
        eventData=Marshal.AllocHGlobal(176);
    }
    public (uint Exit,bool Truncated) Pump(uint root,string log,CancellationToken cancellation)
    {
        activeCancellation=cancellation;
#if MASON_HANDOFF_QA
        DebugProgress();var sampler=new Timer(_=>SampleStall(),null,2000,2000);
#endif
        // A blocked native context query must not prevent deadline/cancellation
        // from stopping the owned job on an independent CLR thread.
        using var cancellationKill=cancellation.Register(()=>TerminateJobObject(job,1));
        try{return PumpEvents(root,log,cancellation);}
        catch(Exception error)
        {
            bool requested=cancellation.IsCancellationRequested;
            TerminateJobObject(job,1);
            bool drained=DrainFailedCommand();
            // Cancellation kills the job concurrently with native validation.
            // Keep every write barrier fail-closed; classify a resulting native
            // error as cancellation only after all owned exits and zero active
            // job processes are witnessed. A prior unrelated failure stays an error.
            if(requested&&drained)throw new OperationCanceledException("The requested cancellation stopped and drained the owned command.",error,cancellation);
            throw;
        }
        finally
        {
#if MASON_HANDOFF_QA
            using var drained=new ManualResetEvent(false);if(sampler.Dispose(drained))drained.WaitOne();
#endif
        }
    }
    bool DrainFailedCommand()
    {
        var known=targets.Keys.ToHashSet();long end=Environment.TickCount64+10000;
        while(Environment.TickCount64<end)
        {
            if(!WaitForDebugEvent(eventData,100))
            {
                if(Marshal.GetLastWin32Error()!=121)break;
                var counts=new byte[48];
                if(known.Count==0&&QueryHoldJob(job,1,counts,(uint)counts.Length,out _)&&BitConverter.ToUInt32(counts,40)==0){Note("Stopped failed command and drained owned debug exits; remaining=0");return true;}
                continue;
            }
            uint kind=(uint)Marshal.ReadInt32(eventData),pid=(uint)Marshal.ReadInt32(eventData,4),tid=(uint)Marshal.ReadInt32(eventData,8);
            var data=eventData+16;
            if(kind==3){known.Add(pid);CloseFile(data);}
            else if(kind==6)CloseFile(data);
            else if(kind==5){known.Remove(pid);if(targets.Remove(pid,out var target))target.BirthThread?.Dispose();CloseAnonymous(pid);}
            if(!ContinueDebugEvent(pid,tid,Continue))break;
        }
        Note("Stopped failed command and drained owned debug exits; remaining="+known.Count);
        return false;
    }
    (uint Exit,bool Truncated) PumpEvents(uint root,string log,CancellationToken cancellation)
    {
        uint? rootExit=null;bool truncated=false,stopping=false,cancelled=false;
        while(true)
        {
            ExpireAnonymous();
            HandoffDeadlines();
            if(cancellation.IsCancellationRequested&&!stopping)
            {
                cancelled=true;stopping=true;if(!TerminateJobObject(job,1))throw new Win32Exception();
            }
            if(!stopping&&new FileInfo(log).Length>256*1024)
            {
                truncated=true;stopping=true;if(!TerminateJobObject(job,1))throw new Win32Exception();
            }
            if(!stopping)ServiceDeferredStops();
            if(!WaitForDebugEvent(eventData,100))
            {
                int error=Marshal.GetLastWin32Error();if(error==121){CaptureStall();continue;}
                throw new Win32Exception(error,"The isolated runtime event loop failed.");
            }
            uint kind=(uint)Marshal.ReadInt32(eventData),pid=(uint)Marshal.ReadInt32(eventData,4),tid=(uint)Marshal.ReadInt32(eventData,8),status=Continue;
            DebugProgress();
            debugStopActive=true;debugStopProcess=pid;debugStopThread=tid;debugStopKind=kind;debugStopGeneration=checked(debugStopGeneration+1);
            var data=eventData+16;bool rootExited=false;uint exit=0;
            try
            {
                if(kind==3)
                {
                    try
                    {
                    if(targets.Count>=64)throw new InvalidDataException("Auto command exceeded 64 concurrent processes.");
                    var process=Marshal.ReadIntPtr(data,8);
                    if(!IsProcessInJob(process,job,out bool member)||!member)throw new UnauthorizedAccessException("A command child was outside the owned job; execution stopped.");
                    sandbox.VerifyToken(process);
                    Note("Verified owned process "+pid);
                    if(Environment.GetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS")=="1")Note("Owned command "+pid+": "+ProcessCommand(process));
                    var pe=Header(process,Marshal.ReadIntPtr(data,24));
                    if(Environment.GetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS")=="1"&&GetProcessMitigationPolicy(process,1,out uint aslr,4))Note("Owned ASLR flags="+aslr.ToString("x")+" imageCharacteristics="+BitConverter.ToUInt16(pe,94).ToString("x")+" subsystemVersion="+BitConverter.ToUInt16(pe,72)+"."+BitConverter.ToUInt16(pe,74));
                    if(BitConverter.ToUInt16(pe,4)!=0x8664)throw new PlatformNotSupportedException("Auto supports x64 runtimes; no unrestricted architecture fallback was run.");
                    var target=new Target(process,pid);target.Threads.Add(tid);targets.Add(pid,target);RecordModule(target,Marshal.ReadIntPtr(data,24),Marshal.ReadIntPtr(data));
                    TrackStartup(target,tid,Marshal.ReadIntPtr(data,16));
                    TraceQa("birth begin pid="+pid+" tid="+tid);
                    HandoffBirth(target,tid,Marshal.ReadIntPtr(data,16),pid==root);
                    TraceQa("birth end pid="+pid);
                    }
                    finally{CloseFile(data);}
                }
                else if(!targets.TryGetValue(pid,out var target))throw new InvalidDataException("Unrecognized isolated process event.");
                else if(kind==2)
                {
                    if(target.Threads.Count>=1024)throw new InvalidDataException("Auto command exceeded 1,024 concurrent threads.");
                    target.Threads.Add(tid);TrackStartup(target,tid,Marshal.ReadIntPtr(data));if(target.Initialized)Arm(target,tid);
                }
                else if(kind==4){if(target.Creations.ContainsKey(tid)||target.DeviceReturns.ContainsKey(tid))throw new InvalidDataException("A thread exited before native return binding.");target.StartupEntries.Remove(tid);target.Threads.Remove(tid);if(target.Scratch.Remove(tid,out var scratch))VirtualFreeEx(target.Handle,scratch,UIntPtr.Zero,0x8000);}
                else if(kind==6)
                {
                    try
                    {
                        RecordModule(target,Marshal.ReadIntPtr(data,8),Marshal.ReadIntPtr(data));
                        if(target.Functions.Length==0)
                        {
                            var functions=Exports(target.Handle,Marshal.ReadIntPtr(data,8),sandbox.Handoff!=null?["NtCreateUserProcess","NtCreateNamedPipeFile","NtCreateFile","NtOpenFile"]:names);if(functions.Length==4){target.Functions=functions;target.OpenFile=sandbox.Handoff!=null?functions[3]:functions[0];target.Section=Export(target.Handle,Marshal.ReadIntPtr(data,8),"NtCreateSection");target.ContinueContext=Export(target.Handle,Marshal.ReadIntPtr(data,8),"NtContinue");target.DuplicateObject=Export(target.Handle,Marshal.ReadIntPtr(data,8),"NtDuplicateObject");}
                        }
                    }
                    finally { CloseFile(data); }
                    if(target.Initialized)foreach(var thread in target.Threads)Arm(target,thread);
                }
                else if(kind==7){HandoffUnload(target,Marshal.ReadIntPtr(data));target.Modules.RemoveAll(m=>m.Start==(long)Marshal.ReadIntPtr(data));}
                else if(kind==1)
                {
                    uint code=(uint)Marshal.ReadInt32(data);
                    if(code==0x80000003&&HandoffCall(target,tid,data)){}
                    else if(code==0x80000003&&!target.Initialized)
                    {
                        if(target.Functions.Length!=4)throw new InvalidDataException("Cannot locate native runtime entry points; Auto stopped before initialization.");
                        foreach(var thread in target.Threads)Arm(target,thread);target.Initialized=true;
                    }
                    else if(code==0x80000004&&target.Initialized){if(!Intercept(target,tid))status=NotHandled;}
#if MASON_BARRIER_QA
                    else if(code==0xe0424350&&Marshal.ReadInt32(data,24)==4)CheckQuiescence(data);
                    else if(code==0xe0424351&&Marshal.ReadInt32(data,24)==4)CheckQuiescenceDenied();
                    else if(code==0xe0424352&&Marshal.ReadInt32(data,24)==4)CheckQuiescenceCancellation();
#endif
                    else if(code==0x406d1388&&target.Initialized&&Marshal.ReadInt32(data,24) is 3 or 4&&(uint)Marshal.ReadInt64(data,32)==0x1000)
                    {
                        // Microsoft's debugger thread-name notification is informational. Passing it to
                        // MSYS's SEH machinery clears hardware contexts; a debugger must consume it.
                        foreach(var thread in target.Threads)Arm(target,thread);
                    }
                    else
                    {
                        bool report=Marshal.ReadInt32(data,152)==0||!target.Modules.Any(m=>m.Name.Equals("conhost.exe",StringComparison.OrdinalIgnoreCase));
                        if(report)
                        {
                        Note("Unhandled exception "+code.ToString("x8")+" at "+Location(target,Marshal.ReadIntPtr(data,16)));
                        TraceUserHeap(target,"exception-"+code.ToString("x8"));
                        if(code==0xc0000005)Note("Access kind="+Marshal.ReadInt64(data,32)+" address="+Marshal.ReadInt64(data,40).ToString("x"));
                        if(code is 0x80000003 or 0xc0000005)
                        {
                            using var thread=OpenThread(0x1a,false,tid);
                            WithContext(thread,c=>{Note("DR7="+Marshal.ReadInt64(c,112).ToString("x")+" DR0="+Location(target,Marshal.ReadIntPtr(c,72))+" RDX="+Marshal.ReadInt64(c,136).ToString("x")+" RSP="+Marshal.ReadInt64(c,152).ToString("x"));var stack=TryRead(target.Handle,Marshal.ReadIntPtr(c,152),128);if(stack!=null)for(int i=0;i<128;i+=8)Note("stack "+i+" "+Location(target,(IntPtr)BitConverter.ToInt64(stack,i)));if(Environment.GetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS")=="1"&&target.Modules.Any(m=>m.Name.Equals("msys-2.0.dll",StringComparison.OrdinalIgnoreCase)))DescribeMsysHeap(target,Marshal.ReadInt64(c,152));});
                        }
                        }
                        status=NotHandled;
                    }
                }
                else if(kind==5)
                {
                    TraceQa("exit pid="+pid+" code="+unchecked((uint)Marshal.ReadInt32(data)).ToString("x8"));
                    target.BirthThread?.Dispose();target.BirthThread=null;
                    CloseAnonymous(pid);
                    Note("Owned process exited "+pid+" code="+Marshal.ReadInt32(data));
                    exit=(uint)Marshal.ReadInt32(data);targets.Remove(pid);rootExited=pid==root;
                    if(rootExited)
                    {
                        rootExit=exit;
                        if(targets.Count>0&&!stopping){stopping=true;if(!TerminateJobObject(job,1))throw new Win32Exception();}
                    }
                }
            }
            catch(PendingThreadExit pending)
            {
                try
                {
                    if(kind!=1||!targets.TryGetValue(pid,out var owner))throw new InvalidDataException("Only a verified tool boundary may be deferred for a pending kernel exit.");
                    ParkDeferredStop(owner,tid,data,pending);
                }
                catch{pending.Dispose();throw;}
            }
            finally { try{if(!ContinueDebugEvent(pid,tid,status))throw new Win32Exception();}finally{debugStopActive=false;} }
            if(rootExit.HasValue&&targets.Count==0)
            {
                if(cancelled||cancellation.IsCancellationRequested)throw new OperationCanceledException(cancellation);
                return(rootExit.Value,truncated);
            }
        }
    }
    void Arm(Target target,uint id)
    {
        // Pure native programs can restore a loader context before their first
        // crypto request or module load. Observe that existing transition from
        // initial arming, rather than waiting for CNG to initialize.
        using var thread=OpenThread(0x1a,false,id);if(thread.IsInvalid)throw new Win32Exception();
        WithContext(thread,context=>
        {
            for(int i=0;i<4;i++)Marshal.WriteIntPtr(context,72+i*8,EffectiveFunction(target,id,i));
            // A different thread's already-queued hardware event may not have
            // been delivered yet. Rearming must preserve its DR6 status.
            Marshal.WriteInt64(context,112,0x55);
        });
    }
    bool Intercept(Target target,uint id)
    {
        using var thread=OpenThread(0x1a,false,id);if(thread.IsInvalid)throw new Win32Exception();bool ours=false;
        CommandHold? commandHold=null;
        try
        {
        WithContext(thread,context=>
        {
            ulong dr6=(ulong)Marshal.ReadInt64(context,104),dr7=(ulong)Marshal.ReadInt64(context,112);IntPtr instruction=Marshal.ReadIntPtr(context,248);int function=0;
            // Another queued debug event can cause Arm to clear DR6 before
            // this event is delivered. Match the live instruction, installed
            // execute breakpoint and this thread's canonical plan; DR6 alone
            // is neither ownership proof nor capability authorization.
            while(function<4&&(instruction!=EffectiveFunction(target,id,function)||Marshal.ReadIntPtr(context,72+function*8)!=instruction||(dr7&(1UL<<(function*2)))==0||((dr7>>(16+function*4))&15)!=0))function++;
            if(function==4)
            {
                Note("Unmatched single-step pid="+target.Id+" tid="+id+" at="+Location(target,instruction)+" dr6="+dr6.ToString("x")+" dr7="+dr7.ToString("x")+" flags="+Marshal.ReadInt32(context,68).ToString("x")+" actual="+string.Join(',',Enumerable.Range(0,4).Select(i=>Marshal.ReadIntPtr(context,72+i*8).ToString("x")))+" expected="+string.Join(',',Enumerable.Range(0,4).Select(i=>EffectiveFunction(target,id,i).ToString("x"))));return;
            }
            if((dr6&(1UL<<function))==0)recoveredBreakpointStatus++;
            ours=true;Marshal.WriteInt32(context,68,Marshal.ReadInt32(context,68)|0x10000);Marshal.WriteInt64(context,104,0);
            // This untrusted read-only prefilter may decline compatibility,
            // never authorize it. Every possible adaptation is decoded again
            // below under the unchanged complete command hold.
            if(!MayNeedCompatibility(target,id,context,function))return;
            commandHold=HoldCommand();
            if(function==0&&target.DeviceReturns.ContainsKey(id)){CompleteCryptoDuplicate(target,id,context);return;}
            if(function==0&&sandbox.Handoff!=null&&target.StartupEntries.ContainsKey(id)){PreserveStartup(target,id,context);return;}
            if(function==0&&sandbox.Handoff!=null){HandoffCreation(target,id,context);return;}
            if(function==3&&target.StartupContextHook)
            {
                var incoming=TryRead(target.Handle,Marshal.ReadIntPtr(context,128),256);
                if(incoming==null||(BitConverter.ToUInt32(incoming,48)&0x100000)==0)return;
                // A normal loader/NLS continuation can restore stale zero debug
                // registers. Carry only our existing owned-thread breakpoints
                // across this startup transition; no API result or token changes.
                RetainFileOpen(target,context);
                if((BitConverter.ToUInt32(incoming,48)&0x10)!=0)
                {
                    var debug=new byte[48];for(int i=0;i<4;i++)BitConverter.GetBytes((long)EffectiveFunction(target,id,i)).CopyTo(debug,i*8);
                    BitConverter.GetBytes(0x55L).CopyTo(debug,40);
                    SafeWrite(target,Marshal.ReadIntPtr(context,128)+72,debug);
                }
                Note("Preserved existing debugger hooks across native startup context restoration.");
                return;
            }
            if(function==0&&target.AnonymousHook){CreateAnonymousPipe(target,context);return;}
            bool fileOpen=function==2||sandbox.Handoff!=null&&function==3&&!target.StartupContextHook||sandbox.Handoff==null&&(function==0&&!target.SectionHook||function==3&&target.DirectoryRetired);
            // Malformed debuggee pointers remain the kernel's problem; never interpret them as broker requests.
            var attributes=TryRead(target.Handle,Marshal.ReadIntPtr(context,184),48); // R8: OBJECT_ATTRIBUTES
            if(attributes==null||BitConverter.ToInt32(attributes,0)!=48)return;
            var unicode=TryRead(target.Handle,(IntPtr)BitConverter.ToInt64(attributes,16),16);if(unicode==null)return;
            int length=BitConverter.ToUInt16(unicode,0);if(length>8192||(length&1)!=0)return;
            var nameBytes=TryRead(target.Handle,(IntPtr)BitConverter.ToInt64(unicode,8),length);if(nameBytes==null)return;
            var name=Encoding.Unicode.GetString(nameBytes);uint access=(uint)Marshal.ReadInt64(context,136); // RDX
            if(NativeAnonymousPipes&&TryAnonymous(target,context,attributes,name,access,function,fileOpen))return;
            if(function==0&&target.SectionHook)
            {
                var stack=Read(target.Handle,Marshal.ReadIntPtr(context,152)+40,24);
                Note("Observed NtCreateSection name="+name+" root="+BitConverter.ToInt64(attributes,8).ToString("x")+" access="+access.ToString("x")+" protect="+BitConverter.ToUInt32(stack).ToString("x")+" attributes="+BitConverter.ToUInt32(stack,8).ToString("x")+" file="+BitConverter.ToInt64(stack,16).ToString("x"));
                PrivateSection(target,id,context,attributes,name,stack);
                // The original syscall executes under the same restricted token. No section handle is delegated.
                return;
            }
            if(fileOpen&&target.Modules.Any(m=>m.Name.Equals("git.exe",StringComparison.OrdinalIgnoreCase))&&Environment.GetEnvironmentVariable("MASON_SANDBOX_DIAGNOSTICS")=="1"||name.Contains("pipe",StringComparison.OrdinalIgnoreCase)||name.Contains("Etw",StringComparison.OrdinalIgnoreCase)||name.Contains("KsecDD",StringComparison.OrdinalIgnoreCase)||name.Contains("nul",StringComparison.OrdinalIgnoreCase)||function==1||function==3&&!target.DirectoryRetired)Note((function==3&&target.DirectoryRetired?"NtOpenFile":names[function])+" "+access.ToString("x")+" root="+BitConverter.ToInt64(attributes,8).ToString("x")+" "+name);
            if(fileOpen&&BitConverter.ToInt64(attributes,8)==0&&CodeNullDevice.Access(name,access) is uint nullAccess)
            {
                bool inherit=(BitConverter.ToUInt32(attributes,24)&2)!=0;
                var delegated=Duplicate(nullDevice.DangerousGetHandle(),target.Handle,nullAccess,inherit);
                SafeWrite(target,Marshal.ReadIntPtr(context,128),BitConverter.GetBytes((long)delegated));
                var io=new byte[16];BitConverter.GetBytes(1L).CopyTo(io,8);SafeWrite(target,Marshal.ReadIntPtr(context,192),io);
                Return(target,context,function);Note("Delegated fixed Null device rights="+nullAccess.ToString("x"));
            }
            else if(fileOpen&&BitConverter.ToInt64(attributes,8)==0&&name.Equals(@"\Device\KsecDD",StringComparison.OrdinalIgnoreCase)&&(access&~0x100003u)==0)
            {
                TraceQa("original crypto request pid="+target.Id+" access="+access.ToString("x")+" output="+Location(target,Marshal.ReadIntPtr(context,128)));
                BeginCryptoDuplicate(target,id,context,function,access);
            }
            else if((function==1||fileOpen)&&PrivatePipe(target,attributes,name,function==1) is string pipe)
            {
                if(function==1)
                {
                    CreatePrivatePipe(target,context,attributes,pipe);
                    RetainFileOpen(target,context);
                    if(target.SectionHook&&target.AnonymousPipe!=IntPtr.Zero){target.Functions[0]=NativeAnonymousPipes?target.OpenFile:target.AnonymousPipe;target.SectionHook=false;target.AnonymousHook=!NativeAnonymousPipes;target.Rearm=true;Marshal.WriteIntPtr(context,72,target.Functions[0]);}
                }
                else if(privatePipes.TryGetValue(pipe,out var destination)){Note("Opening known command-private pipe client: "+pipe);Rewrite(target,id,context,attributes,destination,IntPtr.Zero);}
                else Return(target,context,function,unchecked((int)0xc0000034)); // Never connect an agent-nominated host pipe.
            }
            else if(sandbox.Handoff==null&&function==3&&!target.DirectoryRetired&&BitConverter.ToInt64(attributes,8)==0&&PrivateName(name) is string relative)
            {
                if(relative.Length==0)
                {
                    if((access&~0x2000fu)!=0)return;
                    SafeWrite(target,Marshal.ReadIntPtr(context,128),BitConverter.GetBytes((long)Duplicate(sandbox.ObjectDirectory,target.Handle,access)));Return(target,context,function);
                }
                else
                {
                    if(target.Directory==IntPtr.Zero)target.Directory=Duplicate(sandbox.ObjectDirectory,target.Handle,0x2000f);
                    Rewrite(target,id,context,attributes,relative,target.Directory);
                }
            }
            // Every other path runs normally under the permanent token. No file operation is delegated.
        });
        if(target.Rearm){target.Rearm=false;foreach(var other in target.Threads.Where(t=>t!=id))Arm(target,other);}
        return ours;
        }
        catch(PendingThreadExit){throw;}
        catch{commandHold?.Abort();throw;}
        finally{commandHold?.Dispose();}
    }
    bool MayNeedCompatibility(Target target,uint id,IntPtr context,int function)
    {
        if(function==0&&(sandbox.Handoff!=null||target.AnonymousHook||target.SectionHook||target.DeviceReturns.ContainsKey(id)))return true;
        if(function==1||function==3&&(target.StartupContextHook||!target.DirectoryRetired&&sandbox.Handoff==null))return true;
        var attributes=TryRead(target.Handle,Marshal.ReadIntPtr(context,184),48);
        if(attributes==null||BitConverter.ToInt32(attributes)!=48)return false;
        var unicode=TryRead(target.Handle,new IntPtr(BitConverter.ToInt64(attributes,16)),16);
        if(unicode==null)return false;
        int length=BitConverter.ToUInt16(unicode);if(length>8192||(length&1)!=0)return false;
        var bytes=TryRead(target.Handle,new IntPtr(BitConverter.ToInt64(unicode,8)),length);if(bytes==null)return false;
        string name=Encoding.Unicode.GetString(bytes);uint access=unchecked((uint)Marshal.ReadInt64(context,136));
        return name.Length==0||name.Contains("pipe",StringComparison.OrdinalIgnoreCase)||name.StartsWith("msys-",StringComparison.OrdinalIgnoreCase)||name.StartsWith(@"uv\",StringComparison.OrdinalIgnoreCase)||name.Equals(@"\Device\KsecDD",StringComparison.OrdinalIgnoreCase)||CodeNullDevice.Access(name,access)!=null;
    }
    void PrivateSection(Target target,uint id,IntPtr context,byte[] attributes,string name,byte[] stack)
    {
        using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid=identity.User!.Value;
        bool known=CodePrivateSections.KnownName(name,sid);
        if(!known||BitConverter.ToUInt32(stack)!=4||BitConverter.ToUInt32(stack,8)!=0x08000000||BitConverter.ToInt64(stack,16)!=0)return;
        var sizeBytes=TryRead(target.Handle,Marshal.ReadIntPtr(context,192),8);if(sizeBytes==null)return;
        long size=BitConverter.ToInt64(sizeBytes);if(size is <=0 or >1024*1024)return;
        var root=(IntPtr)BitConverter.ToInt64(attributes,8);if(root==IntPtr.Zero)return;
        if(!DuplicateHandle(target.Handle,root,GetCurrentProcess(),out var duplicate,0,false,0))return;
        using var owned=new SafeFileHandle(duplicate,true);
        var kind=ObjectText(duplicate,2);if(kind!="Directory")return;
        // Never issue a name query against an agent-nominated file/pipe: file-name queries can block on I/O.
        var path=ObjectText(duplicate,1);
        Note("Section directory="+path+" type="+kind+" bytes="+size);
        if(!CodePrivateSections.Allows(kind??"",path,sandbox.ObjectDirectoryName,name,sid,size,BitConverter.ToUInt32(stack),BitConverter.ToUInt32(stack,8),BitConverter.ToInt64(stack,16)))return;
        if(++sectionRequests>64||(sectionBytes+=size)>16*1024*1024)throw new InvalidDataException("Private shared-memory requests exceeded the command bound.");
        var app=new System.Security.Principal.SecurityIdentifier(sandbox.Sid).Value;
        var restriction=new System.Security.Principal.SecurityIdentifier(sandbox.WriteSid).Value;
        var descriptor=new System.Security.AccessControl.RawSecurityDescriptor($"D:P(A;;GA;;;{sid})(A;;GA;;;{app})(A;;GA;;;{restriction})S:(ML;;NW;;;LW)");
        int descriptorOffset=(64+name.Length*2+2+7)&~7;
        var bytes=new byte[descriptorOffset+descriptor.BinaryLength];attributes.CopyTo(bytes,0);
        var scratch=Scratch(target,id);BitConverter.GetBytes((long)(scratch+48)).CopyTo(bytes,16);
        BitConverter.GetBytes((long)(scratch+descriptorOffset)).CopyTo(bytes,32);
        BitConverter.GetBytes((ushort)(name.Length*2)).CopyTo(bytes,48);BitConverter.GetBytes((ushort)(name.Length*2+2)).CopyTo(bytes,50);
        BitConverter.GetBytes((long)(scratch+64)).CopyTo(bytes,56);Encoding.Unicode.GetBytes(name).CopyTo(bytes,64);
        descriptor.GetBinaryForm(bytes,descriptorOffset);SafeWrite(target,scratch,bytes);Marshal.WriteIntPtr(context,184,scratch);
        Note("Applied descriptor for a new private pagefile section; existing descriptors are never changed.");
    }
    static string? ObjectText(IntPtr handle,int information)
    {
        var memory=Marshal.AllocHGlobal(16384);
        try
        {
            if(NtQueryObject(handle,information,memory,16384,out _) <0)return null;
            int length=(ushort)Marshal.ReadInt16(memory);var text=Marshal.ReadIntPtr(memory,8);
            if((length&1)!=0||length>8192||(long)text<(long)memory||(long)text+length>(long)memory+16384)return null;
            return Marshal.PtrToStringUni(text,length/2);
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    static string? ProcessCommand(IntPtr process)
    {
        var memory=Marshal.AllocHGlobal(8192);
        try
        {
            if(NtQueryInformationProcess(process,60,memory,8192,out _) <0)return null;
            int length=(ushort)Marshal.ReadInt16(memory);var text=Marshal.ReadIntPtr(memory,8);
            if((length&1)!=0||length>4096||(long)text<(long)memory||(long)text+length>(long)memory+8192)return null;
            return Marshal.PtrToStringUni(text,length/2);
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    void CreateAnonymousPipe(Target target,IntPtr context)
    {
        RetainFileOpen(target,context);
        Note("Creating a new private anonymous IPC buffer.");
        uint size=(uint)Marshal.ReadInt64(context,192);pipeBudget.Admit(size,size);
        var attributesPointer=Marshal.ReadIntPtr(context,184);bool inherit=false;
        if(attributesPointer!=IntPtr.Zero)
        {
            var attributes=Read(target.Handle,attributesPointer,24);if(BitConverter.ToInt32(attributes)!=24)throw new InvalidDataException("Invalid anonymous pipe security attributes.");inherit=BitConverter.ToInt32(attributes,16)!=0;
        }
        var logical="anonymous-"+Guid.NewGuid().ToString("N");var pipeName="Mason.Code."+Guid.NewGuid().ToString("N");var path=@"\\.\pipe\"+pipeName;
        // Only NEW local buffers. SECURITY_ANONYMOUS prevents the sandboxed server from impersonating the host.
        var security=new Security {Length=Marshal.SizeOf<Security>()};
        using var server=CreateNamedPipe(path,1|0x80000,8,1,Math.Max(size,4096u),Math.Max(size,4096u),0,ref security);
        if(server.IsInvalid)throw new Win32Exception();
        using var client=CreateFile(path,0x40000080,0,IntPtr.Zero,3,0x00100000,IntPtr.Zero); // SQOS_PRESENT | ANONYMOUS
        if(client.IsInvalid)throw new Win32Exception();
        var read=Duplicate(server.DangerousGetHandle(),target.Handle,0x120089,inherit);
        var write=Duplicate(client.DangerousGetHandle(),target.Handle,0x120196,inherit);
        SafeWrite(target,Marshal.ReadIntPtr(context,128),BitConverter.GetBytes((long)read));SafeWrite(target,Marshal.ReadIntPtr(context,136),BitConverter.GetBytes((long)write));
        privatePipes.Add(logical,@"\??\pipe\"+pipeName);Return(target,context,2,1);
        // Source endpoints close before the target resumes, preserving EOF and broken-pipe semantics.
    }
    void RetainFileOpen(Target target,IntPtr context)
    {
        if(sandbox.Handoff!=null)return; // Each handoff thread retains its own DR0 startup continuation role.
        if(target.DirectoryRetired&&!target.StartupContextHook)return;
        // Directory aliases finish initializing before the runtime starts creating IPC buffers.
        // Preserve all initialization calls, then retain the existing NtOpenFile client hook.
        target.DirectoryRetired=true;target.StartupContextHook=false;target.Functions[3]=target.OpenFile;target.Rearm=true;Marshal.WriteIntPtr(context,96,target.OpenFile);
        Note("Runtime IPC initialized; retained NtOpenFile pipe-client interception.");
    }
    static string? PrivatePipe(string name,uint process,bool creating)
    {
        foreach(var prefix in new[]{@"\??\pipe\",@"\Device\NamedPipe\"})
        {
            if(name.StartsWith(prefix+@"uv\",StringComparison.OrdinalIgnoreCase))
            {
                var logical=name[prefix.Length..];var pair=logical[3..].Split('-');
                if(pair.Length==2&&pair[0].Length is >0 and <=20&&pair[1].Length is >0 and <=10&&pair[0].All(char.IsAsciiDigit)&&pair[1].All(char.IsAsciiDigit)&&ulong.TryParse(pair[0],out _)&&uint.TryParse(pair[1],out var owner)&&(!creating||owner==process))return logical;
                return null;
            }
            if(!name.StartsWith(prefix+"msys-",StringComparison.OrdinalIgnoreCase))continue;
            var suffix=name[prefix.Length..];
            if(suffix.Length>220||suffix.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('-' or '_' or '.')))return null;
            return suffix;
        }
        return null;
    }
    static string? PrivatePipe(Target target,byte[] attributes,string name,bool creating)
    {
        var root=(IntPtr)BitConverter.ToInt64(attributes,8);
        if(root==IntPtr.Zero)return PrivatePipe(name,target.Id,creating);
        var logical=CodePrivatePipes.RelativeName(name,target.Id,creating);if(logical==null)return null;
        // Never resolve or open an agent-nominated root/name on the host. Inspect only a zero-right
        // duplicate's type/device category, then allocate a NEW random local buffer as usual.
        if(!DuplicateHandle(target.Handle,root,GetCurrentProcess(),out var duplicate,0,false,0))return null;
        using var owned=new SafeFileHandle(duplicate,true);
        return ObjectText(duplicate,2)=="File"&&GetFileType(owned)==3?logical:null;
    }
    void CreatePrivatePipe(Target target,IntPtr context,byte[] attributes,string logicalName)
    {
        // A short pipeline creates several process-tracker pipes per child. This is a
        // lifetime request bound, not a live-object bound; retain the separate 16 MiB
        // aggregate byte cap and 1 MiB endpoint caps below.
        var stack=Read(target.Handle,Marshal.ReadIntPtr(context,152)+40,72);
        uint disposition=BitConverter.ToUInt32(stack,8),options=BitConverter.ToUInt32(stack,16),type=BitConverter.ToUInt32(stack,24),readMode=BitConverter.ToUInt32(stack,32),completion=BitConverter.ToUInt32(stack,40),instances=BitConverter.ToUInt32(stack,48),incoming=BitConverter.ToUInt32(stack,56),outgoing=BitConverter.ToUInt32(stack,64);
        if(disposition is not(2 or 3)||type>3||readMode>1||completion>1||instances!=1||(options&~0x72u)!=0)
            throw new InvalidDataException($"Unsupported private IPC request: disposition={disposition}, options={options:x}, type={type}, read={readMode}, completion={completion}, instances={instances}, buffers={incoming}/{outgoing}. No host pipe was opened.");
        pipeBudget.Admit(incoming,outgoing);
        uint access=(uint)Marshal.ReadInt64(context,136);bool read=(access&0x80000001)!=0,write=(access&0x40000002)!=0;
        if(!read&&!write)throw new InvalidDataException("Private IPC requires a read/write endpoint.");
        bool previous=privatePipes.TryGetValue(logicalName,out var existing);
        var pipeName=previous?existing![@"\??\pipe\".Length..]:"Mason.Code."+Guid.NewGuid().ToString("N");var win32=@"\\.\pipe\"+pipeName;
        var sid=new System.Security.Principal.SecurityIdentifier(sandbox.Sid).Value;var restriction=new System.Security.Principal.SecurityIdentifier(sandbox.WriteSid).Value;
        using var currentIdentity=System.Security.Principal.WindowsIdentity.GetCurrent();var user=currentIdentity.User!.Value;
        // This descriptor belongs ONLY to this newly created buffer, never to a namespace or existing object.
        var descriptor=new System.Security.AccessControl.RawSecurityDescriptor($"D:P(A;;GA;;;{user})(A;;GA;;;{sid})(A;;GA;;;{restriction})S:(ML;;NW;;;LW)");
        var bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);var memory=Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes,0,memory,bytes.Length);var security=new Security { Length=Marshal.SizeOf<Security>(),Descriptor=memory };
            // MSYS read endpoints also request WRITE_ATTRIBUTES for blocking-mode changes.
            // Acquire that right on the NEW source endpoint; the duplicate still receives only
            // its validated requested rights, including no data-write right on a read endpoint.
            uint mode=(read?(write||(access&0x100)!=0?3u:1u):2u)|0x00080000u;if((options&0x30)==0)mode|=0x40000000;
            using var server=CreateNamedPipe(win32,mode,((type&1)!=0?4u:0u)|(readMode==1?2u:0u)|(completion==1?1u:0u)|8u,1,outgoing,incoming,0,ref security);
            if(server.IsInvalid)
            {
                int error=Marshal.GetLastWin32Error();
                if(previous&&error is 5 or 231){Return(target,context,1,unchecked((int)(error==5?0xc0000022u:0xc00000aeu)));return;}
                throw new Win32Exception(error,"Cannot create a new command-owned IPC buffer.");
            }
            if(GetFileType(server)!=3)throw new InvalidDataException("The new IPC handle was not a pipe.");
            // Only data/attribute rights cross into the sandbox. No WRITE_OWNER/WRITE_DAC or host token handle.
            uint delegated=access&~0x010c0000u;
            if((delegated&~0xc012019fu)!=0)throw new InvalidDataException("Unsupported private IPC endpoint rights.");
            if((delegated&0x80000000)!=0)delegated=(delegated&~0x80000000u)|0x120089u;
            if((delegated&0x40000000)!=0)delegated=(delegated&~0x40000000u)|0x120116u;
            var handle=Duplicate(server.DangerousGetHandle(),target.Handle,delegated,(BitConverter.ToUInt32(attributes,24)&2)!=0);
            SafeWrite(target,Marshal.ReadIntPtr(context,128),BitConverter.GetBytes((long)handle));
            var io=new byte[16];BitConverter.GetBytes(2L).CopyTo(io,8);SafeWrite(target,Marshal.ReadIntPtr(context,192),io);
            privatePipes[logicalName]=@"\??\pipe\"+pipeName;Return(target,context,1);
            Note("Created private pipe server: "+logicalName+" rights="+delegated.ToString("x"));
            // Drop the broker's endpoint immediately: retaining it would prevent EOF / broken-pipe delivery.
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    void Rewrite(Target target,uint id,IntPtr context,byte[] attributes,string name,IntPtr root)
    {
        var scratch=Scratch(target,id);
        var bytes=new byte[64+name.Length*2+2];attributes.CopyTo(bytes,0);
        BitConverter.GetBytes((long)root).CopyTo(bytes,8);BitConverter.GetBytes((long)(scratch+48)).CopyTo(bytes,16);
        // The strict token's default ACL, not the runtime's global descriptor, applies to private objects.
        Array.Clear(bytes,32,16);BitConverter.GetBytes((ushort)(name.Length*2)).CopyTo(bytes,48);
        BitConverter.GetBytes((ushort)(name.Length*2+2)).CopyTo(bytes,50);BitConverter.GetBytes((long)(scratch+64)).CopyTo(bytes,56);
        Encoding.Unicode.GetBytes(name).CopyTo(bytes,64);SafeWrite(target,scratch,bytes);Marshal.WriteIntPtr(context,184,scratch);
    }
    static IntPtr Scratch(Target target,uint id)
    {
        if(!target.Scratch.TryGetValue(id,out var scratch))
        {
            scratch=VirtualAllocEx(target.Handle,IntPtr.Zero,(UIntPtr)16384,0x3000,4);
            if(scratch==IntPtr.Zero)throw new Win32Exception();target.Scratch.Add(id,scratch);
        }
        return scratch;
    }
    static string? PrivateName(string name)
    {
        var session=System.Diagnostics.Process.GetCurrentProcess().SessionId;
        foreach(var prefix in new[]{@"\BaseNamedObjects",$@"\Sessions\{session}\BaseNamedObjects",$@"\Sessions\BNOLINKS\{session}"})
        {
            if(name.Equals(prefix,StringComparison.OrdinalIgnoreCase))return "";
            if(name.StartsWith(prefix+"\\",StringComparison.OrdinalIgnoreCase))return name[(prefix.Length+1)..];
        }
        return null;
    }
    static void Return(Target target,IntPtr context,int function,int status=0)
    {
        // Execute the verified syscall stub's own RET, keeping both the regular and CET shadow stack balanced.
        var stub=Read(target.Handle,target.Functions[function],64);int offset=-1;
        for(int i=0;i<stub.Length-2;i++)if(stub[i]==0x0f&&stub[i+1]==0x05&&stub[i+2]==0xc3){offset=i+2;break;}
        if(offset<0)throw new InvalidDataException("Unsupported native syscall stub; no compatibility return was synthesized.");
        Marshal.WriteIntPtr(context,248,target.Functions[function]+offset);Marshal.WriteInt64(context,120,(uint)status);
    }
    static IntPtr Duplicate(IntPtr source,IntPtr target,uint access,bool inherit=false)
    {
        if(!DuplicateHandle(GetCurrentProcess(),source,target,out var handle,access,inherit,0))throw new Win32Exception();return handle;
    }
    void WithContext(SafeFileHandle thread,Action<IntPtr> action,[System.Runtime.CompilerServices.CallerMemberName]string caller="")
    {
        var memory=Marshal.AllocHGlobal(1250);var context=(IntPtr)(((long)memory+15)&~15L);
        try { Marshal.WriteInt32(context,48,0x100013);ContextStage(caller+" GetThreadContext");if(!GetThreadContext(thread,context))throw new Win32Exception();ContextStage("none");action(context);ContextStage(caller+" SetThreadContext");if(!SetThreadContext(thread,context))throw new Win32Exception(); }
        finally { ContextStage("none");Marshal.FreeHGlobal(memory); }
    }
    static byte[] Header(IntPtr process,IntPtr image,bool allowManaged=false)
    {
        var dos=Read(process,image,64);int offset=BitConverter.ToInt32(dos,60);
        if(BitConverter.ToUInt16(dos)!=0x5a4d||offset<64||offset>1024*1024)throw new InvalidDataException("Invalid runtime image.");
        var header=Read(process,image+offset,264);
        if(BitConverter.ToUInt32(header)!=0x4550)throw new InvalidDataException("Invalid runtime PE signature.");
        if(BitConverter.ToUInt16(header,24)==0x20b&&BitConverter.ToUInt16(header,4)==0x8664)return header;
        // CLR AnyCPU assemblies use PE32 headers even inside an x64 process.
        // Record their range only; never resolve native compatibility exports from them.
        if(allowManaged&&BitConverter.ToUInt16(header,24)==0x10b&&BitConverter.ToUInt16(header,4)==0x14c&&BitConverter.ToUInt16(header,20)>=224&&BitConverter.ToUInt32(header,116)>=15)
        {
            uint size=BitConverter.ToUInt32(header,80),rva=BitConverter.ToUInt32(header,232),bytes=BitConverter.ToUInt32(header,236);
            if(rva>0&&bytes>=72&&rva<size&&bytes<=size-rva)
            {
                var clr=Read(process,image+checked((int)rva),72);uint flags=BitConverter.ToUInt32(clr,16);
                if(BitConverter.ToUInt32(clr)==72&&(flags&1)!=0&&(flags&0x12)==0)return header;
            }
        }
        throw new InvalidDataException("Auto runtime requires native x64 or an IL-only AnyCPU assembly.");
    }
    static IntPtr[] Exports(IntPtr process,IntPtr image,string[] names)
    {
        var header=Header(process,image);int rva=BitConverter.ToInt32(header,136);if(rva==0)return [];
        var export=Read(process,image+rva,40);int count=BitConverter.ToInt32(export,24);if(count<4||count>65536)return [];
        var nameRvas=Read(process,image+BitConverter.ToInt32(export,32),count*4);var ordinals=Read(process,image+BitConverter.ToInt32(export,36),count*2);
        var result=new IntPtr[4];int found=0;
        for(int i=0;i<count;i++)
        {
            var text=Read(process,image+BitConverter.ToInt32(nameRvas,i*4),48);int zero=Array.IndexOf(text,(byte)0);if(zero<0)continue;
            int index=Array.IndexOf(names,Encoding.ASCII.GetString(text,0,zero));if(index<0)continue;
            var address=Read(process,image+BitConverter.ToInt32(export,28)+BitConverter.ToUInt16(ordinals,i*2)*4,4);result[index]=image+BitConverter.ToInt32(address);found++;
        }
        return found==4?result:[];
    }
    static byte[]? TryRead(IntPtr process,IntPtr address,int length)
    {
        var bytes=new byte[length];return length==0||ReadProcessMemory(process,address,bytes,length,out var read)&&(long)read==length?bytes:null;
    }
    static byte[] Read(IntPtr process,IntPtr address,int length)=>TryRead(process,address,length)??throw new Win32Exception();
    void SafeWrite(Target target,IntPtr address,byte[] bytes)
    {
        using var hold=HoldCommand();WritePrivateHeld(target,address,bytes,hold);
    }
    static void CloseFile(IntPtr data){var file=Marshal.ReadIntPtr(data);if(file!=IntPtr.Zero)CloseHandle(file);}
    static string Location(Target target,IntPtr address)
    {
        long value=(long)address;var module=target.Modules.FirstOrDefault(m=>value>=m.Start&&value<m.End);
        return module.Name==null?value.ToString("x"):module.Name+"+"+(value-module.Start).ToString("x");
    }
    void DescribeMsysHeap(Target target,long stack)
    {
        long cursor=0x800000000,end=0xa00000000;var data=new byte[48];
        for(int region=0;region<64&&cursor<end;region++)
        {
            if(VirtualQueryEx(target.Handle,(IntPtr)cursor,data,(UIntPtr)data.Length)==UIntPtr.Zero)break;
            long start=BitConverter.ToInt64(data),length=BitConverter.ToInt64(data,24),next=start+length;
            if(next<=cursor)break;
            if(BitConverter.ToUInt32(data,32)!=0x10000)Note("MSYS heap overlap start="+start.ToString("x")+" bytes="+length.ToString("x")+" allocation="+Location(target,(IntPtr)BitConverter.ToInt64(data,8))+" state="+BitConverter.ToUInt32(data,32).ToString("x")+" type="+BitConverter.ToUInt32(data,40).ToString("x")+" currentStack="+(stack>=start&&stack<next));
            cursor=next;
        }
    }
    void RecordModule(Target target,IntPtr image,IntPtr file)
    {
        TraceQa("module header begin pid="+target.Id);
        var header=Header(target.Handle,image,true);var name=new StringBuilder(32768);GetFinalPathNameByHandle(file,name,(uint)name.Capacity,0);
        TraceQa("module path pid="+target.Id+" name="+Path.GetFileName(name.ToString()));
        target.Modules.RemoveAll(m=>m.Start==(long)image);
        if(target.Modules.Count>=512)throw new InvalidDataException("Auto command exceeded 512 loaded native modules.");
        target.Modules.Add(((long)image,(long)image+BitConverter.ToUInt32(header,80),Path.GetFileName(name.ToString())));
        HandoffModule(target,image,file,name.ToString());
        if(Path.GetFileName(name.ToString()).Equals("msys-2.0.dll",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(name.ToString()).Equals(".exe",StringComparison.OrdinalIgnoreCase))Note("Runtime image="+Path.GetFileName(name.ToString())+" pid="+target.Id+" base="+image.ToString("x")+" preferred="+BitConverter.ToInt64(header,48).ToString("x"));
        if(Path.GetFileName(name.ToString()).Equals("kernel32.dll",StringComparison.OrdinalIgnoreCase))target.AnonymousPipe=Export(target.Handle,image,"CreatePipe");
    }
    static IntPtr Export(IntPtr process,IntPtr image,string symbol)
    {
        var header=Header(process,image);int rva=BitConverter.ToInt32(header,136);if(rva==0)return IntPtr.Zero;
        var export=Read(process,image+rva,40);int count=BitConverter.ToInt32(export,24);if(count<1||count>65536)return IntPtr.Zero;
        var names=Read(process,image+BitConverter.ToInt32(export,32),count*4);var ordinals=Read(process,image+BitConverter.ToInt32(export,36),count*2);
        for(int i=0;i<count;i++)
        {
            var name=Read(process,image+BitConverter.ToInt32(names,i*4),symbol.Length+1);if(Encoding.ASCII.GetString(name,0,symbol.Length)!=symbol||name[^1]!=0)continue;
            var function=Read(process,image+BitConverter.ToInt32(export,28)+BitConverter.ToUInt16(ordinals,i*2)*4,4);return image+BitConverter.ToInt32(function);
        }
        return IntPtr.Zero;
    }
    public void Dispose(){CloseDeferredStops();CloseReturnedBirths();foreach(var target in targets.Values)target.BirthThread?.Dispose();CloseAnonymous(null);pipeRoot?.Dispose();crypto.Dispose();Marshal.FreeHGlobal(eventData);}
    [DllImport("ntdll.dll")]static extern int NtQueryObject(IntPtr handle,int information,IntPtr buffer,int size,out int needed);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool WaitForDebugEvent(IntPtr data,uint timeout);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool ContinueDebugEvent(uint process,uint thread,uint status);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool member);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(IntPtr job,uint exit);
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeFileHandle OpenThread(uint access,bool inherit,uint id);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetThreadContext(SafeFileHandle thread,IntPtr context);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetThreadContext(SafeFileHandle thread,IntPtr context);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadProcessMemory(IntPtr process,IntPtr address,byte[] buffer,int length,out IntPtr read);
    [DllImport("kernel32.dll",SetLastError=true)] static extern UIntPtr VirtualQueryEx(IntPtr process,IntPtr address,[Out] byte[] information,UIntPtr length);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessMitigationPolicy(IntPtr process,int policy,out uint buffer,UIntPtr size);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process,int information,IntPtr buffer,int length,out int required);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr VirtualAllocEx(IntPtr process,IntPtr address,UIntPtr size,uint type,uint protection);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr process,IntPtr address,UIntPtr size,uint type);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool DuplicateHandle(IntPtr sourceProcess,IntPtr source,IntPtr targetProcess,out IntPtr target,uint access,bool inherit,uint options);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",EntryPoint="GetFinalPathNameByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(IntPtr handle,StringBuilder name,uint size,uint flags);
    [StructLayout(LayoutKind.Sequential)] struct Security { public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit; }
    [DllImport("kernel32.dll",EntryPoint="CreateNamedPipeW",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateNamedPipe(string name,uint access,uint mode,uint instances,uint outbound,uint inbound,uint timeout,ref Security security);
}
