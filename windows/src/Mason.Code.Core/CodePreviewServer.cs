using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Playwright;

namespace Mason.Code;

// Explicit operator launch. The HTTP client checks the connected peer belongs to the owned job
// before sending HTTP, including on reconnect if a different service acquires the port.
public sealed class CodePreviewServer:IAsyncDisposable
{
    readonly CancellationTokenSource stop;
    readonly Task<CodeCommandResult> process;
    readonly HttpClient client;
    readonly int port;
    Func<int,bool>? owns;
    Func<string>? tail;
    public string Output=>Volatile.Read(ref tail)?.Invoke()??"";
    CodePreviewServer(CodeWorkspace scope,ProjectWorkflow flow,int port,CancellationToken ct)
    {
        this.port=port;stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=true,ConnectCallback=Connect};
        client=new(handler){BaseAddress=new Uri("http://127.0.0.1:"+port),MaxResponseContentBufferSize=3*1024*1024,Timeout=TimeSpan.FromSeconds(12)};
        var executable=CodeCommand.ResolveExecutable(flow.Executable,scope.Root);
        process=CodeCommand.Run(executable,flow.Arguments,scope.Resolve(flow.Directory,true),stop.Token,
            timeout:TimeSpan.FromMinutes(30),observeLog:f=>Volatile.Write(ref tail,f),observeOwner:f=>Volatile.Write(ref owns,f));
    }
    public static async Task<CodePreviewServer> Start(CodeWorkspace scope,string name,int port,CancellationToken ct)
    {
        if(port is <1024 or >65535)throw new ArgumentException("Use an unprivileged IPv4 loopback port.");
        var flows=ProjectWorkflows.Load(scope);
        if(!flows.Workflows.TryGetValue(name,out var flow))throw new ArgumentException("Unknown registered development-server workflow.");
        if(Owner(port,0,2)!=0)throw new IOException("Development server port is already occupied.");
        var server=new CodePreviewServer(scope,flow,port,ct);
        try{
            var timer=Stopwatch.StartNew();
            while(timer.Elapsed<TimeSpan.FromSeconds(30)){
                if(server.process.IsCompleted){var r=await server.process;throw new IOException("Development server exited: "+r.Output);}
                int owner=Owner(port,0,2);
                if(owner!=0 && Volatile.Read(ref server.owns)?.Invoke(owner)==true)return server;
                await Task.Delay(125,ct);
            }
            throw new TimeoutException("No owned IPv4 loopback listener became ready. "+server.Output);
        }catch{await server.DisposeAsync();throw;}
    }
    async ValueTask<Stream> Connect(SocketsHttpConnectionContext context,CancellationToken ct)
    {
        var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
        try{
            await socket.ConnectAsync(IPAddress.Loopback,port,ct);
            int remote=((IPEndPoint)socket.LocalEndPoint!).Port;
            for(int i=0;i<12;i++){
                int child=Owner(port,remote,5);
                if(child!=0){
                    if(Volatile.Read(ref owns)?.Invoke(child)!=true)throw new UnauthorizedAccessException("The connection belongs to another process.");
                    return new NetworkStream(socket,ownsSocket:true);
                }
                await Task.Delay(25,ct);
            }
            throw new IOException("Could not verify the development server connection.");
        }catch{socket.Dispose();throw;}
    }
    static int Owner(int local,int remote,int state)
    {
        int size=0;GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,5,0);
        if(size<4||size>16*1024*1024)return 0;
        var ptr=Marshal.AllocHGlobal(size);
        try{
            if(GetExtendedTcpTable(ptr,ref size,false,2,5,0)!=0)return 0;
            int count=Marshal.ReadInt32(ptr);int Decode(int value)=>((value&255)<<8)|((value>>8)&255);
            for(int n=0;n<count && n<(size-4)/24;n++){
                int row=4+24*n;
                if(Marshal.ReadInt32(ptr,row)==state && Marshal.ReadInt32(ptr,row+4)==0x0100007f && Decode(Marshal.ReadInt32(ptr,row+8))==local &&
                    Decode(Marshal.ReadInt32(ptr,row+16))==remote)return Marshal.ReadInt32(ptr,row+20);
            }
            return 0;
        }finally{Marshal.FreeHGlobal(ptr);}
    }
    [DllImport("iphlpapi.dll")]static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int af,int cls,uint reserved);
    internal async Task<(int Status,byte[] Body,Dictionary<string,string> Headers)> Request(IRequest request,Uri uri,CancellationToken ct)
    {
        if(request.Method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE"))throw new InvalidDataException("Unsupported method.");
        var target=new UriBuilder("http","127.0.0.1",port){Path=uri.AbsolutePath,Query=uri.Query.TrimStart('?')}.Uri;
        using var message=new HttpRequestMessage(new HttpMethod(request.Method),target);
        if(request.PostDataBuffer is {} bytes){
            if(bytes.Length>128*1024)throw new InvalidDataException("Request body too large.");
            message.Content=new ByteArrayContent(bytes);
            var headers=await request.AllHeadersAsync();
            if(headers.TryGetValue("content-type",out var type))message.Content.Headers.TryAddWithoutValidation("Content-Type",type);
        }
        using var response=await client.SendAsync(message,ct);
        var fields=new Dictionary<string,string>{["Content-Type"]=response.Content.Headers.ContentType?.ToString()??"application/octet-stream"};
        if(response.Headers.Location is {} loc){
            var dest=new Uri(target,loc);if(dest.GetLeftPart(UriPartial.Authority)!=target.GetLeftPart(UriPartial.Authority))throw new IOException("Redirect outside the owned service.");
            fields["Location"]=dest.PathAndQuery;
        }
        return ((int)response.StatusCode,await response.Content.ReadAsByteArrayAsync(ct),fields);
    }
    public async ValueTask DisposeAsync(){stop.Cancel();client.Dispose();try{await process;}catch(OperationCanceledException){}finally{stop.Dispose();}}
}
