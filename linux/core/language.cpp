#include "language.hpp"

#include <algorithm>
#include <cerrno>
#include <csignal>
#include <cstring>
#include <fcntl.h>
#include <poll.h>
#include <set>
#include <sys/wait.h>
#include <unistd.h>

#include "navigation.hpp"
#include "shell.hpp"

namespace mason {

namespace {

using Clock = std::chrono::steady_clock;

LanguageInstall with(const char* tool, std::vector<std::string> packages) { return {tool, std::move(packages), ""}; }
LanguageInstall hint(const std::string& text) { return {"", {}, text}; }
LanguageInstall packaged(const std::string& what) { return hint("Install " + what + " with your package manager."); }

std::string file_uri(const std::string& path) {
    static const char* const hex = "0123456789ABCDEF";
    std::string out = "file://";
    for (const unsigned char c : path) {
        if (std::isalnum(c) || c == '/' || c == '-' || c == '_' || c == '.' || c == '~') out += static_cast<char>(c);
        else { out += '%'; out += hex[c >> 4]; out += hex[c & 15]; }
    }
    return out;
}

/// The file a URI names, with links resolved, so the same file always has the same key.
std::optional<std::string> path_of(const std::string& uri) {
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
    return failure ? path : real.string();
}

std::string key_of(const std::string& file) {
    std::error_code failure;
    const fs::path real = fs::weakly_canonical(file, failure);
    return failure ? file : real.string();
}

/// A file URI as a path relative to the project, or nothing when it is outside it.
std::optional<std::string> relative(const std::string& uri, const std::string& root) {
    const std::optional<std::string> path = path_of(uri);
    if (!path) return std::nullopt;
    const std::string base = key_of(root);
    if (!starts_with(*path, base + "/")) return std::nullopt;
    return path->substr(base.size() + 1);
}

int whole(const json& object, const char* key) { return object.is_object() && object.contains(key) && object[key].is_number_integer() ? object[key].get<int>() : 0; }

/// One diagnostic as a line: path:line:column, how serious, and the message.
std::string line_of(const json& item, const std::string& path) {
    static const char* const severities[] = {"", "error", "warning", "information", "hint"};
    const json start = item.contains("range") && item["range"].is_object() && item["range"].contains("start") ? item["range"]["start"] : json::object();
    const std::string text = item.contains("message") && item["message"].is_string() ? replace_all(item["message"].get<std::string>(), "\n", " ") : "";
    return path + ":" + std::to_string(whole(start, "line") + 1) + ":" + std::to_string(whole(start, "character") + 1) + "  " + severities[std::clamp(whole(item, "severity"), 0, 4)] + "  " + text;
}

std::string report_diagnostics(const std::vector<json>& items, const LanguageServer& server, const std::string& file) {
    std::vector<std::string> lines;
    for (const json& item : items) {
        if (!item.is_object()) continue;
        const std::string entry = line_of(item, file);
        if (std::find(lines.begin(), lines.end(), entry) == lines.end() && lines.size() < 150) lines.push_back(entry);
    }
    return server.name + ": " + std::to_string(lines.size()) + (lines.size() == 1 ? " diagnostic for " : " diagnostics for ") + file + "." + (lines.empty() ? "" : "\n" + join(lines, "\n"));
}

std::string report_answer(const std::optional<json>& answer, const std::string& mode, const LanguageServer& server, const Workspace& workspace) {
    if (!answer) return server.name + " did not answer. It may still have been reading the project; try again.";
    if (answer->contains("error") && (*answer)["error"].is_object()) return server.name + " could not answer: " + (*answer)["error"].value("message", "unknown error");
    json places = answer->contains("result") ? (*answer)["result"] : json::array();
    if (places.is_object()) places = json::array({places});
    if (!places.is_array()) places = json::array();
    int outside = 0;
    std::vector<std::string> lines;
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

std::string note(const std::vector<std::string>& lines, const std::string& name, const std::string& path) {
    if (lines.empty()) return "";
    std::string text = "\n" + name + " reports " + std::to_string(lines.size()) + (lines.size() == 1 ? " error in " : " errors in ") + path + " after this edit. Fix them unless they are what you intend:\n";
    const std::vector<std::string> shown(lines.begin(), lines.begin() + static_cast<long>(std::min<size_t>(lines.size(), 20)));
    text += join(shown, "\n");
    if (lines.size() > 20) text += "\n[" + std::to_string(lines.size() - 20) + " more.]";
    return text;
}

std::string quoted(const std::string& word) { return "'" + replace_all(word, "'", "'\\''") + "'"; }

/// The folders a server's program is looked for in: the PATH, where Mason installs them, and where the languages'
/// own tools put programs.
std::vector<std::string> search_folders(const fs::path& home) {
    const char* path = std::getenv("PATH");
    std::vector<std::string> folders = split(path ? path : "", ':', false);
    const std::string own = language_servers::folder(home).string(), user = home_dir();
    for (const std::string& more : {own + "/bin", own + "/npm/node_modules/.bin", user + "/.cargo/bin", user + "/go/bin", user + "/.dotnet/tools", user + "/.local/bin", std::string("/usr/local/bin"), std::string("/usr/bin")}) {
        folders.push_back(more);
    }
    return folders;
}

std::string find_program(const std::string& name, const fs::path& home) {
    std::error_code failure;
    if (name.find('/') != std::string::npos) return access(name.c_str(), X_OK) == 0 && !fs::is_directory(name, failure) ? name : "";
    for (const std::string& folder : search_folders(home)) {
        const std::string candidate = folder + "/" + name;
        if (access(candidate.c_str(), X_OK) == 0 && !fs::is_directory(candidate, failure)) return candidate;
    }
    return "";
}

/// Servers the user names themselves, by extension. The variable wins over the file.
std::map<std::string, std::vector<std::string>> overrides(const fs::path& home) {
    std::map<std::string, std::vector<std::string>> all;
    const auto take = [&](const std::string& text) {
        const json object = json::parse(text, nullptr, false);
        if (!object.is_object()) return;
        for (const auto& [kind, command] : object.items()) {
            if (!command.is_array() || command.empty()) continue;
            std::vector<std::string> parts;
            for (const json& part : command) parts.push_back(part.is_string() ? part.get<std::string>() : "");
            all[kind] = parts;
        }
    };
    std::error_code failure;
    if (fs::exists(home / "language-servers.json", failure)) {
        try { take(read_file(home / "language-servers.json")); } catch (const std::exception&) {}
    }
    if (const char* named = std::getenv("MASON_LANGUAGE_SERVERS"); named && *named) take(named);
    return all;
}

struct Ran {
    int status = -1;
    std::string output;
};

/// Runs a program with this account, outside the sandbox, and returns what it printed.
Ran run_program(const std::vector<std::string>& arguments, const std::map<std::string, std::string>& environment, int timeout_seconds) {
    int ends[2];
    if (pipe2(ends, O_CLOEXEC) != 0) return {-1, "It could not start: no pipe."};
    const pid_t child = fork();
    if (child < 0) { close(ends[0]); close(ends[1]); return {-1, "It could not start."}; }
    if (child == 0) {
        setsid();
        for (const auto& [name, value] : environment) setenv(name.c_str(), value.c_str(), 1);
        const int nothing = open("/dev/null", O_RDONLY);
        if (nothing >= 0) dup2(nothing, 0);
        dup2(ends[1], 1);
        dup2(ends[1], 2);
        std::vector<char*> argv;
        for (const std::string& argument : arguments) argv.push_back(const_cast<char*>(argument.c_str()));
        argv.push_back(nullptr);
        execv(argv[0], argv.data());
        _exit(127);
    }
    close(ends[1]);
    Ran ran;
    const Clock::time_point deadline = Clock::now() + std::chrono::seconds(timeout_seconds);
    char chunk[4096];
    while (true) {
        if (Clock::now() >= deadline) { kill(-child, SIGKILL); break; }
        pollfd waiting = {ends[0], POLLIN, 0};
        if (poll(&waiting, 1, 200) <= 0) continue;
        const ssize_t count = read(ends[0], chunk, sizeof chunk);
        if (count <= 0) break;
        if (ran.output.size() < 256 * 1024) ran.output.append(chunk, static_cast<size_t>(count));
    }
    close(ends[0]);
    int status = 0;
    waitpid(child, &status, 0);
    ran.status = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    return ran;
}

struct Plan {
    std::string needs;
    std::vector<std::string> arguments;
    std::map<std::string, std::string> environment;
};

/// What installing a language's server would run, or nothing when Mason cannot install it.
std::optional<Plan> installer(const Language& language, const fs::path& home) {
    const std::string own = language_servers::folder(home).string();
    const LanguageInstall& install = language.install;
    if (install.tool.empty() || install.packages.empty()) return std::nullopt;
    Plan plan;
    plan.needs = install.tool;
    if (install.tool == "npm") {
        plan.arguments = {"install", "--prefix", own + "/npm", "--no-fund", "--no-audit", "--loglevel=error"};
        plan.arguments.insert(plan.arguments.end(), install.packages.begin(), install.packages.end());
    } else if (install.tool == "go") {
        plan.arguments = {"install", install.packages[0]};
        plan.environment["GOBIN"] = own + "/bin";
    } else if (install.tool == "dotnet") {
        plan.arguments = {"tool", "update", "--tool-path", own + "/bin", install.packages[0]};
    } else if (install.tool == "rustup") {
        plan.arguments = {"component", "add", install.packages[0]};
    } else if (install.tool == "gem") {
        plan.arguments = {"install", "--user-install", "--no-document", "--bindir", own + "/bin", install.packages[0]};
    } else {
        return std::nullopt;
    }
    return plan;
}

std::string tool_name(const std::string& tool) {
    static const std::map<std::string, std::string> names = {{"npm", "Node.js (npm)"}, {"go", "Go"}, {"dotnet", "the .NET SDK"}, {"rustup", "rustup"}, {"gem", "Ruby (gem)"}};
    const auto found = names.find(tool);
    return found == names.end() ? tool : found->second;
}

bool has_scripts(const Workspace& workspace) {
    static const std::set<std::string> script = {"ts", "tsx", "js", "jsx", "mts", "cts", "mjs", "cjs"};
    try {
        for (const std::string& path : workspace.files(".", 5000).paths) {
            if (script.count(language_servers::kind_of(path))) return true;
        }
    } catch (const std::exception&) {
    }
    return false;
}

}  // namespace

namespace language_servers {

const char* const kHelp =
    "/languages                 The languages in this project and the language server for each\n"
    "/languages all             Every language Mason knows a server for\n"
    "/languages install NAME    Install a language's server with that language's own tool (npm, go, dotnet, rustup or gem)\n"
    "A server you installed yourself is found on the PATH. To name one for any file extension, put JSON such as\n"
    "{\"zig\": [\"/opt/zls/zls\"]} in language-servers.json in Mason's data folder.";

const std::vector<Language>& known() {
    static const std::vector<Language> all = {
        {"csharp", "C#", {"cs"}, "csharp", {{"csharp-ls"}, {"OmniSharp", "-lsp"}, {"omnisharp", "-lsp"}}, with("dotnet", {"csharp-ls"}), {}},
        {"fsharp", "F#", {"fs", "fsi", "fsx"}, "fsharp", {{"fsautocomplete"}}, with("dotnet", {"fsautocomplete"}), {}},
        {"swift", "Swift", {"swift"}, "swift", {{"sourcekit-lsp"}}, hint("sourcekit-lsp comes with Swift: install the Swift toolchain."), {}},
        {"c", "C", {"c", "h"}, "c", {{"clangd"}}, packaged("clangd"), {}},
        {"cpp", "C++ and Objective-C", {"cc", "cpp", "cxx", "hpp", "hh", "hxx", "m", "mm"}, "cpp", {{"clangd"}}, packaged("clangd"), {{"m", "objective-c"}, {"mm", "objective-cpp"}}},
        {"go", "Go", {"go"}, "go", {{"gopls"}}, with("go", {"golang.org/x/tools/gopls@latest"}), {}},
        {"rust", "Rust", {"rs"}, "rust", {{"rust-analyzer"}}, with("rustup", {"rust-analyzer"}), {}},
        {"python", "Python", {"py", "pyi"}, "python", {{"pyright-langserver", "--stdio"}, {"basedpyright-langserver", "--stdio"}}, with("npm", {"pyright"}), {}},
        {"java", "Java", {"java"}, "java", {{"jdtls"}}, packaged("the Eclipse JDT language server (jdtls) and a JDK"), {}},
        {"kotlin", "Kotlin", {"kt", "kts"}, "kotlin", {{"kotlin-lsp", "--stdio"}, {"kotlin-language-server"}}, packaged("kotlin-language-server"), {}},
        {"php", "PHP", {"php"}, "php", {{"intelephense", "--stdio"}, {"phpactor", "language-server"}}, with("npm", {"intelephense"}), {}},
        {"ruby", "Ruby", {"rb", "rake", "gemspec"}, "ruby", {{"ruby-lsp"}, {"solargraph", "stdio"}}, with("gem", {"ruby-lsp"}), {}},
        {"lua", "Lua", {"lua"}, "lua", {{"lua-language-server"}}, packaged("lua-language-server"), {}},
        {"zig", "Zig", {"zig"}, "zig", {{"zls"}}, packaged("zls"), {}},
        {"dart", "Dart", {"dart"}, "dart", {{"dart", "language-server", "--protocol=lsp"}}, hint("The Dart language server comes with the Dart or Flutter SDK."), {}},
        {"elixir", "Elixir", {"ex", "exs"}, "elixir", {{"elixir-ls"}, {"language_server.sh"}}, packaged("elixir-ls"), {}},
        {"erlang", "Erlang", {"erl", "hrl"}, "erlang", {{"elp", "server"}, {"erlang_ls"}}, packaged("erlang_ls"), {}},
        {"haskell", "Haskell", {"hs", "lhs"}, "haskell", {{"haskell-language-server-wrapper", "--lsp"}}, hint("Install haskell-language-server with ghcup: `ghcup install hls`."), {}},
        {"ocaml", "OCaml", {"ml", "mli"}, "ocaml", {{"ocamllsp"}}, hint("Install it with opam: `opam install ocaml-lsp-server`, and start Mason from a shell where `ocamllsp` is on the PATH."), {}},
        {"scala", "Scala", {"scala", "sbt", "sc"}, "scala", {{"metals"}}, hint("Install Metals with Coursier: `cs install metals`."), {}},
        {"clojure", "Clojure", {"clj", "cljs", "cljc", "edn"}, "clojure", {{"clojure-lsp"}}, packaged("clojure-lsp"), {}},
        {"shell", "Shell scripts", {"sh", "bash"}, "shellscript", {{"bash-language-server", "start"}}, with("npm", {"bash-language-server"}), {}},
        {"yaml", "YAML", {"yaml", "yml"}, "yaml", {{"yaml-language-server", "--stdio"}}, with("npm", {"yaml-language-server"}), {}},
        {"json", "JSON", {"json", "jsonc"}, "json", {{"vscode-json-language-server", "--stdio"}}, with("npm", {"vscode-langservers-extracted"}), {{"jsonc", "jsonc"}}},
        {"html", "HTML", {"html", "htm"}, "html", {{"vscode-html-language-server", "--stdio"}}, with("npm", {"vscode-langservers-extracted"}), {}},
        {"css", "CSS, SCSS and Less", {"css", "scss", "less"}, "css", {{"vscode-css-language-server", "--stdio"}}, with("npm", {"vscode-langservers-extracted"}), {{"scss", "scss"}, {"less", "less"}}},
        {"svelte", "Svelte", {"svelte"}, "svelte", {{"svelteserver", "--stdio"}}, with("npm", {"svelte-language-server"}), {}},
        {"docker", "Dockerfiles", {"dockerfile"}, "dockerfile", {{"docker-langserver", "--stdio"}}, with("npm", {"dockerfile-language-server-nodejs"}), {}},
        {"terraform", "Terraform", {"tf", "tfvars"}, "terraform", {{"terraform-ls", "serve"}}, packaged("terraform-ls"), {{"tfvars", "terraform-vars"}}},
        {"nix", "Nix", {"nix"}, "nix", {{"nixd"}, {"nil"}}, hint("Install nixd or nil, for example with `nix profile install nixpkgs#nixd`."), {}},
        {"toml", "TOML", {"toml"}, "toml", {{"taplo", "lsp", "stdio"}}, packaged("taplo"), {}},
        {"latex", "LaTeX", {"tex", "bib"}, "latex", {{"texlab"}}, packaged("texlab"), {{"bib", "bibtex"}}},
        {"typst", "Typst", {"typ"}, "typst", {{"tinymist"}}, packaged("tinymist"), {}},
        {"gleam", "Gleam", {"gleam"}, "gleam", {{"gleam", "lsp"}}, hint("The Gleam language server comes with Gleam."), {}},
        {"r", "R", {"r", "rmd"}, "r", {{"R", "--no-echo", "-e", "languageserver::run()"}}, hint("In R, run `install.packages(\"languageserver\")`."), {{"rmd", "rmd"}}},
    };
    return all;
}

std::string kind_of(const std::string& path) {
    const std::string name = to_lower(fs::path(path).filename().string());
    if (name == "dockerfile" || starts_with(name, "dockerfile.") || name == "containerfile") return "dockerfile";
    const std::string extension = fs::path(name).extension().string();
    return extension.empty() ? "" : extension.substr(1);
}

const Language* language_for(const std::string& kind) {
    for (const Language& entry : known()) {
        if (std::find(entry.extensions.begin(), entry.extensions.end(), kind) != entry.extensions.end()) return &entry;
    }
    return nullptr;
}

fs::path folder(const fs::path& home) { return home / "language-servers"; }

std::optional<LanguageServer> server_for(const std::string& kind, const fs::path& home) {
    const Language* entry = language_for(kind);
    std::string id = kind;
    if (entry) {
        const auto other = entry->others.find(kind);
        id = other == entry->others.end() ? entry->language_id : other->second;
    }
    const std::map<std::string, std::vector<std::string>> named = overrides(home);
    if (const auto own = named.find(kind); own != named.end()) {
        const std::string program = find_program(own->second[0], home);
        if (!program.empty()) {
            std::vector<std::string> command = {program};
            command.insert(command.end(), own->second.begin() + 1, own->second.end());
            return LanguageServer{fs::path(program).filename().string(), command, id};
        }
    }
    if (!entry) return std::nullopt;
    for (const std::vector<std::string>& candidate : entry->candidates) {
        const std::string program = find_program(candidate[0], home);
        if (program.empty()) continue;
        std::vector<std::string> command = {program};
        command.insert(command.end(), candidate.begin() + 1, candidate.end());
        return LanguageServer{candidate[0], command, id};
    }
    return std::nullopt;
}

bool available() {
    if (!overrides(data_home()).empty()) return true;
    for (const Language& entry : known()) {
        if (entry.key != "python" && server_for(entry.extensions[0])) return true;
    }
    return false;
}

std::string frame(const json& message) {
    const std::string body = message.dump();
    return "Content-Length: " + std::to_string(body.size()) + "\r\n\r\n" + body;
}

std::pair<std::vector<json>, size_t> frames(const std::string& output) {
    std::vector<json> found;
    size_t at = 0;
    while (true) {
        const size_t header = output.find("Content-Length:", at);
        if (header == std::string::npos) break;
        const size_t blank = output.find("\r\n\r\n", header);
        if (blank == std::string::npos) break;
        const long length = std::atol(output.c_str() + header + 15);
        if (length <= 0) { at = blank + 4; continue; }
        if (blank + 4 + static_cast<size_t>(length) > output.size()) break;
        const json message = json::parse(output.substr(blank + 4, static_cast<size_t>(length)), nullptr, false);
        if (message.is_object()) found.push_back(message);
        at = blank + 4 + static_cast<size_t>(length);
    }
    return {found, at};
}

std::string query(const Workspace& workspace, const LanguageServer& server, const std::string& path, int line, int column, const std::string& mode, bool sandboxed, LanguageSessions& sessions,
                  const CancelToken& cancel) {
    if (!(mode == "definitions" || mode == "references" || mode == "diagnostics") || line < 1 || column < 1) throw Error("Use definitions, references or diagnostics and a 1-based line and column.");
    const std::optional<std::string> data = workspace.data(path);
    if (!data || !is_valid_utf8(*data)) throw Error(path + " is not a text file in the project.");
    if (data->size() > 1000000) throw Error(path + " is too large to hand to the language server (1 MB at most).");
    const std::string file = workspace.resolve(path).string();
    const auto [session, fresh] = sessions.session(server, workspace, sandboxed, cancel);
    const int before = session->sync(file, *data, server.language_id);
    if (mode == "diagnostics") {
        const std::optional<std::vector<json>> items = session->diagnostics(file, before, fresh ? 20 : 8, cancel);
        if (!items) return server.name + " reported nothing for " + path + " in time. It may still be reading the project; try again.";
        return report_diagnostics(*items, server, path);
    }
    json params = {{"textDocument", {{"uri", file_uri(file)}}}, {"position", {{"line", line - 1}, {"character", column - 1}}}};
    if (mode == "references") params["context"] = {{"includeDeclaration", true}};
    const std::string method = mode == "definitions" ? "textDocument/definition" : "textDocument/references";
    std::optional<json> answer = session->request(method, params, 60, cancel);
    // A server that has only just started may answer with nothing while it is still reading the project.
    const auto empty = [](const json& reply) { return !reply.contains("error") && (!reply.contains("result") || reply["result"].is_null() || (reply["result"].is_array() && reply["result"].empty())); };
    if (fresh && answer && empty(*answer)) {
        for (int waited = 0; waited < 30 && !is_cancelled(cancel); waited++) std::this_thread::sleep_for(std::chrono::milliseconds(100));
        answer = session->request(method, params, 60, cancel);
    }
    check_cancel(cancel);
    return report_answer(answer, mode, server, workspace);
}

std::string query(const Workspace& workspace, const LanguageServer& server, const std::string& path, int line, int column, const std::string& mode, bool sandboxed, const CancelToken& cancel) {
    LanguageSessions sessions;
    return query(workspace, server, path, line, column, mode, sandboxed, sessions, cancel);
}

std::string errors(const Workspace& workspace, const LanguageServer& server, const std::string& path, const std::string& text, bool sandboxed, LanguageSessions& sessions, const CancelToken& cancel) {
    if (text.size() > 1000000) return "";
    const std::string file = workspace.resolve(path).string();
    const auto [session, fresh] = sessions.session(server, workspace, sandboxed, cancel);
    const int before = session->sync(file, text, server.language_id);
    const std::optional<std::vector<json>> items = session->diagnostics(file, before, fresh ? 15 : 6, cancel);
    if (!items) return "";
    std::vector<std::string> lines;
    for (const json& item : *items) {
        if (!item.is_object() || (item.contains("severity") && whole(item, "severity") != 1)) continue;
        const std::string entry = line_of(item, path);
        if (std::find(lines.begin(), lines.end(), entry) == lines.end()) lines.push_back(entry);
    }
    return note(lines, server.name, path);
}

std::string built_in_errors(const Workspace& workspace, const std::string& path, const CancelToken& cancel) {
    json object;
    try {
        object = json::parse(symbols::query(workspace, path, 1, 1, "diagnostics", cancel), nullptr, false);
    } catch (const Cancelled&) {
        throw;
    } catch (const std::exception&) {
        return "";
    }
    if (!object.is_object() || !object.contains("results") || !object["results"].is_array()) return "";
    std::vector<std::string> lines;
    for (const json& item : object["results"]) {
        if (!item.is_object() || item.value("file", path) != path) continue;
        const std::string message = item.contains("message") && item["message"].is_string() ? replace_all(item["message"].get<std::string>(), "\n", " ") : "";
        lines.push_back(path + ":" + std::to_string(std::max(1, whole(item, "line"))) + ":" + std::to_string(std::max(1, whole(item, "column"))) + "  error  " + message);
    }
    const std::string engine = object.value("engine", "The language service");
    const size_t space = engine.find(' ');
    return note(lines, space == std::string::npos || !object.contains("engine") ? engine : engine.substr(0, space), path);
}

LanguageState state(const Language& language, const fs::path& home) {
    if (const std::optional<LanguageServer> server = server_for(language.extensions[0], home)) return {server->name, ""};
    // Python is still checked without a server, by the Jedi that ships with Mason.
    const std::string before = language.key == "python" ? "syntax errors only, from the Jedi that ships with Mason. For type errors: " : "";
    if (const std::optional<Plan> plan = installer(language, home)) {
        if (find_program(plan->needs, home).empty()) return {std::nullopt, before + "needs " + tool_name(plan->needs) + " first, then /languages install " + language.key};
        return {std::nullopt, before + "/languages install " + language.key + " (uses " + plan->needs + ")"};
    }
    return {std::nullopt, language.install.hint};
}

std::pair<std::string, bool> described(const Language& language, const fs::path& home) {
    if (const std::optional<LanguageServer> server = server_for(language.extensions[0], home)) return {server->name + ", installed", false};
    const bool python = language.key == "python";
    const std::string before = python ? "Checked for syntax errors by the Jedi that ships with Mason. Pyright adds type errors. " : "Not installed. ";
    if (const std::optional<Plan> plan = installer(language, home)) {
        if (!find_program(plan->needs, home).empty()) return {python ? trim(before) : "Not installed", true};
        return {before + "Installing it needs " + tool_name(plan->needs) + " first.", false};
    }
    return {language.install.hint.empty() ? "Not installed" : before + language.install.hint, false};
}

std::vector<const Language*> present(const Workspace& workspace) {
    std::set<std::string> kinds;
    try {
        for (const std::string& path : workspace.files(".", 5000).paths) kinds.insert(kind_of(path));
    } catch (const std::exception&) {
    }
    std::vector<const Language*> found;
    for (const Language& entry : known()) {
        if (std::any_of(entry.extensions.begin(), entry.extensions.end(), [&](const std::string& kind) { return kinds.count(kind) > 0; })) found.push_back(&entry);
    }
    return found;
}

std::string install(const Language& language, const fs::path& home) {
    const std::optional<Plan> plan = installer(language, home);
    if (!plan) throw Error("Mason cannot install the " + language.name + " server itself." + (language.install.hint.empty() ? "" : " " + language.install.hint));
    const std::string program = find_program(plan->needs, home);
    if (program.empty()) throw Error("Installing the " + language.name + " server needs " + tool_name(plan->needs) + ", which was not found on this computer.");
    std::error_code failure;
    fs::create_directories(folder(home) / "bin", failure);
    std::vector<std::string> arguments = {program};
    arguments.insert(arguments.end(), plan->arguments.begin(), plan->arguments.end());
    const Ran ran = run_program(arguments, plan->environment, 900);
    const std::optional<LanguageServer> server = server_for(language.extensions[0], home);
    if (server && ran.status == 0) return "Installed " + server->name + " for " + language.name + ".";
    const std::string tail = utf8_suffix(trim(ran.output), 1500);
    throw Error("Installing the " + language.name + " server did not work (" + plan->needs + " exit " + std::to_string(ran.status) + ")." + (tail.empty() ? "" : "\n" + tail));
}

std::string command(const std::string& rest, const Workspace* workspace, const fs::path& home) {
    const std::vector<std::string> words = split(trim(rest), ' ', false);
    const std::string first = words.empty() ? "" : words[0];
    if (first == "help") return kHelp;
    if (first == "install") {
        const Language* chosen = nullptr;
        if (words.size() == 2) {
            const std::string wanted = to_lower(words[1]);
            for (const Language& entry : known()) {
                if (entry.key == wanted || to_lower(entry.name) == wanted) chosen = &entry;
            }
        }
        if (!chosen) throw Error("Use /languages install NAME, with a name from /languages all.");
        return install(*chosen, home);
    }
    if (!first.empty() && first != "all") throw Error("Use /languages, /languages all or /languages install NAME.");
    const bool everything = first == "all" || !workspace;
    std::vector<const Language*> listed;
    if (everything) {
        for (const Language& entry : known()) listed.push_back(&entry);
    } else {
        listed = present(*workspace);
    }
    std::vector<std::string> lines;
    for (const Language* language : listed) {
        const LanguageState found = state(*language, home);
        lines.push_back(language->name + " [" + language->key + "]  ·  " + (found.installed ? *found.installed + ", installed" : "not installed: " + found.action));
    }
    if (!everything && has_scripts(*workspace)) {
        lines.push_back(std::string("TypeScript and JavaScript  ·  ") + (symbols::available() ? "the TypeScript compiler that ships with Mason" : "needs Node.js (node on the PATH)"));
    }
    for (const auto& [kind, named] : overrides(home)) lines.push_back("." + kind + " files  ·  " + join(named, " ") + ", named by you");
    if (lines.empty()) return "No file in this project is in a language Mason knows a server for. /languages all lists them.";
    return join(lines, "\n") + (everything ? "" : "\nEdits to these files are checked by the installed servers. /languages all lists every language.");
}

}  // namespace language_servers

// MARK: one running server

void LanguageSession::start(const Workspace& workspace, bool sandboxed, const CancelToken& cancel) {
    int ends[2];
    if (pipe2(ends, O_CLOEXEC) != 0) throw Error(server.name + " could not start: no pipe.");
    // Writing to a server that has gone must fail as an error, not end Mason.
    std::signal(SIGPIPE, SIG_IGN);
    {
        std::lock_guard<std::mutex> guard(lock_);
        input_fd_ = ends[1];
    }
    std::string command;
    for (const std::string& part : server.command) command += (command.empty() ? "" : " ") + quoted(part);
    command += " 2>/dev/null";
    const int reading = ends[0];
    thread_ = std::thread([this, command, workspace, sandboxed, reading] {
        try {
            shell::run(command, workspace, sandboxed, 60 * 60, stop_, [this](const char* data, size_t size) { receive(data, size); }, reading);
        } catch (const std::exception&) {
        }
        std::lock_guard<std::mutex> guard(lock_);
        ended_ = true;
    });
    const std::string root = file_uri(workspace.root());
    const json capabilities = {{"textDocument", {{"publishDiagnostics", json::object()}, {"diagnostic", json::object()}, {"synchronization", {{"didSave", true}}}}}, {"workspace", {{"workspaceFolders", true}}}};
    const json params = {{"processId", nullptr}, {"rootUri", root}, {"rootPath", workspace.root()}, {"capabilities", capabilities},
                         {"workspaceFolders", json::array({{{"uri", root}, {"name", fs::path(workspace.root()).filename().string()}}})}};
    const std::optional<json> greeting = request("initialize", params, 60, cancel);
    if (!greeting || greeting->contains("error")) {
        stop();
        check_cancel(cancel);
        throw Error(server.name + " did not start: it gave no answer to the first message.");
    }
    {
        std::lock_guard<std::mutex> guard(lock_);
        const json offered = greeting->contains("result") && (*greeting)["result"].is_object() && (*greeting)["result"].contains("capabilities") ? (*greeting)["result"]["capabilities"] : json::object();
        pulls_ = offered.is_object() && offered.contains("diagnosticProvider") && !offered["diagnosticProvider"].is_null() && offered["diagnosticProvider"] != false;
    }
    send({{"jsonrpc", "2.0"}, {"method", "initialized"}, {"params", json::object()}});
}

bool LanguageSession::ended() {
    std::lock_guard<std::mutex> guard(lock_);
    return ended_;
}

void LanguageSession::receive(const char* data, size_t size) {
    std::vector<json> answers;
    {
        std::lock_guard<std::mutex> guard(lock_);
        buffer_.append(data, size);
        const auto [messages, used] = language_servers::frames(buffer_);
        buffer_.erase(0, used);
        // Output that never becomes a message is not kept.
        if (buffer_.size() > 64000000) buffer_.clear();
        for (const json& message : messages) {
            const std::string method = message.contains("method") && message["method"].is_string() ? message["method"].get<std::string>() : "";
            if (!method.empty() && message.contains("id")) {
                // The server asks something of its own. It gets an empty answer, so it does not wait for one.
                json result = nullptr;
                if (method == "workspace/configuration") {
                    result = json::array();
                    const json items = message.contains("params") && message["params"].is_object() && message["params"].contains("items") ? message["params"]["items"] : json::array();
                    for (size_t i = 0; items.is_array() && i < items.size(); i++) result.push_back(nullptr);
                }
                answers.push_back({{"jsonrpc", "2.0"}, {"id", message["id"]}, {"result", result}});
            } else if (message.contains("id") && message["id"].is_number_integer()) {
                replies_[message["id"].get<int>()] = message;
            } else if (method == "textDocument/publishDiagnostics" && message.contains("params") && message["params"].is_object()) {
                const json& params = message["params"];
                const std::optional<std::string> path = path_of(params.value("uri", ""));
                if (!path) continue;
                Published& entry = published_[*path];
                entry.count++;
                entry.at = Clock::now();
                entry.items.clear();
                if (params.contains("diagnostics") && params["diagnostics"].is_array()) {
                    for (const json& item : params["diagnostics"]) entry.items.push_back(item);
                }
            }
        }
    }
    for (const json& answer : answers) send(answer);
}

void LanguageSession::send(const json& message) {
    int descriptor;
    {
        std::lock_guard<std::mutex> guard(lock_);
        descriptor = input_fd_;
    }
    if (descriptor < 0) return;
    const std::string bytes = language_servers::frame(message);
    std::lock_guard<std::mutex> guard(writing_);
    size_t sent = 0;
    while (sent < bytes.size()) {
        const ssize_t count = write(descriptor, bytes.data() + sent, bytes.size() - sent);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) {
            std::lock_guard<std::mutex> state(lock_);
            ended_ = true;
            return;
        }
        sent += static_cast<size_t>(count);
    }
}

std::optional<json> LanguageSession::request(const std::string& method, const json& params, double wait_seconds, const CancelToken& cancel) {
    int id;
    {
        std::lock_guard<std::mutex> guard(lock_);
        id = next_id_++;
    }
    json message = {{"jsonrpc", "2.0"}, {"id", id}, {"method", method}};
    if (!params.is_null()) message["params"] = params;
    send(message);
    const Clock::time_point deadline = Clock::now() + std::chrono::milliseconds(static_cast<long>(wait_seconds * 1000));
    while (Clock::now() < deadline) {
        {
            std::lock_guard<std::mutex> guard(lock_);
            const auto found = replies_.find(id);
            if (found != replies_.end()) {
                json reply = found->second;
                replies_.erase(found);
                return reply;
            }
            if (ended_) return std::nullopt;
        }
        if (is_cancelled(cancel)) return std::nullopt;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    return std::nullopt;
}

int LanguageSession::sync(const std::string& file, const std::string& text, const std::string& language_id) {
    const std::string key = key_of(file), uri = file_uri(file);
    int version, before;
    {
        std::lock_guard<std::mutex> guard(lock_);
        const auto found = published_.find(key);
        before = found == published_.end() ? 0 : found->second.count;
        // A server says nothing new about text it already has, so what it last said stands.
        const auto held = texts_.find(key);
        if (held != texts_.end() && held->second == text) return std::max(0, before - 1);
        texts_[key] = text;
        version = ++versions_[key];
    }
    if (version == 1) {
        send({{"jsonrpc", "2.0"}, {"method", "textDocument/didOpen"}, {"params", {{"textDocument", {{"uri", uri}, {"languageId", language_id}, {"version", 1}, {"text", text}}}}}});
    } else {
        send({{"jsonrpc", "2.0"}, {"method", "textDocument/didChange"}, {"params", {{"textDocument", {{"uri", uri}, {"version", version}}}, {"contentChanges", json::array({{{"text", text}}})}}}});
    }
    send({{"jsonrpc", "2.0"}, {"method", "textDocument/didSave"}, {"params", {{"textDocument", {{"uri", uri}}}}}});
    return before;
}

std::optional<std::vector<json>> LanguageSession::diagnostics(const std::string& file, int before, double wait_seconds, const CancelToken& cancel) {
    const std::string key = key_of(file), uri = file_uri(file);
    const Clock::time_point deadline = Clock::now() + std::chrono::milliseconds(static_cast<long>(wait_seconds * 1000));
    // A request for the file's symbols is answered once the server has read the file, which is when it publishes.
    request("textDocument/documentSymbol", {{"textDocument", {{"uri", uri}}}}, wait_seconds, cancel);
    std::optional<std::vector<json>> pulled;
    bool pulls;
    {
        std::lock_guard<std::mutex> guard(lock_);
        pulls = pulls_;
    }
    if (pulls) {
        const double left = std::max(1.0, std::chrono::duration<double>(deadline - Clock::now()).count());
        const std::optional<json> answer = request("textDocument/diagnostic", {{"textDocument", {{"uri", uri}}}}, left, cancel);
        if (answer && answer->contains("result") && (*answer)["result"].is_object() && (*answer)["result"].contains("items") && (*answer)["result"]["items"].is_array()) {
            pulled = std::vector<json>();
            for (const json& item : (*answer)["result"]["items"]) pulled->push_back(item);
        }
    }
    // What it publishes may come in more than one part; it is taken once it has been quiet for a moment.
    // A server that answered the request may publish nothing at all, so it is not waited on for long.
    const Clock::time_point grace = Clock::now() + std::chrono::milliseconds(1500);
    while (true) {
        std::optional<std::vector<json>> fresh;
        bool quiet = false, gone;
        {
            std::lock_guard<std::mutex> guard(lock_);
            const auto found = published_.find(key);
            if (found != published_.end() && found->second.count > before) {
                fresh = found->second.items;
                quiet = Clock::now() - found->second.at >= std::chrono::milliseconds(400);
            }
            gone = ended_;
        }
        const bool late = Clock::now() >= deadline;
        if (fresh && (quiet || late)) {
            if (pulled) fresh->insert(fresh->end(), pulled->begin(), pulled->end());
            return fresh;
        }
        if (gone || late || is_cancelled(cancel)) return pulled;
        if (!fresh && pulled && Clock::now() >= grace) return pulled;
        std::this_thread::sleep_for(std::chrono::milliseconds(30));
    }
}

void LanguageSession::stop() {
    if (!thread_.joinable()) return;
    if (!ended()) {
        request("shutdown", nullptr, 1);
        send({{"jsonrpc", "2.0"}, {"method", "exit"}});
    }
    int descriptor;
    {
        std::lock_guard<std::mutex> guard(lock_);
        descriptor = input_fd_;
        input_fd_ = -1;
    }
    {
        // No write is under way once this lock is held, so the descriptor can be closed.
        std::lock_guard<std::mutex> guard(writing_);
        if (descriptor >= 0) close(descriptor);
    }
    stop_->store(true);
    thread_.join();
}

std::pair<std::shared_ptr<LanguageSession>, bool> LanguageSessions::session(const LanguageServer& server, const Workspace& workspace, bool sandboxed, const CancelToken& cancel) {
    const std::string key = join(server.command, " ");
    {
        std::lock_guard<std::mutex> guard(lock_);
        if (const auto problem = failed_.find(key); problem != failed_.end()) throw Error(problem->second);
        if (const auto running = sessions_.find(key); running != sessions_.end()) {
            if (!running->second->ended()) return {running->second, false};
            sessions_.erase(running);
        }
    }
    auto made = std::make_shared<LanguageSession>(server);
    try {
        made->start(workspace, sandboxed, cancel);
    } catch (const Error& error) {
        std::lock_guard<std::mutex> guard(lock_);
        failed_[key] = error.what();
        throw;
    }
    std::lock_guard<std::mutex> guard(lock_);
    sessions_[key] = made;
    return {made, true};
}

void LanguageSessions::stop_all() {
    std::map<std::string, std::shared_ptr<LanguageSession>> all;
    {
        std::lock_guard<std::mutex> guard(lock_);
        all.swap(sessions_);
        failed_.clear();
    }
    for (auto& [key, session] : all) session->stop();
}

}  // namespace mason
