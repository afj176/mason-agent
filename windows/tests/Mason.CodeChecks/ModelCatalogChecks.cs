using System.Text;
using System.Text.Json;
using Mason;
using Mason.Code;

/// <summary>Model discovery, compatibility verdicts, runtime inspection and the agent's semantic search. No network or GPU.</summary>
internal static class ModelCatalogChecks
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        // Every Hub result is classified by the job it can do for the agent; nothing is filtered out.
        HubModel Hub(string id, string task, string[] tags, string library = "", string architecture = "") => new(id, task, tags, 1, "") { Library = library, Architecture = architecture };
        var inkling = Hub("thinkingmachines/Inkling-2-Mini", "image-text-to-text", ["safetensors", "conversational"], "transformers", "inkling2_loop");
        var inklingGguf = Hub("unsloth/Inkling-2-Mini-GGUF", "image-text-to-text", ["gguf"], "", "inkling2_loop");
        var embedder = Hub("Qwen/Qwen3-Embedding-0.6B", "feature-extraction", ["safetensors"], "sentence-transformers", "qwen3");
        var reranker = Hub("ggml-org/Qwen3-Reranker-0.6B-GGUF", "text-ranking", ["gguf"], "", "qwen3");
        var image = Hub("black-forest-labs/FLUX.1-dev", "text-to-image", ["diffusers"], "diffusers");
        var untagged = Hub("lmstudio-community/Inkling-2-Mini-GGUF", "", ["gguf"]);
        var lora = Hub("someone/coder-lora", "text-generation", ["peft", "lora"], "peft");
        var mlx = Hub("mlx-community/Inkling-2-Mini-4bit", "image-text-to-text", ["mlx", "safetensors"], "mlx");
        check(inkling.Role == AgentRole.Vision && embedder.Role == AgentRole.CodeSearch && reranker.Role == AgentRole.Reranker && image.Role == AgentRole.NotUsed && untagged.Role == AgentRole.Implementer && lora.Role == AgentRole.Adapter,
            "Hub repositories are classified as coding, vision, code search, reranker, adapter or not used by the agent");

        var old = new RuntimeFacts(true, 12347, new HashSet<string> { "llama", "qwen2", "qwen3" }, true, false, ["qwen3", "llama"]);
        var current = old with { LlamaBuild = 12716, LlamaArchitectures = new HashSet<string> { "llama", "qwen2", "qwen3", "inkling2_loop" } };
        var stale = ModelCompatibility.Assess(inklingGguf, old);
        check(stale.Readiness == ModelReadiness.NeedsRuntimeUpdate && stale.Reason.Contains("b12347") && stale.Reason.Contains("inkling2_loop") && ModelCompatibility.Assess(inklingGguf, current).Readiness == ModelReadiness.Ready,
            "a GGUF whose architecture the selected llama.cpp build lacks asks for an update instead of failing at load");
        check(ModelCompatibility.Assess(inkling, current).Readiness == ModelReadiness.UseGgufBuild && ModelCompatibility.Assess(embedder, current).Readiness == ModelReadiness.UseGgufBuild &&
            ModelCompatibility.Assess(mlx, current).Readiness == ModelReadiness.NotSupported && ModelCompatibility.Assess(image, current).Badge == "Not for the agent" &&
            ModelCompatibility.Assess(Hub("Qwen/Qwen3-4B", "text-generation", ["safetensors"], "transformers", "qwen3"), current).Readiness == ModelReadiness.Ready &&
            ModelCompatibility.Assess(inklingGguf, RuntimeFacts.None).Readiness == ModelReadiness.NeedsRuntime,
            "verdicts explain unsupported Safetensors, MLX, non-agent models and a missing runtime with a next step");

        // Variants: quality hints, bundled projector, draft files and a recommendation that fits the GPU.
        var files = new[] { ("Inkling-2-Mini-UD-Q2_K_XL.gguf", 9L), ("Inkling-2-Mini-UD-Q4_K_XL.gguf", 17L), ("Inkling-2-Mini-UD-Q8_K_XL.gguf", 22L), ("mmproj-BF16.gguf", 1L), ("mmproj-F16.gguf", 1L), ("dspark-Inkling-2-Mini-Q8_0.gguf", 1L) }
            .Select(f => new HubFile(f.Item1, f.Item2 << 30, "", true)).ToArray();
        var variants = HubClient.Variants(files);
        var q4 = variants.Single(v => v.Name.StartsWith("Inkling-2-Mini-UD-Q4_K_XL"));
        check(q4.Role == AgentRole.Vision && q4.Files.Any(f => f.Path == "mmproj-F16.gguf") && variants.Single(v => v.Name.StartsWith("dspark")).Role == AgentRole.Draft &&
            variants.Count(v => v.Role == AgentRole.Projector) == 1 && q4.Quality == "balanced" && ModelCompatibility.QuantQuality("x-Q8_0.gguf") == "near-lossless",
            "GGUF variants carry their projector, mark draft files and describe quantization quality");
        check(ModelCompatibility.Recommend(variants, 24, 64)?.Name == q4.Name && ModelCompatibility.Recommend(variants, 12, 32)?.Name.Contains("Q2_K_XL") == false &&
            ModelCompatibility.Fit(17L << 30, 24, 22, 64) == "Fits on GPU" && ModelCompatibility.Fit(22L << 30, 12, 11, 32) == "Partial GPU offload · slower" && ModelCompatibility.Fit(80L << 30, 12, 11, 16) == "Too large for this PC",
            "the recommended variant is the best quality that fits the GPU, and fit is stated before download");

        // Local GGUF classification comes from the file header, not just its name.
        byte[] Gguf(params (string Key, object Value)[] entries)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Encoding.UTF8);
            w.Write(0x46554747u); w.Write(3u); w.Write(1UL); w.Write((ulong)entries.Length);
            void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
            foreach (var (key, value) in entries) { Str(key); if (value is string s) { w.Write(8u); Str(s); } else { w.Write(4u); w.Write(Convert.ToUInt32(value)); } }
            return stream.ToArray();
        }
        var folder = Directory.CreateDirectory(Path.Combine(root, "catalog-" + Guid.NewGuid().ToString("N"))).FullName;
        ModelLadderChecks.Run(Directory.CreateDirectory(Path.Combine(root, "ladder-" + Guid.NewGuid().ToString("N"))).FullName, check);
        File.WriteAllBytes(Path.Combine(folder, "code-model.gguf"), Gguf(("general.architecture", "bert"), ("bert.pooling_type", 1u)));
        File.WriteAllBytes(Path.Combine(folder, "ranker.gguf"), Gguf(("general.architecture", "qwen3"), ("qwen3.pooling_type", 4u)));
        File.WriteAllBytes(Path.Combine(folder, "tune.gguf"), Gguf(("general.architecture", "llama"), ("general.type", "adapter"), ("adapter.type", "lora")));
        File.WriteAllBytes(Path.Combine(folder, "chat-00001-of-00002.gguf"), Gguf(("general.architecture", "inkling2_loop")));
        File.WriteAllBytes(Path.Combine(folder, "chat-00002-of-00002.gguf"), [0, 1, 2]);
        File.WriteAllBytes(Path.Combine(folder, "sd-v1.ckpt"), [1, 2, 3]);
        var scan = ModelDiscovery.Scan([new("Checks", folder)], default);
        LocalModel Named(string prefix) => scan.Models.Single(m => Path.GetFileName(m.Path).StartsWith(prefix));
        check(Named("code-model").Role == AgentRole.CodeSearch && Named("code-model").Task == "Embeddings" && Named("ranker").Role == AgentRole.Reranker && Named("tune").Role == AgentRole.Adapter &&
            Named("chat-00001").Launchable && Named("chat-00001").Architecture == "inkling2_loop" && scan.Models.Count(m => m.Path.Contains("chat-0000")) == 1 && Named("sd-v1").Role == AgentRole.NotUsed && scan.Models.Length == 5,
            "local discovery keeps every file, reads roles from GGUF headers and loads split GGUFs from their first shard");
        check(ModelCompatibility.Assess(Named("chat-00001"), old).Readiness == ModelReadiness.NeedsRuntimeUpdate && ModelCompatibility.Assess(Named("code-model"), old with { LlamaArchitectures = new HashSet<string> { "llama", "qwen2", "bert" } }).Readiness == ModelReadiness.Ready,
            "local verdicts use the architecture each file declares");

        // llama.cpp: architecture names from the library's string table; releases verified by digest.
        // Recent builds compile short names into code as 8-byte immediates, splitting longer names around opcodes.
        var library = Encoding.ASCII.GetBytes("\0\0llama\0qwen2\0hrm_text\0").Concat(new byte[] { 0x48, 0xB8 }).Concat(Encoding.ASCII.GetBytes("inkling2"))
            .Concat(new byte[] { 0x48, 0x89, 0x45, 0xF8, 0xC7, 0x45 }).Concat(Encoding.ASCII.GetBytes("_loop")).ToArray();
        var names = LlamaRuntime.Architectures(library);
        check(names.Contains("inkling2_loop") && names.Contains("qwen2") && !names.Contains("inkling3_loop") && !names.Contains("made-up") && names.Count == 1 && LlamaRuntime.Architectures(Encoding.ASCII.GetBytes("\0\0foo\0")).Count == 0,
            "architecture support is read from the llama.cpp library, including names compiled into code as split immediates");
        string Asset(string name) => $$"""{"name":"{{name}}","size":10,"digest":"sha256:{{new string('a', 64)}}","browser_download_url":"https://github.com/ggml-org/llama.cpp/releases/download/b12716/{{name}}"}""";
        using var releases = JsonDocument.Parse($$"""[{"tag_name":"v1.0.0","assets":[]},{"tag_name":"b12716","published_at":"2026-11-25T04:27:39Z","assets":[{{Asset("llama-b12716-bin-win-cuda-13.5-x64.zip")}},{{Asset("cudart-llama-bin-win-cuda-13.5-x64.zip")}},{{Asset("llama-b12716-bin-win-vulkan-x64.zip")}},{"name":"evil.zip","size":1,"digest":"sha256:{{new string('b', 64)}}","browser_download_url":"https://example.com/evil.zip"}]}]""");
        var release = LlamaRuntime.ParseReleases(releases.RootElement).Single();
        check(release.Build == 12716 && release.For("cuda-13.5")?.Length == 2 && release.For("vulkan")?.Length == 1 && release.For("cpu") == null && release.Assets.All(a => a.Url.Host == "github.com"),
            "llama.cpp updates come only from ggml-org release assets with published SHA-256 digests, with CUDA runtime libraries paired");

        // The agent's semantic search ranks code by meaning through the host's embeddings model.
        var project = Directory.CreateDirectory(Path.Combine(folder, "project")).FullName;
        File.WriteAllText(Path.Combine(project, "Billing.cs"), "class Billing {\n  // Calculates the invoice total including tax\n  decimal InvoiceTotal(decimal net, decimal taxRate) => net * (1 + taxRate);\n}\n");
        File.WriteAllText(Path.Combine(project, "Logger.cs"), "class Logger {\n  // Writes a diagnostic message to the console output\n  void Log(string message) => System.Console.WriteLine(message);\n}\n");
        var models = new WordModels();
        var result = await CodeSemanticIndex.Search(new CodeWorkspace(project), models, "where is the invoice tax total calculated", ".", 5, default);
        check(result.Results.Count == 2 && result.Results[0].Path == "Billing.cs" && result.Reranked && result.ChunksIndexed == 2,
            "semantic_search ranks the file that matches the meaning first and reranks when a reranker is connected");
        int calls = models.Calls; await CodeSemanticIndex.Search(new CodeWorkspace(project), models, "console logging", ".", 5, default);
        check(models.Calls == calls + 1, "unchanged files are not re-embedded; only the new query is");
        check(CodeSchema.NativeTools().EnumerateArray().Any(t => t.GetProperty("function").GetProperty("name").GetString() == "semantic_search"),
            "semantic_search is part of the native tool contract");
    }

    /// <summary>Deterministic bag-of-words embeddings and an overlap reranker.</summary>
    sealed class WordModels : ICodeSemanticModels
    {
        public int Calls;
        static readonly string[] Vocabulary = ["invoice", "tax", "total", "calculat", "console", "log", "message", "diagnostic"];
        public string EmbeddingIdentity => "words";
        public bool CanRerank => true;
        public Task<float[][]> Embed(IReadOnlyList<string> texts, CancellationToken ct)
        { Calls++; return Task.FromResult(texts.Select(t => Vocabulary.Select(w => (float)System.Text.RegularExpressions.Regex.Matches(t.ToLowerInvariant(), w).Count + .01f).ToArray()).ToArray()); }
        public Task<double[]> Rerank(string query, IReadOnlyList<string> documents, CancellationToken ct) =>
            Task.FromResult(documents.Select(d => (double)query.Split(' ').Count(w => w.Length > 3 && d.Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray());
    }
}
