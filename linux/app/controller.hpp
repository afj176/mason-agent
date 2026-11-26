// Everything the window shows, and the background work behind it.
// State is only read and changed on the GTK main thread. Work runs on other threads and comes back through `post`.
#pragma once
#include "chat.hpp"
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>

#include <glibmm/dispatcher.h>
#include <sigc++/sigc++.h>

#include "agent.hpp"
#include "models.hpp"

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
    /// Replaces the conversation so far with a summary from the model. The original messages stay in the session.
    void compact();
    /// "" lists the project's custom agents, a name uses one, "none" goes back to the default.
    void choose_agent(const std::string& name);
    /// Tells the user which MCP servers are registered and what each may call.
    void show_mcp_servers();

    // ---- chat ----
    const std::vector<ChatConversation>& chats() const { return chats_; }
    const ChatConversation* chat() const;
    std::string chat_selection;
    bool chat_running = false;
    std::string chat_live;
    /// Files waiting to go with the next message.
    std::vector<ChatAttachment> chat_attachments;
    void select_chat(const std::string& id);
    void new_chat();
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
    std::string runtime_path;
    std::string model_busy;  ///< What the Models page is doing, or empty.
    std::optional<DownloadState> download;
    std::vector<HubModel> search_results;
    std::optional<HubRepository> repository;
    std::vector<HubSuggestion> suggestions;
    const ModelServer* server() const { return server_.get(); }

    void use(const ModelEndpoint& chosen, const std::string& name);
    void refresh_models(bool start_last_if_needed = false);
    void start_model(const LocalModel& model, int context = 0);
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
    /// Starts model-page work on a thread. `work` gets a progress callback and a cancel token; `done` runs on the main thread.
    void model_task(const std::string& label, std::function<std::function<void()>(const std::function<void(const std::string&)>&, const CancelToken&)> work);

    std::unique_ptr<SessionStore> store_;
    std::unique_ptr<ChatStore> chat_store_;
    std::vector<ChatConversation> chats_;
    std::thread chat_thread_;
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
