using System.ComponentModel;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Mason.Code;

public enum CodeNetworkMode { Offline, Ask }

/// <summary>Exact destination policy. Untrusted clients never choose an already-connected host socket.</summary>
public static class CodeNetworkPolicy
{
    public static string Host(string value,int port)
    {
        if(port!=443||value.Length is <3 or >253||value!=value.Trim()||value.EndsWith('.')||IPAddress.TryParse(value,out _)||Uri.CheckHostName(value)!=UriHostNameType.Dns)
            throw new UnauthorizedAccessException("Network relay requires a DNS hostname and HTTPS port 443.");
        value=value.ToLowerInvariant();
        if(!value.Contains('.')||value=="localhost"||value.EndsWith(".localhost")||value.EndsWith(".local")||value.Split('.').Any(p=>p.Length is <1 or >63||p[0]=='-'||p[^1]=='-'||p.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-')))
            throw new UnauthorizedAccessException("Local or malformed network destination denied.");
        return value;
    }
    public static bool Public(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        var b=address.GetAddressBytes();
        if(b.Length==4)return b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&(b[1]==168||b[1]==0||b[1]==2))&&!(b[0]==100&&b[1]>=64&&b[1]<=127)&&!(b[0]==198&&(b[1] is 18 or 19||b[1]==51&&b[2]==100))&&!(b[0]==203&&b[1]==0&&b[2]==113);
        // Admit global unicast only. Reject documentation, transition and embedded-v4 forms.
        return b.Length==16&&(b[0]&0xe0)==0x20&&!(b[0]==0x20&&b[1]==0x01&&(b[2]<2||b[2]==0x0d&&b[3]==0xb8))&&!(b[0]==0x20&&b[1]==0x02);
    }
}

public sealed class CodeNetworkAccess(Func<string,CancellationToken,Task<bool>> approve)
{
    readonly SemaphoreSlim gate=new(1);
    readonly HashSet<string> accepted=[];
    readonly List<string> receipts=[];
    public string[] Receipts { get {lock(receipts)return receipts.ToArray();} }
    internal void Record(string value){lock(receipts){if(receipts.Count<128)receipts.Add(value);}}
    internal async Task<bool> Allow(string host,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(accepted.Contains(host))return true;
            if(accepted.Count>=16)throw new IOException("Network destination count exceeded this command's bound.");
            bool allowed=await approve(host,ct);Record((allowed?"Allowed ":"Denied ")+host+":443");
            if(allowed)accepted.Add(host);return allowed;
        }
        finally{gate.Release();}
    }
}

