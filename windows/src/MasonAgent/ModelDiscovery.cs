using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason;

public sealed record ModelLocation(string Name, string Path, bool Custom = false)
{
    public override string ToString() => $"{Name} · {Path}";
}
public sealed record LocalModel(string Name, string Path, string Source, string Format, string Task, long Bytes, string Support)
{
    public string? AdapterPath {get;init;}
    /// <summary>GGUF general.architecture or Transformers model_type.</summary>
    public string Architecture {get;init;}="";
    AgentRole? role;
    /// <summary>The job this file can do for the agent. Older saved launch recipes predate roles.</summary>
    public AgentRole Role {get=>role??Task switch{"Chat"=>AgentRole.Implementer,"Embeddings"=>AgentRole.CodeSearch,"Reranking"=>AgentRole.Reranker,"Draft"=>AgentRole.Draft,"Adapter"=>AgentRole.Adapter,"Projector"=>AgentRole.Projector,_=>AgentRole.NotUsed};init=>role=value;}
    /// <summary>Tasks Mason can serve on its own: chat for agent roles, plus embeddings and reranking for code search.</summary>
    public bool Launchable => Task is "Chat" or "Embeddings" or "Reranking";
    public static string TaskFor(AgentRole role) => role switch { AgentRole.Implementer or AgentRole.Vision => "Chat", AgentRole.CodeSearch => "Embeddings", AgentRole.Reranker => "Reranking", AgentRole.Draft => "Draft", AgentRole.Adapter => "Adapter", AgentRole.Projector => "Projector", _ => "Inventory only" };
    public string Size => Bytes >= 1L << 30 ? $"{Bytes / (double)(1L << 30):0.0} GiB" : Bytes>=1L<<20?$"{Bytes / 1048576d:0.0} MiB":$"{Bytes:N0} B";
    public override string ToString() => $"{Name}\n{Task} · {Format} · {Size} · {Source}\n{Support}";
}
public sealed record ModelInventory(LocalModel[] Models, string[] Notes, int FilesSeen, bool Limited, DateTimeOffset At)
{
    public bool RedirectionBlocked { get; init; }
}
public sealed record ModelLibrarySettings(List<ModelLocation> Locations, string LlamaServer, string Python, bool IncludeCommon = true, string QuantizedPython = "", bool EmbedGguf = true)
{
    public static ModelLibrarySettings? ReadSaved(string path)
    {
        // File.Exists also returns false for access errors. Only a genuinely
        // missing settings file is allowed to trigger initial discovery.
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length>1024*1024)throw new InvalidDataException("Model settings exceed 1 MiB.");
            return JsonSerializer.Deserialize<ModelLibrarySettings>(stream)??throw new InvalidDataException("Model settings contain null.");
        }
        catch(FileNotFoundException){return null;}
        catch(DirectoryNotFoundException){return null;}
    }
    // Runtime edits affect only the explicitly selected field. Folder saves must not
    // overwrite a runtime selection made after this view was constructed.
    public ModelLibrarySettings MergeRuntimeSelection(ModelLibrarySettings saved, int edited = -1) => this with
    {
        LlamaServer=edited==0?LlamaServer:saved.LlamaServer,
        Python=edited==1?Python:saved.Python,
        QuantizedPython=edited==2?QuantizedPython:saved.QuantizedPython
    };
}

