#include "navigation.hpp"

#include <algorithm>
#include <chrono>
#include <csignal>
#include <fcntl.h>
#include <poll.h>
#include <regex>
#include <set>
#include <sys/wait.h>
#include <thread>
#include <unistd.h>

#include "models.hpp"
#include "shell.hpp"

namespace mason {

namespace navigation {

namespace {

constexpr size_t kLimit = 150;

bool source_file(const std::string& path) {
    static const std::set<std::string> extensions = {".cs", ".py", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".go", ".rb", ".dart", ".swift", ".c", ".h", ".cc", ".cpp", ".hpp",
                                                     ".m", ".mm", ".java", ".kt", ".rs", ".php", ".scala"};
    return extensions.count(to_lower(fs::path(path).extension().string())) > 0;
}

bool is_test(const std::string& path) {
    static const std::regex pattern(R"((?:^|[/._-])tests?(?:[/._-]|$)|(?:Tests?|Checks|_test|\.spec|\.test)\.\w+$)", std::regex::icase);
    return std::regex_search(path, pattern);
}

bool word_char(unsigned char c) { return std::isalnum(c) || c == '_' || c >= 0x80; }

/// True when `symbol` appears in `line` as a whole word.
bool has_word(const std::string& line, const std::string& symbol) {
    for (size_t at = line.find(symbol); at != std::string::npos; at = line.find(symbol, at + 1)) {
        const bool before = at == 0 || !word_char(static_cast<unsigned char>(line[at - 1]));
        const bool after = at + symbol.size() >= line.size() || !word_char(static_cast<unsigned char>(line[at + symbol.size()]));
        if (before && after) return true;
    }
    return false;
}

}  // namespace

NavigationResult query(const Workspace& workspace, const std::string& directory, const std::string& symbol, const std::string& mode, const CancelToken& cancel) {
    const bool identifier = !symbol.empty() && symbol.size() <= 100 && !std::isdigit(static_cast<unsigned char>(symbol[0])) &&
                            std::all_of(symbol.begin(), symbol.end(), [](unsigned char c) { return word_char(c); });
    if (!(mode == "definitions" || mode == "references" || mode == "tests") || !identifier) throw Error("Choose definitions, references or tests, and a single identifier.");
    // The symbol is only letters, digits and underscores, so it can go into the pattern as it is.
    const std::regex declaration("\\b(?:class|interface|enum|def|func|function|fn|type|typealias|const|let|var|val|struct|protocol|trait|impl|module|namespace|record)\\s+" + symbol + "(?![A-Za-z0-9_])"
                                 "|^[\\w:<>,*&\\s\\[\\]]+[\\s*&]" + symbol + "\\s*\\([^;]*\\)\\s*(?:const\\s*)?\\{?\\s*$");
    const FileListing listing = workspace.files(directory.empty() ? "." : directory, 4000);
    NavigationResult result;
    result.partial = listing.truncated;
    size_t total = 0;
    for (const std::string& path : listing.paths) {
        check_cancel(cancel);
        if (!source_file(path)) continue;
        if (result.hits.size() >= kLimit || total > 24 * 1024 * 1024) { result.partial = true; break; }
        std::optional<std::string> data;
        try {
            data = workspace.data(path);
        } catch (const std::exception&) {
        }
        if (!data || data->size() > 2 * 1024 * 1024 || !is_valid_utf8(*data)) continue;
        result.files++;
        total += data->size();
        if (data->find(symbol) == std::string::npos) continue;
        const bool test = is_test(path);
        const std::vector<std::string> lines = split(*data, '\n');
        for (size_t i = 0; i < lines.size() && result.hits.size() < kLimit; i++) {
            if (!has_word(lines[i], symbol)) continue;
            const std::string trimmed = trim(lines[i]);
            if (starts_with(trimmed, "//") || starts_with(trimmed, "#") || starts_with(trimmed, "*")) continue;
            const bool declared = std::regex_search(lines[i], declaration);
            if (mode == "definitions" ? !declared : mode == "tests" ? !test : declared) continue;
            result.hits.push_back({path, static_cast<int>(i) + 1, mode == "definitions" ? "definition candidate" : test ? "test reference candidate" : "reference candidate", utf8_prefix(trimmed, 320)});
        }
    }
    if (result.hits.size() >= kLimit) result.partial = true;
    return result;
}

std::string report(const NavigationResult& result, const std::string& symbol, const std::string& mode) {
    std::string text = std::to_string(result.hits.size()) + " " + mode + " of " + symbol + " in " + std::to_string(result.files) + " source files" + (result.partial ? " (not the whole project)" : "") +
                       ". These are whole-word text matches, not a compiler's answer; read the lines before relying on them.";
    for (const NavigationHit& hit : result.hits) text += "\n" + hit.path + ":" + std::to_string(hit.line) + "  [" + hit.kind + "]  " + hit.text;
    if (result.hits.empty()) text += "\nNothing matched. Check the spelling, or search with search_text.";
    return text;
}

}  // namespace navigation

namespace repository_map {

MapResult query(const Workspace& workspace, const std::string& query, const std::string& directory, size_t limit, const CancelToken& cancel) {
    std::vector<std::string> parts;
    std::string current;
    for (char c : query) {
        if (std::string(".:#/ ()").find(c) == std::string::npos) { current += c; continue; }
        if (!current.empty()) parts.push_back(current);
        current.clear();
    }
    if (!current.empty()) parts.push_back(current);
    if (parts.empty() || query.size() > 200) throw Error("Give an identifier or a qualified name such as Cart.total.");
    const std::string name = parts.back();
    const std::string qualifier = parts.size() > 1 ? to_lower(parts[parts.size() - 2]) : "";
    const NavigationResult definitions = navigation::query(workspace, directory, name, "definitions", cancel);
    const NavigationResult references = navigation::query(workspace, directory, name, "references", cancel);
    static const std::regex import_line(R"RX(^\s*(?:import|from|using|use|#\s*include|export\b.*\bfrom)\b|\brequire\s*\()RX");
    std::vector<MapMatch> groups[4];
    for (const NavigationHit& hit : definitions.hits) groups[0].push_back({hit.path, hit.line, hit.kind, name, hit.text});
    for (const NavigationHit& hit : references.hits) {
        const bool imported = std::regex_search(hit.text, import_line);
        groups[imported ? 1 : hit.kind.rfind("test", 0) == 0 ? 3 : 2].push_back({hit.path, hit.line, imported ? "import candidate" : hit.kind, name, hit.text});
    }
    if (!qualifier.empty()) {
        // Matches that mention the qualifier come first, in their original order.
        for (auto& group : groups) {
            std::stable_partition(group.begin(), group.end(), [&](const MapMatch& match) {
                return to_lower(match.path).find(qualifier) != std::string::npos || to_lower(match.text).find(qualifier) != std::string::npos;
            });
        }
    }
    // Every kind gets a share before any kind takes what is left.
    const size_t shares[4] = {12, 6, 10, 6};
    std::vector<MapMatch> chosen, left;
    size_t all = 0;
    for (int group = 0; group < 4; group++) {
        all += groups[group].size();
        for (size_t i = 0; i < groups[group].size(); i++) (i < shares[group] ? chosen : left).push_back(groups[group][i]);
    }
    for (const MapMatch& match : left) {
        if (chosen.size() < limit) chosen.push_back(match);
    }
    if (chosen.size() > limit) chosen.resize(limit);
    auto rank = [](const MapMatch& match) {
        return match.kind == "definition candidate" ? 0 : match.kind == "import candidate" ? 1 : match.kind == "reference candidate" ? 2 : match.kind == "test reference candidate" ? 3 : 4;
    };
    std::stable_sort(chosen.begin(), chosen.end(), [&](const MapMatch& a, const MapMatch& b) { return rank(a) < rank(b); });
    MapResult result;
    result.files = std::max(definitions.files, references.files);
    result.partial = definitions.partial || references.partial || all > chosen.size();
    result.matches = std::move(chosen);
    return result;
}

std::string report(const MapResult& result, const std::string& query) {
    std::string text = std::to_string(result.matches.size()) + " matches for " + query + " in " + std::to_string(result.files) + " source files" +
                       (result.partial ? " (not everything; narrow the folder or the name to see the rest)" : "") +
                       ": definitions, then imports, callers and tests. These are whole-word text matches, not a compiler's answer; read the lines before relying on them.";
    for (const MapMatch& match : result.matches) text += "\n" + match.path + ":" + std::to_string(match.line) + "  [" + match.kind + "]  " + match.text;
    if (result.matches.empty()) text += "\nNothing matched. Check the spelling, or search with search_files.";
    return text;
}

}  // namespace repository_map

namespace symbols {

namespace {

const std::set<std::string> kPython = {".py", ".pyi"};
const std::set<std::string> kScript = {".ts", ".tsx", ".js", ".jsx", ".mts", ".cts", ".mjs", ".cjs"};

std::string on_path(const std::string& name) {
    const char* path = std::getenv("PATH");
    for (const std::string& folder : split(std::string(path ? path : "") + ":/usr/local/bin:/usr/bin", ':', false)) {
        const std::string candidate = folder + "/" + name;
        std::error_code failure;
        if (access(candidate.c_str(), X_OK) == 0 && !fs::is_directory(candidate, failure)) return candidate;
    }
    return "";
}

/// The program and arguments that answer for a file of this kind, or nothing when what it needs is not on this computer.
std::vector<std::string> helper(const std::string& extension) {
    const fs::path tools = folder();
    std::error_code failure;
    if (tools.empty()) return {};
    if (kPython.count(extension)) {
        // Jedi ships beside the helper; without it there is nothing to ask.
        const std::string program = transformers::python();
        if (program.empty() || !fs::exists(tools / "python" / "jedi", failure)) return {};
        return {program, "-I", "-B", (tools / "python-symbols.py").string()};
    }
    if (kScript.count(extension)) {
        const std::string node = on_path("node");
        if (node.empty() || !fs::exists(tools / "typescript" / "typescript.js", failure)) return {};
        return {node, (tools / "typescript-symbols.cjs").string()};
    }
    return {};
}

/// Runs a helper with the request on its standard input and returns what it printed. It gets thirty seconds.
std::string run(const std::vector<std::string>& command, const std::string& input, const CancelToken& cancel) {
    int to_child[2], from_child[2];
    if (pipe2(to_child, O_CLOEXEC) != 0) throw Error("The language service could not start.");
    if (pipe2(from_child, O_CLOEXEC) != 0) { close(to_child[0]); close(to_child[1]); throw Error("The language service could not start."); }
    std::vector<std::string> arguments = command;
    std::vector<char*> argv;
    for (std::string& argument : arguments) argv.push_back(argument.data());
    argv.push_back(nullptr);
    const std::string directory = fs::path(command.back()).parent_path().string();
    const pid_t child = fork();
    if (child < 0) {
        for (const int fd : {to_child[0], to_child[1], from_child[0], from_child[1]}) close(fd);
        throw Error("The language service could not start.");
    }
    if (child == 0) {
        setsid();
        dup2(to_child[0], STDIN_FILENO);
        dup2(from_child[1], STDOUT_FILENO);
        const int null = open("/dev/null", O_WRONLY);
        dup2(null, STDERR_FILENO);
        if (chdir(directory.c_str()) != 0) _exit(126);
        execv(argv[0], argv.data());
        _exit(127);
    }
    close(to_child[0]);
    close(from_child[1]);
    // The request can be megabytes; it is written on another thread so neither pipe fills up.
    std::thread writer([fd = to_child[1], input] {
        size_t sent = 0;
        while (sent < input.size()) {
            const ssize_t count = write(fd, input.data() + sent, input.size() - sent);
            if (count <= 0) break;
            sent += static_cast<size_t>(count);
        }
        close(fd);
    });
    std::string answer;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
    bool stopped = false;
    char buffer[8192];
    while (true) {
        pollfd waiting{from_child[0], POLLIN, 0};
        if (poll(&waiting, 1, 100) > 0) {
            const ssize_t count = read(from_child[0], buffer, sizeof buffer);
            if (count <= 0) break;
            if (answer.size() < 4 * 1024 * 1024) answer.append(buffer, static_cast<size_t>(count));
        }
        if (is_cancelled(cancel) || std::chrono::steady_clock::now() > deadline) { stopped = true; break; }
    }
    close(from_child[0]);
    if (stopped) kill(-child, SIGKILL);
    int status = 0;
    waitpid(child, &status, 0);
    writer.join();
    check_cancel(cancel);
    if (stopped) throw Error("The language service did not answer in thirty seconds.");
    answer = trim(answer);
    if (!WIFEXITED(status) || WEXITSTATUS(status) != 0 || answer.empty()) throw Error("The language service could not answer for this file.");
    return utf8_prefix(answer, 40000);
}

}  // namespace

fs::path folder() {
    std::vector<fs::path> candidates;
    if (const char* named = std::getenv("MASON_LANGUAGE_TOOLS"); named && *named) candidates.push_back(named);
    std::error_code failure;
    const fs::path program = fs::read_symlink("/proc/self/exe", failure);
    if (!failure) candidates.push_back(program.parent_path().parent_path() / "share" / "mason-agent" / "language-tools");
    candidates.push_back("/usr/share/mason-agent/language-tools");
    for (const fs::path& candidate : candidates) {
        if (fs::exists(candidate / "python-symbols.py", failure)) return candidate;
    }
    return {};
}

bool available() { return !helper(".py").empty() || !helper(".ts").empty(); }

std::string query(const Workspace& workspace, const std::string& path, int line, int column, const std::string& mode, const CancelToken& cancel) {
    if (!(mode == "definitions" || mode == "references" || mode == "diagnostics") || line < 1 || column < 1) throw Error("Use definitions, references or diagnostics and a 1-based line and column.");
    const std::string extension = to_lower(fs::path(path).extension().string());
    const bool python = kPython.count(extension) > 0;
    if (!python && !kScript.count(extension)) throw Error("inspect_symbol answers for Python, JavaScript and TypeScript files.");
    const std::set<std::string>& family = python ? kPython : kScript;
    const std::vector<std::string> command = helper(extension);
    if (command.empty()) {
        throw Error(python ? "The Python language service is not available on this computer: it needs python3 and the Jedi that ships with Mason."
                           : "The TypeScript language service is not available on this computer: it needs Node.js (node on the PATH) and the TypeScript that ships with Mason.");
    }
    const std::optional<std::string> main = workspace.data(path);
    if (!main || !is_valid_utf8(*main)) throw Error(path + " is not a text file in the project.");
    const std::string relative = fs::path(workspace.resolve(path)).lexically_relative(workspace.root()).generic_string();
    json files = {{relative, *main}};
    size_t size = main->size();
    const FileListing listing = workspace.files(".", 1000);
    bool partial = listing.truncated;
    for (const std::string& other : listing.paths) {
        if (other == relative || !family.count(to_lower(fs::path(other).extension().string()))) continue;
        if (size > 4000000 || files.size() >= 400) { partial = true; break; }
        std::optional<std::string> data;
        try {
            data = workspace.data(other);
        } catch (const std::exception&) {
        }
        if (!data || !is_valid_utf8(*data)) { partial = true; continue; }
        files[other] = *data;
        size += data->size();
    }
    const json request = {{"path", relative}, {"line", line}, {"column", column}, {"mode", mode}, {"files", files}, {"partial", partial}};
    return run(command, request.dump(), cancel);
}

}  // namespace symbols
// ---- language servers --------------------------------------------------------------------------------------------

bool symbols::handles(const std::string& path) {
    const std::string extension = to_lower(fs::path(path).extension().string());
    return extension == ".py" || extension == ".pyi" || extension == ".ts" || extension == ".tsx" || extension == ".js" || extension == ".jsx" || extension == ".mts" || extension == ".cts" || extension == ".mjs" ||
           extension == ".cjs";
}

namespace language_servers {

namespace {

struct Known {
    std::set<std::string> extensions;
    const char* language_id;
    std::vector<std::vector<std::string>> candidates;
};

const std::vector<Known>& known() {
    static const std::vector<Known> all = {
        {{"cs"}, "csharp", {{"csharp-ls"}, {"OmniSharp", "-lsp"}, {"omnisharp", "-lsp"}}},
        {{"swift"}, "swift", {{"sourcekit-lsp"}}},
        {{"c", "h"}, "c", {{"clangd"}}},
        {{"cc", "cpp", "cxx", "hpp", "hh", "m", "mm"}, "cpp", {{"clangd"}}},
        {{"go"}, "go", {{"gopls"}}},
        {{"rs"}, "rust", {{"rust-analyzer"}}},
    };
    return all;
}

std::string program_on_path(const std::string& name) {
    const char* path = std::getenv("PATH");
    for (const std::string& folder : split(std::string(path ? path : "") + ":/usr/local/bin:/usr/bin", ':', false)) {
        const std::string candidate = folder + "/" + name;
        std::error_code failure;
        if (access(candidate.c_str(), X_OK) == 0 && !fs::is_directory(candidate, failure)) return candidate;
    }
    return "";
}

std::string frame(const json& message) {
    const std::string body = message.dump();
    return "Content-Length: " + std::to_string(body.size()) + "\r\n\r\n" + body;
}

std::string file_uri(const std::string& path) {
    static const char* const hex = "0123456789ABCDEF";
    std::string out = "file://";
    for (const unsigned char c : path) {
        if (std::isalnum(c) || c == '/' || c == '-' || c == '_' || c == '.' || c == '~') out += static_cast<char>(c);
        else { out += '%'; out += hex[c >> 4]; out += hex[c & 15]; }
    }
    return out;
}

/// A file URI as a path relative to the project, or nothing when it is outside it.
std::optional<std::string> relative(const std::string& uri, const std::string& root) {
    if (!starts_with(uri, "file://")) return std::nullopt;
    std::string path;
    const std::string encoded = uri.substr(7);
    for (size_t i = 0; i < encoded.size(); i++) {
        if (encoded[i] == '%' && i + 2 < encoded.size() && std::isxdigit(static_cast<unsigned char>(encoded[i + 1])) && std::isxdigit(static_cast<unsigned char>(encoded[i + 2]))) {
            path += static_cast<char>(std::stoi(encoded.substr(i + 1, 2), nullptr, 16));
            i += 2;
        } else {
            path += encoded[i];
        }
    }
    std::error_code failure;
    const fs::path real = fs::weakly_canonical(path, failure);
    const std::string resolved = failure ? path : real.string();
    if (!starts_with(resolved, root + "/")) return std::nullopt;
    return resolved.substr(root.size() + 1);
}

std::string report(const std::vector<json>& replies, const std::string& mode, const LanguageServer& server, const Workspace& workspace, const std::string& file) {
    std::vector<std::string> lines;
    const auto whole = [](const json& object, const char* key) { return object.is_object() && object.contains(key) && object[key].is_number_integer() ? object[key].get<int>() : 0; };
    if (mode == "diagnostics") {
        static const char* const severities[] = {"", "error", "warning", "information", "hint"};
        for (const json& message : replies) {
            if (message.value("method", "") != "textDocument/publishDiagnostics" || !message.contains("params") || !message["params"].is_object()) continue;
            const json& params = message["params"];
            const std::optional<std::string> path = relative(params.value("uri", ""), workspace.root());
            if (!path || !params.contains("diagnostics") || !params["diagnostics"].is_array()) continue;
            for (const json& item : params["diagnostics"]) {
                if (!item.is_object()) continue;
                const json start = item.contains("range") && item["range"].is_object() && item["range"].contains("start") ? item["range"]["start"] : json::object();
                const std::string text = item.contains("message") && item["message"].is_string() ? replace_all(item["message"].get<std::string>(), "\n", " ") : "";
                const std::string entry = *path + ":" + std::to_string(whole(start, "line") + 1) + ":" + std::to_string(whole(start, "character") + 1) + "  " + severities[std::clamp(whole(item, "severity"), 0, 4)] + "  " + text;
                if (std::find(lines.begin(), lines.end(), entry) == lines.end() && lines.size() < 150) lines.push_back(entry);
            }
        }
        return server.name + ": " + std::to_string(lines.size()) + (lines.size() == 1 ? " diagnostic for " : " diagnostics for ") + file + "." + (lines.empty() ? "" : "\n" + join(lines, "\n"));
    }
    const auto answer = std::find_if(replies.begin(), replies.end(), [](const json& message) { return message.contains("id") && message["id"].is_number_integer() && message["id"].get<int>() == 2; });
    if (answer == replies.end()) return server.name + " did not answer. It may still have been reading the project; try again.";
    if (answer->contains("error") && (*answer)["error"].is_object()) return server.name + " could not answer: " + (*answer)["error"].value("message", "unknown error");
    json places = answer->contains("result") ? (*answer)["result"] : json::array();
    if (places.is_object()) places = json::array({places});
    if (!places.is_array()) places = json::array();
    int outside = 0;
    for (const json& place : places) {
        if (!place.is_object() || lines.size() >= 150) continue;
        // A location, or a location link whose target is the place.
        const std::string uri = place.contains("uri") ? place.value("uri", "") : place.value("targetUri", "");
        const json range = place.contains("range") ? place["range"] : place.contains("targetSelectionRange") ? place["targetSelectionRange"] : place.contains("targetRange") ? place["targetRange"] : json::object();
        const json start = range.is_object() && range.contains("start") ? range["start"] : json::object();
        const std::optional<std::string> path = relative(uri, workspace.root());
        if (!path) { outside++; continue; }
        const int line = whole(start, "line") + 1;
        std::string text;
        try {
            const std::vector<std::string> source = split(workspace.read(*path), '\n');
            if (static_cast<size_t>(line) <= source.size()) text = utf8_prefix(trim(source[static_cast<size_t>(line) - 1]), 200);
        } catch (const std::exception&) {
        }
        lines.push_back(*path + ":" + std::to_string(line) + ":" + std::to_string(whole(start, "character") + 1) + "  " + text);
    }
    std::string text = server.name + ": " + std::to_string(lines.size()) + " " + mode + " in the project" + (outside > 0 ? ", and " + std::to_string(outside) + " outside it (libraries)" : "") + ".";
    if (!lines.empty()) text += "\n" + join(lines, "\n");
    return text;
}

std::string base64(const std::string& data) {
    static const char* const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    for (size_t i = 0; i < data.size(); i += 3) {
        const unsigned a = static_cast<unsigned char>(data[i]);
        const unsigned b = i + 1 < data.size() ? static_cast<unsigned char>(data[i + 1]) : 0;
        const unsigned c = i + 2 < data.size() ? static_cast<unsigned char>(data[i + 2]) : 0;
        out += alphabet[a >> 2];
        out += alphabet[((a & 3) << 4) | (b >> 4)];
        out += i + 1 < data.size() ? alphabet[((b & 15) << 2) | (c >> 6)] : '=';
        out += i + 2 < data.size() ? alphabet[c & 63] : '=';
    }
    return out;
}

std::string quoted(const std::string& word) { return "'" + replace_all(word, "'", "'\\''") + "'"; }

}  // namespace

std::optional<LanguageServer> server_for(const std::string& extension) {
    const auto entry = std::find_if(known().begin(), known().end(), [&](const Known& each) { return each.extensions.count(extension) > 0; });
    if (entry == known().end()) return std::nullopt;
    if (const char* named = std::getenv("MASON_LANGUAGE_SERVERS"); named && *named) {
        const json overrides = json::parse(named, nullptr, false);
        if (overrides.is_object() && overrides.contains(extension) && overrides[extension].is_array() && !overrides[extension].empty() && overrides[extension][0].is_string()) {
            std::vector<std::string> command;
            for (const json& part : overrides[extension]) command.push_back(part.is_string() ? part.get<std::string>() : "");
            if (access(command[0].c_str(), X_OK) == 0) return LanguageServer{fs::path(command[0]).filename().string(), command, entry->language_id};
        }
    }
    for (const std::vector<std::string>& candidate : entry->candidates) {
        const std::string program = program_on_path(candidate[0]);
        if (program.empty()) continue;
        std::vector<std::string> command = {program};
        command.insert(command.end(), candidate.begin() + 1, candidate.end());
        return LanguageServer{candidate[0], command, entry->language_id};
    }
    return std::nullopt;
}

bool available() {
    for (const Known& entry : known()) {
        for (const std::string& extension : entry.extensions) {
            if (server_for(extension)) return true;
        }
    }
    return false;
}

std::string conversation(const std::string& root, const std::string& file, const std::string& text, const std::string& language_id, int line, int column, const std::string& mode) {
    const std::string uri = file_uri(file), root_uri = file_uri(root);
    json position = {{"textDocument", {{"uri", uri}}}, {"position", {{"line", line - 1}, {"character", column - 1}}}};
    std::string all = frame({{"jsonrpc", "2.0"}, {"id", 1}, {"method", "initialize"},
                             {"params", {{"processId", nullptr}, {"rootUri", root_uri}, {"capabilities", {{"textDocument", {{"publishDiagnostics", json::object()}}}}},
                                         {"workspaceFolders", json::array({{{"uri", root_uri}, {"name", fs::path(root).filename().string()}}})}}}});
    all += frame({{"jsonrpc", "2.0"}, {"method", "initialized"}, {"params", json::object()}});
    all += frame({{"jsonrpc", "2.0"}, {"method", "textDocument/didOpen"}, {"params", {{"textDocument", {{"uri", uri}, {"languageId", language_id}, {"version", 1}, {"text", text}}}}}});
    if (mode == "definitions") all += frame({{"jsonrpc", "2.0"}, {"id", 2}, {"method", "textDocument/definition"}, {"params", position}});
    else if (mode == "references") {
        position["context"] = {{"includeDeclaration", true}};
        all += frame({{"jsonrpc", "2.0"}, {"id", 2}, {"method", "textDocument/references"}, {"params", position}});
    } else {
        // Asking for the file's symbols makes the server finish reading it, and it publishes its diagnostics as it does.
        all += frame({{"jsonrpc", "2.0"}, {"id", 2}, {"method", "textDocument/documentSymbol"}, {"params", {{"textDocument", {{"uri", uri}}}}}});
    }
    all += frame({{"jsonrpc", "2.0"}, {"id", 3}, {"method", "shutdown"}});
    all += frame({{"jsonrpc", "2.0"}, {"method", "exit"}});
    return all;
}

std::vector<json> messages_in(const std::string& output) {
    std::vector<json> found;
    size_t at = 0;
    while (true) {
        const size_t header = output.find("Content-Length:", at);
        if (header == std::string::npos) break;
        const size_t blank = output.find("\r\n\r\n", header);
        if (blank == std::string::npos) break;
        const long length = std::atol(output.c_str() + header + 15);
        if (length <= 0 || blank + 4 + static_cast<size_t>(length) > output.size()) break;
        const json message = json::parse(output.substr(blank + 4, static_cast<size_t>(length)), nullptr, false);
        if (message.is_object()) found.push_back(message);
        at = blank + 4 + static_cast<size_t>(length);
    }
    return found;
}

std::string query(const Workspace& workspace, const LanguageServer& server, const std::string& path, int line, int column, const std::string& mode, bool sandboxed, const CancelToken& cancel) {
    if (!(mode == "definitions" || mode == "references" || mode == "diagnostics") || line < 1 || column < 1) throw Error("Use definitions, references or diagnostics and a 1-based line and column.");
    const std::optional<std::string> data = workspace.data(path);
    if (!data || !is_valid_utf8(*data)) throw Error(path + " is not a text file in the project.");
    // The request travels on the command line, which holds only so much.
    if (data->size() > 60000) throw Error(path + " is too large to hand to the language server here (60 KB at most).");
    const std::string request = conversation(workspace.root(), workspace.resolve(path).string(), *data, server.language_id, line, column, mode);
    // Diagnostics are published a moment after the file is read, so the server is given that moment before it is told to leave.
    const std::string closing = frame({{"jsonrpc", "2.0"}, {"id", 3}, {"method", "shutdown"}}) + frame({{"jsonrpc", "2.0"}, {"method", "exit"}});
    const std::string opening = request.substr(0, request.size() - closing.size());
    std::string command = mode == "diagnostics" ? "{ printf %s " + quoted(base64(opening)) + " | base64 --decode; sleep 3; printf %s " + quoted(base64(closing)) + " | base64 --decode; } |"
                                                : "printf %s " + quoted(base64(request)) + " | base64 --decode |";
    for (const std::string& part : server.command) command += " " + quoted(part);
    command += " 2>/dev/null";
    const CommandResult result = shell::run(command, workspace, sandboxed, 90, cancel);
    const std::vector<json> replies = messages_in(result.output);
    if (replies.empty()) throw Error(result.timed_out ? server.name + " did not finish within 90 seconds." : server.name + " did not answer (exit " + std::to_string(result.exit_code) + ").");
    return report(replies, mode, server, workspace, path);
}

}  // namespace language_servers
}  // namespace mason
