using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason;

public sealed record HubModel(string Id, string Task, string[] Tags, long Downloads, string Updated)
{
    public string Library { get; init; } = "";
    /// <summary>GGUF architecture or Transformers model_type, when the Hub reports one.</summary>
    public string Architecture { get; init; } = "";
    public long Parameters { get; init; }
    public long Likes { get; init; }
    public bool Gated { get; init; }
    public string Name => Id.Split('/').Last();
    public string Publisher => Id.Contains('/') ? Id.Split('/')[0] : "";
    public string Format => Tags.Contains("gguf") ? "GGUF" : Tags.Contains("mlx") || Library == "mlx" ? "MLX" : Tags.Contains("safetensors") ? "Safetensors" : Tags.Contains("onnx") ? "ONNX" : "Inspect files";
    public AgentRole Role => ModelCompatibility.Classify(Task, Tags, Id, Library);
    public string Inputs => Task switch { "image-text-to-text" => "Text + image", "audio-text-to-text" => "Text + audio", "video-text-to-text" => "Text + video", "any-to-any" => "Multimodal", _ => Tags.Contains("audio-text-to-text") ? "Text + audio" : "Text" };
    public string Size => Parameters <= 0 ? "" : Parameters >= 1_000_000_000 ? $"{Parameters / 1e9:0.#}B" : $"{Parameters / 1e6:0}M";
    public string Signals => Compact(Downloads) + " downloads" + (Likes > 0 ? " · " + Compact(Likes) + " likes" : "");
    static string Compact(long n) => n >= 1_000_000 ? $"{n / 1e6:0.#}M" : n >= 1000 ? $"{n / 1e3:0.#}k" : n.ToString();
}
public sealed record HubFile(string Path, long Size, string Hash, bool Lfs);
public sealed record HubVariant(string Name, string Format, HubFile[] Files, string Note)
{
    public AgentRole Role { get; init; } = AgentRole.Implementer;
    public long Bytes => Files.Sum(f => f.Size);
    public string Quality => ModelCompatibility.QuantQuality(Name);
    public override string ToString() => Name + " · " + HubFormat.Size(Bytes) + (Quality.Length > 0 ? " · " + Quality : "");
}
public sealed record HubRepository(string Id, string Revision, string License, bool Gated, HubVariant[] Variants);
public static class HubFormat
{
    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GiB" : bytes >= 1L << 20 ? $"{bytes / 1048576d:0.0} MiB" : $"{bytes:N0} B";
    public static string Repo(string value)
    {
        if (!Regex.IsMatch(value, @"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,95}/[a-zA-Z0-9][a-zA-Z0-9_.-]{0,95}$") || value.Contains("..")) throw new InvalidDataException("Enter an exact Hugging Face owner/model ID.");
        return value;
    }
    public static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 220 || value.Contains('\\') || value.StartsWith('/') || value.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase))) throw new InvalidDataException("Repository contains a path that cannot be safely stored on Windows.");
        return value;
    }
    public static string Under(string root, string relative)
    {
        Relative(relative);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var basePath = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("File escaped model storage.");
        return full;
    }
    public static void NoLinks(string path)
    {
        for (var current = System.IO.Path.GetFullPath(path); current != null; current = System.IO.Path.GetDirectoryName(current))
        {
            try{if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Choose storage without directory links or reparse points.");}
            catch(FileNotFoundException){}
            catch(DirectoryNotFoundException){}
        }
    }
}

