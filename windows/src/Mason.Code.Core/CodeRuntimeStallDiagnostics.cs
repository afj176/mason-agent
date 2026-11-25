using System.Runtime.InteropServices;

namespace Mason.Code;

internal sealed partial class CodeRuntimeBroker
{
    string contextStage="none";
    long lastDebugProgress;
#if MASON_HANDOFF_QA
    int stallSampleActive,stallContextsCaptured;
#endif
    [System.Diagnostics.Conditional("MASON_HANDOFF_QA")]
    void ContextStage(string value)=>Volatile.Write(ref contextStage,value);
    [System.Diagnostics.Conditional("MASON_HANDOFF_QA")]
    void DebugProgress()=>Volatile.Write(ref lastDebugProgress,Environment.TickCount64);
    [System.Diagnostics.Conditional("MASON_HANDOFF_QA")]
    void CaptureStall()=>SampleStall(capture:true);
    void SampleStall(bool capture=false)
    {
#if MASON_HANDOFF_QA
        if(Environment.GetEnvironmentVariable("MASON_STALL_TRACE")!="1"||Environment.TickCount64-Volatile.Read(ref lastDebugProgress)<2000)return;
        if(Interlocked.Exchange(ref stallSampleActive,1)!=0)return;
        // Only the debug-loop thread samples contexts, between event waits.
        // The timer is query-only: it cannot race a barrier's suspend counts.
        bool contexts=capture&&Environment.TickCount64-Volatile.Read(ref lastDebugProgress)>5000&&Volatile.Read(ref contextStage)=="none"&&Interlocked.Exchange(ref stallContextsCaptured,1)==0;
        try
        {
            var members=new byte[8+64*8];
            if(!QueryHoldJob(job,3,members,(uint)members.Length,out _))return;
            uint count=BitConverter.ToUInt32(members,4);if(count>64)return;
            var ids=Enumerable.Range(0,(int)count).Select(i=>checked((uint)BitConverter.ToInt64(members,8+i*8))).ToHashSet();
            var lines=new List<string>{"stage="+Volatile.Read(ref contextStage)+" noEventMs="+(Environment.TickCount64-Volatile.Read(ref lastDebugProgress))};
            foreach(var item in CodeThreadCensus.Capture(ids).Take(64))
            {
                using var thread=HoldOpenThread(0x10084a,false,item.Thread);
                if(thread.IsInvalid){lines.Add(item.Process+"/"+item.Thread+" query-denied");continue;}
                var buffer=Marshal.AllocHGlobal(64);
                try
                {
                    if(HandoffQueryThread(thread.DangerousGetHandle(),0,buffer,48,out _)<0||Marshal.ReadInt64(buffer,16)!=item.Process)continue;
                    int terminal=HandoffQueryThread(thread.DangerousGetHandle(),20,buffer,4,out _)>=0?Marshal.ReadInt32(buffer):-1;
                    int suspend=HandoffQueryThread(thread.DangerousGetHandle(),35,buffer,4,out _)>=0?Marshal.ReadInt32(buffer):-1;
                    int syscall=HandoffQueryThread(thread.DangerousGetHandle(),21,buffer,24,out _)>=0?(ushort)Marshal.ReadInt16(buffer,8):-1;
                    lines.Add(item.Process+"/"+item.Thread+" terminal="+terminal+" suspend="+suspend+" syscall="+syscall);
                    if(contexts&&terminal==0)
                    {
                        using var process=HoldOpenProcess(0x1410,false,item.Process);
                        if(process.IsInvalid||!IsProcessInJob(process.DangerousGetHandle(),job,out bool member)||!member)continue;
                        sandbox.VerifyIdentity(process.DangerousGetHandle());
                        uint before=HoldSuspendThread(thread);if(before==uint.MaxValue)continue;
                        var memory=Marshal.AllocHGlobal(1250);var context=(IntPtr)(((long)memory+15)&~15L);
                        try
                        {
                            Marshal.WriteInt32(context,48,0x100013);
                            if(!GetThreadContext(thread,context))continue;
                            string Address(long address)
                            {
                                var info=new byte[48];var name=new System.Text.StringBuilder(1024);
                                if(VirtualQueryEx(process.DangerousGetHandle(),(IntPtr)address,info,(UIntPtr)48)!=(UIntPtr)48||StallMappedName(process,(IntPtr)address,name,(uint)name.Capacity)==0)return address.ToString("x");
                                return Path.GetFileName(name.ToString())+"+"+(address-BitConverter.ToInt64(info,8)).ToString("x");
                            }
                            var stack=TryRead(process.DangerousGetHandle(),Marshal.ReadIntPtr(context,152),128);
                            lines.Add("CONTEXT "+item.Process+"/"+item.Thread+" ip="+Address(Marshal.ReadInt64(context,248))+" dr7="+Marshal.ReadInt64(context,112).ToString("x")+" stack="+(stack==null?"unreadable":string.Join(',',Enumerable.Range(0,16).Select(i=>Address(BitConverter.ToInt64(stack,i*8))))));
                        }
                        finally{Marshal.FreeHGlobal(memory);HoldResumeThread(thread);}
                    }
                }
                finally{Marshal.FreeHGlobal(buffer);}
            }
            Console.Error.WriteLine("QA_STALL "+string.Join(" | ",lines));
        }
        catch(Exception error){Console.Error.WriteLine("QA_STALL query failed: "+error.GetType().Name+" "+error.Message);}
        finally{Volatile.Write(ref stallSampleActive,0);}
#endif
    }
#if MASON_HANDOFF_QA
    [DllImport("psapi.dll",EntryPoint="GetMappedFileNameW",CharSet=CharSet.Unicode)]static extern uint StallMappedName(Microsoft.Win32.SafeHandles.SafeFileHandle process,IntPtr address,System.Text.StringBuilder name,uint count);
#endif
}
