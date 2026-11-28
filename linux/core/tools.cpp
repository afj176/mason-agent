#include "tools.hpp"

#include "bundles.hpp"
#include "policy.hpp"

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <csignal>
#include <regex>

#include <fcntl.h>
#include <pthread.h>
#include <unistd.h>

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
        if (value.contains("events") && value["events"].is_array()) {
            for (const json& event : value["events"]) {
                const std::string name = event.is_string() ? event.get<std::string>() : "";
                if (name != "afterPlan" && name != "afterEdit" && name != "beforeVerify") throw Error("The workflow " + item.key() + " names an event other than afterPlan, afterEdit or beforeVerify.");
                workflow.events.push_back(name);
            }
        }
        if (value.contains("beforeVerify") && value["beforeVerify"].is_boolean() && value["beforeVerify"].get<bool>() && std::find(workflow.events.begin(), workflow.events.end(), "beforeVerify") == workflow.events.end()) {
            workflow.events.push_back("beforeVerify");
        }
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

namespace {

constexpr size_t kMaxPerFolder = 64;
constexpr uintmax_t kMaxSkillBytes = 256 * 1024;

std::vector<std::string> names_in(const fs::path& folder) {
    std::vector<std::string> names;
    std::error_code failure;
    for (const fs::directory_entry& entry : fs::directory_iterator(folder, failure)) names.push_back(entry.path().filename().string());
    std::sort(names.begin(), names.end());
    if (names.size() > kMaxPerFolder) names.resize(kMaxPerFolder);
    return names;
}

/// A skill from its SKILL.md. The name is the folder's; the description comes from the front matter only.
Skill made(const std::string& name, const std::string& text, const std::string& source, const std::string& where, const fs::path& file) {
    const Definition parsed = customization::parse(name, text, where);
    Skill skill;
    skill.name = name;
    const auto described = parsed.fields.find("description");
    skill.description = described != parsed.fields.end() && !trim(described->second).empty() ? trim(described->second) : "No description";
    if (utf8_length(skill.description) > 300) skill.description = utf8_prefix(skill.description, 300) + "…";
    skill.body = parsed.body;
    skill.source = source;
    skill.where = where;
    skill.file = file;
    return skill;
}

/// Skills kept outside a project: in a plain folder (Mason's own) and in installed bundles.
void outside(std::vector<Skill>& found, const fs::path& home, bool own) {
    auto take = [&](const fs::path& folder, const std::string& source, const std::function<std::string(const std::string&)>& where) {
        for (const std::string& name : names_in(folder)) {
            std::error_code failure;
            const fs::path file = folder / name / "SKILL.md";
            // A link could point anywhere on the computer, so only ordinary files are read.
            if (!skills::valid_name(name) || fs::is_symlink(folder / name, failure) || fs::is_symlink(file, failure) || !fs::is_regular_file(file, failure) || fs::file_size(file, failure) > kMaxSkillBytes) continue;
            try {
                found.push_back(made(name, read_file(file), source, where(name), file));
            } catch (const std::exception&) {
            }
        }
    };
    if (own) take(home / "skills", "computer", [&](const std::string& name) { return (home / "skills" / name).string(); });
    for (const InstalledBundle& bundle : bundles::list(home)) take(bundle.folder / "skills", "bundle " + bundle.name, [&](const std::string&) { return "bundle " + bundle.name; });
}

/// Marks every skill after the first of a name as hidden by that first one.
void shadow(std::vector<Skill>& found) {
    std::map<std::string, std::string> first;
    for (Skill& skill : found) {
        const auto seen = first.find(to_lower(skill.name));
        if (seen != first.end()) skill.shadowed_by = seen->second;
        else first[to_lower(skill.name)] = skill.where;
    }
}

}  // namespace

const char* const kUsage =
    "/skills                       Every skill this project can see, on, off or hidden\n"
    "/skills show NAME             Where a skill is and its whole SKILL.md\n"
    "/skills new NAME [description] Make .mason/skills/NAME/SKILL.md\n"
    "/skills on NAME · off NAME    Switch a skill on or off for this project\n"
    "/skill NAME [request]         Run a request that follows the skill";

const std::vector<std::string>& folders() {
    static const std::vector<std::string> list = {".mason/skills", ".claude/skills", ".agents/skills"};
    return list;
}

bool valid_name(const std::string& name) {
    static const std::regex pattern("^[A-Za-z0-9][A-Za-z0-9_.-]{0,47}$");
    return std::regex_match(name, pattern);
}

