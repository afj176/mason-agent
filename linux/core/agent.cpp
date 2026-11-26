#include "agent.hpp"
#include "acceptance.hpp"
#include "browser.hpp"
#include "derived.hpp"
#include "navigation.hpp"
#include "planning.hpp"
#include "roles.hpp"

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
            {"search_files", "Search the project's files for text (case-insensitive). Returns up to 100 matches and, when there may be more, a cursor for search_next.", schema({{"query", {{"type", "string"}}}}, {"query"})},
            {"search_next", "Continue the latest search_files call from the cursor it returned, to see later files and matches.", schema({{"cursor", {{"type", "string"}}}}, {"cursor"})},
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

std::vector<ToolSpec> Agent::desktop_tools(const Session& session, bool available) {
    if (!available || !session.desktop) return {};
    const json text = {{"type", "string"}};
    std::vector<ToolSpec> list = {{"desktop_observe", "Read the control names and available actions from the window the user attached to this session.",
                                   json{{"type", "object"}, {"properties", json::object()}, {"required", json::array()}, {"additionalProperties", false}}}};
    list.push_back({"desktop_visual", "Look at a picture of the attached window with the vision model, for things its controls do not name: a canvas, a chart, a drawing. Describe what to look for, or one click or drag to make; a click or drag asks the user first and happens only if the window still looks the same.",
                    json{{"type", "object"}, {"properties", {{"task", text}}}, {"required", {"task"}}, {"additionalProperties", false}}});
    if (session.ask_only || session.permission == Permission::ReadOnly) return list;
    list.push_back({"desktop_act", "Ask the user to invoke (press) or set a control from the most recent observation of the attached window. Returns a fresh observation. value is the text to set; use an empty string with invoke.",
                    json{{"type", "object"}, {"properties", {{"operation", {{"type", "string"}, {"enum", {"invoke", "set"}}}}, {"id", text}, {"value", text}}}, {"required", {"operation", "id", "value"}}, {"additionalProperties", false}}});
    return list;
}

std::optional<std::pair<std::string, std::string>> Agent::perform_desktop(const ToolCall& call, const json& arguments, Session& session, const CancelToken& cancel) {
    using Outcome = std::pair<std::string, std::string>;
    if (call.name != "desktop_observe" && call.name != "desktop_act" && call.name != "desktop_visual") return std::nullopt;
    if (!session.desktop) throw Error("No window is attached to this session.");
    const std::shared_ptr<DesktopAutomation> automation = desktop_automation();
    if (!automation) throw Error("This build cannot operate windows on this computer.");
    if (const std::string problem = automation->unavailable(); !problem.empty()) throw Error(problem);
    const DesktopWindow window = *session.desktop;
    if (call.name == "desktop_visual") {
        const std::string task = trim(arguments.contains("task") && arguments["task"].is_string() ? arguments["task"].get<std::string>() : "");
        if (task.empty() || utf8_length(task) > 2000) throw Error("desktop_visual needs a task of up to 2,000 characters.");
        std::shared_ptr<DesktopVision> seer = vision_;
        if (!seer) {
            // The model chosen for pictures, or else the one doing the work.
            const std::optional<ModelEndpoint> chosen = roles::load(store_.home()).visual;
            if (const std::optional<ModelEndpoint> endpoint = chosen ? chosen : roles::endpoint_of(model_)) seer = std::make_shared<ModelVision>(*endpoint);
        }
        if (!seer) throw Error("No vision model is available to look at the window.");
        const std::optional<DesktopFrame> frame = automation->frame(window);
        if (!frame) throw Error("This computer cannot take a picture of that window.");
        emit_({AgentEvent::Kind::Status, "Looking at " + window.app, {}});
        const PixelDecision decision = seer->see(task, *frame, cancel);
        if (decision.action == "none") return Outcome{"completed", "Seen in " + desktop::label(window) + ": " + decision.observation};
        if (session.ask_only || session.permission == Permission::ReadOnly) throw Error("This session is read-only. It can look at the window but not click in it. The model saw: " + decision.observation);
        const std::string at = "(" + std::to_string(decision.x) + ", " + std::to_string(decision.y) + ")";
        const std::string what = decision.action == "click" ? "Click at " + at : "Drag from " + at + " to (" + std::to_string(decision.end_x) + ", " + std::to_string(decision.end_y) + ")";
        if (!approve_({call.name, what + " in " + desktop::label(window), "The vision model saw: " + decision.observation + "\nThis acts on another app's window, outside the project and outside the sandbox.", "window " + window.app})) {
            return Outcome{"denied", "The user declined. Do not retry it; ask what they want instead."};
        }
        // The point was chosen on one picture; it is only used while the window still shows that picture.
        const std::optional<DesktopFrame> again = automation->frame(window);
        if (!again || again->hash() != frame->hash()) return Outcome{"failed", "The window changed before the " + decision.action + " could be made, so nothing was done. Look again."};
        automation->pointer(window, decision.action, decision.x, decision.y, decision.end_x, decision.end_y);
        std::this_thread::sleep_for(std::chrono::milliseconds(400));
        try {
            last_observation_ = automation->observe(window);
        } catch (const std::exception&) {
            last_observation_.clear();
        }
        return Outcome{"completed", what + " was made. Before it, the model saw: " + decision.observation + "\n" + desktop::report(window, last_observation_)};
    }
    if (call.name == "desktop_act") {
        if (session.ask_only || session.permission == Permission::ReadOnly) throw Error("This session is read-only. It can observe the window but not act on it.");
        const auto field = [&](const char* key) { return arguments.contains(key) && arguments[key].is_string() ? arguments[key].get<std::string>() : std::string(); };
        const std::string operation = field("operation"), id = field("id"), value = field("value");
        if ((operation != "invoke" && operation != "set") || id.empty()) throw Error("desktop_act needs an operation (invoke or set) and the id of a control from the latest observation.");
        if (utf8_length(value) > 1200) throw Error("A value can be up to 1,200 characters.");
        const auto control = std::find_if(last_observation_.begin(), last_observation_.end(), [&](const DesktopControl& each) { return each.id == id; });
        if (control == last_observation_.end()) throw Error("Observe the window first, then use the id of a control from that observation.");
        if (operation == "invoke" ? !control->can_press : !control->can_set) throw Error(id + " does not accept " + operation + ".");
        // Acting on another app is outside the project and the sandbox, so it asks every time, in every mode.
        const std::string named = control->role + " " + (control->name.empty() ? id : "“" + control->name + "”");
        const std::string what = operation == "invoke" ? "Press " + named : "Set " + named + " to “" + utf8_prefix(value, 200) + "”";
        if (!approve_({call.name, what + " in " + desktop::label(window), "This acts on another app's window, outside the project and outside the sandbox.", "window " + window.app})) {
            return Outcome{"denied", "The user declined. Do not retry it; ask what they want instead."};
        }
        automation->act(window, id, operation, value);
        std::this_thread::sleep_for(std::chrono::milliseconds(400));
    }
    emit_({AgentEvent::Kind::Status, "Reading " + window.app, {}});
    last_observation_ = automation->observe(window);
    return Outcome{"completed", desktop::report(window, last_observation_)};
}

