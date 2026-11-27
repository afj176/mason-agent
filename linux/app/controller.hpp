// Everything the window shows, and the background work behind it.
// State is only read and changed on the GTK main thread. Work runs on other threads and comes back through `post`.
#pragma once
#include "chat.hpp"
#include "schedules.hpp"
#include <chrono>
#include <condition_variable>
#include <deque>
#include <map>
#include <mutex>
#include <thread>

#include <glibmm/dispatcher.h>
#include <sigc++/sigc++.h>

#include "acceptance.hpp"
#include "agent.hpp"
#include "transfer.hpp"
#include "planning.hpp"
#include "models.hpp"
#include "model_memory.hpp"

namespace mason::app {

struct DownloadState {
    std::string title, detail;
    double fraction = 0;
};

class Controller {
public:
    Controller();
    ~Controller();

    /// Emitted on the main thread whenever something the window shows has changed.
    sigc::signal<void()> changed;
    /// A short message for the user (a toast).
    sigc::signal<void(const std::string&)> notice;
    /// The model asks to show the user a file at a line ("file") or one of the project's pages ("page").
    sigc::signal<void(const std::string& kind, const std::string& path, int line)> present;

    // ---- sessions ----
    const std::vector<Session>& sessions() const { return sessions_; }
    const Session* session() const;
    std::vector<std::string> projects() const;
    std::string selection;
    void select(const std::string& id);
    void new_session(const std::string& folder = "");
    void remove_session(const std::string& id);
    void set_permission(Permission permission);

    // ---- running a request ----
    bool running = false;
    std::string live_text, live_status;
    std::vector<Activity> live_activities;
    std::optional<ApprovalRequest> approval;
    void send(const std::string& text);
    void stop();
    /// Hands a message to the running request: queued for after it completes, or steering it now.
    void follow_up(const std::string& text, bool steer);
    /// Follow-ups sent during the current request that have not started yet.
    int waiting_follow_ups = 0;
    /// Runs the follow-ups a stopped or failed request left waiting.
    void run_queued();
    /// Answers the pending approval. A non-empty prefix also saves an always-allow rule for the project.
    void answer(bool allow, const std::string& always_prefix = "");
    void undo(bool redo = false);
    /// When files were last put back (undo, redo, or a fork that restored them); the session shows the restored pose for a while.
    std::chrono::steady_clock::time_point restored_at{};
    /// Replaces the conversation so far with a summary from the model. The original messages stay in the session.
    void compact();
    /// "" lists the project's custom agents, a name uses one, "none" goes back to the default.
    void choose_agent(const std::string& name);
    /// Runs one of the commands the app shares with the command line (/experiment, /deliver, /task, /roles, /policy,
    /// /network) and shows what it says.
    void host_command(const std::string& name, const std::string& rest);
    /// Emitted when a command hands back text to put in the request box.
    sigc::signal<void(const std::string&)> draft_suggested;
    /// Tells the user which MCP servers are registered and what each may call.
    void show_mcp_servers();
    /// Mason's data folder, or an empty path when it could not be opened.
    fs::path home() const { return store_ ? store_->home() : fs::path(); }
    /// Asks a registered MCP server what tools it offers, for the Settings page. The answer lands in `mcp_offered`.
    void discover_mcp(const std::string& name);
    std::map<std::string, std::vector<McpTool>> mcp_offered;
    std::string mcp_busy;  ///< The server being asked, or empty.
    /// "/schedule ..." typed in the request box: list, add, switch or remove a schedule, or run one now.
    void schedule_command(const std::string& arguments);
    /// Runs every schedule that is due (or the one named), one after another, each as its own unattended session.
    void run_due_schedules(const std::string& only = "");
    /// A system notification for something that happened while the window was in the background.
    sigc::signal<void(const std::string& title, const std::string& body)> notification;

    // ---- comments and milestones ----
    /// Pins a note to a line of a diff. It is sent with the next request. Returns false when it could not be added.
    bool add_comment(const std::string& path, const DiffLine& row, const std::string& note);
    void remove_comment(const std::string& id);
    std::vector<MilestoneStatus> milestone_status() const;
    std::vector<std::string> workflow_names() const;
    bool put_milestone(const Milestone& item);
    void remove_milestone(const std::string& id);
    /// Runs the milestone's workflow, or records what the user saw.
    void check_milestone(const std::string& id, const std::string& observation);
    std::string milestone_busy;  ///< The milestone whose check is running, or empty.
    /// Has the model read what this session changed. What it finds is pinned as comments, to go with the next request.
    void review_changes();
    bool reviewing = false;

