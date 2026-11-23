using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mason.Code;

// Servers and tool allowlists are configured in the user's Mason profile, outside project authority.
internal sealed record McpRegistration(string Name,string Endpoint,string[] Tools);
internal static class CodeMcp
{
    internal static McpRegistration[] Read(string root)
    {
        var path=Path.Combine(root,"mcp-servers.json");
        if(!File.Exists(path))return [];
        if(new FileInfo(path).Length>32768)throw new InvalidDataException("MCP configuration exceeds 32 KiB.");
        var servers=JsonSerializer.Deserialize<McpRegistration[]>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow});
        if(servers==null||servers.Length>16||servers.Select(s=>s.Name).Distinct().Count()!=servers.Length)throw new InvalidDataException("Invalid MCP registrations.");
        foreach(var s in servers){
            if(string.IsNullOrWhiteSpace(s.Name)||s.Tools is not {Length:>0 and <=64}||!Uri.TryCreate(s.Endpoint,UriKind.Absolute,out var uri)||
                uri.Scheme!="http"||!IPAddress.TryParse(uri.Host.Trim('[',']'),out var ip)||!IPAddress.IsLoopback(ip)||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)
                throw new InvalidDataException("Register MCP servers by literal HTTP loopback address and explicit tool names.");
        }
        return servers;
    }
    internal static async Task<string> Request(McpRegistration server,string? tool,JsonElement arguments,CancellationToken ct)
    {
        if(tool!=null&&!server.Tools.Contains(tool,StringComparer.Ordinal))throw new UnauthorizedAccessException("Tool is outside the registered allowlist.");
        using var handler=new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false};
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(20),MaxResponseContentBufferSize=128*1024};
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        string? session=null;
        async Task<JsonElement> Send(string method,object parameters,int? id)
        {
            var msg=new Dictionary<string,object>{{"jsonrpc","2.0"},{"method",method},{"params",parameters}};
            if(id.HasValue)msg["id"]=id.Value;
            using var request=new HttpRequestMessage(HttpMethod.Post,server.Endpoint){Content=new StringContent(JsonSerializer.Serialize(msg),Encoding.UTF8,"application/json")};
            if(session!=null)request.Headers.Add("Mcp-Session-Id",session);
            if(method!="initialize")request.Headers.Add("MCP-Protocol-Version","2025-06-18");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            response.EnsureSuccessStatusCode();
            if(method=="initialize"&&response.Headers.TryGetValues("Mcp-Session-Id",out var ids))session=ids.Single();
            if(!id.HasValue)return default;
            JsonElement? Match(string json){
                using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
                if(!root.TryGetProperty("id",out var got)||got.ValueKind!=JsonValueKind.Number||got.GetInt32()!=id.Value)return null;
                if(root.TryGetProperty("error",out var error))throw new InvalidDataException("MCP error: "+error);
                return root.GetProperty("result").Clone();
            }
            bool sse=response.Content.Headers.ContentType?.MediaType=="text/event-stream";
            using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
            using var reader=new StreamReader(stream,Encoding.UTF8);
            var buffer=new char[2048];var pending=new StringBuilder();int seen=0,n;
            while((n=await reader.ReadAsync(buffer,timeout.Token))>0){
                if((seen+=n)>128*1024)throw new InvalidDataException("MCP reply too large.");
                pending.Append(buffer,0,n);
                if(!sse)continue;
                var normalized=pending.ToString().Replace("\r\n","\n");
                int end;
                while((end=normalized.IndexOf("\n\n",StringComparison.Ordinal))>=0){
                    var json=string.Join("\n",normalized[..end].Split('\n').Where(l=>l.StartsWith("data:")).Select(l=>l[5..].TrimStart()));
                    if(json.Length>0 && Match(json) is {} hit)return hit;
                    normalized=normalized[(end+2)..];
                }
                pending.Clear().Append(normalized);
            }
            if(!sse && Match(pending.ToString()) is {} result)return result;
            throw new InvalidDataException("Missing MCP response for the requested operation.");
        }
        try{
            var hello=await Send("initialize",new{protocolVersion="2025-06-18",capabilities=new{},clientInfo=new{name="Mason Agent",version="2.0"}},1);
            if(hello.GetProperty("protocolVersion").GetString()!="2025-06-18")throw new InvalidDataException("Unsupported MCP protocol version.");
            await Send("notifications/initialized",new{},null);
            if(tool!=null){
                var result=await Send("tools/call",new{name=tool,arguments},2);
                if(result.TryGetProperty("isError",out var error)&&error.ValueKind==JsonValueKind.True)throw new InvalidDataException("MCP tool failed: "+result);
                return result.GetRawText();
            }
            var found=new List<JsonElement>();string? cursor=null;
            for(int page=0;page<16;page++){
                var listed=await Send("tools/list",cursor==null?(object)new{}:new{cursor},2+page);
                found.AddRange(listed.GetProperty("tools").EnumerateArray().Where(t=>server.Tools.Contains(t.GetProperty("name").GetString())));
                cursor=listed.TryGetProperty("nextCursor",out var token)?token.GetString():null;
                if(string.IsNullOrEmpty(cursor))return JsonSerializer.Serialize(found);
            }
            throw new InvalidDataException("MCP tool list exceeded page limit.");
        }finally{
            if(session!=null)try{using var end=new HttpRequestMessage(HttpMethod.Delete,server.Endpoint);end.Headers.Add("Mcp-Session-Id",session);end.Headers.Add("MCP-Protocol-Version","2025-06-18");using var closed=await client.SendAsync(end,ct);}catch(HttpRequestException){}
        }
    }
}