std::shared_ptr<SemanticModels> Agent::semantic_models() const {
    if (semantic_) return semantic_;
    if (const std::optional<EmbeddingSettings> saved = semantic::load(store_.home())) return std::make_shared<EmbeddingServers>(*saved);
    return nullptr;
}

std::vector<ToolSpec> Agent::extra_tools(const Session& session, const Workspace& workspace, bool semantic) {
    auto schema = [](json properties, std::vector<std::string> required) {
        return json{{"type", "object"}, {"properties", std::move(properties)}, {"required", std::move(required)}, {"additionalProperties", false}};
    };
    const json text = {{"type", "string"}};
    std::vector<ToolSpec> list = {
        {"read_lines", "Read up to 200 numbered lines of a text file from a starting line. Use it for large files instead of read_file.",
         schema({{"path", text}, {"start", {{"type", "integer"}, {"minimum", 1}}}, {"count", {{"type", "integer"}, {"minimum", 1}, {"maximum", 200}}}}, {"path", "start", "count"})},
        {"git_status", "Show the Git branch and which files are changed, staged or untracked.", schema(json::object(), {})},
        {"delegate_readonly", "Ask one question about 1 to 8 named files in a separate context that can only read them. Returns findings with line numbers. Use it to keep long files out of this conversation.",
         schema({{"question", text}, {"paths", {{"type", "array"}, {"items", text}, {"minItems", 1}, {"maxItems", 8}}}}, {"question", "paths"})},
    };
    list.push_back({"repository_map", "An outline of the project: its source files with their sizes and the functions, classes and types each defines. Use it to find where things live before reading files. Or give query, an identifier or a qualified name such as Cart.total, to get where that name is defined, imported, called and tested, ranked, as labelled text matches. Use an empty query for the outline.",
                    schema({{"path", {{"type", "string"}, {"description", "Folder to map, relative to the project. Use . for the whole project."}}}, {"query", text}}, {"path", "query"})});
    list.push_back({"ask_user", "Ask the user one question when you cannot continue without their answer (a choice only they can make, or a missing fact). The request stops and their reply continues it. Do not use it for things you can find out yourself.",
                    schema({{"question", text}}, {"question"})});
    list.push_back({"navigate_code", "Find where a name is defined, used or tested in a project folder. Returns labelled text candidates with file and line; they are whole-word matches, not a compiler's answer.",
                    schema({{"path", {{"type", "string"}, {"description", "Folder to look in, relative to the project. Use . for the whole project."}}}, {"symbol", text}, {"mode", {{"type", "string"}, {"enum", {"definitions", "references", "tests"}}}}},
                           {"path", "symbol", "mode"})});
    if (symbols::available() || language_servers::available()) {
        list.push_back({"inspect_symbol", "Ask a language service about one position in a source file: where the name there is defined, where it is used, or the file's diagnostics. It answers for Python, JavaScript and TypeScript, and for C#, Swift, C, C++, Go and Rust when that language's server is installed. line and column are 1-based.",
                        schema({{"path", text}, {"line", {{"type", "integer"}, {"minimum", 1}}}, {"column", {{"type", "integer"}, {"minimum", 1}}}, {"mode", {{"type", "string"}, {"enum", {"definitions", "references", "diagnostics"}}}}},
                               {"path", "line", "column", "mode"})});
    }
    const bool read_only = session.ask_only || session.permission == Permission::ReadOnly;
    if (!read_only) {
        list.push_back({"apply_edits", "Make several exact replacements in one existing file at once. Each old_text must match exactly once. Nothing is written unless every edit applies.",
                        schema({{"path", text}, {"edits", {{"type", "array"}, {"minItems", 1}, {"maxItems", 32}, {"items", schema({{"old_text", text}, {"new_text", text}}, {"old_text", "new_text"})}}}}, {"path", "edits"})});
        list.push_back({"start_job", "Start a long-running shell command (a server, a watcher, a long test run) and return at once with a job id. Read it with poll_job. Jobs stop when the request ends.",
                        schema({{"command", text}}, {"command"})});
        list.push_back({"poll_job", "Read a job's state and recent output, waiting up to wait_ms for it to finish.",
                        schema({{"id", text}, {"wait_ms", {{"type", "integer"}, {"minimum", 0}, {"maximum", 10000}}}}, {"id", "wait_ms"})});
        list.push_back({"stop_job", "Stop a job and return its output.", schema({{"id", text}}, {"id"})});
        list.push_back({"run_command", "Run one program with exact arguments and no shell: nothing in an argument is expanded, split or interpreted, and shell builtins are not available. Use it when an argument holds quotes, spaces, $ or other special characters. directory is relative to the project; use . for the project folder. 120 second limit.",
                        schema({{"program", text}, {"arguments", {{"type", "array"}, {"items", text}, {"maxItems", 64}}}, {"directory", text}}, {"program", "arguments", "directory"})});
        list.push_back({"start_terminal", "Start a program you need to answer line by line (a prompt, a REPL, an installer's questions) and return at once with an id. Send lines with terminal_input and read output with terminal_status. It is line-oriented, not a screen: full-screen programs do not work, and some programs hold their output back until they exit. It stops when the request ends.",
                        schema({{"command", text}}, {"command"})});
        list.push_back({"terminal_input", "Send one line to a terminal started with start_terminal. Set eof to true to close its input after the line (use an empty text to only close it).",
                        schema({{"id", text}, {"text", text}, {"eof", {{"type", "boolean"}}}}, {"id", "text", "eof"})});
        list.push_back({"terminal_status", "Read a terminal's state and recent output, waiting up to wait_ms for it to finish.",
                        schema({{"id", text}, {"wait_ms", {{"type", "integer"}, {"minimum", 0}, {"maximum", 10000}}}}, {"id", "wait_ms"})});
        list.push_back({"terminal_stop", "Stop a terminal and return its output.", schema({{"id", text}}, {"id"})});
        try {
            std::vector<std::string> names;
            for (const auto& [name, workflow] : workflows::load(workspace)) names.push_back(name);
            if (!names.empty()) list.push_back({"run_workflow", "Run one of the project's named workflows: " + join(names, ", ") + ".", schema({{"name", text}}, {"name"})});
        } catch (const std::exception&) {
            // A manifest that cannot be read offers no workflows.
        }
    }
    if (semantic) {
        list.push_back({"semantic_search", "Find code by meaning when you don't know the exact names, for example \"where are invoice totals calculated\". Returns ranked line ranges to read next.",
                        schema({{"query", text}, {"path", {{"type", "string"}, {"description", "Folder to search, relative to the project. Use . for the whole project."}}}}, {"query", "path"})});
    }
    if (!browser::find().empty()) {
        list.push_back({"check_page", "Load one of the project's HTML pages in a real browser and report what it shows and any script errors. steps_json is a JSON array of steps carried out in order, each one of: {\"action\":\"click\",\"text\":\"Add\"}, {\"action\":\"fill\",\"selector\":\"#name\",\"value\":\"Ada\"}, {\"action\":\"expect_text\",\"text\":\"Saved\"}, {\"action\":\"expect_no_text\",\"text\":\"Error\"}, {\"action\":\"wait\",\"ms\":500}. Use text or selector to name an element. Use [] to only load the page. The result lists the page's controls with a selector for each, so you can operate a page step by step: load it, read the controls, then call again with the steps so far plus the next one. The page has no network beyond this computer.",
                        schema({{"path", text}, {"steps_json", text}}, {"path", "steps_json"})});
    }
    if (!browser::find().empty() && acceptance::exists(workspace)) {
        list.push_back({"verify_acceptance", "Run the project's acceptance scenarios from mason.acceptance.json in a real browser, at a phone width and a desktop width, and report which requirements hold. Run it after changing a page the scenarios cover.",
                        schema(json::object(), {})});
    }
    if (!skills::list(workspace).empty()) list.push_back({"read_skill", "Read the project's instructions for one kind of work, by skill name.", schema({{"name", text}}, {"name"})});
    return list;
}

