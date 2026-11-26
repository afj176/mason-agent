#include "agent.hpp"

#include <algorithm>
#include <chrono>
#include <set>
#include <thread>

namespace mason {

// ---- approval rules --------------------------------------------------------------------------------------------

ApprovalRules::ApprovalRules(const fs::path& home, const std::string& workspace) {
    uint64_t hash = 0xcbf29ce484222325ULL;
    for (unsigned char byte : workspace) hash = (hash ^ byte) * 0x100000001b3ULL;
    char name[32];
    std::snprintf(name, sizeof name, "%llx.json", static_cast<unsigned long long>(hash));
    file_ = home / "approval-rules" / name;
    try {
        const json saved = json::parse(read_file(file_), nullptr, false);
        if (saved.is_array()) {
            for (const json& item : saved) {
                if (item.is_object()) rules_.push_back({item.value("tool", ""), item.value("prefix", "")});
            }
        }
    } catch (const Error&) {
        // No rules saved yet.
    }
}

bool ApprovalRules::allows(const std::string& tool, const std::string& subject) const {
    return std::any_of(rules_.begin(), rules_.end(), [&](const Rule& rule) { return rule.tool == tool && !rule.prefix.empty() && starts_with(subject, rule.prefix); });
}

void ApprovalRules::add(const std::string& tool, const std::string& prefix) {
    const std::string trimmed = trim(prefix);
    if (trimmed.empty() || trimmed.size() > 400) throw Error("A rule needs 1–400 characters to match.");
    rules_.erase(std::remove_if(rules_.begin(), rules_.end(), [&](const Rule& rule) { return rule.tool == tool && rule.prefix == trimmed; }), rules_.end());
    rules_.push_back({tool, trimmed});
    persist();
}

void ApprovalRules::remove(size_t index) {
    if (index >= rules_.size()) throw Error("There is no rule with that number.");
    rules_.erase(rules_.begin() + static_cast<std::ptrdiff_t>(index));
    persist();
}

void ApprovalRules::persist() const {
    json saved = json::array();
    for (const Rule& rule : rules_) saved.push_back({{"tool", rule.tool}, {"prefix", rule.prefix}});
    write_file_atomic(file_, saved.dump(1));
}

std::string ApprovalRules::suggestion(const std::string& tool, const std::string& subject) {
    if (tool == "run_shell") {
        std::vector<std::string> words = split(subject, ' ', false);
        if (words.size() > 2) words.resize(2);
        return join(words, " ");
    }
    const size_t slash = subject.rfind('/');
    return slash == std::string::npos ? subject : subject.substr(0, slash + 1);
}

// ---- agent -----------------------------------------------------------------------------------------------------

Agent::Agent(SessionStore& store, ChatModel& model, Approve approve, Emit emit)
    : store_(store), model_(model), approve_(std::move(approve)), emit_(std::move(emit)) {}

const std::vector<ToolSpec>& Agent::tools() {
    static const std::vector<ToolSpec> specs = [] {
        auto schema = [](json properties, std::vector<std::string> required) {
            return json{{"type", "object"}, {"properties", std::move(properties)}, {"required", std::move(required)}, {"additionalProperties", false}};
        };
        const json path = {{"type", "string"}, {"description", "Path relative to the project folder."}};
        return std::vector<ToolSpec>{
            {"list_files", "List the project's files.",
             schema({{"path", {{"type", "string"}, {"description", "Folder to list, relative to the project. Use . for the whole project."}}}}, {"path"})},
            {"read_file", "Read a UTF-8 text file with line numbers.", schema({{"path", path}}, {"path"})},
            {"search_files", "Search the project's files for text (case-insensitive).", schema({{"query", {{"type", "string"}}}}, {"query"})},
            {"edit_file", "Replace one exact passage in an existing file. old_text must match exactly once; include enough surrounding lines to make it unique.",
             schema({{"path", path}, {"old_text", {{"type", "string"}}}, {"new_text", {{"type", "string"}}}}, {"path", "old_text", "new_text"})},
            {"write_file", "Create a file, or replace its whole content.", schema({{"path", path}, {"content", {{"type", "string"}}}}, {"path", "content"})},
            {"run_shell", "Run a shell command in the project folder and return its output. No interactive input; 120 second limit.",
             schema({{"command", {{"type", "string"}}}}, {"command"})},
        };
    }();
    return specs;
}

namespace {

std::string system_prompt(const Session& session) {
    const bool read_only = session.ask_only || session.permission == Permission::ReadOnly;
    return "You are Mason Agent, a coding agent working in the project folder " + session.workspace +
           " on Linux.\n"
           "Use the tools to inspect the project before changing it, and call one tool at a time. Read a file before editing it.\n"
           "Make the smallest change that does what was asked, then check your work when a quick check exists.\n" +
           (read_only ? "This session is read-only: answer from what you read; do not edit files or run commands.\n" : "") +
           "When you are done, reply in plain text with a short summary of what you did and anything the user should verify. "
           "Do not claim a check passed unless you ran it.";
}

size_t count_occurrences(const std::string& text, const std::string& part) {
    size_t count = 0;
    for (size_t at = text.find(part); at != std::string::npos; at = text.find(part, at + part.size())) count++;
    return count;
}

}  // namespace

const std::vector<ToolSpec>& Agent::mcp_tools() {
    static const std::vector<ToolSpec> list = [] {
        auto schema = [](const std::vector<std::string>& names) {
            json properties = json::object();
            for (const std::string& name : names) properties[name] = {{"type", "string"}};
            return json{{"type", "object"}, {"properties", properties}, {"required", names}, {"additionalProperties", false}};
        };
        return std::vector<ToolSpec>{
            {"mcp_tools", "List the tools you may call on a user-registered MCP server.", schema({"server"})},
            {"mcp_call", "Call one allowed tool on a registered MCP server. arguments_json is a JSON object. The user is asked first.", schema({"server", "tool", "arguments_json"})},
            {"mcp_resources", "List the resources you may read from a registered MCP server.", schema({"server"})},
            {"mcp_read", "Read one allowed resource from a registered MCP server.", schema({"server", "uri"})},
        };
    }();
    return list;
}

std::vector<ToolSpec> Agent::offered_tools() const {
    std::vector<ToolSpec> offered = tools();
    if (!mcp::enabled(store_.home()).empty()) offered.insert(offered.end(), mcp_tools().begin(), mcp_tools().end());
    return offered;
}

void Agent::send_while_running(const std::string& text, bool steer) {
    const std::string trimmed = trim(text);
    if (trimmed.empty() || utf8_length(trimmed) > 16000) throw Error("Write a message of 1–16,000 characters.");
    std::lock_guard<std::mutex> guard(inbox_lock_);
    if (!accepting_) throw Error("No request is running in this session.");
    if (inbox_.size() >= kFollowUpLimit) throw Error("At most " + std::to_string(kFollowUpLimit) + " messages can wait at once.");
    FollowUp message;
    message.text = trimmed;
    message.steer = steer;
    inbox_.push_back(std::move(message));
    if (steer && turn_cancel_) turn_cancel_->store(true);
}

namespace {
/// The follow-up handled next: a steering message first, else the oldest queued one.
const FollowUp* next_follow_up(const std::vector<FollowUp>& pending) {
    for (const FollowUp& item : pending) {
        if (item.steer) return &item;
    }
    return pending.empty() ? nullptr : &pending.front();
}
}  // namespace

void Agent::run(Session& session, const std::string& request, const CancelToken& cancel) {
    std::string input = request;
    if (to_lower(trim(request)) == "/queue") {
        const FollowUp* next = next_follow_up(session.pending_input);
        if (!next) {
            session.state = "Failed";
            session.detail = "No messages are queued.";
            return;
        }
        input = next->text;
        const std::string id = next->id;
        session.pending_input.erase(std::remove_if(session.pending_input.begin(), session.pending_input.end(), [&](const FollowUp& item) { return item.id == id; }), session.pending_input.end());
    }
    {
        std::lock_guard<std::mutex> guard(inbox_lock_);
        accepting_ = true;
    }
    while (true) {
        // Each request has its own stop signal: steering sets it, and so does the caller's stop.
        const CancelToken turn = make_cancel();
        {
            std::lock_guard<std::mutex> guard(inbox_lock_);
            turn_cancel_ = turn;
            // A steering message that arrived between requests interrupts this one at once.
            for (const FollowUp& item : inbox_) {
                if (item.steer) turn->store(true);
            }
        }
        std::atomic<bool> finished{false};
        std::thread relay([&] {
            while (!finished.load()) {
                if (is_cancelled(cancel)) { turn->store(true); break; }
                std::this_thread::sleep_for(std::chrono::milliseconds(25));
            }
        });
        run_turn(session, input, turn);
        finished.store(true);
        relay.join();

        const bool stopping = is_cancelled(cancel);
        bool continuing = false, steering = false;
        std::vector<FollowUp> arrived;
        {
            std::lock_guard<std::mutex> guard(inbox_lock_);
            turn_cancel_.reset();
            arrived.swap(inbox_);
            session.pending_input.insert(session.pending_input.end(), arrived.begin(), arrived.end());
            if (session.pending_input.size() > kFollowUpLimit) session.pending_input.erase(session.pending_input.begin(), session.pending_input.end() - static_cast<std::ptrdiff_t>(kFollowUpLimit));
            const FollowUp* next = stopping ? nullptr : next_follow_up(session.pending_input);
            continuing = next && (next->steer || session.state == "Completed");
            if (continuing) {
                input = next->text;
                steering = next->steer;
                const std::string id = next->id;
                session.pending_input.erase(std::remove_if(session.pending_input.begin(), session.pending_input.end(), [&](const FollowUp& item) { return item.id == id; }), session.pending_input.end());
            } else {
                accepting_ = false;
            }
        }
        if (!arrived.empty() || continuing) store_.save(session);
        if (!continuing) return;
        emit_({AgentEvent::Kind::FollowUp, input, {}, std::make_shared<const Session>(session)});
        emit_({AgentEvent::Kind::Status, steering ? "Steering" : "Starting the queued message", {}});
    }
}

void Agent::run_turn(Session& session, const std::string& request, const CancelToken& cancel) {
    const std::string input = trim(request);
    if (input.empty() || utf8_length(input) > 16000) {
        session.state = "Failed";
        session.detail = "Describe a request in 1–16,000 characters.";
        return;
    }
    std::optional<Workspace> workspace;
    try {
        workspace.emplace(session.workspace);
    } catch (const Error&) {
        session.state = "Failed";
        session.detail = "The project folder " + session.workspace + " is no longer available.";
        return;
    }
    if (session.title == "New session") session.title = utf8_length(input) > 55 ? utf8_prefix(input, 55) + "…" : input;
    const std::vector<ToolSpec> offered = offered_tools();
    std::string prompt = system_prompt(session);
    {
        std::vector<std::string> names;
        for (const McpServer& server : mcp::enabled(store_.home())) names.push_back(server.name);
        if (!names.empty()) prompt += "\nThe user registered these MCP servers: " + join(names, ", ") + ". Use mcp_tools to see what one offers before calling it.";
    }
    if (!session.messages.empty() && session.messages.front().role == "system") session.messages.front().content = prompt;
    else session.messages.insert(session.messages.begin(), ChatMessage{"system", prompt, {}, ""});
    // A note from undo or redo travels with the next request, so the conversation keeps alternating user and assistant turns.
    // A custom command is expanded and the selected agent's instructions are added; the session still shows what was typed.
    const std::string prepared = customization::prepare(store_.home(), session, input);
    session.messages.push_back({"user", (session.pending_note.empty() ? "" : session.pending_note + "\n\n") + prepared, {}, ""});
    session.pending_note.clear();
    Turn turn;
    turn.request = input;
    turn.first_activity = session.activities.size();
    session.turns.push_back(std::move(turn));
    session.state = "Running";
    session.detail.clear();
    store_.save(session);

    try {
        bool finished = false;
        const int window = model_.context_window();
        for (int step = 0; step < kActionLimit; step++) {
            check_cancel(cancel);
            if (compaction::needed(session, window)) {
                emit_({AgentEvent::Kind::Status, "Compacting the conversation", {}});
                if (compaction::compact(session, model_, 6, cancel)) store_.save(session);
                else session.prompt_tokens = 0;
            }
            ModelReply reply = model_.complete(session.messages, offered, [this](const std::string& text) { emit_({AgentEvent::Kind::Text, text, {}}); }, cancel);
            // A call the model wrote as text instead of through the tool-call channel is recovered.
            if (reply.tool_calls.empty()) {
                if (auto written = recover_tool_call(reply.text, offered)) reply.tool_calls.push_back(*written);
            }
            if (reply.prompt_tokens > 0) session.prompt_tokens = reply.prompt_tokens;
            session.messages.push_back({"assistant", reply.text, reply.tool_calls, ""});
            if (reply.tool_calls.empty()) {
                session.turns.back().answer = reply.text;
                finished = true;
                break;
            }
            for (const ToolCall& call : reply.tool_calls) {
                check_cancel(cancel);
                const std::string result = perform(call, *workspace, session, cancel);
                session.messages.push_back({"tool", result, {}, call.id});
                store_.save(session);
            }
        }
        session.state = finished ? "Completed" : "Stopped";
        session.detail = finished ? "" : "Stopped after " + std::to_string(kActionLimit) + " steps. Send another message to continue.";
    } catch (const Cancelled&) {
        session.state = "Stopped";
        session.detail = "Stopped. Completed edits are kept.";
    } catch (const std::exception& error) {
        session.state = "Failed";
        session.detail = error.what();
    }
    close_open_calls(session);
    store_.save(session);
}

void Agent::close_open_calls(Session& session) {
    std::set<std::string> answered;
    for (const ChatMessage& message : session.messages) {
        if (!message.tool_call_id.empty()) answered.insert(message.tool_call_id);
    }
    for (size_t index = 0; index < session.messages.size(); index++) {
        const std::vector<ToolCall> calls = session.messages[index].tool_calls;
        size_t insert_at = index + 1;
        while (insert_at < session.messages.size() && session.messages[insert_at].role == "tool") insert_at++;
        for (const ToolCall& call : calls) {
            if (answered.count(call.id)) continue;
            session.messages.insert(session.messages.begin() + static_cast<std::ptrdiff_t>(insert_at), ChatMessage{"tool", "Not run: the request was stopped first.", {}, call.id});
            answered.insert(call.id);
            insert_at++;
        }
    }
}

bool Agent::allowed(const ApprovalRequest& request, Session& session) {
    if (ApprovalRules(store_.home(), session.workspace).allows(request.tool, request.subject)) return true;
    session.state = "Awaiting review";
    store_.save(session);
    const bool answer = approve_ && approve_(request);
    session.state = "Running";
    return answer;
}

std::string Agent::perform(const ToolCall& call, const Workspace& workspace, Session& session, const CancelToken& cancel) {
    const json parsed = json::parse(call.arguments, nullptr, false);
    const json arguments = parsed.is_object() ? parsed : json::object();
    auto text = [&](const char* key) -> std::optional<std::string> {
        if (arguments.contains(key) && arguments[key].is_string()) return arguments[key].get<std::string>();
        return std::nullopt;
    };
    Activity activity;
    activity.tool = call.name;
    activity.subject = text("command").value_or(text("path").value_or(text("query").value_or("")));
    if (activity.subject.empty() && text("server")) activity.subject = *text("server") + (text("tool") ? "/" + *text("tool") : text("uri") ? "/" + *text("uri") : "");
    session.activities.push_back(activity);
    const size_t slot = session.activities.size() - 1;
    emit_({AgentEvent::Kind::ActivityStarted, "", activity});
    auto finish = [&](const std::string& state, const std::string& result) {
        activity.state = state;
        activity.result = result.size() > 8000 ? utf8_prefix(result, 8000) : result;
        session.activities[slot] = activity;
        emit_({AgentEvent::Kind::ActivityFinished, "", activity});
        return state == "completed" ? result : "Error: " + result;
    };
    const bool read_only = session.ask_only || session.permission == Permission::ReadOnly;
    try {
        if (call.name == "list_files") {
            const FileListing listing = workspace.files(text("path").value_or("."), 800);
            return finish("completed", listing.paths.empty() ? "No files." : join(listing.paths, "\n") + (listing.truncated ? "\n[Listing clipped at 800 files.]" : ""));
        }
        if (call.name == "read_file") {
            const auto path = text("path");
            if (!path) throw Error("read_file needs a path.");
            const std::vector<std::string> lines = split(workspace.read(*path), '\n');
            std::string numbered;
            for (size_t i = 0; i < lines.size(); i++) numbered += std::to_string(i + 1) + "\t" + lines[i] + (i + 1 < lines.size() ? "\n" : "");
            return finish("completed", numbered.size() > 60000 ? utf8_prefix(numbered, 60000) + "\n[File clipped.]" : numbered);
        }
        if (call.name == "search_files") {
            const auto query = text("query");
            if (!query) throw Error("search_files needs a query.");
            std::string found;
            for (const SearchMatch& match : workspace.search(*query)) found += match.path + ":" + std::to_string(match.line) + ": " + match.text + "\n";
            return finish("completed", found.empty() ? "No matches." : found);
        }
        if (call.name == "edit_file" || call.name == "write_file") {
            if (read_only) throw Error("This session is read-only. Tell the user what you would change instead.");
            const auto path = text("path");
            if (!path) throw Error(call.name + " needs a path.");
            std::optional<std::string> before;
            if (workspace.exists(*path)) before = workspace.read(*path);
            std::string after;
            if (call.name == "edit_file") {
                const auto old_text = text("old_text"), new_text = text("new_text");
                if (!before || !old_text || !new_text || old_text->empty()) throw Error("edit_file needs an existing file, old_text and new_text.");
                const size_t occurrences = count_occurrences(*before, *old_text);
                if (occurrences == 0) throw Error("old_text was not found in " + *path + ". Read the file again and copy the passage exactly.");
                if (occurrences > 1) throw Error("old_text matches " + std::to_string(occurrences) + " places in " + *path + ". Include more surrounding lines so it matches once.");
                after = *before;
                after.replace(after.find(*old_text), old_text->size(), *new_text);
            } else {
                const auto content = text("content");
                if (!content) throw Error("write_file needs content.");
                after = *content;
            }
            if (session.permission == Permission::ReviewEdits) {
                std::string rows;
                int shown = 0;
                for (const DiffLine& row : diff_lines(before, after)) {
                    if (row.kind == ' ' || shown >= 60) continue;
                    rows += std::string(1, row.kind) + " " + row.text + "\n";
                    shown++;
                }
                if (!allowed({call.name, (before ? "Edit " : "Create ") + *path, rows, *path}, session)) {
                    return finish("denied", "The user declined this edit. Do not retry it; ask what they want instead.");
                }
            }
            workspace.write(*path, after);
            // Keep the first "before" of each file per request, so undo returns to the state the request started from.
            Turn& turn = session.turns.back();
            auto existing = std::find_if(turn.changes.begin(), turn.changes.end(), [&](const FileChange& change) { return change.path == *path; });
            if (existing != turn.changes.end()) {
                existing->after = after;
                existing->after_blob.clear();
                existing->after_missing = false;
            } else {
                turn.changes.push_back({*path, before, after});
            }
            const DiffCounts counts = diff_counts(before, after);
            return finish("completed", std::string(before ? "Edited " : "Created ") + *path + " (+" + std::to_string(counts.added) + " −" + std::to_string(counts.removed) + ").");
        }
        if (call.name == "run_shell") {
            if (read_only) throw Error("This session is read-only. Commands are not available.");
            const auto command = text("command");
            if (!command) throw Error("run_shell needs a command.");
            const bool sandboxed = session.permission == Permission::Auto;
            if (!sandboxed) {
                const std::string detail = "Runs in " + workspace.root() + " with your account and environment. It is not sandboxed. Limit: 120 seconds.";
                if (!allowed({call.name, *command, detail, *command}, session)) {
                    return finish("denied", "The user declined this command. Do not retry it; ask what they want instead.");
                }
            }
            emit_({AgentEvent::Kind::Status, sandboxed ? "Running sandboxed command" : "Running command", {}});
            // The project is read before and after, so whatever the command changed can be shown and undone.
            const snapshot::Map before = snapshot::capture(workspace, snapshot_);
            CommandResult result;
            try {
                result = shell::run(*command, workspace, sandboxed, 120, cancel);
            } catch (...) {
                record_command_changes(before, workspace, session);
                throw;
            }
            const std::vector<std::string> changed = record_command_changes(before, workspace, session);
            std::string output = "exit " + std::to_string(result.exit_code) + "\n" + result.output;
            if (!changed.empty()) {
                const std::vector<std::string> shown(changed.begin(), changed.begin() + static_cast<std::ptrdiff_t>(std::min<size_t>(12, changed.size())));
                output += "\n[Files changed by this command: " + join(shown, ", ") + (changed.size() > 12 ? " and " + std::to_string(changed.size() - 12) + " more" : "") + "]";
            }
            return finish(result.exit_code == 0 && !result.timed_out ? "completed" : "failed", output);
        }
        if (call.name == "mcp_tools" || call.name == "mcp_resources" || call.name == "mcp_read" || call.name == "mcp_call") {
            // MCP servers run outside the sandbox and outside the project, so every call that can act asks first.
            const std::vector<McpServer> servers = mcp::enabled(store_.home());
            const std::string wanted = text("server").value_or("");
            const auto server = std::find_if(servers.begin(), servers.end(), [&](const McpServer& item) { return item.name == wanted; });
            if (server == servers.end()) {
                std::vector<std::string> registered;
                for (const McpServer& item : servers) registered.push_back(item.name);
                throw Error("No MCP server named " + wanted + " is registered. Registered: " + join(registered, ", "));
            }
            if (call.name == "mcp_call") {
                const std::string tool = text("tool").value_or("");
                if (!server->allows_tool(tool)) throw Error("Choose one of the tools allowed for " + server->name + ": " + join(server->tools, ", "));
                const json parsed_arguments = json::parse(text("arguments_json").value_or("{}"), nullptr, false);
                if (!parsed_arguments.is_object()) throw Error("arguments_json must be a JSON object.");
                if (!allowed({call.name, "Call " + server->name + " / " + tool, server->transport() + "\n" + parsed_arguments.dump(2), server->name + "/" + tool}, session)) {
                    return finish("denied", "The user declined this MCP call. Do not retry it; ask what they want instead.");
                }
                emit_({AgentEvent::Kind::Status, "Calling " + server->name + " / " + tool, {}});
                return finish("completed", mcp::request(*server, mcp::Operation::Call, tool, parsed_arguments, cancel));
            }
            const std::optional<std::string> uri = text("uri");
            if (call.name == "mcp_read" && !uri) throw Error("mcp_read needs a uri.");
            // Starting a program, or reading a resource, asks first; listing what an HTTP server offers does not.
            if (server->is_program() || call.name == "mcp_read") {
                const std::string title = call.name == "mcp_read" ? "Read " + *uri + " from " + server->name : "Start " + server->name + " to list what it offers";
                if (!allowed({call.name, title, server->transport(), server->name + (uri ? "/" + *uri : "")}, session)) {
                    return finish("denied", "The user declined. Do not retry it; ask what they want instead.");
                }
            }
            const mcp::Operation operation = call.name == "mcp_read" ? mcp::Operation::Read : call.name == "mcp_resources" ? mcp::Operation::Resources : mcp::Operation::Tools;
            return finish("completed", mcp::request(*server, operation, uri.value_or(""), json::object(), cancel));
        }
        std::vector<std::string> names;
        for (const ToolSpec& tool : offered_tools()) names.push_back(tool.name);
        throw Error("Unknown tool " + call.name + ". Use one of: " + join(names, ", "));
    } catch (const Cancelled&) {
        finish("failed", "Stopped by the user.");
        throw;
    } catch (const std::exception& error) {
        return finish("failed", error.what());
    }
}

std::vector<std::string> Agent::record_command_changes(const snapshot::Map& before, const Workspace& workspace, Session& session) {
    snapshot_ = snapshot::capture(workspace, before);
    const BlobStore blobs(store_.home());
    // Text is kept in the session like an edit; anything binary or over the text limit goes to the blob store.
    struct Side { std::optional<std::string> text; std::string blob; bool saved = true; };
    auto side = [&](const std::shared_ptr<const std::string>& data) {
        Side result;
        if (!data) return result;
        if (data->size() <= Workspace::kMaxFileBytes && is_valid_utf8(*data)) {
            result.text = *data;
            return result;
        }
        try {
            result.blob = blobs.put(*data);
        } catch (const std::exception&) {
            result.saved = false;  // could not be saved, so it cannot be undone
        }
        return result;
    };
    Turn& turn = session.turns.back();
    std::vector<std::string> paths;
    for (const snapshot::Change& change : snapshot::changes(before, snapshot_)) {
        const Side now = side(change.after);
        if (!now.saved) continue;
        auto existing = std::find_if(turn.changes.begin(), turn.changes.end(), [&](const FileChange& item) { return item.path == change.path; });
        if (existing != turn.changes.end()) {
            existing->after = now.text.value_or("");
            existing->after_blob = now.blob;
            existing->after_missing = !change.after;
        } else {
            const Side old = side(change.before);
            if (!old.saved) continue;
            FileChange entry{change.path, old.text, now.text.value_or("")};
            entry.before_blob = old.blob;
            entry.after_blob = now.blob;
            entry.after_missing = !change.after;
            entry.by_command = true;
            turn.changes.push_back(std::move(entry));
        }
        paths.push_back(change.path);
    }
    return paths;
}

std::optional<UndoResult> Agent::undo_latest(Session& session, SessionStore& store, bool redo) {
    auto found = std::find_if(session.turns.rbegin(), session.turns.rend(), [&](const Turn& turn) { return !turn.changes.empty() && turn.undone == redo; });
    if (found == session.turns.rend()) return std::nullopt;
    const Workspace workspace(session.workspace);
    const BlobStore blobs(store.home());
    UndoResult result;
    for (const FileChange& change : found->changes) {
        // Each side is the file's bytes, or nothing when the file did not exist there.
        std::optional<std::string> before = change.before, after = change.after, current;
        bool readable = true;
        if (!change.before_blob.empty()) { before = blobs.get(change.before_blob); readable = readable && before.has_value(); }
        if (change.after_missing) after.reset();
        else if (!change.after_blob.empty()) { after = blobs.get(change.after_blob); readable = readable && after.has_value(); }
        try {
            current = workspace.data(change.path);
        } catch (const std::exception&) {
            readable = false;
        }
        const std::optional<std::string>& expected = redo ? before : after;
        const std::optional<std::string>& target = redo ? after : before;
        if (!readable || current != expected) {
            result.skipped.push_back(change.path);
            continue;
        }
        if (target) workspace.write_data(change.path, *target);
        else workspace.remove(change.path);
        result.restored.push_back(change.path);
    }
    found->undone = !redo;
    session.pending_note = (redo ? std::string("The user reapplied the changes of an earlier request.")
                                 : "The user undid the file changes of the request \"" + utf8_prefix(found->request, 200) + "\".") +
                           " Read affected files again before relying on what you saw earlier.";
    store.save(session);
    return result;
}

}  // namespace mason
