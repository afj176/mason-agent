using System.Text.RegularExpressions;

namespace Mason;

/// <summary>The job a model can do for the coding agent. Every model is shown; this says what it is for.</summary>
public enum AgentRole { Implementer, Vision, CodeSearch, Reranker, Draft, Adapter, Projector, NotUsed, Unknown }
public enum ModelReadiness { Ready, NeedsRuntime, NeedsRuntimeUpdate, UseGgufBuild, NotSupported, CheckFiles }

/// <summary>Plain-language answer to “what can the agent do with this, and what is missing?”.</summary>
public sealed record ModelVerdict(AgentRole Role, ModelReadiness Readiness, string Reason, string Action = "")
{
    public string RoleLabel => Label(Role);
    public static string Label(AgentRole role) => role switch
    {
        AgentRole.Implementer => "Coding agent",
        AgentRole.Vision => "Coding agent · vision",
        AgentRole.CodeSearch => "Code search · embeddings",
        AgentRole.Reranker => "Code search · reranker",
        AgentRole.Draft => "Faster turns · draft model",
        AgentRole.Adapter => "Fine-tune · LoRA adapter",
        AgentRole.Projector => "Vision companion file",
        AgentRole.NotUsed => "Not used by the agent",
        _ => "Unclassified"
    };
    public static string Jobs(AgentRole role) => role switch
    {
        AgentRole.Implementer => "Implementer, Reviewer, Chat",
        AgentRole.Vision => "Visual verifier, Implementer, Reviewer, Chat",
        AgentRole.CodeSearch => "Semantic code search for the agent",
        AgentRole.Reranker => "Reorders code search results",
        AgentRole.Draft => "Speculative decoding for a larger model of the same family",
        AgentRole.Adapter => "Loaded on top of its base model",
        AgentRole.Projector => "Gives a GGUF chat model image input",
        _ => "None"
    };
    public string Badge => Readiness switch
    {
        ModelReadiness.Ready => "Ready",
        ModelReadiness.NeedsRuntime => "Needs runtime",
        ModelReadiness.NeedsRuntimeUpdate => "Update llama.cpp",
        ModelReadiness.UseGgufBuild => "Use a GGUF build",
        ModelReadiness.CheckFiles => "Check files",
        _ => Role == AgentRole.NotUsed ? "Not for the agent" : "Not supported"
    };
    /// <summary>True when Mason can run it for the agent now or after a runtime step it can explain.</summary>
    public bool Runnable => Readiness is ModelReadiness.Ready or ModelReadiness.NeedsRuntime or ModelReadiness.NeedsRuntimeUpdate or ModelReadiness.CheckFiles;
    public string Summary => Badge + " · " + Reason;
}

/// <summary>What the selected runtimes can load. Architectures come from the llama.cpp library itself.</summary>
public sealed record RuntimeFacts(bool LlamaServer, int? LlamaBuild, IReadOnlySet<string>? LlamaArchitectures, bool Python, bool QuantizedPython, IReadOnlyCollection<string> TransformersFamilies)
{
    public static readonly RuntimeFacts None = new(false, null, null, false, false, []);
    public bool? Loads(string architecture) => string.IsNullOrEmpty(architecture) || LlamaArchitectures == null ? null : LlamaArchitectures.Contains(architecture);
    public string BuildLabel => LlamaBuild is { } b ? "b" + b : "your build";
}

public static partial class ModelCompatibility
{
    static readonly HashSet<string> ChatTasks = ["text-generation", "text2text-generation", "image-text-to-text", "audio-text-to-text", "video-text-to-text", "any-to-any", "conversational"];
    static readonly HashSet<string> VisionTasks = ["image-text-to-text", "video-text-to-text", "any-to-any"];

    public static AgentRole Classify(string task, IReadOnlyCollection<string> tags, string id, string library = "")
    {
        var name = id[(id.LastIndexOf('/') + 1)..];
        if (library == "peft" || tags.Contains("lora") || tags.Contains("peft") || AdapterName().IsMatch(name)) return AgentRole.Adapter;
        if (task is "text-ranking" || RerankName().IsMatch(name)) return AgentRole.Reranker;
        if (task is "feature-extraction" or "sentence-similarity" || library == "sentence-transformers" && !ChatTasks.Contains(task) || EmbedName().IsMatch(name) && !ChatTasks.Contains(task)) return AgentRole.CodeSearch;
        if (DraftName().IsMatch(name)) return AgentRole.Draft;
        if (ChatTasks.Contains(task)) return VisionTasks.Contains(task) || tags.Contains("image-text-to-text") ? AgentRole.Vision : AgentRole.Implementer;
        if (task.Length > 0) return AgentRole.NotUsed;
        if (tags.Contains("gguf") || tags.Contains("conversational") || tags.Contains("text-generation-inference")) return AgentRole.Implementer;
        return AgentRole.Unknown;
    }

