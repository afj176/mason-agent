#include "script.hpp"

#include "agent.hpp"
#include "background.hpp"
#include "experiments.hpp"
#include "desktop.hpp"
#include "acceptance.hpp"
#include "navigation.hpp"
#include "preview.hpp"
#include "roles.hpp"
#include "shell.hpp"

#include <fstream>
#include <sstream>

namespace mason::script {

const std::set<std::string>& names() {
    static const std::set<std::string> all = {"exec", "map", "symbol", "git-status", "worktree", "review", "serve", "task-start", "task-list", "task-show", "task-allow", "task-deny", "task-cancel", "task-run"};
    return all;
}

const char* const kUsage =
    "mason exec --workspace PATH --endpoint URL --model ID --prompt TEXT [--auto] [--profile PATH] [--resume SESSION_ID]\n"
    "                   [--visual-model ID [--visual-endpoint URL]] [--trace-dir FOLDER]\n"
    "mason review --workspace PATH --endpoint URL --model ID --prompt TEXT --path FILE\n"
    "mason serve --workspace PATH --workflow NAME --port PORT [--page PATH]\n"
    "mason map --workspace PATH --query NAME\n"
    "mason symbol --workspace PATH --path FILE --line N --column N --mode definitions|references|diagnostics\n"
    "mason git-status --workspace PATH\n"
    "mason worktree --workspace PATH --destination NEW_PATH --branch NEW_BRANCH\n"
    "mason task-start --workspace PATH --endpoint URL --model ID --prompt TEXT [--auto]\n"
    "mason task-list [--workspace PATH]\n"
    "mason task-show --id TASK_ID\n"
    "mason task-allow|task-deny --id TASK_ID --request APPROVAL_ID\n"
    "mason task-cancel --id TASK_ID\n"
    "  For scripts: one operation, printed as JSON Lines, then exit. 0 means it completed, 2 that it ran and did not, 1 an error.\n"
    "  exec only reads unless you pass --auto, which lets it edit files and run commands in the sandbox. Nothing is ever asked:\n"
    "  an approval it would need is reported and declined. --prompt-file PATH reads the request from a file.\n"
    "  Each exec keeps its own journal, or the one in --profile.\n"
    "  review has the model read one file against what it was meant to do; it exits 2 when it finds something.\n"
    "  serve starts the workflow with your account, waits for the port, fetches the page, and stops the server.\n"
    "  A task runs in the background and keeps going after this command returns. Without --auto it waits for\n"
    "  task-allow before each edit and command; task-show prints the approval it is waiting on.\n";

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
        if (operation.rfind("task-", 0) == 0) {
            const fs::path home = values.count("task-home") ? fs::path(values["task-home"]) : data_home();
            if (operation == "task-run") {
                background::detach();
                return background::work(get("id"), home, model);
            }
            if (operation == "task-start") {
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
                const ModelEndpoint endpoint{validated_endpoint(get("endpoint")), get("model"), ""};
                event("task.started", {{"id", background::start(background::own_program(), Workspace(get("workspace")), prompt, endpoint, automatic, home)}});
            } else if (operation == "task-list") {
                std::optional<Workspace> only;
                if (values.count("workspace")) only.emplace(values["workspace"]);
                json tasks = json::array();
                for (const auto& [task, status] : background::all(only ? &*only : nullptr, home)) {
                    tasks.push_back({{"id", task.id}, {"workspace", task.workspace}, {"prompt", task.prompt}, {"state", status.state}, {"detail", status.detail}});
                }
                event("tasks", {{"tasks", tasks}});
            } else if (operation == "task-show") {
                const std::string id = get("id");
                const TaskStatus state = background::status(id, home);
                json events = json::array();
                for (const std::string& line : background::tail(id, 40, home)) {
                    const json parsed = json::parse(line, nullptr, false);
                    if (parsed.is_object()) events.push_back(parsed);
                }
                json data = {{"id", id}, {"state", state.state}, {"detail", state.detail}, {"session", state.session}, {"events", events}};
                if (state.approval) data["approval"] = {{"id", state.approval->id}, {"tool", state.approval->tool}, {"title", state.approval->title}, {"detail", state.approval->detail}, {"subject", state.approval->subject}};
                event("task", data);
            } else if (operation == "task-allow" || operation == "task-deny") {
                background::decide(get("id"), get("request"), operation == "task-allow", home);
                event("task.decision", {{"id", get("id")}, {"request", get("request")}, {"allowed", operation == "task-allow"}});
            } else {
                background::cancel(get("id"), home);
                event("task.cancel", {{"id", get("id")}});
            }
            return 0;
        }
        const Workspace workspace(get("workspace"));

