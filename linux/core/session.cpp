#include "session.hpp"

#include <algorithm>

namespace mason {

std::string permission_label(Permission permission) {
    switch (permission) {
        case Permission::ReadOnly: return "read-only";
        case Permission::ReviewEdits: return "review edits";
        case Permission::WorkspaceEdits: return "workspace edits";
        case Permission::Auto: return "auto (sandboxed)";
    }
    return "";
}

std::string permission_key(Permission permission) {
    switch (permission) {
        case Permission::ReadOnly: return "readOnly";
        case Permission::ReviewEdits: return "reviewEdits";
        case Permission::WorkspaceEdits: return "workspaceEdits";
        case Permission::Auto: return "auto";
    }
    return "reviewEdits";
}

Permission permission_from_key(const std::string& key) {
    if (key == "readOnly") return Permission::ReadOnly;
    if (key == "workspaceEdits") return Permission::WorkspaceEdits;
    if (key == "auto") return Permission::Auto;
    return Permission::ReviewEdits;
}

json to_json(const Session& session) {
    json messages = json::array();
    for (const ChatMessage& message : session.messages) {
        json item = {{"role", message.role}, {"content", message.content}};
        if (!message.tool_calls.empty()) {
            item["toolCalls"] = json::array();
            for (const ToolCall& call : message.tool_calls) item["toolCalls"].push_back({{"id", call.id}, {"name", call.name}, {"arguments", call.arguments}});
        }
        if (!message.tool_call_id.empty()) item["toolCallId"] = message.tool_call_id;
        messages.push_back(std::move(item));
    }
    json activities = json::array();
    for (const Activity& activity : session.activities) {
        activities.push_back({{"id", activity.id}, {"tool", activity.tool}, {"subject", activity.subject}, {"state", activity.state}, {"result", activity.result}});
    }
    json turns = json::array();
    for (const Turn& turn : session.turns) {
        json changes = json::array();
        for (const FileChange& change : turn.changes) {
            json item = {{"path", change.path}, {"after", change.after}};
            if (change.before) item["before"] = *change.before;
            changes.push_back(std::move(item));
        }
        turns.push_back({{"id", turn.id}, {"request", turn.request}, {"answer", turn.answer}, {"changes", changes}, {"undone", turn.undone}, {"firstActivity", turn.first_activity}});
    }
    return {{"version", 1}, {"id", session.id}, {"title", session.title}, {"workspace", session.workspace},
            {"permission", permission_key(session.permission)}, {"askOnly", session.ask_only}, {"state", session.state},
            {"detail", session.detail}, {"updated", session.updated}, {"messages", messages}, {"activities", activities},
            {"turns", turns}, {"promptTokens", session.prompt_tokens}, {"pendingNote", session.pending_note}};
}

Session session_from_json(const json& value) {
    Session session;
    session.id = value.at("id").get<std::string>();
    session.title = value.value("title", "New session");
    session.workspace = value.at("workspace").get<std::string>();
    session.permission = permission_from_key(value.value("permission", "reviewEdits"));
    session.ask_only = value.value("askOnly", false);
    session.state = value.value("state", "Ready");
    session.detail = value.value("detail", "");
    session.updated = value.value("updated", now_iso());
    session.prompt_tokens = value.value("promptTokens", 0);
    session.pending_note = value.value("pendingNote", "");
    for (const json& item : value.value("messages", json::array())) {
        ChatMessage message{item.value("role", ""), item.value("content", ""), {}, item.value("toolCallId", "")};
        for (const json& call : item.value("toolCalls", json::array())) message.tool_calls.push_back({call.value("id", ""), call.value("name", ""), call.value("arguments", "{}")});
        session.messages.push_back(std::move(message));
    }
    for (const json& item : value.value("activities", json::array())) {
        session.activities.push_back({item.value("id", new_id()), item.value("tool", ""), item.value("subject", ""), item.value("state", "completed"), item.value("result", "")});
    }
    for (const json& item : value.value("turns", json::array())) {
        Turn turn;
        turn.id = item.value("id", new_id());
        turn.request = item.value("request", "");
        turn.answer = item.value("answer", "");
        turn.undone = item.value("undone", false);
        turn.first_activity = item.value("firstActivity", size_t{0});
        for (const json& change : item.value("changes", json::array())) {
            FileChange entry{change.value("path", ""), std::nullopt, change.value("after", "")};
            if (change.contains("before") && change["before"].is_string()) entry.before = change["before"].get<std::string>();
            turn.changes.push_back(std::move(entry));
        }
        session.turns.push_back(std::move(turn));
    }
    return session;
}

SessionStore::SessionStore(fs::path home) : home_(std::move(home)), folder_(home_ / "sessions") {
    fs::create_directories(folder_);
    std::error_code failure;
    for (fs::directory_iterator it(folder_, failure), end; !failure && it != end; it.increment(failure)) {
        if (it->path().extension() != ".json") continue;
        try {
            Session session = session_from_json(json::parse(read_file(it->path())));
            if (session.state == "Running" || session.state == "Awaiting review") {
                session.state = "Interrupted";
                session.detail = "The previous run was interrupted. Check the changes before continuing; nothing has been replayed.";
                for (Activity& activity : session.activities) {
                    if (activity.state == "running") activity.state = "failed";
                }
            }
            sessions_.push_back(std::move(session));
        } catch (const std::exception&) {
            // A file that is not a session is left alone; it is neither loaded nor deleted.
        }
    }
    std::sort(sessions_.begin(), sessions_.end(), [](const Session& a, const Session& b) { return a.updated > b.updated; });
}

std::vector<Session> SessionStore::sessions_in(const std::string& workspace) const {
    std::vector<Session> mine;
    for (const Session& session : sessions_) {
        if (session.workspace == workspace) mine.push_back(session);
    }
    return mine;
}

Session SessionStore::create(const std::string& workspace, Permission permission) {
    Session session;
    session.workspace = workspace;
    session.permission = permission;
    save(session);
    return session;
}

void SessionStore::save(Session& session) {
    session.updated = now_iso();
    write_file_atomic(folder_ / (session.id + ".json"), to_json(session).dump(1, ' ', false, json::error_handler_t::replace));
    auto existing = std::find_if(sessions_.begin(), sessions_.end(), [&](const Session& s) { return s.id == session.id; });
    if (existing != sessions_.end()) *existing = session;
    else sessions_.insert(sessions_.begin(), session);
}

void SessionStore::remove(const std::string& id) {
    std::error_code failure;
    fs::remove(folder_ / (id + ".json"), failure);
    sessions_.erase(std::remove_if(sessions_.begin(), sessions_.end(), [&](const Session& s) { return s.id == id; }), sessions_.end());
}

}  // namespace mason
