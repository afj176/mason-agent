// Models on this computer: the files, the llama.cpp runtime that runs them, a server process for one of them,
// and the Hugging Face hub for finding more. Mason reaches the internet only for these, and only when asked.
#pragma once
#include "model.hpp"

namespace mason {

struct LocalModel {
    std::string name, path;
    int64_t bytes = 0;
    std::string source;

    /// coding, vision, projector, embeddings or reranker.
    std::string role() const;
    /// What the agent can do with this file, in plain words. Every file is listed with one of these; none are hidden.
    std::string verdict() const;
    bool startable() const;
    /// The mmproj file beside a vision model, if any.
    std::string projector() const;
};

fs::path download_folder();
/// Folders where GGUF files usually are: Mason's own downloads, the Hugging Face cache, LM Studio and llama.cpp's cache.
std::vector<std::pair<std::string, fs::path>> common_model_folders();
std::vector<LocalModel> scan_models(const std::vector<std::pair<std::string, fs::path>>& folders = common_model_folders(), size_t limit = 400);

/// Memory a model may use: 85% of free NVIDIA video memory when there is some, else half of the computer's memory.
int64_t memory_budget_bytes();
std::string memory_budget_source();
/// Weights plus working memory for an 8K context: about 15% of the weights plus 1.5 GiB.
int64_t memory_needed_bytes(int64_t weights);
bool memory_fits(int64_t weights);
/// The largest context, up to 32K, that leaves room.
int context_for(int64_t weights);

namespace llama {
fs::path managed_folder();
/// llama-server from a build Mason installed, else from PATH. Empty when there is none.
std::string find();
struct Release {
    std::string tag, asset, url, sha256;
    int64_t size = 0;
};
/// The release asset for this computer: the Vulkan build when a GPU and the Vulkan loader are present, else the CPU build.
std::string asset_suffix();
Release latest(const CancelToken& cancel = nullptr);
/// Downloads the release, checks its SHA-256 when GitHub publishes one, unpacks it and returns the llama-server path.
std::string install(const Release& release, const std::function<void(const std::string&)>& progress, const CancelToken& cancel = nullptr);
}  // namespace llama

/// One llama-server process owned by this app or command line session. It stops when stopped, when this object goes
/// away, and when the owning process exits for any reason.
class ModelServer {
public:
    /// Starts the model and waits until the server lists it, up to five minutes. Progress carries the last log line while it loads.
    static std::unique_ptr<ModelServer> start(const LocalModel& model, const std::string& runtime, int context, bool cpu_only,
                                              const std::function<void(const std::string&)>& progress, const CancelToken& cancel = nullptr);
    ~ModelServer();
    ModelServer(const ModelServer&) = delete;
    ModelServer& operator=(const ModelServer&) = delete;
    bool running() const;
    void stop();

    LocalModel model;
    ModelEndpoint endpoint;
    int context = 0;
    fs::path log;
private:
    ModelServer() = default;
    int pid_ = 0;
};

struct RunningServer {
    std::string name;
    ModelEndpoint endpoint;
};
/// Local OpenAI-compatible servers that are already running: llama.cpp, LM Studio, Ollama and Mason's own.
std::vector<RunningServer> detect_servers();

/// What the app and command line remember between launches.
struct Preferences {
    std::optional<ModelEndpoint> last_endpoint;
    std::string last_model_path;
    int last_context = 0;
    static Preferences load();
    void save() const;
};

// ---- Hugging Face ----------------------------------------------------------------------------------------------

struct HubModel {
    std::string id;
    int64_t downloads = 0, likes = 0;
    std::vector<std::string> tags;
    std::string task;
    bool gated = false;
    bool is_gguf() const;
    /// What the agent could do with this repository. Shown for every result.
    std::string verdict() const;
};

struct HubBuild {
    std::string file;
    int64_t bytes = 0;
    std::string sha256;
    std::string quant() const;
    bool is_projector() const;
};

struct HubRepository {
    std::string id, revision, license;
    bool gated = false;
    std::vector<HubBuild> builds;
    /// Best quality first; the first single-file build that fits the memory budget.
    std::optional<HubBuild> suggested(int64_t budget = memory_budget_bytes()) const;
};

/// Every matching repository, GGUF builds first. Nothing is filtered out; each result carries a verdict.
std::vector<HubModel> hub_search(const std::string& query, const CancelToken& cancel = nullptr);
/// The repository's single-file GGUF builds with sizes and hashes, pinned to its current commit.
HubRepository hub_inspect(const std::string& id, const CancelToken& cancel = nullptr);
/// Downloads one build into Mason's model folder and verifies it against the hash Hugging Face publishes. Returns the file.
fs::path hub_download(const HubBuild& build, const HubRepository& repository, const std::function<void(int64_t, int64_t)>& progress, const CancelToken& cancel = nullptr);
struct HubSuggestion {
    HubRepository repository;
    HubBuild build;
};
/// Coding models from well-known publishers whose best build fits this computer's memory, largest first.
std::vector<HubSuggestion> hub_suggestions(size_t limit = 4, const std::function<void(const std::string&)>& progress = nullptr, const CancelToken& cancel = nullptr);

}  // namespace mason
