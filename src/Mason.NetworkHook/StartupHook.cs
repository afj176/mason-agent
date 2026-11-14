using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

// Transport integration only. The host relay authenticates the process and enforces policy.
public static class StartupHook
{
    public static void Initialize()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MASON_NETWORK_PIPE")))return;
        var directory=AppContext.BaseDirectory;
        if(directory==null||!File.Exists(Path.Combine(directory,"NuGet.Protocol.dll")))return;
        AssemblyLoadContext.Default.Resolving+=(_,name)=>
        {
            if(name.Name==null||!name.Name.StartsWith("NuGet.",StringComparison.Ordinal))return null;
            var file=Path.Combine(directory,name.Name+".dll");
            return File.Exists(file)?AssemblyLoadContext.Default.LoadFromAssemblyPath(file):null;
        };
        InstallContext();
        AppDomain.CurrentDomain.AssemblyLoad+=(_,args)=>
        {
            if(args.LoadedAssembly.GetName().Name!="NuGet.Protocol")return;
            var context=AssemblyLoadContext.GetLoadContext(args.LoadedAssembly);
            if(context==null||context==AssemblyLoadContext.Default)return;
            // MSBuild isolates restore tasks. Install against that context's public
            // NuGet extension point instead of changing a disconnected default copy.
            var adapter=context.LoadFromAssemblyPath(typeof(StartupHook).Assembly.Location);
            adapter.GetType(nameof(StartupHook))!.GetMethod(nameof(InstallContext))!.Invoke(null,null);
        };
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void InstallContext()=>Repository.Provider=new Providers();
    sealed class Providers:Repository.ProviderFactory
    {
        public override IEnumerable<Lazy<INuGetResourceProvider>> GetCoreV3()
        {
            yield return new(()=>new PipeProvider());
            foreach(var provider in base.GetCoreV3())
                if(provider.Value is not HttpHandlerResourceV3Provider)yield return provider;
        }
    }
    sealed class PipeProvider:ResourceProvider
    {
        public PipeProvider():base(typeof(HttpHandlerResource),nameof(PipeProvider),NuGetResourceProviderPositions.First){}
        public override Task<Tuple<bool,INuGetResource?>> TryCreate(SourceRepository source,CancellationToken token)
        {
            if(!source.PackageSource.IsHttp)return Task.FromResult(Tuple.Create<bool,INuGetResource?>(false,null));
            if(source.PackageSource.SourceUri.Scheme!="https")throw new IOException("Sandbox NuGet sources require HTTPS.");
            var transport=new PackageHandler();
            return Task.FromResult(Tuple.Create<bool,INuGetResource?>(true,new HttpHandlerResourceV3(new HttpClientHandler(),transport)));
        }
    }
    sealed class PackageHandler:HttpMessageHandler
    {
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        if(request.Method!=HttpMethod.Get&&request.Method!=HttpMethod.Head)throw new IOException("Sandbox package access supports GET/HEAD only.");
        Uri uri=request.RequestUri??throw new IOException("Package URL missing.");
        for(int redirects=0;redirects<=5;redirects++)
        {
            var response=await Fetch(uri,request.Method,ct);
            if((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)||response.Headers.Location==null)return response;
            var next=new Uri(uri,response.Headers.Location);response.Dispose();uri=next;
        }
        throw new IOException("Package redirect limit exceeded.");
    }
    }
    static async Task<HttpResponseMessage> Fetch(Uri uri,HttpMethod method,CancellationToken ct)
    {
        if(uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length!=0)throw new IOException("Package source requires public HTTPS on port 443 without embedded credentials.");
        string path=Environment.GetEnvironmentVariable("MASON_NETWORK_PIPE")??throw new IOException("Network relay unavailable.");
        const string prefix=@"\\.\pipe\";
        if(!path.StartsWith(prefix+"dbc-network-",StringComparison.Ordinal))throw new IOException("Invalid relay path.");
        var pipe=new NamedPipeClientStream(".",path[prefix.Length..],PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Anonymous);
        try
        {
            await pipe.ConnectAsync(ct);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{kind="https",host=uri.IdnHost,port=uri.Port,method=method.Method,path=uri.PathAndQuery})+"\n"),ct);
            var bytes=new List<byte>();var one=new byte[1];
            while(bytes.Count<16384){if(await pipe.ReadAsync(one,ct)!=1)throw new IOException("Relay closed before approval.");if(one[0]==10)break;bytes.Add(one[0]);}
            if(bytes.Count>=16384)throw new IOException("Relay response exceeded bound.");
            using var reply=JsonDocument.Parse(bytes.ToArray());
            if(!reply.RootElement.TryGetProperty("ok",out var ok)||!ok.GetBoolean())throw new IOException(reply.RootElement.GetProperty("error").GetString());
            var response=new HttpResponseMessage((HttpStatusCode)reply.RootElement.GetProperty("status").GetInt32()){Content=new StreamContent(pipe),RequestMessage=new HttpRequestMessage(method,uri)};
            foreach(var header in reply.RootElement.GetProperty("headers").EnumerateObject())
            {
                var values=header.Value.EnumerateArray().Select(v=>v.GetString()!).ToArray();
                if(!response.Headers.TryAddWithoutValidation(header.Name,values))response.Content.Headers.TryAddWithoutValidation(header.Name,values);
            }
            return response;
        }
        catch{pipe.Dispose();throw;}
    }
}
