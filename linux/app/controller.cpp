#include "generation.hpp"
#include "notes.hpp"
#include "language.hpp"
#include "controller.hpp"
#include "roles.hpp"
#include "background.hpp"
#include "bundles.hpp"
#include "delivery.hpp"
#include "derived.hpp"
#include "experiments.hpp"
#include "network.hpp"
#include "policy.hpp"

#include <gio/gio.h>
#include <set>

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
    try {
        chat_store_ = std::make_unique<ChatStore>();
        chats_ = chat_store_->conversations();
        if (!chats_.empty()) chat_selection = chats_.front().id;
    } catch (const std::exception&) {
        // Chat stays empty when its folder cannot be opened; the coding sessions are unaffected.
    }
    runtime_path = llama::find();
    refresh_models(true);
    // Scheduled requests are checked shortly after launch and then every minute while the app is open.
    Glib::signal_timeout().connect_seconds_once([this] { run_due_schedules(); }, 8);
    schedule_timer_ = Glib::signal_timeout().connect_seconds([this] { run_due_schedules(); return true; }, 60);
}

Controller::~Controller() {
    *alive_ = false;
    if (run_cancel_) run_cancel_->store(true);
    if (model_cancel_) model_cancel_->store(true);
    if (chat_cancel_) chat_cancel_->store(true);
    if (schedule_cancel_) schedule_cancel_->store(true);
    schedule_timer_.disconnect();
    {
        // A request waiting for an approval must not wait forever.
        std::lock_guard<std::mutex> guard(approval_lock_);
        approval_answer_ = false;
        chat_answer_ = ChatAnswer::No;
    }
    approval_answered_.notify_all();
    if (plan_cancel_) plan_cancel_->store(true);
    if (run_thread_.joinable()) run_thread_.join();
    if (plan_thread_.joinable()) plan_thread_.join();
    if (model_thread_.joinable()) model_thread_.join();
    if (chat_thread_.joinable()) chat_thread_.join();
    if (schedule_thread_.joinable()) schedule_thread_.join();
    if (mcp_thread_.joinable()) mcp_thread_.join();
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

void Controller::fork_session(const std::string& turn_id, bool restore_files) {
    const Session* current = session();
    if (!current || !store_ || running) return;
    try {
        Session source = *current;
        size_t index = source.turns.size();
        for (size_t i = 0; i < source.turns.size(); i++) {
            if (source.turns[i].id == turn_id) index = i;
        }
        const SessionFork made = transfer::fork(source, *store_, index, restore_files);
        replace(source);
        replace(made.session);
        selection = made.session.id;
        forked_request = made.request;
        if (restore_files) restored_at = std::chrono::steady_clock::now();
        notice.emit(made.session.detail);
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
    changed.emit();
}

void Controller::export_session(const std::string& id, const std::string& path, bool sanitize) {
    const auto found = std::find_if(sessions_.begin(), sessions_.end(), [&](const Session& item) { return item.id == id; });
    if (found == sessions_.end()) return;
    try {
        write_file_atomic(path, transfer::export_session(*found, sanitize));
        notice.emit(std::string("Saved ") + (sanitize ? "a sanitized copy of " : "") + "the session to " + path);
    } catch (const std::exception& error) {
        notice.emit(std::string("The session could not be saved: ") + error.what());
    }
}

void Controller::import_session(const std::string& path, const std::string& workspace) {
    if (!store_) return;
    try {
        const Session imported = transfer::import_session(read_file(path), *store_, workspace);
        replace(imported);
        selection = imported.id;
        notice.emit(imported.detail);
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
    changed.emit();
}

std::vector<DesktopWindow> Controller::open_windows() {
    const std::shared_ptr<DesktopAutomation> automation = desktop::system();
    try {
        if (!automation) throw Error("This build cannot operate windows on this computer.");
        if (const std::string problem = automation->unavailable(); !problem.empty()) throw Error(problem);
        return automation->windows();
    } catch (const std::exception& error) {
        notice.emit(error.what());
        return {};
    }
}

void Controller::attach_window(const std::optional<DesktopWindow>& window) {
    const Session* current = session();
    if (!current || !store_ || running) return;
    Session updated = *current;
    updated.desktop = window;
    keep(updated);
    notice.emit(window ? "Attached " + desktop::label(*window) + ". Mason asks before pressing or changing anything in it." : "No window is attached.");
}

void Controller::keep(Session& session) {
    try {
        store_->save(session);
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
    replace(session);
    changed.emit();
}

bool Controller::add_comment(const std::string& path, const DiffLine& row, const std::string& note) {
    const Session* current = session();
    if (!current || !store_) return false;
    try {
        if (current->comments.size() >= review::kLimit) throw Error("At most " + std::to_string(review::kLimit) + " comments can wait at once.");
        Session updated = *current;
        updated.comments.push_back(review::comment(Workspace(updated.workspace), path, row.number, note, row.kind == '-' ? std::optional<std::string>(row.text) : std::nullopt));
        keep(updated);
        return true;
    } catch (const std::exception& error) {
        notice.emit(error.what());
        return false;
    }
}

void Controller::remove_comment(const std::string& id) {
    const Session* current = session();
    if (!current || !store_) return;
    Session updated = *current;
    updated.comments.erase(std::remove_if(updated.comments.begin(), updated.comments.end(), [&](const ReviewComment& item) { return item.id == id; }), updated.comments.end());
    keep(updated);
}

bool Controller::answer_suggestion(const std::string& choice) {
    const Session* current = session();
    if (!current || !store_ || running || current->suggested_note.empty()) return false;
    try {
        const Workspace workspace(current->workspace);
        if (choice == "remember") project_notes::add(current->suggested_note, "", workspace, store_->home());
        else if (choice == "dismiss") project_notes::dismiss(current->suggested_note, workspace.root(), store_->home());
    } catch (const std::exception& error) {
        notice.emit(error.what());
        return false;
    }
    Session updated = *current;
    updated.suggested_note.clear();
    keep(updated);
    return true;
}

void Controller::review_changes() {
    const Session* current = session();
    if (!current || !store_ || running || reviewing || !milestone_busy.empty()) return;
    if (!endpoint) {
        notice.emit("No model is running. Open Models to start one.");
        return;
    }
    if (plan_thread_.joinable()) plan_thread_.join();
    reviewing = true;
    plan_cancel_ = make_cancel();
    changed.emit();
    const std::shared_ptr<bool> alive = alive_;
    plan_thread_ = std::thread([this, alive, working = *current, target = *endpoint, cancel = plan_cancel_, home = store_->home()] {
        ReviewFindings found;
        std::string problem;
        try {
            OpenAIClient model(target);
            const Workspace folder(working.workspace);
            const std::unique_ptr<RoleModel> reviewer = roles::reviewer(model, &folder, home);
            found = review::findings(working, folder, *reviewer, cancel);
        } catch (const Cancelled&) {
            problem = "The review was stopped.";
        } catch (const std::exception& error) {
            problem = error.what();
        }
        if (!*alive) return;
        post([this, found, problem, id = working.id] {
            reviewing = false;
            const auto latest = std::find_if(sessions_.begin(), sessions_.end(), [&](const Session& each) { return each.id == id; });
            if (problem.empty() && latest != sessions_.end() && !found.comments.empty()) {
                Session updated = *latest;
                for (const ReviewComment& item : found.comments) {
                    if (updated.comments.size() < review::kLimit) updated.comments.push_back(item);
                }
                keep(updated);
            }
            notice.emit(!problem.empty() ? problem : found.comments.empty() ? review::summary(found)
                        : "The review pinned " + std::to_string(found.comments.size()) + (found.comments.size() == 1 ? " comment" : " comments") + " above the request box. A model wrote them: check each before sending.");
            changed.emit();
        });
    });
}

std::vector<MilestoneStatus> Controller::milestone_status() const {
    const Session* current = session();
    if (!current || current->milestones.empty()) return {};
    try {
        return milestones::inspect(Workspace(current->workspace), current->milestones);
    } catch (const std::exception&) {
        return {};
    }
}

std::vector<std::string> Controller::workflow_names() const {
    std::vector<std::string> names;
    const Session* current = session();
    if (!current) return names;
    try {
        for (const auto& [name, workflow] : workflows::load(Workspace(current->workspace))) names.push_back(name);
    } catch (const std::exception&) {
    }
    return names;
}

bool Controller::put_milestone(const Milestone& item) {
    const Session* current = session();
    if (!current || !store_) return false;
    try {
        Session updated = *current;
        updated.milestones = milestones::put(Workspace(updated.workspace), updated.milestones, item);
        keep(updated);
        return true;
    } catch (const std::exception& error) {
        notice.emit(error.what());
        return false;
    }
}

void Controller::remove_milestone(const std::string& id) {
    const Session* current = session();
    if (!current || !store_) return;
    Session updated = *current;
    updated.milestones = milestones::remove(id, updated.milestones);
    keep(updated);
}

void Controller::check_milestone(const std::string& id, const std::string& observation) {
    const Session* current = session();
    if (!current || !store_ || !milestone_busy.empty()) return;
    const auto found = std::find_if(current->milestones.begin(), current->milestones.end(), [&](const Milestone& item) { return item.id == id; });
    if (found == current->milestones.end()) return;
    const bool automatic = current->permission == Permission::Auto;
    const bool allowed = automatic || ApprovalRules(store_->home(), current->workspace).allows("run_workflow", "workflow " + found->workflow);
    if (plan_thread_.joinable()) plan_thread_.join();
    milestone_busy = id;
    plan_cancel_ = make_cancel();
    changed.emit();
    const std::shared_ptr<bool> alive = alive_;
    plan_thread_ = std::thread([this, alive, item = *found, plan = current->milestones, folder = current->workspace, session_id = current->id, allowed, automatic, observation, cancel = plan_cancel_] {
        std::optional<MilestoneReceipt> receipt;
        std::string problem;
        try {
            receipt = milestones::check(Workspace(folder), item, plan, allowed, automatic, observation, cancel);
        } catch (const Cancelled&) {
        } catch (const std::exception& error) {
            problem = error.what();
        }
        if (!*alive) return;
        post([this, receipt, problem, session_id, id = item.id] {
            milestone_busy.clear();
            // The session may have moved on while the workflow ran; only the receipt is added.
            const auto latest = std::find_if(sessions_.begin(), sessions_.end(), [&](const Session& each) { return each.id == session_id; });
            if (receipt && latest != sessions_.end()) {
                Session updated = *latest;
                for (Milestone& each : updated.milestones) {
                    if (each.id == id) each.receipt = receipt;
                }
                keep(updated);
            }
            if (!problem.empty()) notice.emit(problem);
            changed.emit();
        });
    });
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

void Controller::host_command(const std::string& name, const std::string& rest) {
    const Session* current = session();
    if (!current || !store_) return;
    const std::string folder = current->workspace;
    const fs::path home = store_->home();
    const std::optional<ModelEndpoint> target = endpoint;
    const bool automatic = current->permission == Permission::Auto && !current->ask_only;
    const std::shared_ptr<bool> alive = alive_;
    // Some of these run git, so they work off the main thread and report back.
    if (name == "/network") {
        // The choice is this session's, so it is made here, where the session is kept.
        Session updated = *current;
        std::string said;
        try {
            said = network_settings::session_command(rest, updated, home);
            store_->save(updated);
        } catch (const std::exception& error) {
            said = error.what();
        }
        replace(updated);
        notice.emit(said);
        changed.emit();
        return;
    }
    const std::string receipt = derived::passed_receipt(*current);
    std::thread([this, alive, name, rest, folder, home, target, automatic, receipt] {
        std::string said, request;
        try {
            const Workspace workspace(folder);
            if (name == "/experiment" || name == "/experiments") said = experiments::command(rest, workspace, home);
            else if (name == "/deliver") {
                const delivery::Said answer = delivery::command(rest, workspace, home, receipt);
                said = answer.text;
                request = answer.request;
            } else if (name == "/task" || name == "/tasks") {
                // The mason command line tool beside this program is what runs a background task.
                const fs::path program = fs::path(background::own_program()).parent_path() / "mason";
                said = background::command(rest, workspace, target, automatic, program.string(), home);
            } else if (name == "/roles" || name == "/role") said = roles::command(rest, home);
            else if (name == "/policy") said = policies::command(rest, workspace, home);
            else if (name == "/bundle" || name == "/bundles") said = bundles::command(rest, home);
            else if (name == "/notes" || name == "/note") said = project_notes::command(project_notes::arguments(name, rest), workspace, home);
            else if (name == "/generation") said = generation::command(rest, target, home);
            else if (name == "/languages" || name == "/language") said = language_servers::command(rest, &workspace, home);
            else said = network_settings::command(rest, home);
        } catch (const std::exception& error) {
            said = error.what();
        }
        post([this, alive, said, request] {
            if (!*alive) return;
            notice.emit(said);
            // Feedback to act on is put in the request box for the user to send.
            if (!request.empty()) draft_suggested.emit(request);
            // What the command changed shows on the Settings page at once.
            changed.emit();
        });
    }).detach();
}

void Controller::send(const std::string& text) {
    const std::string input = trim(text);
    const Session* current = session();
    // Comments pinned to lines are a request of their own, with or without typed text.
    if (running || !current || !store_ || (input.empty() && current->comments.empty())) return;
    // Two commands act on the session itself. Any other /name is a custom command, which the engine expands.
    const std::string lowered = to_lower(input);
    if (lowered == "/compact") { compact(); return; }
    if (lowered == "/compact auto" || starts_with(lowered, "/compact auto ")) {
        try {
            const auto [on, said] = compaction::auto_text(lowered.substr(13), current->automatic_compaction);
            if (on != current->automatic_compaction) set_automatic_compaction(current->id, on);
            else notice.emit(said);
        } catch (const std::exception& error) {
            notice.emit(error.what());
        }
        return;
    }
    if (lowered == "/mcp") { show_mcp_servers(); return; }
    if (lowered == "/schedule" || lowered == "/schedules" || starts_with(lowered, "/schedule ")) {
        const size_t space = input.find(' ');
        schedule_command(space == std::string::npos ? "" : trim(input.substr(space)));
        return;
    }
    {
        // Commands that act on the project or on Mason itself, shared with the command line.
        const size_t space = input.find(' ');
        const std::string word = lowered.substr(0, space);
        static const std::set<std::string> hosted = {"/experiment", "/experiments", "/deliver", "/task", "/tasks", "/roles", "/role", "/policy", "/network", "/bundle", "/bundles", "/languages", "/language", "/generation", "/notes", "/note"};
        if (hosted.count(word)) { host_command(word, space == std::string::npos ? "" : trim(input.substr(space))); return; }
    }
    if (lowered == "/agent" || starts_with(lowered, "/agent ")) { choose_agent(trim(input.substr(6))); return; }
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
        turn.request = review::request(input, shown.comments);
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
                    post([this, request] { approval = request; notification.emit("Mason needs your approval", request.title); });
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
                            case AgentEvent::Kind::FollowUp: {
                                // The previous request is finished: show it as it ended, and the follow-up as the request now running.
                                Session shown = *event.session;
                                Turn turn;
                                turn.request = event.text;
                                turn.first_activity = shown.activities.size();
                                shown.turns.push_back(std::move(turn));
                                shown.state = "Running";
                                replace(shown);
                                live_text.clear();
                                live_activities.clear();
                                waiting_follow_ups = std::max(0, waiting_follow_ups - 1);
                                break;
                            }
                        }
                    });
                });
            agent.set_presenter([this](const std::string& kind, const std::string& path, int line) { post([this, kind, path, line] { present.emit(kind, path, line); }); });
            { std::lock_guard<std::mutex> guard(agent_lock_); active_agent_ = &agent; }
            agent.run(working, input, cancel);
            { std::lock_guard<std::mutex> guard(agent_lock_); active_agent_ = nullptr; }
        } catch (const std::exception& error) {
            { std::lock_guard<std::mutex> guard(agent_lock_); active_agent_ = nullptr; }
            working.state = "Failed";
            working.detail = error.what();
            working.repeat_stop.reset();
            working.ending = RunEnding{dynamic_cast<const ModelUnavailable*>(&error) ? "model_unavailable" : "failed", 0};
        }
        if (!*alive) return;
        post([this, working] {
            approval.reset();
            replace(working);
            const std::optional<EndingStyle> look = working.ending ? ending_style(working.ending->code) : std::nullopt;
            notification.emit(working.title, (look ? look->headline : working.state) + (working.detail.empty() ? "" : ": " + working.detail));
            running = false;
            waiting_follow_ups = 0;
            live_text.clear();
            live_status.clear();
            live_activities.clear();
        });
    });
    changed.emit();
}

