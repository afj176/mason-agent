// The agent loop: ask the model, run the tool it calls, hand the result back, until it answers in text.
#pragma once
#include "desktop.hpp"
#include "harness.hpp"
#include "mcp.hpp"
#include "network.hpp"
#include "tools.hpp"
#include "model.hpp"
#include "semantic.hpp"
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
    /// Tools beyond the basic file and shell ones. Which of them a request is offered depends on the session and the project.
    static std::vector<ToolSpec> extra_tools(const Session& session, const Workspace& workspace, bool semantic = false, bool presents = false);
    /// Set by an app that can show things to the user: a file at a line ("file") or a project page ("page").
    void set_presenter(std::function<void(const std::string& kind, const std::string& path, int line)> present) { present_ = std::move(present); }
    /// In Auto, Mason works out checks from the request and runs them itself when the model says it is done.
    bool derives_checks = true;
    /// What reads and operates the attached window. Left unset, it is this computer's accessibility interface.
    void set_desktop(std::shared_ptr<DesktopAutomation> automation) { desktop_ = std::move(automation); }
    /// What looks at a picture of the attached window. Left unset, it is the running model, which must take images.
    void set_vision(std::shared_ptr<DesktopVision> vision) { vision_ = std::move(vision); }
    /// The window tools a session is offered: none without an attached window, and no acting in a read-only session.
    static std::vector<ToolSpec> desktop_tools(const Session& session, bool available);
    /// What searches code by meaning. Left unset, it is the embeddings server saved in settings, if there is one.
    void set_semantic(std::shared_ptr<SemanticModels> models) { semantic_ = std::move(models); }
    /// True when no background job from the last request is still held.
    bool jobs_empty() { return jobs_.empty(); }
    static constexpr size_t kFollowUpLimit = 16;
    /// How many times a request is handed back to the model because verification regressed.
    static constexpr int kRepairLimit = 2;
    /// The name of the workflow in mason.workflows.json that Mason runs to verify a request.
    static constexpr const char* kVerifyWorkflow = "verify";
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
    /// Builds a single-page offline store with the catalog component, and checks it. Returns false, having changed
    /// nothing, when the request is not one the component fully covers; the request then takes the ordinary path.
    bool build_catalog(const std::string& request, const Workspace& workspace, Session& session, const CancelToken& cancel);
    /// The verify workflow's command, when the project defines one.
    std::optional<std::string> verify_command(const Workspace& workspace) const;
    /// Mason runs the workflow itself, so it needs the permission a command would: Auto, or a saved rule for "workflow verify".
    bool may_verify(const Session& session) const;
    /// Runs the verify workflow, or returns nothing when there is none or it may not be run.
    std::optional<CommandResult> verify_run(const Workspace& workspace, const Session& session, const CancelToken& cancel);
    /// After the model's answer: when the request changed files and the project has a verify workflow, the receipt.
    std::optional<Verification> verify(const Workspace& workspace, const Session& session, const std::optional<CommandResult>& baseline, int repairs, const CancelToken& cancel);
    std::string perform(const ToolCall& call, const Workspace& workspace, Session& session, const CancelToken& cancel);
    /// The tools for this request: the built-in ones, the extra ones that fit the session and the project, and the
    /// MCP tools when a server is registered.
    std::vector<ToolSpec> offered_tools(const Session& session, const Workspace& workspace) const;
    /// Carries out one of the extra tools. Returns nothing when the tool is not one of them; otherwise the state and the result.
    std::optional<std::pair<std::string, std::string>> perform_extra(const ToolCall& call, const json& arguments, const Workspace& workspace, Session& session, const CancelToken& cancel);
    bool allowed(const ApprovalRequest& request, Session& session);
    /// Records what a command created, changed or deleted, and returns the paths.
    std::vector<std::string> record_command_changes(const snapshot::Map& before, const Workspace& workspace, Session& session);

    SessionStore& store_;
    ChatModel& model_;
    Approve approve_;
    Emit emit_;
    /// The project as last read, so each command only re-reads the files that changed.
    snapshot::Map snapshot_;
    /// Background programs started during the current request.
    Jobs jobs_;
    /// The tools offered for the request in progress, for the message about an unknown tool.
    std::vector<std::string> offered_names_;
    /// A question the model asked with ask_user. The request stops there and waits for the user's next message.
    std::optional<std::string> pending_question_;
    /// The relay this request's sandboxed commands may reach hosts through, once one has been needed.
    std::unique_ptr<NetworkRelay> network_;
    NetworkRelay* relay(const Session& session);
    std::string network_note(size_t since);
    std::function<void(const std::string&, const std::string&, int)> present_;
    std::optional<std::string> last_search_;  // this request's latest search_files text, which search_next continues
    std::shared_ptr<SemanticModels> semantic_;
    std::shared_ptr<DesktopAutomation> desktop_;
    std::shared_ptr<DesktopVision> vision_;
    std::shared_ptr<DesktopAutomation> desktop_automation() const { return desktop_ ? desktop_ : desktop::system(); }
    /// The controls from the latest observation of the attached window; an action must name one of them.
    std::vector<DesktopControl> last_observation_;
    std::optional<std::pair<std::string, std::string>> perform_desktop(const ToolCall& call, const json& arguments, Session& session, const CancelToken& cancel);
    std::shared_ptr<SemanticModels> semantic_models() const;
    /// Follow-ups that arrive while a request runs. Everything here is read and changed under the lock.
    std::mutex inbox_lock_;
    std::vector<FollowUp> inbox_;
    bool accepting_ = false;
    CancelToken turn_cancel_;
};

}  // namespace mason