    /// <summary>Verdict for a Hub repository before anything is downloaded.</summary>
    public static ModelVerdict Assess(HubModel model, RuntimeFacts runtime)
    {
        var role = Classify(model.Task, model.Tags, model.Id, model.Library);
        if (role == AgentRole.NotUsed) return new(role, ModelReadiness.NotSupported, Plain(model.Task) + " models don't take part in coding work.", "Look for a chat, vision, embedding or reranker model instead.");
        if (role == AgentRole.Unknown) return new(role, ModelReadiness.CheckFiles, "The repository doesn't declare a task.", "Open it to inspect its files and model card.");
        if (model.Tags.Contains("mlx") || model.Library == "mlx") return new(role, ModelReadiness.NotSupported, "MLX weights run on Apple Silicon only.", "Choose a GGUF build of the same model.");
        if (model.Format == "GGUF") return Gguf(role, model.Architecture, runtime);
        if (model.Format == "Safetensors")
        {
            if (role == AgentRole.Adapter) return runtime.Python ? new(role, ModelReadiness.Ready, "LoRA adapter for the Transformers runtime. It loads on its base model.", "Download the base model too.") : new(role, ModelReadiness.NeedsRuntime, "Loads with the Transformers runtime, which isn't set up.", "Choose Python in Runtimes.");
            if (role is AgentRole.Implementer && model.Architecture.Length > 0 && runtime.TransformersFamilies.Contains(model.Architecture))
                return runtime.Python ? new(role, ModelReadiness.Ready, "Runs with the local Transformers runtime.") : new(role, ModelReadiness.NeedsRuntime, "Runs with Transformers once Python is chosen.", "Choose Python in Runtimes.");
            var why = role switch
            {
                AgentRole.Vision => "Mason serves vision models through llama.cpp, which needs GGUF weights and a projector.",
                AgentRole.CodeSearch or AgentRole.Reranker => "Mason serves embeddings and rerankers through llama.cpp, which needs GGUF weights.",
                AgentRole.Draft => "Draft models run inside llama.cpp, which needs GGUF weights.",
                _ => model.Architecture.Length > 0 ? $"No local Transformers adapter for “{model.Architecture}”." : "No local Transformers adapter for this architecture."
            };
            return new(role, ModelReadiness.UseGgufBuild, why, "Search this exact ID to see its GGUF quantizations.");
        }
        return new(role, ModelReadiness.CheckFiles, "No GGUF or Safetensors weights are tagged.", "Open it to inspect its files.");
    }

    internal static ModelVerdict Gguf(AgentRole role, string architecture, RuntimeFacts runtime)
    {
        var use = role switch { AgentRole.CodeSearch => "an embeddings server", AgentRole.Reranker => "a reranking server", AgentRole.Draft => "a draft beside its target model", AgentRole.Adapter => "a LoRA on its base model", AgentRole.Vision => "a vision chat model with its projector", _ => "a chat model" };
        if (!runtime.LlamaServer) return new(role, ModelReadiness.NeedsRuntime, $"Runs in llama.cpp as {use}. No llama.cpp runtime is set up.", "Install llama.cpp in Runtimes.");
        if (role == AgentRole.Adapter) return new(role, ModelReadiness.Ready, "Runs in llama.cpp on top of its base model.", "Choose it when starting the base model.");
        return runtime.Loads(architecture) switch
        {
            false => new(role, ModelReadiness.NeedsRuntimeUpdate, $"llama.cpp {runtime.BuildLabel} can't load the “{architecture}” architecture.", "Update llama.cpp in Runtimes."),
            true => new(role, ModelReadiness.Ready, $"Runs in llama.cpp {runtime.BuildLabel} as {use}."),
            null => new(role, ModelReadiness.Ready, $"Runs in llama.cpp as {use}. The architecture is confirmed when files are inspected.")
        };
    }

