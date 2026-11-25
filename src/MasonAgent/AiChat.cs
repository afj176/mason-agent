using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mason;

public sealed record AiToolCall(string Id, string Name, string Arguments);
public sealed record AiMessage(string Role, string Content)
{
    [System.Text.Json.Serialization.JsonIgnore] public string? Jpeg {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; init; }
}
public sealed record AiChatResult(string FinishReason, int Characters, string? ObservedModel, int ReasoningCharacters = 0, int? PromptTokens = null, int? CompletionTokens = null, double? GenerationTokensPerSecond = null, double? TimeToFirstTokenSeconds = null, double? RequestSeconds = null)
{
    /// <summary>The tool call was recovered from text the model wrote instead of its native tool format.</summary>
    public bool RecoveredToolCall { get; init; }
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = [];
    public string PerformanceSummary => (GenerationTokensPerSecond is double speed ? $"{speed:0.0} tok/s generation" : CompletionTokens is int tokens && RequestSeconds is > 0 ? $"{tokens / RequestSeconds.Value:0.0} tok/s request average" : "Speed unavailable") +
        (TimeToFirstTokenSeconds is double first ? $" · first token {first:0.00}s" : "") +
        (CompletionTokens is int output ? $" · {output:N0} output tokens" : "");
}
public sealed class ChatNoFinalAnswerException() : IOException(ChatCompletionFeedback.NoFinalAnswerDetail);

