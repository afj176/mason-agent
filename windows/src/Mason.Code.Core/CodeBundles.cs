using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason.Code;

public sealed record DocSource(string Name,string Version,string Base);
public sealed record HarnessBundle(string Name,string Version,Dictionary<string,string> Skills,McpRegistration[] Servers,DocSource[] Docs);
public sealed record InstalledBundle(HarnessBundle Package,bool Enabled);
public static class CodeBundles
{
    static readonly Regex Identifier=new(@"\A[a-z][a-z0-9-]{0,39}\z");
    static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    static string BundlePath(string root,string name)
    {
        if(!Identifier.IsMatch(name))throw new InvalidDataException("Use a lowercase package name.");
        return Path.Combine(root,"harness-bundles",name+".json");
    }
    public static HarnessBundle Inspect(string json)
    {
        if(json.Length>128000)throw new InvalidDataException("Extension exceeds 128,000 characters.");
        var p=JsonSerializer.Deserialize<HarnessBundle>(json,Json)??throw new InvalidDataException("Empty extension.");
        if(p.Name==null || !Identifier.IsMatch(p.Name) || !System.Version.TryParse(p.Version,out _) || p.Skills is not {Count:<=16} || p.Servers is not {Length:<=8} || p.Docs is not {Length:<=8})
            throw new InvalidDataException("Invalid extension identity, version or contents.");
        if(p.Skills.Any(k=>!Identifier.IsMatch(k.Key)||k.Value is null || k.Value.Length>32000))throw new InvalidDataException("Invalid extension skill.");
        foreach(var s in p.Servers){if(s==null || !Identifier.IsMatch(s.Name))throw new InvalidDataException("Invalid server name."); CodeMcp.Validate(s);}
        foreach(var d in p.Docs){
            if(d==null || !Identifier.IsMatch(d.Name) || string.IsNullOrWhiteSpace(d.Version) || d.Version.Length>80 || string.IsNullOrWhiteSpace(d.Base))throw new InvalidDataException("Invalid documentation registration.");
            if(Path.IsPathFullyQualified(d.Base)) {if(!Directory.Exists(d.Base))throw new InvalidDataException("Local documentation folder missing.");new CodeWorkspace(d.Base);}
            else {var u=new Uri(d.Base);if(u.Scheme!="https"||u.Port!=443||u.UserInfo.Length!=0||u.Query.Length!=0||u.Fragment.Length!=0||!u.AbsolutePath.EndsWith('/'))throw new InvalidDataException("Use an HTTPS documentation directory ending in /.");CodeNetworkPolicy.Host(u.IdnHost,u.Port);}
        }
        if(p.Servers.Select(s=>s.Name).Distinct().Count()!=p.Servers.Length||p.Docs.Select(d=>d.Name).Distinct().Count()!=p.Docs.Length)throw new InvalidDataException("Duplicate extension member.");
        return p;
    }
    public static InstalledBundle[] List(string root)
    {
        var folder=Path.Combine(root,"harness-bundles");if(!Directory.Exists(folder))return [];
        var files=Directory.GetFiles(folder,"*.json");
        if(files.Length>32)throw new InvalidDataException("Too many installed extensions.");
        return files.Select(f=>{
            using var stream=new FileStream(f,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            if(stream.Length>1024*1024)throw new InvalidDataException("Installed extension exceeds its limit.");
            return JsonSerializer.Deserialize<InstalledBundle>(stream,Json)??throw new InvalidDataException("Missing extension entry.");
        }).OrderBy(b=>b.Package.Name,StringComparer.Ordinal).ToArray();
    }
    public static void Save(string root,HarnessBundle bundle,bool enabled)
    {
        Inspect(JsonSerializer.Serialize(bundle));
        var path=BundlePath(root,bundle.Name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if(!File.Exists(path) && List(root).Length>=32)throw new InvalidOperationException("32 extensions are already installed.");
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(new InstalledBundle(bundle,enabled)));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public static void Remove(string root,string name)=>File.Delete(BundlePath(root,name));
    internal static IEnumerable<HarnessBundle> Enabled(string root)=>List(root).Where(b=>b.Enabled).Select(b=>b.Package);
    internal static Dictionary<string,DocSource> Sources(string root)=>Enabled(root).SelectMany(b=>b.Docs.Select(d=>(key:b.Name+"/"+d.Name,doc:d))).ToDictionary(x=>x.key,x=>x.doc);
    public static async Task<object> ReadDocs(DocSource source,string path,string query,Func<Uri,Task> approve,CancellationToken ct)
    {
        if(path.Length is 0 or >500 || path.Contains('\\') || path.StartsWith('/') || path.Contains(':') || path.Contains('%') ||
            path.Split('/').Any(p=>p is "" or ".." or ".") || path.Any(c=>char.IsControl(c)||c is '?' or '#'))throw new InvalidDataException("Use a relative documentation path.");
        string content,location;
        if(Path.IsPathFullyQualified(source.Base)){
            var docs=new CodeWorkspace(source.Base);
            if(Path.GetExtension(path).ToLowerInvariant() is not (".md" or ".txt" or ".html" or ".htm" or ".xml"))throw new InvalidDataException("Select a text documentation file.");
            content=docs.Read(path).Content;location=new Uri(docs.Resolve(path)).AbsoluteUri;
        }else{
            var origin=new Uri(source.Base);var uri=new Uri(origin,string.Join('/',path.Split('/').Select(Uri.EscapeDataString)));
            if(!origin.IsBaseOf(uri))throw new InvalidDataException("Outside the documentation directory.");
            await approve(uri);
            using var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false};
            handler.ConnectCallback=async(ctx,token)=>{
                var host=CodeNetworkPolicy.Host(ctx.DnsEndPoint.Host,ctx.DnsEndPoint.Port);
                var addresses=await Dns.GetHostAddressesAsync(host,token);
                if(addresses.Length==0 || addresses.Any(ip=>!CodeNetworkPolicy.Public(ip)))throw new IOException("Documentation requires a public HTTPS destination.");
                var socket=new Socket(SocketType.Stream,ProtocolType.Tcp);
                try{await socket.ConnectAsync(addresses,443,token);return new NetworkStream(socket,true);}catch{socket.Dispose();throw;}
            };
            using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(15)};
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentType?.MediaType is not ("text/plain" or "text/markdown" or "text/html" or "application/xml" or "text/xml"))
                throw new InvalidDataException("Unsupported documentation content type.");
            await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
            using var bytes=new MemoryStream();var buffer=new byte[8192];int n;
            while((n=await stream.ReadAsync(buffer,timeout.Token))!=0){if(bytes.Length+n>512*1024)throw new InvalidDataException("Documentation page exceeds 512 KiB.");bytes.Write(buffer,0,n);}
            content=new UTF8Encoding(false,true).GetString(bytes.ToArray());location=uri.AbsoluteUri;
        }
        if(query.Length is 0 or >200)throw new InvalidDataException("Enter a literal phrase to locate.");
        int at=content.IndexOf(query,StringComparison.OrdinalIgnoreCase);
        if(at<0)throw new InvalidDataException("Phrase absent from the requested documentation.");
        int start=Math.Max(0,at-400),length=Math.Min(1400,content.Length-start);
        return new{source=source.Name,version=source.Version,url=location,line=1+content.AsSpan(0,at).Count('\n'),
            sha256=CodeProtocol.Hash(Encoding.UTF8.GetBytes(content)),excerpt=content.Substring(start,length)};
    }
}
