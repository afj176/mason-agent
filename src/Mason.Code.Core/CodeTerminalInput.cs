using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Mason.Code;

/// <summary>A bounded line-input channel for a single owned workspace process.</summary>
public sealed class CodeTerminalInput : IDisposable
{
    readonly AnonymousPipeServerStream pipe=new(PipeDirection.Out,HandleInheritability.Inheritable,4096);
    readonly Channel<string> lines=Channel.CreateBounded<string>(8);
    internal SafePipeHandle Child => pipe.ClientSafePipeHandle;
    internal void Started()
    {
        pipe.DisposeLocalCopyOfClientHandle();
        _=Task.Run(async ()=>{
            try {
                await foreach(var line in lines.Reader.ReadAllAsync())
                {
                    var bytes=Encoding.UTF8.GetBytes(line+"\r\n");
                    await pipe.WriteAsync(bytes);await pipe.FlushAsync();
                }
            } catch(IOException) {} catch(ObjectDisposedException) {}
            finally {lines.Writer.TryComplete();pipe.Dispose();}
        });
    }
    public bool TrySend(string line)
    {
        if(line.Length>4000 || line.Contains('\0') || line.Contains('\r') || line.Contains('\n'))throw new ArgumentException("Send one line of up to 4,000 characters.");
        if(lines.Reader.Completion.IsCompleted)throw new InvalidOperationException("Terminal input is closed.");
        return lines.Writer.TryWrite(line);
    }
    public void Send(string line)
    {
        if(!TrySend(line))throw new InvalidOperationException("Terminal input is closed or full.");
    }
    public void Eof()=>lines.Writer.TryComplete();
    public void Dispose(){lines.Writer.TryComplete();pipe.Dispose();}
}
