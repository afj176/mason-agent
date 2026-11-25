using System.Text.RegularExpressions;

namespace Mason;

/// <summary>
/// G2 one-click local setup: picks the llama.cpp build for this machine's GPU and the best-quality quantization of a
/// coding model that fits in memory, the way Goose's onboarding recommends a variant. The caller shows the plan and
/// the user confirms before anything is downloaded.
/// </summary>
public static partial class QuickSetup
{
    /// <summary>Best quality first; the first that fits wins. Q4_K_M is the usual balance point.</summary>
    static readonly string[] QualityOrder = ["Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q4_K_M", "Q4_K_S", "IQ4_XS", "IQ4_NL", "Q3_K_L", "Q3_K_M", "IQ3_M", "IQ3_XS"];

    public sealed record Choice(HubRepository Repository, HubVariant Variant, double NeededGiB, string Reason);

    /// <summary>CUDA 13 for NVIDIA (the release notes require driver 580+; the embedded check falls back to CPU),
    /// Vulkan for other GPUs, CPU otherwise.</summary>
    public static string Backend(GpuSample? sample) =>
        sample?.Adapters is not { Length: > 0 } adapters ? "cpu"
        : adapters.Any(a => a.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) ? "cuda-13.5" : "vulkan";

    /// <summary>Memory the model may use: 85% of free VRAM on a GPU, else a conservative 8 GiB of system memory.</summary>
    public static double BudgetGiB(GpuSample? sample)
    {
        var best = sample?.Adapters.Where(a => a.FreeMiB is > 0).Select(a => a.FreeMiB!.Value).DefaultIfEmpty(0).Max() ?? 0;
        return best > 0 ? best / 1024d * .85 : 8;
    }

    /// <summary>Weights plus KV cache and working memory for an 8K context: about 15% of the weights plus 1.5 GiB.</summary>
    public static double NeededGiB(long bytes) => bytes / 1073741824d * 1.15 + 1.5;

    public static Choice? Fit(HubRepository repository, double budgetGiB)
    {
        var variants = repository.Variants.Where(v => v.Format == "GGUF" && !v.Name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var quant in QualityOrder)
        {
            var variant = variants.FirstOrDefault(v => Quant(v.Name).Equals(quant, StringComparison.OrdinalIgnoreCase));
            if (variant == null) continue;
            var needed = NeededGiB(variant.Bytes);
            if (needed <= budgetGiB)
                return new(repository, variant, needed, $"{quant} is the highest-quality build that fits: about {needed:0.0} GiB of the {budgetGiB:0.0} GiB available.");
        }
        return null;
    }

    /// <summary>Publishers whose GGUF builds are the usual reference conversions.</summary>
    static readonly string[] Publishers = ["Qwen", "unsloth", "bartowski", "ggml-org", "lmstudio-community", "mistralai", "google", "microsoft", "deepseek-ai", "ibm-granite", "LiquidAI", "openai"];
    public static bool Trusted(string id) => Publishers.Contains(id.Split('/')[0], StringComparer.OrdinalIgnoreCase);
    /// <summary>Fine-tunes that remove safety training or target narrow tasks are never suggested by default.</summary>
    public static bool Suggestible(string id) => !Unsuitable().IsMatch(id);

    /// <summary>Default order: models at Q4 quality or better first, then the largest weights that fit, then trusted publishers.</summary>
    public static IEnumerable<Choice> Rank(IEnumerable<Choice> choices) => choices
        .OrderByDescending(c => Array.IndexOf(QualityOrder, Quant(c.Variant.Name)) is var i && i >= 0 && i <= Array.IndexOf(QualityOrder, "IQ4_NL"))
        .ThenByDescending(c => c.Variant.Bytes)
        .ThenByDescending(c => Trusted(c.Repository.Id));

    public static string Quant(string name) => QuantToken().Match(name.Replace("UD-", "", StringComparison.OrdinalIgnoreCase)) is { Success: true } m ? m.Value.ToUpperInvariant() : "";

    [GeneratedRegex(@"(?i)uncensored|abliterat|heretic|nsfw|erotic|roleplay|3d-coder")] private static partial Regex Unsuitable();
    [GeneratedRegex(@"(?i)(?<![A-Z0-9])(IQ\d_[A-Z]+|Q\d_K_[SML]|Q\d_K|Q\d_\d|Q\d_[A-Z]+)(?![A-Z0-9])")] private static partial Regex QuantToken();
}
