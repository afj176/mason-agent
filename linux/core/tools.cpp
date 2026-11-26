#include "tools.hpp"

#include <algorithm>
#include <chrono>
#include <regex>

namespace mason {

namespace workflows {

std::map<std::string, ProjectWorkflow> load(const Workspace& workspace) {
    std::map<std::string, ProjectWorkflow> found;
    if (!workspace.exists("mason.workflows.json")) return found;
    const json parsed = json::parse(workspace.read("mason.workflows.json"), nullptr, false);
    if (!parsed.is_object() || parsed.value("version", 0) != 1 || !parsed.contains("workflows") || !parsed["workflows"].is_object() || parsed["workflows"].size() > 24) {
        throw Error("mason.workflows.json must be version 1 with at most 24 workflows.");
    }
    static const std::regex name_pattern("^[A-Za-z][A-Za-z0-9_-]{0,49}$");
    for (auto item = parsed["workflows"].begin(); item != parsed["workflows"].end(); ++item) {
        const json& value = item.value();
        ProjectWorkflow workflow;
        bool valid = std::regex_match(item.key(), name_pattern) && value.is_object() && value.contains("executable") && value["executable"].is_string();
        if (valid) {
            workflow.executable = value["executable"].get<std::string>();
            valid = !trim(workflow.executable).empty();
        }
        if (valid && value.contains("arguments")) {
            valid = value["arguments"].is_array() && value["arguments"].size() <= 40;
            for (const json& argument : valid ? value["arguments"] : json::array()) {
                if (!argument.is_string() || argument.get<std::string>().size() > 4000) { valid = false; break; }
                workflow.arguments.push_back(argument.get<std::string>());
            }
        }
        if (!valid) throw Error("The workflow " + item.key() + " in mason.workflows.json is not valid.");
        if (value.contains("directory") && value["directory"].is_string()) workflow.directory = value["directory"].get<std::string>();
        if (workflow.directory != ".") workspace.resolve(workflow.directory);
        found[item.key()] = std::move(workflow);
    }
    return found;
}

/// A word quoted so the shell passes it on exactly.
static std::string quoted(const std::string& word) { return "'" + replace_all(word, "'", "'\\''") + "'"; }

std::string command_line(const ProjectWorkflow& workflow) {
    std::string command = quoted(workflow.executable);
    for (const std::string& argument : workflow.arguments) command += " " + quoted(argument);
    return workflow.directory == "." ? command : "cd " + quoted(workflow.directory) + " && " + command;
}

}  // namespace workflows

namespace skills {

std::vector<Definition> list(const Workspace& workspace) {
    static const std::regex name_pattern("^[A-Za-z0-9][A-Za-z0-9_.-]{0,47}$");
    std::map<std::string, Definition> found;
    for (const char* folder : {".mason/skills", ".claude/skills", ".agents/skills"}) {
        std::vector<std::string> names;
        std::error_code failure;
        for (const fs::directory_entry& entry : fs::directory_iterator(fs::path(workspace.root()) / folder, failure)) names.push_back(entry.path().filename().string());
        std::sort(names.begin(), names.end());
        if (names.size() > 64) names.resize(64);
        for (const std::string& name : names) {
            if (!std::regex_match(name, name_pattern) || found.count(to_lower(name))) continue;
            const std::string relative = std::string(folder) + "/" + name + "/SKILL.md";
            try {
                // Read through the workspace, so a link out of the project or an oversized file is refused.
                found[to_lower(name)] = customization::parse(name, workspace.read(relative), relative);
            } catch (const std::exception&) {
            }
        }
    }
    std::vector<Definition> result;
    for (auto& [key, definition] : found) result.push_back(std::move(definition));
    std::sort(result.begin(), result.end(), [](const Definition& a, const Definition& b) { return to_lower(a.name) < to_lower(b.name); });
    return result;
}

}  // namespace skills

namespace repository_map {

namespace {
constexpr size_t kMaxFiles = 400, kMaxBytes = 48 * 1024;

struct Family {
    std::vector<std::string> extensions;
    std::regex pattern;  ///< Captures the name being defined.
};

const std::vector<Family>& families() {
    static const std::vector<Family> list = {
        {{"py"}, std::regex(R"(^(?:async\s+)?(?:def|class)\s+([A-Za-z_][A-Za-z0-9_]*))")},
        {{"js", "jsx", "ts", "tsx", "mjs", "cjs"}, std::regex(R"(^(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:function\*?|class|interface|type|enum|const)\s+([A-Za-z_$][A-Za-z0-9_$]*))")},
        {{"swift"}, std::regex(R"(^(?:public\s+|private\s+|internal\s+|fileprivate\s+|open\s+|final\s+|static\s+)*(?:func|class|struct|enum|protocol|extension|actor)\s+([A-Za-z_][A-Za-z0-9_.]*))")},
        {{"go"}, std::regex(R"(^(?:func\s+(?:\([^)]*\)\s*)?|type\s+)([A-Za-z_][A-Za-z0-9_]*))")},
        {{"rs"}, std::regex(R"(^(?:pub(?:\([a-z]+\))?\s+)?(?:async\s+)?(?:fn|struct|enum|trait|impl|mod)\s+([A-Za-z_][A-Za-z0-9_]*))")},
        {{"c", "h", "cc", "cpp", "cxx", "hpp", "hh"}, std::regex(R"(^(?:class|struct|enum(?:\s+class)?|namespace)\s+([A-Za-z_][A-Za-z0-9_]*))")},
        {{"cs", "java", "kt"}, std::regex(R"(^\s{0,8}(?:public|internal|private|protected)\s+(?:static\s+|final\s+|sealed\s+|abstract\s+|partial\s+|data\s+)*(?:class|interface|enum|record|struct|object|fun)\s+([A-Za-z_][A-Za-z0-9_]*))")},
        {{"rb"}, std::regex(R"(^\s{0,4}(?:def|class|module)\s+([A-Za-z_][A-Za-z0-9_.?!]*))")},
        {{"php"}, std::regex(R"(^\s{0,4}(?:abstract\s+|final\s+)?(?:function|class|interface|trait)\s+([A-Za-z_][A-Za-z0-9_]*))")},
    };
    return list;
}
}  // namespace

std::string outline(const Workspace& workspace, const std::string& under) {
    const FileListing listing = workspace.files(under, kMaxFiles);
    std::string text;
    size_t shown = 0;
    for (const std::string& path : listing.paths) {
        std::string entry = path;
        try {
            const std::vector<std::string> rows = split(workspace.read(path), '\n');
            entry += "  (" + std::to_string(rows.size()) + " lines)";
            std::string kind = fs::path(path).extension().string();
            if (!kind.empty()) kind = to_lower(kind.substr(1));
            for (const Family& family : families()) {
                if (std::find(family.extensions.begin(), family.extensions.end(), kind) == family.extensions.end()) continue;
                std::vector<std::string> names;
                for (const std::string& row : rows) {
                    if (names.size() >= 40) break;
                    const std::string line = row.size() > 300 ? row.substr(0, 300) : row;
                    std::smatch match;
                    if (std::regex_search(line, match, family.pattern) && match.size() > 1) names.push_back(match[1].str());
                }
                if (!names.empty()) entry += ": " + join(names, ", ");
                break;
            }
        } catch (const std::exception&) {
            // Not text, or too large: listed by name alone.
        }
        if (text.size() + entry.size() > kMaxBytes) break;
        text += (text.empty() ? "" : "\n") + entry;
        shown++;
    }
    if (text.empty()) return "No files.";
    if (shown < listing.paths.size() || listing.truncated) text += "\n[" + std::to_string(shown) + " files shown. Map a subfolder to see more.]";
    return text;
}

}  // namespace repository_map

std::string Jobs::start(const std::string& command, const Workspace& workspace, bool sandboxed) {
    auto job = std::make_shared<Job>();
    job->command = command;
    const std::string id = new_id().substr(0, 8);
    {
        std::lock_guard<std::mutex> guard(lock_);
        size_t running = 0;
        for (const auto& [key, existing] : jobs_) {
            if (existing->done()) continue;
            running++;
            if (existing->command == command) throw Error("That command is already running as a job. Poll or stop it.");
        }
        if (jobs_.size() >= kMaxTotal) throw Error("This request has started " + std::to_string(kMaxTotal) + " jobs already.");
        if (running >= kMaxRunning) throw Error("Three jobs are running. Stop one before starting another.");
        jobs_[id] = job;
    }
    job->thread = std::thread([this, job, command, workspace, sandboxed] {
        std::optional<CommandResult> result;
        std::string failure;
        try {
            result = shell::run(command, workspace, sandboxed, kTimeoutSeconds, job->cancel, [this, job](const char* data, size_t size) {
                std::lock_guard<std::mutex> guard(lock_);
                if (job->output.size() < 256 * 1024) job->output.append(data, size);
            });
        } catch (const Cancelled&) {
            failure = "stopped";
        } catch (const std::exception& error) {
            failure = error.what();
        }
        std::lock_guard<std::mutex> guard(lock_);
        job->result = result;
        job->failure = failure;
    });
    return id;
}

std::string Jobs::observe(const std::string& id, bool stop, int wait_ms, const CancelToken& cancel) {
    std::shared_ptr<Job> job;
    {
        std::lock_guard<std::mutex> guard(lock_);
        const auto found = jobs_.find(id);
        if (found == jobs_.end()) {
            std::vector<std::string> ids;
            for (const auto& [key, existing] : jobs_) ids.push_back(key);
            throw Error("No job " + id + " in this request. Jobs: " + join(ids, ", "));
        }
        job = found->second;
    }
    if (wait_ms < 0 || wait_ms > 10000) throw Error("wait_ms must be between 0 and 10000.");
    if (stop) {
        job->cancel->store(true);
        if (job->thread.joinable()) job->thread.join();
    } else {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(wait_ms);
        while (std::chrono::steady_clock::now() < deadline) {
            check_cancel(cancel);
            {
                std::lock_guard<std::mutex> guard(lock_);
                if (job->done()) break;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(25));
        }
    }
    std::lock_guard<std::mutex> guard(lock_);
    std::string text = is_valid_utf8(job->output) ? job->output : std::string();
    if (text.empty() && !job->output.empty()) {
        text = job->output;
        for (char& c : text) {
            if (static_cast<unsigned char>(c) >= 0x80) c = '?';
        }
    }
    const std::string tail = utf8_length(text) > 8000 ? "[… earlier output omitted]\n" + utf8_suffix(text, 8000) : text;
    const std::string state = job->result ? "exited " + std::to_string(job->result->exit_code) + (job->result->timed_out ? " (stopped at the 30 minute limit)" : "")
                                          : !job->failure.empty() ? job->failure : "running";
    return "job " + id + ": " + state + "\n" + tail;
}

void Jobs::stop_all() {
    std::vector<std::shared_ptr<Job>> all;
    {
        std::lock_guard<std::mutex> guard(lock_);
        for (const auto& [key, job] : jobs_) all.push_back(job);
        jobs_.clear();
    }
    for (const auto& job : all) job->cancel->store(true);
    for (const auto& job : all) {
        if (job->thread.joinable()) job->thread.join();
    }
}

bool Jobs::empty() {
    std::lock_guard<std::mutex> guard(lock_);
    return jobs_.empty();
}

}  // namespace mason
