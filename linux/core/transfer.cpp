#include "transfer.hpp"

#include <regex>

#include "agent.hpp"

namespace mason::transfer {

const char* const kHelp =
    "/fork NUMBER [files]     A new session from before request NUMBER; \"files\" also puts the project back as it was then\n"
    "/export FILE [sanitized] Save this session as a file; \"sanitized\" removes the project's path, likely secrets and file contents\n"
    "/import FILE             Add an exported session to this project";

namespace {

/// The position of the user message that began turn `index`, counted from the end so that messages a compaction
/// replaced do not shift it. Nothing when that request has been summarized away.
std::optional<size_t> message_index(const Session& session, size_t index) {
    size_t remaining = session.turns.size() - index;
    for (size_t position = session.messages.size(); position-- > 0;) {
        if (session.messages[position].role != "user") continue;
        if (--remaining == 0) return position;
    }
    return std::nullopt;
}

/// The text with the project's path and anything that looks like a key or password taken out.
std::string clean(const std::string& text, const std::string& root) {
    static const std::regex tokens(R"((sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[abpr]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}))");
    static const std::regex named(R"(((?:api[_-]?key|token|secret|password)\s{0,3}[:=]\s{0,3}["']?)([^\s"']{6,}))", std::regex::icase);
    std::string moved = root.empty() ? text : replace_all(text, root, "<workspace>");
    moved = std::regex_replace(moved, tokens, "<redacted>");
    return std::regex_replace(moved, named, "$1<redacted>");
}

}  // namespace

SessionFork fork(Session& source, SessionStore& store, size_t index, bool restore_files) {
    if (source.state == "Running") throw Error("Stop the request that is running before forking.");
    if (index >= source.turns.size()) throw Error("Give the number of a request in this session.");
    const std::optional<size_t> cut = message_index(source, index);
    if (!cut) throw Error("That request was replaced by a summary when the session was compacted. Fork from a later one.");
    const Turn turn = source.turns[index];
    SessionFork made;
    made.request = turn.request;
    if (restore_files) {
        while (true) {
            size_t latest = source.turns.size();
            for (size_t i = source.turns.size(); i-- > 0;) {
                if (!source.turns[i].changes.empty() && !source.turns[i].undone) { latest = i; break; }
            }
            if (latest == source.turns.size() || latest < index) break;
            const std::optional<UndoResult> result = Agent::undo_latest(source, store);
            if (!result) break;
            made.restored.insert(made.restored.end(), result->restored.begin(), result->restored.end());
            made.skipped.insert(made.skipped.end(), result->skipped.begin(), result->skipped.end());
        }
    }
    Session copy = store.create(source.workspace, source.permission == Permission::Auto ? Permission::ReviewEdits : source.permission);
    copy.title = (utf8_length(source.title) > 46 ? utf8_prefix(source.title, 46) + "…" : source.title) + " · fork";
    copy.ask_only = source.ask_only;
    copy.agent = source.agent;
    copy.messages.assign(source.messages.begin(), source.messages.begin() + static_cast<std::ptrdiff_t>(*cut));
    copy.turns.assign(source.turns.begin(), source.turns.begin() + static_cast<std::ptrdiff_t>(index));
    copy.activities.assign(source.activities.begin(), source.activities.begin() + static_cast<std::ptrdiff_t>(std::min(turn.first_activity, source.activities.size())));
    copy.archived = source.archived;
    copy.compactions = source.compactions;
    copy.milestones = source.milestones;
    copy.detail = "Forked before “" + (utf8_length(turn.request) > 50 ? utf8_prefix(turn.request, 50) + "…" : turn.request) + "”. Edit the request and send it.";
    if (restore_files) {
        copy.detail += " Files put back: " + std::to_string(made.restored.size()) + (made.skipped.empty() ? "." : "; changed since and left alone: " + join(made.skipped, ", ") + ".");
    }
    store.save(copy);
    made.session = copy;
    return made;
}

std::string export_session(const Session& session, bool sanitize) {
    Session copy = session;
    copy.desktop.reset();
    copy.pending_input.clear();
    if (sanitize) {
        const std::string root = session.workspace;
        copy.workspace = "<workspace>";
        copy.title = clean(copy.title, root);
        copy.detail = clean(copy.detail, root);
        for (std::vector<ChatMessage>* list : {&copy.messages, &copy.archived}) {
            for (ChatMessage& message : *list) {
                message.content = clean(message.content, root);
                for (ToolCall& call : message.tool_calls) call.arguments = clean(call.arguments, root);
            }
        }
        for (Activity& activity : copy.activities) {
            activity.subject = clean(activity.subject, root);
            activity.result = clean(activity.result, root);
        }
        for (Turn& turn : copy.turns) {
            turn.request = clean(turn.request, root);
            turn.answer = clean(turn.answer, root);
            turn.changes.clear();
            if (turn.verification) turn.verification->output = clean(turn.verification->output, root);
        }
        copy.comments.clear();
        copy.pending_note.clear();
    }
    const json exported = {{"format", kFormat}, {"app", "Mason Agent"}, {"at", now_iso()}, {"sanitized", sanitize}, {"session", to_json(copy)}};
    return exported.dump(1, ' ', false, json::error_handler_t::replace);
}

Session import_session(const std::string& data, SessionStore& store, const std::string& workspace) {
    if (data.size() > kMaxBytes) throw Error("The session file is larger than 24 MiB.");
    const json exported = json::parse(data, nullptr, false);
    if (!exported.is_object() || !exported.contains("session") || !exported["session"].is_object()) throw Error("That file is not a Mason session export.");
    if (exported.value("format", 0) != kFormat) throw Error("That session export is in a format this version does not read.");
    Session source;
    try {
        source = session_from_json(exported["session"]);
    } catch (const std::exception&) {
        throw Error("That file is not a Mason session export.");
    }
    Session target = store.create(workspace);
    target.title = source.title + " · imported";
    target.messages = source.messages;
    target.activities = source.activities;
    target.turns = source.turns;
    for (Turn& turn : target.turns) {
        turn.changes.clear();
        turn.undone = false;
    }
    target.ask_only = source.ask_only;
    target.agent = source.agent;
    target.archived = source.archived;
    target.compactions = source.compactions;
    target.detail = std::string("Imported ") + (exported.value("sanitized", false) ? "a sanitized" : "a") + " session. File history and undo stay with the original.";
    store.save(target);
    return target;
}

}  // namespace mason::transfer