std::vector<ToolSpec> Agent::offered_tools(const Session& session, const Workspace& workspace) const {
    std::vector<ToolSpec> offered = tools();
    const std::vector<ToolSpec> extra = extra_tools(session, workspace, semantic_models() != nullptr);
    offered.insert(offered.end(), extra.begin(), extra.end());
    const std::vector<ToolSpec> window = desktop_tools(session, desktop_automation() != nullptr);
    offered.insert(offered.end(), window.begin(), window.end());
    if (!mcp::enabled(store_.home()).empty()) offered.insert(offered.end(), mcp_tools().begin(), mcp_tools().end());
    return offered;
}

std::optional<std::pair<std::string, std::string>> Agent::perform_extra(const ToolCall& call, const json& arguments, const Workspace& workspace, Session& session, const CancelToken& cancel) {
    auto text = [&](const char* key) -> std::optional<std::string> {
        if (arguments.contains(key) && arguments[key].is_string()) return arguments[key].get<std::string>();
        return std::nullopt;
    };
    auto number = [&](const char* key) -> std::optional<int> {
        if (!arguments.contains(key)) return std::nullopt;
        const json& value = arguments[key];
        if (value.is_number()) return static_cast<int>(value.get<double>());
        if (value.is_string()) {
            const std::string digits = value.get<std::string>();
            if (!digits.empty() && digits.size() <= 9 && digits.find_first_not_of("0123456789") == std::string::npos) return std::stoi(digits);
        }
        return std::nullopt;
    };
    using Outcome = std::pair<std::string, std::string>;
    const bool read_only = session.ask_only || session.permission == Permission::ReadOnly;
    const bool sandboxed = session.permission == Permission::Auto;

    if (call.name == "read_lines") {
        const auto path = text("path");
        const auto start = number("start"), count = number("count");
        if (!path || !start || !count || *start < 1 || *count < 1 || *count > 200) throw Error("read_lines needs a path, a start line of 1 or more, and a count of 1 to 200.");
        const std::optional<std::string> data = workspace.data(*path);
        if (!data) throw Error(*path + " does not exist.");
        if (data->size() > 64 * 1024 * 1024) throw Error(*path + " is larger than 64 MiB.");
        const std::vector<std::string> lines = split(*data, '\n');
        if (static_cast<size_t>(*start) > lines.size()) throw Error(*path + " has " + std::to_string(lines.size()) + " lines.");
        const size_t end = std::min(lines.size(), static_cast<size_t>(*start + *count - 1));
        std::string shown;
        for (size_t line = static_cast<size_t>(*start); line <= end; line++) {
            std::string content = lines[line - 1].size() > 2000 ? lines[line - 1].substr(0, 2000) : lines[line - 1];
            if (!is_valid_utf8(content)) {
                for (char& c : content) {
                    if (static_cast<unsigned char>(c) >= 0x80) c = '?';
                }
            }
            shown += std::to_string(line) + "\t" + content + "\n";
        }
        return Outcome{"completed", shown + "[lines " + std::to_string(*start) + "–" + std::to_string(end) + " of " + std::to_string(lines.size()) + "]"};
    }
    if (call.name == "repository_map") {
        const std::string query = trim(text("query").value_or(""));
        if (!query.empty()) return Outcome{"completed", repository_map::report(repository_map::query(workspace, query, text("path").value_or("."), 32, cancel), query)};
        return Outcome{"completed", repository_map::outline(workspace, text("path").value_or("."))};
    }
    if (call.name == "ask_user") {
        const std::string question = trim(text("question").value_or(""));
        if (question.empty() || utf8_length(question) > 2000) throw Error("ask_user needs a question of up to 2,000 characters.");
        pending_question_ = question;
        return Outcome{"completed", "Asked the user. The request stops here; their reply will come as the next message."};
    }
    if (call.name == "navigate_code") {
        const auto symbol = text("symbol"), mode = text("mode");
        if (!symbol || !mode) throw Error("navigate_code needs a symbol and a mode.");
        return Outcome{"completed", navigation::report(navigation::query(workspace, text("path").value_or("."), *symbol, *mode, cancel), *symbol, *mode)};
    }
    if (call.name == "inspect_symbol") {
        const auto path = text("path"), mode = text("mode");
        const auto whole = [&](const char* key) -> int {
            if (!arguments.contains(key)) return 0;
            if (arguments[key].is_number_integer()) return arguments[key].get<int>();
            return arguments[key].is_string() ? std::atoi(arguments[key].get<std::string>().c_str()) : 0;
        };
        if (!path || !mode) throw Error("inspect_symbol needs a path, a line, a column and a mode.");
        if (!symbols::handles(*path)) {
            const std::string extension = to_lower(fs::path(*path).extension().string());
            const std::optional<LanguageServer> server = language_servers::server_for(extension.empty() ? "" : extension.substr(1));
            if (!server) throw Error("No language server for " + (extension.empty() ? std::string("such") : extension) + " files was found on this computer. Use navigate_code for text matches.");
            // A language server reads the project and may run its build, so it is started the way a command is.
            if (read_only) throw Error("This session is read-only, and starting " + server->name + " is like running a command. Use navigate_code for text matches.");
            if (!sandboxed) {
                const std::string detail = "Starts " + join(server->command, " ") + " on this project with your account and environment, to answer one question. It is not sandboxed, and some language servers run the project's build.";
                if (!allowed({call.name, "Start " + server->name + " to look up " + *path + ":" + std::to_string(whole("line")), detail, "language server " + server->name}, session)) {
                    return Outcome{"denied", "The user declined. Do not retry it; use navigate_code instead."};
                }
            }
            emit_({AgentEvent::Kind::Status, "Asking " + server->name, {}});
            return Outcome{"completed", language_servers::query(workspace, *server, *path, whole("line"), whole("column"), *mode, sandboxed, cancel)};
        }
        emit_({AgentEvent::Kind::Status, "Asking the language service", {}});
        return Outcome{"completed", symbols::query(workspace, *path, whole("line"), whole("column"), *mode, cancel)};
    }
    if (call.name == "semantic_search") {
        const std::shared_ptr<SemanticModels> models = semantic_models();
        if (!models) throw Error("No embeddings server is chosen for code search.");
        const auto query = text("query");
        if (!query) throw Error("semantic_search needs a query.");
        emit_({AgentEvent::Kind::Status, "Searching by meaning", {}});
        return Outcome{"completed", semantic::search(workspace, *models, *query, text("path").value_or("."), 8, cancel).report()};
    }
    if (call.name == "verify_acceptance") {
        const AcceptanceContract contract = acceptance::read(workspace);
        if (session.permission != Permission::Auto) {
            const std::string detail = "Opens the pages of " + std::to_string(contract.scenarios.size()) + (contract.scenarios.size() == 1 ? " scenario" : " scenarios") +
                                       " in a headless browser with a fresh profile and no network beyond this computer." +
                                       (contract.application.empty() ? "" : " Runs the project's Python application " + contract.application + " in a throwaway copy of the project" +
                                                                                (shell::sandbox_available() ? ", inside the sandbox." : ". The sandbox is unavailable here, so it runs with your account."));
            if (!allowed({call.name, "Run the acceptance scenarios in a browser", detail, acceptance::kFilename}, session)) return Outcome{"denied", "The user declined. Do not retry it; ask what they want instead."};
        }
        const std::vector<AcceptanceResult> results = acceptance::run(workspace, contract, [this](const std::string& status) { emit_({AgentEvent::Kind::Status, status, {}}); }, cancel, session.permission == Permission::Auto);
        const bool passed = std::all_of(results.begin(), results.end(), [](const AcceptanceResult& result) { return result.passed; });
        return Outcome{passed ? "completed" : "failed", acceptance::receipt(results)};
    }
    if (call.name == "check_page") {
        const auto path = text("path");
        if (!path) throw Error("check_page needs the path of an HTML file in the project.");
        const json steps = json::parse(text("steps_json").value_or("[]"), nullptr, false);
        if (!steps.is_array()) throw Error("steps_json must be a JSON array of steps.");
        // The page's scripts run in a browser outside the command sandbox, so outside Auto this asks like a command.
        if (session.permission != Permission::Auto) {
            const std::string detail = "Opens " + *path + " in a headless browser with a fresh profile and no network beyond this computer, and carries out " +
                                       std::to_string(steps.size()) + (steps.size() == 1 ? " step." : " steps.");
            if (!allowed({call.name, "Check " + *path + " in a browser", detail, *path}, session)) return Outcome{"denied", "The user declined. Do not retry it; ask what they want instead."};
        }
        emit_({AgentEvent::Kind::Status, "Checking " + *path + " in a browser", {}});
        const browser::Result result = browser::run(workspace, *path, steps, cancel);
        return Outcome{result.passed() ? "completed" : "failed", browser::report(result, *path)};
    }
    if (call.name == "git_status") {
        std::error_code failure;
        if (!fs::exists(fs::path(workspace.root()) / ".git", failure)) return Outcome{"completed", "This project is not a Git repository."};
        // Nothing in the repository's own settings is allowed to start a program for this.
        const CommandResult result = shell::run("git -c core.fsmonitor= -c core.hooksPath=/dev/null --no-optional-locks status --porcelain=v1 --branch", workspace, shell::sandbox_available(), 20, cancel);
        return Outcome{result.exit_code == 0 ? "completed" : "failed", result.output.empty() ? "Nothing to report." : result.output};
    }
    if (call.name == "delegate_readonly") {
        const auto question = text("question");
        std::vector<std::string> paths;
        if (arguments.contains("paths") && arguments["paths"].is_array()) {
            for (const json& item : arguments["paths"]) {
                if (item.is_string()) paths.push_back(item.get<std::string>());
            }
        }
        if (!question || trim(*question).empty() || paths.empty() || paths.size() > 8) throw Error("delegate_readonly needs a question and 1 to 8 paths.");
        std::string files;
        for (const std::string& path : paths) {
            const std::vector<std::string> lines = split(workspace.read(path), '\n');
            std::string numbered;
            for (size_t i = 0; i < lines.size(); i++) numbered += std::to_string(i + 1) + "\t" + lines[i] + (i + 1 < lines.size() ? "\n" : "");
            files += "File " + path + ":\n" + (numbered.size() > 60000 ? utf8_prefix(numbered, 60000) + "\n[File clipped.]" : numbered) + "\n\n";
        }
        emit_({AgentEvent::Kind::Status, "Asking about " + std::to_string(paths.size()) + (paths.size() == 1 ? " file" : " files"), {}});
        const std::vector<ChatMessage> ask = {
            {"system", "You answer one question about the files you are given. You cannot edit, run or read anything else. Cite findings as path:line. Say so when the files do not answer the question.", {}, ""},
            {"user", files + "Question: " + *question, {}, ""},
        };
        const std::string answer = trim(model_.complete(ask, {}, [](const std::string&) {}, cancel).text);
        return answer.empty() ? Outcome{"failed", "The separate context returned nothing."} : Outcome{"completed", answer};
    }
    if (call.name == "apply_edits") {
        if (read_only) throw Error("This session is read-only. Tell the user what you would change instead.");
        const auto path = text("path");
        if (!path || !arguments.contains("edits") || !arguments["edits"].is_array() || arguments["edits"].empty() || arguments["edits"].size() > 32) throw Error("apply_edits needs a path and 1 to 32 edits.");
        if (!workspace.exists(*path)) throw Error(*path + " does not exist. Use write_file to create it.");
        const std::string before = workspace.read(*path);
        std::string after = before;
        size_t index = 0;
        for (const json& edit : arguments["edits"]) {
            index++;
            if (!edit.is_object() || !edit.contains("old_text") || !edit["old_text"].is_string() || !edit.contains("new_text") || !edit["new_text"].is_string() || edit["old_text"].get<std::string>().empty()) {
                throw Error("Edit " + std::to_string(index) + " needs old_text and new_text.");
            }
            const std::string old_text = edit["old_text"].get<std::string>(), new_text = edit["new_text"].get<std::string>();
            const size_t occurrences = count_occurrences(after, old_text);
            if (occurrences != 1) {
                throw Error("Edit " + std::to_string(index) + ": old_text " + (occurrences == 0 ? std::string("was not found") : "matches " + std::to_string(occurrences) + " places") + " in " + *path +
                            ". Nothing was changed. Read the file again and make each old_text match exactly once.");
            }
            after.replace(after.find(old_text), old_text.size(), new_text);
        }
        const size_t edits = arguments["edits"].size();
        if (session.permission == Permission::ReviewEdits) {
            std::string rows;
            int shown = 0;
            for (const DiffLine& row : diff_lines(before, after)) {
                if (row.kind == ' ' || shown >= 60) continue;
                rows += std::string(1, row.kind) + " " + row.text + "\n";
                shown++;
            }
            if (!allowed({call.name, "Edit " + *path + " (" + std::to_string(edits) + " changes)", rows, *path}, session)) {
                return Outcome{"denied", "The user declined these edits. Do not retry them; ask what they want instead."};
            }
        }
        workspace.write(*path, after);
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
        return Outcome{"completed", "Edited " + *path + " with " + std::to_string(edits) + (edits == 1 ? " change" : " changes") + " (+" + std::to_string(counts.added) + " −" + std::to_string(counts.removed) + ")."};
    }
    if (call.name == "start_job" || call.name == "run_workflow" || call.name == "run_command" || call.name == "start_terminal") {
        if (read_only) throw Error("This session is read-only. Commands are not available.");
        std::string command, title, subject;
        const bool background = call.name == "start_job" || call.name == "start_terminal";
        if (call.name == "run_workflow") {
            const std::string name = text("name").value_or("");
            const std::map<std::string, ProjectWorkflow> known = workflows::load(workspace);
            const auto found = known.find(name);
            if (found == known.end()) throw Error("No workflow named " + name + " in mason.workflows.json.");
            command = workflows::command_line(found->second);
            title = "Run the workflow " + name + ": " + command;
            subject = "workflow " + name;
        } else if (call.name == "run_command") {
            const std::string program = text("program").value_or("");
            if (program.empty() || program.find('\0') != std::string::npos) throw Error("run_command needs a program.");
            std::vector<std::string> given;
            if (arguments.contains("arguments")) {
                if (!arguments["arguments"].is_array() || arguments["arguments"].size() > 64) throw Error("run_command takes up to 64 text arguments.");
                for (const json& item : arguments["arguments"]) {
                    if (!item.is_string() || item.get<std::string>().find('\0') != std::string::npos) throw Error("run_command takes up to 64 text arguments.");
                    given.push_back(item.get<std::string>());
                }
            }
            const std::string directory = text("directory").value_or(".");
            bool outside = !directory.empty() && directory[0] == '/';
            for (const std::string& part : split(directory, '/')) outside = outside || part == "..";
            if (outside) throw Error("directory must be a folder inside the project, written relative to it.");
            std::string exact = shell::quote(program);
            for (const std::string& argument : given) exact += " " + shell::quote(argument);
            command = (directory.empty() || directory == "." ? "" : "cd -- " + shell::quote(directory) + " && ") + "exec " + exact;
            title = "Run " + program + " with " + std::to_string(given.size()) + (given.size() == 1 ? " exact argument" : " exact arguments");
            subject = exact;
        } else {
            command = text("command").value_or("");
            if (command.empty()) throw Error(call.name + " needs a command.");
            title = (call.name == "start_terminal" ? "Start a terminal for: " : "Start in the background: ") + command;
            subject = command;
        }
        if (!sandboxed) {
            const std::string detail = (call.name == "run_command" ? command + "\n" : std::string()) + "Runs in " + workspace.root() + " with your account and environment. It is not sandboxed." +
                                       (background ? " It keeps running until this request ends, for at most 30 minutes." : " Limit: 120 seconds.");
            if (!allowed({call.name, title, detail, subject}, session)) return Outcome{"denied", "The user declined. Do not retry it; ask what they want instead."};
        }
        if (background) {
            const std::string id = jobs_.start(command, workspace, sandboxed, call.name == "start_terminal");
            return Outcome{"completed", call.name == "start_terminal" ? "Started terminal " + id + ". Send lines with terminal_input and read it with terminal_status; it stops when this request ends."
                                                                      : "Started job " + id + ". Read it with poll_job; it stops when this request ends."};
        }
        emit_({AgentEvent::Kind::Status, call.name == "run_command" ? "Running the command" : "Running the workflow", {}});
        const snapshot::Map before = snapshot::capture(workspace, snapshot_);
        CommandResult result;
        try {
            result = shell::run(command, workspace, sandboxed, 120, cancel);
        } catch (...) {
            record_command_changes(before, workspace, session);
            throw;
        }
        const std::vector<std::string> changed = record_command_changes(before, workspace, session);
        std::string output = "exit " + std::to_string(result.exit_code) + "\n" + result.output;
        if (!changed.empty()) {
            const std::vector<std::string> shown(changed.begin(), changed.begin() + static_cast<std::ptrdiff_t>(std::min<size_t>(12, changed.size())));
            output += std::string("\n[Files changed by this ") + (call.name == "run_command" ? "command" : "workflow") + ": " + join(shown, ", ") + "]";
        }
        return Outcome{result.exit_code == 0 && !result.timed_out ? "completed" : "failed", output};
    }
    if (call.name == "poll_job" || call.name == "stop_job" || call.name == "terminal_status" || call.name == "terminal_stop") {
        const auto id = text("id");
        if (!id) throw Error(call.name + " needs the id.");
        const bool stop = call.name == "stop_job" || call.name == "terminal_stop";
        return Outcome{"completed", jobs_.observe(*id, stop, stop ? 0 : number("wait_ms").value_or(0), cancel)};
    }
    if (call.name == "terminal_input") {
        const auto id = text("id");
        if (!id) throw Error("terminal_input needs the terminal's id.");
        const bool eof = arguments.contains("eof") && ((arguments["eof"].is_boolean() && arguments["eof"].get<bool>()) || (arguments["eof"].is_string() && arguments["eof"].get<std::string>() == "true"));
        return Outcome{"completed", jobs_.send(*id, text("text").value_or(""), eof)};
    }
    if (call.name == "read_skill") {
        const std::vector<Definition> known = skills::list(workspace);
        const std::string wanted = to_lower(text("name").value_or(""));
        for (const Definition& skill : known) {
            if (to_lower(skill.name) == wanted) return Outcome{"completed", "Skill " + skill.name + " (" + skill.source + "):\n" + skill.body};
        }
        std::vector<std::string> names;
        for (const Definition& skill : known) names.push_back(skill.name);
        throw Error("No skill named " + wanted + ". Skills: " + join(names, ", "));
    }
    return std::nullopt;
}

