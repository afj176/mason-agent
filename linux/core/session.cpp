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

std::string Verification::summary() const {
    if (verdict == "passed") return workflow + " passed";
    if (verdict == "fixed") return workflow + " passes now; it failed before this request";
    if (verdict == "regression") return workflow + " fails now; it passed before this request";
    if (verdict == "failingBefore") return workflow + " fails, as it did before this request";
    if (verdict == "notRun") return workflow + " was not run";
    return workflow + " failed";
}

json message_json(const ChatMessage& message) {
    json item = {{"role", message.role}, {"content", message.content}};
    if (!message.tool_calls.empty()) {
        item["toolCalls"] = json::array();
        for (const ToolCall& call : message.tool_calls) item["toolCalls"].push_back({{"id", call.id}, {"name", call.name}, {"arguments", call.arguments}});
    }
    if (!message.tool_call_id.empty()) item["toolCallId"] = message.tool_call_id;
    return item;
}

ChatMessage message_from_json(const json& item) {
    ChatMessage message{item.value("role", ""), item.value("content", ""), {}, item.value("toolCallId", "")};
    for (const json& call : item.value("toolCalls", json::array())) message.tool_calls.push_back({call.value("id", ""), call.value("name", ""), call.value("arguments", "{}")});
    return message;
}