/// <summary>Read-only inventory. No imports, model loading, downloads, or directory-link traversal.</summary>
public static partial class ModelDiscovery
{
    /// <summary>Unverified partial downloads live here and are never inventoried.</summary>
    public const string StagingFolder = ".mason-downloads";
    // Staging name used by earlier releases; still skipped by scans and reused by downloads already in it.
    public const string LegacyStagingFolder = ".db-cowboy-downloads";
    public static bool IsStagingFolder(string name) => name.Equals(StagingFolder,StringComparison.OrdinalIgnoreCase) || name.Equals(LegacyStagingFolder,StringComparison.OrdinalIgnoreCase);
    public static List<ModelLocation> CommonLocations()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<ModelLocation>();
        void Add(string name, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { roots.Add(new(name,System.IO.Path.GetFullPath(path))); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { roots.Add(new(name + " (invalid path)",path)); }
        }
        Add("Hugging Face", Environment.GetEnvironmentVariable("HF_HUB_CACHE"));
        Add("Hugging Face (legacy variable)", Environment.GetEnvironmentVariable("HUGGINGFACE_HUB_CACHE"));
        Add("Hugging Face home", System.IO.Path.Combine(Environment.GetEnvironmentVariable("HF_HOME") ?? System.IO.Path.Combine(home, ".cache", "huggingface"), "hub"));
        Add("Ollama", Environment.GetEnvironmentVariable("OLLAMA_MODELS") ?? System.IO.Path.Combine(home, ".ollama", "models"));
        Add("llama.cpp", Environment.GetEnvironmentVariable("LLAMA_CACHE") ?? System.IO.Path.Combine(local, "llama.cpp"));
        Add("llama.cpp user cache", System.IO.Path.Combine(home, ".cache", "llama.cpp"));
        Add("Unsloth", System.IO.Path.Combine(home, ".unsloth", "models"));
        Add("Unsloth Studio", System.IO.Path.Combine(home, ".unsloth", "studio", "models"));
        Add("ComfyUI", System.IO.Path.Combine(home, "ComfyUI", "models"));
        Add("ComfyUI Documents", System.IO.Path.Combine(home, "Documents", "ComfyUI", "models"));
        Add("ComfyUI portable", System.IO.Path.Combine(home, "ComfyUI_windows_portable", "ComfyUI", "models"));
        return roots.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ModelInventory Scan(IEnumerable<ModelLocation> locations, CancellationToken ct, int maxEntries = 60000)
    {
        if (maxEntries is < 1 or > 100000) throw new ArgumentException("Invalid scan budget.");
        var models = new Dictionary<string, LocalModel>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>(); int count = 0; var roots = locations.Take(65).ToArray(); bool limited = roots.Length > 64; bool redirectionBlocked = false;
        foreach (var location in roots.Take(64))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(location.Path)) continue;
            var pending = new Stack<(string Path, int Depth)>(); pending.Push((System.IO.Path.GetFullPath(location.Path), 0));
            while (pending.TryPop(out var current))
            {
                ct.ThrowIfCancellationRequested();
                if (current.Path.Split(System.IO.Path.DirectorySeparatorChar).Any(IsStagingFolder) || !visited.Add(current.Path)) continue;
                if (count >= maxEntries) { limited = true; break; }
                if (current.Depth > 14) { limited = true; continue; }
                try
                {
                    if ((File.GetAttributes(current.Path) & FileAttributes.ReparsePoint) != 0) { if (notes.Count < 100) notes.Add("Skipped directory link: " + current.Path); continue; }
                    foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
                    {
                        ct.ThrowIfCancellationRequested(); if (++count > maxEntries) { limited = true; break; }
                        try
                        {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var name = System.IO.Path.GetFileName(entry);
                            if ((attributes & FileAttributes.ReparsePoint) == 0 && name is not (".git" or "node_modules" or "venv" or ".venv" or "Lib" or "Scripts" or "__pycache__" or ".locks" or ".no_exist" or "blobs") && !IsStagingFolder(name)) pending.Push((entry, current.Depth + 1));
                            continue;
                        }
                        string ext = System.IO.Path.GetExtension(entry).ToLowerInvariant();
                        if (ext == ".gguf") AddGguf(entry, location.Name, System.IO.Path.GetFileNameWithoutExtension(entry));
                        else if (System.IO.Path.GetFileName(entry) == "config.json") AddTransformers(current.Path, location.Name);
                        else if (System.IO.Path.GetFileName(entry) == "adapter_config.json") AddAdapter(current.Path, location.Name);
                        else if (ext is ".safetensors" or ".ckpt" or ".pt" or ".pth" && !File.Exists(System.IO.Path.Combine(current.Path, "config.json")) && !File.Exists(System.IO.Path.Combine(current.Path, "adapter_config.json")))
                            models.TryAdd(entry, new(System.IO.Path.GetFileName(entry), entry, location.Name, ext[1..], "Inventory only", FileLength(entry), "Checkpoint without a model configuration, such as image-generation weights. These don't take part in coding work.") { Role = AgentRole.NotUsed });
                        else if (entry.Contains(System.IO.Path.DirectorySeparatorChar + "manifests" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) AddOllama(entry, location.Path);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                        { redirectionBlocked |= IsRedirectionBlocked(e); if (notes.Count < 100) notes.Add("Skipped unreadable entry: " + entry + " · " + e.Message); }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { redirectionBlocked |= IsRedirectionBlocked(e); if (notes.Count < 100) notes.Add("Skipped unreadable location: " + current.Path + " · " + e.Message); }
            }
            if (limited && count >= maxEntries) break;
        }
        return new(models.Values.OrderByDescending(x => x.Launchable).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(2000).ToArray(),
            notes.Take(100).ToArray(), count, limited || models.Count > 2000, DateTimeOffset.UtcNow) { RedirectionBlocked = redirectionBlocked };

        void AddGguf(string path, string source, string name)
        {
            using var stream = File.OpenRead(path); Span<byte> magic = stackalloc byte[4];
            if (stream.Read(magic) != 4 || !magic.SequenceEqual("GGUF"u8)) return;
            var resolved = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
            var split = SplitPattern().Match(name);
            // llama.cpp loads every shard from the first one; later shards are part of that entry.
            if (split.Success && split.Groups[1].Value != "00001") return;
            long bytes = stream.Length;
            if (split.Success)
            {
                var folder = System.IO.Path.GetDirectoryName(path)!; var stem = name[..^split.Length];
                var shards = Directory.EnumerateFiles(folder, stem + "-*-of-" + split.Groups[2].Value + ".gguf").ToArray();
                if (shards.Length != int.Parse(split.Groups[2].Value)) { models.TryAdd(resolved, new(stem, path, source, "GGUF", "Inventory only", bytes, $"Needs local files: {shards.Length} of {int.Parse(split.Groups[2].Value)} shards are present.") { Role = AgentRole.Implementer }); return; }
                bytes = shards.Sum(FileLength); name = stem;
            }
            stream.Position = 0; GgufHeader? header; try { header = GgufHeader.Read(stream); } catch (InvalidDataException) { header = null; }
            var role = ModelCompatibility.ClassifyGguf(header, System.IO.Path.GetFileName(path));
            if (role == AgentRole.Implementer && System.IO.Path.GetDirectoryName(path) is { } dir && Directory.EnumerateFiles(dir, "*mmproj*.gguf").Any()) role = AgentRole.Vision;
            string support = role switch
            {
                AgentRole.CodeSearch => "Embeddings model · serve it to power the agent's semantic code search.",
                AgentRole.Reranker => "Reranker · serve it to reorder the agent's code search results.",
                AgentRole.Draft => "Draft model · choose it when starting a larger model of the same family for faster turns.",
                AgentRole.Adapter => "LoRA adapter · choose it when starting its base model.",
                AgentRole.Projector => "Vision projector · loaded automatically with a chat model in this folder.",
                AgentRole.Vision => "Chat with image input · llama.cpp loads the projector in this folder.",
                _ => split.Success ? "Split GGUF · llama.cpp loads all shards from the first file." : "Chat model · runs in llama.cpp."
            };
            models.TryAdd(resolved, new(name, path, source, "GGUF", LocalModel.TaskFor(role), bytes, support) { Role = role, Architecture = header?.Architecture ?? "" });
        }
        void AddTransformers(string folder, string source)
        {
            var configPath = System.IO.Path.Combine(folder, "config.json");
            if (FileLength(configPath) > 262144) return;
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = config.RootElement; string type = root.TryGetProperty("model_type", out var t) ? t.GetString() ?? "unknown" : "unknown";
            var files = Directory.EnumerateFiles(folder, "*.safetensors").Take(257).ToArray(); if (files.Length == 0) return;
            string name = new DirectoryInfo(folder).Name;
            var parent = Directory.GetParent(folder);
            if (name.Length == 40 && name.All(Uri.IsHexDigit) && parent?.Name != "snapshots") name = parent?.Name ?? name;
            if (parent?.Name == "snapshots") name = parent.Parent?.Name.Replace("models--", "").Replace("--", "/") ?? name;
            string task = "Inventory only", support = "Transformers weights · this architecture has no managed serving adapter";
            var architectureNames = root.TryGetProperty("architectures", out var archs) && archs.ValueKind == JsonValueKind.Array ? string.Join(" ", archs.EnumerateArray().Select(x => x.GetString())) : "";
            var role = architectureNames.Contains("ForCausalLM") || architectureNames.Contains("ForConditionalGeneration") || architectureNames.Contains("LMHeadModel") ? AgentRole.Implementer
                : System.Text.RegularExpressions.Regex.IsMatch(name, "(?i)rerank") || architectureNames.Contains("ForSequenceClassification") ? AgentRole.Reranker
                : architectureNames.Contains("BertModel") || architectureNames.Contains("RobertaModel") || System.Text.RegularExpressions.Regex.IsMatch(name, "(?i)embed") ? AgentRole.CodeSearch
                : architectureNames.Length > 0 ? AgentRole.NotUsed : AgentRole.Unknown;
            if (role == AgentRole.Implementer && System.Text.RegularExpressions.Regex.IsMatch(name, "(?i)embed")) role = AgentRole.CodeSearch;
            if(role==AgentRole.Implementer && ModelAdapters.TextSupport(folder,root) is string textSupport)
            { support=textSupport; if(textSupport.StartsWith("Python ·"))task="Chat"; }
            else support = role switch
            {
                AgentRole.CodeSearch or AgentRole.Reranker => "Mason serves embeddings and rerankers through llama.cpp. Download a GGUF build of this model.",
                AgentRole.Implementer => $"No local Transformers adapter for “{type}”. Download a GGUF build of this model to run it in llama.cpp.",
                AgentRole.NotUsed => $"{type} ({architectureNames}) isn't a chat, embedding or reranking model, so the agent doesn't use it.",
                _ => "The configuration doesn't declare an architecture."
            };
            var quantization=ModelAdapters.Quantization(root);
            var format="safetensors · " + type+(quantization.StartsWith("GPTQ") || quantization.StartsWith("AWQ")?" · "+quantization:"");
            models.TryAdd(folder, new(name, folder, source, format, task, files.Sum(FileLength), support) { Role = role, Architecture = type });
        }
        void AddAdapter(string folder, string source)
        {
            var configPath = System.IO.Path.Combine(folder, "adapter_config.json");
            if (FileLength(configPath) > 262144) return;
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            var baseModel = config.RootElement.TryGetProperty("base_model_name_or_path", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            var weights = Directory.EnumerateFiles(folder, "adapter_model.*").ToArray(); if (weights.Length == 0) return;
            models.TryAdd(folder, new(new DirectoryInfo(folder).Name, folder, source, "LoRA · safetensors", "Adapter", weights.Sum(FileLength),
                "LoRA adapter" + (baseModel.Length > 0 ? " for " + baseModel : "") + " · choose it when starting the base model with the Transformers runtime.") { Role = AgentRole.Adapter });
        }
        void AddOllama(string manifestPath, string root)
        {
            if (FileLength(manifestPath) > 262144) return;
            try
            {
                for (var ancestor = Directory.GetParent(manifestPath); ancestor != null; ancestor = ancestor.Parent)
                {
                    if (Directory.Exists(System.IO.Path.Combine(ancestor.FullName,"blobs")) && Directory.Exists(System.IO.Path.Combine(ancestor.FullName,"manifests"))) { root = ancestor.FullName; break; }
                    if (ancestor.FullName.Equals(System.IO.Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase)) break;
                }
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!document.RootElement.TryGetProperty("layers", out var layers)) return;
                var entries = layers.EnumerateArray().Take(65).ToArray();
                if (entries.Length > 64) return;
                bool companion = entries.Any(x => x.TryGetProperty("mediaType", out var media) && (media.GetString()?.Contains("adapter", StringComparison.OrdinalIgnoreCase) == true || media.GetString()?.Contains("projector", StringComparison.OrdinalIgnoreCase) == true));
                foreach (var layer in entries)
                {
                    if (layer.GetProperty("mediaType").GetString() != "application/vnd.ollama.image.model") continue;
                    var digest = layer.GetProperty("digest").GetString() ?? "";
                    if (!DigestPattern().IsMatch(digest)) continue;
                    var blob = System.IO.Path.Combine(root, "blobs", digest.Replace(':', '-'));
                    if (File.Exists(blob))
                    {
                        var name = System.IO.Path.GetRelativePath(root, manifestPath).Replace("manifests" + System.IO.Path.DirectorySeparatorChar, "");
                        if (companion)
                        {
                            using var file = File.OpenRead(blob); Span<byte> header = new byte[4];
                            if (file.Read(header) == 4 && header.SequenceEqual("GGUF"u8))
                                models.TryAdd(manifestPath,new(name,blob,"Ollama","GGUF + companion","Inventory only",file.Length,"Ollama bundle with an adapter or projector. Mason doesn't rebuild Ollama bundles; connect to Ollama under Connections to use it.") { Role = AgentRole.Vision });
                        }
                        else
                        {
                            AddGguf(blob, "Ollama", name);
                            var resolved = new FileInfo(blob).ResolveLinkTarget(true)?.FullName ?? blob;
                            if (models.TryGetValue(resolved, out var found)) models[resolved] = found with {
                                Support = found.Support + " · uses GGUF settings, not Ollama template/parameter overrides" };
                        }
                    }
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
    }
    // ERROR_UNTRUSTED_MOUNT_POINT (448); classify by code, independent of OS language.
    public static bool IsRedirectionBlocked(Exception error) => error is IOException && error.HResult == unchecked((int)0x800701C0);
    static long FileLength(string path)
    {
        var file = new FileInfo(path); return (file.ResolveLinkTarget(true) as FileInfo ?? file).Length;
    }
    [GeneratedRegex(@"-(\d{5})-of-(\d{5})$")] private static partial Regex SplitPattern();
    [GeneratedRegex(@"^sha256:[0-9a-f]{64}$")] private static partial Regex DigestPattern();
}

