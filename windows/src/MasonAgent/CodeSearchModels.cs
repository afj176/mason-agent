using Mason.Code;

namespace Mason;

/// <summary>Which connections power the agent's semantic code search. Applies to every coding session.</summary>
/// <summary>Off switches code search off for every session; Embeddings and Reranker are kept for when it is back on.</summary>
public sealed record CodeSearchSettings(Guid? Embeddings = null, Guid? Reranker = null, bool Off = false)
{
    public const string File = CodeSemanticIndex.SettingsFile;
    public static CodeSearchSettings Read() { try { return Store.Read<CodeSearchSettings>(File) ?? new(); } catch { return new(); } }
}

/// <summary>Routes the agent's semantic_search tool to the assigned embeddings and reranking servers.</summary>
public sealed class CodeSearchModels(AiEndpointProfile embeddings, string embeddingsKey, AiEndpointProfile? reranker, string rerankerKey) : ICodeSemanticModels
{
    public string EmbeddingIdentity => embeddings.Endpoint + "#" + embeddings.Model;
    public bool CanRerank => reranker != null;
    public async Task<float[][]> Embed(IReadOnlyList<string> texts, CancellationToken ct) { using var client = new LocalAiClient(); return await client.Embed(embeddings, embeddingsKey, texts, ct); }
    public async Task<double[]> Rerank(string query, IReadOnlyList<string> documents, CancellationToken ct)
    {
        if (reranker == null) throw new InvalidOperationException("No reranker is assigned.");
        using var client = new LocalAiClient(); return await client.Rerank(reranker, rerankerKey, query, documents, ct);
    }

    /// <summary>Uses the assigned connections, or the only connection that passed an embeddings check when none is assigned.</summary>
    public static AiEndpointProfile? Pick(IReadOnlyList<AiEndpointProfile> profiles, Guid? assigned, AiCapability capability)
    {
        bool Passed(AiEndpointProfile p) => p.Checks.Any(c => c.Capability == capability && c.State == "Passed");
        if (assigned is { } id) return profiles.FirstOrDefault(p => p.Id == id && Passed(p));
        var ready = profiles.Where(p => p.ManagedCapability == capability || Passed(p)).Where(Passed).ToArray();
        return ready.Length == 1 ? ready[0] : null;
    }
}