std::optional<std::string> Agent::verify_command(const Workspace& workspace) const {
    try {
        const std::map<std::string, ProjectWorkflow> known = workflows::load(workspace);
        const auto found = known.find(kVerifyWorkflow);
        if (found != known.end()) return workflows::command_line(found->second);
    } catch (const std::exception&) {
        // A manifest that cannot be read verifies nothing.
    }
    return std::nullopt;
}

bool Agent::may_verify(const Session& session) const {
    if (session.ask_only || session.permission == Permission::ReadOnly) return false;
    return session.permission == Permission::Auto || ApprovalRules(store_.home(), session.workspace).allows("run_workflow", std::string("workflow ") + kVerifyWorkflow);
}

std::optional<CommandResult> Agent::verify_run(const Workspace& workspace, const Session& session, const CancelToken& cancel) {
    const std::optional<std::string> command = verify_command(workspace);
    if (!command || !may_verify(session)) return std::nullopt;
    emit_({AgentEvent::Kind::Status, "Verifying", {}});
    std::optional<CommandResult> result;
    try {
        result = shell::run(*command, workspace, session.permission == Permission::Auto, 120, cancel);
    } catch (const Cancelled&) {
        throw;
    } catch (const std::exception&) {
        // It could not be started: there is no result.
    }
    // Whatever the run left behind is not part of the request's changes.
    snapshot_ = snapshot::capture(workspace, snapshot_);
    return result;
}

