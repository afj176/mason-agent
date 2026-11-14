using System.Runtime.InteropServices;

namespace Mason.Code;

/// <summary>Bounded x64 SYSTEM_PROCESS_INFORMATION / SYSTEM_THREAD_INFORMATION census.</summary>
public static class CodeThreadCensus
{
    // Windows SDK winternl.h: header 256, process ID 80, thread stride 80,
    // thread CLIENT_ID at 40. Admission pins the native x64 Windows build.
    public static List<(uint Process,uint Thread)> Decode(ReadOnlySpan<byte> data,IReadOnlySet<uint> members)
    {
        var result=new List<(uint,uint)>();var processes=new HashSet<uint>();var threads=new HashSet<uint>();int offset=0;
        while(true)
        {
            if(offset<0||data.Length-offset<256)throw new InvalidDataException("Truncated native process census.");
            var entry=data[offset..];uint next=BitConverter.ToUInt32(entry),count=BitConverter.ToUInt32(entry[4..]);ulong rawPid=BitConverter.ToUInt64(entry[80..]);
            if(rawPid>uint.MaxValue||count>100000||(next!=0&&(next<256||(next&7)!=0||next>entry.Length)))throw new InvalidDataException("Unsupported native process census layout.");
            int size=next==0?entry.Length:checked((int)next);
            if((ulong)count*80>(ulong)(size-256))throw new InvalidDataException("Native thread census exceeds its process record.");
            uint pid=(uint)rawPid;
            if(members.Contains(pid))
            {
                if(count>1024||!processes.Add(pid))throw new InvalidDataException("Owned process thread census exceeded its bound or duplicated a process.");
                for(int i=0;i<count;i++)
                {
                    var thread=entry.Slice(256+i*80,80);ulong owner=BitConverter.ToUInt64(thread[40..]),id=BitConverter.ToUInt64(thread[48..]);
                    if(owner!=pid||id is 0 or >uint.MaxValue||!threads.Add((uint)id))throw new InvalidDataException("Native thread census has an invalid ownership or duplicate identity.");
                    result.Add((pid,(uint)id));
                }
            }
            if(next==0)return result;
            offset=checked(offset+(int)next);
        }
    }
    internal static List<(uint Process,uint Thread)> Capture(IReadOnlySet<uint> members)
    {
        if(RuntimeInformation.ProcessArchitecture!=Architecture.X64)throw new PlatformNotSupportedException("Native thread census requires x64.");
        for(int size=1024*1024;size<=64*1024*1024;size=checked(size*2))
        {
            var buffer=Marshal.AllocHGlobal(size);
            try
            {
                int status=NtQuerySystemInformation(5,buffer,(uint)size,out uint needed);
                if(status is unchecked((int)0xc0000004) or unchecked((int)0xc0000023))continue;
                if(status<0||needed is <256||needed>size)throw new InvalidDataException("Cannot obtain a complete native thread census: "+status.ToString("x8"));
                var data=new byte[needed];Marshal.Copy(buffer,data,0,data.Length);return Decode(data,members);
            }
            finally{Marshal.FreeHGlobal(buffer);}
        }
        throw new InvalidDataException("Native process/thread census exceeded 64 MiB.");
    }
    [DllImport("ntdll.dll")]static extern int NtQuerySystemInformation(int information,IntPtr data,uint bytes,out uint needed);
}