/// <summary>Hub metadata and HTTPS file transport. Endpoint credentials are never used here.</summary>
public sealed class HubClient : IDisposable
{
    readonly HttpClient client;
    readonly Func<string> token;
    public HubClient(Func<string>? token = null, HttpMessageHandler? handler = null)
    {
        this.token = token ?? (() => "");
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MasonAgent/1.1");
    }
    public static Uri ModelPage(string id) => new("https://huggingface.co/" + HubFormat.Repo(id));
    public static Uri FileUrl(string id, string revision, string path)
    {
        HubFormat.Repo(id); HubFormat.Relative(path);
        if (!Regex.IsMatch(revision, "^[a-fA-F0-9]{40}$")) throw new InvalidDataException("Download requires a pinned commit.");
        return new("https://huggingface.co/" + id + "/resolve/" + revision + "/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));
    }
    public async Task<HttpResponseMessage> Send(Uri uri, long offset, CancellationToken ct)
    {
        for (int redirects = 0; redirects < 6; redirects++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || !(uri.Host == "huggingface.co" || uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase))) throw new IOException("The Hub returned an unsupported download host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // Bearer tokens are only sent to the Hub, never its redirected CDN URLs.
            if (uri.Host == "huggingface.co" && token() is { Length: > 0 } secret) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is {} next) { uri = next.IsAbsoluteUri ? next : new Uri(uri, next); response.Dispose(); continue; }
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode; response.Dispose();
                throw new IOException(code switch { HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Access required. Open the model page to accept its access terms, then add a Hugging Face read token.", HttpStatusCode.NotFound => "Repository, revision or file was not found, or your account cannot access it.", HttpStatusCode.TooManyRequests => "Hugging Face rate limit reached. Wait before retrying.", _ => "Hugging Face request failed (HTTP " + (int)code + "). Retry when the connection is available." });
            }
            return response;
        }
        throw new IOException("Too many download redirects.");
    }
    async Task<(JsonDocument Document, string? Next)> Json(Uri uri, CancellationToken ct)
    {
        using var response = await Send(uri, 0, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var bytes = new byte[65536]; int read;
        while ((read = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0) { if (buffer.Length + read > 16 * 1024 * 1024) throw new IOException("Hub metadata exceeds the supported size."); buffer.Write(bytes, 0, read); }
        string? next = null;
        if (response.Headers.TryGetValues("Link", out var links)) foreach (var link in links.SelectMany(l => l.Split(','))) { var match = Regex.Match(link, "<([^>]+)>;\\s*rel=\"next\""); if (match.Success) next = match.Groups[1].Value; }
        return (JsonDocument.Parse(buffer.ToArray()), next);
    }
    public static readonly string[] Publishers = ["unsloth", "ggml-org", "bartowski", "lmstudio-community", "Qwen", "google", "mistralai", "microsoft", "nomic-ai", "BAAI"];
    const string Fields = "&expand[]=pipeline_tag&expand[]=tags&expand[]=downloads&expand[]=likes&expand[]=lastModified&expand[]=library_name&expand[]=gguf&expand[]=safetensors&expand[]=config&expand[]=gated";
    /// <summary>Every matching repository is returned. Compatibility is explained per result, never used to hide it.</summary>
    public async Task<HubModel[]> Search(string query, string publisher, string format, CancellationToken ct, string sort = "downloads")
    {
        query = query.Trim();
        if (Uri.TryCreate(query, UriKind.Absolute, out var page) && page.Host is "huggingface.co" or "hf.co" && page.AbsolutePath.Trim('/').Split('/') is [var owner, var name, ..]) query = owner + "/" + name;
        string? author = Publishers.FirstOrDefault(p => p.Equals(publisher, StringComparison.OrdinalIgnoreCase));
        bool Keep(HubModel m) => (author == null || m.Publisher.Equals(author, StringComparison.OrdinalIgnoreCase)) && (format is not ("GGUF" or "Safetensors") || m.Format == format);
        string Filters() => (author == null ? "" : "&author=" + author) + (format is "GGUF" or "Safetensors" ? "&filter=" + format.ToLowerInvariant() : "");
        if (Regex.IsMatch(query, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
        {
            HubFormat.Repo(query);
            HubModel? exact = null;
            try { var direct = await Json(new Uri("https://huggingface.co/api/models/" + query + "?" + Fields.TrimStart('&')), ct).ConfigureAwait(false); using var doc = direct.Document; exact = ReadModel(doc.RootElement); }
            catch (IOException) { }
            // An exact repository also lists its quantizations and adapters, so local builds are one click away.
            var derived = exact == null ? Array.Empty<HubModel>() : await List("&filter=" + Uri.EscapeDataString("base_model:quantized:" + exact.Id) + Filters(), "downloads", ct).ConfigureAwait(false);
            var adapters = exact == null ? Array.Empty<HubModel>() : await List("&filter=" + Uri.EscapeDataString("base_model:adapter:" + exact.Id) + Filters(), "downloads", ct, 10).ConfigureAwait(false);
            var fallback = exact != null ? Array.Empty<HubModel>() : await List("&search=" + Uri.EscapeDataString(query.Split('/')[1]) + Filters(), sort, ct).ConfigureAwait(false);
            return new[] { exact }.OfType<HubModel>().Concat(derived).Concat(adapters).Concat(fallback).Where(Keep).DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return await List((query.Length == 0 ? "" : "&search=" + Uri.EscapeDataString(query)) + Filters(), sort, ct).ConfigureAwait(false);
    }
    /// <summary>Landing lists: what is trending for one agent job.</summary>
    public Task<HubModel[]> Trending(string pipelineTag, CancellationToken ct, string filter = "") =>
        List((pipelineTag.Length == 0 ? "" : "&pipeline_tag=" + Uri.EscapeDataString(pipelineTag)) + (filter.Length == 0 ? "" : "&filter=" + Uri.EscapeDataString(filter)), "trendingScore", ct, 24);
    async Task<HubModel[]> List(string parameters, string sort, CancellationToken ct, int limit = 50)
    {
        if (sort is not ("downloads" or "likes" or "lastModified" or "trendingScore" or "createdAt")) sort = "downloads";
        var result = await Json(new Uri($"https://huggingface.co/api/models?limit={limit}&sort={sort}&direction=-1" + parameters + Fields), ct).ConfigureAwait(false); using var document = result.Document;
        return document.RootElement.EnumerateArray().Select(ReadModel).Where(m => m.Id.Contains('/')).ToArray();
    }
    static HubModel ReadModel(JsonElement m)
    {
        var tags = m.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
        long Long(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        var gguf = m.TryGetProperty("gguf", out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
        var tensors = m.TryGetProperty("safetensors", out var st) && st.ValueKind == JsonValueKind.Object ? st : default;
        var config = m.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        string architecture = gguf.ValueKind == JsonValueKind.Object ? String(gguf, "architecture") : "";
        if (architecture.Length == 0 && config.ValueKind == JsonValueKind.Object) architecture = String(config, "model_type");
        bool gated = m.TryGetProperty("gated", out var gate) && gate.ValueKind is not (JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined);
        return new(m.GetProperty("id").GetString()!, String(m, "pipeline_tag"), tags, m.TryGetProperty("downloads", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0, String(m, "lastModified"))
        { Library = String(m, "library_name"), Architecture = architecture, Parameters = Math.Max(Long(gguf, "total"), Long(tensors, "total")), Likes = m.TryGetProperty("likes", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt64() : 0, Gated = gated };
    }
    static string String(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    /// <summary>Plain-text model card for the inspector. Front matter, HTML, images and link targets are dropped.</summary>
    public async Task<string> Readme(string id, string revision, CancellationToken ct)
    {
        using var response = await Send(FileUrl(id, revision, "README.md"), 0, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return CardText(text);
    }
    public static string CardText(string text)
    {
        if (text.Length > 200_000) text = text[..200_000];
        text = text.ReplaceLineEndings("\n");
        text = Regex.Replace(text, @"\A---\n[\s\S]*?\n---\n", "");
        text = Regex.Replace(text, @"<[^>]{1,400}>", "");
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
    public async Task<HubRepository> Inspect(string id, CancellationToken ct)
    {
        HubFormat.Repo(id);
        var info = await Json(new("https://huggingface.co/api/models/" + id), ct).ConfigureAwait(false); using var doc = info.Document;
        string revision = String(doc.RootElement, "sha");
        if (!Regex.IsMatch(revision, "^[a-fA-F0-9]{40}$")) throw new InvalidDataException("Hub did not return a pinned commit.");
        string license = doc.RootElement.TryGetProperty("cardData", out var card) ? String(card,"license") : "";
        bool gated = doc.RootElement.TryGetProperty("gated", out var gate) && gate.ValueKind != JsonValueKind.False && gate.ValueKind != JsonValueKind.Null;
        string? next = "https://huggingface.co/api/models/" + id + "/tree/" + revision + "?recursive=true&limit=1000";
        var files = new List<HubFile>(); int pages = 0;
        while (next != null)
        {
            if (++pages > 30) throw new IOException("Repository has too many files to review safely.");
            var uri = new Uri(next); if (uri.Host != "huggingface.co" || !uri.AbsolutePath.StartsWith("/api/models/" + id + "/tree/" + revision, StringComparison.Ordinal)) throw new IOException("Invalid Hub pagination link.");
            var result = await Json(uri, ct).ConfigureAwait(false); using var page = result.Document;
            foreach (var item in page.RootElement.EnumerateArray())
            {
                if (String(item,"type") != "file") continue;
                string path = HubFormat.Relative(String(item,"path")); long size = item.GetProperty("size").GetInt64();
                bool lfs = item.TryGetProperty("lfs",out var large) && large.ValueKind == JsonValueKind.Object;
                string hash = lfs ? String(large,"oid") : String(item,"oid");
                if (size < 0 || !Regex.IsMatch(hash,lfs?"^[a-fA-F0-9]{64}$":"^[a-fA-F0-9]{40}$")) throw new InvalidDataException("File integrity metadata is missing or unsupported.");
                files.Add(new(path,size,hash.ToLowerInvariant(),lfs));
            }
            next = result.Next;
        }
        if (files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count) throw new InvalidDataException("Repository paths collide on Windows.");
        var variants=Variants(files);
        if(files.FirstOrDefault(f=>f.Path=="model.safetensors.index.json") is {} index)
        {
            if(index.Size>16*1024*1024)throw new InvalidDataException("Weight index is too large to validate.");
            var indexed=await Json(FileUrl(id,revision,index.Path),ct).ConfigureAwait(false);using var indexDoc=indexed.Document;
            if(!indexDoc.RootElement.TryGetProperty("weight_map",out var map)||map.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Weight index has no file map.");
            var required=map.EnumerateObject().Select(p=>HubFormat.Relative(p.Value.GetString()??"")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(required.Length==0||required.Any(p=>p.Contains('/')||!p.EndsWith(".safetensors",StringComparison.Ordinal)||!files.Any(f=>f.Path==p)))throw new InvalidDataException("Safetensors index references missing or unsupported weight files.");
            variants=variants.Select(v=>v.Format!="Safetensors"?v:v with{Files=v.Files.Where(f=>!f.Path.EndsWith(".safetensors",StringComparison.OrdinalIgnoreCase)||required.Contains(f.Path)).ToArray()}).ToArray();
        }
        return new(id,revision,string.IsNullOrEmpty(license)?"Not declared · review model card":license,gated,variants);
    }
    public static HubVariant[] Variants(IEnumerable<HubFile> source)
    {
        var files = source.ToArray(); var variants = new List<HubVariant>();
        var ggufs = files.Where(f => f.Path.EndsWith(".gguf",StringComparison.OrdinalIgnoreCase)).ToArray();
        static bool IsProjector(HubFile f) => System.IO.Path.GetFileName(f.Path).Contains("mmproj", StringComparison.OrdinalIgnoreCase);
        var projectors = ggufs.Where(IsProjector).ToArray();
        if (projectors.Length > 0 && ggufs.Any(f => !IsProjector(f))) ggufs = ggufs.Where(f => !IsProjector(f)).Concat(projectors.OrderBy(p => System.IO.Path.GetFileNameWithoutExtension(p.Path).EndsWith("-F16", StringComparison.OrdinalIgnoreCase) ? 0 : System.IO.Path.GetFileNameWithoutExtension(p.Path).EndsWith("-BF16", StringComparison.OrdinalIgnoreCase) ? 1 : 2).Take(1)).ToArray();
        // Vision models need their projector beside the weights; prefer an exact name match, then F16, BF16, F32 in the same folder.
        HubFile? Projector(HubFile weights)
        {
            string folder = weights.Path.Contains('/') ? weights.Path[..weights.Path.LastIndexOf('/')] : "";
            var near = projectors.Where(p => (p.Path.Contains('/') ? p.Path[..p.Path.LastIndexOf('/')] : "") == folder).ToArray();
            return near.FirstOrDefault(p => System.IO.Path.GetFileName(p.Path).Equals("mmproj-" + System.IO.Path.GetFileName(weights.Path), StringComparison.OrdinalIgnoreCase))
                ?? new[] { "F16", "BF16", "F32" }.Select(precision => near.FirstOrDefault(p => System.IO.Path.GetFileNameWithoutExtension(p.Path).EndsWith("-" + precision, StringComparison.OrdinalIgnoreCase))).OfType<HubFile>().FirstOrDefault()
                ?? near.OrderBy(p => p.Size).FirstOrDefault();
        }
        foreach(var group in ggufs.GroupBy(f=>(Name:Regex.Replace(f.Path,@"-\d{5}-of-\d{5}\.gguf$",".gguf",RegexOptions.IgnoreCase),Split:Regex.IsMatch(f.Path,@"-\d{5}-of-\d{5}\.gguf$",RegexOptions.IgnoreCase))))
        {
            var ordered=group.OrderBy(f=>f.Path,StringComparer.Ordinal).ToArray(); var split=Regex.Match(ordered[0].Path,@"-(\d{5})-of-(\d{5})\.gguf$",RegexOptions.IgnoreCase);
            if(split.Success && (int.Parse(split.Groups[2].Value)!=ordered.Length || ordered.Where((f,i)=>!f.Path.EndsWith($"-{i+1:00000}-of-{ordered.Length:00000}.gguf",StringComparison.OrdinalIgnoreCase)).Any())) continue;
            var role = ModelCompatibility.ClassifyGguf(null, System.IO.Path.GetFileName(ordered[0].Path));
            var projector = role is AgentRole.Implementer ? Projector(ordered[0]) : null;
            string note = role switch
            {
                AgentRole.Projector => "Vision companion file. It is bundled with each chat variant; download it alone only to add vision to weights you already have.",
                AgentRole.Draft => "Draft model for speculative decoding. Start the main model with this as its draft for faster agent turns.",
                AgentRole.CodeSearch => "Embeddings model · powers the agent's semantic code search.",
                AgentRole.Reranker => "Reranker · reorders the agent's code search results.",
                AgentRole.Adapter => "LoRA adapter · loads on top of its base model.",
                _ => split.Success ? "Every shard is included; llama.cpp loads them from the first file." : "Runs in llama.cpp" + (projector != null ? " with image input (projector " + System.IO.Path.GetFileName(projector.Path) + " included)." : ".")
            };
            variants.Add(new(group.Key.Name + (split.Success ? " (split)" : "") + (projector == null ? "" : " + vision"), "GGUF", projector == null ? ordered : [.. ordered, projector], note) { Role = projector != null ? AgentRole.Vision : role });
        }
        // Only coherent root bundles are offered; custom code and unrelated binary formats are never executed or fetched.
        var rootWeights=files.Where(f=>!f.Path.Contains('/')&&f.Path.EndsWith(".safetensors",StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Any(f=>f.Path=="config.json") && rootWeights.Length>0 && (rootWeights.Length==1&&!Regex.IsMatch(rootWeights[0].Path,@"-\d+-of-\d+\.safetensors$")||files.Any(f=>f.Path=="model.safetensors.index.json")))
        {
            var bundle=files.Where(f=>!f.Path.StartsWith(".git") && ((!f.Path.Contains('/')&&new[]{".safetensors",".json",".model",".txt",".tiktoken"}.Any(ext=>f.Path.EndsWith(ext,StringComparison.OrdinalIgnoreCase)))||Regex.IsMatch(f.Path,@"^\d+_[A-Za-z]+/config\.json$")||f.Path is "README.md" or "LICENSE" or "LICENSE.md")).ToArray();
            variants.Add(new("Safetensors bundle","Safetensors",bundle,"Weights, tokenizer and configuration. Architecture support is checked locally; no repository Python code is run."));
        }
        return variants.OrderBy(v=>v.Bytes).ToArray();
    }
    public void Dispose()=>client.Dispose();
}
