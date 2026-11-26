// How much memory a local model needs at a given context length, read from the file's own metadata (never its
// tensor data), and how that compares with the memory free right now. The rules are the same on Windows, macOS
// and Linux, so the numbers a person sees match on every computer.
#pragma once
#include "models.hpp"

namespace mason {

constexpr double kGiB = 1073741824.0;

/// What a model needs, per its metadata. When `unknown` is set there is no estimate, only the reason, though
/// `weight_bytes`, `max_context` and `context_declared` may still be known.
struct MemoryProfile {
    double weight_bytes = 0;
    double kv_bytes_per_token = 0;
    double workspace_bytes = 0;
    double workspace_per_token = 0;
    int max_context = 4096;
    bool context_declared = false;
    std::string architecture;
    std::string basis;
    std::string unknown;
    bool estimated() const { return unknown.empty(); }
    /// The largest context the ladder offers: the declared one, else 32K.
    int context_cap() const { return context_declared ? max_context : 32768; }
};

/// The parts of a split GGUF (`-00001-of-00003.gguf`) in order, or just the file itself.
std::vector<fs::path> gguf_parts(const fs::path& file);
/// The size of a GGUF on disk, all parts together.
int64_t gguf_total_bytes(const fs::path& file);
/// Reads a GGUF header (at most 16 MiB of it).
MemoryProfile read_gguf_profile(const fs::path& file);
/// Reads a Transformers folder's config.json and safetensors headers.
MemoryProfile read_transformers_profile(const fs::path& folder);
MemoryProfile read_profile(const LocalModel& model);

/// Free memory where the model would run: the NVIDIA GPU with the most free, else the computer's memory.
struct MemoryReading {
    std::string device;  ///< the GPU's name, or "System memory"
    double free_bytes = 0, total_bytes = 0;
    bool gpu = false;
    bool usable() const { return total_bytes > 0 && free_bytes >= 0 && free_bytes <= total_bytes; }
    double free_gib() const { return free_bytes / kGiB; }
    double total_gib() const { return total_bytes / kGiB; }
    /// "RTX 4070 · 8.2 GiB free", "System memory · 12.4 GiB free".
    std::string label() const;
};
/// What `nvidia-smi --query-gpu=name,memory.free,memory.total --format=csv,noheader,nounits` printed, as the GPU with the most free.
std::optional<MemoryReading> parse_nvidia_memory(const std::string& output);
/// /proc/meminfo as "System memory": MemAvailable of MemTotal.
std::optional<MemoryReading> parse_meminfo(const std::string& text);
/// Takes a reading now. An empty device and zero sizes when nothing could be read.
MemoryReading read_memory();
/// A GPU's name without the vendor's brand words: "NVIDIA GeForce RTX 4070" → "RTX 4070".
std::string short_device_name(const std::string& name);

enum class Fit { Likely, Tight, TooLarge, Unverified, Waiting };
/// "Likely fits", "Tight fit", "Too large for free memory", "Fit unverified", "Waiting for a memory reading".
std::string fit_label(Fit fit);

struct FitAssessment {
    Fit fit = Fit::Waiting;
    /// Too large judged on the weights alone, since context memory is not estimated for this model.
    bool lower_bound = false;
    /// `low_gib` and `high_gib` hold an estimate.
    bool estimated = false;
    int tokens = 0;
    double low_gib = 0, high_gib = 0, weight_gib = 0, free_gib = 0, total_gib = 0, spare_gib = 0;
    std::string label() const { return fit_label(fit); }
};
FitAssessment assess(const MemoryProfile& profile, const MemoryReading& memory, int context);

/// The context lengths offered: powers of two from 2K up to the model's capacity (32K when undeclared), the
/// capacity itself when it is not one of them, at most six, dropping the smallest first.
std::vector<int> ladder_steps(const MemoryProfile& profile);
/// tokens/1000 + "K" for whole thousands, else round(tokens/1024) + "K": 2048 → "2K", 40960 → "40K", 128000 and 131072 → "128K", 32000 → "32K".
std::string context_label(int tokens);
/// "16384", "16k" and "16K" are all 16384. Nothing for text that is not a size of at least 512 tokens.
std::optional<int> parse_context_size(const std::string& text);
/// 32768 → "32,768".
std::string group_digits(int64_t value);
/// One decimal: 6.04 → "6.0".
std::string gib_text(double gib);

struct Recommendation {
    bool gpu = true;
    int context = 2048;
    std::string text;
};
/// The largest step up to 32K that likely fits on the GPU; else CPU when the GPU is too tight even at 512 tokens;
/// else a 2K starting point whose fit is checked when it starts.
Recommendation recommend(const MemoryProfile& profile, const MemoryReading& memory);

/// The line under the fit, for the panel. `step` is the context label ("16K").
std::string fit_detail(const FitAssessment& fit, const MemoryProfile& profile, bool cpu);
/// How the estimate is made, shown behind the "?" button and the CLI's ? key.
std::string fit_explanation(const MemoryProfile& profile);

/// The fit of a model at a glance, for the lists: one mark per step up to 32K and a short note.
enum class Tone { Good, Tight, Over, Muted };
Tone tone_of(Fit fit);
struct FitMark {
    std::string symbol;
    Tone tone = Tone::Muted;
};
struct FitSummary {
    std::vector<FitMark> marks;
    std::string note;
};
FitSummary summarize_fit(const LocalModel& model, const MemoryProfile& profile, const MemoryReading& memory);

/// The quantisation named in a file name, such as Q4_K_M, or empty.
std::string quant_in_name(const std::string& name);

}  // namespace mason
