#include "script.hpp"

#include "agent.hpp"
#include "experiments.hpp"
#include "navigation.hpp"
#include "shell.hpp"

#include <fstream>
#include <sstream>

namespace mason::script {

const std::set<std::string>& names() {
    static const std::set<std::string> all = {"exec", "map", "symbol", "git-status", "worktree"};
    return all;
}

const char* const kUsage =
    "mason exec --workspace PATH --endpoint URL --model ID --prompt TEXT [--auto] [--profile PATH] [--resume SESSION_ID]\n"
    "mason map --workspace PATH --query NAME\n"
    "mason symbol --workspace PATH --path FILE --line N --column N --mode definitions|references|diagnostics\n"
    "mason git-status --workspace PATH\n"
    "mason worktree --workspace PATH --destination NEW_PATH --branch NEW_BRANCH\n"
    "  For scripts: one operation, printed as JSON Lines, then exit. 0 means it completed, 2 that it ran and did not, 1 an error.\n"
    "  exec only reads unless you pass --auto, which lets it edit files and run commands in the sandbox. Nothing is ever asked:\n"
    "  an approval it would need is reported and declined. --prompt-file PATH reads the request from a file.\n"
    "  Each exec keeps its own journal, or the one in --profile.\n";

int run(const std::vector<std::string>& arguments, const std::function<void(const std::string&)>& write, ChatModel* model, const CancelToken& cancel) {
    auto event = [&](const std::string& type, const json& data) {
        write(json{{"type", type}, {"data", data}}.dump(-1, ' ', false, json::error_handler_t::replace));
    };
    try {
        if (arguments.empty() || !names().count(arguments[0])) throw Error("Unknown operation.");
        const std::string& operation = arguments[0];
        std::map<std::string, std::string> values;
        bool automatic = false;
        for (size_t i = 1; i < arguments.size(); i++) {
            if (arguments[i] == "--auto") { automatic = true; continue; }
            if (arguments[i].rfind("--", 0) != 0 || i + 1 >= arguments.size() || values.count(arguments[i].substr(2))) throw Error("Expected unique --option value pairs.");
            values[arguments[i].substr(2)] = arguments[i + 1];
            i++;
        }
        auto get = [&](const std::string& name) -> std::string {
            const auto found = values.find(name);
            if (found == values.end()) throw Error("--" + name + " is required.");
            return found->second;
        };
        auto whole = [&](const std::string& name) -> int {
            const std::string digits = get(name);
            if (digits.empty() || digits.size() > 9 || digits.find_first_not_of("0123456789") != std::string::npos || std::stoi(digits) < 1) throw Error("--line and --column are numbers from 1.");
            return std::stoi(digits);
        };
        const Workspace workspace(get("workspace"));

        if (operation == "map") {
            const std::string query = get("query");
            const MapResult result = repository_map::query(workspace, query, ".", 32, cancel);
            json matches = json::array();
            for (const MapMatch& match : result.matches) matches.push_back({{"path", match.path}, {"line", match.line}, {"kind", match.kind}, {"name", match.name}, {"text", match.text}});
            event("map.result", {{"query", query}, {"files", result.files}, {"partial", result.partial}, {"matches", matches}});
            return 0;
        }
        if (operation == "worktree") {
            event("worktree.created", {{"path", experiments::create_worktree(workspace, get("destination"), get("branch"))}});
            return 0;
        }
        if (operation == "git-status") {
            if (!fs::exists(fs::path(workspace.root()) / ".git")) {
                event("git.status", {{"repository", false}, {"output", ""}});
                return 0;
            }
            // Nothing in the repository's own settings is allowed to start a program for this.
            const CommandResult result = shell::run("git -c core.fsmonitor= -c core.hooksPath=/dev/null --no-optional-locks status --porcelain=v1 --branch", workspace, shell::sandbox_available(), 20, cancel);
            event("git.status", {{"repository", true}, {"exit", result.exit_code}, {"output", result.output}});
            return result.exit_code == 0 ? 0 : 2;
        }
        if (operation == "symbol") {
            const std::string path = get("path"), mode = get("mode");
            const int line = whole("line"), column = whole("column");
            std::string report;
            if (symbols::handles(path)) {
                report = symbols::query(workspace, path, line, column, mode, cancel);
            } else {
                const std::string extension = fs::path(path).extension().string();
                const std::optional<LanguageServer> server = language_servers::server_for(extension.empty() ? "" : extension.substr(1));
                if (!server) throw Error("No language server for " + (extension.empty() ? std::string("this file") : extension + " files") + " was found on this computer.");
                // A language server may run the project's build, and a script has nobody to ask, so it only runs sandboxed.
                if (!shell::sandbox_available()) throw Error(server->name + " is only started inside the sandbox here, and the sandbox is unavailable.");
                report = language_servers::query(workspace, *server, path, line, column, mode, true, cancel);
            }
            event("symbol.result", {{"path", path}, {"line", line}, {"column", column}, {"mode", mode}, {"report", report}});
            return 0;
        }

        std::string prompt;
        if (values.count("prompt-file")) {
            std::ifstream file(values["prompt-file"], std::ios::binary);
            if (!file) throw Error("The prompt file could not be read.");
            std::ostringstream content;
            content << file.rdbuf();
            prompt = content.str();
        } else {
            prompt = get("prompt");
        }
        const fs::path profile = values.count("profile") ? fs::absolute(values["profile"]).lexically_normal() : data_home() / "cli-runs" / new_id();
        // Compared as real paths, so a link cannot hide a journal inside the project. The journal may not exist yet.
        std::error_code ignored;
        const std::string real = fs::weakly_canonical(profile, ignored).string(), root = fs::weakly_canonical(workspace.root(), ignored).string();
        if (((real.empty() ? profile.string() : real) + "/").rfind((root.empty() ? workspace.root() : root) + "/", 0) == 0) throw Error("Keep the journal outside the project.");
        std::unique_ptr<OpenAIClient> owned;
        if (!model) {
            owned = std::make_unique<OpenAIClient>(ModelEndpoint{validated_endpoint(get("endpoint")), get("model"), ""});
            model = owned.get();
        }
        SessionStore store(profile);
        std::optional<Session> session;
        if (values.count("resume")) {
            for (const Session& item : store.sessions_in(workspace.root())) {
                if (item.id == values["resume"]) session = item;
            }
            if (!session) throw Error("No session " + values["resume"] + " for this project in that journal.");
            session->permission = automatic ? Permission::Auto : Permission::ReadOnly;
        } else {
            session = store.create(workspace.root(), automatic ? Permission::Auto : Permission::ReadOnly);
        }
        session->ask_only = !automatic;
        event("run.started", {{"id", session->id}, {"workspace", workspace.root()}, {"profile", profile.string()}, {"model", values.count("model") ? values["model"] : ""}, {"mode", automatic ? "build" : "ask"}});
        Agent agent(store, *model,
                    [&](const ApprovalRequest& request) {
                        event("approval.required", {{"tool", request.tool}, {"title", request.title}, {"subject", request.subject}});
                        return false;
                    },
                    [&](const AgentEvent& change) {
                        if (change.kind != AgentEvent::Kind::ActivityFinished) return;
                        event("tool.result", {{"tool", change.activity.tool}, {"subject", change.activity.subject}, {"state", change.activity.state}, {"result", change.activity.result}});
                    });
        agent.run(*session, prompt, cancel);
        event("run.finished", {{"id", session->id}, {"state", session->state}, {"detail", session->detail}, {"answer", session->turns.empty() ? "" : session->turns.back().answer}});
        return session->state == "Completed" ? 0 : 2;
    } catch (const std::exception& error) {
        event("error", {{"message", error.what()}});
        return 1;
    }
}

}  // namespace mason::script
