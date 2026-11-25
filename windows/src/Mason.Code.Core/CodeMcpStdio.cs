using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mason.Code;

internal sealed class CodeMcpStdio : IDisposable
{
    readonly Process child;
    readonly CancellationTokenSource deadline;
    readonly CancellationTokenRegistration stop;
    readonly Task drain;
    public CodeMcpStdio(McpRegistration registration,CancellationToken token)
    {
        string executable=registration.Executable!;
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(executable)!,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),
            StandardOutputEncoding=new UTF8Encoding(false,true)};
        foreach(var arg in registration.Arguments??[])start.ArgumentList.Add(arg);
        foreach(var variable in new[]{"NODE_OPTIONS","NODE_PATH","PYTHONPATH","PYTHONHOME"})start.Environment.Remove(variable);
        child=Process.Start(start)??throw new IOException("Could not start registered MCP server.");
        deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        stop=deadline.Token.Register(()=>{try{if(!child.HasExited)child.Kill(true);}catch(InvalidOperationException){}});
        drain=child.StandardError.BaseStream.CopyToAsync(Stream.Null,deadline.Token);
    }
    public async Task<JsonElement> Send(string method,object parameters,int? id)
    {
        var message=new Dictionary<string,object>{{"jsonrpc","2.0"},{"method",method},{"params",parameters}};
        if(id!=null)message["id"]=id;
        string line=JsonSerializer.Serialize(message);
        if(line.Length>32768)throw new InvalidDataException("MCP request exceeds 32 KiB.");
        await child.StandardInput.WriteLineAsync(line.AsMemory(),deadline.Token);
        await child.StandardInput.FlushAsync(deadline.Token);
        if(id==null)return default;
        var buffer=new char[1];var text=new StringBuilder();int total=0;
        while(total++<131072 && await child.StandardOutput.ReadAsync(buffer,deadline.Token)>0)
        {
            if(buffer[0]!='\n'){if(buffer[0]!='\r')text.Append(buffer[0]);continue;}
            using var record=JsonDocument.Parse(text.ToString());text.Clear();var root=record.RootElement;
            if(!root.TryGetProperty("id",out var found)||!found.TryGetInt32(out var number)||number!=id)continue;
            if(root.TryGetProperty("error",out var error))throw new InvalidDataException("MCP error: "+error);
            return root.GetProperty("result").Clone();
        }
        throw new InvalidDataException("Missing or oversized MCP response.");
    }
    public void Dispose()
    {
        try{child.StandardInput.Close();if(!child.WaitForExit(250))deadline.Cancel();}catch(IOException){}
        finally {deadline.Cancel();stop.Dispose();try{drain.GetAwaiter().GetResult();}catch(Exception e) when(e is IOException or OperationCanceledException){}
            child.Dispose();deadline.Dispose();}
    }
}