std::optional<Verification> Agent::verify(const Workspace& workspace, const Session& session, const std::optional<CommandResult>& baseline, int repairs, const CancelToken& cancel) {
    const std::optional<std::string> command = verify_command(workspace);
    if (session.turns.back().changes.empty() || !command) return std::nullopt;
    Verification receipt;
    receipt.workflow = kVerifyWorkflow;
    receipt.command = *command;
    receipt.repairs = repairs;
    if (!may_verify(session)) {
        receipt.verdict = "notRun";
        receipt.output = "Mason runs it by itself in Auto, or when you have chosen Always Allow for the verify workflow.";
        return receipt;
    }
    check_cancel(cancel);
    const std::optional<CommandResult> after = verify_run(workspace, session, cancel);
    if (!after) return std::nullopt;
    const bool passed = after->exit_code == 0 && !after->timed_out;
    const std::optional<bool> before = baseline ? std::optional<bool>(baseline->exit_code == 0 && !baseline->timed_out) : std::nullopt;
    receipt.verdict = passed ? (before && !*before ? "fixed" : "passed") : (!before ? "failed" : *before ? "regression" : "failingBefore");
    receipt.exit_code = after->exit_code;
    if (baseline) receipt.baseline_exit_code = baseline->exit_code;
    receipt.output = utf8_suffix(after->output, 4000);
    return receipt;
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
        if (!continuing) {
            // Whatever the request left running in the background ends with it.
            jobs_.stop_all();
            return;
        }
        emit_({AgentEvent::Kind::FollowUp, input, {}, std::make_shared<const Session>(session)});
        emit_({AgentEvent::Kind::Status, steering ? "Steering" : "Starting the queued message", {}});
    }
}

