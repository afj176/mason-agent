using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mason.Code;

internal static class McpIntegration
{
    public static async Task Run(Action<bool,string> check)
    {
        using var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();
        int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        var url=$"http://127.0.0.1:{port}/";
        using var mock=new HttpListener();mock.Prefixes.Add(url);mock.Start();
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int invocations=0;
        async Task Serve(){
            while(!cancel.IsCancellationRequested){
                HttpListenerContext exchange;
                try{exchange=await mock.GetContextAsync().WaitAsync(cancel.Token);}catch(OperationCanceledException){break;}
                var response=exchange.Response;
                if(exchange.Request.HttpMethod=="DELETE"){response.StatusCode=204;response.Close();continue;}
                var request=JsonSerializer.Deserialize<JsonElement>(await new StreamReader(exchange.Request.InputStream).ReadToEndAsync());
                string? method=request.GetProperty("method").GetString();
                if(method=="notifications/initialized"){response.StatusCode=202;response.Close();continue;}
                object result=method switch {
                    "initialize"=>new{protocolVersion="2025-06-18",capabilities=new{tools=new{}},serverInfo=new{name="fixture",version="1"}},
                    "tools/list"=>new{tools=new[]{new{name="approved"},new{name="private"}}},
                    "tools/call"=>new{content=new[]{new{type="text",text="pong"}}},
                    _=>throw new InvalidDataException("Unexpected fixture call")
                };
                if(method=="initialize")response.AddHeader("Mcp-Session-Id","test");
                else if(exchange.Request.Headers["Mcp-Session-Id"]!="test")throw new InvalidDataException("Missing negotiated session");
                bool sse=method=="tools/call";if(sse)invocations++;
                string json=JsonSerializer.Serialize(new{jsonrpc="2.0",id=request.GetProperty("id").GetInt32(),result});
                var body=Encoding.UTF8.GetBytes(sse?"event: message\ndata: "+json+"\n\n":json);
                response.ContentType=sse?"text/event-stream":"application/json";response.ContentLength64=body.Length;
                await response.OutputStream.WriteAsync(body);response.Close();
            }
        }
        var task=Serve();var entry=new McpRegistration("test",url,["approved"]);
        try{
            var listed=await CodeMcp.Request(entry,null,default,cancel.Token);
            check(listed.Contains("approved")&&!listed.Contains("private"),"MCP discovery exposes only registered tools");
            var reply=await CodeMcp.Request(entry,"approved",JsonSerializer.SerializeToElement(new{}),cancel.Token);
            check(reply.Contains("pong")&&invocations==1,"MCP negotiates a session and parses the SSE tool result");
            try{await CodeMcp.Request(entry,"private",default,cancel.Token);throw new Exception("Off-list tool accepted");}
            catch(UnauthorizedAccessException){check(invocations==1,"Off-list MCP tool rejected before dispatch");}
        }finally{cancel.Cancel();await task;}
    }
}
