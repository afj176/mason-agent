// Sessions: the conversation with the model, what the tools did, and what each request changed.
#pragma once
#include <mutex>

#include "util.hpp"

namespace mason {

/// What Mason may do without asking.
enum class Permission {
    ReadOnly,        ///< Reads and answers; no edits, no commands.
    ReviewEdits,     ///< Asks before each edit and each command.
    WorkspaceEdits,  ///< Edits apply without asking; commands still ask.
    Auto,            ///< Edits apply and commands run without asking, inside a sandbox limited to the project.
};
std::string permission_label(Permission permission);
std::string permission_key(Permission permission);
Permission permission_from_key(const std::string& key);

struct ToolCall {
    std::string id, name;
    std::string arguments;  ///< JSON object text, exactly as the model produced it.
};

struct ChatMessage {
    std::string role, content;
    std::vector<ToolCall> tool_calls;
    std::string tool_call_id;
    /// Pictures that go with a chat message, each as a data: address. The model receives them beside the text.
    std::vector<std::string> images = {};
};

struct Activity {
    std::string id = new_id();
    std::string tool, subject;
    std::string state = "running";  ///< running, completed, failed or denied
    std::string result;
};

/// One file as it was before a request first touched it, so the request can be undone.
struct FileChange {
    std::string path;
    std::optional<std::string> before;  ///< Empty when the request created the file.
    std::string after;
    /// Set when a side is binary or too large to keep as text; the content is in the blob store under this SHA-256.
    std::string before_blob = "", after_blob = "";
    bool after_missing = false;  ///< A command deleted the file.
    bool by_command = false;     ///< A command, not an edit tool, made the change.

    bool opaque() const { return !before_blob.empty() || !after_blob.empty(); }
    bool created() const { return !before && before_blob.empty(); }
    bool deleted() const { return after_missing; }
};

/// A message sent while a request was running. A steering message interrupts the request and is handled next;
/// a queued one waits until the request completes.
struct FollowUp {
    std::string id = new_id();
    std::string text;
    bool steer = false;
};

/// What Mason itself found when it ran the project's `verify` workflow around a request that changed files.
/// The model does not write this; it is the result of the command.
struct Verification {
    std::string workflow, command;
    /// passed, fixed (failed before, passes now), regression (passed before, fails now), failingBefore (failed before
    /// and still fails), failed (fails, with nothing from before to compare), or notRun (Mason was not allowed to run it).
    std::string verdict;
    std::optional<int> exit_code, baseline_exit_code;
    std::string output;  ///< The end of the output of the run after the changes, or why it was not run.
    int repairs = 0;     ///< How many times the model was handed a regression to repair.
    std::string summary() const;
};

/// A check Mason worked out from a request. `kind` is document_text, browser_heading, browser_text, browser_click or
/// browser_flow; for a flow, `steps` holds each step as op, target, value.
struct DerivedCheck {
    int unit = 0;  ///< The requirement this checks, numbered from 1 in the order the request states them.
    std::string feature, kind, path, target, expected;
    std::vector<std::vector<std::string>> steps;
    std::string application;
};

struct DerivedResult {
    DerivedCheck check;
    std::string state;  ///< passed, failed, or notRun (no browser on this computer)
    std::string detail;
};

/// What Mason found when it checked a request's requirements itself. The model does not write this.
struct DerivedReceipt {
    std::vector<DerivedResult> results;
    std::vector<std::string> unverified;  ///< Requirements Mason had no way to check.
    int repairs = 0;                      ///< How many times the model was handed a failed check to fix.
};

struct Turn {
    std::string id = new_id();
    std::string request, answer;
    std::vector<FileChange> changes;
    bool undone = false;
    size_t first_activity = 0;
    /// Set when the project has a `verify` workflow and the request changed files.
    std::optional<Verification> verification;
    /// Set in Auto when Mason worked out checks from the request and ran them itself.
    std::optional<DerivedReceipt> checks;
};

/// A note on one line of a file. `revision` is the file's SHA-256 when the note was made; a note on a line the
/// request removed (`original`) quotes the old text and is located by its old line number.
struct ReviewComment {
    std::string id = new_id();
    std::string path, revision;
    bool original = false;
    int line = 0;
    std::string quote, note;
};

/// A window of another app, as the user attached it to a session.
struct DesktopWindow {
    int pid = 0;
    std::string app, title;
};

struct MilestoneReceipt {
    std::string id = new_id();
    std::string at = now_iso();
    std::string revision, detail;
    bool manual = false;  ///< True when a person recorded what they saw; false when a workflow passed.
};

/// One step of a saved plan: what it decides, the files it covers, what must come first, and how it is checked
/// (a workflow from mason.workflows.json, or by the user recording what they saw).
struct Milestone {
    std::string id = new_id();
    std::string title, decision;
    std::vector<std::string> files, depends_on;
    std::string workflow;
    std::optional<MilestoneReceipt> receipt;
};

struct Session {
    std::string id = new_id();
    std::string title = "New session";
    std::string workspace;
    Permission permission = Permission::ReviewEdits;
    bool ask_only = false;
    /// What Auto does about the network in this session: "ask", "off", or empty to follow the computer's setting.
    std::string network = "";
    std::string state = "Ready";
    std::string detail;
    std::string updated = now_iso();
    std::vector<ChatMessage> messages;
    std::vector<Activity> activities;
    std::vector<Turn> turns;
    int prompt_tokens = 0;
    /// Told to the model with the next request: what the user undid or redid outside the conversation.
    std::string pending_note;
    /// The custom agent whose instructions frame each request, if one is selected.
    std::string agent;
    /// Messages a compaction replaced with a summary, oldest first.
    std::vector<ChatMessage> archived;
    int compactions = 0;
    /// Follow-ups waiting their turn, oldest first. "/queue" resumes them after a stop.
    std::vector<FollowUp> pending_input;
    /// Comments on lines of the project, sent with the next request.
    std::vector<ReviewComment> comments;
    /// The saved plan, if the user made one.
    std::vector<Milestone> milestones;
    /// The window of another app the user attached, which Mason may read and, with approval, operate.
    std::optional<DesktopWindow> desktop;
};

json message_json(const ChatMessage& message);
ChatMessage message_from_json(const json& item);
json to_json(const Session& session);
Session session_from_json(const json& value);

/// Sessions as one JSON file each, written atomically. A session interrupted mid-run is marked so on load; nothing is replayed.
class SessionStore {
public:
    explicit SessionStore(fs::path home = data_home());
    const fs::path& home() const { return home_; }
    const fs::path& folder() const { return folder_; }
    /// Newest first.
    const std::vector<Session>& sessions() const { return sessions_; }
    std::vector<Session> sessions_in(const std::string& workspace) const;
    Session create(const std::string& workspace, Permission permission = Permission::ReviewEdits);
    void save(Session& session);
    void remove(const std::string& id);

private:
    fs::path home_, folder_;
    std::vector<Session> sessions_;
    /// The app saves from the engine thread and the main thread; saving, creating and removing take this lock.
    mutable std::mutex lock_;
};

}  // namespace mason
