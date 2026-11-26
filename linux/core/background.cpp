#include "background.hpp"

#include "agent.hpp"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <csignal>
#include <fstream>
#include <mutex>
#include <regex>
#include <thread>

#include <fcntl.h>
#include <sys/wait.h>
#include <unistd.h>

namespace mason::background {

namespace {

fs::path folder(const std::string& id, const fs::path& home) { return home / "background-tasks" / id; }

std::string checked(const std::string& id) {
    if (!std::regex_match(id, std::regex("[a-f0-9]{8,32}"))) throw Error("That is not a task id.");
    return id;
}

json to_json(const TaskStatus& status) {
    json value = {{"state", status.state}, {"detail", status.detail}, {"pid", status.pid}, {"started", static_cast<long long>(status.started)}, {"session", status.session}};
    if (status.approval) value["approval"] = {{"id", status.approval->id}, {"tool", status.approval->tool}, {"title", status.approval->title}, {"detail", status.approval->detail}, {"subject", status.approval->subject}};
    return value;
}

std::optional<json> read_json(const fs::path& file) {
    std::error_code ignored;
    if (!fs::exists(file, ignored) || fs::file_size(file, ignored) > 2 * 1024 * 1024) return std::nullopt;
    try {
        const json parsed = json::parse(read_file(file), nullptr, false);
        if (parsed.is_object()) return parsed;
    } catch (const std::exception&) {
    }
    return std::nullopt;
}

void write_json(const fs::path& file, const json& value) {
    try {
        write_file_atomic(file, value.dump(-1, ' ', false, json::error_handler_t::replace));
    } catch (const std::exception&) {
    }
}

std::string summary(const BackgroundTask& task, const TaskStatus& status) {
    std::string prompt = utf8_length(task.prompt) > 70 ? utf8_prefix(task.prompt, 70) : task.prompt;
    std::replace(prompt.begin(), prompt.end(), '\n', ' ');
    std::string text = task.id + "  " + status.state + (status.detail.empty() ? "" : " (" + status.detail + ")") + "  ·  " + prompt;
    if (status.approval) text += "\n    waiting on: " + status.approval->title + "  ·  /task allow " + task.id + " or /task deny " + task.id;
    return text;
}

}  // namespace

const char* const kUsage =
    "/task                     This project's background tasks\n"
    "/task start REQUEST       Run a request in the background; it keeps going after you close Mason\n"
    "/task show ID             What a task has done, and the approval it is waiting on\n"
    "/task allow ID            Allow what the task is waiting on (deny: /task deny ID)\n"
    "/task cancel ID           Stop a task\n"
    "A task started in Auto edits files and runs sandboxed commands without asking. Otherwise each edit and command waits for /task allow.";

std::string own_program() {
    std::error_code ignored;
    const fs::path self = fs::read_symlink("/proc/self/exe", ignored);
    return self.string();
}

std::string start(const std::string& program, const Workspace& workspace, const std::string& given, const ModelEndpoint& endpoint, bool automatic, const fs::path& home) {
    const std::string prompt = trim(given);
    if (prompt.empty() || utf8_length(prompt) > 16000) throw Error("Describe the task in 1–16,000 characters.");
    validated_endpoint(endpoint.url);
    if (program.empty() || ::access(program.c_str(), X_OK) != 0) throw Error("The mason program that would run the task was not found at " + program + ".");
    std::string id = new_id();
    id.erase(std::remove(id.begin(), id.end(), '-'), id.end());
    id = id.substr(0, 12);
    const fs::path place = folder(id, home);
    fs::create_directories(place);
    std::error_code ignored;
    const fs::path root = fs::weakly_canonical(workspace.root(), ignored);
    write_json(place / "request.json", {{"id", id}, {"workspace", root.empty() ? workspace.root() : root.string()}, {"prompt", prompt}, {"endpoint", {{"url", endpoint.url}, {"model", endpoint.model}, {"key", endpoint.key}}},
                                        {"auto", automatic}, {"created", static_cast<long long>(std::time(nullptr))}});
    TaskStatus starting;
    starting.state = "Starting";
    starting.started = std::time(nullptr);
    write_json(place / "status.json", to_json(starting));

    // A child that starts the worker and leaves at once, so the worker belongs to nobody here; the worker then takes
    // its own session, so closing this terminal or app does not reach it.
    const std::string log = (place / "worker.log").string(), home_text = home.string();
    const pid_t child = fork();
    if (child < 0) throw Error("The task could not start.");
    if (child == 0) {
        if (fork() == 0) {
            setsid();
            const int null = open("/dev/null", O_RDONLY);
            const int out = open(log.c_str(), O_WRONLY | O_CREAT | O_APPEND, 0600);
            dup2(null, 0); dup2(out, 1); dup2(out, 2);
            for (int fd = 3; fd < 256; fd++) close(fd);
            execl(program.c_str(), program.c_str(), "task-run", "--id", id.c_str(), "--task-home", home_text.c_str(), static_cast<char*>(nullptr));
            _exit(127);
        }
        _exit(0);
    }
    int ended = 0;
    waitpid(child, &ended, 0);
    return id;
}

std::optional<BackgroundTask> task(const std::string& id, const fs::path& home) {
    const auto value = read_json(folder(id, home) / "request.json");
    if (!value) return std::nullopt;
    BackgroundTask found;
    found.id = value->value("id", id);
    found.workspace = value->value("workspace", "");
    found.prompt = value->value("prompt", "");
    if (value->contains("endpoint") && (*value)["endpoint"].is_object()) {
        const json& endpoint = (*value)["endpoint"];
        found.endpoint = ModelEndpoint{endpoint.value("url", ""), endpoint.value("model", ""), endpoint.value("key", "")};
    }
    found.automatic = value->value("auto", false);
    found.created = static_cast<std::time_t>(value->value("created", 0LL));
    return found;
}

TaskStatus status(const std::string& id, const fs::path& home) {
    const auto value = read_json(folder(checked(id), home) / "status.json");
    if (!value) throw Error("No task " + id + ".");
    TaskStatus found;
    found.state = value->value("state", "");
    found.detail = value->value("detail", "");
    found.pid = value->value("pid", 0);
    found.started = static_cast<std::time_t>(value->value("started", 0LL));
    found.session = value->value("session", "");
    if (value->contains("approval") && (*value)["approval"].is_object()) {
        const json& approval = (*value)["approval"];
        found.approval = TaskApproval{approval.value("id", ""), approval.value("tool", ""), approval.value("title", ""), approval.value("detail", ""), approval.value("subject", "")};
    }
    if (found.active() && found.pid > 0 && ::kill(found.pid, 0) != 0 && errno == ESRCH) {
        found.state = "Stopped";
        found.detail = "The worker is no longer running and did not finish.";
        found.approval.reset();
    } else if (found.state == "Starting" && std::time(nullptr) - found.started > 30) {
        found.state = "Failed";
        found.detail = "The worker did not start. See worker.log in the task's folder.";
    }
    return found;
}

std::vector<std::pair<BackgroundTask, TaskStatus>> all(const Workspace* workspace, const fs::path& home) {
    std::vector<std::pair<BackgroundTask, TaskStatus>> found;
    std::error_code ignored;
    std::string root;
    if (workspace) {
        const fs::path real = fs::weakly_canonical(workspace->root(), ignored);
        root = real.empty() ? workspace->root() : real.string();
    }
    if (!fs::is_directory(home / "background-tasks", ignored)) return found;
    for (const auto& entry : fs::directory_iterator(home / "background-tasks", ignored)) {
        const std::string id = entry.path().filename().string();
        const auto one = task(id, home);
        if (!one || (workspace && one->workspace != root)) continue;
        try {
            found.emplace_back(*one, status(id, home));
        } catch (const std::exception&) {
        }
    }
    std::sort(found.begin(), found.end(), [](const auto& a, const auto& b) { return a.first.created > b.first.created; });
    return found;
}

std::vector<std::string> tail(const std::string& id, size_t lines, const fs::path& home) {
    const fs::path file = folder(checked(id), home) / "events.jsonl";
    if (!fs::exists(file)) return {};
    std::vector<std::string> all_lines = split(read_file(file), '\n', false);
    if (all_lines.size() > lines) all_lines.erase(all_lines.begin(), all_lines.end() - static_cast<std::ptrdiff_t>(lines));
    return all_lines;
}

void decide(const std::string& id, const std::string& request, bool allow, const fs::path& home) {
    const TaskStatus found = status(id, home);
    if (found.state != "Waiting for approval" || !found.approval) throw Error("Task " + id + " is not waiting for an approval.");
    if (found.approval->id != request) throw Error("Task " + id + " is now waiting on a different approval (" + found.approval->id + "). Look at it again.");
    write_json(folder(id, home) / "decision.json", {{"request", request}, {"decision", allow ? "allow" : "deny"}});
}

void cancel(const std::string& id, const fs::path& home) {
    status(id, home);
    std::ofstream(folder(id, home) / "cancel").put('\n');
}

void detach() {
    setsid();
    std::signal(SIGHUP, SIG_IGN);
}

int work(const std::string& given, const fs::path& home, ChatModel* model) {
    std::optional<BackgroundTask> spec;
    try {
        spec = task(checked(given), home);
    } catch (const std::exception&) {
    }
    if (!spec) return 1;
    const std::string id = spec->id;
    const fs::path place = folder(id, home);
    std::mutex lock;
    TaskStatus record;
    record.state = "Running";
    record.pid = static_cast<int>(getpid());
    record.started = std::time(nullptr);
    auto change = [&](const std::function<void(TaskStatus&)>& body) {
        std::lock_guard<std::mutex> guard(lock);
        body(record);
        write_json(place / "status.json", to_json(record));
    };
    change([](TaskStatus&) {});
    auto log = [&](const std::string& type, const json& data) {
        std::lock_guard<std::mutex> guard(lock);
        std::ofstream file(place / "events.jsonl", std::ios::app | std::ios::binary);
        file << json{{"type", type}, {"data", data}, {"at", static_cast<long long>(std::time(nullptr))}}.dump(-1, ' ', false, json::error_handler_t::replace) << "\n";
    };
    const fs::path cancel_file = place / "cancel", decision_file = place / "decision.json";
    const CancelToken cancelling = make_cancel();
    std::atomic<bool> finished{false};
    std::thread watcher([&] {
        while (!finished.load()) {
            std::error_code ignored;
            if (fs::exists(cancel_file, ignored)) { cancelling->store(true); return; }
            std::this_thread::sleep_for(std::chrono::milliseconds(300));
        }
    });
    int code = 1;
    try {
        std::unique_ptr<OpenAIClient> owned;
        if (!model) {
            owned = std::make_unique<OpenAIClient>(spec->endpoint);
            model = owned.get();
        }
        const Workspace workspace(spec->workspace);
        SessionStore store(home);
        Session session = store.create(workspace.root(), spec->automatic ? Permission::Auto : Permission::ReviewEdits);
        session.title = "Background: " + (utf8_length(spec->prompt) > 60 ? utf8_prefix(spec->prompt, 60) : spec->prompt);
        change([&](TaskStatus& status) { status.session = session.id; });
        log("task.started", {{"session", session.id}, {"workspace", spec->workspace}, {"mode", spec->automatic ? "auto" : "review"}});
        Agent agent(store, *model,
                    [&](const ApprovalRequest& request) {
                        TaskApproval approval{new_id().substr(0, 8), request.tool, request.title, utf8_length(request.detail) > 4000 ? utf8_prefix(request.detail, 4000) : request.detail, request.subject};
                        change([&](TaskStatus& status) { status.state = "Waiting for approval"; status.approval = approval; });
                        log("approval.required", {{"id", approval.id}, {"tool", approval.tool}, {"title", approval.title}, {"subject", approval.subject}});
                        bool allowed = false;
                        while (!cancelling->load()) {
                            if (const auto answer = read_json(decision_file)) {
                                std::error_code ignored;
                                fs::remove(decision_file, ignored);
                                if (answer->value("request", "") == approval.id) { allowed = answer->value("decision", "") == "allow"; break; }
                            }
                            std::this_thread::sleep_for(std::chrono::milliseconds(200));
                        }
                        change([](TaskStatus& status) { status.state = "Running"; status.approval.reset(); });
                        log("approval.decided", {{"id", approval.id}, {"allowed", allowed}});
                        return allowed;
                    },
                    [&](const AgentEvent& event) {
                        if (event.kind != AgentEvent::Kind::ActivityFinished) return;
                        log("tool.result", {{"tool", event.activity.tool}, {"subject", event.activity.subject}, {"state", event.activity.state},
                                            {"result", utf8_length(event.activity.result) > 2000 ? utf8_prefix(event.activity.result, 2000) : event.activity.result}});
                    });
        agent.run(session, spec->prompt, cancelling);
        const bool stopped = cancelling->load();
        const std::string state = stopped ? "Cancelled" : session.state;
        change([&](TaskStatus& status) { status.state = state; status.detail = session.detail; status.approval.reset(); });
        log("task.finished", {{"state", state}, {"detail", session.detail}, {"answer", session.turns.empty() ? "" : session.turns.back().answer}});
        code = session.state == "Completed" && !stopped ? 0 : 2;
    } catch (const std::exception& error) {
        const std::string message = error.what();
        change([&](TaskStatus& status) { status.state = "Failed"; status.detail = message; status.approval.reset(); });
        log("task.finished", {{"state", "Failed"}, {"detail", message}});
    }
    finished.store(true);
    watcher.join();
    return code;
}

std::string command(const std::string& rest, const Workspace& workspace, const std::optional<ModelEndpoint>& endpoint, bool automatic, const std::string& program, const fs::path& home) {
    const std::string trimmed = trim(rest);
    const size_t space = trimmed.find(' ');
    const std::string verb = to_lower(trimmed.substr(0, space));
    const std::string argument = space == std::string::npos ? "" : trim(trimmed.substr(space + 1));
    if (verb.empty() || verb == "list") {
        const auto mine = all(&workspace, home);
        if (mine.empty()) return "No background tasks in this project. /task start REQUEST runs one.";
        std::string text;
        for (size_t i = 0; i < mine.size() && i < 20; i++) text += (i ? "\n" : "") + summary(mine[i].first, mine[i].second);
        return text;
    }
    if (verb == "start") {
        if (!endpoint) throw Error("Choose a model first; a background task uses the one this session uses.");
        const std::string id = start(program, workspace, argument, *endpoint, automatic, home);
        return "Started task " + id + " in the background" + (automatic ? ", in Auto." : "; it will wait for /task allow before each edit and command.") + " /task show " + id + " follows it.";
    }
    if (verb == "show") {
        const TaskStatus state = status(argument, home);
        const auto one = task(argument, home);
        if (!one) throw Error("No task " + argument + ".");
        std::string text = summary(*one, state);
        if (state.approval && !state.approval->detail.empty()) text += "\n" + state.approval->detail;
        for (const std::string& line : tail(argument, 12, home)) {
            const json object = json::parse(line, nullptr, false);
            if (!object.is_object() || !object.contains("data") || !object["data"].is_object()) continue;
            const json& data = object["data"];
            const std::string type = object.value("type", "");
            if (type == "tool.result") text += "\n  " + data.value("tool", "") + " " + data.value("subject", "") + "  →  " + data.value("state", "");
            if (type == "task.finished") {
                const std::string answer = data.value("answer", "");
                text += "\n  " + (answer.empty() ? "finished: " + data.value("state", "") : (utf8_length(answer) > 1200 ? utf8_prefix(answer, 1200) : answer));
            }
        }
        if (!state.session.empty() && !state.active()) text += "\nIts session is " + state.session + "; /resume shows it in that project.";
        return text;
    }
    if (verb == "allow" || verb == "deny") {
        const TaskStatus state = status(argument, home);
        if (!state.approval) throw Error("Task " + argument + " is not waiting for an approval.");
        decide(argument, state.approval->id, verb == "allow", home);
        return (verb == "allow" ? "Allowed: " : "Denied: ") + state.approval->title;
    }
    if (verb == "cancel") {
        cancel(argument, home);
        return "Asked task " + argument + " to stop.";
    }
    return kUsage;
}

}  // namespace mason::background
