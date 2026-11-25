#include "models.hpp"

#include <algorithm>
#include <chrono>
#include <future>
#include <map>
#include <regex>
#include <thread>
#include <sys/sysinfo.h>
#include <sys/wait.h>
#include <unistd.h>

#include "shell.hpp"

namespace mason {

// ---- local model files -----------------------------------------------------------------------------------------

std::string LocalModel::projector() const {
    std::error_code failure;
    for (fs::directory_iterator it(fs::path(path).parent_path(), failure), end; !failure && it != end; it.increment(failure)) {
        const std::string file = to_lower(it->path().filename().string());
        if (starts_with(file, "mmproj") && ends_with(file, ".gguf")) return it->path().string();
    }
    return "";
}

std::string LocalModel::role() const {
    const std::string lower = to_lower(name);
    if (starts_with(lower, "mmproj") || lower.find("-mmproj") != std::string::npos) return "projector";
    if (lower.find("rerank") != std::string::npos) return "reranker";
    if (lower.find("embed") != std::string::npos || starts_with(lower, "bge-")) return "embeddings";
    return projector().empty() ? "coding" : "vision";
}

std::string LocalModel::verdict() const {
    const std::string kind = role();
    if (kind == "coding") return "Coding agent · runs in llama.cpp as a chat model";
    if (kind == "vision") return "Coding agent · vision · runs in llama.cpp with its projector";
    if (kind == "projector") return "Vision companion file · loaded with the chat model beside it";
    if (kind == "embeddings") return "Code search · embeddings · not started from Mason yet";
    return "Code search · reranker · not started from Mason yet";
}

bool LocalModel::startable() const {
    const std::string kind = role();
    return kind == "coding" || kind == "vision";
}

fs::path download_folder() { return data_home() / "Models"; }

std::vector<std::pair<std::string, fs::path>> common_model_folders() {
    const fs::path home = home_dir();
    std::vector<std::pair<std::string, fs::path>> folders = {
        {"Mason downloads", download_folder()},
        {"Hugging Face cache", home / ".cache/huggingface/hub"},
        {"LM Studio", home / ".lmstudio/models"},
        {"LM Studio", home / ".cache/lm-studio/models"},
        {"llama.cpp cache", home / ".cache/llama.cpp"},
        {"Models folder", home / "models"},
    };
    if (const char* custom = std::getenv("HF_HOME"); custom && *custom) folders.push_back({"Hugging Face cache", fs::path(custom) / "hub"});
    return folders;
}

std::vector<LocalModel> scan_models(const std::vector<std::pair<std::string, fs::path>>& folders, size_t limit) {
    static const std::regex later_part(R"(-0000[2-9]-of-\d{5}\.gguf$)");
    std::map<std::string, LocalModel> found;  // by real path, so a cache link and its blob count once
    for (const auto& [source, root] : folders) {
        std::error_code failure;
        if (!fs::is_directory(root, failure)) continue;
        for (fs::recursive_directory_iterator it(root, fs::directory_options::skip_permission_denied, failure), end; !failure && it != end; it.increment(failure)) {
            const fs::path file = it->path();
            if (to_lower(file.extension().string()) != ".gguf") continue;
            // Parts 2…n of a split model belong to part 1.
            if (std::regex_search(file.filename().string(), later_part)) continue;
            std::error_code ignored;
            const fs::path real = fs::canonical(file, ignored);
            if (ignored) continue;
            const auto size = fs::file_size(real, ignored);
            if (ignored || size <= 1'000'000) continue;
            found[real.string()] = LocalModel{file.stem().string(), file.string(), static_cast<int64_t>(size), source};
            if (found.size() >= limit) break;
        }
    }
    std::vector<LocalModel> models;
    for (auto& entry : found) models.push_back(std::move(entry.second));
    std::sort(models.begin(), models.end(), [](const LocalModel& a, const LocalModel& b) {
        const int ka = a.startable() ? 0 : 1, kb = b.startable() ? 0 : 1;
        return ka != kb ? ka < kb : to_lower(a.name) < to_lower(b.name);
    });
    return models;
}

// ---- memory ----------------------------------------------------------------------------------------------------

namespace {

/// Runs a small helper program and returns what it printed, or nothing when it is missing or fails.
std::string capture(const std::vector<std::string>& arguments) {
    int ends[2];
    if (pipe(ends) != 0) return "";
    const pid_t child = fork();
    if (child == 0) {
        dup2(ends[1], 1);
        close(ends[0]); close(ends[1]);
        std::vector<char*> argv;
        for (const std::string& argument : arguments) argv.push_back(const_cast<char*>(argument.c_str()));
        argv.push_back(nullptr);
        execvp(argv[0], argv.data());
        _exit(127);
    }
    close(ends[1]);
    std::string output;
    char buffer[1024];
    ssize_t count;
    while ((count = read(ends[0], buffer, sizeof buffer)) > 0) output.append(buffer, static_cast<size_t>(count));
    close(ends[0]);
    int status = 0;
    if (child < 0 || waitpid(child, &status, 0) < 0 || !WIFEXITED(status) || WEXITSTATUS(status) != 0) return "";
    return output;
}

/// Free video memory on the NVIDIA GPU with the most, in bytes, or 0 when there is no such GPU.
int64_t nvidia_free_bytes() {
    static const int64_t cached = [] {
        int64_t best = 0;
        for (const std::string& line : split(capture({"nvidia-smi", "--query-gpu=memory.free", "--format=csv,noheader,nounits"}), '\n', false)) {
            try { best = std::max<int64_t>(best, std::stoll(trim(line)) * 1048576LL); } catch (const std::exception&) {}
        }
        return best;
    }();
    return cached;
}

}  // namespace

int64_t memory_budget_bytes() {
    if (const int64_t video = nvidia_free_bytes(); video > 0) return static_cast<int64_t>(video * 0.85);
    struct sysinfo info {};
    if (sysinfo(&info) != 0) return 8LL << 30;
    return static_cast<int64_t>(static_cast<double>(info.totalram) * info.mem_unit * 0.5);
}

std::string memory_budget_source() { return nvidia_free_bytes() > 0 ? "free video memory" : "system memory"; }
int64_t memory_needed_bytes(int64_t weights) { return static_cast<int64_t>(weights * 1.15) + 1'610'612'736LL; }
bool memory_fits(int64_t weights) { return memory_needed_bytes(weights) <= memory_budget_bytes(); }

int context_for(int64_t weights) {
    const int64_t spare = memory_budget_bytes() - memory_needed_bytes(weights);
    // Each further 8K of context is counted as roughly 0.5 GiB.
    for (int context : {32768, 16384, 8192}) {
        if (spare >= static_cast<int64_t>(context / 8192 - 1) * 536'870'912LL) return context;
    }
    return 4096;
}

// ---- llama.cpp runtime -----------------------------------------------------------------------------------------

namespace llama {

fs::path managed_folder() { return data_home() / "runtimes" / "llama.cpp"; }

std::string find() {
    // Builds Mason installed come first, newest first. Release archives have changed their inner layout over time, so look for the program by name.
    std::vector<fs::path> builds;
    std::error_code failure;
    for (fs::directory_iterator it(managed_folder(), failure), end; !failure && it != end; it.increment(failure)) {
        if (it->is_directory(failure)) builds.push_back(it->path());
    }
    std::sort(builds.rbegin(), builds.rend());
    for (const fs::path& build : builds) {
        for (fs::recursive_directory_iterator it(build, fs::directory_options::skip_permission_denied, failure), end; !failure && it != end; it.increment(failure)) {
            if (it->path().filename() == "llama-server" && ::access(it->path().c_str(), X_OK) == 0) return it->path().string();
        }
    }
    const char* search = std::getenv("PATH");
    for (const std::string& folder : split(search ? search : "/usr/local/bin:/usr/bin", ':', false)) {
        const std::string candidate = folder + "/llama-server";
        if (::access(candidate.c_str(), X_OK) == 0) return candidate;
    }
    return "";
}

std::string asset_suffix() {
#if defined(__aarch64__)
    const std::string arch = "arm64";
#else
    const std::string arch = "x64";
#endif
    std::error_code failure;
    bool vulkan = false;
    for (const char* library : {"/usr/lib/x86_64-linux-gnu/libvulkan.so.1", "/usr/lib/aarch64-linux-gnu/libvulkan.so.1", "/usr/lib64/libvulkan.so.1", "/usr/lib/libvulkan.so.1"}) {
        vulkan = vulkan || fs::exists(library, failure);
    }
    bool gpu = false;
    for (fs::directory_iterator it("/dev/dri", failure), end; !failure && it != end; it.increment(failure)) {
        gpu = gpu || starts_with(it->path().filename().string(), "renderD");
    }
    return std::string("bin-ubuntu-") + (vulkan && gpu ? "vulkan-" : "") + arch + ".tar.gz";
}

Release latest(const CancelToken& cancel) {
    const HttpResponse response = http_get("https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=5", {"Accept: application/vnd.github+json"}, 30, cancel);
    const json releases = json::parse(response.body, nullptr, false);
    if (response.status != 200 || !releases.is_array()) throw Error("GitHub did not return the llama.cpp releases (HTTP " + std::to_string(response.status) + ").");
    const std::string suffix = asset_suffix();
    for (const json& release : releases) {
        const std::string tag = release.value("tag_name", "");
        for (const json& asset : release.value("assets", json::array())) {
            const std::string name = asset.value("name", ""), url = asset.value("browser_download_url", "");
            if (name != "llama-" + tag + "-" + suffix || !starts_with(url, "https://github.com/")) continue;
            const std::string digest = asset.contains("digest") && asset["digest"].is_string() ? asset["digest"].get<std::string>() : "";
            return {tag, name, url, starts_with(digest, "sha256:") ? digest.substr(7) : "", asset.value("size", int64_t{0})};
        }
    }
    throw Error("No llama.cpp release has a build for this computer (" + suffix + ").");
}

std::string install(const Release& release, const std::function<void(const std::string&)>& progress, const CancelToken& cancel) {
    if (!std::regex_match(release.tag, std::regex("[A-Za-z0-9._-]+"))) throw Error("That release has an unexpected name.");
    fs::create_directories(managed_folder());
    const fs::path archive = managed_folder() / (release.asset + ".partial");
    struct Cleanup {
        fs::path file;
        ~Cleanup() { std::error_code ignored; fs::remove(file, ignored); }
    } cleanup{archive};
    const std::string digest = http_download(release.url, archive, release.size, {"github.com", "githubusercontent.com"},
        [&](int64_t done, int64_t total) { if (progress) progress("Downloading " + release.asset + " · " + byte_text(done) + " of " + byte_text(total)); }, cancel);
    if (!release.sha256.empty() && to_lower(release.sha256) != digest) throw Error("SHA-256 check failed for " + release.asset + ". The file was discarded.");
    const fs::path target = managed_folder() / release.tag;
    std::error_code failure;
    fs::remove_all(target, failure);
    fs::create_directories(target);
    if (progress) progress("Unpacking " + release.asset);
    capture({"tar", "-xzf", archive.string(), "-C", target.string()});
    const std::string server = find();
    if (!starts_with(server, target.string() + "/")) {
        fs::remove_all(target, failure);
        throw Error("The llama.cpp release did not unpack to a usable llama-server.");
    }
    return server;
}

}  // namespace llama

// ---- model server ----------------------------------------------------------------------------------------------

namespace {
std::string last_lines(const fs::path& log, size_t count) {
    std::string text;
    try { text = read_file(log); } catch (const Error&) { return ""; }
    if (text.size() > 4000) text = text.substr(text.size() - 4000);
    std::vector<std::string> lines = split(text, '\n', false);
    if (lines.size() > count) lines.erase(lines.begin(), lines.end() - static_cast<std::ptrdiff_t>(count));
    return join(lines, " · ");
}
}  // namespace

std::unique_ptr<ModelServer> ModelServer::start(const LocalModel& model, const std::string& runtime, int context, bool cpu_only,
                                                const std::function<void(const std::string&)>& progress, const CancelToken& cancel) {
    if (!model.startable()) throw Error(model.name + " is not a chat model: " + model.verdict() + ".");
    int port = 0;
    for (int candidate = 18310; candidate < 18330 && port == 0; candidate++) {
        if (!OpenAIClient::models("http://127.0.0.1:" + std::to_string(candidate), "", 0.4)) port = candidate;
    }
    if (port == 0) throw Error("Ports 18310–18329 on this computer are all in use.");
    const int tokens = context > 0 ? context : context_for(model.bytes);
    const std::string alias = "mason-" + new_id().substr(0, 8);
    const fs::path folder = data_home() / "model-servers";
    fs::create_directories(folder);

    std::unique_ptr<ModelServer> server(new ModelServer());
    server->model = model;
    server->endpoint = {"http://127.0.0.1:" + std::to_string(port) + "/", alias, ""};
    server->context = tokens;
    server->log = folder / (alias + ".log");
    std::vector<std::string> arguments = {runtime, "-m", model.path, "--host", "127.0.0.1", "--port", std::to_string(port), "--alias", alias,
                                          "-c", std::to_string(tokens), "--jinja", "-ngl", cpu_only ? "0" : "999"};
    if (const std::string projector = model.projector(); model.role() == "vision" && !projector.empty()) arguments.insert(arguments.end(), {"--mmproj", projector});
    server->pid_ = shell::spawn_owned(arguments, server->log);

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::minutes(5);
    while (std::chrono::steady_clock::now() < deadline) {
        if (is_cancelled(cancel)) throw Cancelled();  // the server stops when `server` is destroyed
        if (!server->running()) throw Error("llama-server stopped while loading " + model.name + ". " + last_lines(server->log, 4));
        if (const auto listed = OpenAIClient::models(server->endpoint.url, "", 1); listed && std::find(listed->begin(), listed->end(), alias) != listed->end()) return server;
        if (progress) progress(last_lines(server->log, 1));
        std::this_thread::sleep_for(std::chrono::milliseconds(700));
    }
    throw Error(model.name + " did not finish loading in five minutes. Log: " + server->log.string());
}

ModelServer::~ModelServer() { stop(); }
bool ModelServer::running() const { return shell::is_running(pid_); }
void ModelServer::stop() {
    if (pid_ > 0) shell::stop_owned(pid_);
    pid_ = 0;
}

std::vector<RunningServer> detect_servers() {
    std::vector<std::pair<std::string, int>> ports = {{"llama.cpp", 8080}, {"LM Studio", 1234}, {"Ollama", 11434}};
    for (int port = 18310; port < 18320; port++) ports.push_back({"Mason", port});
    std::vector<std::future<std::vector<RunningServer>>> checks;
    for (const auto& [name, port] : ports) {
        checks.push_back(std::async(std::launch::async, [name = name, port = port] {
            std::vector<RunningServer> found;
            const std::string url = "http://127.0.0.1:" + std::to_string(port) + "/";
            if (const auto listed = OpenAIClient::models(url, "", 0.8)) {
                for (size_t i = 0; i < listed->size() && i < 8; i++) found.push_back({name, {url, (*listed)[i], ""}});
            }
            return found;
        }));
    }
    std::vector<RunningServer> all;
    for (auto& check : checks) {
        for (RunningServer& server : check.get()) all.push_back(std::move(server));
    }
    return all;
}

Preferences Preferences::load() {
    Preferences preferences;
    try {
        const json saved = json::parse(read_file(data_home() / "preferences.json"), nullptr, false);
        if (!saved.is_object()) return preferences;
        if (saved.contains("lastEndpoint") && saved["lastEndpoint"].is_object()) {
            preferences.last_endpoint = ModelEndpoint{saved["lastEndpoint"].value("url", ""), saved["lastEndpoint"].value("model", ""), ""};
        }
        preferences.last_model_path = saved.value("lastModelPath", "");
        preferences.last_context = saved.value("lastContext", 0);
    } catch (const Error&) {
        // Nothing remembered yet.
    }
    return preferences;
}

void Preferences::save() const {
    json saved = {{"lastModelPath", last_model_path}, {"lastContext", last_context}};
    if (last_endpoint) saved["lastEndpoint"] = {{"url", last_endpoint->url}, {"model", last_endpoint->model}};
    try { write_file_atomic(data_home() / "preferences.json", saved.dump(1)); } catch (const Error&) {}
}

// ---- Hugging Face ----------------------------------------------------------------------------------------------

namespace {

bool has_tag(const HubModel& model, const char* tag) { return std::find(model.tags.begin(), model.tags.end(), tag) != model.tags.end(); }

std::string checked_repository(const std::string& id) {
    if (!std::regex_match(id, std::regex(R"([A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+)")) || id.find("..") != std::string::npos) throw Error("Use a repository as OWNER/NAME.");
    return id;
}

json hub_get(const std::string& path, const CancelToken& cancel) {
    const HttpResponse response = http_get("https://huggingface.co/api/" + path, {}, 45, cancel);
    if (response.status == 401 || response.status == 403) throw Error("Hugging Face refused access (HTTP " + std::to_string(response.status) + ").");
    if (response.status == 404) throw Error("Hugging Face has no such repository.");
    if (response.status != 200) throw Error("Hugging Face returned HTTP " + std::to_string(response.status) + ".");
    return json::parse(response.body, nullptr, false);
}

bool gated_flag(const json& item) { return item.contains("gated") && (item["gated"].is_string() || (item["gated"].is_boolean() && item["gated"].get<bool>())); }

std::string url_encode(const std::string& text) {
    std::string out;
    char buffer[4];
    for (unsigned char c : text) {
        if (std::isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~') out += static_cast<char>(c);
        else { std::snprintf(buffer, sizeof buffer, "%%%02X", c); out += buffer; }
    }
    return out;
}

const char* const kQuality[] = {"Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q4_K_M", "Q4_K_S", "IQ4_XS", "IQ4_NL", "Q3_K_L", "Q3_K_M"};
const char* const kTrusted[] = {"Qwen/", "unsloth/", "bartowski/", "ggml-org/", "lmstudio-community/", "mistralai/", "google/", "microsoft/", "deepseek-ai/", "ibm-granite/", "LiquidAI/", "openai/"};

}  // namespace

bool HubModel::is_gguf() const { return has_tag(*this, "gguf"); }

std::string HubModel::verdict() const {
    const std::string lower = to_lower(id);
    auto one_of = [&](std::initializer_list<const char*> tasks) { return std::any_of(tasks.begin(), tasks.end(), [&](const char* t) { return task == t; }); };
    if (has_tag(*this, "mlx") && !is_gguf()) return "Coding agent · MLX weights run on Apple silicon only; look for a GGUF build of this model";
    if (one_of({"text-to-image", "text-to-speech", "automatic-speech-recognition", "text-to-video", "image-to-image"})) {
        return "Not used by the agent · " + replace_all(task, "-", " ") + " models don't take part in coding work";
    }
    if (one_of({"feature-extraction", "sentence-similarity"}) || lower.find("embed") != std::string::npos) return "Code search · embeddings · not started from Mason yet";
    if (task == "text-ranking" || lower.find("rerank") != std::string::npos) return "Code search · reranker · not started from Mason yet";
    if (!is_gguf()) return "Coding agent · not a GGUF build; look for a GGUF build of this model";
    return task == "image-text-to-text" ? "Coding agent · vision · GGUF, runs in llama.cpp" : "Coding agent · GGUF, runs in llama.cpp";
}

std::string HubBuild::quant() const {
    std::string upper = file;
    std::transform(upper.begin(), upper.end(), upper.begin(), [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
    for (const char* name : {"Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q4_K_M", "Q4_K_S", "Q4_0", "IQ4_XS", "IQ4_NL", "Q3_K_L", "Q3_K_M", "Q3_K_S", "IQ3_M", "Q2_K", "BF16", "FP16", "F16"}) {
        if (upper.find(name) != std::string::npos) return name;
    }
    return "";
}

bool HubBuild::is_projector() const { return to_lower(file).find("mmproj") != std::string::npos; }

std::optional<HubBuild> HubRepository::suggested(int64_t budget) const {
    for (const char* quality : kQuality) {
        for (const HubBuild& build : builds) {
            if (!build.is_projector() && build.quant() == quality && memory_needed_bytes(build.bytes) <= budget) return build;
        }
    }
    return std::nullopt;
}

std::vector<HubModel> hub_search(const std::string& query, const CancelToken& cancel) {
    const std::string text = trim(query);
    if (text.empty() || text.size() > 200) throw Error("Search for 1–200 characters.");
    const json list = hub_get("models?search=" + url_encode(text) + "&sort=downloads&direction=-1&limit=40", cancel);
    std::vector<HubModel> gguf, other;
    if (!list.is_array()) return gguf;
    for (const json& item : list) {
        if (!item.is_object()) continue;
        HubModel model;
        model.id = item.value("id", item.value("modelId", ""));
        if (model.id.empty()) continue;
        model.downloads = item.value("downloads", int64_t{0});
        model.likes = item.value("likes", int64_t{0});
        if (item.contains("tags") && item["tags"].is_array()) {
            for (const json& tag : item["tags"]) {
                if (tag.is_string()) model.tags.push_back(tag.get<std::string>());
            }
        }
        model.task = item.contains("pipeline_tag") && item["pipeline_tag"].is_string() ? item["pipeline_tag"].get<std::string>() : "";
        model.gated = gated_flag(item);
        (model.is_gguf() ? gguf : other).push_back(std::move(model));
    }
    gguf.insert(gguf.end(), other.begin(), other.end());
    return gguf;
}

HubRepository hub_inspect(const std::string& id, const CancelToken& cancel) {
    const json info = hub_get("models/" + checked_repository(id) + "?blobs=true", cancel);
    HubRepository repository;
    repository.id = id;
    repository.revision = info.is_object() ? info.value("sha", "") : "";
    if (!std::regex_match(repository.revision, std::regex("[a-f0-9]{40}"))) throw Error("Hugging Face did not return a pinned commit for " + id + ".");
    if (info.contains("cardData") && info["cardData"].is_object() && info["cardData"].contains("license") && info["cardData"]["license"].is_string()) {
        repository.license = info["cardData"]["license"].get<std::string>();
    }
    repository.gated = gated_flag(info);
    static const std::regex split_part(R"(-\d{5}-of-\d{5}\.gguf$)");
    for (const json& file : info.value("siblings", json::array())) {
        const std::string name = file.value("rfilename", "");
        if (!ends_with(to_lower(name), ".gguf") || name.find("..") != std::string::npos || starts_with(name, "/") || std::regex_search(name, split_part)) continue;
        if (!file.contains("lfs") || !file["lfs"].is_object()) continue;
        const std::string hash = file["lfs"].value("sha256", "");
        if (!std::regex_match(hash, std::regex("[a-f0-9]{64}"))) continue;
        const int64_t size = file["lfs"].value("size", file.value("size", int64_t{0}));
        if (size <= 0) continue;
        repository.builds.push_back({name, size, hash});
    }
    std::sort(repository.builds.begin(), repository.builds.end(), [](const HubBuild& a, const HubBuild& b) { return a.bytes < b.bytes; });
    return repository;
}

fs::path hub_download(const HubBuild& build, const HubRepository& repository, const std::function<void(int64_t, int64_t)>& progress, const CancelToken& cancel) {
    const fs::path folder = download_folder() / replace_all(checked_repository(repository.id), "/", "--");
    fs::create_directories(folder);
    const fs::path target = folder / fs::path(build.file).filename();
    std::error_code failure;
    if (fs::exists(target, failure) && static_cast<int64_t>(fs::file_size(target, failure)) == build.bytes) return target;
    fs::path partial = target;
    partial += ".partial";
    std::vector<std::string> parts;
    for (const std::string& part : split(build.file, '/', false)) parts.push_back(url_encode(part));
    const std::string url = "https://huggingface.co/" + repository.id + "/resolve/" + repository.revision + "/" + join(parts, "/");
    try {
        const std::string digest = http_download(url, partial, build.bytes, {"huggingface.co", "hf.co"}, progress, cancel);
        if (digest != to_lower(build.sha256)) throw Error("SHA-256 check failed for " + build.file + ". The file was discarded.");
    } catch (...) {
        fs::remove(partial, failure);
        throw;
    }
    fs::rename(partial, target, failure);
    if (failure) throw Error("Could not save " + target.string() + ": " + failure.message());
    return target;
}

std::vector<HubSuggestion> hub_suggestions(size_t limit, const std::function<void(const std::string&)>& progress, const CancelToken& cancel) {
    static const std::regex unsuitable("abliterat|uncensor", std::regex::icase);
    std::vector<HubSuggestion> picks;
    size_t inspected = 0;
    for (const HubModel& candidate : hub_search("coder", cancel)) {
        const bool trusted = std::any_of(std::begin(kTrusted), std::end(kTrusted), [&](const char* owner) { return starts_with(candidate.id, owner); });
        if (!candidate.is_gguf() || candidate.gated || !trusted || !starts_with(candidate.verdict(), "Coding agent") || std::regex_search(candidate.id, unsuitable)) continue;
        if (++inspected > 12) break;
        if (progress) progress("Checking " + candidate.id);
        try {
            HubRepository repository = hub_inspect(candidate.id, cancel);
            if (auto build = repository.suggested()) picks.push_back({std::move(repository), *build});
        } catch (const Error&) {
            // One repository that cannot be read does not stop the others.
        }
        if (picks.size() >= limit) break;
    }
    std::sort(picks.begin(), picks.end(), [](const HubSuggestion& a, const HubSuggestion& b) { return a.build.bytes > b.build.bytes; });
    return picks;
}

}  // namespace mason
