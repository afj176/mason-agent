// The agent loop: ask the model, run the tool it calls, hand the result back, until it answers in text.
#pragma once
#include "harness.hpp"
#include "mcp.hpp"
#include "model.hpp"
#include "shell.hpp"

namespace mason {

struct ApprovalRequest {
    std::string tool, title, detail;
    std::string subject;  ///< What an always-allow rule would match: the command text or the file path.
};

struct AgentEvent {
    enum class Kind { Text, ActivityStarted, ActivityFinished, Status, FollowUp } kind;
    std::string text;
    Activity activity;
    /// For FollowUp: the session as the previous request left it; `text` is the follow-up about to run.
    std::shared_ptr<const Session> session = nullptr;
};

/// Always-allow rules a user saved for one project. They only skip the prompt; they never widen the sandbox.
class ApprovalRules {
public:
    struct Rule { std::string tool, prefix; };
    ApprovalRules(const fs::path& home, const std::string& workspace);
    const std::vector<Rule>& rules() const { return rules_; }
    bool allows(const std::string& tool, const std::string& subject) const;
    void add(const std::string& tool, const std::string& prefix);
    void remove(size_t index);
    /// The program and its first argument for a command ("git status"), the folder for a file.
    static std::string suggestion(const std::string& tool, const std::string& subject);
private:
    void persist() const;
    fs::path file_;
    std::vector<Rule> rules_;
};

struct UndoResult {
    std::vector<std::string> restored, skipped;
};

class Agent {
public:
    static constexpr int kActionLimit = 64;
    using Approve = std::function<bool(const ApprovalRequest&)>;
    using Emit = std::function<void(const AgentEvent&)>;

    /// `approve` and `emit` are called on the thread that calls `run`.
    Agent(SessionStore& store, ChatModel& model, Approve approve, Emit emit);

    static const std::vector<ToolSpec>& tools();
    /// Offered only when the user has registered an MCP server, so sessions without one keep a short tool list.
    static const std::vector<ToolSpec>& mcp_tools();
    static constexpr size_t kFollowUpLimit = 16;
    /// Runs a request, then any follow-ups sent while it ran. "/queue" resumes follow-ups left from an earlier stop.
    /// A queued follow-up runs only after a request that completed; a steering one runs whatever happened.
    /// The session is saved after every step, so an interruption loses nothing that already happened.
    void run(Session& session, const std::string& request, const CancelToken& cancel = nullptr);
    /// Adds a message while a request is running; safe to call from another thread. Steering stops the model's
    /// current step and handles the message next; otherwise the message is queued until the request completes.
    void send_while_running(const std::string& text, bool steer);

    /// Puts back every file the latest request changed, whether an edit tool or a command changed it. A file changed
    /// again since is left alone and reported. Returns nothing when there is no such request.
    static std::optional<UndoResult> undo_latest(Session& session, SessionStore& store, bool redo = false);
    /// A tool call without a result would make the next request invalid for the model, so calls that were cut short are closed.
    static void close_open_calls(Session& session);

private:
    void run_turn(Session& session, const std::string& request, const CancelToken& cancel);
    std::string perform(const ToolCall& call, const Workspace& workspace, Session& session, const CancelToken& cancel);
    /// The tools for this request: the built-in ones, plus the MCP tools when a server is registered.
    std::vector<ToolSpec> offered_tools() const;
    bool allowed(const ApprovalRequest& request, Session& session);
    /// Records what a command created, changed or deleted, and returns the paths.
    std::vector<std::string> record_command_changes(const snapshot::Map& before, const Workspace& workspace, Session& session);

    SessionStore& store_;
    ChatModel& model_;
    Approve approve_;
    Emit emit_;
    /// The project as last read, so each command only re-reads the files that changed.
    snapshot::Map snapshot_;
    /// Follow-ups that arrive while a request runs. Everything here is read and changed under the lock.
    std::mutex inbox_lock_;
    std::vector<FollowUp> inbox_;
    bool accepting_ = false;
    CancelToken turn_cancel_;
};

}  // namespace mason
