using System.Text.RegularExpressions;

namespace Mason.Code;

/// <summary>Models that let the agent search code by meaning. Supplied by the host when an embeddings model is connected.</summary>
public interface ICodeSemanticModels
{
    /// <summary>Stable identity of the embeddings model; cached vectors are keyed by it.</summary>
    string EmbeddingIdentity { get; }
    bool CanRerank { get; }
    Task<float[][]> Embed(IReadOnlyList<string> texts, CancellationToken ct);
    Task<double[]> Rerank(string query, IReadOnlyList<string> documents, CancellationToken ct);
}

public sealed record SemanticHit(string Path, int StartLine, int EndLine, double Score, string Preview);
public sealed record SemanticResult(string Query, string Model, bool Reranked, int FilesIndexed, int ChunksIndexed, bool Partial, IReadOnlyList<SemanticHit> Results, string Note);

/// <summary>Chunks workspace source, embeds each chunk once per content hash, and ranks by cosine similarity, then optional reranking.</summary>
public static class CodeSemanticIndex
{
    /// <summary>The desktop app sets this when an embeddings connection is chosen for code search. Null means the tool is unavailable.</summary>
    public static Func<ICodeSemanticModels?>? Provider { get; set; }
    public static bool Available => Provider?.Invoke() != null;

    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".rb", ".go", ".rs", ".java", ".kt", ".swift", ".c", ".h", ".cpp", ".hpp", ".php", ".dart", ".scala", ".sql", ".html", ".css", ".scss", ".vue", ".svelte", ".md", ".ps1", ".sh", ".json", ".yaml", ".yml", ".toml", ".xaml", ".csproj" };
    const int ChunkLines = 40, Overlap = 8, MaxFiles = 1000, MaxChunks = 6000, Batch = 32;
    // Vectors survive across runs in this process: (model, file hash, chunk start) -> vector.
    static readonly Dictionary<string, float[]> vectors = new(StringComparer.Ordinal);

    public static IReadOnlyList<(int Start, int End, string Text)> Chunks(string path, string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var chunks = new List<(int, int, string)>();
        for (int start = 0; start < lines.Length; start += ChunkLines - Overlap)
        {
            int end = Math.Min(lines.Length, start + ChunkLines);
            var body = string.Join("\n", lines[start..end]);
            if (body.Trim().Length >= 20) chunks.Add((start + 1, end, "File: " + path + "\n" + (body.Length > 6000 ? body[..6000] : body)));
            if (end == lines.Length) break;
        }
        return chunks;
    }

    public static async Task<SemanticResult> Search(CodeWorkspace scope, ICodeSemanticModels models, string query, string directory, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) throw new ArgumentException("Describe what to find in 1–2,000 characters.");
        var listing = scope.Files(string.IsNullOrWhiteSpace(directory) ? "." : directory, MaxFiles);
        var pending = new List<(string Key, string Text)>();
        var all = new List<(string Key, string Path, int Start, int End, string Text)>();
        bool partial = listing.Truncated; int files = 0;
        foreach (var path in listing.Files.Where(p => Extensions.Contains(Path.GetExtension(p)) && !Regex.IsMatch(p, @"(?i)(^|/)(node_modules|bin|obj|dist|build|\.git|\.venv|venv|__pycache__)/|\.min\.(js|css)$|package-lock\.json$")))
        {
            ct.ThrowIfCancellationRequested();
            CodeFile file;
            try { file = scope.Read(path); } catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { continue; }
            files++;
            foreach (var (start, end, text) in Chunks(file.Path, file.Content))
            {
                if (all.Count >= MaxChunks) { partial = true; break; }
                var key = models.EmbeddingIdentity + "|" + file.Sha256 + "|" + start;
                all.Add((key, file.Path, start, end, text));
                lock (vectors) if (!vectors.ContainsKey(key)) pending.Add((key, text));
            }
        }
        for (int i = 0; i < pending.Count; i += Batch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = pending.Skip(i).Take(Batch).ToArray();
            var embedded = await models.Embed(batch.Select(b => b.Text).ToArray(), ct).ConfigureAwait(false);
            lock (vectors) for (int j = 0; j < batch.Length; j++) vectors[batch[j].Key] = Normalize(embedded[j]);
        }
        var q = Normalize((await models.Embed([query], ct).ConfigureAwait(false))[0]);
        var ranked = all.Select(c => { float[]? v; lock (vectors) vectors.TryGetValue(c.Key, out v); return (c, score: v == null || v.Length != q.Length ? double.NegativeInfinity : Dot(q, v)); })
            .Where(x => !double.IsNegativeInfinity(x.score)).OrderByDescending(x => x.score).Take(Math.Max(limit * 4, 24)).ToList();
        bool reranked = false;
        if (models.CanRerank && ranked.Count > 1)
        {
            try
            {
                var scores = await models.Rerank(query, ranked.Select(r => r.c.Text).ToArray(), ct).ConfigureAwait(false);
                ranked = ranked.Select((r, i) => (r.c, score: scores[i])).OrderByDescending(x => x.score).ToList(); reranked = true;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException) { }
        }
        var hits = new List<SemanticHit>();
        foreach (var (c, score) in ranked)
        {
            // Overlapping windows from the same file collapse to the best one.
            if (hits.Any(h => h.Path == c.Path && c.Start <= h.EndLine && c.End >= h.StartLine)) continue;
            var body = c.Text[(c.Text.IndexOf('\n') + 1)..];
            hits.Add(new(c.Path, c.Start, c.End, Math.Round(score, 4), body.Length > 700 ? body[..700] + "…" : body));
            if (hits.Count >= limit) break;
        }
        return new(query, models.EmbeddingIdentity, reranked, files, all.Count, partial, hits,
            "Ranked by meaning, not exact text. Read the cited lines before editing; use search_text for exact strings.");
    }
    static float[] Normalize(float[] v) { double n = Math.Sqrt(v.Sum(x => (double)x * x)); return n == 0 ? v : v.Select(x => (float)(x / n)).ToArray(); }
    static double Dot(float[] a, float[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }
}
