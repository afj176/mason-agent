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
public sealed record AiEndpointProfile
{
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
        if(capability is not (AiCapability.Models or AiCapability.Chat))throw new ArgumentException("This app supports language model connections only.");
        var endpoint = ValidateEndpoint(profile.Endpoint);
        if (capability != AiCapability.Models && string.IsNullOrWhiteSpace(profile.Model))
            throw new ArgumentException("Choose or enter the exact model ID before testing inference.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var path=capability==AiCapability.Models?"v1/models":"v1/chat/completions";
            object body=new { model=profile.Model,messages=new[]{new{role="user",content="Reply with OK."}},max_tokens=32,stream=false,temperature=0,chat_template_kwargs=new{enable_thinking=false},reasoning_effort="none" };
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
        throw new ArgumentException("Unsupported capability.");
    }
    public void Dispose() => http.Dispose();
}
