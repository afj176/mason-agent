using System.Diagnostics;
using System.Text.Json;
using Mason.Code;

namespace Mason;

/// <summary>Roles make model proposals. The engine retains the workspace, permission and completion checks.</summary>
public sealed class RoleModels(CodeLocalModel implementer, CodeLocalModel reviewer, CodeLocalModel visual,
    string profileRoot, Action<string>? progress = null, string? traceDirectory = null, bool inspectBrowserFrames = false,Func<CodeLocalModel,CancellationToken,Task>? prepare = null,int roleLimit=64) :
    ICodeContextModel, ICodeAcceptancePlanner, ICodeCatalogPlanner, ICodeBrowserOperator, ICodeChangeReviewer, IReviewPrediction,
    ICodeInvestigator, ICodeVisualVerifier, IProjectProbeReviewer
{
    readonly Dictionary<string,int> calls=new(StringComparer.Ordinal);
    public bool Enabled => inspectBrowserFrames;
    public string VisualModel => visual.ModelIdentity;
    async Task<T> Invoke<T>(string role,CodeLocalModel model,Func<Task<T>> work,CancellationToken ct)
    {
        var file=Path.Combine(profileRoot,"inference","gpu0.lock"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        FileStream handle;
        progress?.Invoke("Waiting for "+role+" · "+model.ModelIdentity);
        while(true) {
            ct.ThrowIfCancellationRequested();
            try {handle=new FileStream(file,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);break;}
            catch(IOException e) when((e.HResult&65535) is 32 or 33){await Task.Delay(100,ct);}
        }
        using(handle) {
            var prior=LocalAiClient.TraceSink.Value;
            try {
                if(prepare!=null)await prepare(model,ct);
                if(role is "Reviewer" or "Browser verifier" or "Visual verifier"){
                    int count=calls.GetValueOrDefault(role)+1;
                    if(count>roleLimit)throw new InvalidOperationException(role+" exhausted its per-turn call budget of "+roleLimit+".");
                    calls[role]=count;
                }
                if(traceDirectory!=null)LocalAiClient.TraceSink.Value=(body,response,receipt)=>ModelTrace.Save(traceDirectory,role,model.ModelIdentity,body,response,receipt);
                progress?.Invoke(role+" · "+model.ModelIdentity);
                return await work();
            } finally {LocalAiClient.TraceSink.Value=prior;}
        }
    }
    public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> text,CancellationToken ct)=>
        Invoke("Implementer",implementer,()=>implementer.Next(messages,text,ct),ct);
    public Task<CodeContextUsage> Measure(IReadOnlyList<CodeMessage> messages,bool digest,CancellationToken ct)=>
        Invoke("Context",implementer,()=>implementer.Measure(messages,digest,ct),ct);
    public Task<CodeDigest> Summarize(IReadOnlyList<CodeMessage> messages,CancellationToken ct)=>
        Invoke("Context",implementer,()=>implementer.Summarize(messages,ct),ct);
    public Task<CatalogSpec> ExtractCatalog(string request,CancellationToken ct)=>
        Invoke("Implementer",implementer,()=>implementer.ExtractCatalog(request,ct),ct);
    public Task<bool> ArgumentsInterchangeable(string request,ExampleProbe example,bool sourceSymmetric,CancellationToken ct)=>
        Invoke("Reviewer",reviewer,()=>reviewer.ArgumentsInterchangeable(request,example,sourceSymmetric,ct),ct);
    public Task<List<AcceptanceCheck>> PlanAcceptance(string request,string[] units,CodeFiles inventory,CancellationToken ct)=>
        Invoke("Reviewer",reviewer,()=>reviewer.PlanAcceptance(request,units,inventory,ct),ct);
    public Task<ReviewFinding[]> ReviewChanges(string request,IReadOnlyList<ReviewSource> sources,CancellationToken ct)=>
        Invoke("Reviewer",reviewer,()=>reviewer.ReviewChanges(request,sources,ct),ct);
    public Task<ReviewPrediction> Predict(ReviewFinding finding,IReadOnlyDictionary<string,AcceptanceCheck> cases,CancellationToken ct)=>
        Invoke("Reviewer",reviewer,()=>reviewer.Predict(finding,cases,ct),ct);
    public Task<WorkerFinding[]> Investigate(string question,CodeFile[] files,CancellationToken ct)=>
        Invoke("Reviewer",reviewer,()=>reviewer.Investigate(question,files,ct),ct);
    public Task<ComputerAction> ChooseAction(string task,ComputerObservation observation,IReadOnlyList<string> history,CancellationToken ct)=>
        Invoke("Browser verifier",visual,()=>visual.ChooseAction(task,observation,history,ct),ct);
    public Task<BrowserScenario[]> DesignReview(string request,string path,ComputerObservation observation,CancellationToken ct)=>
        Invoke("Reviewer",reviewer,()=>reviewer.DesignReview(request,path,observation,ct),ct);
    public Task<PixelDecision> See(string task,DesktopFrame frame,bool observeOnly,CancellationToken ct)=>
        Invoke("Visual verifier",visual,()=>visual.See(task,frame,observeOnly,ct),ct);
}

