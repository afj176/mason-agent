// Mason speaks the Language Server Protocol to the language servers on this computer, the way editors do. A server
// is started the first time a request touches a file of its language and stays running until the request ends.
// After each edit the file is handed to it, and the errors it reports go back to the model with the edit's result.
// `inspect_symbol` asks the same server where a name is defined or used.
//
// A server is a program that reads the project, and some run the project's build, so it is started the way a command
// is: inside the sandbox in Auto, and after asking otherwise. Mason ships none of them. `/languages` shows which are
// installed and installs the ones a language's own tools can fetch.
#pragma once

#include <chrono>
#include <map>
#include <mutex>
#include <thread>

#include "util.hpp"
#include "workspace.hpp"

namespace mason {

struct LanguageServer {
    std::string name;
    std::vector<std::string> command;
    std::string language_id;
};

/// How a server that is missing is got: with the language's own tool, or by the user (`hint`).
struct LanguageInstall {
    std::string tool;                   // "npm", "go", "dotnet", "rustup", "gem", or "" for a hint
    std::vector<std::string> packages;  // what the tool is asked for
    std::string hint;
};

struct Language {
    std::string key;
    std::string name;
    std::vector<std::string> extensions;
    std::string language_id;
    std::vector<std::vector<std::string>> candidates;
    LanguageInstall install;
    /// Extensions whose language id differs from the entry's.
    std::map<std::string, std::string> others;
};

/// One language's state: its server when installed, otherwise how to get one.
struct LanguageState {
    std::optional<std::string> installed;
    std::string action;
};

/// One running language server: what it has been sent, and what it has said.
class LanguageSession {
public:
    explicit LanguageSession(LanguageServer server) : server(std::move(server)) {}
    ~LanguageSession() { stop(); }
    LanguageSession(const LanguageSession&) = delete;
    LanguageSession& operator=(const LanguageSession&) = delete;

    /// Starts the server in the project folder and greets it. Throws when it does not answer.
    void start(const Workspace& workspace, bool sandboxed, const CancelToken& cancel = nullptr);
    bool ended();
    /// Sends a request and waits for its answer; nothing when none comes in time or the server has gone.
    std::optional<json> request(const std::string& method, const json& params, double wait_seconds, const CancelToken& cancel = nullptr);
    /// Hands the server a file's text as it is now, and returns how many times it had published for the file before.
    int sync(const std::string& file, const std::string& text, const std::string& language_id);
    /// The file's diagnostics once the server has read it again; nothing when it says nothing in time.
    std::optional<std::vector<json>> diagnostics(const std::string& file, int before, double wait_seconds, const CancelToken& cancel = nullptr);
    void stop();

    const LanguageServer server;

private:
    struct Published {
        int count = 0;
        std::chrono::steady_clock::time_point at;
        std::vector<json> items;
    };
    void receive(const char* data, size_t size);
    void send(const json& message);

    std::mutex lock_, writing_;
    std::string buffer_;
    std::map<int, json> replies_;
    std::map<std::string, Published> published_;
    std::map<std::string, int> versions_;
    std::map<std::string, std::string> texts_;
    int next_id_ = 1;
    int input_fd_ = -1;
    std::thread thread_;
    CancelToken stop_ = make_cancel();
    bool ended_ = false;
    /// True when the server answers a request for a file's diagnostics rather than only publishing them.
    bool pulls_ = false;
};

/// The language servers one request has started.
class LanguageSessions {
public:
    ~LanguageSessions() { stop_all(); }
    /// The running server, started now if this request has not used it yet. The flag is true when it was started
    /// by this call, so the caller gives it longer.
    std::pair<std::shared_ptr<LanguageSession>, bool> session(const LanguageServer& server, const Workspace& workspace, bool sandboxed, const CancelToken& cancel = nullptr);
    void stop_all();

private:
    std::mutex lock_;
    std::map<std::string, std::shared_ptr<LanguageSession>> sessions_;
    std::map<std::string, std::string> failed_;
};

namespace language_servers {
const std::vector<Language>& known();
/// What a path's language is looked up by: its extension in lower case without the dot, or `dockerfile` for a Dockerfile.
std::string kind_of(const std::string& path);
const Language* language_for(const std::string& kind);
/// Where Mason puts the servers it installs.
fs::path folder(const fs::path& home = data_home());
/// The language server for a file of this kind, when one is installed. `language-servers.json` in Mason's data
/// folder and `MASON_LANGUAGE_SERVERS` can name one for any extension, as JSON such as {"zig": ["/opt/zls/zls"]}.
std::optional<LanguageServer> server_for(const std::string& kind, const fs::path& home = data_home());
/// True when some language server is installed, so the tool is worth offering.
bool available();

std::string frame(const json& message);
/// The whole messages at the start of what a server wrote, and how many bytes they took.
std::pair<std::vector<json>, size_t> frames(const std::string& output);

/// Asks a server about one position, starting it if `sessions` has not yet. The server runs as a command does:
/// `sandboxed` puts it in the sandbox.
std::string query(const Workspace& workspace, const LanguageServer& server, const std::string& path, int line, int column, const std::string& mode, bool sandboxed, LanguageSessions& sessions,
                  const CancelToken& cancel = nullptr);
/// One question to a server started for it and stopped after: what `mason symbol` uses.
std::string query(const Workspace& workspace, const LanguageServer& server, const std::string& path, int line, int column, const std::string& mode, bool sandboxed, const CancelToken& cancel = nullptr);
/// The errors a server reports for a file that was just written, as a note for the model; empty when it reports
/// none or does not answer in time.
std::string errors(const Workspace& workspace, const LanguageServer& server, const std::string& path, const std::string& text, bool sandboxed, LanguageSessions& sessions, const CancelToken& cancel = nullptr);
/// What Jedi or the TypeScript compiler, which ship with Mason, report for a file that was just written.
std::string built_in_errors(const Workspace& workspace, const std::string& path, const CancelToken& cancel = nullptr);

LanguageState state(const Language& language, const fs::path& home = data_home());
/// The same state in words for a settings row, and whether an Install button belongs beside it.
std::pair<std::string, bool> described(const Language& language, const fs::path& home = data_home());
/// The languages with files in the project.
std::vector<const Language*> present(const Workspace& workspace);
/// Installs a language's server with the language's own tool. Nothing is installed without this being called.
std::string install(const Language& language, const fs::path& home = data_home());
/// The /languages command, shared by the command line and the app. `workspace` may be null.
std::string command(const std::string& rest, const Workspace* workspace, const fs::path& home = data_home());
extern const char* const kHelp;
}  // namespace language_servers

}  // namespace mason