    // ---- fork, export and import ----
    /// Opens a new session holding the conversation before this request. The request is left in `forked_request`
    /// for the window to put back in the request box.
    void fork_session(const std::string& turn_id, bool restore_files);
    std::string forked_request;
    void export_session(const std::string& id, const std::string& path, bool sanitize);
    void import_session(const std::string& path, const std::string& workspace);

    // ---- an attached window ----
    /// The windows of other apps, or none when the accessibility service is not answering.
    std::vector<DesktopWindow> open_windows();
    /// Attaches a window to the session, or detaches with nothing.
    void attach_window(const std::optional<DesktopWindow>& window);

    // ---- chat ----
    const std::vector<ChatConversation>& chats() const { return chats_; }
    const ChatConversation* chat() const;
    std::string chat_selection;
    bool chat_running = false;
    std::string chat_live;
    /// Why the last answer did not arrive, until the next message is sent.
    std::string chat_problem;
    /// Files waiting to go with the next message.
    std::vector<ChatAttachment> chat_attachments;
    void select_chat(const std::string& id);
    void new_chat();
    /// Sets the selected chat's reasoning level ("default" for the server's own), output limit (0 for none) and pinned reference.
    void configure_chat(const std::string& reasoning, int max_tokens, const std::string& reference);
    void remove_chat(const std::string& id);
    void attach_to_chat(const std::string& path);
    void detach_from_chat(const std::string& name);
    void send_chat(const std::string& text);
    void stop_chat();

    // ---- models ----
    std::optional<ModelEndpoint> endpoint;
    std::string endpoint_name;
    std::vector<RunningServer> servers;
    std::vector<LocalModel> local_models;
    /// Each local model's memory needs from its metadata, read with the scan, by path.
    std::map<std::string, MemoryProfile> profiles;
    std::string runtime_path;
    /// The model being started and the step it is on ("inspect", "load", "check", "ready"), or empty.
    std::string starting_path, starting_stage;
    std::string model_busy;  ///< What the Models page is doing, or empty.
    std::optional<DownloadState> download;
    std::vector<HubModel> search_results;
    std::optional<HubRepository> repository;
    std::vector<HubSuggestion> suggestions;
    const ModelServer* server() const { return server_.get(); }

    void use(const ModelEndpoint& chosen, const std::string& name);
    void refresh_models(bool start_last_if_needed = false);
    /// Starts a model. A context of 0 uses the suggestion for the memory free now; `cpu_only` keeps it off the GPU.
    void start_model(const LocalModel& model, int context = 0, bool cpu_only = false);
    /// Moves a model's files to the Trash, then scans again. Not while it runs.
    void trash_model(const LocalModel& model);
    void stop_server();
    void cancel_model_work();
    void install_runtime();
    void search(const std::string& query);
    void inspect(const std::string& id);
    void suggest();
    void fetch(const HubBuild& build, const HubRepository& from);

private:
    /// Runs `work` on the main thread soon. Safe to call from any thread.
    void post(std::function<void()> work);
    void drain();
    void replace(const Session& session);
    /// Saves a changed session and shows it.
    void keep(Session& session);
    /// Starts model-page work on a thread. `work` gets a progress callback and a cancel token; `done` runs on the main thread.
    void model_task(const std::string& label, std::function<std::function<void()>(const std::function<void(const std::string&)>&, const CancelToken&)> work);

    std::unique_ptr<SessionStore> store_;
    std::unique_ptr<ChatStore> chat_store_;
    std::vector<ChatConversation> chats_;
    std::thread chat_thread_, schedule_thread_, mcp_thread_, plan_thread_;
    CancelToken plan_cancel_;
    bool schedules_running_ = false;
    CancelToken schedule_cancel_;
    sigc::connection schedule_timer_;
    /// The engine working on the current request, set and cleared by the run thread under the lock.
    std::mutex agent_lock_;
    Agent* active_agent_ = nullptr;
    CancelToken chat_cancel_;
    std::vector<Session> sessions_;
    std::shared_ptr<ModelServer> server_;  // shared only so a finished start can be handed from its thread to the main thread

    Glib::Dispatcher dispatcher_;
    std::mutex queue_lock_;
    std::deque<std::function<void()>> queue_;

    std::thread run_thread_, model_thread_;
    CancelToken run_cancel_, model_cancel_;
    std::mutex approval_lock_;
    std::condition_variable approval_answered_;
    std::optional<bool> approval_answer_;
    std::shared_ptr<bool> alive_ = std::make_shared<bool>(true);
};

}  // namespace mason::app