public sealed partial class CodeLocalModel { public string ModelIdentity => profile.Model; internal Guid ProfileId=>profile.Id; internal void RouteTo(AiEndpointProfile next){if(next.Id!=profile.Id)throw new InvalidOperationException("Role identity changed.");profile=next;} }

public static class ModelTrace
{
    public sealed record Entry(string Role,string Model,string Request,string Response,string Receipt);
    public static void Save(string folder,string role,string model,string payload,string response,string receipt)
    {
        // A trace stays in the host profile. Retain bounded text decisions and exclude image-bearing requests.
        if(payload.Length>512000 || payload.Contains("data:image",StringComparison.OrdinalIgnoreCase))return;
        Directory.CreateDirectory(folder);
        string name=DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff")+"-"+Guid.NewGuid().ToString("N")[..8]+".json";
        string temp=Path.Combine(folder,name+".tmp");
        try {
            File.WriteAllText(temp,JsonSerializer.Serialize(new Entry(role,model,payload,response[..Math.Min(response.Length,100000)],receipt)));
            File.Move(temp,Path.Combine(folder,name));
            foreach(var old in Directory.GetFiles(folder,"*.json").Order(StringComparer.Ordinal).Reverse().Skip(12))File.Delete(old);
        } finally {if(File.Exists(temp))File.Delete(temp);}
    }
    public static async Task<object> Predict(string path,AiEndpointProfile profile,string key,CancellationToken ct)
    {
        if(new FileInfo(path).Length>4*1024*1024)throw new InvalidDataException("Decision trace exceeds the limit.");
        var entry=JsonSerializer.Deserialize<Entry>(File.ReadAllText(path))??throw new InvalidDataException("Empty trace.");
        var body=System.Text.Json.Nodes.JsonNode.Parse(entry.Request)?.AsObject()??throw new InvalidDataException("Invalid recorded request.");
        if(!body.ContainsKey("messages") || body["stream"]?.GetValue<bool>()!=true)throw new InvalidDataException("Expected a streaming chat trace.");
        body["model"]=profile.Model;
        using var client=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=TimeSpan.FromMinutes(8)};
        using var post=new HttpRequestMessage(HttpMethod.Post,new Uri(LocalAiClient.ValidateEndpoint(profile.Endpoint),"v1/chat/completions"));
        if(key.Length!=0)post.Headers.Authorization=new("Bearer",key);
        post.Content=new StringContent(body.ToJsonString(),System.Text.Encoding.UTF8,"application/json");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(8));
        var clock=Stopwatch.StartNew();
        using var reply=await client.SendAsync(post,HttpCompletionOption.ResponseHeadersRead,deadline.Token);reply.EnsureSuccessStatusCode();
        await using var stream=await reply.Content.ReadAsStreamAsync(deadline.Token);
        var text=new System.Text.StringBuilder();
        var result=await LocalAiClient.ReadChatStream(stream,s=>{if(text.Length+s.Length>100000)throw new InvalidDataException("Response too large.");text.Append(s);},deadline.Token,allowToolCalls:body.ContainsKey("tools"));
        if(result.ObservedModel!=null && result.ObservedModel!=profile.Model)throw new InvalidDataException("The comparison endpoint returned another model.");
        return new { original=entry.Model,entry.Role,originalResponse=entry.Response,originalReceipt=entry.Receipt, comparison=profile.Model,
            proposed=text.ToString(),result.ToolCalls,result.FinishReason,elapsedMs=clock.ElapsedMilliseconds,executedTools=0 };
    }
}
