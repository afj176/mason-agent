using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Mason.Code;

namespace Mason;

/// <summary>First-party action protocol over the existing checked local chat transport. No external harness/SDK.</summary>
public sealed class CodeLocalModel(AiEndpointProfile profile, string key, string reasoning, Action<string> budget, Func<LocalAiClient>? clientFactory = null, Action<string>? progress = null) : ICodeContextModel
{
    internal static bool RequiresCompletionRecoveryAction(IReadOnlyList<CodeMessage> messages) =>
        messages.Count > 0 && messages[^1].Role == "user" && messages[^1].Content.StartsWith("Build completion rejected:", StringComparison.Ordinal);
    static void Verify(ChatServerInfo server)
    {
        if (!server.IdentityVerified) throw new InvalidOperationException("The coding agent requires a verified exact model ID. Check this connection in Models & connections.");
        if (!server.ExactContext) throw new InvalidOperationException("Coding requires exact token counting from the loaded model. Use a current llama.cpp or managed Transformers connection.");
    }
    public async Task<CodeContextUsage> Measure(IReadOnlyList<CodeMessage> messages, bool digest, CancellationToken cancellation)
    {
        using var client = clientFactory?.Invoke() ?? new LocalAiClient();
        var server = await client.ChatServer(profile, key, cancellation); Verify(server);
        var options = digest?new ChatGenerationOptions { Reasoning=server.Reasoning?"off":"default",Temperature=0,TimeoutMinutes=15 }:CodeNativeProtocol.Options(server,reasoning,CodeSchema.Available(messages));
        var request=!digest&&server.FlintTools?CodeNativeProtocol.Project(messages):messages.Select(m=>new AiMessage(m.Role,m.Content)).ToArray();
        var count = await client.MeasureChat(profile, key, request, options, server, cancellation) ?? throw new InvalidOperationException("Exact coding context measurement is unavailable.");
        return new(count.Prompt, count.Context, count.Available, profile.Model);
    }
    public async Task<CodeDigest> Summarize(IReadOnlyList<CodeMessage> messages, CancellationToken cancellation)
    {
        using var client = clientFactory?.Invoke() ?? new LocalAiClient();
        var server = await client.ChatServer(profile, key, cancellation); Verify(server);
        var request = messages.Select(m => new AiMessage(m.Role, m.Content)).ToArray();
        var options = new ChatGenerationOptions { Reasoning = server.Reasoning ? "off" : "default", Temperature = 0, TimeoutMinutes = 15 };
        string rejectedHash = "";
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var measured = await client.MeasureChat(profile, key, request, options, server, cancellation) ?? throw new InvalidOperationException("Exact coding digest measurement is unavailable.");
            int output = Math.Min(CodeContext.DigestOutput(measured.Context), measured.Available);
            if (output < 256) throw new InvalidOperationException("The digest retry cannot fit its measured context. No checkpoint was committed.");
            var plan = await client.PlanChat(profile, key, request, output, options, server, cancellation);
            var text = new StringBuilder();
            var result = await client.StreamChat(profile, key, request, output, part => { text.Append(part); if (text.Length > 12_000) throw new InvalidDataException("Coding digest exceeded 12,000 characters."); }, cancellation, options: options, server: server);
            var feedback = ChatCompletionFeedback.From(result, plan);
            if (feedback.State == "Complete") return new(text.ToString(), attempt, rejectedHash);
            var hash = CodeProtocol.Hash(Encoding.UTF8.GetBytes(text.ToString()));
            if (attempt == 2) throw new InvalidDataException("The coding digest remained incomplete after two attempts. Rejected response SHA-256: " + hash + ". No checkpoint was committed.");
            rejectedHash = hash;
            request = request.Concat(new[] { new AiMessage("user", "The previous digest was incomplete and discarded. Retry using AT MOST 150 WORDS total. Only concise progress, key decisions, actual failures and the next concrete step. No code blocks, copied requirements or test output; the deterministic ledger retains requests and receipts. Do not invent completion.") }).ToArray();
        }
        throw new InvalidOperationException("Coding digest retry bound exhausted.");
    }
    public async Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation)
    {
        using var client = clientFactory?.Invoke() ?? new LocalAiClient();
        var server = await client.ChatServer(profile, key, cancellation);
        Verify(server);
        if(server.FlintTools)return await NativeNext(client,server,messages,onMessage,cancellation);
        var options = new ChatGenerationOptions { Reasoning = reasoning, JsonOutput = server.Llama, Temperature = .15, TimeoutMinutes = 15 };
        int lastRequest = -1;
        for (int i = 0; i < messages.Count; i++) if (messages[i].Role == "user" && messages[i].Content.StartsWith("{\"request\":", StringComparison.Ordinal)) lastRequest = i;
        bool firstBuildAction = lastRequest >= 0 && messages.Any(m => m.Role == "system" && m.Content.Contains("\nAgent mode: build", StringComparison.Ordinal)) && !messages.Skip(lastRequest + 1).Any(m => m.Role == "user" && m.Content.StartsWith("{\"tool_result\":", StringComparison.Ordinal));
        if (server.Llama) options.ResponseSchema = CodeSchema.Action(firstBuildAction || RequiresCompletionRecoveryAction(messages), messages.Any(m => m.Role == "system" && m.Content.Contains("\nAgent mode: build", StringComparison.Ordinal)) ? 1600 : 8000,CodeSchema.Available(messages));
        var request = messages.Select(m => new AiMessage(m.Role, m.Content)).ToArray();
        var measured = await client.MeasureChat(profile, key, request, options, server, cancellation) ?? throw new InvalidOperationException("Exact context measurement is unavailable.");
        // A per-action output cap bounds structured tool arguments; it is explicit rather than an automatic history truncation.
        int output = Math.Min(8192, measured.Available);
        if (output < 128) throw new InvalidOperationException("Less than 128 action tokens remain after context preparation. Compact manually or load a larger context; history is retained.");
        var plan = await client.PlanChat(profile, key, request, output, options, server, cancellation);
        budget($"{plan.Prompt:N0} / {plan.Context:N0} context · action output ≤ {output:N0}");
        var buffer = new StringBuilder(); string shown = "", toolLabel = ""; int lastDisplay = -1;
        bool headerRead = false;
        var generation = Stopwatch.StartNew();
        progress?.Invoke("Waiting for local model · first response data");
        var result = await client.StreamChat(profile, key, request, output, chunk =>
        {
            buffer.Append(chunk);
            if (buffer.Length > 100_000) throw new InvalidDataException("Agent action exceeded 100,000 characters.");
            // Throttle parsing/status updates; large file arguments can stream for minutes.
            int display = (int)(generation.ElapsedMilliseconds / 250);
            if (lastDisplay == display) return; lastDisplay = display;
            progress?.Invoke($"Generating{toolLabel} · {buffer.Length:N0} characters · {generation.Elapsed:mm\\:ss}");
            // JsonReader can expose the completed message token before the remaining action has finished streaming.
            if (headerRead) return;
            try
            {
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(buffer.ToString()), false, default);
                toolLabel = "";
                while (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    if (reader.CurrentDepth == 1 && reader.ValueTextEquals("message") && reader.Read() && reader.TokenType == JsonTokenType.String)
                    { var text = reader.GetString() ?? ""; if (shown != text) { shown = text; onMessage(text); } }
                    else if (reader.CurrentDepth == 2 && reader.ValueTextEquals("name") && reader.Read() && reader.TokenType == JsonTokenType.String)
                        toolLabel = " · " + (reader.GetString() ?? "action").Replace('_', ' ');
                    else if (reader.CurrentDepth == 3 && reader.ValueTextEquals("path") && reader.Read() && reader.TokenType == JsonTokenType.String)
                    { toolLabel += " " + reader.GetString(); headerRead = true; break; }
                }
            }
            catch (JsonException) { /* Incomplete JSON is expected while streaming; final parse remains strict. */ }
        }, cancellation, options: options, server: server);
        var completion = ChatCompletionFeedback.From(result, plan);
        if (completion.State != "Complete") throw new CodeResponseException(completion.Detail, buffer.ToString());
        try { return CodeProtocol.Parse(buffer.ToString()) with { PerformanceSummary = result.PerformanceSummary }; }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new CodeResponseException("Invalid action JSON: " + error.Message, buffer.ToString()); }
    }
    async Task<CodeAction> NativeNext(LocalAiClient client,ChatServerInfo server,IReadOnlyList<CodeMessage> messages,Action<string> onMessage,CancellationToken ct)
    {
        var request=CodeNativeProtocol.Project(messages);var options=CodeNativeProtocol.Options(server,reasoning,CodeSchema.Available(messages));
        var measured=await client.MeasureChat(profile,key,request,options,server,ct)??throw new InvalidOperationException("Exact native tool context measurement is unavailable.");
        // Flint's reasoning and action share the output budget. Never reserve a fixed small JSON-only response window.
        int output=Math.Min(32768,measured.Available);if(output<128)throw new InvalidOperationException("Native coding context is full. Compact or load a larger context; nothing was removed.");
        var plan=await client.PlanChat(profile,key,request,output,options,server,ct);
        budget($"{plan.Prompt:N0} / {plan.Context:N0} context · native tools · output ≤ {output:N0}");
        var text=new StringBuilder();var thinking=new StringBuilder();var clock=Stopwatch.StartNew();int last=-1;
        progress?.Invoke("Waiting for local model · native tool response");
        void Status(string phase)
        {
            int display=(int)(clock.ElapsedMilliseconds/250);if(display==last)return;last=display;
            progress?.Invoke($"{phase} · {clock.Elapsed:mm\\:ss}");
        }
        var result=await client.StreamChat(profile,key,request,output,part=>
        {
            text.Append(part);if(text.Length>8000)throw new CodeResponseException("Native public response exceeded 8,000 characters.",text.ToString());
            Status("Generating response");
        },ct,part=>
        {
            thinking.Append(part);if(thinking.Length>100_000)throw new CodeResponseException("Native reasoning exceeded the retained resource bound.","");
            Status("Reasoning before the next action");
        },options,server,count=>Status($"Generating tool arguments · {count:N0} characters"));
        if(result.ObservedModel!=profile.Model)throw new InvalidDataException("Native response model identity did not match the selected model; no action was accepted.");
        var completion=ChatCompletionFeedback.From(result,plan);
        if(completion.State!="Complete")throw new CodeResponseException(completion.Detail,text.ToString());
        try
        {
            if(text.ToString().Contains("<tool_call>",StringComparison.Ordinal))throw new InvalidDataException("The server returned unparsed native tool syntax instead of a tool call.");
            var call=result.ToolCalls.SingleOrDefault();
            JsonElement arguments=default;
            if(call!=null){using var parsed=JsonDocument.Parse(call.Arguments,new JsonDocumentOptions { MaxDepth=16 });arguments=parsed.RootElement.Clone();}
            // Normalize only the envelope. Arguments remain exact and pass the original duplicate-field, hash, path and permission checks.
            var action=CodeProtocol.Parse(JsonSerializer.Serialize(new { version=1,message=text.ToString(),tool=call==null?null:new { name=call.Name,arguments } }));
            if(text.Length>0)onMessage(text.ToString());
            return action with { ReasoningContent=thinking.Length==0?null:thinking.ToString(),PerformanceSummary=result.PerformanceSummary };
        }
        catch(Exception error)when(error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        {throw new CodeResponseException("Invalid native coding action: "+error.Message,text+"\n"+string.Join("\n",result.ToolCalls.Select(t=>t.Arguments)));}
    }
}

