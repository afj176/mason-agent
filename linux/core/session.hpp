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

struct Turn {
    std::string id = new_id();
    std::string request, answer;
    std::vector<FileChange> changes;
    bool undone = false;
    size_t first_activity = 0;
};

struct Session {
    std::string id = new_id();
    std::string title = "New session";
    std::string workspace;
    Permission permission = Permission::ReviewEdits;
    bool ask_only = false;
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
};

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
