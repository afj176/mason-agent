#include "model_memory.hpp"

#include <algorithm>
#include <cmath>
#include <fstream>
#include <map>
#include <regex>
#include <set>

namespace mason {

namespace {

constexpr int64_t kHeaderLimit = 16LL * 1024 * 1024;
constexpr int kCandidates[] = {2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144};
const char* const kUnreadable = "Model metadata could not be read safely. Check files and compatibility; no fit claim is made.";

/// Metadata that breaks the format's rules or Mason's bounds.
struct Invalid {};

struct Value {
    enum class Kind { Number, Text, Flag } kind = Kind::Text;
    double number = 0;
    std::string text;
};

/// Reads GGUF key/value metadata without ever touching tensor data, and never past the first 16 MiB.
class GgufReader {
public:
    explicit GgufReader(const fs::path& file) : in_(file, std::ios::binary), length_(static_cast<int64_t>(fs::file_size(file))) {
        if (!in_) throw Invalid{};
    }
    template <typename T>
    T get() {
        T value{};
        in_.read(reinterpret_cast<char*>(&value), sizeof value);
        if (!in_) throw Invalid{};
        return value;
    }
    int64_t position() { return static_cast<int64_t>(in_.tellg()); }
    void bound() {
        const int64_t at = position();
        if (at < 0 || at > kHeaderLimit || at > length_) throw Invalid{};
    }
    std::string text(bool keep) {
        bound();
        const uint64_t size = get<uint64_t>();
        if (size > static_cast<uint64_t>(std::max<int64_t>(0, std::min<int64_t>(kHeaderLimit, length_ - position())))) throw Invalid{};
        if (keep && size <= 1024) {
            std::string out(static_cast<size_t>(size), '\0');
            in_.read(out.data(), static_cast<std::streamsize>(size));
            if (!in_) throw Invalid{};
            return out;
        }
        in_.seekg(static_cast<std::streamoff>(size), std::ios::cur);
        bound();
        return "";
    }
    /// One value of the given type. Arrays are skipped and come back as the text "unsupported array".
    Value value(uint32_t type, bool keep) {
        bound();
        Value out;
        if (type == 8) {
            out.text = text(keep);
            return out;
        }
        if (type == 9) {
            const uint32_t element = get<uint32_t>();
            const uint64_t length = get<uint64_t>();
            if (element == 9 || length > 1'000'000) throw Invalid{};
            for (uint64_t i = 0; i < length; i++) value(element, false);
            out.text = "unsupported array";
            return out;
        }
        out.kind = Value::Kind::Number;
        switch (type) {
            case 0: out.number = get<uint8_t>(); break;
            case 1: out.number = get<int8_t>(); break;
            case 2: out.number = get<uint16_t>(); break;
            case 3: out.number = get<int16_t>(); break;
            case 4: out.number = get<uint32_t>(); break;
            case 5: out.number = get<int32_t>(); break;
            case 6: out.number = get<float>(); break;
            case 7: out.kind = Value::Kind::Flag; out.number = get<uint8_t>(); break;
            case 10: out.number = static_cast<double>(get<uint64_t>()); break;
            case 11: out.number = static_cast<double>(get<int64_t>()); break;
            case 12: out.number = get<double>(); break;
            default: throw Invalid{};
        }
        bound();
        return out;
    }
    int64_t length() const { return length_; }

private:
    std::ifstream in_;
    int64_t length_;
};

bool kept_key(const std::string& key) {
    return key == "general.architecture" || ends_with(key, ".block_count") || ends_with(key, ".embedding_length") || key.find(".attention.head_count") != std::string::npos ||
           ends_with(key, ".attention.key_length") || ends_with(key, ".attention.value_length") || ends_with(key, ".context_length") || key.find(".ssm.") != std::string::npos ||
           ends_with(key, ".full_attention_interval") || ends_with(key, ".nextn_predict_layers") || ends_with(key, ".attention.recurrent_layers");
}

bool whole(double value) { return value == std::floor(value); }

std::string without_period(std::string text) {
    while (!text.empty() && text.back() == '.') text.pop_back();
    return text;
}

/// Every step up to the model's capacity, before trimming to six.
std::vector<int> all_steps(const MemoryProfile& profile) {
    const int cap = profile.context_cap();
    std::vector<int> steps;
    for (int candidate : kCandidates) {
        if (candidate <= cap) steps.push_back(candidate);
    }
    const bool candidate = std::find(std::begin(kCandidates), std::end(kCandidates), cap) != std::end(kCandidates);
    if (!candidate && (steps.empty() || cap > steps.back())) steps.push_back(cap);
    return steps;
}

std::string fixed(double value, int decimals) {
    char buffer[64];
    std::snprintf(buffer, sizeof buffer, "%.*f", decimals, value);
    return buffer;
}

}  // namespace

// ---- reading metadata --------------------------------------------------------------------------------------------

std::vector<fs::path> gguf_parts(const fs::path& file) {
    static const std::regex part(R"(^(.*)-(\d{5})-of-(\d{5})\.gguf$)", std::regex::icase);
    std::smatch match;
    const std::string name = file.filename().string();
    if (!std::regex_match(name, match, part)) return {file};
    const int count = std::stoi(match[3].str());
    std::vector<fs::path> parts;
    std::error_code failure;
    for (int i = 1; i <= count && i <= 999; i++) {
        char suffix[32];
        std::snprintf(suffix, sizeof suffix, "-%05d-of-%05d", i, count);
        const fs::path candidate = file.parent_path() / (match[1].str() + suffix + file.extension().string());
        if (fs::exists(candidate, failure)) parts.push_back(candidate);
    }
    if (parts.empty()) parts.push_back(file);
    return parts;
}

int64_t gguf_total_bytes(const fs::path& file) {
    int64_t total = 0;
    for (const fs::path& part : gguf_parts(file)) {
        std::error_code failure;
        const auto size = fs::file_size(part, failure);
        if (!failure) total += static_cast<int64_t>(size);
    }
    return total;
}

MemoryProfile read_gguf_profile(const fs::path& file) {
    MemoryProfile profile;
    profile.weight_bytes = static_cast<double>(gguf_total_bytes(file));
    try {
        GgufReader reader(file);
        if (reader.get<uint32_t>() != 0x46554747) throw Invalid{};
        const uint32_t version = reader.get<uint32_t>();
        if (version != 2 && version != 3) throw Invalid{};
        const uint64_t tensors = reader.get<uint64_t>(), count = reader.get<uint64_t>();
        if (tensors == 0 || tensors > 200'000 || count > 100'000) throw Invalid{};
        std::map<std::string, Value> values;
        for (uint64_t i = 0; i < count; i++) {
            const std::string key = reader.text(true);
            const uint32_t type = reader.get<uint32_t>();
            const bool keep = kept_key(key);
            Value value = reader.value(type, keep);
            if (keep) values[key] = std::move(value);
        }
        const auto architecture_value = values.find("general.architecture");
        const std::string architecture = architecture_value != values.end() && architecture_value->second.kind == Value::Kind::Text ? architecture_value->second.text : "";
        profile.architecture = architecture;
        const auto declared = values.find(architecture + ".context_length");
        profile.context_declared = declared != values.end() && declared->second.kind == Value::Kind::Number && std::isfinite(declared->second.number) && declared->second.number >= 64;
        profile.max_context = profile.context_declared ? static_cast<int>(std::clamp(declared->second.number, 64.0, 2147483647.0)) : 4096;
        auto unavailable = [&](const std::string& why) {
            profile.unknown = why;
            return profile;
        };
        static const std::set<std::string> supported = {"qwen35", "llama", "qwen2", "qwen3", "gemma", "gemma2", "gemma3", "phi2", "phi3", "mistral", "gptneox", "gpt2", "qwen2moe", "qwen3moe"};
        if (!supported.count(architecture)) return unavailable("This GGUF architecture needs a runtime-specific memory estimate.");
        auto number = [&](const std::string& key, double fallback = 0) {
            const auto found = values.find(architecture + "." + key);
            if (found == values.end() || found->second.kind != Value::Kind::Number) return fallback;
            const double n = found->second.number;
            return std::isfinite(n) && n > 0 ? n : fallback;
        };
        const double layers = number("block_count"), heads = number("attention.head_count"), kv_heads = number("attention.head_count_kv", heads), hidden = number("embedding_length");
        const double key_length = number("attention.key_length", heads > 0 ? hidden / heads : 0), value_length = number("attention.value_length", key_length);
        if (layers <= 0 || layers > 1000 || kv_heads <= 0 || kv_heads > 1024 || key_length <= 0 || key_length > 8192 || value_length <= 0 || value_length > 8192) {
            return unavailable("GGUF attention metadata is insufficient for a fit estimate.");
        }
        if (architecture == "qwen35") {
            // One request at a time, no speculative decoder. A second FP32 state allowance is included.
            if (values.count("qwen35.attention.recurrent_layers")) return unavailable("Custom recurrent-layer maps need a dedicated estimate.");
            const double mtp = number("nextn_predict_layers"), trunk = layers - mtp, interval = number("full_attention_interval");
            const double state = number("ssm.state_size"), inner = number("ssm.inner_size"), groups = number("ssm.group_count"), kernel = number("ssm.conv_kernel"), rank = number("ssm.time_step_rank");
            const double geometry[] = {layers, mtp, trunk, interval, state, inner, groups, kernel, rank};
            if (!std::all_of(std::begin(geometry), std::end(geometry), whole) || mtp > 8 || trunk <= 0 || interval < 1 || interval > trunk || state <= 0 || state > 4096 || inner <= 0 ||
                inner > 1'000'000 || groups <= 0 || groups > 1024 || kernel < 1 || kernel > 64 || rank <= 0 || rank > 1024 || inner != state * rank) {
                return unavailable("Qwen hybrid metadata is missing or outside the supported estimate.");
            }
            const double attention = std::floor(trunk / interval), recurrent = trunk - attention;
            const double state_bytes = recurrent * (state * inner + (kernel - 1) * (inner + 2 * groups * state)) * 4 * 2;
            profile.kv_bytes_per_token = (attention + mtp) * kv_heads * (key_length + value_length) * 2;
            profile.workspace_bytes = kGiB * 0.75 + state_bytes;
            profile.basis = "Qwen hybrid: " + fixed(attention, 0) + " full-attention + " + fixed(recurrent, 0) + " recurrent layers; " + fixed(mtp, 0) +
                            " optional prediction blocks budgeted conservatively. FP16 KV + two FP32 recurrent-state allowances; one slot, no speculative decoding";
            return profile;
        }
        profile.kv_bytes_per_token = layers * kv_heads * (key_length + value_length) * 2;
        profile.workspace_bytes = kGiB * 0.75;
        profile.basis = "GGUF file size, FP16 KV cache and bounded prompt batching";
        return profile;
    } catch (const Invalid&) {
    } catch (const std::exception&) {
    }
    MemoryProfile unread;
    unread.weight_bytes = profile.weight_bytes;
    unread.unknown = kUnreadable;
    return unread;
}

MemoryProfile read_transformers_profile(const fs::path& folder) {
    MemoryProfile profile;
    try {
        auto read_json = [](const fs::path& file, int64_t limit) {
            if (static_cast<int64_t>(fs::file_size(file)) > limit) throw Invalid{};
            return json::parse(read_file(file));
        };
        auto number = [](const json& object, const char* key, double fallback = 0) {
            if (!object.contains(key) || !object[key].is_number()) return fallback;
            const double n = object[key].get<double>();
            return std::isfinite(n) && n >= 0 ? n : fallback;
        };
        const json config = read_json(folder / "config.json", 1024 * 1024);
        if (!config.is_object()) throw Invalid{};
        const std::string family = config.contains("model_type") && config["model_type"].is_string() ? config["model_type"].get<std::string>() : "";
        profile.architecture = family;
        if (family == "t5" || family == "bart" || family == "qwen3_moe" || family == "mixtral") {
            profile.unknown = "Encoder-decoder and expert-model peaks need a runtime-specific estimate.";
            return profile;
        }
        std::vector<std::string> files;
        const fs::path index = folder / "model.safetensors.index.json";
        std::error_code failure;
        if (fs::exists(index, failure)) {
            const json document = read_json(index, 4 * 1024 * 1024);
            std::set<std::string> seen;
            for (const auto& [name, file] : document.at("weight_map").items()) {
                const std::string value = file.get<std::string>();
                if (seen.insert(value).second) files.push_back(value);
                if (files.size() > 256) break;
            }
        } else {
            files.push_back("model.safetensors");
        }
        if (files.empty() || files.size() > 256) throw Invalid{};
        double weights = 0;
        int64_t header_budget = kHeaderLimit;
        for (const std::string& file : files) {
            if (file != fs::path(file).filename().string() || !ends_with(to_lower(file), ".safetensors")) throw Invalid{};
            const fs::path path = folder / file;
            std::ifstream in(path, std::ios::binary);
            if (!in) throw Invalid{};
            const int64_t length = static_cast<int64_t>(fs::file_size(path));
            uint64_t size = 0;
            in.read(reinterpret_cast<char*>(&size), sizeof size);
            if (!in || size == 0 || size > static_cast<uint64_t>(header_budget) || size > static_cast<uint64_t>(std::max<int64_t>(0, length - 8))) throw Invalid{};
            header_budget -= static_cast<int64_t>(size);
            std::string header(static_cast<size_t>(size), '\0');
            in.read(header.data(), static_cast<std::streamsize>(size));
            if (!in) throw Invalid{};
            const json tensors = json::parse(header);
            if (!tensors.is_object()) throw Invalid{};
            for (const auto& [name, tensor] : tensors.items()) {
                if (name == "__metadata__") continue;
                const json& shape = tensor.at("shape");
                if (!shape.is_array() || shape.size() > 8) throw Invalid{};
                double elements = 1;
                for (const json& dimension : shape) {
                    if (!dimension.is_number_integer()) throw Invalid{};
                    const int64_t n = dimension.get<int64_t>();
                    if (n < 0 || n > 1'000'000'000) throw Invalid{};
                    elements *= static_cast<double>(n);
                }
                const std::string dtype = tensor.at("dtype").get<std::string>();
                const json& offsets = tensor.at("data_offsets");
                const int64_t begin = offsets.at(0).get<int64_t>(), end = offsets.at(1).get<int64_t>();
                if (begin < 0 || end < begin || end > length - 8 - static_cast<int64_t>(size) || !std::isfinite(elements)) throw Invalid{};
                static const std::set<std::string> floats = {"F16", "BF16", "F32", "F64"};
                static const std::set<std::string> integers = {"I64", "I32", "I16", "I8", "U8", "U16", "U32", "U64", "BOOL"};
                if (floats.count(dtype)) weights += elements * 2;  // served as FP16/BF16
                else if (integers.count(dtype)) weights += static_cast<double>(end - begin);
                else throw Invalid{};
            }
        }
        if (weights <= 0 || weights > 1e15) throw Invalid{};
        profile.weight_bytes = weights;
        const double layers = number(config, "num_hidden_layers", number(config, "n_layer")), heads = number(config, "num_attention_heads", number(config, "n_head"));
        const double hidden = number(config, "hidden_size", number(config, "n_embd"));
        const double kv_heads = number(config, "num_key_value_heads", heads), head_dim = number(config, "head_dim", heads > 0 ? hidden / heads : 0);
        const bool declared = config.contains("max_position_embeddings") || config.contains("n_positions");
        profile.max_context = static_cast<int>(std::clamp(number(config, "max_position_embeddings", number(config, "n_positions", 4096)), 64.0, 2147483647.0));
        profile.context_declared = declared;
        const double geometry[] = {layers, heads, kv_heads, head_dim, hidden};
        if (layers <= 0 || layers > 1000 || heads <= 0 || heads > 1024 || kv_heads <= 0 || kv_heads > heads || head_dim <= 0 || head_dim > 8192 || hidden <= 0 || hidden > 1'000'000 ||
            number(config, "vocab_size") > 1'000'000 || number(config, "intermediate_size") > 10'000'000 || !std::all_of(std::begin(geometry), std::end(geometry), whole)) {
            profile.unknown = "Attention geometry is missing or outside the supported estimate.";
            return profile;
        }
        bool quantized = false;
        if (config.contains("quantization_config") && config["quantization_config"].is_object()) {
            const json& quantization = config["quantization_config"];
            const std::string method = quantization.contains("quant_method") && quantization["quant_method"].is_string() ? to_lower(quantization["quant_method"].get<std::string>()) : "";
            quantized = method == "gptq" || method == "awq";
        }
        profile.workspace_bytes = kGiB * 0.75;
        if (quantized) profile.workspace_bytes += std::max(hidden * number(config, "intermediate_size", hidden * 4) * 4, kGiB * 0.25);
        profile.kv_bytes_per_token = 2 * layers * kv_heads * head_dim * 2;
        profile.workspace_per_token = hidden * 12 + number(config, "vocab_size") * 4;
        profile.basis = std::string("Tensor headers, FP16/BF16 activations and full-context KV cache") + (quantized ? "; packed weights plus temporary dequantization" : "");
        return profile;
    } catch (const Invalid&) {
    } catch (const std::exception&) {
    }
    MemoryProfile unread;
    unread.architecture = profile.architecture;
    unread.unknown = kUnreadable;
    return unread;
}

MemoryProfile read_profile(const LocalModel& model) {
    return model.transformers ? read_transformers_profile(model.path) : read_gguf_profile(model.path);
}

// ---- memory readings ---------------------------------------------------------------------------------------------

std::string short_device_name(const std::string& name) {
    std::string out = trim(name);
    for (const char* brand : {"NVIDIA ", "GeForce ", "Quadro "}) {
        if (starts_with(out, brand) && out.size() > std::string(brand).size()) out = out.substr(std::string(brand).size());
    }
    return out;
}

std::string MemoryReading::label() const {
    if (!usable()) return "No memory reading";
    return (gpu ? short_device_name(device) : device) + " · " + gib_text(free_gib()) + " GiB free";
}

std::optional<MemoryReading> parse_nvidia_memory(const std::string& output) {
    std::optional<MemoryReading> best;
    for (const std::string& line : split(output, '\n', false)) {
        const size_t second = line.rfind(',');
        if (second == std::string::npos || second == 0) continue;
        const size_t first = line.rfind(',', second - 1);
        if (first == std::string::npos) continue;
        try {
            size_t used = 0;
            const std::string free_text = trim(line.substr(first + 1, second - first - 1)), total_text = trim(line.substr(second + 1));
            const double free_mib = std::stod(free_text, &used);
            if (used != free_text.size()) continue;
            const double total_mib = std::stod(total_text, &used);
            if (used != total_text.size()) continue;
            MemoryReading reading{trim(line.substr(0, first)), free_mib * 1048576.0, total_mib * 1048576.0, true};
            if (!reading.usable() || reading.device.empty()) continue;
            if (!best || reading.free_bytes > best->free_bytes) best = reading;
        } catch (const std::exception&) {
        }
    }
    return best;
}

std::optional<MemoryReading> parse_meminfo(const std::string& text) {
    double total = -1, available = -1;
    for (const std::string& line : split(text, '\n', false)) {
        const size_t colon = line.find(':');
        if (colon == std::string::npos) continue;
        const std::string key = line.substr(0, colon);
        if (key != "MemTotal" && key != "MemAvailable") continue;
        try {
            const double kib = std::stod(trim(line.substr(colon + 1)));
            (key == "MemTotal" ? total : available) = kib * 1024.0;
        } catch (const std::exception&) {
        }
    }
    if (total <= 0 || available < 0) return std::nullopt;
    MemoryReading reading{"System memory", std::min(available, total), total, false};
    return reading;
}

MemoryReading read_memory() {
    if (auto gpu = parse_nvidia_memory(program_output({"nvidia-smi", "--query-gpu=name,memory.free,memory.total", "--format=csv,noheader,nounits"}))) return *gpu;
    try {
        if (auto system = parse_meminfo(read_file("/proc/meminfo"))) return *system;
    } catch (const std::exception&) {
    }
    return {};
}

// ---- fit ---------------------------------------------------------------------------------------------------------

std::string fit_label(Fit fit) {
    switch (fit) {
        case Fit::Likely: return "Likely fits";
        case Fit::Tight: return "Tight fit";
        case Fit::TooLarge: return "Too large for free memory";
        case Fit::Unverified: return "Fit unverified";
        case Fit::Waiting: break;
    }
    return "Waiting for a memory reading";
}

FitAssessment assess(const MemoryProfile& profile, const MemoryReading& memory, int context) {
    FitAssessment out;
    out.tokens = std::min(context, profile.context_cap());
    out.weight_gib = profile.weight_bytes / kGiB;
    if (profile.estimated()) {
        const double cache = profile.kv_bytes_per_token * out.tokens;
        const double work = profile.workspace_bytes + profile.workspace_per_token * out.tokens;
        out.low_gib = (profile.weight_bytes + cache + work) / kGiB;
        out.high_gib = (profile.weight_bytes * 1.12 + cache * 1.15 + work * 1.5) / kGiB;
        out.estimated = true;
    }
    if (!memory.usable()) return out;
    out.free_gib = memory.free_gib();
    out.total_gib = memory.total_gib();
    out.spare_gib = std::max(0.5, out.total_gib * 0.05);
    if (!profile.estimated()) {
        out.lower_bound = out.weight_gib > 0 && out.weight_gib + out.spare_gib > out.free_gib;
        out.fit = out.lower_bound ? Fit::TooLarge : Fit::Unverified;
        return out;
    }
    out.fit = out.high_gib + out.spare_gib <= out.free_gib ? Fit::Likely : out.low_gib + out.spare_gib <= out.free_gib ? Fit::Tight : Fit::TooLarge;
    return out;
}

std::vector<int> ladder_steps(const MemoryProfile& profile) {
    std::vector<int> steps = all_steps(profile);
    if (steps.size() > 6) steps.erase(steps.begin(), steps.end() - 6);
    return steps;
}

std::string context_label(int tokens) {
    if (tokens < 1000) return std::to_string(tokens);
    // A declared size in decimal thousands stays one (128000 → 128K); the rest count binary ones (16384 → 16K).
    if (tokens % 1000 == 0) return std::to_string(tokens / 1000) + "K";
    return std::to_string(std::llround(tokens / 1024.0)) + "K";
}

std::optional<int> parse_context_size(const std::string& text) {
    std::string value = trim(text);
    int64_t multiplier = 1;
    if (!value.empty() && (value.back() == 'k' || value.back() == 'K')) {
        multiplier = 1024;
        value.pop_back();
    }
    if (value.empty() || value.size() > 9 || !std::all_of(value.begin(), value.end(), [](unsigned char c) { return std::isdigit(c); })) return std::nullopt;
    const int64_t tokens = std::stoll(value) * multiplier;
    if (tokens < 512 || tokens > (1LL << 24)) return std::nullopt;
    return static_cast<int>(tokens);
}

std::string group_digits(int64_t value) {
    std::string digits = std::to_string(value < 0 ? -value : value), out;
    for (size_t i = 0; i < digits.size(); i++) {
        if (i > 0 && (digits.size() - i) % 3 == 0) out += ',';
        out += digits[i];
    }
    return (value < 0 ? "-" : "") + out;
}

std::string gib_text(double gib) { return fixed(gib, 1); }

Recommendation recommend(const MemoryProfile& profile, const MemoryReading& memory) {
    const int cap = std::min(profile.context_cap(), 32768);
    const std::vector<int> steps = ladder_steps(profile);
    for (auto step = steps.rbegin(); step != steps.rend(); ++step) {
        if (*step <= cap && assess(profile, memory, *step).fit == Fit::Likely) return {true, *step, "Suggested: " + context_label(*step) + " on GPU · largest likely fit"};
    }
    const int start = std::min(2048, cap);
    const FitAssessment small = assess(profile, memory, std::min(512, cap));
    if (small.fit == Fit::Tight || small.fit == Fit::TooLarge) {
        const bool weights = small.lower_bound || small.weight_gib + small.spare_gib > small.free_gib;
        return {false, start, std::string("Suggested: CPU · ") + (weights ? "weights exceed free memory" : "GPU headroom is limited")};
    }
    return {true, start, "Suggested starting point: GPU · " + context_label(start) + " · fit unverified"};
}

std::string fit_detail(const FitAssessment& fit, const MemoryProfile& profile, bool cpu) {
    if (cpu) return "Runs from system RAM. Available RAM is not checked here, and CPU turns are much slower.";
    const std::string step = context_label(fit.tokens), range = gib_text(fit.low_gib) + "–" + gib_text(fit.high_gib) + " GiB at " + step;
    const std::string architecture = profile.architecture.empty() ? "this model" : profile.architecture;
    switch (fit.fit) {
        case Fit::Likely: return range + " · " + gib_text(fit.spare_gib) + " GiB kept spare. An estimate, not a guarantee.";
        case Fit::Tight: return range + " · other apps could push it over. Try a smaller context or close apps using the GPU.";
        case Fit::TooLarge:
            if (fit.lower_bound) return "The weights alone are " + gib_text(fit.weight_gib) + " GiB and " + gib_text(fit.free_gib) + " GiB is free. Context memory for " + architecture + " is not estimated yet.";
            return range + " · more than the " + gib_text(fit.free_gib) + " GiB free.";
        case Fit::Unverified:
            return (fit.weight_gib > 0 ? "The weights are " + gib_text(fit.weight_gib) + " GiB. " : std::string()) + "Context memory for " + architecture + " is not estimated, so fit is checked when it starts.";
        case Fit::Waiting: break;
    }
    return fit.estimated ? range + ". Free memory has not been read yet." : "Free memory has not been read yet.";
}

std::string fit_explanation(const MemoryProfile& profile) {
    const std::string capacity = profile.context_declared ? "Model capacity: " + group_digits(profile.max_context) + " tokens. " : "Model capacity unavailable. ";
    const std::string basis = !profile.unknown.empty() ? profile.unknown : !profile.basis.empty() ? profile.basis : "Metadata unavailable";
    return capacity + without_period(basis) +
           ". Based on metadata from the last scan and current memory readings. Estimates include runtime headroom, not model quality or speed. Other apps can change free "
           "memory before Start. GGUF prompt batch 256 / microbatch 128; one request at a time. Context includes prompt, reasoning and output. Restart the model to apply a "
           "different loaded window.";
}

Tone tone_of(Fit fit) {
    switch (fit) {
        case Fit::Likely: return Tone::Good;
        case Fit::Tight: return Tone::Tight;
        case Fit::TooLarge: return Tone::Over;
        default: return Tone::Muted;
    }
}

FitSummary summarize_fit(const LocalModel& model, const MemoryProfile& profile, const MemoryReading& memory) {
    const std::string role = model.role();
    if (role == "projector") return {{{"─", Tone::Muted}}, "vision companion"};
    if (!memory.usable()) return {{{"?", Tone::Muted}}, "waiting for a memory reading"};
    if (role == "embeddings" || role == "reranker") {
        const double spare = std::max(0.5, memory.total_gib() * 0.05), weights = profile.weight_bytes / kGiB;
        if (weights + spare <= memory.free_gib()) return {{{"●", Tone::Good}}, "fits"};
        return {{{"✕", Tone::Over}}, "weights alone exceed free memory"};
    }
    if (!profile.estimated()) {
        if (assess(profile, memory, 2048).lower_bound) return {{{"✕", Tone::Over}}, "weights alone exceed free memory"};
        return {{{"?", Tone::Muted}}, "fit unverified"};
    }
    std::vector<int> steps;
    for (int step : all_steps(profile)) {
        if (step <= 32768) steps.push_back(step);
    }
    if (steps.size() > 5) steps.erase(steps.begin(), steps.end() - 5);
    if (steps.empty()) steps.push_back(std::min(2048, profile.context_cap()));
    FitSummary summary;
    int largest_likely = 0;
    bool any = false;
    for (int step : steps) {
        const Fit fit = assess(profile, memory, step).fit;
        summary.marks.push_back({fit == Fit::Likely ? "●" : fit == Fit::Tight ? "◐" : "○", fit == Fit::TooLarge ? Tone::Muted : tone_of(fit)});
        if (fit == Fit::Likely) largest_likely = step;
        any = any || fit == Fit::Likely || fit == Fit::Tight;
    }
    if (!any) return {{{"✕", Tone::Over}}, "needs " + gib_text(assess(profile, memory, steps.front()).low_gib) + "+ GiB · try CPU"};
    summary.note = largest_likely > 0 ? "up to " + context_label(largest_likely) : "tight at " + context_label(steps.front());
    return summary;
}

std::string quant_in_name(const std::string& name) {
    std::string upper = name;
    std::transform(upper.begin(), upper.end(), upper.begin(), [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
    for (const char* quant : {"IQ1_S", "IQ1_M", "IQ2_XXS", "IQ2_XS", "IQ2_S", "IQ2_M", "IQ3_XXS", "IQ3_XS", "IQ3_S", "IQ3_M", "IQ4_XS", "IQ4_NL", "Q2_K_S", "Q2_K", "Q3_K_S", "Q3_K_M", "Q3_K_L",
                              "Q4_K_S", "Q4_K_M", "Q4_0", "Q4_1", "Q5_K_S", "Q5_K_M", "Q5_0", "Q5_1", "Q6_K", "Q8_0", "MXFP4", "BF16", "FP16", "F16", "F32"}) {
        if (upper.find(quant) != std::string::npos) return quant;
    }
    return "";
}

}  // namespace mason