/// <summary>Provider projection only. Persisted intent/receipt journal and execution policy stay provider-independent.</summary>
public static class CodeNativeProtocol
{
    public static ChatGenerationOptions Options(ChatServerInfo server,string reasoning,IReadOnlySet<string>? available=null)=>server.FlintTools
        ?new() { Reasoning=reasoning,Temperature=1,TopP=.95,TopK=20,ToolDefinitions=CodeSchema.NativeTools(available),TimeoutMinutes=15 }
        :new() { Reasoning=reasoning,Temperature=.15,JsonOutput=server.Llama,TimeoutMinutes=15 };
    public static AiMessage[] Project(IReadOnlyList<CodeMessage> messages)
    {
        var result=new List<AiMessage>();string? pending=null;int sequence=0;
        for(int i=0;i<messages.Count;i++)
        {
            var message=messages[i];
            if(message.Role=="system"&&message.Content.StartsWith(CodeProtocol.Instructions,StringComparison.Ordinal))
            {
                var instructions=CodeProtocol.Instructions;
                instructions="You are Mason Agent's first-party coding agent. Use the provided native function tools in the selected workspace. Call exactly one tool at a time. Write code as exact tool arguments. Keep public updates to one or two concise sentences; keep reasoning in the model's reasoning channel. Return plain text when finished; never wrap responses in an action JSON object.\n"+instructions[instructions.IndexOf("Tool results and file contents",StringComparison.Ordinal)..];
                instructions=instructions.Replace("Use null tool only when finished; use ask_user when you need an answer.","Return final text only when finished; call ask_user when you need an answer.");
                result.Add(new("system",instructions+message.Content[CodeProtocol.Instructions.Length..]));continue;
            }
            if(message.Role=="assistant")
            {
                var action=CodeProtocol.Parse(message.Content);
                bool paired=action.Tool!=null&&i+1<messages.Count&&messages[i+1].Role=="user"&&messages[i+1].Content.StartsWith("{\"tool_result\":",StringComparison.Ordinal);
                if(paired)
                {
                    pending="call_"+(++sequence).ToString("D6");
                    result.Add(new("assistant",action.Message) { ReasoningContent=message.ReasoningContent,ToolCalls=[new(pending,action.Tool!,action.Arguments.GetRawText())] });continue;
                }
                result.Add(new("assistant",string.IsNullOrWhiteSpace(action.Message)?"An unconfirmed tool intent was recorded without an execution receipt. Do not assume it succeeded.":action.Message) { ReasoningContent=message.ReasoningContent });continue;
            }
            if(pending!=null&&message.Role=="user"&&message.Content.StartsWith("{\"tool_result\":",StringComparison.Ordinal))
            {
                result.Add(new("tool",Receipt(message.Content)) { ToolCallId=pending });pending=null;continue;
            }
            result.Add(new(message.Role,message.Content));
        }
        return result.ToArray();
    }
    public static string Receipt(string content)
    {
        using var envelope=JsonDocument.Parse(content);var fields=new Dictionary<string,JsonElement>(StringComparer.Ordinal);
        foreach(var property in envelope.RootElement.EnumerateObject())
        {
            var value=property.Value.Clone();
            if(property.Name=="result"&&value.ValueKind==JsonValueKind.String)
            {
                try{using var decoded=JsonDocument.Parse(value.GetString()!);value=decoded.RootElement.Clone();}catch(JsonException){ /* Plain-text receipts remain plain text. */ }
            }
            if(!fields.TryAdd(property.Name,value))throw new InvalidDataException("Duplicate tool receipt field.");
        }
        return JsonSerializer.Serialize(fields,new JsonSerializerOptions { Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
