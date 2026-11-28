using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mason;

public enum AiCapability { Models, Chat, Embeddings, Reranking }
public sealed record AiCheck(AiCapability Capability, string State, string Detail, DateTimeOffset At, string[]? Models = null);
public sealed record ManagedEmbeddingProcessing(string QueryPrompt,string DocumentPrompt,bool IncludePrompt,string Fingerprint,int Dimensions,string Normalization)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Description=>$"Automatic query/document prompts · {(IncludePrompt?"prompt tokens included":"prompt tokens excluded")} in pooling · {Normalization} · {Dimensions} dimensions";
    public void Validate()
    {
        if(QueryPrompt is null||DocumentPrompt is null||Fingerprint is null||QueryPrompt.Length>2000||DocumentPrompt.Length>2000||Fingerprint.Length!=64||!Fingerprint.All(Uri.IsHexDigit)||Dimensions is <1 or >16000||Normalization is not ("none" or "L2"))throw new ArgumentException("Invalid managed embedding processing identity. Restart and check the model.");
    }
}
/// <summary>Generation settings a connection applies to the coding agent's actions. Null values keep the model's defaults.
/// Helper calls that need deterministic output (reviews, validators) keep their own settings.</summary>
public sealed record CodeSampling(double? Temperature = null, double? TopP = null, int? TopK = null, double? MinP = null, double? RepeatPenalty = null, int? Seed = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool Empty => Temperature == null && TopP == null && TopK == null && MinP == null && RepeatPenalty == null && Seed == null;
    public void Apply(ChatGenerationOptions options)
    {
        if (Temperature is { } t) options.Temperature = t; if (TopP is { } p) options.TopP = p; if (TopK is { } k) options.TopK = k;
        if (MinP is { } m) options.MinP = m; if (RepeatPenalty is { } r) options.RepeatPenalty = r; if (Seed is { } s) options.Seed = s;
    }

    public const string Help =
        "/generation                  The generation settings kept for the model in use\n" +
        "/generation NAME VALUE       Set one: temperature (0–2), top_p (0–1), top_k (1–1000), min_p (0–1), repeat_penalty (0–2), seed\n" +
        "/generation NAME off         Leave one to the server's default\n" +
        "/generation clear            Leave them all to the server's defaults\n" +
        "They apply to this connection whenever it works as the coding agent. Chats have their own options.";

    static readonly (string Key, string Label, double Low, double High, bool Whole)[] Fields =
        [("temperature", "Temperature", 0, 2, false), ("top_p", "Top P", 0, 1, false), ("top_k", "Top K", 1, 1000, true),
         ("min_p", "Min P", 0, 1, false), ("repeat_penalty", "Repeat penalty", 0, 2, false), ("seed", "Seed", 0, int.MaxValue, true)];

    double? Get(string key) => key switch { "temperature" => Temperature, "top_p" => TopP, "top_k" => TopK, "min_p" => MinP, "repeat_penalty" => RepeatPenalty, _ => Seed };
    CodeSampling With(string key, double? v) => key switch
    {
        "temperature" => this with { Temperature = v }, "top_p" => this with { TopP = v }, "top_k" => this with { TopK = v is { } k ? (int)k : null },
        "min_p" => this with { MinP = v }, "repeat_penalty" => this with { RepeatPenalty = v }, _ => this with { Seed = v is { } s ? (int)s : null }
    };
    static string Shown(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The CLI's /generation for one connection: show, set or clear the sampling the app's
    /// "Coding generation" fields keep. <paramref name="save"/> receives the new settings (null for the defaults).</summary>
    public static string Command(string rest, AiEndpointProfile profile, Action<CodeSampling?> save)
    {
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && words[0].Equals("help", StringComparison.OrdinalIgnoreCase)) return Help;
        var kept = profile.Sampling ?? new CodeSampling();
        if (words.Length == 0)
            return string.Join("\n", new[] { "Generation settings for " + profile.Model + " at " + profile.Endpoint + ":" }
                .Concat(Fields.Select(f => f.Key + "  ·  " + (kept.Get(f.Key) is { } v ? Shown(v) : "the server's default"))));
        if (words.Length == 1 && words[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            save(null);
            return "Generation settings for " + profile.Model + " are left to the server's defaults.";
        }
        var key = words.Length == 2 ? words[0].ToLowerInvariant().Replace('-', '_') : "";
        if (Array.FindIndex(Fields, f => f.Key == key) is var index && index < 0)
            throw new ArgumentException("Use /generation NAME VALUE, with one of: " + string.Join(", ", Fields.Select(f => f.Key)) + ".");
        var field = Fields[index];
        if (words[1].ToLowerInvariant() is "off" or "default" or "none")
        {
            var cleared = kept.With(key, null);
            save(cleared.Empty ? null : cleared);
            return field.Label + " is left to the server's default for " + profile.Model + ".";
        }
        if (!double.TryParse(words[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) ||
            value < field.Low || value > field.High || field.Whole && value != Math.Round(value))
            throw new ArgumentException(field.Label + " is a " + (field.Whole ? "whole number" : "number") + " from " + Shown(field.Low) + " to " + Shown(field.High) + ", or off.");
        save(kept.With(key, value));
        return field.Label + " is " + Shown(value) + " for " + profile.Model + ".";
    }
}
public sealed record AiEndpointProfile
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CodeSampling? Sampling { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Local AI connection";
    public string Endpoint { get; init; } = "";
    public string Model { get; init; } = "";
    public AiCapability? ManagedCapability { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RerankingRoute { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ManagedEmbeddingProcessing? EmbeddingProcessing { get; init; }
    public string Secret { get; init; } = "";
    public List<AiCheck> Checks { get; init; } = [];
    /// <summary>Chat-capable for agent roles: not a managed or verified embeddings/reranking-only server.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool ForChat => ManagedCapability is null or AiCapability.Chat &&
        !(ManagedCapability == null && Checks.Any(c => c.State == "Passed" && c.Capability is AiCapability.Embeddings or AiCapability.Reranking) && !Checks.Any(c => c.State == "Passed" && c.Capability == AiCapability.Chat));
    /// <summary>Latest non-listing check, phrased for the connection list.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Health
    {
        get
        {
            var last = Checks.Where(c => c.Capability != AiCapability.Models).OrderByDescending(c => c.At).FirstOrDefault();
            if (last == null) return "○ Not checked yet";
            var age = DateTimeOffset.UtcNow - last.At;
            var when = age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{age.TotalMinutes:0} min ago" : age.TotalDays < 1 ? $"{age.TotalHours:0} h ago" : last.At.LocalDateTime.ToString("d");
            return (last.State == "Passed" ? "● " : "○ ") + last.Capability + " " + (last.State == "Passed" ? "passed" : last.State.ToLowerInvariant()) + " · " + when;
        }
    }
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayName => Name.Replace(" · managed chat", "", StringComparison.Ordinal);
    public override string ToString() => DisplayName;
}

/// <summary>Explicit, synthetic capability probes. Never sends user content.</summary>
public sealed partial class LocalAiClient : IDisposable
{
    const int MaxBytes = 1_048_576;
    readonly HttpClient http;

    public LocalAiClient() : this(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { }
    public LocalAiClient(HttpMessageHandler handler) => http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri ValidateEndpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use an HTTP loopback URL without credentials, query or fragment.");
        // Literal addresses avoid DNS rebinding; localhost is pinned to 127.0.0.1.
        var host = uri.Host.Trim('[', ']');
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !(IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException("Local AI connections require localhost or a literal loopback address.");
        if (uri.AbsolutePath is not ("/" or "/v1" or "/v1/"))
            throw new ArgumentException("Use the server root URL or its /v1 base URL.");
        return new UriBuilder(uri) { Host = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : host, Path = "/" }.Uri;
    }

    public async Task<AiCheck> Check(AiEndpointProfile profile, AiCapability capability, string key, CancellationToken cancellation)
    {

        var endpoint = ValidateEndpoint(profile.Endpoint);
        if (capability != AiCapability.Models && string.IsNullOrWhiteSpace(profile.Model))
            throw new ArgumentException("Choose or enter the exact model ID before testing inference.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var path=capability switch{AiCapability.Models=>"v1/models",AiCapability.Embeddings=>"v1/embeddings",AiCapability.Reranking=>"v1/rerank",_=>"v1/chat/completions"};
            object body=capability switch
            {
                AiCapability.Embeddings=>new { model=profile.Model,input=new[]{"int add(int a, int b) => a + b;","Return the sum of two integers."} },
                AiCapability.Reranking=>new { model=profile.Model,query="Where are two numbers added?",documents=new[]{"int add(int a, int b) => a + b;","void Log(string message) => Console.WriteLine(message);"} },
                _=>new { model=profile.Model,messages=new[]{new{role="user",content="Reply with OK."}},max_tokens=32,stream=false,temperature=0,chat_template_kwargs=new{enable_thinking=false},reasoning_effort="none" }
            };
            using var request = new HttpRequestMessage(capability == AiCapability.Models ? HttpMethod.Get : HttpMethod.Post, new Uri(endpoint, path));
            if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (capability != AiCapability.Models) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var state = (int)response.StatusCode is 404 or 405 or 501 ? "Unavailable" : "Failed";
                var failureDetail = state == "Unavailable"
                    ? $"HTTP {(int)response.StatusCode}: this endpoint does not expose this capability in its current configuration. Check the server route and configuration."
                    : (int)response.StatusCode is 401 or 403 ? $"HTTP {(int)response.StatusCode}: check this connection's API key and server access settings."
                    : $"HTTP {(int)response.StatusCode}: the server could not complete this check. Retry or inspect its local logs.";
                return new(capability, state, failureDetail, DateTimeOffset.UtcNow);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + count > MaxBytes) throw new InvalidDataException("Capability response exceeded 1 MiB.");
                buffer.Write(chunk, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var detail = ValidateResponse(capability, json.RootElement);
            var models = capability == AiCapability.Models ? json.RootElement.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).Distinct(StringComparer.Ordinal).ToArray() : null;
            return new(capability, "Passed", detail, DateTimeOffset.UtcNow, models);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { return new(capability, "Timed out", "No completed response within 60 seconds; model load time may contribute.", DateTimeOffset.UtcNow); }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return new(capability, "Unreachable", "Could not complete a connection to the local endpoint. Check its address, TLS and server state.", DateTimeOffset.UtcNow); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException or FormatException or OverflowException or IndexOutOfRangeException)
        { return new(capability, "Invalid response", "The response did not match the expected capability format, or exceeded the response limit.", DateTimeOffset.UtcNow); }
    }

    static string ValidateResponse(AiCapability capability, JsonElement root)
    {
        if (capability == AiCapability.Models)
        {
            var models = root.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
            if (models.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException();
            return models.Length == 0 ? "Server returned no model IDs; inference remains untested." : "Model IDs: " + string.Join(", ", models).Substring(0, Math.Min(2000, string.Join(", ", models).Length));
        }
        if (capability == AiCapability.Chat)
        {
            var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException();
            return "Completed a bounded synthetic chat request. No user content was included.";
        }
        if (capability == AiCapability.Embeddings)
        {
            var vectors = Vectors(root);
            if (vectors.Length != 2 || vectors[0].Length == 0 || vectors[0].Length != vectors[1].Length) throw new InvalidDataException();
            return $"Returned {vectors[0].Length}-dimension embeddings for a synthetic code snippet. Ready for code search.";
        }
        if (capability == AiCapability.Reranking)
        {
            var scores = Scores(root, 2);
            if (scores.Length != 2) throw new InvalidDataException();
            return scores[0] > scores[1] ? "Ranked the matching code snippet first. Ready to rerank code search." : "Returned scores, but ranked the unrelated snippet first. Results may be weak.";
        }
        throw new ArgumentException("Unsupported capability.");
    }
    static float[][] Vectors(JsonElement root) => root.GetProperty("data").EnumerateArray().OrderBy(x => x.TryGetProperty("index", out var i) ? i.GetInt32() : 0)
        .Select(x => x.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray()).ToArray();
    static double[] Scores(JsonElement root, int count)
    {
        var scores = new double[count]; var seen = 0;
        foreach (var r in root.GetProperty("results").EnumerateArray())
        { var i = r.GetProperty("index").GetInt32(); if (i < 0 || i >= count) throw new InvalidDataException(); scores[i] = r.GetProperty("relevance_score").GetDouble(); seen++; }
        return seen == count ? scores : [];
    }
    async Task<JsonDocument> Post(AiEndpointProfile profile, string key, string path, object body, CancellationToken ct, int maxBytes = 64 * 1024 * 1024)
    {
        var endpoint = ValidateEndpoint(profile.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path)) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"{profile.Name} returned HTTP {(int)response.StatusCode} for {path}.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + count > maxBytes) throw new InvalidDataException("Response exceeded its size limit."); buffer.Write(chunk, 0, count); }
        return JsonDocument.Parse(buffer.ToArray());
    }
    /// <summary>Embeds code or a query for the agent's semantic search.</summary>
    public async Task<float[][]> Embed(AiEndpointProfile profile, string key, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        using var doc = await Post(profile, key, "v1/embeddings", new { model = profile.Model, input = inputs }, ct);
        var vectors = Vectors(doc.RootElement);
        if (vectors.Length != inputs.Count) throw new InvalidDataException("The embeddings server returned a different number of vectors.");
        return vectors;
    }
    /// <summary>Scores documents against a query, in input order.</summary>
    public async Task<double[]> Rerank(AiEndpointProfile profile, string key, string query, IReadOnlyList<string> documents, CancellationToken ct)
    {
        using var doc = await Post(profile, key, "v1/rerank", new { model = profile.Model, query, documents, top_n = documents.Count }, ct);
        var scores = Scores(doc.RootElement, documents.Count);
        if (scores.Length != documents.Count) throw new InvalidDataException("The reranker didn't score every document.");
        return scores;
    }
    /// <summary>Well-known local inference servers. Mason's own managed ports (18310–18329) are excluded.</summary>
    public static readonly (int Port, string Name)[] KnownServers = [(11434, "Ollama"), (1234, "LM Studio"), (8080, "llama.cpp server"), (8081, "llama.cpp server"), (8000, "vLLM"), (1337, "Jan"), (5001, "KoboldCpp"), (5000, "text-generation-webui"), (4891, "GPT4All"), (39281, "Cortex")];
    public sealed record FoundServer(string Name, string Endpoint, string[] Models);
    /// <summary>Probes loopback ports for OpenAI-compatible servers. Reads only the model list.</summary>
    public async Task<FoundServer[]> Detect(CancellationToken ct)
    {
        async Task<FoundServer?> Probe(int port, string name)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/v1/models", timeout.Token);
                if (!response.IsSuccessStatusCode) return null;
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                if (bytes.Length > MaxBytes) return null;
                using var doc = JsonDocument.Parse(bytes);
                var models = doc.RootElement.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString() ?? "").Where(x => x.Length > 0).Distinct().Take(200).ToArray();
                return new(name, $"http://127.0.0.1:{port}/", models);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { if (ct.IsCancellationRequested) throw; return null; }
        }
        var found = await Task.WhenAll(KnownServers.Select(s => Probe(s.Port, s.Name)));
        return found.OfType<FoundServer>().ToArray();
    }
    public void Dispose() => http.Dispose();
}
