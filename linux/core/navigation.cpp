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
}  // namespace mason