/// <summary>One request contract shared by native Chat and the other chat callers. Null sampling values use server defaults.</summary>
public sealed class ChatGenerationOptions
{
    public string Reasoning { get; set; } = "default";
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public double? MinP { get; set; }
    public double? TypicalP { get; set; }
    public double? RepeatPenalty { get; set; }
    public double? PresencePenalty { get; set; }
    public double? FrequencyPenalty { get; set; }
    public int? Seed { get; set; }
    public string[] Stop { get; set; } = [];
    public bool JsonOutput { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public JsonElement? ToolDefinitions { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public JsonElement? ResponseSchema { get; set; }
    public bool ParseReasoning { get; set; } = true;
    public int TimeoutMinutes { get; set; } = 15;
    public int AnswerReservePercent { get; set; } = 25;
    // Resolved for one request, never saved as a user preference.
    [System.Text.Json.Serialization.JsonIgnore]
    public int? EffectiveReasoningBudget { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int? ReasoningBudget => Reasoning switch { "off" => 0, "low" => 512, "medium" => 2048, "high" => 8192, _ => null };
    public void Validate()
    {
        if (Reasoning is not ("default" or "off" or "low" or "medium" or "high" or "max")) throw new ArgumentException("Choose a supported reasoning level.");
        static void Range(double? n, double low, double high, string name) { if (n.HasValue && (!double.IsFinite(n.Value) || n < low || n > high)) throw new ArgumentException($"{name} must be between {low} and {high}."); }
        Range(Temperature, 0, 2, "Temperature"); Range(TopP, 0, 1, "Top P"); Range(MinP, 0, 1, "Min P"); Range(TypicalP, 0, 1, "Typical P");
        Range(RepeatPenalty, 0, 2, "Repeat penalty"); Range(PresencePenalty, -2, 2, "Presence penalty"); Range(FrequencyPenalty, -2, 2, "Frequency penalty");
        if (TopK < 0 || Seed < -1 || TimeoutMinutes is < 1 or > 120) throw new ArgumentException("Check top K, seed and timeout settings.");
        if (AnswerReservePercent is < 1 or > 100 || EffectiveReasoningBudget < 0) throw new ArgumentException("Reserve between 1% and 100% of output for the answer.");
        if (Stop is null || Stop.Length > 8 || Stop.Any(s => string.IsNullOrEmpty(s) || s.Length > 256)) throw new ArgumentException("Use up to eight nonempty stop sequences, at most 256 characters each.");
        if (ResponseSchema is { } schema && (schema.ValueKind != JsonValueKind.Object || schema.GetRawText().Length > 32000)) throw new ArgumentException("Use an object response schema up to 32,000 characters.");
        if(ToolDefinitions is { } tools && (tools.ValueKind!=JsonValueKind.Array||tools.GetArrayLength() is <1 or >64||tools.GetRawText().Length>32000||JsonOutput||ResponseSchema.HasValue))throw new ArgumentException("Use 1–64 native tool definitions up to 32,000 characters, without a response format.");
    }
    public Dictionary<string, object> Body(AiEndpointProfile profile, IReadOnlyList<AiMessage> messages, int? maxTokens, bool llama, bool restrictedTextAdapter = false)
    {
        Validate();
        if (ResponseSchema.HasValue && !llama) throw new ArgumentException("Schema-constrained output requires a verified llama.cpp connection.");
        if(ToolDefinitions.HasValue&&(!llama||restrictedTextAdapter))throw new ArgumentException("Native coding tools require a verified llama.cpp connection.");
        if(!ToolDefinitions.HasValue&&messages.Any(m=>m.Role=="tool"||m.ToolCalls is { Count:>0 }))throw new ArgumentException("Native tool history requires explicit tool definitions; ordinary Chat cannot enable tool calling through its history.");
        if (maxTokens < 1) throw new ArgumentException("Maximum output tokens must be a positive whole number.");
        if(restrictedTextAdapter&&(TopP.HasValue||TopK.HasValue||MinP.HasValue||TypicalP.HasValue||RepeatPenalty.HasValue||PresencePenalty.HasValue||FrequencyPenalty.HasValue||Seed.HasValue||Stop.Length>0||JsonOutput))
            throw new ArgumentException("This Transformers text adapter supports temperature and output tokens. Reset unsupported generation options to defaults or select llama.cpp for advanced controls.");
        if (!llama && (Reasoning != "default" || TopK.HasValue || MinP.HasValue || TypicalP.HasValue || RepeatPenalty.HasValue || !ParseReasoning))
            throw new ArgumentException("This connection has not reported llama.cpp controls. Reset llama.cpp-specific options to model defaults or select a llama.cpp connection.");
        var body = new Dictionary<string, object> { ["model"] = profile.Model, ["messages"] = messages, ["stream"] = true };
        if (maxTokens.HasValue) body["max_tokens"] = maxTokens.Value;
        // AiMessage is also used in persisted/domain data; the wire uses OpenAI's lowercase fields.
        body["messages"] = messages.Select(m =>
        {
            var message=new Dictionary<string,object?> { ["role"]=m.Role,["content"]=m.Content };
            if(m.Jpeg!=null)message["content"]=new object[]{new{type="text",text=m.Content},new{type="image_url",image_url=new{url="data:image/jpeg;base64,"+m.Jpeg}}};
            if(m.ReasoningContent!=null)message["reasoning_content"]=m.ReasoningContent;
            if(m.ToolCallId!=null)message["tool_call_id"]=m.ToolCallId;
            if(m.ToolCalls is { Count:>0 })message["tool_calls"]=m.ToolCalls.Select(t=>new { id=t.Id,type="function",function=new { name=t.Name,arguments=t.Arguments } }).ToArray();
            return message;
        }).ToArray();
        if(ToolDefinitions.HasValue){body["tools"]=ToolDefinitions.Value;body["tool_choice"]="auto";body["parallel_tool_calls"]=false;}
        void Put(string key, object? value) { if (value != null) body[key] = value; }
        Put("temperature", Temperature); Put("top_p", TopP); Put("top_k", TopK); Put("min_p", MinP); Put("typical_p", TypicalP);
        Put("repeat_penalty", RepeatPenalty); Put("presence_penalty", PresencePenalty); Put("frequency_penalty", FrequencyPenalty); Put("seed", Seed);
        if (Stop.Length > 0) body["stop"] = Stop;
        if (ResponseSchema.HasValue) body["response_format"] = new { type = "json_object", schema = ResponseSchema.Value };
        else if (JsonOutput) body["response_format"] = new { type = "json_object" };
        if (llama)
        {
            body["stream_options"] = new { include_usage = true };
            body["reasoning_format"] = ParseReasoning ? "auto" : "none";
            if (Reasoning != "default") body["chat_template_kwargs"] = new { enable_thinking = Reasoning != "off" };
            if (Reasoning == "off") body["reasoning_effort"] = "none";
            if ((EffectiveReasoningBudget ?? ReasoningBudget) is int budget) body["thinking_budget_tokens"] = budget;
        }
        return body;
    }
}

/// <summary>Output is shared, not partitioned: unused thinking tokens remain available to the answer.</summary>
public sealed record ChatBudgetPlan(int? Output, int? Reasoning, int? AnswerReserve, int? Prompt, int? Context, bool Automatic, bool ReasoningAdjusted, int? DecoderContext = null)
{
    public string Summary => Prompt is int prompt && Context is int context
        ? $"{prompt:N0} prompt · up to {Output:N0} output ({(Automatic ? "automatic" : "custom")})" +
          (Reasoning is int thinking ? $" · reasoning ≤ {thinking:N0} · answer headroom {AnswerReserve:N0}" : "") +
          $" · {context:N0} loaded context" + (DecoderContext is int decoder ? $" · separate {decoder:N0}-token output window" : "") + (ReasoningAdjusted ? " · reasoning reduced to leave answer space" : "")
        : $"Output: {(Output is int output ? $"{output:N0} tokens" : "Automatic · server managed")}. Exact prompt usage and automatic reasoning coordination are unavailable on this connection.";

    public static ChatBudgetPlan Resolve(int? requested, ChatGenerationOptions options, bool reasoning, int? prompt = null, int? context = null, int? decoderContext = null)
    {
        options.Validate();
        if (requested < 1) throw new ArgumentException("Use Automatic or a positive whole output-token limit.");
        int? output = requested;
        if (prompt.HasValue && context.HasValue)
        {
            int available = decoderContext ?? Math.Max(0, context.Value - prompt.Value);
            if (available == 0) throw new ArgumentException($"The prompt uses {prompt:N0} of {context:N0} loaded tokens, leaving no room for an answer. Shorten the conversation or load a larger context. Nothing was removed.");
            output ??= available;
            new ChatTokenBudget(prompt.Value, context.Value, output.Value, decoderContext).Validate();
        }
        int? thinking = null, reserve = null; bool adjusted = false;
        if (reasoning && output is int total)
        {
            reserve = (int)Math.Max(1, ((long)total * options.AnswerReservePercent + 99) / 100);
            int maximum = total - reserve.Value;
            thinking = Math.Min(options.ReasoningBudget ?? maximum, maximum);
            adjusted = options.ReasoningBudget is int selected && selected > maximum;
        }
        return new(output, thinking, reserve, prompt, context, requested == null, adjusted, decoderContext);
    }
}

public sealed record ChatCompletionFeedback(string State, string Detail)
{
    public const string NoFinalAnswerDetail = "The model stopped after reasoning without returning an answer. Reduce reasoning or choose Off, then Retry. This was not a connection failure.";
    public static ChatCompletionFeedback From(AiChatResult result, ChatBudgetPlan plan)
    {
        var limit = result.CompletionTokens is int actual ? $" after {actual:N0} generated tokens" : "";
        var recovery = plan.Automatic ? (plan.DecoderContext.HasValue ? "The separate decoder output window was exhausted. Load a larger output window or Retry with a shorter requested answer; summarizing input does not enlarge decoder capacity." : "Shorten the conversation or load a larger context, or reduce reasoning and Retry.") : "Choose Automatic output or increase your custom output limit, then Retry.";
        if (result.FinishReason == "length")
            return new(result.Characters == 0 && result.ReasoningCharacters > 0 ? "Reasoning exhausted output budget" : "Output limit reached",
                (result.Characters == 0 ? "The server reached its generation limit before returning a final answer" : "The server reached its generation limit; the answer is incomplete") + limit + ". " + recovery +
                (result.Characters == 0 && plan.Reasoning.HasValue ? " The requested answer headroom was not realized by this model/runtime." : ""));
        if (result.Characters == 0 && result.ToolCalls.Count == 0)
            return new("No final answer", NoFinalAnswerDetail);
        return new("Complete", "");
    }
}

public sealed record ChatServerInfo(bool Llama, bool IdentityVerified, int? Context, int? ModelCapacity, bool Reasoning, string Detail, bool RestrictedTextAdapter = false, bool ManagedTokenCount = false, int? DecoderContext = null)
{
    public bool FlintTools { get; init; }
    public bool Vision {get;init;}
    public bool ExactContext => Llama || ManagedTokenCount;
}
public sealed record ChatTokenBudget(int Prompt, int Context, int Output, int? DecoderContext = null)
{
    public int Available => DecoderContext ?? Math.Max(0, Context - Prompt);
    public bool Fits(int output) => Prompt <= Context && output > 0 && output <= Available;
    public void Validate()
    {
        if (Fits(Output)) return;
        if (DecoderContext is int decoder) throw new ArgumentException($"Prompt uses {Prompt:N0} of {Context:N0} input tokens; the separate decoder allows {decoder:N0} output tokens. The prompt must fit the input window and the requested {Output:N0} output must fit the decoder window. Shorten the conversation or choose Automatic output. Nothing was removed.");
        throw new ArgumentException($"Prompt uses {Prompt:N0} of {Context:N0} loaded context tokens, leaving {Available:N0} for reasoning and the answer. Lower maximum output tokens from {Output:N0}, shorten the conversation, or restart the model with a larger context window. Nothing was removed.");
    }
}

public sealed partial class LocalAiClient
{
    internal static readonly AsyncLocal<Action<string,string,string>?> TraceSink = new();
    public const int ChatTextLimit = 4 * 1024 * 1024;
    public static void ValidateChatMessages(IReadOnlyList<AiMessage> messages)
    {
        if (messages.Count is < 1 or > 400 || messages.Any(x => x.Role is not ("system" or "user" or "assistant" or "tool") ||
            string.IsNullOrWhiteSpace(x.Content)&&!(x.Role=="assistant"&&x.ToolCalls is { Count:>0 }) ||
            x.ToolCalls is { Count:>1 } || x.ToolCalls!=null&&x.Role!="assistant" ||
            x.ToolCalls?.Any(t=>string.IsNullOrWhiteSpace(t.Id)||t.Id.Length>128||string.IsNullOrWhiteSpace(t.Name)||t.Name.Length>64||t.Arguments.Length>100_000)==true ||
            x.Role=="tool"&&string.IsNullOrWhiteSpace(x.ToolCallId) || x.Role!="tool"&&x.ToolCallId!=null ||
            x.ReasoningContent!=null&&x.Role!="assistant") ||
            messages.Sum(x => (long)x.Content.Length+(x.ReasoningContent?.Length??0)+(x.ToolCalls?.Sum(t=>(long)t.Id.Length+t.Name.Length+t.Arguments.Length)??0)) > ChatTextLimit)
            throw new ArgumentException("The request exceeds the application's 400-message / 4 Mi-character resource bound or contains an invalid message. Nothing is silently removed. The model's token context is checked separately when available.");
    }

    async Task<JsonDocument?> ChatJson(AiEndpointProfile profile, string key, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(ValidateEndpoint(profile.Endpoint), path));
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var chunk = new byte[8192];
        int read; while ((read = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + read > 32 * 1024 * 1024) throw new InvalidDataException("Server metadata or tokenization exceeded 32 MiB."); buffer.Write(chunk, 0, read); }
        return JsonDocument.Parse(buffer.ToArray());
    }
    public async Task<ChatServerInfo> ChatServer(AiEndpointProfile profile, string key, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var models = await ChatJson(profile, key, "v1/models", null, deadline.Token);
        JsonElement model = default;
        bool verified = models != null && models.RootElement.TryGetProperty("data", out var list) && (model = list.EnumerateArray().FirstOrDefault(m => m.TryGetProperty("id", out var id) && id.GetString() == profile.Model)).ValueKind == JsonValueKind.Object;
        if (models != null && !verified) throw new InvalidOperationException("This server no longer lists the saved model ID. Start this model in Models or choose its current connection; a different model will not be substituted.");
        static int? Positive(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var number) && number > 0 ? number : null;
        var meta = model.ValueKind == JsonValueKind.Object && model.TryGetProperty("meta", out var metadata) ? metadata : default;
        bool textAdapter=model.ValueKind==JsonValueKind.Object&&model.TryGetProperty("owned_by",out var owner)&&owner.GetString()=="mason-transformers";
        using var props = await ChatJson(profile, key, "props", null, deadline.Token);
        if (props == null)
        {
            bool counter = textAdapter && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("chat_token_count", out var protocol) && protocol.ValueKind == JsonValueKind.String && protocol.GetString() == "v1";
            var decoder = Positive(meta, "n_ctx_output");
            if (counter && (Positive(meta, "n_ctx") == null || !meta.TryGetProperty("n_ctx_output", out var window) || window.ValueKind != JsonValueKind.Null && decoder == null))
                throw new InvalidDataException("The Transformers server reported invalid context windows. Restart the model before sending.");
            return new(false, verified, Positive(meta, "n_ctx"), Positive(meta, "n_ctx_train"), false,
                textAdapter ? "Transformers text adapter · temperature and output tokens; " + (counter ? "exact context counting and automatic compaction available." : "restart this managed model to enable exact context counting and automatic compaction.")
                : "Server context/control discovery unavailable; standard chat options remain available.", textAdapter, counter, counter ? decoder : null);
        }
        var root = props.RootElement;
        bool llama = root.TryGetProperty("build_info", out _) && root.TryGetProperty("chat_template", out var template);
        var text = llama ? root.GetProperty("chat_template").GetString() ?? "" : "";
        bool reasoning = text.Contains("enable_thinking", StringComparison.Ordinal) || text.Contains("reasoning_effort", StringComparison.Ordinal) || text.Contains("thinking_budget", StringComparison.Ordinal) || (text.Contains("<think>") && text.Contains("</think>")) || text.Contains("<|channel>thought") || text.Contains("<|think|>") || text.Contains("<seed:think|>");
        var context = root.TryGetProperty("default_generation_settings", out var settings) ? Positive(settings, "n_ctx") : null;
        return new(llama, verified, context ?? Positive(meta, "n_ctx"), Positive(meta, "n_ctx_train"), reasoning, llama ? "llama.cpp · model identity checked; reasoning support detected from the chat template." : "Standard chat connection; llama.cpp controls unavailable.")
        { Vision=root.TryGetProperty("modalities",out var modalities) && modalities.ValueKind==JsonValueKind.Object && modalities.TryGetProperty("vision",out var vision) && vision.ValueKind==JsonValueKind.True,
          FlintTools=llama&&text.Contains("macro flint_text",StringComparison.Ordinal)&&text.Contains("<arg_key>",StringComparison.Ordinal)&&text.Contains("tool_calls",StringComparison.Ordinal) };
    }
    public async Task<ChatTokenBudget?> TokenBudget(AiEndpointProfile profile, string key, IReadOnlyList<AiMessage> messages, int output, ChatGenerationOptions options, ChatServerInfo server, CancellationToken ct)
    {
        var measured = await MeasureChat(profile, key, messages, options, server, ct);
        if (measured == null) return null;
        var budget = measured with { Output = output }; budget.Validate(); return budget;
    }
    public async Task<ChatTokenBudget?> MeasureChat(AiEndpointProfile profile, string key, IReadOnlyList<AiMessage> messages, ChatGenerationOptions options, ChatServerInfo server, CancellationToken ct, bool allowEmptyDraft = false)
    {
        if (!server.ExactContext) return null;
        ValidateChatMessages(allowEmptyDraft && messages.Count > 0 && messages[^1] is { Role: "user", Content.Length: 0 }
            ? messages.Select((m, i) => i == messages.Count - 1 ? m with { Content = "(empty draft)" } : m).ToArray() : messages);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var body = options.Body(profile, messages, 1, server.Llama, server.RestrictedTextAdapter);
        if (server.ManagedTokenCount)
        {
            using var counted = await ChatJson(profile, key, "v1/chat/token-count", new { model = profile.Model, messages = body["messages"] }, deadline.Token);
            if (counted == null) throw new InvalidOperationException("The Transformers token counter is unavailable. Restart this managed model; no approximate count is substituted.");
            var value = counted.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("model", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != profile.Model ||
                !value.TryGetProperty("prompt_tokens", out var promptCount) || promptCount.ValueKind != JsonValueKind.Number || !promptCount.TryGetInt32(out int count) || count < 0 ||
                !value.TryGetProperty("context_tokens", out var inputWindow) || inputWindow.ValueKind != JsonValueKind.Number || !inputWindow.TryGetInt32(out int inputContext) || inputContext != server.Context ||
                !value.TryGetProperty("output_context_tokens", out var outputWindow) ||
                (server.DecoderContext.HasValue ? outputWindow.ValueKind != JsonValueKind.Number || !outputWindow.TryGetInt32(out int decoder) || decoder != server.DecoderContext : outputWindow.ValueKind != JsonValueKind.Null))
                throw new InvalidDataException("The token counter no longer matches the selected model or its context windows. Refresh the connection before sending.");
            return new(count, inputContext, 1, server.DecoderContext);
        }
        using var applied = await ChatJson(profile, key, "apply-template", body, deadline.Token);
        if (applied == null) throw new InvalidOperationException("This llama.cpp server cannot apply its chat template for token budgeting. Update the runtime before sending; no approximate token count is substituted.");
        var prompt = applied.RootElement.GetProperty("prompt").GetString()!;
        using var tokens = await ChatJson(profile, key, "tokenize", new { content = prompt, add_special = false, parse_special = true }, deadline.Token);
        if (tokens == null) throw new InvalidOperationException("This server cannot tokenize its prompt. Update the llama.cpp runtime.");
        var context = server.Context ?? throw new InvalidOperationException("The server did not report its loaded context window.");
        return new(tokens.RootElement.GetProperty("tokens").GetArrayLength(), context, 1);
    }
    public async Task<ChatBudgetPlan> PlanChat(AiEndpointProfile profile, string key, IReadOnlyList<AiMessage> messages, int? output, ChatGenerationOptions options, ChatServerInfo server, CancellationToken ct)
    {
        ValidateChatMessages(messages); options.Validate();
        if (options.Reasoning != "default" && !server.Reasoning) throw new ArgumentException("This model has not reported reasoning controls. Choose Default or a model with reasoning support.");
        // Thinking budgets are sampling controls, not template inputs. Count with the same template kwargs as generation.
        var counted = await TokenBudget(profile, key, messages, 1, options, server, ct);
        var plan = ChatBudgetPlan.Resolve(output, options, server.Llama && server.Reasoning, counted?.Prompt, counted?.Context, counted?.DecoderContext);
        options.EffectiveReasoningBudget = plan.Reasoning;
        _ = options.Body(profile, messages, plan.Output, server.Llama, server.RestrictedTextAdapter);
        return plan;
    }
    public async Task<AiChatResult> StreamChat(AiEndpointProfile profile, string key, IReadOnlyList<AiMessage> messages,
        int? maxTokens, Action<string> onText, CancellationToken cancellation, Action<string>? onReasoning = null, ChatGenerationOptions? options = null, ChatServerInfo? server = null,Action<int>? onToolArguments=null)
    {
        var endpoint = ValidateEndpoint(profile.Endpoint);
        if (string.IsNullOrWhiteSpace(profile.Model)) throw new ArgumentException("Select and save an exact model ID.");
        options ??= new(); options.Validate(); ValidateChatMessages(messages);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.TimeoutMinutes));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v1/chat/completions"));
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var body = JsonSerializer.Serialize(options.Body(profile, messages, maxTokens, server?.Llama == true, server?.RestrictedTextAdapter == true));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var trace = TraceSink.Value;
        var captured = trace == null ? null : new StringBuilder();
        void Record(string receipt)
        {
            try { trace?.Invoke(body,captured?.ToString() ?? "",receipt); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(e.Message); }
        }
        var requestClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Local chat returned HTTP {(int)response.StatusCode}; " + ((int)response.StatusCode is 400 or 422 ? "the server rejected this request. Reduce the output-token limit or shorten the conversation/attachments to fit the model context, and check the server log." : "check the saved model, key and server configuration."));
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new InvalidDataException("The endpoint did not return an event stream.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            double headersSeconds = requestClock.Elapsed.TotalSeconds;
            var answer = options.ToolDefinitions.HasValue ? new StringBuilder() : null;
            var result = await ReadChatStream(stream, part=>{
                if(captured!=null && captured.Length<100000)captured.Append(part.AsSpan(0,Math.Min(part.Length,100000-captured.Length)));
                if(answer!=null && answer.Length<200_000)answer.Append(part);
                onText(part);
            }, timeout.Token, onReasoning,options.ToolDefinitions.HasValue,onToolArguments);
            // A model that wrote its call as text (wrong tags, a fenced block) still gets its action executed.
            if(answer!=null && result.ToolCalls.Count==0 && ToolCallRecovery.TryParse(answer.ToString(),ToolCallRecovery.Names(options.ToolDefinitions!.Value)) is {} recovered)
                result=result with{FinishReason="tool_calls",ToolCalls=new[] { recovered },RecoveredToolCall=true};
            if(trace!=null)Record(JsonSerializer.Serialize(new{result.ObservedModel,result.FinishReason,result.ToolCalls,result.PromptTokens,result.CompletionTokens,elapsedMs=requestClock.ElapsedMilliseconds}));
            return result with { TimeToFirstTokenSeconds = result.TimeToFirstTokenSeconds + headersSeconds, RequestSeconds = requestClock.Elapsed.TotalSeconds };
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { Record("Request timed out."); throw new TimeoutException($"Local chat exceeded its {options.TimeoutMinutes}-minute deadline. Partial output is incomplete."); }
        catch(Exception e) { Record(e.Message); throw; }
    }

    // Incremental UTF-8 and SSE parsing. No ReadLineAsync allocation on an unbounded peer line.
    public static async Task<AiChatResult> ReadChatStream(Stream stream, Action<string> onText, CancellationToken cancellation, Action<string>? onReasoning = null,bool allowToolCalls=false,Action<int>? onToolArguments=null)
    {
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var bytes = new byte[4096]; var chars = new char[8192];
        var pending = new StringBuilder(); var data = new StringBuilder();
        int retained = 0, output = 0, thinking = 0; int? promptTokens = null, completionTokens = null; string? finish = null, observedModel = null;
        bool ended = false;
        string toolId="",toolName="";var toolArguments=new StringBuilder();bool hasTool=false;
        var streamClock = System.Diagnostics.Stopwatch.StartNew(); double? firstSeconds = null, generationRate = null;
        void Event()
        {
            if (data.Length == 0) return;
            var payload = data.ToString(); data.Clear();
            if (payload == "[DONE]")
            {
                if (finish == null) throw new InvalidDataException("The stream ended without a completion result. Partial output is incomplete.");
                if(hasTool&&(string.IsNullOrWhiteSpace(toolId)||toolId.Length>128||string.IsNullOrWhiteSpace(toolName)||toolName.Length>64||toolArguments.Length==0))throw new InvalidDataException("The streamed tool call was incomplete; no action is executable.");
                if(finish=="tool_calls"&&!hasTool)throw new InvalidDataException("The server finished a tool response without a tool call.");
                if (finish == "stop" && output == 0 && thinking == 0&&!hasTool) throw new InvalidDataException("The model returned an empty response.");
                // Other consumers also require an actual answer before accepting a natural stop as success.
                if (finish == "stop" && output == 0&&!hasTool) throw new ChatNoFinalAnswerException();
                ended = true; return;
            }
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _)) throw new InvalidDataException("The model server reported an error within the stream.");
            if (root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object &&
                timings.TryGetProperty("predicted_per_second", out var rate) && rate.ValueKind == JsonValueKind.Number && rate.TryGetDouble(out double perSecond) && double.IsFinite(perSecond) && perSecond > 0)
                generationRate = perSecond;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pn) && pn >= 0) promptTokens = pn;
                if (usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var cn) && cn >= 0) completionTokens = cn;
            }
            if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
            {
                var current = model.GetString();
                if (observedModel != null && current != observedModel) throw new InvalidDataException("The server changed model identity during the answer.");
                observedModel = current;
            }
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return; // Optional usage-only event.
            if (finish != null) throw new InvalidDataException("The stream supplied answer data after its completion result.");
            if (choices.GetArrayLength() != 1 || choices[0].GetProperty("index").GetInt32() != 0) throw new InvalidDataException("Expected one streamed answer.");
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("reasoning_content", out var reasoning) || delta.TryGetProperty("reasoning", out reasoning))
                {
                    if (reasoning.ValueKind == JsonValueKind.String) { var text = reasoning.GetString() ?? ""; if (text.Length > 0) firstSeconds ??= streamClock.Elapsed.TotalSeconds; thinking += text.Length; retained += text.Length; if (retained > ChatTextLimit) throw new InvalidDataException("Reasoning and answer exceeded the 4 Mi-character resource bound."); onReasoning?.Invoke(text); }
                }
                if (delta.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null && tools.GetArrayLength() > 0)
                {
                    if(!allowToolCalls)throw new InvalidDataException("This chat does not execute model tool calls.");
                    if(tools.GetArrayLength()!=1||tools[0].GetProperty("index").GetInt32()!=0)throw new InvalidDataException("Parallel or indexed tool calls are not accepted by the sequential coding harness.");
                    var tool=tools[0];hasTool=true;firstSeconds??=streamClock.Elapsed.TotalSeconds;
                    if(tool.TryGetProperty("id",out var id)){var value=id.GetString()??"";if(toolId.Length>0&&value!=toolId)throw new InvalidDataException("The streamed tool identity changed.");toolId=value;}
                    if(tool.TryGetProperty("type",out var type)&&type.GetString()!="function")throw new InvalidDataException("Unsupported native tool type.");
                    if(tool.TryGetProperty("function",out var function))
                    {
                        if(function.TryGetProperty("name",out var name)){var value=name.GetString()??"";if(toolName.Length>0&&value!=toolName)throw new InvalidDataException("The streamed tool name changed.");toolName=value;}
                        if(function.TryGetProperty("arguments",out var arguments)){var part=arguments.GetString()??"";toolArguments.Append(part);retained+=part.Length;}
                    }
                    if(toolArguments.Length>100_000||retained>ChatTextLimit)throw new InvalidDataException("Native tool arguments exceeded the resource bound.");
                    onToolArguments?.Invoke(toolArguments.Length);
                }
                if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                {
                    var text = content.GetString() ?? "";
                    output += text.Length;
                    retained += text.Length;
                    if (retained > ChatTextLimit) throw new InvalidDataException("Reasoning and answer exceeded the 4 Mi-character resource bound.");
                    if (text.Length > 0) { firstSeconds ??= streamClock.Elapsed.TotalSeconds; onText(text); }
                }
            }
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            {
                finish = reason.GetString();
                if (finish is not ("stop" or "length")&&!(allowToolCalls&&finish=="tool_calls")) throw new InvalidDataException("The server did not finish a normal text answer.");
            }
        }
        while (!ended)
        {
            var read = await stream.ReadAsync(bytes, cancellation);
            if (read == 0) throw new InvalidDataException("The chat stream disconnected before its completion marker. Partial output is incomplete.");
            var decoded = decoder.GetChars(bytes.AsSpan(0, read), chars.AsSpan(), false);
            for (int i = 0; i < decoded; i++)
            {
                if (chars[i] != '\n') { if (pending.Length + data.Length >= 256 * 1024) throw new InvalidDataException("A single stream event exceeded 256 Ki-characters."); pending.Append(chars[i]); continue; }
                var line = pending.ToString().TrimEnd('\r'); pending.Clear();
                if (line.Length == 0) Event();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    var value = line[5..]; if (value.StartsWith(' ')) value = value[1..];
                    data.Append(value);
                }
                if (ended) break;
            }
        }
        return new(finish!, output, observedModel, thinking, promptTokens, completionTokens, generationRate, firstSeconds, streamClock.Elapsed.TotalSeconds)
        { ToolCalls=hasTool?new AiToolCall[] { new(toolId,toolName,toolArguments.ToString()) }:Array.Empty<AiToolCall>() };
    }
}