bool Agent::build_catalog(const std::string& request, const Workspace& workspace, Session& session, const CancelToken& cancel) {
    emit_({AgentEvent::Kind::Status, "Reading the offers", {}});
    const std::optional<CatalogSpec> spec = catalog::extract(request, model_, cancel);
    if (!spec || !catalog::complete(*spec, request)) return false;
    emit_({AgentEvent::Kind::Status, "Writing the store", {}});
    const CatalogDesign design = catalog::design(request, *spec, model_, cancel);
    check_cancel(cancel);
    const std::string page = catalog::render(*spec, design);
    Activity activity;
    activity.tool = "write_file";
    activity.subject = "index.html";
    session.activities.push_back(activity);
    emit_({AgentEvent::Kind::ActivityStarted, "", activity});
    workspace.write("index.html", page);
    session.turns.back().changes.push_back({"index.html", std::nullopt, page});
    activity.state = "completed";
    activity.result = "Created index.html with Mason's catalog component: " + std::to_string(spec->items.size()) + " products, totals in whole cents.";
    session.activities.back() = activity;
    emit_({AgentEvent::Kind::ActivityFinished, "", activity});
    emit_({AgentEvent::Kind::Status, "Checking the store", {}});
    DerivedPlan plan;
    for (const AcceptanceScenario& scenario : catalog::scenarios(*spec, request)) {
        plan.units.push_back(scenario.requirement);
        DerivedCheck check{static_cast<int>(plan.units.size()), scenario.requirement, "browser_flow", scenario.page, scenario.id, "All scenario assertions pass", {}, ""};
        for (const AcceptanceStep& step : scenario.steps) check.steps.push_back({step.op, step.target, step.value});
        plan.checks.push_back(std::move(check));
    }
    const DerivedReceipt receipt{derived::verify(plan, workspace, false, cancel), {}, 0};
    session.turns.back().checks = receipt;
    std::vector<std::string> offers;
    for (const CatalogItem& item : spec->items) {
        offers.push_back(item.name + " at $" + catalog::money(item.cents) + (item.threshold > 0 ? " ($" + catalog::money(item.sale_cents) + " each from " + std::to_string(item.threshold) + ")" : ""));
    }
    const bool ran = std::any_of(receipt.results.begin(), receipt.results.end(), [](const DerivedResult& result) { return result.state != "notRun"; });
    const std::string answer = "I built index.html with Mason's catalog component, which computes totals in whole cents and makes the receipt in the browser. The offers, as the request states them: " + join(offers, ", ") + "." +
                               (ran ? (derived::passed(receipt) ? " Mason then checked the page in a browser at a phone width and a desktop width: every offer, discount and the checkout hold."
                                                                : " Mason checked the page in a browser and some checks failed; they are listed with this request.")
                                    : " No browser was found on this computer, so the page was not checked: open it and try each offer and the checkout.");
    session.messages.push_back({"assistant", answer, {}, ""});
    session.turns.back().answer = answer;
    emit_({AgentEvent::Kind::Text, answer, {}});
    return true;
}

