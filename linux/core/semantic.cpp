#include "semantic.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <map>
#include <mutex>
#include <set>

#include "planning.hpp"

namespace mason {

json EmbeddingServers::post(const std::string& base, const std::string& path, const json& body, const CancelToken& cancel) {
    std::vector<std::string> headers = {"Content-Type: application/json"};
    if (!settings_.key.empty()) headers.push_back("Authorization: Bearer " + settings_.key);
    const HttpResponse reply = http_send("POST", validated_endpoint(base) + path, headers, body.dump(-1, ' ', false, json::error_handler_t::replace), 120, 256 * 1024 * 1024, nullptr, cancel);
    if (reply.status != 200) throw Error("The server at " + base + " answered HTTP " + std::to_string(reply.status) + ".");
    const json parsed = json::parse(reply.body, nullptr, false);
    if (!parsed.is_object()) throw Error("The server at " + base + " did not answer with JSON.");
    return parsed;
}

std::vector<std::vector<float>> EmbeddingServers::embed(const std::vector<std::string>& texts, const CancelToken& cancel) {
    const json answer = post(settings_.url, "v1/embeddings", {{"model", settings_.model}, {"input", texts}}, cancel);
    std::vector<std::vector<float>> vectors(texts.size());
    size_t filled = 0;
    if (answer.contains("data") && answer["data"].is_array()) {
        size_t position = 0;
        for (const json& row : answer["data"]) {
            const size_t index = row.is_object() && row.contains("index") && row["index"].is_number_unsigned() ? row["index"].get<size_t>() : position;
            position++;
            if (index >= vectors.size() || !row.is_object() || !row.contains("embedding") || !row["embedding"].is_array() || !vectors[index].empty()) continue;
            for (const json& value : row["embedding"]) {
                if (value.is_number()) vectors[index].push_back(value.get<float>());
            }
            if (!vectors[index].empty()) filled++;
        }
    }
    if (filled != texts.size()) throw Error("The embeddings server answered with a different number of vectors than it was sent.");
    return vectors;
}

std::vector<double> EmbeddingServers::rerank(const std::string& query, const std::vector<std::string>& documents, const CancelToken& cancel) {
    const json answer = post(settings_.rerank_url, "v1/rerank", {{"model", settings_.rerank_model}, {"query", query}, {"documents", documents}}, cancel);
    std::vector<double> scores(documents.size(), -INFINITY);
    if (answer.contains("results") && answer["results"].is_array()) {
        for (const json& row : answer["results"]) {
            if (!row.is_object() || !row.contains("index") || !row["index"].is_number_unsigned() || !row.contains("relevance_score") || !row["relevance_score"].is_number()) continue;
            const size_t index = row["index"].get<size_t>();
            if (index < scores.size()) scores[index] = row["relevance_score"].get<double>();
        }
    }
    return scores;
}

std::string EmbeddingServers::probe() {
    const std::vector<std::vector<float>> vectors = embed({"func total(items) { return items.reduce(0, +) }"}, nullptr);
    return "The server returned " + std::to_string(vectors[0].size()) + "-dimension embeddings. Code search by meaning is ready.";
}

std::string SemanticResult::report() const {
    std::string text = "Ranked by meaning, not exact text (" + std::to_string(files) + " files, " + std::to_string(chunks) + " passages" + (partial ? ", not the whole project" : "") +
                       (reranked ? ", reranked" : "") + "). Read the lines before editing; use search_text for exact strings.";
    if (hits.empty()) text += "\nNothing matched.";
    for (const SemanticHit& hit : hits) {
        char score[32];
        std::snprintf(score, sizeof score, "%.3f", hit.score);
        text += "\n\n" + hit.path + " lines " + std::to_string(hit.start_line) + "–" + std::to_string(hit.end_line) + " (score " + score + ")\n" + hit.preview;
    }
    return text;
}

namespace semantic {

const char* const kHelp =
    "/embeddings                       Show the embeddings server used to search code by meaning\n"
    "/embeddings URL MODEL [KEY]       Choose one (any server with /v1/embeddings, such as llama-server --embedding)\n"
    "/embeddings rerank URL MODEL      Also reorder the best matches with a /v1/rerank server   ·   rerank off\n"
    "/embeddings test                  Embed a line of code to check the server answers\n"
    "/embeddings off                   Stop offering search by meaning";

namespace {

constexpr int kChunkLines = 40, kOverlap = 8;
constexpr size_t kMaxFiles = 1000, kMaxChunks = 6000, kBatch = 32;

// Vectors last as long as the program: model | file hash | first line -> vector.
std::mutex cache_lock;
std::map<std::string, std::vector<float>> cache;

bool skipped(const std::string& path) {
    static const std::set<std::string> folders = {"node_modules", "bin", "obj", "dist", "build", ".build", ".git", ".venv", "venv", "__pycache__"};
    const std::vector<std::string> parts = split(path, '/');
    for (size_t i = 0; i + 1 < parts.size(); i++) {
        if (folders.count(parts[i])) return true;
    }
    const std::string& name = parts.back();
    return ends_with(name, ".min.js") || ends_with(name, ".min.css") || name == "package-lock.json";
}

bool searchable(const std::string& path) {
    static const std::set<std::string> extensions = {".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".rb", ".go", ".rs", ".java", ".kt", ".swift", ".c", ".h", ".cpp", ".hpp",
                                                     ".cc", ".m", ".mm", ".php", ".dart", ".scala", ".sql", ".html", ".css", ".scss", ".vue", ".svelte", ".md", ".ps1", ".sh", ".json",
                                                     ".yaml", ".yml", ".toml", ".xaml", ".csproj"};
    return extensions.count(to_lower(fs::path(path).extension().string())) > 0 && !skipped(path);
}

std::vector<float> normalized(std::vector<float> vector) {
    double length = 0;
    for (const float value : vector) length += static_cast<double>(value) * value;
    length = std::sqrt(length);
    if (length == 0) return vector;
    for (float& value : vector) value = static_cast<float>(value / length);
    return vector;
}

fs::path file(const fs::path& home) { return home / "embeddings.json"; }

}  // namespace

std::optional<EmbeddingSettings> load(const fs::path& home) {
    std::error_code failure;
    if (!fs::exists(file(home), failure)) return std::nullopt;
    json saved;
    try {
        saved = json::parse(read_file(file(home)), nullptr, false);
    } catch (const std::exception&) {
        return std::nullopt;
    }
    if (!saved.is_object() || saved.value("url", "").empty()) return std::nullopt;
    return EmbeddingSettings{saved.value("url", ""), saved.value("model", ""), saved.value("key", ""), saved.value("rerankUrl", ""), saved.value("rerankModel", "")};
}

void save(const std::optional<EmbeddingSettings>& settings, const fs::path& home) {
    std::error_code failure;
    if (!settings) {
        fs::remove(file(home), failure);
        return;
    }
    validated_endpoint(settings->url);
    if (!settings->rerank_url.empty()) validated_endpoint(settings->rerank_url);
    fs::create_directories(home, failure);
    write_file_atomic(file(home), json{{"url", settings->url}, {"model", settings->model}, {"key", settings->key}, {"rerankUrl", settings->rerank_url}, {"rerankModel", settings->rerank_model}}.dump(1));
}

std::vector<Chunk> chunks(const std::string& path, const std::string& content) {
    const std::vector<std::string> lines = split(replace_all(content, "\r\n", "\n"), '\n');
    std::vector<Chunk> result;
    for (size_t start = 0; start < lines.size(); start += kChunkLines - kOverlap) {
        const size_t end = std::min(lines.size(), start + kChunkLines);
        std::string body;
        for (size_t i = start; i < end; i++) body += (i > start ? "\n" : "") + lines[i];
        if (trim(body).size() >= 20) result.push_back({static_cast<int>(start) + 1, static_cast<int>(end), "File: " + path + "\n" + utf8_prefix(body, 6000)});
        if (end == lines.size()) break;
    }
    return result;
}

SemanticResult search(const Workspace& workspace, SemanticModels& models, const std::string& query, const std::string& directory, size_t limit, const CancelToken& cancel) {
    const std::string question = trim(query);
    if (question.empty() || utf8_length(question) > 2000) throw Error("Describe what to find in 1–2,000 characters.");
    const FileListing listing = workspace.files(directory.empty() ? "." : directory, kMaxFiles);
    struct Passage {
        std::string key, path;
        int start, end;
        std::string text;
    };
    std::vector<Passage> all;
    std::vector<const Passage*> pending;
    SemanticResult result;
    result.partial = listing.truncated;
    all.reserve(kMaxChunks);
    for (const std::string& path : listing.paths) {
        check_cancel(cancel);
        if (!searchable(path)) continue;
        if (all.size() >= kMaxChunks) { result.partial = true; break; }
        std::optional<std::string> data;
        try {
            data = workspace.data(path);
        } catch (const std::exception&) {
        }
        if (!data || data->size() > 2 * 1024 * 1024 || !is_valid_utf8(*data)) continue;
        result.files++;
        const std::string hash = sha256_hex(*data);
        for (Chunk& chunk : chunks(path, *data)) {
            if (all.size() >= kMaxChunks) { result.partial = true; break; }
            all.push_back({models.identity() + "|" + hash + "|" + std::to_string(chunk.start), path, chunk.start, chunk.end, std::move(chunk.text)});
            std::lock_guard<std::mutex> guard(cache_lock);
            if (!cache.count(all.back().key)) pending.push_back(&all.back());
        }
    }
    result.chunks = all.size();
    for (size_t index = 0; index < pending.size(); index += kBatch) {
        check_cancel(cancel);
        std::vector<std::string> texts;
        for (size_t i = index; i < std::min(pending.size(), index + kBatch); i++) texts.push_back(pending[i]->text);
        std::vector<std::vector<float>> embedded = models.embed(texts, cancel);
        std::lock_guard<std::mutex> guard(cache_lock);
        for (size_t i = 0; i < embedded.size() && i < texts.size(); i++) cache[pending[index + i]->key] = normalized(std::move(embedded[i]));
    }
    const std::vector<float> target = normalized(models.embed({question}, cancel).at(0));
    std::vector<std::pair<const Passage*, double>> ranked;
    {
        std::lock_guard<std::mutex> guard(cache_lock);
        for (const Passage& passage : all) {
            const auto found = cache.find(passage.key);
            if (found == cache.end() || found->second.size() != target.size()) continue;
            double score = 0;
            for (size_t i = 0; i < target.size(); i++) score += static_cast<double>(found->second[i]) * target[i];
            ranked.push_back({&passage, score});
        }
    }
    const auto by_score = [](const auto& a, const auto& b) { return a.second > b.second; };
    std::stable_sort(ranked.begin(), ranked.end(), by_score);
    if (ranked.size() > std::max<size_t>(limit * 4, 24)) ranked.resize(std::max<size_t>(limit * 4, 24));
    if (models.can_rerank() && ranked.size() > 1) {
        try {
            std::vector<std::string> documents;
            for (const auto& item : ranked) documents.push_back(item.first->text);
            const std::vector<double> scores = models.rerank(question, documents, cancel);
            if (scores.size() == ranked.size()) {
                for (size_t i = 0; i < ranked.size(); i++) ranked[i].second = scores[i];
                std::stable_sort(ranked.begin(), ranked.end(), by_score);
                result.reranked = true;
            }
        } catch (const Cancelled&) {
            throw;
        } catch (const std::exception&) {
            // Without the reranker the embedding order stands.
        }
    }
    for (const auto& [passage, score] : ranked) {
        // Overlapping windows of one file collapse to the best one.
        const bool overlaps = std::any_of(result.hits.begin(), result.hits.end(), [&](const SemanticHit& hit) { return hit.path == passage->path && passage->start <= hit.end_line && passage->end >= hit.start_line; });
        if (overlaps) continue;
        const std::string body = passage->text.substr(passage->text.find('\n') + 1);
        result.hits.push_back({passage->path, passage->start, passage->end, score, utf8_length(body) > 700 ? utf8_prefix(body, 700) + "…" : body});
        if (result.hits.size() >= limit) break;
    }
    return result;
}

std::string command(const std::string& rest, const fs::path& home) {
    const std::vector<std::string> words = split(trim(rest), ' ', false);
    const std::optional<EmbeddingSettings> saved = load(home);
    const std::string verb = words.empty() ? "" : to_lower(words[0]);
    if (verb.empty()) {
        if (!saved) return std::string("Code search by meaning is off: no embeddings server is chosen.\n") + kHelp;
        return "Embeddings: " + saved->model + " at " + saved->url + (saved->rerank_url.empty() ? "" : "\nReranker: " + saved->rerank_model + " at " + saved->rerank_url) +
               "\nMason offers the model semantic_search in every session.";
    }
    if (verb == "help") return kHelp;
    if (verb == "off") {
        save(std::nullopt, home);
        return "Code search by meaning is off.";
    }
    if (verb == "test") {
        if (!saved) throw Error("Choose a server first: /embeddings URL MODEL");
        return EmbeddingServers(*saved).probe();
    }
    if (verb == "rerank") {
        if (!saved) throw Error("Choose an embeddings server first: /embeddings URL MODEL");
        EmbeddingSettings changed = *saved;
        if (words.size() == 2 && to_lower(words[1]) == "off") { changed.rerank_url.clear(); changed.rerank_model.clear(); }
        else if (words.size() == 3) { changed.rerank_url = words[1]; changed.rerank_model = words[2]; }
        else throw Error("Write it as /embeddings rerank URL MODEL, or /embeddings rerank off.");
        save(changed, home);
        return changed.rerank_url.empty() ? "Matches are no longer reranked." : "The best matches are reranked by " + changed.rerank_model + ".";
    }
    if (words.size() != 2 && words.size() != 3) throw Error("Write it as /embeddings URL MODEL, for example /embeddings http://127.0.0.1:8081/ nomic-embed-text");
    save(EmbeddingSettings{words[0], words[1], words.size() == 3 ? words[2] : "", saved ? saved->rerank_url : "", saved ? saved->rerank_model : ""}, home);
    return "Mason will search code by meaning with " + words[1] + " at " + words[0] + ". /embeddings test checks it answers.";
}

}  // namespace semantic
}  // namespace mason
