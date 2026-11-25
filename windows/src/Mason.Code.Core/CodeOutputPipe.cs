using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mason.Code;

/// <summary>Only a pipe write end crosses into Auto. The reader drains to an already-open, bounded log handle.</summary>
internal sealed class CodeOutputPipe : IDisposable
{
    public SafeFileHandle Writer { get; }
    readonly SafeFileHandle reader;
    public CodeOutputPipe()
    {
        var security=new Security { Length=Marshal.SizeOf<Security>(),Inherit=true };
        if(!CreatePipe(out reader,out var writer,ref security,0))throw new Win32Exception();Writer=writer;
        if(!SetHandleInformation(reader,1,0)){Dispose();throw new Win32Exception();}
    }
    public Task Drain(SafeFileHandle log)
    {
        // The child has inherited its own write handle. Holding ours would prevent EOF.
        Writer.Dispose();
        return Task.Run(()=>
        {
            using var input=new FileStream(reader,FileAccess.Read,4096,false);using var output=new FileStream(log,FileAccess.Write,4096,false);
            var buffer=new byte[4096];int count,written=0;
            while((count=input.Read(buffer))!=0)
            {
                if(written<=256*1024){output.Write(buffer,0,count);output.Flush();written+=count;}
            }
        });
    }
    public void Dispose(){Writer.Dispose();reader.Dispose();}
    [StructLayout(LayoutKind.Sequential)]struct Security { public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit; }
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool CreatePipe(out SafeFileHandle read,out SafeFileHandle write,ref Security security,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetHandleInformation(SafeFileHandle handle,uint mask,uint flags);
}