json to_json(const Session& session) {
    json messages = json::array();
    for (const ChatMessage& message : session.messages) messages.push_back(message_json(message));
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
            if (!change.before_blob.empty()) item["beforeBlob"] = change.before_blob;
            if (!change.after_blob.empty()) item["afterBlob"] = change.after_blob;
            if (change.after_missing) item["afterMissing"] = true;
            if (change.by_command) item["byCommand"] = true;
            changes.push_back(std::move(item));
        }
        json saved = {{"id", turn.id}, {"request", turn.request}, {"answer", turn.answer}, {"changes", changes}, {"undone", turn.undone}, {"firstActivity", turn.first_activity}};
        if (turn.verification) {
            const Verification& receipt = *turn.verification;
            saved["verification"] = {{"workflow", receipt.workflow}, {"command", receipt.command}, {"verdict", receipt.verdict}, {"output", receipt.output}, {"repairs", receipt.repairs}};
            if (receipt.exit_code) saved["verification"]["exitCode"] = *receipt.exit_code;
            if (receipt.baseline_exit_code) saved["verification"]["baselineExitCode"] = *receipt.baseline_exit_code;
        }
        turns.push_back(std::move(saved));
    }
    json result = {{"version", 1}, {"id", session.id}, {"title", session.title}, {"workspace", session.workspace},
                   {"permission", permission_key(session.permission)}, {"askOnly", session.ask_only}, {"state", session.state},
                   {"detail", session.detail}, {"updated", session.updated}, {"messages", messages}, {"activities", activities},
                   {"turns", turns}, {"promptTokens", session.prompt_tokens}, {"pendingNote", session.pending_note}};
    if (!session.agent.empty()) result["agent"] = session.agent;
    if (session.compactions > 0) result["compactions"] = session.compactions;
    if (!session.pending_input.empty()) {
        result["pendingInput"] = json::array();
        for (const FollowUp& item : session.pending_input) result["pendingInput"].push_back({{"id", item.id}, {"text", item.text}, {"steer", item.steer}});
    }
    if (session.desktop) result["desktop"] = {{"pid", session.desktop->pid}, {"app", session.desktop->app}, {"title", session.desktop->title}};
    if (!session.comments.empty()) {
        result["comments"] = json::array();
        for (const ReviewComment& item : session.comments) {
            result["comments"].push_back({{"id", item.id}, {"path", item.path}, {"revision", item.revision}, {"original", item.original}, {"line", item.line}, {"quote", item.quote}, {"note", item.note}});
        }
    }
    if (!session.milestones.empty()) {
        result["milestones"] = json::array();
        for (const Milestone& item : session.milestones) {
            json saved = {{"id", item.id}, {"title", item.title}, {"decision", item.decision}, {"files", item.files}, {"dependsOn", item.depends_on}, {"workflow", item.workflow}};
            if (item.receipt) saved["receipt"] = {{"id", item.receipt->id}, {"at", item.receipt->at}, {"revision", item.receipt->revision}, {"detail", item.receipt->detail}, {"manual", item.receipt->manual}};
            result["milestones"].push_back(std::move(saved));
        }
    }
    if (!session.archived.empty()) {
        result["archived"] = json::array();
        for (const ChatMessage& message : session.archived) result["archived"].push_back(message_json(message));
    }
    return result;
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
    session.agent = value.value("agent", "");
    session.compactions = value.value("compactions", 0);
    for (const json& item : value.value("pendingInput", json::array())) {
        if (item.is_object()) session.pending_input.push_back({item.value("id", new_id()), item.value("text", ""), item.value("steer", false)});
    }
    if (value.contains("desktop") && value["desktop"].is_object()) session.desktop = DesktopWindow{value["desktop"].value("pid", 0), value["desktop"].value("app", ""), value["desktop"].value("title", "")};
    for (const json& item : value.value("comments", json::array())) {
        if (item.is_object()) session.comments.push_back({item.value("id", new_id()), item.value("path", ""), item.value("revision", ""), item.value("original", false), item.value("line", 0), item.value("quote", ""), item.value("note", "")});
    }
    for (const json& item : value.value("milestones", json::array())) {
        if (!item.is_object()) continue;
        Milestone milestone;
        milestone.id = item.value("id", new_id());
        milestone.title = item.value("title", "");
        milestone.decision = item.value("decision", "");
        milestone.files = item.value("files", std::vector<std::string>{});
        milestone.depends_on = item.value("dependsOn", std::vector<std::string>{});
        milestone.workflow = item.value("workflow", "");
        if (item.contains("receipt") && item["receipt"].is_object()) {
            const json& receipt = item["receipt"];
            milestone.receipt = MilestoneReceipt{receipt.value("id", new_id()), receipt.value("at", now_iso()), receipt.value("revision", ""), receipt.value("detail", ""), receipt.value("manual", false)};
        }
        session.milestones.push_back(std::move(milestone));
    }
    for (const json& item : value.value("messages", json::array())) session.messages.push_back(message_from_json(item));
    for (const json& item : value.value("archived", json::array())) session.archived.push_back(message_from_json(item));
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
        if (item.contains("verification") && item["verification"].is_object()) {
            const json& saved = item["verification"];
            Verification receipt;
            receipt.workflow = saved.value("workflow", "");
            receipt.command = saved.value("command", "");
            receipt.verdict = saved.value("verdict", "");
            receipt.output = saved.value("output", "");
            receipt.repairs = saved.value("repairs", 0);
            if (saved.contains("exitCode") && saved["exitCode"].is_number_integer()) receipt.exit_code = saved["exitCode"].get<int>();
            if (saved.contains("baselineExitCode") && saved["baselineExitCode"].is_number_integer()) receipt.baseline_exit_code = saved["baselineExitCode"].get<int>();
            turn.verification = std::move(receipt);
        }
        for (const json& change : item.value("changes", json::array())) {
            FileChange entry{change.value("path", ""), std::nullopt, change.value("after", "")};
            if (change.contains("before") && change["before"].is_string()) entry.before = change["before"].get<std::string>();
            entry.before_blob = change.value("beforeBlob", "");
            entry.after_blob = change.value("afterBlob", "");
            entry.after_missing = change.value("afterMissing", false);
            entry.by_command = change.value("byCommand", false);
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
    std::lock_guard<std::mutex> guard(lock_);
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
    std::lock_guard<std::mutex> guard(lock_);
    session.updated = now_iso();
    write_file_atomic(folder_ / (session.id + ".json"), to_json(session).dump(1, ' ', false, json::error_handler_t::replace));
    auto existing = std::find_if(sessions_.begin(), sessions_.end(), [&](const Session& s) { return s.id == session.id; });
    if (existing != sessions_.end()) *existing = session;
    else sessions_.insert(sessions_.begin(), session);
}

void SessionStore::remove(const std::string& id) {
    std::lock_guard<std::mutex> guard(lock_);
    std::error_code failure;
    fs::remove(folder_ / (id + ".json"), failure);
    sessions_.erase(std::remove_if(sessions_.begin(), sessions_.end(), [&](const Session& s) { return s.id == id; }), sessions_.end());
}

}  // namespace mason
