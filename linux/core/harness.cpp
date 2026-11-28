#include "harness.hpp"

#include <algorithm>
#include <regex>

namespace mason {

// ---- what a command changed ------------------------------------------------------------------------------------

namespace snapshot {

Map capture(const Workspace& workspace, const Map& previous, bool* complete) {
    Map entries;
    bool all = true;
    uintmax_t total = 0;
    FileListing listing;
    try {
        listing = workspace.files(".", kMaxFiles);
    } catch (const Error&) {
        if (complete) *complete = false;
        return entries;
    }
    if (listing.truncated) all = false;
    for (const std::string& path : listing.paths) {
        const fs::path file = fs::path(workspace.root()) / path;
        std::error_code failure;
        const uintmax_t size = fs::file_size(file, failure);
        if (failure) continue;
        const fs::file_time_type modified = fs::last_write_time(file, failure);
        if (failure) continue;
        if (size > kMaxFileBytes || total + size > kMaxTotalBytes) {
            all = false;
            continue;
        }
        const auto known = previous.find(path);
        if (known != previous.end() && known->second.size == size && known->second.modified == modified) {
            entries[path] = known->second;
            total += size;
            continue;
        }
        try {
            auto data = std::make_shared<const std::string>(read_file(file));
            total += data->size();
            entries[path] = Entry{data->size(), modified, std::move(data)};
        } catch (const std::exception&) {
            // Unreadable right now (removed meanwhile, or no permission): not tracked.
        }
    }
    if (complete) *complete = all;
    return entries;
}

std::vector<Change> changes(const Map& before, const Map& after) {
    std::vector<Change> found;
    for (const auto& [path, old] : before) {
        const auto now = after.find(path);
        if (now == after.end()) found.push_back({path, old.data, nullptr});
        else if (*now->second.data != *old.data) found.push_back({path, old.data, now->second.data});
    }
    for (const auto& [path, now] : after) {
        if (!before.count(path)) found.push_back({path, nullptr, now.data});
    }
    std::sort(found.begin(), found.end(), [](const Change& a, const Change& b) { return a.path < b.path; });
    return found;
}

}  // namespace snapshot

std::string BlobStore::put(const std::string& data) const {
    Sha256 hasher;
    hasher.update(data.data(), data.size());
    const std::string name = hasher.hex();
    const fs::path file = folder_ / name;
    std::error_code failure;
    if (!fs::exists(file, failure)) {
        fs::create_directories(folder_);
        write_file_atomic(file, data);
    }
    return name;
}

std::optional<std::string> BlobStore::get(const std::string& name) const {
    if (name.size() != 64 || name.find_first_not_of("0123456789abcdef") != std::string::npos) return std::nullopt;
    try {
        return read_file(folder_ / name);
    } catch (const std::exception&) {
        return std::nullopt;
    }
}

// ---- custom agents and commands --------------------------------------------------------------------------------

namespace customization {
namespace {

const std::vector<std::string> kReserved = {"new", "queue", "compact", "undo", "redo", "help"};
const std::vector<std::string> kAgentFolders = {".mason/agents", ".agents/agents", ".claude/agents"};
const std::vector<std::string> kCommandFolders = {".mason/commands", ".claude/commands", ".opencode/command", ".opencode/commands"};
constexpr size_t kMaxBody = 8000, kMaxFiles = 64;

bool valid_name(const std::string& name) {
    static const std::regex pattern("^[A-Za-z0-9][A-Za-z0-9_.-]{0,47}$");
    return std::regex_match(name, pattern);
}

std::vector<Definition> load(const fs::path& home, const std::string& workspace, const std::vector<std::string>& project_folders, const std::string& profile_folder) {
    std::map<std::string, Definition> found;
    std::vector<std::pair<fs::path, std::string>> folders;  // folder, and its project-relative name ("" for the data folder)
    for (const std::string& folder : project_folders) folders.push_back({fs::path(workspace) / folder, folder});
    folders.push_back({home / profile_folder, ""});
    for (const auto& [folder, relative] : folders) {
        std::error_code failure;
        if (!fs::is_directory(folder, failure)) continue;
        std::vector<fs::path> files;
        for (const fs::directory_entry& entry : fs::directory_iterator(folder, failure)) {
            if (entry.path().extension() == ".md") files.push_back(entry.path());
        }
        std::sort(files.begin(), files.end(), [](const fs::path& a, const fs::path& b) { return to_lower(a.filename().string()) < to_lower(b.filename().string()); });
        if (files.size() > kMaxFiles) files.resize(kMaxFiles);
        for (const fs::path& file : files) {
            const std::string name = file.stem().string(), key = to_lower(name);
            if (!valid_name(name) || found.count(key) || std::find(kReserved.begin(), kReserved.end(), key) != kReserved.end()) continue;
            if (fs::is_symlink(file, failure) || !fs::is_regular_file(file, failure) || fs::file_size(file, failure) > 64 * 1024) continue;
            try {
                const std::string text = read_file(file);
                if (!is_valid_utf8(text)) continue;
                found[key] = parse(name, text, relative.empty() ? file.string() : relative + "/" + file.filename().string());
            } catch (const std::exception&) {
            }
        }
    }
    std::vector<Definition> result;
    for (auto& [key, definition] : found) result.push_back(std::move(definition));
    std::sort(result.begin(), result.end(), [](const Definition& a, const Definition& b) { return to_lower(a.name) < to_lower(b.name); });
    return result;
}

std::string strip(std::string value, const std::string& characters) {
    const size_t first = value.find_first_not_of(characters);
    if (first == std::string::npos) return "";
    return value.substr(first, value.find_last_not_of(characters) - first + 1);
}

std::vector<std::string> words(const std::string& text) {
    std::vector<std::string> found;
    std::string current;
    char quote = 0;
    for (const char c : text) {
        if (!quote && (c == '"' || c == '\'')) { quote = c; continue; }
        if (quote && c == quote) { quote = 0; continue; }
        if (!quote && std::isspace(static_cast<unsigned char>(c))) {
            if (!current.empty()) { found.push_back(current); current.clear(); }
            continue;
        }
        current += c;
    }
    if (!current.empty()) found.push_back(current);
    return found;
}

}  // namespace

std::vector<Definition> agents(const fs::path& home, const std::string& workspace) { return load(home, workspace, kAgentFolders, "agents"); }
std::vector<Definition> commands(const fs::path& home, const std::string& workspace) { return load(home, workspace, kCommandFolders, "commands"); }

Definition parse(const std::string& name, std::string text, const std::string& source) {
    text = replace_all(std::move(text), "\r\n", "\n");
    if (starts_with(text, "\xEF\xBB\xBF")) text.erase(0, 3);
    Definition definition;
    definition.source = source;
    std::string body = text;
    if (starts_with(text, "---\n")) {
        const size_t end = text.find("\n---", 4);
        if (end != std::string::npos) {
            for (const std::string& line : split(text.substr(4, end - 4), '\n', false)) {
                const size_t colon = line.find(':');
                if (colon == std::string::npos || colon == 0 || std::isspace(static_cast<unsigned char>(line[0]))) continue;
                definition.fields[to_lower(trim(line.substr(0, colon)))] = strip(trim(line.substr(colon + 1)), "\"'");
            }
            body = text.substr(end + 4);
            body.erase(0, body.find_first_not_of('\n'));
        }
    }
    body = trim(body);
    if (utf8_length(body) > kMaxBody) body = utf8_prefix(body, kMaxBody);
    std::string description = definition.fields.count("description") ? definition.fields["description"] : "";
    if (description.empty()) {
        for (const std::string& line : split(body, '\n', false)) {
            if (trim(line).empty()) continue;
            description = strip(trim(line), "# ");
            break;
        }
    }
    if (utf8_length(description) > 160) description = utf8_prefix(description, 160) + "…";
    const auto named = definition.fields.find("name");
    definition.name = named != definition.fields.end() && valid_name(named->second) ? named->second : name;
    definition.description = description;
    definition.body = body;
    return definition;
}

std::string template_text(const std::string& name, bool command) {
    return command ? "---\ndescription: \n---\n\n# " + name + "\n\nWhat Mason should do. $ARGUMENTS is what you type after /" + name + ".\n"
                   : "---\ndescription: \n---\n\n# " + name + "\n\nStanding instructions Mason follows while this agent is in use.\n";
}

fs::path create(const Workspace& workspace, const std::string& name, bool command) {
    const std::string kind = command ? "command" : "agent";
    const std::string wanted = trim(name);
    if (!valid_name(wanted)) throw Error(std::string(command ? "A " : "An ") + kind + " name starts with a letter or digit and has up to 48 letters, digits, '.', '_' or '-'.");
    if (std::find(kReserved.begin(), kReserved.end(), to_lower(wanted)) != kReserved.end()) throw Error(wanted + " is a built-in command. Choose another name.");
    const std::string relative = ".mason/" + kind + "s/" + wanted + ".md";
    std::error_code failure;
    for (const auto& entry : fs::directory_iterator(fs::path(workspace.root()) / (".mason/" + kind + "s"), failure)) {
        if (to_lower(entry.path().filename().string()) == to_lower(wanted) + ".md") throw Error(relative + " already exists.");
    }
    workspace.write(relative, template_text(wanted, command));
    return workspace.resolve(relative);
}

std::optional<std::string> expand(const std::vector<Definition>& commands, const std::string& input) {
    const size_t start = input.find_first_not_of(" \t\r\n");
    if (start == std::string::npos || input[start] != '/') return std::nullopt;
    const std::string rest = input.substr(start + 1);
    const size_t name_end = rest.find_first_of(" \t\r\n");
    const std::string name = rest.substr(0, name_end);
    if (!valid_name(name)) return std::nullopt;
    const auto command = std::find_if(commands.begin(), commands.end(), [&](const Definition& item) { return to_lower(item.name) == to_lower(name); });
    if (command == commands.end()) return std::nullopt;
    const std::string arguments = name_end == std::string::npos ? "" : trim(rest.substr(name_end));
    const std::vector<std::string> parts = words(arguments);
    std::string expanded = replace_all(command->body, "$ARGUMENTS", arguments);
    for (int index = 9; index >= 1; index--) {
        expanded = replace_all(expanded, "$" + std::to_string(index), static_cast<size_t>(index) <= parts.size() ? parts[static_cast<size_t>(index) - 1] : "");
    }
    if (command->body.find('$') == std::string::npos && !arguments.empty()) expanded += "\n\n" + arguments;
    return trim(expanded);
}

std::string prepare(const fs::path& home, const Session& session, const std::string& input) {
    std::string request = expand(commands(home, session.workspace), input).value_or(input);
    if (!session.agent.empty()) {
        for (const Definition& agent : agents(home, session.workspace)) {
            if (to_lower(agent.name) != to_lower(session.agent)) continue;
            request = "Standing instructions from the selected agent \"" + agent.name + "\" (" + agent.source + "):\n" + agent.body + "\n\nRequest:\n" + request;
            break;
        }
    }
    return request;
}

}  // namespace customization

// ---- compaction ------------------------------------------------------------------------------------------------

namespace compaction {
namespace {

constexpr size_t kTranscriptLimit = 48000;

std::string upper(std::string text) {
    std::transform(text.begin(), text.end(), text.begin(), [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
    return text;
}

std::string transcript(const std::vector<ChatMessage>& messages, size_t from, size_t to) {
    std::string joined;
    for (size_t index = from; index < to; index++) {
        const ChatMessage& message = messages[index];
        std::string text = utf8_length(message.content) > 2000 ? utf8_prefix(message.content, 1400) + "\n[…]\n" + utf8_suffix(message.content, 500) : message.content;
        for (const ToolCall& call : message.tool_calls) text += (text.empty() ? "" : "\n") + std::string("calls ") + call.name + " " + utf8_prefix(call.arguments, 600);
        joined += (joined.empty() ? "" : "\n\n") + upper(message.role) + ": " + text;
    }
    if (utf8_length(joined) <= kTranscriptLimit) return joined;
    return utf8_prefix(joined, kTranscriptLimit / 3) + "\n\n[… the middle of the conversation is omitted …]\n\n" + utf8_suffix(joined, kTranscriptLimit * 2 / 3);
}

}  // namespace

bool needed(int prompt_tokens, int window) { return window > 0 && prompt_tokens > 0 && prompt_tokens >= window * kThreshold; }
bool needed(const Session& session, int window) { return needed(session.prompt_tokens, window); }

size_t cut(const std::vector<ChatMessage>& messages, size_t keep) {
    size_t start = messages.size() > keep + 1 ? messages.size() - keep : 1;
    while (start > 1 && start < messages.size() && messages[start].role == "tool") start--;
    return start;
}

bool compact(Session& session, ChatModel& model, size_t keep, const CancelToken& cancel) {
    if (session.messages.size() <= 1 || session.messages.front().role != "system") return false;
    const size_t start = cut(session.messages, keep);
    if (start < 3) return false;  // fewer than two older messages
    const std::vector<ChatMessage> ask = {
        {"system", "You condense a coding session so the same agent can continue it with less text. Reply with the summary only.", {}, ""},
        {"user",
         "Summarise the session below for the agent that will continue it. Keep: what the user asked for and any constraints they set; "
         "decisions made; each file read or changed and its current state; commands run and what they showed; what is still to do. "
         "Use exact paths, names and values. Do not invent anything. At most 400 words.\n\n" + transcript(session.messages, 1, start),
         {}, ""},
    };
    const ModelReply reply = model.complete(ask, {}, [](const std::string&) {}, cancel);
    const std::string summary = trim(reply.text);
    if (summary.empty()) throw Error("The model returned no summary, so the conversation was left as it is.");
    const std::string block = "Summary of the earlier conversation (the original messages are archived):\n" + summary;
    session.archived.insert(session.archived.end(), session.messages.begin() + 1, session.messages.begin() + static_cast<std::ptrdiff_t>(start));
    std::vector<ChatMessage> kept = {session.messages.front()};
    std::vector<ChatMessage> tail(session.messages.begin() + static_cast<std::ptrdiff_t>(start), session.messages.end());
    if (tail.empty()) {
        // Nothing follows, so the summary travels with the next request, like an undo note.
        session.pending_note = (session.pending_note.empty() ? "" : session.pending_note + "\n\n") + block;
    } else if (tail.front().role == "user") {
        tail.front().content = block + "\n\n" + tail.front().content;
    } else {
        tail.insert(tail.begin(), ChatMessage{"user", block, {}, ""});
    }
    kept.insert(kept.end(), tail.begin(), tail.end());
    session.messages = std::move(kept);
    session.prompt_tokens = 0;
    session.compactions++;
    return true;
}

}  // namespace compaction

}  // namespace mason