        if (operation == "map") {
            const std::string query = get("query");
            const MapResult result = repository_map::query(workspace, query, ".", 32, cancel);
            json matches = json::array();
            for (const MapMatch& match : result.matches) matches.push_back({{"path", match.path}, {"line", match.line}, {"kind", match.kind}, {"name", match.name}, {"text", match.text}});
            event("map.result", {{"query", query}, {"files", result.files}, {"partial", result.partial}, {"matches", matches}});
            return 0;
        }
        if (operation == "review") {
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
            const std::string path = get("path");
            if (!workspace.exists(path)) throw Error(path + " is not a file in the project.");
            std::unique_ptr<OpenAIClient> owned;
            if (!model) {
                owned = std::make_unique<OpenAIClient>(ModelEndpoint{validated_endpoint(get("endpoint")), get("model"), ""});
                model = owned.get();
            }
            // The review pass reads what a session changed; here that is the one file, against the stated intent.
            Session reading;
            reading.workspace = workspace.root();
            Turn turn;
            turn.request = prompt;
            turn.changes.push_back(FileChange{path, std::nullopt, workspace.read(path)});
            reading.turns.push_back(turn);
            const std::unique_ptr<RoleModel> reviewer = roles::reviewer(*model, &workspace, data_home());
            const ReviewFindings found = review::findings(reading, workspace, *reviewer, cancel);
            json findings = json::array();
            for (const ReviewComment& item : found.comments) findings.push_back({{"line", item.line}, {"quote", item.quote}, {"problem", item.note}});
            event("review.result", {{"path", path}, {"dropped", found.dropped}, {"findings", findings}});
            return found.comments.empty() ? 0 : 2;
        }
        if (operation == "serve") {
            const std::string name = get("workflow");
            const int port = whole("port");
            preview::DevServer server(workspace, name, port, cancel);
            event("server.ready", {{"workflow", name}, {"port", port}, {"address", server.address()}});
            const std::string page = values.count("page") ? values["page"] : "";
            json fetched = {{"address", server.address() + page}};
            const std::optional<std::string> body = preview::fetch(port, "/" + page);
            if (body) {
                fetched["bytes"] = body->size();
                const size_t open = to_lower(*body).find("<title"), start = open == std::string::npos ? open : body->find('>', open), end = start == std::string::npos ? start : body->find('<', start);
                fetched["title"] = end == std::string::npos ? "" : trim(body->substr(start + 1, end - start - 1));
            } else {
                fetched["error"] = "The server listened, but the page could not be fetched.";
            }
            event("page.result", fetched);
            event("server.output", {{"output", server.output()}});
            return body ? 0 : 2;
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
        std::unique_ptr<RoleModel> traced = roles::implementer(*model, &workspace, data_home());
        if (values.count("trace-dir")) {
            const fs::path place = fs::absolute(values["trace-dir"]).lexically_normal();
            fs::create_directories(place);
            std::error_code unused;
            if ((fs::weakly_canonical(place, unused).string() + "/").rfind(fs::weakly_canonical(workspace.root(), unused).string() + "/", 0) == 0) throw Error("Keep decision traces outside the project.");
            traced = std::make_unique<RoleModel>(*model, "implementer", 0, place);
        }
        Agent agent(store, traced ? static_cast<ChatModel&>(*traced) : *model,
                    [&](const ApprovalRequest& request) {
                        event("approval.required", {{"tool", request.tool}, {"title", request.title}, {"subject", request.subject}});
                        return false;
                    },
                    [&](const AgentEvent& change) {
                        if (change.kind != AgentEvent::Kind::ActivityFinished) return;
                        event("tool.result", {{"tool", change.activity.tool}, {"subject", change.activity.subject}, {"state", change.activity.state}, {"result", change.activity.result}});
                    });
        if (values.count("visual-model")) {
            agent.set_vision(std::make_shared<ModelVision>(ModelEndpoint{validated_endpoint(values.count("visual-endpoint") ? values["visual-endpoint"] : get("endpoint")), values["visual-model"], ""}));
        }
        agent.run(*session, prompt, cancel);
        event("run.finished", {{"id", session->id}, {"state", session->state}, {"detail", session->detail}, {"answer", session->turns.empty() ? "" : session->turns.back().answer}});
        return session->state == "Completed" ? 0 : 2;
    } catch (const std::exception& error) {
        event("error", {{"message", error.what()}});
        return 1;
    }
}

}  // namespace mason::script