std::vector<Skill> all(const Workspace& workspace, const fs::path& home) {
    std::vector<Skill> found;
    for (const std::string& folder : folders()) {
        const std::string source = folder == ".mason/skills" ? "project" : folder.substr(0, folder.find('/'));
        for (const std::string& name : names_in(fs::path(workspace.root()) / folder)) {
            if (!valid_name(name)) continue;
            const std::string relative = folder + "/" + name + "/SKILL.md";
            try {
                // Read through the workspace, so a link out of the project or an oversized file is refused.
                const fs::path file = workspace.resolve(relative);
                std::error_code failure;
                if (!fs::is_regular_file(file, failure) || fs::file_size(file, failure) > kMaxSkillBytes) continue;
                found.push_back(made(name, workspace.read(relative), source, folder + "/" + name, file));
            } catch (const std::exception&) {
            }
        }
    }
    outside(found, home, false);
    shadow(found);
    const std::vector<std::string> off = policies::disabled_skills(home, workspace.root());
    for (Skill& skill : found) {
        skill.enabled = std::none_of(off.begin(), off.end(), [&](const std::string& name) { return to_lower(name) == to_lower(skill.name); });
    }
    return found;
}

std::vector<Skill> computer(const fs::path& home) {
    std::vector<Skill> found;
    outside(found, home, true);
    shadow(found);
    return found;
}

std::vector<Definition> list(const Workspace& workspace, const fs::path& home) {
    std::vector<Definition> result;
    for (const Skill& skill : all(workspace, home)) {
        if (skill.shadowed() || !skill.enabled) continue;
        Definition definition;
        definition.name = skill.name;
        definition.description = skill.description;
        definition.body = skill.body;
        definition.source = skill.where;
        result.push_back(std::move(definition));
    }
    return result;
}

std::string read(const std::vector<Skill>& known, const std::string& name) {
    const std::string wanted = to_lower(trim(name));
    std::vector<std::string> names;
    for (const Skill& skill : known) {
        if (skill.shadowed()) continue;
        if (to_lower(skill.name) == wanted) {
            if (!skill.enabled) throw Error("The skill " + skill.name + " is switched off for this project.");
            return "Skill " + skill.name + " (" + skill.where + "):\n" + skill.body;
        }
        if (skill.enabled) names.push_back(skill.name);
    }
    throw Error("No skill named " + trim(name) + ". Skills: " + (names.empty() ? std::string("none") : join(names, ", ")));
}

std::string prompt(const std::vector<Skill>& known) {
    std::string lines;
    for (const Skill& skill : known) {
        if (!skill.shadowed() && skill.enabled) lines += "\n- " + skill.name + ": " + skill.description;
    }
    if (lines.empty()) return "";
    return "Skills: instructions for particular kinds of work." + lines + "\nCall read_skill with the name before doing work a skill covers.";
}

std::string template_text(const std::string& name, const std::string& description) {
    return "---\nname: " + name + "\ndescription: " + description + "\n---\n\n# " + name + "\n\n## When to use\n\n## Steps\n\n";
}

fs::path create(const Workspace& workspace, const std::string& name, const std::string& description) {
    const std::string wanted = trim(name);
    if (!valid_name(wanted)) throw Error("A skill's name is 1 to 48 letters, digits, dots, dashes or underscores, starting with a letter or digit.");
    const std::string said = trim(replace_all(replace_all(description, "\r", " "), "\n", " "));
    if (utf8_length(said) > 300) throw Error("A description is at most 300 characters.");
    std::error_code failure;
    const fs::path folder = fs::path(workspace.root()) / ".mason/skills";
    for (const std::string& existing : names_in(folder)) {
        if (to_lower(existing) == to_lower(wanted)) throw Error("A skill named " + existing + " is already in .mason/skills.");
    }
    if (fs::exists(folder / wanted, failure)) throw Error("A skill named " + wanted + " is already in .mason/skills.");
    const std::string relative = ".mason/skills/" + wanted + "/SKILL.md";
    workspace.write(relative, template_text(wanted, said));
    return workspace.resolve(relative);
}

std::string request(const std::vector<Skill>& known, const std::string& name, const std::string& asked) {
    const std::string wanted = to_lower(trim(name));
    for (const Skill& skill : known) {
        if (skill.shadowed() || to_lower(skill.name) != wanted) continue;
        if (!skill.enabled) throw Error("The skill " + skill.name + " is switched off for this project.");
        return "Follow the skill " + skill.name + ":\n\n" + skill.body + "\n\nRequest: " + trim(asked);
    }
    throw Error("No skill named " + trim(name) + ". /skills lists them.");
}