    /// <summary>Role for a local GGUF from its header, falling back to its file name.</summary>
    public static AgentRole ClassifyGguf(GgufHeader? header, string name)
    {
        if (header?.Type == "adapter" || header?.AdapterType.Length > 0) return AgentRole.Adapter;
        if (header?.Type == "mmproj" || header?.Architecture == "clip" || name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) return AgentRole.Projector;
        if (header?.Pooling == 4 || RerankName().IsMatch(name)) return AgentRole.Reranker;
        if (header?.Architecture is "bert" or "nomic-bert" or "nomic-bert-moe" or "jina-bert-v2" or "jina-bert-v3" or "modern-bert" or "neo-bert" or "t5encoder" || header?.Pooling is > 0 and < 4 || EmbedName().IsMatch(name)) return AgentRole.CodeSearch;
        if (header?.Architecture is { } a && DraftArchitecture().IsMatch(a) || DraftName().IsMatch(name)) return AgentRole.Draft;
        return AgentRole.Implementer;
    }
    /// <summary>Quality hint from the quantization in a file name.</summary>
    public static string QuantQuality(string name)
    {
        var q = QuantPattern().Match(name).Groups[1].Value.ToUpperInvariant().Replace("UD-", "");
        if (q.Length == 0) return "";
        if (q is "BF16" or "F16" or "F32") return "full precision";
        if (q.StartsWith("Q8")) return "near-lossless";
        if (q.StartsWith("Q6")) return "very high quality";
        if (q.StartsWith("Q5")) return "high quality";
        if (q.StartsWith("Q4") || q.StartsWith("IQ4") || q.StartsWith("MXFP4")) return "balanced";
        if (q.StartsWith("Q3") || q.StartsWith("IQ3")) return "smaller · lower quality";
        return "smallest · noticeably lower quality";
    }
    /// <summary>Pre-download fit from weight bytes: weights plus about 1 GiB of working memory at a short context.</summary>
    public static string Fit(long bytes, double? gpuTotalGiB, double? gpuFreeGiB, double systemGiB)
    {
        double need = bytes / 1073741824d * 1.08 + 1.0;
        if (gpuFreeGiB is { } free && need <= free) return "Fits on GPU";
        if (gpuTotalGiB is { } total && need <= total) return "Fits on GPU after stopping other models";
        if (need <= (gpuTotalGiB ?? 0) + systemGiB * .75) return "Partial GPU offload · slower";
        return "Too large for this PC";
    }
    /// <summary>Best variant for the agent: the highest quality that fits fully on the GPU, else the smallest balanced one.</summary>
    public static HubVariant? Recommend(IReadOnlyList<HubVariant> variants, double? gpuTotalGiB, double systemGiB)
    {
        var main = variants.Where(v => v.Role is AgentRole.Implementer or AgentRole.Vision or AgentRole.CodeSearch or AgentRole.Reranker).ToArray();
        if (main.Length == 0) return variants.FirstOrDefault();
        int Rank(HubVariant v) => QuantQuality(v.Name) switch { "balanced" => 0, "high quality" => 1, "very high quality" => 2, "near-lossless" => 3, "full precision" => 4, "smaller · lower quality" => 5, "" => 6, _ => 7 };
        double Need(HubVariant v) => v.Bytes / 1073741824d * 1.08 + 1.0;
        var fits = main.Where(v => gpuTotalGiB is { } t && Need(v) <= t).ToArray();
        // Coding quality drops sharply below 4-bit, so a balanced quant with partial offload beats a 2-bit one that fits.
        return fits.Where(v => Rank(v) <= 2).OrderByDescending(v => v.Bytes).FirstOrDefault()
            ?? main.Where(v => Rank(v) == 0).OrderBy(v => v.Bytes).FirstOrDefault()
            ?? fits.OrderBy(Rank).ThenByDescending(v => v.Bytes).FirstOrDefault()
            ?? main.OrderBy(v => v.Bytes).First();
    }
    public static string Plain(string task) => string.IsNullOrEmpty(task) ? "Unclassified" : char.ToUpperInvariant(task[0]) + task[1..].Replace('-', ' ');

    [GeneratedRegex(@"(?i)(?:^|[-_.])(?:lora|qlora|adapter)(?:$|[-_.])")] private static partial Regex AdapterName();
    [GeneratedRegex(@"(?i)(?:^|[-_.])((?:UD-)?(?:I?Q\d(?:_[A-Z0-9]+)*|BF16|F16|F32|MXFP4))(?:[-_.]|$)")] private static partial Regex QuantPattern();
    [GeneratedRegex(@"(?i)rerank")] private static partial Regex RerankName();
    [GeneratedRegex(@"(?i)(?:embed|^bge-|^e5-|^gte-|minilm|nomic-embed|arctic-embed)")] private static partial Regex EmbedName();
    [GeneratedRegex(@"(?i)(?:^|[-_.])(?:dspark|draft|eagle3?|dflash)(?:$|[-_.])")] private static partial Regex DraftName();
    [GeneratedRegex(@"(?i)(?:dspark|eagle|dflash|draft)")] private static partial Regex DraftArchitecture();
}