void Controller::follow_up(const std::string& text, bool steer) {
    if (!running || trim(text).empty() || approval) return;
    try {
        std::lock_guard<std::mutex> guard(agent_lock_);
        if (!active_agent_) throw Error("This request cannot take a follow-up. Wait for it to finish, or stop it.");
        active_agent_->send_while_running(text, steer);
        waiting_follow_ups++;
    } catch (const std::exception& error) {
        notice.emit(error.what());
        return;
    }
    changed.emit();
}

void Controller::run_queued() {
    const Session* current = session();
    if (running || !current || current->pending_input.empty()) return;
    send("/queue");
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

// ---- chat ------------------------------------------------------------------------------------------------------

const ChatConversation* Controller::chat() const {
    for (const ChatConversation& item : chats_) {
        if (item.id == chat_selection) return &item;
    }
    return nullptr;
}

void Controller::select_chat(const std::string& id) {
    if (chat_running || id == chat_selection) return;
    chat_selection = id;
    changed.emit();
}

void Controller::reload_chats() {
    if (!chat_store_ || chat_running) return;
    chat_store_->reload();
    std::vector<ChatConversation> fresh = chat_store_->conversations();
    // Only a change on disk is shown; the selected chat follows its newer version, or the newest when it is gone.
    auto same = [](const std::vector<ChatConversation>& a, const std::vector<ChatConversation>& b) {
        if (a.size() != b.size()) return false;
        for (size_t i = 0; i < a.size(); i++) {
            if (a[i].id != b[i].id || a[i].updated != b[i].updated || a[i].messages.size() != b[i].messages.size()) return false;
        }
        return true;
    };
    if (same(fresh, chats_)) return;
    chats_ = std::move(fresh);
    const bool kept = std::any_of(chats_.begin(), chats_.end(), [&](const ChatConversation& item) { return item.id == chat_selection; });
    if (!kept) chat_selection = chats_.empty() ? "" : chats_.front().id;
    changed.emit();
}

void Controller::configure_chat(const std::string& reasoning, int max_tokens, const std::string& reference) {
    const ChatConversation* current = chat();
    if (!current || !chat_store_ || chat_running) return;
    ChatConversation updated = *current;
    try {
        chat::configure(updated, reasoning, max_tokens, reference);
        chat_store_->save(updated);
        chats_ = chat_store_->conversations();
        chat_selection = updated.id;
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
    changed.emit();
}

void Controller::new_chat() {
    if (!chat_store_ || chat_running) return;
    // Reuse an untouched chat rather than collecting empty ones.
    for (const ChatConversation& item : chats_) {
        if (item.messages.size() <= 1) { chat_selection = item.id; changed.emit(); return; }
    }
    try {
        const ChatConversation created = chat_store_->create();
        chats_ = chat_store_->conversations();
        chat_selection = created.id;
        changed.emit();
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
}

void Controller::remove_chat(const std::string& id) {
    if (!chat_store_) return;
    if (chat_running && id == chat_selection) { notice.emit("Stop the answer before deleting this chat."); return; }
    chat_store_->remove(id);
    chats_ = chat_store_->conversations();
    if (chat_selection == id) chat_selection = chats_.empty() ? "" : chats_.front().id;
    changed.emit();
}

void Controller::attach_to_chat(const std::string& path) {
    if (chat_attachments.size() >= ChatAttachment::kMaxPerMessage) { notice.emit("Attach at most 5 files to one message."); return; }
    try {
        ChatAttachment file = ChatAttachment::from_file(path);
        for (const ChatAttachment& existing : chat_attachments) {
            if (existing.name == file.name) return;
        }
        chat_attachments.push_back(std::move(file));
        changed.emit();
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
}

void Controller::detach_from_chat(const std::string& name) {
    chat_attachments.erase(std::remove_if(chat_attachments.begin(), chat_attachments.end(), [&](const ChatAttachment& file) { return file.name == name; }), chat_attachments.end());
    changed.emit();
}

void Controller::send_chat(const std::string& text) {
    const ChatConversation* current = chat();
    if (!current || chat_running || !chat_store_) return;
    if (trim(text).empty() && chat_attachments.empty()) return;
    if (!endpoint) {
        notice.emit("No model is running. Open Models to start one, or start your own local server.");
        return;
    }
    if (chat_thread_.joinable()) chat_thread_.join();
    chat_running = true;
    chat_live.clear();
    chat_problem.clear();
    chat_steps.clear();
    chat_approval.reset();
    chat_sending = trim(text);
    for (const ChatAttachment& file : chat_attachments) chat_sending += (chat_sending.empty() ? "" : "\n") + std::string("📎 ") + file.name;
    chat_cancel_ = make_cancel();
    {
        std::lock_guard<std::mutex> guard(approval_lock_);
        chat_answer_.reset();
    }
    const std::vector<ChatAttachment> files = chat_attachments;
    chat_attachments.clear();
    const ModelEndpoint target = *endpoint;
    const CancelToken cancel = chat_cancel_;
    const fs::path home = store_ ? store_->home() : data_home();
    changed.emit();
    chat_thread_ = std::thread([this, working = *current, text, files, target, cancel, home]() mutable {
        std::string problem;
        try {
            OpenAIClient model(target);
            ChatTools tools;
            tools.home = home;
            tools.calls_tools = chat::calls_tools(model);
            // A question waits for the user's answer on the card in the conversation; stopping the chat answers no.
            tools.approve = [this, cancel](const ApprovalRequest& request) {
                post([this, request] { chat_approval = request; changed.emit(); notification.emit("Mason needs your approval", request.title); });
                std::unique_lock<std::mutex> guard(approval_lock_);
                approval_answered_.wait(guard, [&] { return chat_answer_.has_value() || is_cancelled(cancel); });
                const ChatAnswer answer = chat_answer_.value_or(ChatAnswer::No);
                chat_answer_.reset();
                return answer;
            };
            tools.step = [this](const Activity& step) {
                post([this, step] {
                    const auto found = std::find_if(chat_steps.begin(), chat_steps.end(), [&](const Activity& item) { return item.id == step.id; });
                    if (found != chat_steps.end()) *found = step;
                    else chat_steps.push_back(step);
                    chat_live.clear();
                    changed.emit();
                });
            };
            chat::send(working, text, files, model, *chat_store_, [this](const std::string& piece) {
                post([this, piece] { chat_live += piece; changed.emit(); });
            }, cancel, &tools);
        } catch (const Cancelled&) {
        } catch (const std::exception& error) {
            problem = error.what();
        }
        post([this, problem] {
            // The store has the conversation as far as it got: with the answer, or with the question alone.
            chats_ = chat_store_->conversations();
            chat_running = false;
            chat_live.clear();
            chat_steps.clear();
            chat_approval.reset();
            chat_sending.clear();
            chat_problem = problem;
            changed.emit();
            if (!problem.empty()) notice.emit(problem);
        });
    });
}

void Controller::stop_chat() {
    if (chat_cancel_) chat_cancel_->store(true);
    if (chat_approval) answer_chat(ChatAnswer::No);
}

void Controller::answer_chat(ChatAnswer answer) {
    if (!chat_approval) return;
    chat_approval.reset();
    {
        std::lock_guard<std::mutex> guard(approval_lock_);
        chat_answer_ = answer;
    }
    approval_answered_.notify_all();
    changed.emit();
}

bool Controller::chat_calls_tools() const {
    if (!endpoint) return true;
    return chat::calls_tools(OpenAIClient(*endpoint));
}

void Controller::set_chat_tool(const std::string& group, bool on) {
    const ChatConversation* current = chat();
    if (!current || !chat_store_ || chat_running) return;
    ChatConversation updated = *current;
    try {
        chat::set_tool(updated, group, on);
        chat_store_->save(updated);
        chats_ = chat_store_->conversations();
        chat_selection = updated.id;
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
    changed.emit();
}

void Controller::compact() {
    const Session* current = session();
    if (!current || running || !store_) return;
    if (current->messages.size() <= 2) { notice.emit("There is nothing to compact yet."); return; }
    if (!endpoint) { notice.emit("Compaction needs a running model. Open Models to start one."); return; }
    if (run_thread_.joinable()) run_thread_.join();
    running = true;
    live_text.clear();
    live_status = "Compacting the conversation";
    live_activities.clear();
    changed.emit();
    run_cancel_ = make_cancel();
    const ModelEndpoint target = *endpoint;
    const CancelToken cancel = run_cancel_;
    const std::shared_ptr<bool> alive = alive_;
    run_thread_ = std::thread([this, working = *current, target, cancel, alive]() mutable {
        std::string message;
        bool done = false;
        try {
            OpenAIClient model(target);
            done = compaction::compact(working, model, 0, cancel);
            if (done) store_->save(working);
            message = done ? "The conversation was replaced by a summary, which goes with your next request. The original messages are kept." : "There is nothing to compact yet.";
        } catch (const Cancelled&) {
            message = "Compaction was stopped. The conversation is unchanged.";
        } catch (const std::exception& error) {
            message = error.what();
        }
        post([this, working, done, message] {
            if (done) replace(working);
            running = false;
            live_status.clear();
            changed.emit();
            notice.emit(message);
        });
    });
}

void Controller::set_automatic_compaction(const std::string& id, bool on) {
    if (running || !store_) return;
    const auto found = std::find_if(sessions_.begin(), sessions_.end(), [&](const Session& item) { return item.id == id; });
    if (found == sessions_.end()) return;
    Session updated = *found;
    updated.automatic_compaction = on;
    try {
        store_->save(updated);
        replace(updated);
        changed.emit();
        notice.emit(compaction::auto_text(on ? "on" : "off", on).second);
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
}

void Controller::set_chat_automatic_compaction(bool on) {
    const ChatConversation* current = chat();
    if (!current || !chat_store_ || chat_running || current->automatic_compaction == on) return;
    ChatConversation updated = *current;
    updated.automatic_compaction = on;
    try {
        chat_store_->save(updated);
        chats_ = chat_store_->conversations();
        chat_selection = updated.id;
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
    changed.emit();
}

void Controller::choose_agent(const std::string& name) {
    const Session* current = session();
    if (!current || running || !store_) return;
    const std::vector<Definition> agents = customization::agents(store_->home(), current->workspace);
    if (name.empty()) {
        if (agents.empty()) { notice.emit("No custom agents. Add a Markdown file to .mason/agents (or .claude/agents) in this project."); return; }
        std::vector<std::string> names;
        for (const Definition& agent : agents) names.push_back(agent.name);
        notice.emit("Agents: " + join(names, ", ") + ". Type /agent NAME to use one, or /agent none for the default." + (current->agent.empty() ? "" : " Current: " + current->agent + "."));
        return;
    }
    Session updated = *current;
    const std::string wanted = to_lower(name);
    if (wanted == "none" || wanted == "default" || wanted == "off") {
        updated.agent.clear();
    } else {
        const auto found = std::find_if(agents.begin(), agents.end(), [&](const Definition& agent) { return to_lower(agent.name) == wanted; });
        if (found == agents.end()) { notice.emit("No agent named " + name + "."); return; }
        updated.agent = found->name;
    }
    try {
        store_->save(updated);
        replace(updated);
        notice.emit(updated.agent.empty() ? "Default agent." : "Agent: " + updated.agent + ".");
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
}

void Controller::schedule_command(const std::string& arguments) {
    if (!store_) return;
    if (starts_with(to_lower(arguments), "run ")) {
        const std::string name = trim(arguments.substr(4));
        bool known = false;
        for (const Schedule& item : schedules::load(store_->home())) known = known || to_lower(item.name) == to_lower(name);
        if (!known) { notice.emit("No schedule named " + name + "."); return; }
        notice.emit("Running " + name + " now, unattended. Its session appears in the sidebar when it finishes.");
        run_due_schedules(name);
        return;
    }
    try {
        const Session* current = session();
        notice.emit(schedules::command(arguments, store_->home(), current ? current->workspace : home_dir(), endpoint));
    } catch (const std::exception& error) {
        notice.emit(error.what());
    }
}

void Controller::run_due_schedules(const std::string& only) {
    if (!store_ || schedules_running_) return;
    std::vector<Schedule> chosen;
    const std::vector<Schedule> all = schedules::load(store_->home());
    if (only.empty()) {
        chosen = schedules::due(all);
    } else {
        for (const Schedule& item : all) {
            if (to_lower(item.name) == to_lower(only)) chosen.push_back(item);
        }
    }
    if (chosen.empty()) return;
    if (schedule_thread_.joinable()) schedule_thread_.join();
    schedules_running_ = true;
    schedule_cancel_ = make_cancel();
    const CancelToken cancel = schedule_cancel_;
    const std::shared_ptr<bool> alive = alive_;
    schedule_thread_ = std::thread([this, chosen, cancel, alive] {
        for (const Schedule& schedule : chosen) {
            if (is_cancelled(cancel)) break;
            schedules::run(schedule, store_->home(), *store_, nullptr, cancel);
            std::string outcome = "finished";
            for (const Schedule& item : schedules::load(store_->home())) {
                if (item.id == schedule.id) outcome = item.last_outcome;
            }
            if (!*alive) return;
            const std::string name = schedule.name;
            post([this, name, outcome] {
                sessions_ = store_->sessions();
                changed.emit();
                notification.emit("Scheduled: " + name, outcome);
            });
        }
        if (!*alive) return;
        post([this] { schedules_running_ = false; });
    });
}

void Controller::discover_mcp(const std::string& name) {
    if (!store_ || !mcp_busy.empty()) return;
    std::optional<McpServer> found;
    try {
        for (const McpServer& server : mcp::all(store_->home())) {
            if (server.name == name) found = server;
        }
    } catch (const std::exception& error) {
        notice.emit(error.what());
        return;
    }
    if (!found) return;
    if (mcp_thread_.joinable()) mcp_thread_.join();
    mcp_busy = name;
    changed.emit();
    const std::shared_ptr<bool> alive = alive_;
    mcp_thread_ = std::thread([this, server = *found, alive] {
        std::vector<McpTool> offered;
        std::string problem;
        try {
            offered = mcp::discover(server);
        } catch (const std::exception& error) {
            problem = error.what();
        }
        if (!*alive) return;
        post([this, name = server.name, offered, problem] {
            mcp_busy.clear();
            if (problem.empty()) mcp_offered[name] = offered;
            else notice.emit(name + " did not answer: " + problem);
            changed.emit();
        });
    });
}

void Controller::show_mcp_servers() {
    if (!store_) return;
    // Servers are registered for every project in Mason's data folder; the mason command manages them.
    const std::vector<McpServer> servers = mcp::enabled(store_->home());
    if (servers.empty()) {
        notice.emit("No MCP servers are registered. In a terminal, run mason and type /mcp add NAME http://127.0.0.1:PORT/mcp (or the full path of a program).");
        return;
    }
    std::string text;
    for (const McpServer& server : servers) text += server.name + ": " + (server.tools.empty() ? std::string("no tools allowed yet") : join(server.tools, ", ")) + "\n";
    notice.emit(text + "\nEvery call asks first. Manage servers with /mcp in the mason command.");
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
        restored_at = std::chrono::steady_clock::now();
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
        // Metadata only: headers are read, never the weights.
        std::map<std::string, MemoryProfile> read;
        for (const LocalModel& model : files) read[model.path] = read_profile(model);
        std::string runtime = llama::find();
        // The endpoint used last may be on a port the scan does not cover (a server the user started themselves).
        const Preferences remembered = Preferences::load();
        if (remembered.last_endpoint && std::none_of(found.begin(), found.end(), [&](const RunningServer& s) { return s.endpoint == *remembered.last_endpoint; })) {
            const auto listed = OpenAIClient::models(remembered.last_endpoint->url);
            if (listed && std::find(listed->begin(), listed->end(), remembered.last_endpoint->model) != listed->end()) found.insert(found.begin(), {"Local server", *remembered.last_endpoint});
        }
        return [this, found, files, read, runtime, start_last_if_needed] {
            servers = found;
            local_models = files;
            profiles = read;
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
                        const bool cpu = preferences.last_cpu;
                        post([this, last, context, cpu] { start_model(last, context, cpu); });
                        break;
                    }
                }
            }
        };
    });
}

void Controller::start_model(const LocalModel& model, int context, bool cpu_only) {
    if (!model_busy.empty() || download) return;
    // Transformers weights run through Python, not llama.cpp.
    if (runtime_path.empty() && !model.transformers) {
        notice.emit("Install llama.cpp first; it runs GGUF models.");
        return;
    }
    const std::string runtime = runtime_path;
    // The previous model is stopped first so two models never hold memory at once.
    if (server_) {
        if (endpoint && *endpoint == server_->endpoint) endpoint.reset();
        server_.reset();
    }
    starting_path = model.path;
    starting_stage = kStartStages[0];
    const std::shared_ptr<bool> alive = alive_;
    model_task("Starting " + model.name + "…", [this, model, context, cpu_only, runtime, alive](const auto& progress, const CancelToken& cancel) -> std::function<void()> {
        std::shared_ptr<ModelServer> started;
        try {
            started = ModelServer::start(model, runtime, context, cpu_only, [&](const std::string& line) {
                if (!line.empty()) progress("Starting " + model.name + "… " + utf8_suffix(line, 90));
            }, cancel, [this, alive](int stage) {
                if (*alive) post([this, stage] { starting_stage = kStartStages[std::clamp(stage, 0, 3)]; changed.emit(); });
            });
        } catch (...) {
            if (*alive) post([this] { starting_path.clear(); starting_stage.clear(); });
            throw;
        }
        return [this, started, model] {
            starting_path.clear();
            starting_stage.clear();
            // Replacing the previous server stops it.
            server_ = started;
            endpoint = started->endpoint;
            endpoint_name = model.name;
            Preferences preferences = Preferences::load();
            preferences.last_endpoint = started->endpoint;
            preferences.last_model_path = model.path;
            preferences.last_context = started->context;
            preferences.last_cpu = started->cpu_only;
            preferences.save();
            post([this] { refresh_models(); });
        };
    });
}

void Controller::trash_model(const LocalModel& model) {
    if (server_ && server_->model.path == model.path) {
        notice.emit("Stop " + model.name + " before moving it to the Trash.");
        return;
    }
    // Every part of a split model goes, and in the Hugging Face cache the stored file behind each link too.
    std::vector<fs::path> paths = model.transformers ? std::vector<fs::path>{model.path} : gguf_parts(model.path);
    std::vector<fs::path> targets;
    for (const fs::path& path : paths) {
        std::error_code failure;
        if (fs::is_symlink(path, failure)) {
            const fs::path target = fs::canonical(path, failure);
            if (!failure) targets.push_back(target);
        }
    }
    paths.insert(paths.end(), targets.begin(), targets.end());
    std::string problem;
    for (const fs::path& path : paths) {
        GFile* file = g_file_new_for_path(path.c_str());
        GError* error = nullptr;
        if (!g_file_trash(file, nullptr, &error)) {
            if (problem.empty()) problem = error ? error->message : "The file could not be moved to the Trash.";
            if (error) g_error_free(error);
        }
        g_object_unref(file);
    }
    notice.emit(problem.empty() ? "Moved " + model.name + " to the Trash." : model.name + " was not fully moved to the Trash: " + problem);
    refresh_models();
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
