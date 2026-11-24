using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason;

public sealed record HubModel(string Id, string Task, string[] Tags, long Downloads, string Updated)
{
    public string Name => Id.Split('/').Last();
    public string Publisher => Id.Contains('/') ? Id.Split('/')[0] : "";
    public string Format => Tags.Contains("gguf") ? "GGUF" : Tags.Contains("safetensors") ? "Safetensors" : "Inspect files";
    public string Capability => Task switch { "text-generation" => "Chat", "sentence-similarity" or "feature-extraction" => "Embeddings", "text-ranking" => "Reranking", _ => string.IsNullOrEmpty(Task) ? "Inspect model card" : Task };
}
public sealed record HubFile(string Path, long Size, string Hash, bool Lfs);
public sealed record HubVariant(string Name, string Format, HubFile[] Files, string Note)
{
    public long Bytes => Files.Sum(f => f.Size);
    public override string ToString() => Name + " · " + HubFormat.Size(Bytes);
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
    public async Task<HubModel[]> Search(string query, string publisher, string format, CancellationToken ct)
    {
        query=query.Trim();
        if(Regex.IsMatch(query,"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
        {
            HubFormat.Repo(query);var direct=await Json(new Uri("https://huggingface.co/api/models/"+query),ct).ConfigureAwait(false);using var doc=direct.Document;
            var entry=ReadModel(doc.RootElement);return (publisher!="Unsloth"||entry.Publisher.Equals("unsloth",StringComparison.OrdinalIgnoreCase))&&(format is not("GGUF" or "Safetensors")||entry.Format==format)?[entry]:[];
        }
        var url = "https://huggingface.co/api/models?limit=40&full=true&sort=downloads&direction=-1&search=" + Uri.EscapeDataString(query.Trim());
        if (publisher == "Unsloth") url += "&author=unsloth";
        if (format is "GGUF" or "Safetensors") url += "&filter=" + format.ToLowerInvariant();
        var result = await Json(new Uri(url), ct).ConfigureAwait(false); using var document = result.Document;
        return document.RootElement.EnumerateArray().Select(ReadModel).Where(m => m.Id.Contains('/')).ToArray();
    }
    static HubModel ReadModel(JsonElement m)=>new(m.GetProperty("id").GetString()!,String(m,"pipeline_tag"),m.TryGetProperty("tags",out var tags)?tags.EnumerateArray().Select(t=>t.GetString()??"").ToArray():[],m.TryGetProperty("downloads",out var d)?d.GetInt64():0,String(m,"lastModified"));
    static string String(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
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
        var ggufs = files.Where(f => f.Path.EndsWith(".gguf",StringComparison.OrdinalIgnoreCase));
        foreach(var group in ggufs.GroupBy(f=>(Name:Regex.Replace(f.Path,@"-\d{5}-of-\d{5}\.gguf$",".gguf",RegexOptions.IgnoreCase),Split:Regex.IsMatch(f.Path,@"-\d{5}-of-\d{5}\.gguf$",RegexOptions.IgnoreCase))))
        {
            var ordered=group.OrderBy(f=>f.Path,StringComparer.Ordinal).ToArray(); var split=Regex.Match(ordered[0].Path,@"-(\d{5})-of-(\d{5})\.gguf$",RegexOptions.IgnoreCase);
            if(split.Success && (int.Parse(split.Groups[2].Value)!=ordered.Length || ordered.Where((f,i)=>!f.Path.EndsWith($"-{i+1:00000}-of-{ordered.Length:00000}.gguf",StringComparison.OrdinalIgnoreCase)).Any())) continue;
            variants.Add(new(group.Key.Name+(split.Success?" (split)":""),"GGUF",ordered,split.Success?"Includes every shard. Split GGUF is inventory-only in the current local serving adapter.":group.Key.Name.Contains("mmproj",StringComparison.OrdinalIgnoreCase)?"Projection companion file; not a standalone chat model.":"llama.cpp · compatibility checked on start"));
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
