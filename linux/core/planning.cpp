#include "planning.hpp"

#include <algorithm>
#include <set>

#include "agent.hpp"

namespace mason {

std::string sha256_hex(const std::string& data) {
    Sha256 hash;
    hash.update(data.data(), data.size());
    return hash.hex();
}

namespace {

std::vector<std::string> lines_of(std::string text) {
    text = replace_all(std::move(text), "\r\n", "\n");
    std::vector<std::string> lines = split(text, '\n');
    if (!lines.empty() && lines.back().empty()) lines.pop_back();
    return lines;
}

std::vector<std::string> rows(const std::string& text) { return split(text, '\n'); }

/// The first word of a text and what follows it.
std::pair<std::string, std::string> head(const std::string& text) {
    const std::string trimmed = trim(text);
    const size_t space = trimmed.find(' ');
    if (space == std::string::npos) return {trimmed, ""};
    return {trimmed.substr(0, space), trim(trimmed.substr(space + 1))};
}

std::optional<int> number(const std::string& text) {
    const std::string trimmed = trim(text);
    if (trimmed.empty() || trimmed.size() > 6 || !std::all_of(trimmed.begin(), trimmed.end(), [](unsigned char c) { return std::isdigit(c); })) return std::nullopt;
    return std::stoi(trimmed);
}

}  // namespace

// ---- comments --------------------------------------------------------------------------------------------------

namespace review {

const char* const kHelp =
    "/comment PATH:LINE NOTE     Pin a note to a line; it is sent with your next request\n"
    "/comment                    List the notes waiting to be sent\n"
    "/comment remove NUMBER      Drop one    ·    /comment clear   drops them all\n"
    "/comment send               Send the notes now, as a request of their own";

std::string location(const ReviewComment& comment) {
    return comment.path + " · " + (comment.original ? "original" : "updated") + " line " + std::to_string(comment.line);
}

ReviewComment comment(const Workspace& workspace, const std::string& path, int line, const std::string& note, const std::optional<std::string>& removed) {
    const std::string trimmed = trim(note);
    if (trimmed.empty() || utf8_length(trimmed) > 2000) throw Error("Write feedback of 1–2,000 characters.");
    const std::optional<std::string> data = workspace.data(path);
    if (!data) throw Error(path + " does not exist.");
    ReviewComment made;
    made.path = path;
    made.revision = sha256_hex(*data);
    made.line = line;
    made.note = trimmed;
    if (removed) {
        made.original = true;
        made.quote = *removed;
        return made;
    }
    const std::vector<std::string> lines = lines_of(*data);
    if (line < 1 || static_cast<size_t>(line) > lines.size()) throw Error("Choose a line between 1 and " + std::to_string(lines.size()) + " of " + path + ".");
    made.quote = lines[static_cast<size_t>(line) - 1];
    return made;
}

std::string request(const std::string& draft, const std::vector<ReviewComment>& comments) {
    const std::string typed = trim(draft);
    if (comments.empty()) return typed;
    std::vector<std::string> parts = {typed.empty() ? "Address these review comments." : typed};
    for (const ReviewComment& item : comments) parts.push_back(location(item) + " (" + utf8_prefix(trim(item.quote), 160) + ") — " + item.note);
    return join(parts, "\n");
}

void validate(const Workspace& workspace, const std::vector<ReviewComment>& comments) {
    for (const ReviewComment& item : comments) {
        std::optional<std::string> data;
        try {
            data = workspace.data(item.path);
        } catch (const std::exception&) {
        }
        if (!data) throw Error(item.path + " is missing. Remove its comment or restore the file.");
        if (sha256_hex(*data) != item.revision) throw Error(location(item) + " changed since the comment was written. Remove the comment or write it again.");
    }
}

std::string command(const std::string& rest, const Workspace& workspace, Session& session) {
    const auto [word, after] = head(rest);
    const std::string verb = to_lower(word);
    std::vector<ReviewComment>& comments = session.comments;
    if (verb.empty()) {
        if (comments.empty()) return std::string("No comments are waiting.\n") + kHelp;
        std::string text;
        for (size_t i = 0; i < comments.size(); i++) text += std::to_string(i + 1) + ". " + location(comments[i]) + " — " + comments[i].note + "\n";
        return text + "They are sent with your next request.";
    }
    if (verb == "help") return kHelp;
    if (verb == "clear") {
        const size_t count = comments.size();
        comments.clear();
        return "Dropped " + std::to_string(count) + (count == 1 ? " comment." : " comments.");
    }
    if (verb == "remove") {
        const std::optional<int> index = number(after);
        if (!index || *index < 1 || static_cast<size_t>(*index) > comments.size()) throw Error("Give the number of a comment. /comment lists them.");
        const std::string where = location(comments[static_cast<size_t>(*index) - 1]);
        comments.erase(comments.begin() + (*index - 1));
        return "Dropped the comment on " + where + ".";
    }
    const size_t colon = word.rfind(':');
    const std::optional<int> line = colon == std::string::npos ? std::nullopt : number(word.substr(colon + 1));
    if (!line || after.empty()) throw Error("Write it as /comment PATH:LINE NOTE, for example /comment src/app.py:12 Use the helper here.");
    if (comments.size() >= kLimit) throw Error("At most " + std::to_string(kLimit) + " comments can wait at once.");
    const ReviewComment made = comment(workspace, word.substr(0, colon), *line, after);
    comments.push_back(made);
    return "Noted on " + location(made) + ": " + utf8_prefix(trim(made.quote), 100);
}

}  // namespace review

// ---- milestones ------------------------------------------------------------------------------------------------

namespace milestones {

const char* const kHelp =
    "/milestone                                  List the plan and where each milestone stands\n"
    "/milestone add TITLE | FILES [| after N,N] [| workflow NAME] [| decision TEXT]\n"
    "                                            Add one. FILES are project paths separated by commas\n"
    "/milestone check NUMBER [WHAT YOU SAW]      Run its workflow, or record what you observed\n"
    "/milestone remove NUMBER";

namespace {

const Milestone* find(const std::vector<Milestone>& plan, const std::string& id) {
    for (const Milestone& item : plan) {
        if (item.id == id) return &item;
    }
    return nullptr;
}

ProjectWorkflow workflow_named(const Workspace& workspace, const std::string& name) {
    const std::map<std::string, ProjectWorkflow> known = workflows::load(workspace);
    const auto found = known.find(name);
    if (found == known.end()) throw Error("mason.workflows.json has no workflow named " + name + ".");
    return found->second;
}

/// What a receipt vouches for: the milestone's wording, the content of its files, its prerequisites' receipts and its
/// workflow. Any of them changing means the milestone needs checking again.
std::string revision(const Workspace& workspace, const Milestone& item, const std::vector<Milestone>& plan) {
    std::vector<std::string> parts = {item.title, item.decision};
    for (const std::string& path : item.files) {
        const std::optional<std::string> data = workspace.data(path);
        if (!data) throw Error(path + " is missing.");
        parts.push_back(path + "=" + sha256_hex(*data));
    }
    for (const std::string& id : item.depends_on) {
        const Milestone* parent = find(plan, id);
        parts.push_back("after " + (parent && parent->receipt ? parent->receipt->id : std::string("-")));
    }
    if (!item.workflow.empty()) parts.push_back("workflow " + workflows::command_line(workflow_named(workspace, item.workflow)));
    return sha256_hex(join(parts, std::string(1, '\x01')));
}

}  // namespace

std::vector<Milestone> put(const Workspace& workspace, const std::vector<Milestone>& current, Milestone item) {
    item.title = trim(item.title);
    item.decision = trim(item.decision);
    item.workflow = trim(item.workflow);
    if (item.title.empty() || utf8_length(item.title) > 240 || utf8_length(item.decision) > 2000 || item.files.empty() || item.files.size() > 32 || item.depends_on.size() > 32 ||
        item.workflow.size() > 50) {
        throw Error("Use a title, 1–32 project files and a decision under 2,000 characters.");
    }
    std::set<std::string> files;
    const fs::path root = workspace.root();
    for (const std::string& path : item.files) {
        const std::string relative = fs::path(workspace.resolve(path)).lexically_relative(root).generic_string();
        if (relative.empty() || relative == "." || starts_with(relative, "..")) throw Error(path + " is not a file in the project.");
        files.insert(relative);
    }
    item.files.assign(files.begin(), files.end());
    const std::set<std::string> dependencies(item.depends_on.begin(), item.depends_on.end());
    item.depends_on.assign(dependencies.begin(), dependencies.end());
    item.receipt.reset();
    std::vector<Milestone> result = current;
    const auto prior = std::find_if(result.begin(), result.end(), [&](const Milestone& known) { return known.id == item.id; });
    if (prior != result.end()) {
        if (prior->title == item.title && prior->decision == item.decision && prior->workflow == item.workflow && prior->files == item.files && prior->depends_on == item.depends_on) return current;
        *prior = item;
    } else {
        result.push_back(item);
    }
    if (result.size() > kLimit) throw Error("A session holds up to " + std::to_string(kLimit) + " milestones.");
    std::set<std::string> visited, pending;
    std::function<void(const std::string&)> visit = [&](const std::string& id) {
        if (visited.count(id)) return;
        const Milestone* node = find(result, id);
        if (!node) throw Error("Choose a prerequisite that is in this plan.");
        if (!pending.insert(id).second) throw Error("These milestones depend on each other in a circle.");
        for (const std::string& dependency : node->depends_on) visit(dependency);
        pending.erase(id);
        visited.insert(id);
    };
    for (const Milestone& milestone : result) visit(milestone.id);
    return result;
}

std::vector<Milestone> remove(const std::string& id, const std::vector<Milestone>& plan) {
    std::vector<Milestone> result;
    for (Milestone item : plan) {
        if (item.id == id) continue;
        const auto found = std::find(item.depends_on.begin(), item.depends_on.end(), id);
        if (found != item.depends_on.end()) {
            item.depends_on.erase(found);
            item.receipt.reset();
        }
        result.push_back(std::move(item));
    }
    return result;
}

std::vector<MilestoneStatus> inspect(const Workspace& workspace, const std::vector<Milestone>& plan) {
    std::map<std::string, MilestoneStatus> states;
    std::set<std::string> visiting;
    std::function<MilestoneStatus(const Milestone&)> one = [&](const Milestone& item) -> MilestoneStatus {
        if (const auto saved = states.find(item.id); saved != states.end()) return saved->second;
        if (!visiting.insert(item.id).second) return {item, "Blocked", "Its prerequisites form a circle."};
        MilestoneStatus status{item, "Ready", "Awaiting evidence."};
        bool blocked = false;
        for (const std::string& id : item.depends_on) {
            const Milestone* parent = find(plan, id);
            if (!parent || !one(*parent).done()) blocked = true;
        }
        if (blocked) {
            status = {item, "Blocked", "A prerequisite still needs attention."};
        } else if (item.receipt) {
            try {
                status = revision(workspace, item, plan) == item.receipt->revision ? MilestoneStatus{item, item.receipt->manual ? "Reviewed" : "Verified", item.receipt->detail}
                                                                                    : MilestoneStatus{item, "Needs recheck", "A file, the workflow or a prerequisite changed."};
            } catch (const std::exception& error) {
                status = {item, "Needs recheck", error.what()};
            }
        }
        visiting.erase(item.id);
        states[item.id] = status;
        return status;
    };
    std::vector<MilestoneStatus> result;
    for (const Milestone& item : plan) result.push_back(one(item));
    return result;
}

MilestoneReceipt check(const Workspace& workspace, const Milestone& item, const std::vector<Milestone>& plan, bool allowed, bool sandboxed, const std::string& observation,
                       const CancelToken& cancel) {
    const auto blocked = [&] {
        for (const MilestoneStatus& status : inspect(workspace, plan)) {
            if (status.item.id == item.id) return status.state == "Blocked";
        }
        return false;
    };
    if (blocked()) throw Error("Complete the prerequisites first.");
    const std::string before = revision(workspace, item, plan);
    std::string detail = trim(observation);
    if (!item.workflow.empty()) {
        if (!allowed) throw Error("Mason runs a milestone's workflow in Auto, or when you have chosen Always Allow for the workflow " + item.workflow + ".");
        const CommandResult result = shell::run(workflows::command_line(workflow_named(workspace, item.workflow)), workspace, sandboxed, 120, cancel);
        if (result.exit_code != 0 || result.timed_out) throw Error("The workflow " + item.workflow + " exited " + std::to_string(result.exit_code) + ". " + utf8_suffix(result.output, 1800));
        detail = item.workflow + " · exit 0\n" + utf8_suffix(result.output, 2000);
    } else if (detail.empty() || utf8_length(detail) > 2000) {
        throw Error("Record what you observed in 1–2,000 characters.");
    }
    check_cancel(cancel);
    if (blocked() || revision(workspace, item, plan) != before) throw Error("The milestone's files changed during the check. Check it again.");
    MilestoneReceipt receipt;
    receipt.revision = before;
    receipt.detail = detail;
    receipt.manual = item.workflow.empty();
    return receipt;
}

std::string context(const Workspace& workspace, const std::vector<Milestone>& plan) {
    const std::vector<MilestoneStatus> status = inspect(workspace, plan);
    const MilestoneStatus* next = nullptr;
    for (const MilestoneStatus& row : status) {
        if (row.state == "Ready" || row.state == "Needs recheck") { next = &row; break; }
    }
    std::string summary;
    const auto add = [&](const MilestoneStatus& row) {
        const std::string line = "- " + row.item.title + " [" + row.state + "]" + (row.item.decision.empty() ? "" : " Decision: " + row.item.decision) + " Files: " + join(row.item.files, ", ") + "\n";
        if (summary.size() + line.size() > 5000) return false;
        summary += line;
        return true;
    };
    if (next) add(*next);
    for (const MilestoneStatus& row : status) {
        if (&row != next && !add(row)) break;
    }
    return "The user's saved milestones. Verified means a workflow passed; Reviewed means the user recorded what they saw. Next: " + (next ? next->item.title : std::string("none ready")) + "\n" + summary;
}

std::string list(const Workspace& workspace, const std::vector<Milestone>& plan) {
    if (plan.empty()) return std::string("No milestones yet.\n") + kHelp;
    std::vector<std::string> shown;
    const std::vector<MilestoneStatus> status = inspect(workspace, plan);
    for (size_t i = 0; i < status.size(); i++) {
        const MilestoneStatus& row = status[i];
        std::string text = std::to_string(i + 1) + ". [" + row.state + "] " + row.item.title + "  ·  " + join(row.item.files, ", ");
        std::vector<std::string> after;
        for (const std::string& id : row.item.depends_on) {
            for (size_t j = 0; j < plan.size(); j++) {
                if (plan[j].id == id) after.push_back(std::to_string(j + 1));
            }
        }
        if (!after.empty()) text += "  ·  after " + join(after, ", ");
        if (!row.item.workflow.empty()) text += "  ·  workflow " + row.item.workflow;
        shown.push_back(text + "\n   " + rows(row.reason).front());
    }
    return join(shown, "\n");
}

std::string command(const std::string& rest, const Workspace& workspace, Session& session, const fs::path& home, const CancelToken& cancel) {
    std::vector<Milestone>& plan = session.milestones;
    const auto [word, after] = head(rest);
    const std::string verb = to_lower(word);
    const auto numbered = [&](const std::string& text) {
        const std::optional<int> index = number(text);
        if (!index || *index < 1 || static_cast<size_t>(*index) > plan.size()) throw Error("Give the number of a milestone. /milestone lists them.");
        return static_cast<size_t>(*index) - 1;
    };
    if (verb.empty()) return list(workspace, plan);
    if (verb == "help") return kHelp;
    if (verb == "add") {
        std::vector<std::string> fields;
        for (const std::string& field : split(after, '|')) fields.push_back(trim(field));
        if (fields.size() < 2 || fields[1].empty()) throw Error("Write it as /milestone add TITLE | FILES, for example /milestone add Totals are right | src/totals.py, tests/test_totals.py");
        Milestone item;
        item.title = fields[0];
        for (const std::string& path : split(fields[1], ',', false)) {
            if (!trim(path).empty()) item.files.push_back(trim(path));
        }
        for (size_t i = 2; i < fields.size(); i++) {
            const auto [key, value] = head(fields[i]);
            if (to_lower(key) == "after") {
                for (const std::string& each : split(value, ',', false)) item.depends_on.push_back(plan[numbered(each)].id);
            } else if (to_lower(key) == "workflow") {
                workflow_named(workspace, value);
                item.workflow = value;
            } else if (to_lower(key) == "decision") {
                item.decision = value;
            } else {
                throw Error("After the files, use: after N,N · workflow NAME · decision TEXT.");
            }
        }
        plan = put(workspace, plan, item);
        return "Added milestone " + std::to_string(plan.size()) + ": " + trim(item.title);
    }
    if (verb == "remove") {
        const size_t index = numbered(after);
        const std::string title = plan[index].title;
        plan = remove(plan[index].id, plan);
        return "Removed " + title + ".";
    }
    if (verb == "check") {
        const auto [which, observation] = head(after);
        const size_t index = numbered(which);
        const Milestone item = plan[index];
        const bool automatic = session.permission == Permission::Auto;
        const bool allowed = automatic || ApprovalRules(home, session.workspace).allows("run_workflow", "workflow " + item.workflow);
        plan[index].receipt = check(workspace, item, plan, allowed, automatic, observation, cancel);
        return item.title + ": " + inspect(workspace, plan)[index].state + ".";
    }
    throw Error("Use /milestone, /milestone add, /milestone check or /milestone remove. /milestone help shows how.");
}

}  // namespace milestones

}  // namespace mason
