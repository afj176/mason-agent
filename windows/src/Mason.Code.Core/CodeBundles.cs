using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason.Code;

public sealed record DocSource(string Name,string Version,string Base);
public sealed record HarnessBundle(string Name,string Version,Dictionary<string,string> Skills,McpRegistration[] Servers,DocSource[] Docs,string? Description=null);
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
        if(p.Skills.Any(k=>!CodeSkills.ValidName(k.Key)||k.Value is null || k.Value.Length>32000) || p.Description is {Length:>300})throw new InvalidDataException("Invalid extension skill or description.");
        // A server that arrives switched off is only a proposal; it is checked when the user adds and switches it on.
        foreach(var s in p.Servers){if(s==null || !Identifier.IsMatch(s.Name))throw new InvalidDataException("Invalid server name."); if(s.Enabled)CodeMcp.Validate(s);}
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
    public static void Remove(string root,string name)
    {
        File.Delete(BundlePath(root,name));
        var docs=Path.Combine(root,"bundle-docs",name);
        if(Directory.Exists(docs))Directory.Delete(docs,true);
    }

    /// <summary>
    /// Installs a bundle folder: bundle.json ({"name", "version", "description"}), skills/NAME/SKILL.md, docs/*.md and,
    /// optionally, mcp.json in the format of Mason's own MCP list. Only those text files are read; links are refused.
    /// The docs are copied into Mason's data folder. The bundle's MCP servers arrive switched off and are not used until
    /// the user adds them with /bundle servers NAME add and switches one on.
    /// </summary>
    public static HarnessBundle InstallFolder(string root,string folder)
    {
        var source=Path.GetFullPath(folder);
        static HarnessBundle Fail()=>throw new InvalidDataException("That folder has no valid bundle.json. It needs a name (lowercase letters, digits and -) and a version such as 1.0.");
        string Text(string relative,int limit)
        {
            var file=new FileInfo(Path.Combine(source,relative));
            if(!file.Exists||(file.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException(relative.Replace('\\','/')+" in the bundle is not an ordinary file.");
            if(file.Length>limit)throw new InvalidDataException(relative.Replace('\\','/')+" in the bundle is larger than "+limit/1024+" KB.");
            return File.ReadAllText(file.FullName);
        }
        if(!File.Exists(Path.Combine(source,"bundle.json")))return Fail();
        string name,version,description;
        try{
            using var manifest=JsonDocument.Parse(Text("bundle.json",64*1024));var m=manifest.RootElement;
            name=(m.TryGetProperty("name",out var n)&&n.ValueKind==JsonValueKind.String?n.GetString():null)?.ToLowerInvariant()??"";
            version=m.TryGetProperty("version",out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
            description=m.TryGetProperty("description",out var d)&&d.ValueKind==JsonValueKind.String?d.GetString()??"":"";
        }catch(JsonException){return Fail();}
        if(!Identifier.IsMatch(name)||!System.Version.TryParse(version,out _))return Fail();
        var skills=new Dictionary<string,string>(StringComparer.Ordinal);
        var skillRoot=Path.Combine(source,"skills");
        if(Directory.Exists(skillRoot))
            foreach(var dir in Directory.GetDirectories(skillRoot).Order(StringComparer.Ordinal))
            {
                var key=Path.GetFileName(dir);
                if(!CodeSkills.ValidName(key)||!File.Exists(Path.Combine(dir,"SKILL.md")))continue;
                if((new DirectoryInfo(dir).Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("skills/"+key+" in the bundle is a link.");
                skills[key]=Text(Path.Combine("skills",key,"SKILL.md"),32000);
            }
        var docs=Directory.Exists(Path.Combine(source,"docs"))?Directory.GetFiles(Path.Combine(source,"docs"),"*.md").Order(StringComparer.Ordinal).ToArray():[];
        if(skills.Count==0&&docs.Length==0)throw new InvalidDataException("The bundle "+name+" has no skills or docs.");
        if(skills.Count>16||docs.Length>200)throw new InvalidDataException("A bundle holds at most 16 skills and 200 documents.");
        McpRegistration[] servers=[];
        if(File.Exists(Path.Combine(source,"mcp.json")))
        {
            try{servers=JsonSerializer.Deserialize<McpRegistration[]>(Text("mcp.json",32768),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??[];}
            catch(JsonException){throw new InvalidDataException("The mcp.json of "+name+" is not a list of MCP servers.");}
            if(servers.Length>8)throw new InvalidDataException("The mcp.json of "+name+" lists more than 8 servers.");
            servers=servers.Select(s=>s with{Enabled=false}).ToArray();
        }
        var target=Path.Combine(root,"bundle-docs",name);
        var docSources=new List<DocSource>();
        if(docs.Length>0)
        {
            var staging=Path.Combine(root,"bundle-docs",".installing-"+Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);long total=0;
                foreach(var doc in docs)
                {
                    var text=Text(Path.Combine("docs",Path.GetFileName(doc)),1024*1024);
                    total+=text.Length;if(total>8*1024*1024)throw new InvalidDataException("A bundle's docs hold at most 8 MiB.");
                    File.WriteAllText(Path.Combine(staging,Path.GetFileName(doc)),text);
                }
                if(Directory.Exists(target))Directory.Delete(target,true);
                Directory.Move(staging,target);
            }
            finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
            docSources.Add(new DocSource("docs",version,target));
        }
        var bundle=new HarnessBundle(name,version,skills,servers,docSources.ToArray(),description.Length>300?description[..300]:description);
        Save(root,bundle,true);
        return bundle;
    }

    /// <summary>Adds a bundle's MCP servers to Mason's own list, switched off. Returns the names added.</summary>
    public static string[] AddServers(string root,HarnessBundle bundle)
    {
        var registered=CodeMcpRegistry.ReadAll(root);var added=new List<string>();
        foreach(var server in bundle.Servers.Where(s=>!registered.Any(r=>r.Name==s.Name))){registered.Add(server with{Enabled=false});added.Add(server.Name);}
        if(added.Count>0)CodeMcpRegistry.Save(root,registered);
        return added.ToArray();
    }

    public const string Usage = """
        /bundle                    The bundles installed on this computer
        /bundle install FOLDER     Install a bundle: its skills and docs become available in every project
        /bundle servers NAME       The MCP servers a bundle names
        /bundle servers NAME add   Add them to your MCP servers, switched off; switch one on in Settings once you have looked at it
        /bundle remove NAME        Remove one
        A bundle is a folder with bundle.json ({"name": ..., "version": ...}), skills/NAME/SKILL.md and docs/*.md.
        """;

    /// <summary>The /bundle command, shared by the command line and the app.</summary>
    public static string Command(string rest,string root)
    {
        var trimmed=rest.Trim();var verb=trimmed.Split(' ',2)[0].ToLowerInvariant();var argument=trimmed[verb.Length..].Trim();
        switch(verb)
        {
            case "":
                var installed=List(root);
                return installed.Length==0?"No bundles are installed.\n"+Usage:string.Join("\n",installed.Select(b=>b.Package.Name+" "+b.Package.Version+(b.Package.Description is {Length:>0} d?"  ·  "+d:"")+(b.Enabled?"":"  ·  off")));
            case "install":
            {
                if(argument.Length==0)throw new ArgumentException("Use /bundle install FOLDER.");
                var made=InstallFolder(root,argument.Trim('"'));
                int count=made.Skills.Count+(made.Docs.Length==0?0:Directory.GetFiles(made.Docs[0].Base,"*.md").Length);
                return "Installed "+made.Name+" "+made.Version+": "+count+" file"+(count==1?"":"s")+" of skills and docs."+
                    (made.Servers.Length==0?"":"\nIt names "+made.Servers.Length+" MCP server"+(made.Servers.Length==1?"":"s")+" ("+string.Join(", ",made.Servers.Select(s=>s.Name))+"). They are not in use: /bundle servers "+made.Name+" shows them.");
            }
            case "servers":
            {
                var words=argument.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                var bundle=words.Length==0?null:List(root).FirstOrDefault(b=>b.Package.Name.Equals(words[0],StringComparison.OrdinalIgnoreCase))?.Package;
                if(bundle==null)throw new ArgumentException("Use /bundle servers NAME, with the name of an installed bundle.");
                if(bundle.Servers.Length==0)return bundle.Name+" names no MCP servers.";
                if(words.Length>1&&words[1].Equals("add",StringComparison.OrdinalIgnoreCase))
                {
                    var added=AddServers(root,bundle);
                    return added.Length==0?"Those servers are already in your list.":"Added, switched off: "+string.Join(", ",added)+". Look at each and switch it on in Settings, under MCP servers.";
                }
                return string.Join("\n",bundle.Servers.Select(s=>s.Name+"  ·  "+(s.Executable??s.Endpoint)+"  ·  tools: "+(s.Tools.Length==0?"none listed":string.Join(", ",s.Tools))))+"\n/bundle servers "+bundle.Name+" add puts them in your list, switched off.";
            }
            case "remove":
                if(List(root).FirstOrDefault(b=>b.Package.Name.Equals(argument,StringComparison.OrdinalIgnoreCase)) is not {} found)throw new ArgumentException("No bundle named "+argument+" is installed.");
                Remove(root,found.Package.Name);
                return "Removed "+found.Package.Name+".";
            default:
                return Usage;
        }
    }
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
