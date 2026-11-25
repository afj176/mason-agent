#include "controller.hpp"

#include <algorithm>
#include <chrono>

namespace mason::app {

Controller::Controller() {
    dispatcher_.connect(sigc::mem_fun(*this, &Controller::drain));
    try {
        store_ = std::make_unique<SessionStore>();
        sessions_ = store_->sessions();
        if (!sessions_.empty()) selection = sessions_.front().id;
    } catch (const std::exception& error) {
        const std::string message = error.what();
        post([this, message] { notice.emit("Mason could not open its data folder: " + message); });
    }
    runtime_path = llama::find();
    refresh_models(true);
}

Controller::~Controller() {
    *alive_ = false;
    if (run_cancel_) run_cancel_->store(true);
    if (model_cancel_) model_cancel_->store(true);
    {
        // A request waiting for an approval must not wait forever.
        std::lock_guard<std::mutex> guard(approval_lock_);
        approval_answer_ = false;
    }
    approval_answered_.notify_all();
    if (run_thread_.joinable()) run_thread_.join();
    if (model_thread_.joinable()) model_thread_.join();
    server_.reset();
}

void Controller::post(std::function<void()> work) {
    {
        std::lock_guard<std::mutex> guard(queue_lock_);
        queue_.push_back(std::move(work));
    }
    dispatcher_.emit();
}

void Controller::drain() {
    while (true) {
        std::function<void()> work;
        {
            std::lock_guard<std::mutex> guard(queue_lock_);
            if (queue_.empty()) break;
            work = std::move(queue_.front());
            queue_.pop_front();
        }
        work();
    }
    changed.emit();
}

// ---- sessions --------------------------------------------------------------------------------------------------

const Session* Controller::session() const {
    for (const Session& item : sessions_) {
        if (item.id == selection) return &item;
    }
    return nullptr;
}

std::vector<std::string> Controller::projects() const {
    std::vector<std::string> seen;
    for (const Session& item : sessions_) {
        if (std::find(seen.begin(), seen.end(), item.workspace) == seen.end()) seen.push_back(item.workspace);
    }
    return seen;
}

void Controller::replace(const Session& session) {
    auto existing = std::find_if(sessions_.begin(), sessions_.end(), [&](const Session& item) { return item.id == session.id; });
    if (existing != sessions_.end()) *existing = session;
    else sessions_.insert(sessions_.begin(), session);
}

void Controller::select(const std::string& id) {
    if (selection == id) return;
    selection = id;
    changed.emit();
}

void Controller::new_session(const std::string& folder) {
    if (!store_) return;
    const Session* current = session();
    const std::string path = !folder.empty() ? folder : current ? current->workspace : "";
    if (path.empty()) return;
    try {
        const Workspace workspace(path);
        // Reuse an untouched session rather than collecting empty ones.
        for (const Session& item : sessions_) {
            if (item.workspace == workspace.root() && item.turns.empty()) {
                selection = item.id;
                changed.emit();
                return;
            }
        }
        Session created = store_->create(workspace.root(), current ? current->permission : Permission::ReviewEdits);
        replace(created);
        selection = created.id;
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
    changed.emit();
}

void Controller::remove_session(const std::string& id) {
    if (running && id == selection) {
        notice.emit("Stop the run before deleting this session.");
        return;
    }
    if (store_) store_->remove(id);
    sessions_.erase(std::remove_if(sessions_.begin(), sessions_.end(), [&](const Session& item) { return item.id == id; }), sessions_.end());
    if (selection == id) selection = sessions_.empty() ? "" : sessions_.front().id;
    changed.emit();
}

void Controller::set_permission(Permission permission) {
    const Session* current = session();
    if (!current || running || !store_ || current->permission == permission) return;
    Session updated = *current;
    updated.permission = permission;
    updated.ask_only = permission == Permission::ReadOnly;
    try {
        store_->save(updated);
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
    replace(updated);
    changed.emit();
}

// ---- running a request -----------------------------------------------------------------------------------------

void Controller::send(const std::string& text) {
    const std::string input = trim(text);
    const Session* current = session();
    if (input.empty() || running || !current || !store_) return;
    if (!endpoint) {
        notice.emit("No model is running. Open Models to start one, or start your own local server.");
        return;
    }
    if (run_thread_.joinable()) run_thread_.join();
    running = true;
    live_text.clear();
    live_status = "Thinking";
    live_activities.clear();
    // Show the request at once; the engine's copy replaces it when the run ends.
    Session working = *current;
    {
        Session shown = working;
        Turn turn;
        turn.request = input;
        turn.first_activity = shown.activities.size();
        shown.turns.push_back(std::move(turn));
        shown.state = "Running";
        replace(shown);
    }
    run_cancel_ = make_cancel();
    {
        std::lock_guard<std::mutex> guard(approval_lock_);
        approval_answer_.reset();
    }
    const ModelEndpoint target = *endpoint;
    const CancelToken cancel = run_cancel_;
    const std::shared_ptr<bool> alive = alive_;
    run_thread_ = std::thread([this, working, input, target, cancel, alive]() mutable {
        try {
            OpenAIClient model(target);
            Agent agent(
                *store_, model,
                [&](const ApprovalRequest& request) {
                    post([this, request] { approval = request; });
                    std::unique_lock<std::mutex> guard(approval_lock_);
                    approval_answered_.wait(guard, [&] { return approval_answer_.has_value(); });
                    const bool allow = *approval_answer_;
                    approval_answer_.reset();
                    return allow;
                },
                [&](const AgentEvent& event) {
                    post([this, event] {
                        switch (event.kind) {
                            case AgentEvent::Kind::Text: live_text += event.text; break;
                            case AgentEvent::Kind::ActivityStarted:
                                live_text.clear();
                                live_status = replace_all(event.activity.tool, "_", " ") + " " + event.activity.subject;
                                live_activities.push_back(event.activity);
                                break;
                            case AgentEvent::Kind::ActivityFinished:
                                for (Activity& item : live_activities) {
                                    if (item.id == event.activity.id) item = event.activity;
                                }
                                live_status = "Thinking";
                                break;
                            case AgentEvent::Kind::Status: live_status = event.text; break;
                        }
                    });
                });
            agent.run(working, input, cancel);
        } catch (const std::exception& error) {
            working.state = "Failed";
            working.detail = error.what();
        }
        if (!*alive) return;
        post([this, working] {
            approval.reset();
            replace(working);
            running = false;
            live_text.clear();
            live_status.clear();
            live_activities.clear();
        });
    });
    changed.emit();
}

void Controller::stop() {
    if (run_cancel_) run_cancel_->store(true);
    if (approval) answer(false);
}

void Controller::answer(bool allow, const std::string& always_prefix) {
    if (!approval) return;
    if (allow && !always_prefix.empty() && store_) {
        if (const Session* current = session()) {
            try {
                ApprovalRules(store_->home(), current->workspace).add(approval->tool, always_prefix);
            } catch (const std::exception& error) {
                notice.emit(error.what());
            }
        }
    }
    approval.reset();
    {
        std::lock_guard<std::mutex> guard(approval_lock_);
        approval_answer_ = allow;
    }
    approval_answered_.notify_all();
    changed.emit();
}

void Controller::undo(bool redo) {
    const Session* current = session();
    if (!current || running || !store_) return;
    Session updated = *current;
    try {
        const std::optional<UndoResult> result = Agent::undo_latest(updated, *store_, redo);
        if (!result) {
            notice.emit(redo ? "Nothing to redo." : "Nothing to undo in this session.");
            return;
        }
        replace(updated);
        std::string message = std::to_string(result->restored.size()) + (result->restored.size() == 1 ? " file " : " files ") + (redo ? "reapplied." : "put back.");
        if (!result->skipped.empty()) message += " Changed since and left alone: " + join(result->skipped, ", ") + ".";
        notice.emit(message);
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
    changed.emit();
}

// ---- models ----------------------------------------------------------------------------------------------------

void Controller::use(const ModelEndpoint& chosen, const std::string& name) {
    endpoint = chosen;
    endpoint_name = name;
    Preferences preferences = Preferences::load();
    preferences.last_endpoint = chosen;
    preferences.save();
    changed.emit();
}

void Controller::model_task(const std::string& label, std::function<std::function<void()>(const std::function<void(const std::string&)>&, const CancelToken&)> work) {
    if (!model_busy.empty() || download) return;
    if (model_thread_.joinable()) model_thread_.join();
    model_busy = label;
    model_cancel_ = make_cancel();
    const CancelToken cancel = model_cancel_;
    const std::shared_ptr<bool> alive = alive_;
    model_thread_ = std::thread([this, work = std::move(work), cancel, alive, label] {
        std::function<void()> done;
        std::string failure;
        try {
            done = work([this, alive](const std::string& text) {
                if (*alive && !text.empty()) post([this, text] { if (!model_busy.empty()) model_busy = text; });
            }, cancel);
        } catch (const Cancelled&) {
        } catch (const std::exception& error) {
            failure = error.what();
        }
        if (!*alive) return;
        post([this, done, failure] {
            model_busy.clear();
            download.reset();
            if (!failure.empty()) notice.emit(failure);
            if (done) done();
        });
    });
    changed.emit();
}

void Controller::refresh_models(bool start_last_if_needed) {
    model_task("Looking for models on this computer…", [this, start_last_if_needed](const auto&, const CancelToken&) -> std::function<void()> {
        std::vector<RunningServer> found = detect_servers();
        std::vector<LocalModel> files = scan_models();
        std::string runtime = llama::find();
        // The endpoint used last may be on a port the scan does not cover (a server the user started themselves).
        const Preferences remembered = Preferences::load();
        if (remembered.last_endpoint && std::none_of(found.begin(), found.end(), [&](const RunningServer& s) { return s.endpoint == *remembered.last_endpoint; })) {
            const auto listed = OpenAIClient::models(remembered.last_endpoint->url);
            if (listed && std::find(listed->begin(), listed->end(), remembered.last_endpoint->model) != listed->end()) found.insert(found.begin(), {"Local server", *remembered.last_endpoint});
        }
        return [this, found, files, runtime, start_last_if_needed] {
            servers = found;
            local_models = files;
            runtime_path = runtime;
            auto listed = [&](const ModelEndpoint& wanted) { return std::any_of(found.begin(), found.end(), [&](const RunningServer& s) { return s.endpoint == wanted; }); };
            if (endpoint && listed(*endpoint)) return;
            const Preferences preferences = Preferences::load();
            if (preferences.last_endpoint && listed(*preferences.last_endpoint)) {
                endpoint = preferences.last_endpoint;
                endpoint_name = endpoint->model;
                return;
            }
            if (!found.empty()) {
                endpoint = found.front().endpoint;
                endpoint_name = found.front().endpoint.model + " (" + found.front().name + ")";
                return;
            }
            endpoint.reset();
            endpoint_name.clear();
            // With nothing answering at launch, restart the model used last.
            if (start_last_if_needed && !runtime.empty()) {
                for (const LocalModel& model : files) {
                    if (model.path == preferences.last_model_path) {
                        const LocalModel last = model;
                        const int context = preferences.last_context;
                        post([this, last, context] { start_model(last, context); });
                        break;
                    }
                }
            }
        };
    });
}

void Controller::start_model(const LocalModel& model, int context) {
    if (runtime_path.empty()) {
        notice.emit("Install llama.cpp first; it runs GGUF models.");
        return;
    }
    const std::string runtime = runtime_path;
    // The previous model is stopped first so two models never hold memory at once.
    if (server_) {
        if (endpoint && *endpoint == server_->endpoint) endpoint.reset();
        server_.reset();
    }
    model_task("Starting " + model.name + "…", [this, model, context, runtime](const auto& progress, const CancelToken& cancel) -> std::function<void()> {
        std::shared_ptr<ModelServer> started = ModelServer::start(model, runtime, context, false, [&](const std::string& line) {
            if (!line.empty()) progress("Starting " + model.name + "… " + utf8_suffix(line, 90));
        }, cancel);
        return [this, started, model] {
            // Replacing the previous server stops it.
            server_ = started;
            endpoint = started->endpoint;
            endpoint_name = model.name;
            Preferences preferences = Preferences::load();
            preferences.last_endpoint = started->endpoint;
            preferences.last_model_path = model.path;
            preferences.last_context = started->context;
            preferences.save();
            post([this] { refresh_models(); });
        };
    });
}

void Controller::stop_server() {
    if (!server_) return;
    if (endpoint && *endpoint == server_->endpoint) {
        endpoint.reset();
        endpoint_name.clear();
    }
    server_.reset();
    changed.emit();
    refresh_models();
}

void Controller::cancel_model_work() {
    if (model_cancel_) model_cancel_->store(true);
}

void Controller::install_runtime() {
    model_task("Finding the current llama.cpp release…", [this](const auto& progress, const CancelToken& cancel) -> std::function<void()> {
        const llama::Release release = llama::latest(cancel);
        const std::string path = llama::install(release, progress, cancel);
        return [this, path] {
            runtime_path = path;
            notice.emit("llama.cpp is installed.");
        };
    });
}

void Controller::search(const std::string& query) {
    const std::string text = trim(query);
    if (text.empty()) return;
    repository.reset();
    model_task("Searching Hugging Face…", [this, text](const auto&, const CancelToken& cancel) -> std::function<void()> {
        std::vector<HubModel> results = hub_search(text, cancel);
        return [this, results] { search_results = results; suggestions.clear(); };
    });
}

void Controller::inspect(const std::string& id) {
    model_task("Reading " + id + "…", [this, id](const auto&, const CancelToken& cancel) -> std::function<void()> {
        HubRepository found = hub_inspect(id, cancel);
        return [this, found] { repository = found; };
    });
}

void Controller::suggest() {
    model_task("Finding coding models that fit this computer…", [this](const auto& progress, const CancelToken& cancel) -> std::function<void()> {
        std::vector<HubSuggestion> picks = hub_suggestions(4, progress, cancel);
        return [this, picks] {
            suggestions = picks;
            if (picks.empty()) notice.emit("No suggested coding model fits this computer's memory. Try a search.");
        };
    });
}

void Controller::fetch(const HubBuild& build, const HubRepository& from) {
    if (download || !model_busy.empty()) return;
    const std::shared_ptr<bool> alive = alive_;
    model_task("Downloading " + build.file + "…", [this, build, from, alive](const auto&, const CancelToken& cancel) -> std::function<void()> {
        auto last = std::chrono::steady_clock::now() - std::chrono::seconds(1);
        const fs::path file = hub_download(build, from, [&](int64_t done, int64_t total) {
            const auto now = std::chrono::steady_clock::now();
            if (now - last < std::chrono::milliseconds(250) || !*alive) return;
            last = now;
            const DownloadState state{build.file, byte_text(done) + " of " + byte_text(total), total > 0 ? static_cast<double>(done) / static_cast<double>(total) : 0};
            post([this, state] { download = state; });
        }, cancel);
        const LocalModel model{file.stem().string(), file.string(), build.bytes, "Mason downloads"};
        return [this, model] {
            notice.emit("Downloaded and verified " + model.name + ".");
            if (model.startable() && !runtime_path.empty()) post([this, model] { start_model(model); });
            else post([this] { refresh_models(); });
        };
    });
}

}  // namespace mason::app