/// <summary>Host-owned relay. Pipe ACL, live job and permanent token authenticate every connection.</summary>
[SupportedOSPlatform("windows")]
internal sealed class CodeNetworkRelay : IDisposable
{
    readonly CodeSandbox sandbox;readonly IntPtr job;readonly CodeNetworkAccess access;
    readonly CancellationTokenSource lifetime;readonly List<Task> clients=[];readonly SemaphoreSlim slots=new(8);
    readonly Task listener;readonly CodeNetworkTransferBudget transferBudget=new();int connections;bool disposed;
    public string Name {get;}="dbc-network-"+Guid.NewGuid().ToString("N");
    public string Path=>@"\\.\pipe\"+Name;
    public CodeNetworkRelay(CodeSandbox sandbox,IntPtr job,CodeNetworkAccess access,CancellationToken ct)
    {
        this.sandbox=sandbox;this.job=job;this.access=access;lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first=NewPipe(true);listener=Listen(first);
    }
    NamedPipeServerStream NewPipe(bool first)
    {
        using var identity=WindowsIdentity.GetCurrent();var owner=identity.User!.Value;
        var app=new SecurityIdentifier(sandbox.Sid).Value;var write=new SecurityIdentifier(sandbox.WriteSid).Value;
        string sddl=$"D:P(A;;GA;;;{owner})(A;;0x12019b;;;{app})(A;;0x12019b;;;{write})S:(ML;;NW;;;LW)";
        if(!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl,1,out var sd,out _))throw new Win32Exception();
        try
        {
            var sa=new Security{Length=Marshal.SizeOf<Security>(),Descriptor=sd};
            var pipe=CreateNamedPipe(Path,3u|0x40000000u|(first?0x80000u:0),8,16,65536,65536,0,ref sa);
            if(pipe.IsInvalid){pipe.Dispose();throw new Win32Exception();}
            return new NamedPipeServerStream(PipeDirection.InOut,true,false,pipe);
        }
        finally{LocalFree(sd);}
    }
    async Task Listen(NamedPipeServerStream next)
    {
        try
        {
            for(int admitted=0;admitted<64&&!lifetime.IsCancellationRequested;admitted++)
            {
                await slots.WaitAsync(lifetime.Token);
                await next.WaitForConnectionAsync(lifetime.Token);
                var connected=next;
                // Keep a server instance alive for the full command, including between clients.
                next=NewPipe(false);
                var work=Serve(connected);lock(clients)clients.Add(work);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception error){access.Record("Relay stopped: "+error.Message);lifetime.Cancel();}
        finally{next.Dispose();}
    }
    async Task Serve(NamedPipeServerStream pipe)
    {
        using var owned=pipe;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));var ct=deadline.Token;
        bool tunnel=false;
        try
        {
            if(Interlocked.Increment(ref connections)>64)throw new IOException("Network connection quota exceeded.");
            if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var pid))throw new Win32Exception();
            using var process=OpenProcess(0x1000,false,pid);
            if(process.IsInvalid||!IsProcessInJob(process,job,out var member)||!member)throw new UnauthorizedAccessException("Network client is not a member of this command.");
            sandbox.VerifyIdentity(process.DangerousGetHandle());
            var input=new List<byte>();var one=new byte[1];
            while(input.Count<1024){if(await pipe.ReadAsync(one,ct)!=1)throw new IOException("Incomplete network request.");if(one[0]==10)break;input.Add(one[0]);}
            if(input.Count>=1024)throw new IOException("Network request too large.");
            using var request=JsonDocument.Parse(input.ToArray());var root=request.RootElement;
            bool http=root.TryGetProperty("kind",out var kind)&&kind.GetString()=="https";
            if(http)CodeProtocol.Exact(root,"kind","host","port","method","path");else CodeProtocol.Exact(root,"host","port");
            string host=CodeNetworkPolicy.Host(root.GetProperty("host").GetString()??"",root.GetProperty("port").GetInt32());
            if(!await access.Allow(host,ct))throw new UnauthorizedAccessException("Network access declined for "+host+".");
            var addresses=await Dns.GetHostAddressesAsync(host,ct);
            if(addresses.Length==0||addresses.Any(a=>!CodeNetworkPolicy.Public(a)))throw new UnauthorizedAccessException("Destination resolves to a private, local or reserved address.");
            ct.ThrowIfCancellationRequested();
            if(http)
            {
                string method=root.GetProperty("method").GetString()??"",path=root.GetProperty("path").GetString()??"";
                if(method is not ("GET" or "HEAD")||!path.StartsWith('/')||path.Any(char.IsControl))throw new IOException("Package relay supports bounded HTTPS GET/HEAD only.");
                var uri=new Uri("https://"+host+path);
                if(uri.Host!=host||uri.Port!=443||uri.Scheme!="https"||uri.UserInfo.Length!=0)throw new IOException("Invalid package URL.");
                using var handler=new SocketsHttpHandler{UseProxy=false,UseCookies=false,AllowAutoRedirect=false,Credentials=null,
                    ConnectCallback=async (_,token)=>{var connected=new Socket(addresses[0].AddressFamily,SocketType.Stream,ProtocolType.Tcp);try{await connected.ConnectAsync(new IPEndPoint(addresses[0],443),token);return new NetworkStream(connected,true);}catch{connected.Dispose();throw;}}};
                using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
                using var message=new HttpRequestMessage(new HttpMethod(method),uri);
                using var response=await client.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,ct);
                var headers=response.Headers.Concat(response.Content.Headers).Where(h=>h.Key is "Content-Type" or "Content-Length" or "Content-Encoding" or "Location" or "ETag" or "Last-Modified" or "Cache-Control").ToDictionary(h=>h.Key,h=>h.Value.ToArray());
                var metadata=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{ok=true,status=(int)response.StatusCode,headers})+"\n");
                if(metadata.Length>16384)throw new IOException("Package response headers exceeded bound.");
                await pipe.WriteAsync(metadata,ct);tunnel=true;access.Record("HTTPS "+method+" "+host+":443 status="+(int)response.StatusCode);
                await using var body=await response.Content.ReadAsStreamAsync(ct);await Copy(body,pipe,ct);return;
            }
            using var socket=new Socket(addresses[0].AddressFamily,SocketType.Stream,ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(addresses[0],443),ct);
            using var remote=new NetworkStream(socket,false);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true}\n"),ct);tunnel=true;
            access.Record("Connected "+host+":443");
            var up=Copy(pipe,remote,ct);var down=Copy(remote,pipe,ct);
            await Task.WhenAny(up,down);deadline.Cancel();
            try{await Task.WhenAll(up,down);}catch(OperationCanceledException){}
        }
        catch(Exception error)
        {
            access.Record("Connection ended: "+error.Message);
            if(!tunnel&&!ct.IsCancellationRequested)try{await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{error=error.Message})+"\n"),ct);}catch(IOException){}
        }
        finally{slots.Release();}
    }
    async Task Copy(Stream source,Stream target,CancellationToken ct)
    {
        byte[] buffer=new byte[32768];long bytes=0;int count;
        while((count=await source.ReadAsync(buffer,ct))>0)
        {
            transferBudget.Consume(ref bytes,count);
            await target.WriteAsync(buffer.AsMemory(0,count),ct);
        }
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        lifetime.Cancel();try{listener.GetAwaiter().GetResult();Task[] tasks;lock(clients)tasks=clients.ToArray();Task.WhenAll(tasks).GetAwaiter().GetResult();}catch(OperationCanceledException){}
        lifetime.Dispose();slots.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]struct Security{public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl,uint revision,out IntPtr descriptor,out uint size);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr value);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafePipeHandle CreateNamedPipe(string name,uint open,uint mode,uint max,uint output,uint input,uint timeout,ref Security security);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint process);
    [DllImport("kernel32.dll",SetLastError=true)]static extern SafeFileHandle OpenProcess(uint access,bool inherit,uint process);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(SafeFileHandle process,IntPtr job,out bool result);
}