std::string command(const std::string& rest, const Workspace& workspace, const fs::path& home) {
    const std::vector<std::string> words = split(trim(rest), ' ', false);
    const std::string verb = words.empty() ? "" : to_lower(words[0]);
    const std::vector<Skill> known = all(workspace, home);
    auto find = [&](const std::string& name) -> const Skill& {
        for (const Skill& skill : known) {
            if (!skill.shadowed() && to_lower(skill.name) == to_lower(name)) return skill;
        }
        throw Error("No skill named " + name + ". /skills lists them.");
    };
    if (verb.empty() || verb == "list") {
        if (known.empty()) return "No skills yet. /skills new NAME makes one in .mason/skills.";
        size_t width = 4;
        for (const Skill& skill : known) width = std::max(width, skill.name.size());
        std::string text;
        for (const Skill& skill : known) {
            std::string source = skill.source;
            text += (text.empty() ? "" : "\n") + skill.name + std::string(width + 2 - skill.name.size(), ' ') + skill.state() + std::string(8 - skill.state().size(), ' ') + source +
                    std::string(source.size() < 14 ? 14 - source.size() : 1, ' ') + (skill.shadowed() ? "Hidden by " + skill.shadowed_by : skill.description);
        }
        return text;
    }
    if (verb == "show") {
        if (words.size() < 2) throw Error("Use /skills show NAME.");
        const Skill& skill = find(words[1]);
        return skill.file.string() + "\n\n" + read_file(skill.file);
    }
    if (verb == "on" || verb == "off") {
        if (words.size() < 2) throw Error("Use /skills " + verb + " NAME.");
        const Skill& skill = find(words[1]);
        policies::set_skill(skill.name, verb == "on", workspace.root(), home);
        return "The skill " + skill.name + " is " + verb + " for this project.";
    }
    return kUsage;
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

std::string Jobs::start(const std::string& command, const Workspace& workspace, bool sandboxed, bool interactive) {
    auto job = std::make_shared<Job>();
    job->command = command;
    job->interactive = interactive;
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
    int reading = -1;
    if (interactive) {
        int ends[2];
        if (pipe2(ends, O_CLOEXEC) != 0) {
            std::lock_guard<std::mutex> guard(lock_);
            jobs_.erase(id);
            throw Error("The terminal could not start: no pipe.");
        }
        reading = ends[0];
        // A line sent to a program that stopped reading must fail as an error, not block.
        fcntl(ends[1], F_SETFL, fcntl(ends[1], F_GETFL) | O_NONBLOCK);
        std::lock_guard<std::mutex> guard(lock_);
        job->input_fd = ends[1];
    }
    job->thread = std::thread([this, job, command, workspace, sandboxed, reading] {
        std::optional<CommandResult> result;
        std::string failure;
        try {
            result = shell::run(command, workspace, sandboxed, kTimeoutSeconds, job->cancel, [this, job](const char* data, size_t size) {
                std::lock_guard<std::mutex> guard(lock_);
                if (job->output.size() < 256 * 1024) job->output.append(data, size);
            }, reading);
        } catch (const Cancelled&) {
            failure = "stopped";
        } catch (const std::exception& error) {
            failure = error.what();
        }
        std::lock_guard<std::mutex> guard(lock_);
        job->result = result;
        job->failure = failure;
        if (job->input_fd >= 0) { ::close(job->input_fd); job->input_fd = -1; }
    });
    return id;
}

std::string Jobs::send(const std::string& id, const std::string& text, bool eof) {
    std::lock_guard<std::mutex> guard(lock_);
    const auto found = jobs_.find(id);
    if (found == jobs_.end()) throw Error("No terminal " + id + " in this request.");
    const std::shared_ptr<Job>& job = found->second;
    if (!job->interactive) throw Error("Job " + id + " was started with start_job and reads no input. Use start_terminal for a program you need to answer.");
    if (text.size() > 4000 || text.find('\n') != std::string::npos || text.find('\0') != std::string::npos) throw Error("terminal_input sends one line of up to 4,000 bytes.");
    if (job->done()) throw Error("Terminal " + id + " has ended. Read what it printed with terminal_status.");
    if (job->input_fd < 0) throw Error("The input of terminal " + id + " is already closed.");
    if (!text.empty() || !eof) {
        // Writing to a program that closed its input raises SIGPIPE. Hold it back for this thread and discard it, so the
        // write fails with an error instead of ending Mason, and nothing changes for the rest of the process.
        sigset_t pipe_signal, before;
        sigemptyset(&pipe_signal);
        sigaddset(&pipe_signal, SIGPIPE);
        pthread_sigmask(SIG_BLOCK, &pipe_signal, &before);
        const std::string line = text + "\n";
        size_t sent = 0;
        int problem = 0;
        while (sent < line.size()) {
            const ssize_t count = ::write(job->input_fd, line.data() + sent, line.size() - sent);
            if (count <= 0) { problem = errno ? errno : EPIPE; break; }
            sent += static_cast<size_t>(count);
        }
        if (problem == EPIPE) {
            const timespec none{0, 0};
            sigtimedwait(&pipe_signal, nullptr, &none);
        }
        pthread_sigmask(SIG_SETMASK, &before, nullptr);
        if (problem != 0) throw Error(problem == EAGAIN ? "The program in terminal " + id + " is not reading its input." : "The program in terminal " + id + " has closed its input.");
    }
    if (eof) { ::close(job->input_fd); job->input_fd = -1; }
    return eof ? "Sent, and closed the input of terminal " + id + ". Read what it prints with terminal_status." : "Sent to terminal " + id + ". Read what it prints with terminal_status.";
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