void Agent::run_turn(Session& session, const std::string& request, const CancelToken& cancel) {
    std::optional<Workspace> workspace;
    try {
        workspace.emplace(session.workspace);
    } catch (const Error&) {
        session.state = "Failed";
        session.detail = "The project folder " + session.workspace + " is no longer available.";
        return;
    }
    // Comments on lines of the project travel with the request, and only while those files are unchanged.
    try {
        review::validate(*workspace, session.comments);
    } catch (const Error& error) {
        session.state = "Failed";
        session.detail = error.what();
        return;
    }
    const std::string input = review::request(request, session.comments);
    if (input.empty() || utf8_length(input) > 16000) {
        session.state = "Failed";
        session.detail = "Describe a request in 1–16,000 characters.";
        return;
    }
    session.comments.clear();
    if (session.title == "New session") session.title = utf8_length(input) > 55 ? utf8_prefix(input, 55) + "…" : input;
    const std::vector<ToolSpec> offered = offered_tools(session, *workspace);
    offered_names_.clear();
    for (const ToolSpec& tool : offered) offered_names_.push_back(tool.name);
    std::string prompt = system_prompt(session);
    {
        std::vector<std::string> listed;
        for (const Definition& skill : skills::list(*workspace)) listed.push_back(skill.name + " (" + skill.description + ")");
        if (!listed.empty()) prompt += "\nThis project has instructions for particular kinds of work. Read the one that fits with read_skill before starting: " + join(listed, "; ") + ".";
    }
    {
        std::vector<std::string> names;
        for (const McpServer& server : mcp::enabled(store_.home())) names.push_back(server.name);
        if (!names.empty()) prompt += "\nThe user registered these MCP servers: " + join(names, ", ") + ". Use mcp_tools to see what one offers before calling it.";
    }
    if (std::find(offered_names_.begin(), offered_names_.end(), "verify_acceptance") != offered_names_.end()) {
        prompt += "\nThis project has acceptance scenarios in mason.acceptance.json. Run verify_acceptance after changing a page they cover, and do not edit that file unless asked.";
    }
    if (session.desktop && desktop_automation()) prompt += "\nThe user attached a window of another app: " + desktop::label(*session.desktop) + ". Read it with desktop_observe; use desktop_visual for what its controls do not name. Acting on it asks the user each time.";
    last_observation_.clear();
    if (!session.milestones.empty()) prompt += "\n" + milestones::context(*workspace, session.milestones);
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
        bool finished = false, asked = false, baseline_taken = false;
        std::optional<CommandResult> baseline;
        int repairs = 0;
        static const std::set<std::string> changing = {"edit_file", "write_file", "apply_edits", "run_shell", "run_command", "run_workflow", "start_job", "start_terminal"};
        pending_question_.reset();
        // In Auto, Mason works out what it can check for itself before the work starts.
        std::optional<DerivedPlan> plan;
        if (derives_checks && session.permission == Permission::Auto && !session.ask_only) {
            if (catalog::applies(input, *workspace) && build_catalog(input, *workspace, session, cancel)) {
                session.state = "Completed";
                session.detail.clear();
                store_.save(session);
                return;
            }
            DerivedPlan made = derived::prepare(input, *workspace, &model_, cancel);
            if (!made.checks.empty()) {
                session.messages.back().content += "\n\n" + made.brief();
                plan = std::move(made);
            }
        }
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
                // The model says it is done. If the project can be verified and files changed, Mason checks for itself.
                if (auto receipt = verify(*workspace, session, baseline, repairs, cancel)) {
                    session.turns.back().verification = receipt;
                    if (receipt->verdict == "regression" && repairs < kRepairLimit) {
                        repairs++;
                        session.messages.push_back({"user", "Mason ran the project's verify workflow (" + receipt->command + "). It passed before your changes and fails now:\n" + receipt->output +
                                                                "\nFix what your changes broke, then answer again.", {}, ""});
                        store_.save(session);
                        continue;
                    }
                }
                // Then the checks Mason worked out from the request itself. A failed one goes back to the model.
                if (plan) {
                    emit_({AgentEvent::Kind::Status, "Checking the result", {}});
                    DerivedReceipt receipt{derived::verify(*plan, *workspace, true, cancel), plan->unverified(), repairs};
                    session.turns.back().checks = receipt;
                    if (!derived::passed(receipt) && repairs < kRepairLimit) {
                        repairs++;
                        std::string failed;
                        for (const DerivedResult& result : receipt.results) {
                            if (result.state == "failed") failed += "\n- " + derived::description(result.check) + ": " + result.detail;
                        }
                        session.messages.push_back({"user", "Mason checked the result itself and this does not hold yet:" + failed + "\nFix it, then answer again.", {}, ""});
                        store_.save(session);
                        continue;
                    }
                }
                finished = true;
                break;
            }
            for (const ToolCall& call : reply.tool_calls) {
                check_cancel(cancel);
                // Before the first step that can change the project, note whether it verifies as it stands.
                if (!baseline_taken && changing.count(call.name)) {
                    baseline_taken = true;
                    baseline = verify_run(*workspace, session, cancel);
                }
                const std::string result = perform(call, *workspace, session, cancel);
                session.messages.push_back({"tool", result, {}, call.id});
                store_.save(session);
                if (pending_question_) break;
            }
            if (pending_question_) {
                // The model needs something only the user can say. Its question is the answer to this request.
                session.turns.back().answer = *pending_question_;
                pending_question_.reset();
                asked = true;
                break;
            }
        }
        session.state = asked ? "Needs your answer" : finished ? "Completed" : "Stopped";
        session.detail = asked ? "Mason asked a question. Reply to continue." : finished ? "" : "Stopped after " + std::to_string(kActionLimit) + " steps. Send another message to continue.";
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
    if (activity.subject.empty()) activity.subject = text("name").value_or(text("id").value_or(text("question").value_or("")));
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
        if (call.name == "search_files" || call.name == "search_next") {
            std::string query;
            size_t skip = 0;
            if (call.name == "search_next") {
                if (!last_search_) throw Error("search_next continues a search_files call, and this request has not made one.");
                const std::string cursor = text("cursor").value_or(arguments.contains("cursor") && arguments["cursor"].is_number() ? std::to_string(arguments["cursor"].get<long>()) : "");
                if (cursor.empty() || cursor.size() > 9 || cursor.find_first_not_of("0123456789") != std::string::npos || std::stoul(cursor) == 0) throw Error("search_next needs the cursor the search returned.");
                query = *last_search_;
                skip = std::stoul(cursor);
            } else {
                const auto given = text("query");
                if (!given) throw Error("search_files needs a query.");
                query = *given;
                last_search_ = query;
            }
            const std::vector<SearchMatch> matches = workspace.search(query, 100, skip);
            std::string found;
            for (const SearchMatch& match : matches) found += match.path + ":" + std::to_string(match.line) + ": " + match.text + "\n";
            if (matches.size() == 100) found += "[There may be more matches. Continue with search_next and cursor \"" + std::to_string(skip + 100) + "\".]";
            return finish("completed", found.empty() ? (skip > 0 ? "No more matches." : "No matches.") : found);
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
        if (const auto extra = perform_extra(call, arguments, workspace, session, cancel)) return finish(extra->first, extra->second);
        if (const auto window = perform_desktop(call, arguments, session, cancel)) return finish(window->first, window->second);
        throw Error("Unknown tool " + call.name + ". Use one of: " + join(offered_names_, ", "));
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
