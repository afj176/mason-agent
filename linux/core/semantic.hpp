// Searching code by meaning. The project's source is cut into overlapping windows of lines, each window is embedded
// once per content hash by an embeddings server the user chose, and windows are ranked against the question.
#pragma once
#include "model.hpp"
#include "workspace.hpp"

namespace mason {

/// The embeddings server used for code search, and optionally a reranker. Any server that speaks the OpenAI
/// `/v1/embeddings` shape works, for example `llama-server --embedding`.
struct EmbeddingSettings {
    std::string url, model, key;
    /// A server that answers `/v1/rerank`, used to reorder the best matches. Empty for none.
    std::string rerank_url, rerank_model;
    bool operator==(const EmbeddingSettings& other) const {
        return url == other.url && model == other.model && key == other.key && rerank_url == other.rerank_url && rerank_model == other.rerank_model;
    }
};

class SemanticModels {
public:
    virtual ~SemanticModels() = default;
    /// Names the embeddings model; saved vectors are keyed by it.
    virtual std::string identity() const = 0;
    virtual bool can_rerank() const = 0;
    virtual std::vector<std::vector<float>> embed(const std::vector<std::string>& texts, const CancelToken& cancel) = 0;
    virtual std::vector<double> rerank(const std::string& query, const std::vector<std::string>& documents, const CancelToken& cancel) = 0;
};

/// The servers in `EmbeddingSettings`.
class EmbeddingServers : public SemanticModels {
public:
    explicit EmbeddingServers(EmbeddingSettings settings) : settings_(std::move(settings)) {}
    std::string identity() const override { return settings_.url + "|" + settings_.model; }
    bool can_rerank() const override { return !settings_.rerank_url.empty(); }
    std::vector<std::vector<float>> embed(const std::vector<std::string>& texts, const CancelToken& cancel) override;
    std::vector<double> rerank(const std::string& query, const std::vector<std::string>& documents, const CancelToken& cancel) override;
    /// Embeds one short text, to tell the user whether the server works.
    std::string probe();

private:
    json post(const std::string& base, const std::string& path, const json& body, const CancelToken& cancel);
    EmbeddingSettings settings_;
};

struct SemanticHit {
    std::string path;
    int start_line = 0, end_line = 0;
    double score = 0;
    std::string preview;
};

struct SemanticResult {
    std::vector<SemanticHit> hits;
    size_t files = 0, chunks = 0;
    bool partial = false, reranked = false;
    /// The result as the model reads it.
    std::string report() const;
};

namespace semantic {

struct Chunk {
    int start = 0, end = 0;
    std::string text;
};

/// The saved choice, or nothing when code search by meaning is off.
std::optional<EmbeddingSettings> load(const fs::path& home = data_home());
/// Saves the choice after checking the addresses; nothing turns code search by meaning off.
void save(const std::optional<EmbeddingSettings>& settings, const fs::path& home = data_home());
/// Overlapping windows of lines, each labelled with its file so the model that embeds it knows where it is from.
std::vector<Chunk> chunks(const std::string& path, const std::string& content);
SemanticResult search(const Workspace& workspace, SemanticModels& models, const std::string& query, const std::string& directory = ".", size_t limit = 8, const CancelToken& cancel = nullptr);
/// The typed form: what followed "/embeddings".
std::string command(const std::string& rest, const fs::path& home = data_home());
extern const char* const kHelp;

}  // namespace semantic
}  // namespace mason
