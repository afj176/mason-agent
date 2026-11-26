// Engine tests. Each test is a function; a failed check prints its location and the run exits non-zero.
#include <chrono>
#include <deque>
#include <fstream>
#include <iostream>
#include <thread>
#include <csignal>
#include <fcntl.h>
#include <sys/wait.h>
#include <unistd.h>

#include "agent.hpp"
#include "derived.hpp"
#include "navigation.hpp"
#include "transfer.hpp"
#include "acceptance.hpp"
#include "browser.hpp"
#include "planning.hpp"
#include "chat.hpp"
#include "schedules.hpp"
#include "models.hpp"

using namespace mason;

static int failures = 0;
#define CHECK(...)                                                                                        \
    do {                                                                                                  \
        if (!(__VA_ARGS__)) {                                                                               \
            failures++;                                                                                   \
            std::cerr << "  FAILED " << __FILE__ << ":" << __LINE__ << "  " << #__VA_ARGS__ << "\n";        \
        }                                                                                                 \
    } while (0)
#define CHECK_THROWS(expression)                                                                          \
    do {                                                                                                  \
        bool threw = false;                                                                               \
        try { (void)(expression); } catch (const std::exception&) { threw = true; }                       \
        if (!threw) {                                                                                     \
            failures++;                                                                                   \
            std::cerr << "  FAILED " << __FILE__ << ":" << __LINE__ << "  expected an error: " << #expression << "\n"; \
        }                                                                                                 \
    } while (0)

/// A fresh project folder and data home for one test, removed afterwards.
struct Sandbox {
    fs::path base = fs::temp_directory_path() / ("mason-tests-" + new_id().substr(0, 12));
    fs::path project = base / "project", home = base / "home";
    Sandbox() {
        fs::create_directories(project);
        write("calc.py", "def add(a, b):\n    return a - b\n");
    }
    ~Sandbox() { std::error_code ignored; fs::remove_all(base, ignored); }
    void write(const std::string& name, const std::string& content) const {
        fs::create_directories((project / name).parent_path());
        std::ofstream(project / name, std::ios::binary) << content;
    }
    std::string read(const std::string& name) const { return read_file(project / name); }
};

/// Replays fixed replies, like a model that always makes the same decisions.
struct ScriptedModel : ChatModel {
    std::deque<ModelReply> replies;
    std::vector<std::vector<ChatMessage>> seen;
    int window = 0;
    int context_window() override { return window; }
    /// Called at the start of each request, with its number, so a test can act while the "model" is busy.
    std::function<void(size_t)> on_request;
    int delay_ms = 0;
    std::string name() const override { return "scripted"; }
    ModelReply complete(const std::vector<ChatMessage>& messages, const std::vector<ToolSpec>&, const std::function<void(const std::string&)>& on_text, const CancelToken& cancel) override {
        seen.push_back(messages);
        if (on_request) on_request(seen.size());
        for (int waited = 0; waited < delay_ms; waited += 10) {
            check_cancel(cancel);
            usleep(10 * 1000);
        }
        if (replies.empty()) throw Error("The script ran out of replies.");
        ModelReply reply = replies.front();
        replies.pop_front();
        if (!reply.text.empty() && on_text) on_text(reply.text);
        return reply;
    }
};
static ModelReply call(const std::string& name, const json& arguments) {
    ModelReply reply;
    reply.tool_calls.push_back({"call_" + new_id().substr(0, 6), name, arguments.dump()});
    return reply;
}
static ModelReply say(const std::string& text) {
    ModelReply reply;
    reply.text = text;
    return reply;
}

static void test_workspace() {
    Sandbox box;
    Workspace workspace(box.project.string());
    CHECK_THROWS(workspace.resolve("../outside.txt"));
    CHECK_THROWS(workspace.resolve("/etc/passwd"));
    CHECK_THROWS(workspace.resolve(".env"));
    CHECK_THROWS(workspace.resolve("keys/id_rsa"));
    CHECK_THROWS(workspace.resolve(".git/config"));
    CHECK(workspace.resolve("src/new/file.cpp").string() == workspace.root() + "/src/new/file.cpp");

    // A link that leaves the project is refused and not listed.
    fs::create_directories(box.base / "outside");
    std::ofstream(box.base / "outside" / "note.txt") << "secret";
    fs::create_directory_symlink(box.base / "outside", box.project / "link");
    CHECK_THROWS(workspace.read("link/note.txt"));

    box.write("node_modules/x/index.js", "return a - b");
    const FileListing listing = workspace.files();
    CHECK(listing.paths == std::vector<std::string>{"calc.py"});
    const auto matches = workspace.search("RETURN A");
    CHECK(matches.size() == 1 && matches[0].path == "calc.py" && matches[0].line == 2);

    // An executable script keeps its permissions when it is rewritten.
    box.write("run.sh", "#!/bin/sh\necho one\n");
    fs::permissions(box.project / "run.sh", fs::perms::owner_all);
    workspace.write("run.sh", "#!/bin/sh\necho two\n");
    CHECK((fs::status(box.project / "run.sh").permissions() & fs::perms::owner_exec) != fs::perms::none);
}

static void test_diff() {
    std::string joined;
    for (const DiffLine& row : diff_lines(std::string("a\nb\nc\n"), "a\nB\nc\n")) joined += std::string(1, row.kind) + row.text + "|";
    CHECK(joined == " a|-b|+B| c|");
    CHECK(diff_counts(std::nullopt, "x\ny").added == 2);
    CHECK(diff_counts(std::string("same\n"), "same\n").added == 0);
}

static void test_stream_and_endpoint() {
    StreamAssembler assembler;
    CHECK(assembler.feed(R"(data: {"choices":[{"delta":{"content":"Hel"}}]})") == "Hel");
    CHECK(assembler.feed(R"(data: {"choices":[{"delta":{"content":"lo"}}]})") == "lo");
    assembler.feed(R"(data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read_file","arguments":"{\"pa"}}]}}]})");
    assembler.feed(R"(data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"calc.py\"}"}}]},"finish_reason":"tool_calls"}]})");
    assembler.feed(R"(data: {"choices":[],"usage":{"prompt_tokens":42}})");
    CHECK(assembler.feed("data: [DONE]").empty());
    const ModelReply reply = assembler.finish();
    CHECK(reply.text == "Hello");
    CHECK(reply.tool_calls.size() == 1 && reply.tool_calls[0].id == "c1" && reply.tool_calls[0].name == "read_file" && reply.tool_calls[0].arguments == R"({"path":"calc.py"})");
    CHECK(reply.prompt_tokens == 42);
    StreamAssembler failing;
    CHECK_THROWS(failing.feed(R"(data: {"error":{"message":"context too long"}})"));

    CHECK(validated_endpoint("http://localhost:8080/v1") == "http://127.0.0.1:8080/");
    CHECK(validated_endpoint("http://127.0.0.1:1234") == "http://127.0.0.1:1234/");
    CHECK_THROWS(validated_endpoint("https://api.example.com/v1"));
    CHECK_THROWS(validated_endpoint("http://user:pw@127.0.0.1:8080"));
    CHECK_THROWS(validated_endpoint("http://127.0.0.1:8080/other"));
    CHECK_THROWS(validated_endpoint("http://127.0.0.1.evil.example:8080"));
}

static void test_recovery() {
    const auto& tools = Agent::tools();
    const auto fenced = recover_tool_call("I'll fix it.\n```json\n{\n  \"name\": \"edit_file\",\n  \"arguments\": {\"path\": \"calc.py\", \"old_text\": \"a - b\", \"new_text\": \"a + b\"}\n}\n```\nThen run it.", tools);
    CHECK(fenced && fenced->name == "edit_file" && json::parse(fenced->arguments)["new_text"] == "a + b");
    const auto tagged = recover_tool_call("<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.txt\"}}\n</tool_call>", tools);
    CHECK(tagged && tagged->name == "read_file");
    const auto nested = recover_tool_call(R"({"function": {"name": "run_shell", "arguments": "{\"command\": \"ls\"}"}})", tools);
    CHECK(nested && json::parse(nested->arguments)["command"] == "ls");
    CHECK(!recover_tool_call("```json\n{\"name\": \"launch_rocket\", \"arguments\": {}}\n```", tools));
    CHECK(!recover_tool_call("```python\nprint(1)\n```\nDone.", tools));
    CHECK(!recover_tool_call("The config is {\"name\": \"edit_file\"} in prose.", tools));
}

static void test_agent_edit_undo() {
    Sandbox box;
    SessionStore store(box.home);
    Session session = store.create(Workspace(box.project.string()).root());
    ScriptedModel model;
    model.replies = {call("read_file", {{"path", "calc.py"}}), call("edit_file", {{"path", "calc.py"}, {"old_text", "return a - b"}, {"new_text", "return a + b"}}), say("Fixed add.")};
    std::vector<ApprovalRequest> asked;
    Agent agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request); return true; }, [](const AgentEvent&) {});
    agent.run(session, "Fix add");
    CHECK(session.state == "Completed");
    CHECK(asked.size() == 1 && asked[0].tool == "edit_file" && asked[0].detail.find("+     return a + b") != std::string::npos);
    CHECK(box.read("calc.py") == "def add(a, b):\n    return a + b\n");
    CHECK(session.turns.back().answer == "Fixed add.");
    CHECK(session.activities.size() == 2 && session.activities[1].state == "completed");
    CHECK(model.seen[1].back().content.find("2\t    return a - b") != std::string::npos);

    auto undone = Agent::undo_latest(session, store);
    CHECK(undone && undone->restored == std::vector<std::string>{"calc.py"});
    CHECK(box.read("calc.py") == "def add(a, b):\n    return a - b\n");
    auto redone = Agent::undo_latest(session, store, true);
    CHECK(redone && redone->restored == std::vector<std::string>{"calc.py"});
    CHECK(box.read("calc.py") == "def add(a, b):\n    return a + b\n");

    // A reloaded store has the same session, and the undo note travels with the next request.
    SessionStore reloaded(box.home);
    CHECK(reloaded.sessions().size() == 1 && reloaded.sessions()[0].turns.size() == 1 && !reloaded.sessions()[0].pending_note.empty());
    CHECK(reloaded.sessions()[0].turns[0].changes[0].before == std::optional<std::string>("def add(a, b):\n    return a - b\n"));
}

static void test_agent_refusals() {
    Sandbox box;
    SessionStore store(box.home);
    const json edit = {{"path", "calc.py"}, {"old_text", "return a - b"}, {"new_text", "return a + b"}};

    Session declined = store.create(box.project.string());
    ScriptedModel first;
    first.replies = {call("edit_file", edit), say("Not changed.")};
    Agent(store, first, [](const ApprovalRequest&) { return false; }, [](const AgentEvent&) {}).run(declined, "Fix add");
    CHECK(declined.activities.back().state == "denied");

    Session read_only = store.create(box.project.string(), Permission::ReadOnly);
    ScriptedModel second;
    second.replies = {call("edit_file", edit), call("run_shell", {{"command", "touch made"}}), say("I can only read.")};
    bool asked = false;
    Agent(store, second, [&](const ApprovalRequest&) { asked = true; return true; }, [](const AgentEvent&) {}).run(read_only, "Fix add");
    CHECK(!asked);
    CHECK(read_only.activities.size() == 2 && read_only.activities[0].state == "failed" && read_only.activities[1].state == "failed");
    CHECK(box.read("calc.py") == "def add(a, b):\n    return a - b\n");
    CHECK(!fs::exists(box.project / "made"));

    // An ambiguous edit is rejected with guidance the model can act on.
    box.write("twice.py", "x = 1\nx = 1\n");
    Session ambiguous = store.create(box.project.string(), Permission::WorkspaceEdits);
    ScriptedModel third;
    third.replies = {call("edit_file", {{"path", "twice.py"}, {"old_text", "x = 1"}, {"new_text", "x = 2"}}), say("Could not.")};
    Agent(store, third, [](const ApprovalRequest&) { return true; }, [](const AgentEvent&) {}).run(ambiguous, "Change x");
    CHECK(ambiguous.activities.back().state == "failed");
    CHECK(third.seen[1].back().content.find("matches 2 places") != std::string::npos);

    // A user edit made after the request is left alone by undo.
    Session edits = store.create(box.project.string(), Permission::WorkspaceEdits);
    ScriptedModel fourth;
    fourth.replies = {call("write_file", {{"path", "notes.txt"}, {"content", "one\n"}}), say("Done.")};
    Agent(store, fourth, [](const ApprovalRequest&) { return false; }, [](const AgentEvent&) {}).run(edits, "Add notes");
    box.write("notes.txt", "mine\n");
    auto undone = Agent::undo_latest(edits, store);
    CHECK(undone && undone->skipped == std::vector<std::string>{"notes.txt"} && box.read("notes.txt") == "mine\n");
}

static void test_commands_and_rules() {
    Sandbox box;
    SessionStore store(box.home);
    Session session = store.create(Workspace(box.project.string()).root(), Permission::WorkspaceEdits);
    int asked = 0;
    auto run = [&](const char* request) {
        ScriptedModel model;
        model.replies = {call("run_shell", {{"command", "echo hello"}}), say("Ran it.")};
        Agent(store, model, [&](const ApprovalRequest&) { asked++; return true; }, [](const AgentEvent&) {}).run(session, request);
    };
    run("Say hello");
    CHECK(asked == 1);
    CHECK(session.activities.back().state == "completed" && session.activities.back().result.find("hello") != std::string::npos);
    CHECK(ApprovalRules::suggestion("run_shell", "echo hello world") == "echo hello");
    CHECK(ApprovalRules::suggestion("edit_file", "src/app/main.cpp") == "src/app/");
    ApprovalRules rules(store.home(), session.workspace);
    rules.add("run_shell", "echo hello");
    run("Again");
    CHECK(asked == 1);  // the saved rule answered the second prompt

    // A stopped request leaves a conversation the model can continue.
    Session cut;
    cut.messages = {{"user", "go", {}, ""}, {"assistant", "", {{"a", "read_file", "{}"}, {"b", "read_file", "{}"}}, ""}, {"tool", "ok", {}, "a"}};
    Agent::close_open_calls(cut);
    CHECK(cut.messages.size() == 4 && cut.messages[3].tool_call_id == "b");
}

static void test_shell() {
    Sandbox box;
    Workspace workspace(box.project.string());
    const CommandResult plain = shell::run("echo out; echo err 1>&2; exit 3", workspace, false);
    CHECK(plain.exit_code == 3 && plain.output.find("out") != std::string::npos && plain.output.find("err") != std::string::npos);

    // A command past its deadline is stopped together with what it started, promptly.
    const auto started = std::chrono::steady_clock::now();
    const CommandResult hung = shell::run("echo before; sleep 30 & sleep 30; echo after", workspace, false, 1);
    const double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
    CHECK(hung.timed_out && hung.output.find("before") != std::string::npos && hung.output.find("after") == std::string::npos);
    CHECK(elapsed < 8);

    // A background program left behind does not keep the command from returning.
    const auto quick = std::chrono::steady_clock::now();
    const CommandResult detached = shell::run("sleep 30 & echo started", workspace, false, 20);
    CHECK(detached.exit_code == 0 && detached.output.find("started") != std::string::npos);
    CHECK(std::chrono::duration<double>(std::chrono::steady_clock::now() - quick).count() < 5);

    const CommandResult big = shell::run("head -c 200000 /dev/zero | tr '\\0' 'x'", workspace, false);
    CHECK(big.truncated && big.output.size() < 70000);
}

static void test_sandbox() {
    Sandbox box;
    Workspace workspace(box.project.string());
    if (!shell::sandbox_available()) {
        std::cerr << "  note: bubblewrap cannot create a sandbox here; checking that Auto refuses to run instead.\n";
        CHECK_THROWS(shell::run("touch made", workspace, true));
        CHECK(!fs::exists(box.project / "made"));
        return;
    }
    const CommandResult inside = shell::run("echo hello > inside.txt && cat inside.txt", workspace, true);
    CHECK(inside.exit_code == 0 && inside.output.find("hello") != std::string::npos && fs::exists(box.project / "inside.txt"));

    const fs::path outside = fs::path(home_dir()) / ("mason-sandbox-outside-" + new_id().substr(0, 8));
    const CommandResult escape = shell::run("echo leaked > '" + outside.string() + "'", workspace, true);
    CHECK(escape.exit_code != 0 && !fs::exists(outside));

    const CommandResult network = shell::run("getent hosts example.com || (command -v curl >/dev/null && curl -m 5 -s -o /dev/null https://example.com)", workspace, true);
    CHECK(network.exit_code != 0);

    const CommandResult temporary = shell::run("f=$(mktemp) && echo ok > \"$f\" && cat \"$f\"", workspace, true);
    CHECK(temporary.exit_code == 0 && temporary.output.find("ok") != std::string::npos);

    // A credential folder that exists is hidden from sandboxed commands.
    const fs::path ssh = fs::path(home_dir()) / ".ssh";
    const bool created = !fs::exists(ssh);
    if (created) fs::create_directories(ssh);
    std::ofstream(ssh / "mason-test-key") << "private";
    const CommandResult secrets = shell::run("cat ~/.ssh/mason-test-key", workspace, true);
    CHECK(secrets.exit_code != 0 && secrets.output.find("private") == std::string::npos);
    fs::remove(ssh / "mason-test-key");
    if (created) fs::remove(ssh);

    const auto started = std::chrono::steady_clock::now();
    const CommandResult hung = shell::run("sleep 30", workspace, true, 1);
    CHECK(hung.timed_out && std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count() < 8);
}

static void test_sha_and_models() {
    Sha256 empty;
    CHECK(empty.hex() == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    Sha256 abc;
    abc.update("abc", 3);
    CHECK(abc.hex() == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    std::string text;
    for (int i = 0; i < 1000; i++) text += "mason agent ";
    Sha256 whole, pieces;
    whole.update(text.data(), text.size());
    size_t offset = 0;
    for (size_t size : {1u, 63u, 64u, 65u, 1000u, 7u}) { pieces.update(text.data() + offset, size); offset += size; }
    pieces.update(text.data() + offset, text.size() - offset);
    CHECK(whole.hex() == pieces.hex());

    // Model files are classified and never hidden.
    Sandbox box;
    const fs::path root = box.base / "models";
    for (const char* name : {"coder/Qwen-Coder-Q4_K_M.gguf", "vision/Seer-Q8_0.gguf", "vision/mmproj-Seer-f16.gguf", "search/nomic-embed-text-Q8.gguf", "split/Big-00001-of-00002.gguf", "split/Big-00002-of-00002.gguf", "tiny/stub.gguf"}) {
        fs::create_directories((root / name).parent_path());
        std::ofstream(root / name, std::ios::binary) << std::string(std::string(name).find("stub") != std::string::npos ? 10 : 1'100'000, 'x');
    }
    const std::vector<LocalModel> models = scan_models({{"Test", root}});
    std::vector<std::string> names, roles;
    for (const LocalModel& model : models) { names.push_back(model.name); roles.push_back(model.role()); CHECK(!model.verdict().empty()); }
    CHECK(names == (std::vector<std::string>{"Big-00001-of-00002", "Qwen-Coder-Q4_K_M", "Seer-Q8_0", "mmproj-Seer-f16", "nomic-embed-text-Q8"}));
    CHECK(roles == (std::vector<std::string>{"coding", "coding", "vision", "projector", "embeddings"}));

    HubRepository repository{"a/b", std::string(40, 'a'), "mit", false, {{"m-Q8_0.gguf", 400LL << 30, ""}, {"m-Q4_K_M.gguf", 1 << 20, ""}, {"mmproj-Q8_0.gguf", 1 << 20, ""}}};
    const auto pick = repository.suggested(8LL << 30);
    CHECK(pick && pick->file == "m-Q4_K_M.gguf");
    CHECK(!repository.suggested(1 << 20));
    CHECK(HubBuild{"x.Q5_K_M.gguf", 1, ""}.quant() == "Q5_K_M");
    CHECK(memory_budget_bytes() > 0);

    auto hub = [](const char* id, std::vector<std::string> tags, const char* task) { return HubModel{id, 1, 0, std::move(tags), task, false}; };
    CHECK(starts_with(hub("Qwen/Coder-GGUF", {"gguf"}, "text-generation").verdict(), "Coding agent · GGUF"));
    CHECK(starts_with(hub("a/sdxl", {}, "text-to-image").verdict(), "Not used by the agent"));
    CHECK(hub("a/coder", {"safetensors"}, "text-generation").verdict().find("look for a GGUF build") != std::string::npos);
    CHECK(starts_with(hub("nomic/embed-GGUF", {"gguf"}, "feature-extraction").verdict(), "Code search"));
    CHECK_THROWS(hub_inspect("../etc"));
}

static void test_undo_of_command_changes() {
    Sandbox box;
    box.write("keep.txt", "keep me\n");
    box.write("node_modules/old.js", "x");
    SessionStore store(box.home);
    Session session = store.create(Workspace(box.project.string()).root(), Permission::WorkspaceEdits);
    const std::string original = box.read("calc.py");
    const auto approve = [](const ApprovalRequest&) { return true; };
    {
        ScriptedModel model;
        model.replies = {call("run_shell", {{"command", "printf 'made' > made.txt && printf 'changed' > calc.py && rm keep.txt && printf '\\377\\376\\000x' > image.bin && printf dep > node_modules/dep.js"}}), say("Done.")};
        Agent(store, model, approve, [](const AgentEvent&) {}).run(session, "Run the script");
    }
    std::vector<std::string> paths;
    for (const FileChange& change : session.turns.back().changes) {
        paths.push_back(change.path);
        CHECK(change.by_command);
        if (change.path == "image.bin") CHECK(change.opaque());   // a binary file is kept as a blob
        if (change.path == "keep.txt") CHECK(change.deleted());
        if (change.path == "made.txt") CHECK(change.created() && change.after == "made");
    }
    std::sort(paths.begin(), paths.end());
    CHECK(paths == (std::vector<std::string>{"calc.py", "image.bin", "keep.txt", "made.txt"}));  // dependency folders are not followed
    CHECK(session.messages[session.messages.size() - 2].content.find("Files changed by this command") != std::string::npos);

    // The changes survive a reload, then undo puts everything back.
    Session reloaded = SessionStore(box.home).sessions()[0];
    auto undone = Agent::undo_latest(reloaded, store);
    CHECK(undone && undone->restored.size() == 4 && undone->skipped.empty());
    CHECK(box.read("calc.py") == original && box.read("keep.txt") == "keep me\n");
    CHECK(!fs::exists(box.project / "made.txt") && !fs::exists(box.project / "image.bin"));
    auto redone = Agent::undo_latest(reloaded, store, true);
    CHECK(redone && redone->restored.size() == 4);
    CHECK(box.read("image.bin") == std::string("\xFF\xFE\x00x", 4) && !fs::exists(box.project / "keep.txt"));

    // A file the user changed after the command is left alone.
    box.write("made.txt", "mine now");
    auto again = Agent::undo_latest(reloaded, store);
    CHECK(again && again->skipped == std::vector<std::string>{"made.txt"} && box.read("made.txt") == "mine now");

    // An edit followed by a command on the same file undoes to where the request started.
    Sandbox second;
    SessionStore other(second.home);
    Session mixed = other.create(Workspace(second.project.string()).root(), Permission::WorkspaceEdits);
    ScriptedModel model;
    model.replies = {call("write_file", {{"path", "calc.py"}, {"content", "one\n"}}), call("run_shell", {{"command", "printf 'two\\n' >> calc.py"}}), say("Done.")};
    Agent(other, model, approve, [](const AgentEvent&) {}).run(mixed, "Rewrite it");
    CHECK(mixed.turns.back().changes.size() == 1 && mixed.turns.back().changes[0].after == "one\ntwo\n");
    Agent::undo_latest(mixed, other);
    CHECK(second.read("calc.py") == "def add(a, b):\n    return a - b\n");
}

static void test_custom_agents_and_commands() {
    Sandbox box;
    box.write(".claude/commands/review.md", "---\ndescription: Review one file\n---\nReview $1 for $2. All: $ARGUMENTS");
    box.write(".claude/commands/tests.md", "Explain the tests.");
    box.write(".claude/commands/undo.md", "not a command");
    box.write(".mason/agents/careful.md", "---\nname: careful\ndescription: Slow and careful\n---\nNever guess.");
    SessionStore store(box.home);
    Session session = store.create(Workspace(box.project.string()).root(), Permission::ReadOnly);

    const std::vector<Definition> commands = customization::commands(store.home(), session.workspace);
    CHECK(commands.size() == 2 && commands[0].name == "review" && commands[1].name == "tests");  // reserved names are skipped
    CHECK(commands[0].description == "Review one file" && commands[0].source == ".claude/commands/review.md");
    CHECK(customization::expand(commands, "/review calc.py \"off by one\"") == std::optional<std::string>("Review calc.py for off by one. All: calc.py \"off by one\""));
    CHECK(customization::expand(commands, "/tests quickly") == std::optional<std::string>("Explain the tests.\n\nquickly"));
    CHECK(!customization::expand(commands, "/missing") && !customization::expand(commands, "review calc.py"));
    CHECK(customization::agents(store.home(), session.workspace).size() == 1);

    session.agent = "careful";
    ScriptedModel model;
    model.replies = {say("Reviewed.")};
    Agent(store, model, nullptr, [](const AgentEvent&) {}).run(session, "/review calc.py bugs");
    const std::string sent = model.seen[0].back().content;
    CHECK(sent.find("Never guess.") != std::string::npos && sent.find("Review calc.py for bugs.") != std::string::npos);
    CHECK(session.turns.back().request == "/review calc.py bugs");  // the session shows what was typed
    CHECK(SessionStore(box.home).sessions()[0].agent == "careful");
}

static void test_compaction() {
    Sandbox box;
    SessionStore store(box.home);
    Session session = store.create(Workspace(box.project.string()).root(), Permission::ReadOnly);
    session.messages = {{"system", "sys", {}, ""}, {"user", "first", {}, ""}, {"assistant", "", {{"a", "read_file", "{\"path\":\"calc.py\"}"}}, ""},
                        {"tool", "1\tdef add", {}, "a"}, {"assistant", "It subtracts.", {}, ""}, {"user", "second", {}, ""},
                        {"assistant", "", {{"b", "read_file", "{}"}}, ""}, {"tool", "more", {}, "b"}};
    CHECK(compaction::cut(session.messages, 1) == 6);  // a tool result stays with the call that asked for it
    ScriptedModel summariser;
    summariser.replies = {say("The user asked about calc.py; add subtracts.")};
    CHECK(compaction::compact(session, summariser, 1));
    CHECK(session.messages.size() == 4 && session.messages[1].role == "user" && session.messages[2].role == "assistant" && session.messages[3].role == "tool");
    CHECK(session.messages[1].content.find("add subtracts") != std::string::npos && session.archived.size() == 5);
    CHECK(summariser.seen[0][1].content.find("calls read_file") != std::string::npos);

    // A manual compaction keeps nothing; the summary travels with the next request.
    session.messages.push_back({"assistant", "Done.", {}, ""});
    summariser.replies = {say("All of it.")};
    CHECK(compaction::compact(session, summariser, 0));
    CHECK(session.messages.size() == 1 && session.pending_note.find("All of it.") != std::string::npos && session.compactions == 2);
    store.save(session);
    CHECK(SessionStore(box.home).sessions()[0].archived.size() == session.archived.size());

    // A run compacts by itself when the context is nearly full.
    Session busy = store.create(Workspace(box.project.string()).root(), Permission::ReadOnly);
    ScriptedModel answers;
    for (int number = 1; number <= 4; number++) answers.replies.push_back(say("Answer."));
    for (int number = 1; number <= 4; number++) Agent(store, answers, nullptr, [](const AgentEvent&) {}).run(busy, "question " + std::to_string(number));
    busy.prompt_tokens = 900;
    CHECK(compaction::needed(busy, 1000) && !compaction::needed(busy, 4000) && !compaction::needed(busy, 0));
    ScriptedModel model;
    model.window = 1000;
    model.replies = {say("Earlier: two questions were answered."), say("Five.")};
    Agent(store, model, nullptr, [](const AgentEvent&) {}).run(busy, "fifth question");
    CHECK(busy.state == "Completed" && busy.compactions == 1 && model.seen[1].size() < 10);
    bool continued = false;
    for (const ChatMessage& message : model.seen[1]) continued = continued || message.content.find("two questions were answered") != std::string::npos;
    CHECK(continued && model.seen[1].back().content.find("fifth question") != std::string::npos);
}

static void test_mcp() {
    Sandbox box;
    fs::create_directories(box.home);
    // The file is read like the one the Windows app writes, whatever the case of its keys.
    std::ofstream(box.home / "mcp-servers.json") << R"([{"Name":"docs","Endpoint":"http://127.0.0.1:8088/mcp","Tools":["search_docs"]},{"name":"off","endpoint":"http://127.0.0.1:9/mcp","tools":[],"enabled":false}])";
    CHECK(mcp::all(box.home).size() == 2);
    const std::vector<McpServer> usable = mcp::enabled(box.home);
    CHECK(usable.size() == 1 && usable[0].name == "docs" && usable[0].allows_tool("search_docs"));
    for (const char* bad : {"http://example.com/mcp", "https://127.0.0.1/mcp", "http://127.0.0.1.evil.example/mcp", "http://user@127.0.0.1/mcp", "http://localhost:1/mcp"}) {
        McpServer server;
        server.name = "x";
        server.endpoint = bad;
        CHECK_THROWS(mcp::validate(server));
    }
    McpServer relative;
    relative.name = "x";
    relative.executable = "relative/program";
    CHECK_THROWS(mcp::validate(relative));
    McpServer one, two;
    one.name = two.name = "same";
    one.endpoint = "http://127.0.0.1:1/mcp";
    two.endpoint = "http://127.0.0.1:2/mcp";
    CHECK_THROWS(mcp::save({one, two}, box.home));
    mcp::save({one}, box.home);
    CHECK(read_file(box.home / "mcp-servers.json").find("\"Name\"") != std::string::npos);  // written with the keys Windows uses

    const std::string python = "/usr/bin/python3";
    const std::string script = (fs::path(__FILE__).parent_path() / "mcp-test-server.py").string();
    if (access(python.c_str(), X_OK) != 0 || !fs::exists(script)) {
        std::cout << "  note: python3 or the MCP test server is missing; the connection checks were skipped.\n";
        return;
    }

    // A server started as a program: listed, called, and held to its allowlist.
    McpServer program;
    program.name = "test";
    program.executable = python;
    program.arguments = {script, "stdio"};
    program.tools = {"echo", "fail"};
    program.resources = {"note://hello"};
    const std::vector<McpTool> offered = mcp::discover(program);
    CHECK(offered.size() == 2 && offered[0].name == "echo" && offered[1].name == "fail");
    CHECK(mcp::request(program, mcp::Operation::Call, "echo", {{"text", "hi"}}).find("echo: hi") != std::string::npos);
    CHECK(mcp::request(program, mcp::Operation::Read, "note://hello").find("hello from the test server") != std::string::npos);
    CHECK_THROWS(mcp::request(program, mcp::Operation::Call, "fail"));
    program.tools = {"fail"};
    CHECK(mcp::request(program, mcp::Operation::Tools).find("\"echo\"") == std::string::npos);  // the agent only sees allowed tools
    CHECK_THROWS(mcp::request(program, mcp::Operation::Call, "echo", {{"text", "hi"}}));
    CHECK_THROWS(mcp::request(program, mcp::Operation::Read, "note://other"));

    // A program that exits without answering is an error straight away, not a wait.
    McpServer silent;
    silent.name = "silent";
    silent.executable = python;
    silent.arguments = {"-c", "pass"};
    const auto started = std::chrono::steady_clock::now();
    CHECK_THROWS(mcp::discover(silent));
    CHECK(std::chrono::steady_clock::now() - started < std::chrono::seconds(10));

    // Over HTTP: the session id from initialize is sent again, and a reply sent as an event stream is read.
    const int port = 18600 + static_cast<int>(getpid() % 300);
    const pid_t child = fork();
    if (child == 0) {
        const int null = open("/dev/null", O_WRONLY);
        dup2(null, STDOUT_FILENO);
        dup2(null, STDERR_FILENO);
        execl(python.c_str(), python.c_str(), script.c_str(), "http", std::to_string(port).c_str(), static_cast<char*>(nullptr));
        _exit(127);
    }
    McpServer web;
    web.name = "web";
    web.endpoint = "http://127.0.0.1:" + std::to_string(port) + "/mcp";
    web.tools = {"echo"};
    std::vector<McpTool> listed;
    for (int attempt = 0; attempt < 40 && listed.empty(); attempt++) {
        try { listed = mcp::discover(web); } catch (const std::exception&) { usleep(150 * 1000); }
    }
    CHECK(listed.size() == 2);
    bool echoed = false;
    try { echoed = mcp::request(web, mcp::Operation::Call, "echo", {{"text", "over http"}}).find("echo: over http") != std::string::npos; } catch (const std::exception& error) { std::cerr << "  " << error.what() << "\n"; }
    CHECK(echoed);
    kill(child, SIGKILL);
    waitpid(child, nullptr, 0);

    // The agent calls an MCP tool only after the user allows it, even in Auto, because the call is not sandboxed.
    program.tools = {"echo"};
    mcp::save({program}, box.home);
    SessionStore store(box.home);
    Session session = store.create(Workspace(box.project.string()).root(), Permission::Auto);
    std::vector<ApprovalRequest> asked;
    ScriptedModel model;
    model.replies = {call("mcp_call", {{"server", "test"}, {"tool", "echo"}, {"arguments_json", "{\"text\":\"from the agent\"}"}}), say("Called it.")};
    Agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request); return true; }, [](const AgentEvent&) {}).run(session, "Use the test server");
    CHECK(asked.size() == 1 && asked[0].subject == "test/echo" && asked[0].detail.find("from the agent") != std::string::npos);
    CHECK(session.activities.back().state == "completed" && session.activities.back().result.find("echo: from the agent") != std::string::npos);
    CHECK(model.seen[0][0].content.find("MCP servers: test") != std::string::npos);

    ScriptedModel declined;
    declined.replies = {call("mcp_call", {{"server", "test"}, {"tool", "echo"}, {"arguments_json", "{}"}}), call("mcp_call", {{"server", "test"}, {"tool", "fail"}, {"arguments_json", "{}"}}), say("Stopped.")};
    Agent(store, declined, [](const ApprovalRequest&) { return false; }, [](const AgentEvent&) {}).run(session, "Again");
    const size_t count = session.activities.size();
    // A declined call is not made, and a tool outside the allowlist is refused without asking.
    CHECK(session.activities[count - 2].state == "denied" && session.activities[count - 1].state == "failed");
}

static void test_chat() {
    Sandbox box;
    fs::create_directories(box.home);
    ChatStore store(box.home);
    ChatConversation conversation = store.create();
    std::ofstream(box.home / "notes.txt", std::ios::binary) << "line one\n```\nfenced\n```";
    std::ofstream(box.home / "image.bin", std::ios::binary) << std::string("\xFF\xFE\x00\x01", 4);
    std::ofstream(box.home / "large.txt", std::ios::binary) << std::string(ChatAttachment::kMaxBytes + 1, 'x');
    CHECK_THROWS(ChatAttachment::from_file(box.home / "image.bin"));
    CHECK_THROWS(ChatAttachment::from_file(box.home / "large.txt"));
    const ChatAttachment notes = ChatAttachment::from_file(box.home / "notes.txt");

    // A chat is saved, carries attachments and offers no tools.
    ScriptedModel model;
    model.replies = {say("It has a fenced block."), say("Second answer.")};
    std::string streamed;
    chat::send(conversation, "What is in this file?", {notes}, model, store, [&](const std::string& text) { streamed += text; });
    CHECK(streamed == "It has a fenced block." && conversation.title == "What is in this file?");
    CHECK(conversation.messages.size() == 3 && conversation.messages[0].content.find("no tools") != std::string::npos);
    CHECK(conversation.messages[1].content.find("Attached file notes.txt:\n````\nline one") != std::string::npos);  // a longer fence around a file with a fence
    chat::send(conversation, "And again?", {}, model, store, nullptr);
    CHECK(model.seen[1].size() == 4 && model.seen[1].back().role == "user");
    {
        ChatStore reloaded(box.home);
        CHECK(reloaded.conversations().size() == 1 && reloaded.conversations()[0].messages.size() == 5 && reloaded.conversations()[0].model == "scripted");
    }

    // A failed request keeps the question, and the next message joins it.
    ChatConversation second = store.create();
    ScriptedModel empty;
    CHECK_THROWS(chat::send(second, "Hello?", {}, empty, store, nullptr));
    CHECK(second.messages.size() == 2 && second.messages[1].role == "user");
    ScriptedModel answers;
    answers.replies = {say("Yes.")};
    chat::send(second, "Still there?", {}, answers, store, nullptr);
    CHECK(second.messages.size() == 3 && second.messages[1].content.find("Hello?") != std::string::npos && second.messages[1].content.find("Still there?") != std::string::npos);
    CHECK_THROWS(chat::send(second, "   ", {}, answers, store, nullptr));

    // A long chat is compacted before the next answer.
    ChatConversation busy = store.create();
    ScriptedModel four;
    for (int number = 1; number <= 4; number++) four.replies.push_back(say("Answer."));
    for (int number = 1; number <= 4; number++) chat::send(busy, "Question " + std::to_string(number), {}, four, store, nullptr);
    busy.prompt_tokens = 900;
    ScriptedModel full;
    full.window = 1000;
    full.replies = {say("Earlier: four questions were answered."), say("Answer 5.")};
    chat::send(busy, "Question 5", {}, full, store, nullptr);
    bool continued = false;
    for (const ChatMessage& message : full.seen[1]) continued = continued || message.content.find("four questions were answered") != std::string::npos;
    CHECK(busy.compactions == 1 && continued && !busy.archived.empty() && full.seen[1].back().content.find("Question 5") != std::string::npos);

    store.remove(conversation.id);
    CHECK(ChatStore(box.home).conversations().size() == 2);
}

static void test_queue_and_steer() {
    Sandbox box;
    SessionStore store(box.home);
    const auto quiet = [](const AgentEvent&) {};

    // A queued message runs after the request completes.
    {
        Session session = store.create(Workspace(box.project.string()).root(), Permission::ReadOnly);
        ScriptedModel model;
        model.replies = {say("One."), say("Two.")};
        Agent agent(store, model, nullptr, quiet);
        model.on_request = [&](size_t number) { if (number == 1) agent.send_while_running("second question", false); };
        agent.run(session, "first question");
        CHECK(session.turns.size() == 2 && session.turns[1].request == "second question" && session.turns[1].answer == "Two.");
        CHECK(session.pending_input.empty());
        CHECK_THROWS(agent.send_while_running("too late", false));  // nothing is running any more
    }

    // A queued message waits after a failure, is saved, and /queue resumes it.
    {
        Session session = store.create(Workspace(box.project.string()).root(), Permission::ReadOnly);
        ScriptedModel failing;
        Agent agent(store, failing, nullptr, quiet);
        failing.on_request = [&](size_t) { agent.send_while_running("do this next", false); };
        agent.run(session, "first question");
        CHECK(session.state == "Failed" && session.pending_input.size() == 1 && session.pending_input[0].text == "do this next");
        bool saved = false;
        const SessionStore reloaded(box.home);
        for (const Session& item : reloaded.sessions()) saved = saved || (item.id == session.id && item.pending_input.size() == 1);
        CHECK(saved);
        ScriptedModel next;
        next.replies = {say("Done next.")};
        Agent(store, next, nullptr, quiet).run(session, "/queue");
        CHECK(session.turns.back().request == "do this next" && session.state == "Completed" && session.pending_input.empty());
        ScriptedModel none;
        Agent(store, none, nullptr, quiet).run(session, "/queue");
        CHECK(session.detail == "No messages are queued.");
    }

    // Steering interrupts the request and is handled next.
    {
        Session session = store.create(Workspace(box.project.string()).root(), Permission::ReadOnly);
        ScriptedModel model;
        model.delay_ms = 300;
        // The first request is interrupted while the model is still working, so it never takes a reply from the script.
        model.replies = {say("Steered answer.")};
        Agent agent(store, model, nullptr, quiet);
        model.on_request = [&](size_t number) { if (number == 1) agent.send_while_running("actually, do this instead", true); };
        const auto started = std::chrono::steady_clock::now();
        agent.run(session, "long task");
        CHECK(session.turns.size() == 2 && session.turns[0].answer.empty() && session.turns[1].request == "actually, do this instead" && session.turns[1].answer == "Steered answer.");
        CHECK(session.state == "Completed");
        size_t user_messages = 0;
        for (const ChatMessage& message : model.seen[1]) user_messages += message.role == "user" ? 1 : 0;
        CHECK(user_messages == 2);  // the model sees both requests in order
        CHECK(std::chrono::steady_clock::now() - started < std::chrono::seconds(5));
    }

    // The caller's stop ends the run, and a message queued before it stays queued.
    {
        Session session = store.create(Workspace(box.project.string()).root(), Permission::ReadOnly);
        ScriptedModel model;
        model.delay_ms = 2000;
        model.replies = {say("Never.")};
        Agent agent(store, model, nullptr, quiet);
        const CancelToken cancel = make_cancel();
        model.on_request = [&](size_t) { agent.send_while_running("later", false); cancel->store(true); };
        agent.run(session, "long task", cancel);
        CHECK(session.state == "Stopped" && session.turns.size() == 1 && session.pending_input.size() == 1);
    }
}

static void test_schedules() {
    setenv("TZ", "UTC", 1);
    tzset();
    Sandbox box;
    box.write("a.py", "x = 1\n");
    const std::string project = Workspace(box.project.string()).root();
    const ModelEndpoint endpoint{"http://127.0.0.1:1/", "scripted", ""};
    auto at = [](const char* text) {
        std::tm parts{};
        strptime(text, "%Y-%m-%dT%H:%M:%SZ", &parts);
        return timegm(&parts);
    };
    auto make = [&](Schedule::Cadence cadence, int hour, int minute, int weekday = 1) {
        Schedule schedule;
        schedule.cadence = cadence;
        schedule.hour = hour;
        schedule.minute = minute;
        schedule.weekday = weekday;
        return schedule;
    };
    // 2026-11-27 is a Friday.
    const std::time_t friday = at("2026-11-27T10:30:00Z");
    CHECK(make(Schedule::Cadence::Hourly, 0, 45).next(friday) == at("2026-11-27T10:45:00Z"));
    CHECK(make(Schedule::Cadence::Hourly, 0, 15).next(friday) == at("2026-11-27T11:15:00Z"));
    CHECK(make(Schedule::Cadence::Daily, 9, 0).next(friday) == at("2026-11-28T09:00:00Z"));
    CHECK(make(Schedule::Cadence::Daily, 18, 0).next(friday) == at("2026-11-27T18:00:00Z"));
    CHECK(make(Schedule::Cadence::Weekdays, 9, 0).next(friday) == at("2026-11-30T09:00:00Z"));  // the weekend is skipped
    CHECK(make(Schedule::Cadence::Weekly, 9, 0, 3).next(friday) == at("2026-12-02T09:00:00Z"));  // the next Wednesday
    CHECK(make(Schedule::Cadence::Weekly, 9, 0, 3).summary() == "Every Wednesday at 09:00" && make(Schedule::Cadence::Hourly, 0, 5).summary() == "Every hour at :05");

    // The /schedule command adds, lists, switches and removes.
    CHECK(starts_with(schedules::command("", box.home, project, endpoint), "No schedules."));
    const std::string added = schedules::command("add tidy weekly wed 09:00 auto -- Run the linter -- and fix what it finds", box.home, project, endpoint, friday);
    CHECK(added.find("every wednesday at 09:00") != std::string::npos && added.find("Auto") != std::string::npos);
    schedules::command("add report hourly :45 -- Read a.py and report", box.home, project, endpoint, friday);
    std::vector<Schedule> saved = schedules::load(box.home);
    CHECK(saved.size() == 2 && saved[0].name == "tidy" && saved[0].prompt == "Run the linter -- and fix what it finds" && saved[0].automatic && !saved[1].automatic);
    CHECK(saved[1].cadence == Schedule::Cadence::Hourly && saved[1].created == friday);
    CHECK(schedules::command("list", box.home, project, endpoint, friday).find("report  ·  Every hour at :45  ·  read-only") != std::string::npos);
    for (const char* bad : {"add tidy daily 09:00 -- again", "add x daily 25:00 -- p", "add x weekly 09:00 -- p", "add x daily 09:00", "add two words daily 09:00 -- p", "remove nothing", "nonsense"}) {
        CHECK_THROWS(schedules::command(bad, box.home, project, endpoint, friday));
    }
    CHECK_THROWS(schedules::command("add x daily 09:00 -- p", box.home, project, std::nullopt));  // a schedule needs a model
    CHECK(schedules::command("off tidy", box.home, project, endpoint) == "tidy is off.");
    CHECK(schedules::command("remove tidy", box.home, project, endpoint) == "Removed tidy.");

    // A due schedule runs once, unattended, and records its outcome.
    CHECK(schedules::due(schedules::load(box.home), at("2026-11-27T10:40:00Z")).empty());
    const std::vector<Schedule> due = schedules::due(schedules::load(box.home), at("2026-11-27T13:50:00Z"));  // three hours with nothing checking
    CHECK(due.size() == 1 && due[0].name == "report");
    SessionStore store(box.home);
    ScriptedModel model;
    model.replies = {call("read_file", {{"path", "a.py"}}), call("write_file", {{"path", "a.py"}, {"content", "x = 2\n"}}), say("a.py sets x to 1.")};
    const std::optional<Session> session = schedules::run(due[0], box.home, store, &model);
    CHECK(session && session->title == "Scheduled: report" && session->state == "Completed");
    CHECK(session && session->activities.size() == 2 && session->activities[1].state == "failed");  // a read-only scheduled run cannot edit
    CHECK(box.read("a.py") == "x = 1\n");
    const Schedule after = schedules::load(box.home)[0];
    CHECK(after.last_outcome == "Completed" && session && after.last_session == session->id && after.last_run > 0);
    CHECK(after.next(after.last_run) > after.last_run);  // the missed hours are not made up

    // A model that is not answering is recorded as skipped, and no session is made.
    CHECK(!schedules::run(after, box.home, store));
    CHECK(starts_with(schedules::load(box.home)[0].last_outcome, "Skipped:"));
}

static ModelReply raw_call(const std::string& name, const std::string& arguments) {
    ModelReply reply;
    reply.tool_calls.push_back({new_id(), name, arguments});
    return reply;
}

static void test_extra_tools() {
    const auto approve = [](const ApprovalRequest&) { return true; };
    const auto quiet = [](const AgentEvent&) {};

    // A question stops the request until the user answers.
    {
        Sandbox box;
        SessionStore store(box.home);
        Session session = store.create(Workspace(box.project.string()).root(), Permission::ReadOnly);
        ScriptedModel model;
        model.replies = {call("ask_user", {{"question", "Which currency should totals use?"}}), say("Using EUR.")};
        Agent(store, model, approve, quiet).run(session, "Add totals");
        CHECK(session.state == "Needs your answer" && session.turns[0].answer == "Which currency should totals use?" && model.seen.size() == 1);
        Agent(store, model, approve, quiet).run(session, "EUR");
        CHECK(session.state == "Completed" && session.turns[1].answer == "Using EUR." && model.seen[1].back().content == "EUR");
    }

    // The repository map lists what each file defines.
    {
        Sandbox box;
        box.write("src/cart.ts", "export function total(items) {}\nclass Cart {}\nconst TAX = 0.2\n");
        box.write("src/money.rs", "pub struct Money {}\nfn format(value: Money) -> String { String::new() }\n");
        box.write("notes.txt", "just words\n");
        const Workspace workspace(box.project.string());
        const std::string map = repository_map::outline(workspace);
        CHECK(map.find("calc.py  (3 lines): add") != std::string::npos);
        CHECK(map.find("src/cart.ts  (4 lines): total, Cart, TAX") != std::string::npos);
        CHECK(map.find("src/money.rs  (3 lines): Money, format") != std::string::npos);
        CHECK(map.find("notes.txt  (2 lines)") != std::string::npos && map.find("notes.txt  (2 lines):") == std::string::npos);
        CHECK(repository_map::outline(workspace, "src").find("calc.py") == std::string::npos);
    }

    // Line ranges, several edits at once, a delegated question, and Git status outside a repository.
    {
        Sandbox box;
        std::string big;
        for (int line = 1; line <= 500; line++) big += "line " + std::to_string(line) + (line < 500 ? "\n" : "");
        box.write("big.txt", big);
        SessionStore store(box.home);
        Session session = store.create(Workspace(box.project.string()).root(), Permission::WorkspaceEdits);
        ScriptedModel model;
        model.replies = {
            call("read_lines", {{"path", "big.txt"}, {"start", "250"}, {"count", 3}}),
            raw_call("apply_edits", R"({"path":"calc.py","edits":[{"old_text":"a - b","new_text":"a + b"},{"old_text":"def add","new_text":"def plus"}]})"),
            raw_call("apply_edits", R"({"path":"calc.py","edits":[{"old_text":"plus","new_text":"sum"},{"old_text":"not there","new_text":"x"}]})"),
            raw_call("delegate_readonly", R"({"question":"What does it return?","paths":["calc.py"]})"),
            say("calc.py:2 returns a + b."),
            call("git_status", json::object()),
            say("Done."),
        };
        Agent(store, model, approve, quiet).run(session, "Work on it");
        CHECK(session.state == "Completed" && session.activities.size() == 5);
        CHECK(starts_with(session.activities[0].result, "250\tline 250\n251\tline 251\n252\tline 252\n") && ends_with(session.activities[0].result, "[lines 250–252 of 500]"));
        CHECK(session.activities[1].state == "completed" && session.activities[2].state == "failed");
        CHECK(box.read("calc.py") == "def plus(a, b):\n    return a + b\n");  // the second batch changed nothing because one edit did not apply
        CHECK(session.activities[2].result.find("Edit 2") != std::string::npos && session.activities[2].result.find("Nothing was changed") != std::string::npos);
        CHECK(session.activities[3].result == "calc.py:2 returns a + b.");
        CHECK(model.seen[4].size() == 2 && model.seen[4][1].content.find("2\t    return a + b") != std::string::npos);  // the separate context sees only the files and the question
        CHECK(session.activities[4].result == "This project is not a Git repository.");
        CHECK(session.turns[0].changes.size() == 1);
        Agent::undo_latest(session, store);
        CHECK(box.read("calc.py") == "def add(a, b):\n    return a - b\n");
    }

    // A command with exact arguments, search paging, and a terminal that is answered line by line.
    {
        Sandbox box;
        std::string many;
        for (int i = 1; i <= 150; i++) many += "needle " + std::to_string(i) + "\n";
        box.write("many.txt", many);
        SessionStore store(box.home);
        Session session = store.create(Workspace(box.project.string()).root(), Permission::WorkspaceEdits);
        std::vector<std::string> asked;
        ScriptedModel model;
        model.replies = {call("run_command", {{"program", "printf"}, {"arguments", json::array({"%s|", "a b", "$HOME", "it's", "*"})}, {"directory", "."}}),
                         call("search_files", {{"query", "needle"}}), call("search_next", {{"cursor", "100"}}), say("Done.")};
        Agent agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request.tool); return true; }, quiet);
        agent.run(session, "Try them");
        CHECK(asked == std::vector<std::string>{"run_command"});  // a command with exact arguments asks like any command outside Auto
        CHECK(session.activities.size() == 3);
        if (session.activities.size() == 3) {
            CHECK(session.activities[0].result.find("a b|$HOME|it's|*|") != std::string::npos);  // nothing in an argument is expanded or split
            const std::string first = session.activities[1].result, second = session.activities[2].result;
            CHECK(first.find("many.txt:100: needle 100") != std::string::npos && first.find("needle 101") == std::string::npos && first.find("search_next and cursor \"100\"") != std::string::npos);
            CHECK(second.find("many.txt:101: needle 101") != std::string::npos && second.find("needle 150") != std::string::npos && second.find("search_next") == std::string::npos);
        }

        Jobs jobs;
        const Workspace workspace(box.project.string());
        const std::string id = jobs.start("read a; echo got $a; read b; echo end $b", workspace, false, true);
        jobs.send(id, "one", false);
        std::string seen;
        for (int i = 0; i < 40 && seen.find("got one") == std::string::npos; i++) seen = jobs.observe(id, false, 250);
        CHECK(seen.find("got one") != std::string::npos && seen.find("running") != std::string::npos);  // the program answered the line and waits for the next
        jobs.send(id, "", true);
        seen = jobs.observe(id, false, 5000);
        CHECK(seen.find("exited 0") != std::string::npos && seen.find("end") != std::string::npos);  // closing the input lets the program finish
        bool refused = false;
        try { jobs.send(id, "late", false); } catch (const Error&) { refused = true; }
        CHECK(refused);  // a terminal that has ended takes no more input
        const std::string plain = jobs.start("sleep 30", workspace, false);
        refused = false;
        try { jobs.send(plain, "x", false); } catch (const Error&) { refused = true; }
        CHECK(refused);  // a job started without input takes none
        // A program that closed its input: the line fails as an error and Mason keeps running.
        const std::string closed = jobs.start("exec 0<&-; sleep 30", workspace, false, true);
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
        refused = false;
        try { jobs.send(closed, "anyone", false); } catch (const Error&) { refused = true; }
        CHECK(refused);
        jobs.stop_all();
    }

    // A job runs in the background and stops when the request ends.
    {
        Sandbox box;
        SessionStore store(box.home);
        Session session = store.create(Workspace(box.project.string()).root(), Permission::WorkspaceEdits);
        std::vector<std::string> asked;
        ScriptedModel model;
        model.replies = {call("start_job", {{"command", "for i in 1 2 3; do echo tick $i; sleep 0.1; done"}}), say("placeholder"), call("start_job", {{"command", "sleep 60"}}), say("Started.")};
        // The second step polls the job the first step started, using the id it returned.
        model.on_request = [&](size_t number) {
            if (number != 2) return;
            const std::string started = model.seen.back().back().content;  // "Started job ID. Read it ..."
            const std::string id = started.substr(12, 8);
            model.replies[0] = call("poll_job", {{"id", id}, {"wait_ms", 3000}});
        };
        Agent agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request.tool); return true; }, quiet);
        const auto began = std::chrono::steady_clock::now();
        agent.run(session, "Start them");
        CHECK(session.state == "Completed" && asked == (std::vector<std::string>{"start_job", "start_job"}));  // starting a job asks, like any command outside Auto
        const std::string polled = session.activities.size() > 1 ? session.activities[1].result : "";
        CHECK(polled.find("exited 0") != std::string::npos && polled.find("tick 1") != std::string::npos && polled.find("tick 3") != std::string::npos);
        CHECK(agent.jobs_empty());  // every job is stopped when the request ends
        CHECK(std::chrono::steady_clock::now() - began < std::chrono::seconds(20));  // the 60 second job did not hold the request open
    }

    // Project workflows and skills are offered only when the project has them.
    {
        Sandbox box;
        SessionStore store(box.home);
        const Workspace workspace(box.project.string());
        Session session = store.create(workspace.root(), Permission::WorkspaceEdits);
        auto names = [&](const Session& item) {
            std::vector<std::string> found;
            for (const ToolSpec& tool : Agent::extra_tools(item, workspace)) found.push_back(tool.name);
            return found;
        };
        auto has = [](const std::vector<std::string>& list, const char* name) { return std::find(list.begin(), list.end(), name) != list.end(); };
        CHECK(!has(names(session), "run_workflow") && !has(names(session), "read_skill"));
        box.write("mason.workflows.json", R"({"version": 1, "workflows": {"stamp": {"executable": "sh", "arguments": ["-c", "printf done > stamp.txt; echo \"it's stamped\""]}}})");
        box.write(".mason/skills/release/SKILL.md", "---\ndescription: How we cut a release\n---\nTag, then publish.");
        CHECK(has(names(session), "run_workflow") && has(names(session), "read_skill"));
        CHECK(workflows::command_line({"sh", {"-c", "echo it's"}, "src"}) == R"(cd 'src' && 'sh' '-c' 'echo it'\''s')");

        std::vector<std::string> asked;
        ScriptedModel model;
        model.replies = {call("read_skill", {{"name", "release"}}), call("run_workflow", {{"name", "stamp"}}), call("run_workflow", {{"name", "missing"}}), say("Done.")};
        Agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request.subject); return true; }, quiet).run(session, "Release it");
        CHECK(model.seen[0][0].content.find("release (How we cut a release)") != std::string::npos);  // the model is told which skills exist
        CHECK(session.activities.size() == 3 && session.activities[0].result.find("Tag, then publish.") != std::string::npos);
        CHECK(asked == std::vector<std::string>{"workflow stamp"});
        CHECK(session.activities[1].state == "completed" && session.activities[1].result.find("it's stamped") != std::string::npos && session.activities[1].result.find("stamp.txt") != std::string::npos);
        CHECK(session.activities[2].state == "failed");
        CHECK(session.turns[0].changes.size() == 1 && session.turns[0].changes[0].path == "stamp.txt");  // what a workflow changes can be undone

        Session reading = store.create(workspace.root(), Permission::ReadOnly);
        reading.ask_only = true;
        // check_page is offered wherever a browser is installed, which depends on the computer.
        std::vector<std::string> offered = names(reading);
        offered.erase(std::remove(offered.begin(), offered.end(), "check_page"), offered.end());
        offered.erase(std::remove(offered.begin(), offered.end(), "inspect_symbol"), offered.end());
        CHECK(offered == (std::vector<std::string>{"read_lines", "git_status", "delegate_readonly", "repository_map", "ask_user", "navigate_code", "read_skill"}));  // no editing, job or workflow tools
        box.write("mason.workflows.json", "{\"version\": 2}");
        CHECK_THROWS(workflows::load(workspace));
    }
}

static void test_browser() {
    Sandbox box;
    box.write("site/index.html", "<!DOCTYPE html><title>Counter</title><input id=\"name\" placeholder=\"Name\"><button onclick=\"add()\">Add</button><p id=\"out\">Total 0</p>"
                                 "<script>var n=0;function add(){n++;document.getElementById('out').textContent='Total '+n+' for '+document.getElementById('name').value}</script>");
    box.write("site/broken.html", "<title>Broken</title><p>Shown</p><script>missing()</script>");
    box.write("site/app.js", "var x = 1;");
    box.write("notes.txt", "plain");
    fs::path outside = box.base / "secret.html";
    std::ofstream(outside) << "<p>secret</p>";
    const Workspace workspace(box.project.string());

    // The server sends project files only, and pages record errors from their first script on.
    const browser::Reply page = browser::respond("/site/index.html?x=1", workspace);
    CHECK(page.status == 200 && starts_with(page.content_type, "text/html") && page.body.find("<!DOCTYPE html><script>window.__masonErrors") == 0);
    CHECK(browser::respond("/site/broken.html", workspace).body.find("<script>window.__masonErrors") == 0);
    CHECK(browser::respond("/site/", workspace).status == 200);
    CHECK(browser::respond("/site/app.js", workspace).body == "var x = 1;" && starts_with(browser::respond("/site/app.js", workspace).content_type, "text/javascript"));
    CHECK(browser::respond("/__mason/harness?page=x", workspace).body.find("mason-result") != std::string::npos);
    CHECK(browser::respond("/../secret.html", workspace).status == 404);
    CHECK(browser::respond("/%2e%2e/secret.html", workspace).status == 404);
    CHECK(browser::respond("/site/missing.html", workspace).status == 404);

    if (browser::find().empty()) {
        std::cout << "  (no browser on this computer: the real-browser part was not run)\n";
        return;
    }
    CHECK_THROWS(browser::run(workspace, "notes.txt"));
    CHECK_THROWS(browser::run(workspace, "../secret.html"));

    const json steps = json::array({{{"action", "fill"}, {"selector", "#name"}, {"value", "Ada"}}, {{"action", "click"}, {"text", "Add"}}, {{"action", "click"}, {"text", "Add"}},
                                    {{"action", "expect_text"}, {"text", "Total 2 for Ada"}}, {{"action", "expect_no_text"}, {"text", "Total 0"}}});
    const browser::Result good = browser::run(workspace, "site/index.html", steps);
    CHECK(good.passed() && good.title == "Counter" && good.steps.size() == 5 && good.text.find("Total 2 for Ada") != std::string::npos);
    CHECK(starts_with(browser::report(good, "site/index.html"), "PASSED: site/index.html"));
    CHECK(good.controls == (std::vector<std::string>{"#name | input | Name", "button | button | Add"}));
    CHECK(browser::report(good, "site/index.html").find("Controls (selector | kind | label):\n  #name | input | Name") != std::string::npos);

    const browser::Result missing = browser::run(workspace, "site/index.html", json::array({{{"action", "click"}, {"text", "Remove"}}, {{"action", "expect_text"}, {"text", "never"}}}));
    CHECK(!missing.passed() && missing.steps.size() == 1 && !missing.steps[0].ok);

    const browser::Result broken = browser::run(workspace, "site/broken.html");
    CHECK(!broken.passed() && broken.errors.size() == 1 && broken.errors[0].find("missing") != std::string::npos && broken.text.find("Shown") != std::string::npos);

    // The agent asks before opening a page outside Auto, and reports a failing page as failed.
    SessionStore store(box.home);
    Session session = store.create(workspace.root(), Permission::ReviewEdits);
    ScriptedModel model;
    model.replies = {call("check_page", {{"path", "site/index.html"}, {"steps_json", "[{\"action\":\"click\",\"text\":\"Add\"},{\"action\":\"expect_text\",\"text\":\"Total 1\"}]"}}),
                     call("check_page", {{"path", "site/broken.html"}, {"steps_json", "[]"}}), say("Checked.")};
    std::vector<std::string> asked;
    Agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request.subject); return true; }, [](const AgentEvent&) {}).run(session, "Check the pages");
    CHECK(asked == std::vector<std::string>{"site/index.html", "site/broken.html"});
    CHECK(session.activities.size() == 2 && session.activities[0].state == "completed" && session.activities[1].state == "failed");
    CHECK(session.activities[1].result.find("Script errors:") != std::string::npos);
}

/// Embeds text as counts of a few known words, so ranking is predictable.
struct WordModels : SemanticModels {
    bool reranks = false;
    size_t embedded = 0;
    std::string identity() const override { return "words"; }
    bool can_rerank() const override { return reranks; }
    std::vector<std::vector<float>> embed(const std::vector<std::string>& texts, const CancelToken&) override {
        embedded += texts.size();
        std::vector<std::vector<float>> vectors;
        for (const std::string& text : texts) {
            std::vector<float> vector;
            const std::string lower = to_lower(text);
            for (const char* word : {"invoice", "total", "tax", "login", "password", "color"}) {
                float count = 0;
                for (size_t at = lower.find(word); at != std::string::npos; at = lower.find(word, at + 1)) count++;
                vector.push_back(count);
            }
            vector.push_back(0.01f);
            vectors.push_back(vector);
        }
        return vectors;
    }
    std::vector<double> rerank(const std::string&, const std::vector<std::string>& documents, const CancelToken&) override {
        std::vector<double> scores;
        for (const std::string& document : documents) scores.push_back(document.find("File: auth.py") != std::string::npos ? 9 : 0);
        return scores;
    }
};

static void test_planning() {
    const auto approve = [](const ApprovalRequest&) { return true; };
    const auto quiet = [](const AgentEvent&) {};
    const auto states = [](const Workspace& workspace, const std::vector<Milestone>& plan) {
        std::vector<std::string> found;
        for (const MilestoneStatus& status : milestones::inspect(workspace, plan)) found.push_back(status.state);
        return found;
    };
    using Names = std::vector<std::string>;

    // Comments travel with the next request while their files are unchanged.
    {
        Sandbox box;
        const Workspace workspace(box.project.string());
        SessionStore store(box.home);
        Session session = store.create(workspace.root(), Permission::WorkspaceEdits);
        CHECK_THROWS(review::comment(workspace, "calc.py", 3, "x"));
        CHECK_THROWS(review::comment(workspace, "calc.py", 1, "  "));
        const ReviewComment made = review::comment(workspace, "calc.py", 2, "This subtracts.");
        CHECK(made.quote == "    return a - b");
        const ReviewComment removed = review::comment(workspace, "calc.py", 7, "Why was this removed?", std::string("old()"));
        CHECK(review::location(removed) == "calc.py · original line 7");
        session.comments = {made, removed};
        ScriptedModel model;
        model.replies = {say("Fixed."), say("Again.")};
        Agent(store, model, approve, quiet).run(session, "Fix it");
        const std::string sent = model.seen[0].back().content;
        CHECK(starts_with(sent, "Fix it\ncalc.py · updated line 2 (return a - b) — This subtracts.\ncalc.py · original line 7 (old()) — Why was this removed?"));
        CHECK(session.comments.empty() && session.turns[0].request == sent);

        session.comments = {review::comment(workspace, "calc.py", 1, "Rename it.")};
        box.write("calc.py", "def add(a, b):\n    return a + b\n");
        Agent(store, model, approve, quiet).run(session, "");
        CHECK(session.state == "Failed" && session.detail.find("changed since the comment was written") != std::string::npos && session.comments.size() == 1);
        session.comments = {review::comment(workspace, "calc.py", 1, "Rename it.")};
        store.save(session);
        CHECK(SessionStore(box.home).sessions().front().comments.size() == 1);  // comments are saved with the session
        Agent(store, model, approve, quiet).run(session, " ");
        CHECK(session.state == "Completed" && starts_with(model.seen[1].back().content, "Address these review comments.\ncalc.py · updated line 1"));

        // The typed form.
        CHECK(starts_with(review::command("", workspace, session), "No comments are waiting."));
        CHECK_THROWS(review::command("calc.py nothing", workspace, session));
        CHECK(review::command("calc.py:2 Use plus here.", workspace, session) == "Noted on calc.py · updated line 2: return a + b");
        CHECK(starts_with(review::command("", workspace, session), "1. calc.py · updated line 2 — Use plus here."));
        CHECK(review::command("remove 1", workspace, session) == "Dropped the comment on calc.py · updated line 2." && session.comments.empty());
    }

    // Milestones follow the files they cover.
    {
        Sandbox box;
        const Workspace workspace(box.project.string());
        box.write("mason.workflows.json", R"({"version": 1, "workflows": {"tests": {"executable": "sh", "arguments": ["-c", "grep -q 'a + b' calc.py"]}}})");
        std::vector<Milestone> plan;
        Milestone first;
        first.title = "Add works"; first.decision = "Use +"; first.files = {"./calc.py"}; first.workflow = "tests";
        Milestone bad = first;
        bad.id = new_id(); bad.title = "";
        CHECK_THROWS(milestones::put(workspace, plan, bad));
        bad.title = "Outside"; bad.files = {"../x"};
        CHECK_THROWS(milestones::put(workspace, plan, bad));
        plan = milestones::put(workspace, plan, first);
        CHECK(plan[0].files == Names{"calc.py"});
        Milestone second;
        second.title = "Docs"; second.files = {"calc.py"}; second.depends_on = {first.id};
        plan = milestones::put(workspace, plan, second);
        first.depends_on = {second.id};
        CHECK_THROWS(milestones::put(workspace, plan, first));  // a circle is refused
        CHECK(states(workspace, plan) == (Names{"Ready", "Blocked"}));
        CHECK(milestones::context(workspace, plan).find("Next: Add works") != std::string::npos);

        CHECK_THROWS(milestones::check(workspace, plan[0], plan, false, false, ""));  // Mason may not run the workflow
        CHECK_THROWS(milestones::check(workspace, plan[0], plan, true, false, ""));   // the workflow fails
        CHECK_THROWS(milestones::check(workspace, plan[1], plan, true, false, "Looks right"));  // blocked by its prerequisite
        box.write("calc.py", "def add(a, b):\n    return a + b\n");
        plan[0].receipt = milestones::check(workspace, plan[0], plan, true, false, "");
        CHECK(states(workspace, plan) == (Names{"Verified", "Ready"}));
        CHECK_THROWS(milestones::check(workspace, plan[1], plan, true, false, ""));  // a manual check needs an observation
        plan[1].receipt = milestones::check(workspace, plan[1], plan, false, false, "Read the docstring.");
        CHECK(states(workspace, plan) == (Names{"Verified", "Reviewed"}));
        box.write("calc.py", "def add(a, b):\n    return a + b  # sum\n");
        CHECK(states(workspace, plan) == (Names{"Needs recheck", "Blocked"}));
        plan = milestones::remove(plan[0].id, plan);
        CHECK(plan.size() == 1 && states(workspace, plan) == (Names{"Ready"}));

        // The model is told the plan, and the plan is saved with the session.
        SessionStore store(box.home);
        Session session = store.create(workspace.root(), Permission::ReadOnly);
        session.milestones = plan;
        ScriptedModel model;
        model.replies = {say("Ok.")};
        Agent(store, model, approve, quiet).run(session, "What is next?");
        CHECK(model.seen[0][0].content.find("The user's saved milestones") != std::string::npos && model.seen[0][0].content.find("- Docs [Ready]") != std::string::npos);
        const std::vector<Session> saved = SessionStore(box.home).sessions();
        CHECK(saved.front().milestones.size() == 1 && saved.front().milestones[0].title == "Docs");

        // The typed form.
        Session typed = store.create(workspace.root(), Permission::ReviewEdits);
        box.write("mason.workflows.json", R"({"version": 1, "workflows": {"tests": {"executable": "sh", "arguments": ["-c", "true"]}}})");
        CHECK(milestones::command("add Add works | calc.py | workflow tests | decision Use plus", workspace, typed, box.home) == "Added milestone 1: Add works");
        milestones::command("add Documented | calc.py | after 1", workspace, typed, box.home);
        CHECK_THROWS(milestones::command("add Bad | calc.py | workflow missing", workspace, typed, box.home));
        const std::string listed = milestones::command("", workspace, typed, box.home);
        CHECK(listed.find("1. [Ready] Add works  ·  calc.py  ·  workflow tests") != std::string::npos && listed.find("2. [Blocked] Documented  ·  calc.py  ·  after 1") != std::string::npos);
        CHECK_THROWS(milestones::command("check 1", workspace, typed, box.home));  // outside Auto a workflow needs a saved rule
        ApprovalRules(box.home, typed.workspace).add("run_workflow", "workflow tests");
        CHECK(milestones::command("check 1", workspace, typed, box.home) == "Add works: Verified.");
        CHECK(milestones::command("check 2 Read it through.", workspace, typed, box.home) == "Documented: Reviewed.");
        milestones::command("remove 1", workspace, typed, box.home);
        CHECK(typed.milestones.size() == 1 && typed.milestones[0].title == "Documented" && typed.milestones[0].depends_on.empty());
    }

    // Code is found by meaning, with each passage embedded once.
    {
        Sandbox box;
        const Workspace workspace(box.project.string());
        box.write("billing.py", "def invoice_total(items):\n    # the invoice total with tax\n    return sum(items) * 1.2\n");
        box.write("auth.py", "def login(user, password):\n    # check the password for a login\n    return user == 'ada'\n");
        box.write("node_modules/x/invoice.js", "function invoice() { return 'invoice invoice invoice total total'; }\n");
        box.write("logo.png", "invoice total invoice total invoice total");
        auto models = std::make_shared<WordModels>();
        const SemanticResult result = semantic::search(workspace, *models, "where is the invoice total and tax worked out");
        CHECK(!result.hits.empty() && result.hits[0].path == "billing.py" && result.hits[0].start_line == 1 && result.files == 3);
        for (const SemanticHit& hit : result.hits) CHECK(hit.path.find("node_modules") == std::string::npos && !ends_with(hit.path, ".png"));
        CHECK(result.report().find("billing.py lines 1–") != std::string::npos && result.report().find("invoice_total") != std::string::npos);
        const size_t first = models->embedded;
        semantic::search(workspace, *models, "login");
        CHECK(models->embedded == first + 1);  // only the new question is embedded the second time
        models->reranks = true;
        const SemanticResult reordered = semantic::search(workspace, *models, "invoice total");
        CHECK(reordered.reranked && reordered.hits[0].path == "auth.py");  // the reranker's order wins
        models->reranks = false;
        std::string hundred;
        for (int i = 1; i <= 100; i++) hundred += "line number " + std::to_string(i) + (i < 100 ? "\n" : "");
        const std::vector<semantic::Chunk> pieces = semantic::chunks("a.py", hundred);
        CHECK(pieces.size() == 3 && pieces[0].start == 1 && pieces[1].start == 33 && pieces[2].start == 65);

        // The tool is offered only when an embeddings server is chosen, and the agent can call it.
        SessionStore store(box.home);
        Session session = store.create(workspace.root(), Permission::ReadOnly);
        const auto offered = [&](bool semantic) {
            for (const ToolSpec& tool : Agent::extra_tools(session, workspace, semantic)) {
                if (tool.name == "semantic_search") return true;
            }
            return false;
        };
        CHECK(!offered(false) && offered(true));
        CHECK(!semantic::load(box.home));
        CHECK_THROWS(semantic::save(EmbeddingSettings{"ftp://x", "m", "", "", ""}, box.home));
        CHECK(starts_with(semantic::command("", box.home), "Code search by meaning is off"));
        CHECK_THROWS(semantic::command("http://example.com/ m", box.home));  // only servers on this computer
        semantic::command("http://127.0.0.1:9/ embed", box.home);
        semantic::command("rerank http://127.0.0.1:9/ ranker", box.home);
        CHECK(semantic::load(box.home) == std::optional<EmbeddingSettings>(EmbeddingSettings{"http://127.0.0.1:9/", "embed", "", "http://127.0.0.1:9/", "ranker"}));
        CHECK_THROWS(semantic::command("test", box.home));  // nothing answers on that port
        semantic::command("off", box.home);
        CHECK(!semantic::load(box.home));
        ScriptedModel model;
        model.replies = {call("semantic_search", {{"query", "invoice total"}, {"path", "."}}), say("Found.")};
        Agent agent(store, model, approve, quiet);
        agent.set_semantic(models);
        agent.run(session, "Where are totals?");
        CHECK(session.activities.size() == 1 && session.activities[0].state == "completed" && session.activities[0].result.find("billing.py lines") != std::string::npos);
    }
}

static void test_transformers() {
    // A stand-in for Python: it writes the status file the real server writes, then serves the stub model.
    Sandbox box;
    const fs::path folder = box.base / "hub" / "models--acme--tiny-coder" / "snapshots" / "abc", scripts = box.base / "scripts";
    fs::create_directories(folder);
    fs::create_directories(scripts);
    for (const char* name : {"config.json", "tokenizer_config.json", "tokenizer.json"}) std::ofstream(folder / name) << "{}";
    std::ofstream(folder / "model.safetensors") << std::string(4096, '\0');
    std::ofstream(scripts / "Serve-Transformers.py") << "# stand-in";
    const fs::path stub = fs::path(__FILE__).parent_path().parent_path().parent_path() / "macos" / "Tests" / "stub-model.py";
    const fs::path python = box.base / "fake-python";
    std::ofstream(python) << "#!/bin/sh\n"
                             "while [ $# -gt 0 ]; do case \"$1\" in --model-id) id=\"$2\"; shift;; --port) port=\"$2\"; shift;; --manifest) manifest=\"$2\"; shift;; --model-dir) dir=\"$2\"; shift;; --check-only) check=1;; esac; shift; done\n"
                             "if [ -f \"$dir/broken\" ]; then printf '{\"status\": \"Tokenizer files are missing. Use the complete local model folder.\", \"error\": true}' > \"$manifest\"; exit 1; fi\n"
                             "printf '{\"status\": \"Setup ready.\", \"contextTokens\": 2048}' > \"$manifest\"\n"
                             "[ -n \"$check\" ] && exit 0\n"
                             "STUB_NAME=\"$id\" exec python3 '" + stub.string() + "' \"$port\"\n";
    fs::permissions(python, fs::perms::owner_all);
    setenv("MASON_TRANSFORMERS_DIR", scripts.c_str(), 1);
    setenv("MASON_PYTHON", python.c_str(), 1);
    setenv("MASON_DATA_HOME", (box.base / "data").c_str(), 1);

    const std::vector<LocalModel> listed = scan_models({{"Hugging Face cache", box.base / "hub"}});
    CHECK(listed.size() == 1 && listed[0].name == "acme/tiny-coder" && listed[0].transformers && listed[0].startable() && listed[0].bytes == 4096);
    CHECK(listed[0].verdict() == "Coding agent · Transformers weights · runs through Python with PyTorch");
    CHECK_THROWS(transformers::model_at(box.base.string()));  // a folder without weights is not a model
    CHECK(transformers::model_at(folder.string()).name == "acme/tiny-coder");
    if (listed.empty() || !fs::exists(stub) || access("/usr/bin/python3", X_OK) != 0) {
        std::cout << "  (python3 or the stub model is missing: serving was not run)\n";
    } else {
        CHECK(starts_with(transformers::check(listed[0]), "Setup ready. Context: 2048 tokens."));
        {
            const std::unique_ptr<ModelServer> server = ModelServer::start(listed[0], "", 0, false, nullptr);
            CHECK(server->context == 2048);  // the context the server settled on
            const auto names = OpenAIClient::models(server->endpoint.url);
            CHECK(names && *names == std::vector<std::string>{server->endpoint.model});
            const std::string url = server->endpoint.url;
            server->stop();
            bool gone = false;
            for (int i = 0; i < 40 && !gone; i++) {
                gone = !OpenAIClient::models(url, "", 0.3);
                if (!gone) std::this_thread::sleep_for(std::chrono::milliseconds(200));
            }
            CHECK(gone);  // stopping ends the Python process
        }
        // What the Python program reports is what the user is told.
        std::ofstream(folder / "broken") << "";
        std::string said;
        try { ModelServer::start(listed[0], "", 0, false, nullptr); } catch (const Error& error) { said = error.what(); }
        CHECK(said == "Tokenizer files are missing. Use the complete local model folder.");
    }
    unsetenv("MASON_TRANSFORMERS_DIR");
    unsetenv("MASON_PYTHON");
    unsetenv("MASON_DATA_HOME");
}

/// A window with a text field, a button and a label that shows what was last saved.
struct FakeDesktop : DesktopAutomation {
    bool trusted = true;
    std::string field, saved;
    std::vector<std::string> acted;
    DesktopWindow window{42, "Notes", "Shopping"};
    std::string unavailable() override { return trusted ? "" : "The accessibility service is not answering."; }
    std::vector<DesktopWindow> windows() override { return {DesktopWindow{43, "Calculator", "Calculator"}, window}; }
    std::vector<DesktopControl> observe(const DesktopWindow&) override {
        return {{"c1", "text", "Item", field, false, true}, {"c2", "push button", "Save", "", true, false}, {"c3", "label", "", saved.empty() ? "Nothing saved" : "Saved " + saved, false, false}};
    }
    void act(const DesktopWindow&, const std::string& control, const std::string& operation, const std::string& value) override {
        acted.push_back(operation + " " + control + " " + value);
        if (control == "c1") field = value; else saved = field;
    }
    /// The window's picture changes whenever something is drawn on its canvas.
    int strokes = 0;
    std::optional<DesktopFrame> frame(const DesktopWindow&) override { return DesktopFrame{200, 100, "picture " + std::to_string(strokes)}; }
    void pointer(const DesktopWindow&, const std::string& action, int x, int y, int end_x, int end_y) override {
        const std::string at = std::to_string(x) + "," + std::to_string(y);
        acted.push_back(action == "drag" ? "drag " + at + " to " + std::to_string(end_x) + "," + std::to_string(end_y) : "click " + at);
        strokes++;
    }
};

/// Answers about a picture from a script. `draws` makes the window change while the model is looking.
struct ScriptedVision : DesktopVision {
    std::deque<std::string> replies;
    FakeDesktop* draws = nullptr;
    PixelDecision see(const std::string&, const DesktopFrame& frame, const CancelToken&) override {
        if (draws) draws->strokes++;
        const std::string reply = replies.front();
        replies.pop_front();
        return desktop::parse_decision(reply, frame);
    }
};

static void test_desktop_vision() {
    Sandbox box;
    SessionStore store(box.home);
    Session session = store.create(Workspace(box.project.string()).root(), Permission::Auto);
    auto desktop = std::make_shared<FakeDesktop>();
    session.desktop = desktop->window;
    const DesktopFrame frame{200, 100, "x"};
    using Names = std::vector<std::string>;

    // The model's reply is read strictly: a point outside the picture is refused.
    const PixelDecision read = desktop::parse_decision(R"(Sure: {"observation": "A red circle", "action": "click", "x": 40.4, "y": 20})", frame);
    CHECK(read.action == "click" && read.x == 40 && read.y == 20 && read.observation == "A red circle");
    CHECK_THROWS(desktop::parse_decision(R"({"action": "click", "x": 400, "y": 20, "observation": ""})", frame));
    CHECK_THROWS(desktop::parse_decision(R"({"action": "drag", "x": 4, "y": 2, "endX": 4, "endY": 200, "observation": ""})", frame));
    CHECK_THROWS(desktop::parse_decision(R"({"action": "type", "observation": ""})", frame));
    CHECK_THROWS(desktop::parse_decision("I cannot see images.", frame));
    CHECK(desktop::vision_prompt("Find the circle", frame).find("200 by 100 pixels") != std::string::npos);

    // A picture is a real PNG: its signature, a header with the size, and the pixels stored whole.
    const std::string png = desktop::encode_png(2, 1, std::string("\xff\x00\x00\x00\xff\x00", 6));
    CHECK(png.substr(0, 8) == std::string("\x89PNG\r\n\x1a\n", 8) && png.substr(12, 4) == "IHDR" && png.substr(16, 8) == std::string("\0\0\0\x02\0\0\0\x01", 8));
    CHECK(png.find(std::string("\x00\xff\x00\x00\x00\xff\x00", 7)) != std::string::npos && png.substr(png.size() - 8, 4) == "IEND");

    const auto look = [](const char* task) { return call("desktop_visual", {{"task", task}}); };
    auto vision = std::make_shared<ScriptedVision>();
    vision->replies = {R"({"observation": "A chart with three bars", "action": "none"})", R"({"observation": "A canvas with a red circle at the left", "action": "click", "x": 40, "y": 20})",
                       R"({"observation": "The circle", "action": "drag", "x": 40, "y": 20, "endX": 150, "endY": 60})", R"({"observation": "A Clear button", "action": "click", "x": 10, "y": 10})",
                       R"({"observation": "The canvas", "action": "click", "x": 5, "y": 5})"};
    ScriptedModel model;
    model.replies = {look("What does the chart show?"), look("Click the red circle"), look("Drag the circle to the right"), look("Click Clear"), look("Click the corner"), say("Done.")};
    Names asked;
    std::deque<bool> answers = {true, true, false, true};
    Agent agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request.title + " | " + split(request.detail, '\n').front()); const bool answer = answers.front(); answers.pop_front(); return answer; },
                [](const AgentEvent&) {});
    agent.set_desktop(desktop);
    agent.set_vision(vision);
    agent.run(session, "Use the drawing app");
    Names states;
    for (const Activity& activity : session.activities) states.push_back(activity.state);
    CHECK(states == (Names{"completed", "completed", "completed", "denied", "completed"}));
    CHECK(session.activities[0].result == "Seen in Notes — Shopping: A chart with three bars");  // looking needs no approval
    // Every click and drag asks, even in Auto.
    CHECK(asked == (Names{"Click at (40, 20) in Notes — Shopping | The vision model saw: A canvas with a red circle at the left", "Drag from (40, 20) to (150, 60) in Notes — Shopping | The vision model saw: The circle",
                          "Click at (10, 10) in Notes — Shopping | The vision model saw: A Clear button", "Click at (5, 5) in Notes — Shopping | The vision model saw: The canvas"}));
    CHECK(desktop->acted == (Names{"click 40,20", "drag 40,20 to 150,60", "click 5,5"}));  // the declined click was not made
    CHECK(starts_with(session.activities[1].result, "Click at (40, 20) was made.") && session.activities[1].result.find("c2 · push button · Save") != std::string::npos);

    // A window that changed between the look and the approval is left alone.
    const Names before = desktop->acted;
    auto shifting = std::make_shared<ScriptedVision>();
    shifting->replies = {R"({"observation": "A button", "action": "click", "x": 1, "y": 1})"};
    shifting->draws = desktop.get();
    ScriptedModel second;
    second.replies = {look("Click the button"), say("It changed.")};
    Agent changed(store, second, [](const ApprovalRequest&) { return true; }, [](const AgentEvent&) {});
    changed.set_desktop(desktop);
    changed.set_vision(shifting);
    changed.run(session, "Again");
    CHECK(session.activities.back().state == "failed" && session.activities.back().result.find("The window changed before the click could be made") != std::string::npos);
    CHECK(desktop->acted == before);  // nothing was clicked

    // A read-only session may look, and is told what was seen, but may not click.
    session.permission = Permission::ReadOnly;
    session.ask_only = true;
    auto last = std::make_shared<ScriptedVision>();
    last->replies = {R"({"observation": "A big button", "action": "click", "x": 3, "y": 3})"};
    ScriptedModel third;
    third.replies = {look("Click it"), say("Read-only.")};
    Agent reading(store, third, [](const ApprovalRequest&) { return true; }, [](const AgentEvent&) {});
    reading.set_desktop(desktop);
    reading.set_vision(last);
    reading.run(session, "Look");
    CHECK(session.activities.back().state == "failed" && session.activities.back().result.find("The model saw: A big button") != std::string::npos && desktop->acted == before);
}

static void test_desktop() {
    Sandbox box;
    SessionStore store(box.home);
    Session session = store.create(Workspace(box.project.string()).root(), Permission::Auto);
    auto desktop = std::make_shared<FakeDesktop>();
    const auto names = [](const std::vector<ToolSpec>& tools) {
        std::vector<std::string> found;
        for (const ToolSpec& tool : tools) found.push_back(tool.name);
        return found;
    };
    using Names = std::vector<std::string>;

    // Attaching is the user's act; without a window the tools are not offered.
    CHECK(Agent::desktop_tools(session, true).empty());
    CHECK(starts_with(desktop::command("", session, desktop), "1. Calculator\n2. Notes — Shopping\nAttach one with /window NUMBER."));
    CHECK_THROWS(desktop::command("9", session, desktop));
    CHECK(starts_with(desktop::command("2", session, desktop), "Attached Notes — Shopping."));
    CHECK(names(Agent::desktop_tools(session, true)) == (Names{"desktop_observe", "desktop_visual", "desktop_act"}) && Agent::desktop_tools(session, false).empty());
    desktop->trusted = false;
    CHECK_THROWS(desktop::command("", session, desktop));  // a missing service is said plainly
    desktop->trusted = true;
    store.save(session);
    CHECK(SessionStore(box.home).sessions().front().desktop && SessionStore(box.home).sessions().front().desktop->title == "Shopping");

    const auto act = [](const char* operation, const char* id, const char* value) { return call("desktop_act", {{"operation", operation}, {"id", id}, {"value", value}}); };
    ScriptedModel model;
    model.replies = {act("set", "c1", "Milk"), call("desktop_observe", json::object()), act("set", "c1", "Milk"), act("invoke", "c2", ""), act("invoke", "c3", ""), act("invoke", "c2", ""), say("Saved.")};
    std::vector<std::string> asked;
    std::deque<bool> answers = {true, true, false};
    Agent agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request.title); const bool answer = answers.front(); answers.pop_front(); return answer; }, [](const AgentEvent&) {});
    agent.set_desktop(desktop);
    agent.run(session, "Add milk to the list");
    CHECK(model.seen[0][0].content.find("The user attached a window of another app: Notes — Shopping.") != std::string::npos);
    Names states;
    for (const Activity& activity : session.activities) states.push_back(activity.state);
    // An action needs an observation first, a control that accepts it, and the user's yes.
    CHECK(states == (Names{"failed", "completed", "completed", "completed", "failed", "denied"}));
    CHECK(session.activities[1].result.find("c1 · text · Item  [set]") != std::string::npos && session.activities[1].result.find("c2 · push button · Save  [invoke]") != std::string::npos);
    // It asks every time, even in Auto.
    CHECK(asked == (Names{"Set text “Item” to “Milk” in Notes — Shopping", "Press push button “Save” in Notes — Shopping", "Press push button “Save” in Notes — Shopping"}));
    CHECK(desktop->acted == (Names{"set c1 Milk", "invoke c2 "}));
    CHECK(session.activities[3].result.find("c3 · label = Saved Milk") != std::string::npos);  // each action returns a fresh observation

    // A read-only session may look but not touch.
    session.permission = Permission::ReadOnly;
    session.ask_only = true;
    CHECK(names(Agent::desktop_tools(session, true)) == (Names{"desktop_observe", "desktop_visual"}));
    CHECK(desktop::command("off", session, desktop) == "No window is attached to this session." && !session.desktop);
}

static void test_acceptance() {
    Sandbox box;
    const Workspace workspace(box.project.string());
    // A small shop: three products, ten percent off at three units, and an order number that survives a reload.
    box.write("index.html", R"html(<!doctype html><title>Shop</title><ul aria-label="Products"><li>Wrench</li><li>Hammer</li><li>Saw</li></ul>
<input id="qty" value="1"><p id="total">Total: $10.00</p><button id="checkout">Check out</button><p id="order"></p>
<script>
var qty = document.getElementById('qty'), total = document.getElementById('total'), order = document.getElementById('order');
qty.addEventListener('input', function () { var n = Number(qty.value) || 0, sum = n * 10; if (n >= 3) sum = sum * 0.9; total.textContent = 'Total: $' + sum.toFixed(2); });
order.textContent = localStorage.getItem('order') || '';
document.getElementById('checkout').onclick = function () { var id = 'ORD-' + (Number(localStorage.getItem('count') || 0) + 1); localStorage.setItem('count', id.slice(4)); localStorage.setItem('order', id); order.textContent = id; };
</script>)html");
    const auto contract = [](const std::string& discount) {
        return R"({"Version": 1, "Scenarios": [
          {"Id": "products", "Requirement": "List three distinct products", "Page": "index.html", "Steps": [{"Op": "count_distinct", "Target": "ul[aria-label='Products'] li", "Value": "3"}]},
          {"Id": "discount", "Requirement": "Ten percent off at three units", "Page": "index.html",
           "Steps": [{"Op": "fill", "Target": "#qty", "Value": "3"}, {"Op": "money", "Target": "#total", "Value": ")" + discount + R"("}, {"Op": "fill", "Target": "#qty", "Value": "2"}, {"Op": "money", "Target": "#total", "Value": "20.00"}]},
          {"Id": "order", "Requirement": "Checkout makes an order number that survives a reload", "Page": "index.html",
           "Steps": [{"Op": "empty", "Target": "#order", "Value": ""}, {"Op": "click", "Target": "#checkout", "Value": ""}, {"Op": "capture", "Target": "#order", "Value": "ORD-"},
                     {"Op": "reload", "Target": "", "Value": ""}, {"Op": "same", "Target": "#order", "Value": ""}, {"Op": "click", "Target": "#checkout"}, {"Op": "new_id", "Target": "#order", "Value": "ORD-"}]}
        ]})";
    };

    // A contract is checked before it is run.
    const AcceptanceContract parsed = acceptance::parse(workspace, contract("27.00"));
    CHECK(parsed.scenarios.size() == 3 && parsed.scenarios[2].steps.size() == 7 && parsed.application.empty());
    const auto one = [](const std::string& page, const std::string& steps) { return R"({"Version": 1, "Scenarios": [{"Id": "a", "Requirement": "r", "Page": ")" + page + R"(", "Steps": [)" + steps + "]}]}"; };
    CHECK_THROWS(acceptance::parse(workspace, R"({"Version": 2, "Scenarios": []})"));
    CHECK_THROWS(acceptance::parse(workspace, one("index.html", R"({"Op": "click", "Target": "#x"})")));  // must end with an assertion
    CHECK_THROWS(acceptance::parse(workspace, one("index.html", R"({"Op": "same", "Target": "#x"})")));   // nothing was captured to compare
    CHECK_THROWS(acceptance::parse(workspace, one("../x.html", R"({"Op": "text", "Target": "#x", "Value": "y"})")));  // the page must be in the project
    CHECK_THROWS(acceptance::parse(workspace, one("index.html", R"({"Op": "hover", "Target": "#x"}, {"Op": "text", "Target": "#x", "Value": "y"})")));
    CHECK_THROWS(acceptance::parse(workspace, one("index.html", R"({"Op": "money", "Target": "#x", "Value": "ten"})")));

    // A review keeps only findings that quote the line they name.
    {
        SessionStore store(box.home);
        Session session = store.create(workspace.root(), Permission::WorkspaceEdits);
        ScriptedModel model;
        CHECK_THROWS(review::findings(session, workspace, model));  // nothing was changed yet
        Turn turn;
        turn.request = "Add an add function";
        turn.changes.push_back({"calc.py", std::nullopt, "def add(a, b):\n    return a - b\n"});
        session.turns.push_back(turn);
        model.replies = {say(R"(Here is what I found:
[{"path": "calc.py", "line": 2, "evidence": "return a - b", "problem": "It subtracts instead of adding."},
 {"path": "calc.py", "line": 1, "evidence": "return a - b", "problem": "Quoted from the wrong line."},
 {"path": "other.py", "line": 1, "evidence": "x", "problem": "A file that was not changed."}])"), say("[]")};
        const ReviewFindings found = review::findings(session, workspace, model);
        CHECK(found.comments.size() == 1 && review::location(found.comments[0]) == "calc.py · updated line 2" && found.comments[0].note == "It subtracts instead of adding." && found.dropped == 2);
        CHECK(model.seen[0][1].content.find("2\t    return a - b") != std::string::npos);  // the model reads numbered lines
        CHECK(starts_with(review::summary(found), "The review found 1 thing in calc.py:\ncalc.py · updated line 2 — It subtracts instead of adding.\n2 findings were left out"));
        review::validate(workspace, found.comments);  // a finding is a comment that can be sent
        CHECK(review::summary(review::findings(session, workspace, model)) == "The review of calc.py found nothing to report.");
    }

    if (browser::find().empty()) {
        std::cout << "  (no browser on this computer: the scenarios were not run)\n";
        return;
    }
    const std::vector<AcceptanceResult> passed = acceptance::run(workspace, parsed);
    CHECK(passed.size() == 3 && passed[0].passed && passed[1].passed && passed[2].passed);
    if (passed.size() == 3 && !(passed[0].passed && passed[1].passed && passed[2].passed)) std::cerr << acceptance::receipt(passed) << "\n";
    CHECK(starts_with(acceptance::receipt(passed), "3 of 3 acceptance scenarios passed, each at 390px and 1280px."));
    CHECK(std::find(passed[1].detail.begin(), passed[1].detail.end(), "money #total = Total: $27.00") != passed[1].detail.end());
    CHECK(std::find(passed[2].detail.begin(), passed[2].detail.end(), "new_id #order = ORD-2") != passed[2].detail.end());
    const std::vector<AcceptanceResult> failed = acceptance::run(workspace, acceptance::parse(workspace, contract("30.00")));
    CHECK(failed[0].passed && !failed[1].passed && failed[2].passed);
    CHECK(failed[1].detail == std::vector<std::string>{"at 390px, step 2, money #total: expected '30.00', observed 'Total: $27.00' (1 visible)"});

    // The agent runs them as a tool, asking first outside Auto.
    box.write(acceptance::kFilename, contract("27.00"));
    SessionStore store(box.home);
    Session session = store.create(workspace.root(), Permission::ReviewEdits);
    ScriptedModel model;
    model.replies = {call("verify_acceptance", json::object()), say("All three hold.")};
    std::vector<std::string> asked;
    Agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request.subject); return true; }, [](const AgentEvent&) {}).run(session, "Check the shop");
    CHECK(asked == std::vector<std::string>{"mason.acceptance.json"});
    CHECK(session.activities.size() == 1 && session.activities[0].state == "completed");
    CHECK(session.activities[0].result.find("PASSED order — Checkout makes an order number that survives a reload (index.html)") != std::string::npos);
    CHECK(model.seen[0][0].content.find("acceptance scenarios in mason.acceptance.json") != std::string::npos);
}

static void test_transfer() {
    Sandbox box;
    const Workspace workspace(box.project.string());
    SessionStore store(box.home);
    Session session = store.create(workspace.root(), Permission::Auto);
    ScriptedModel model;
    model.replies = {call("write_file", {{"path", "a.txt"}, {"content", "one\n"}}), say("Made a."),
                     call("write_file", {{"path", "b.txt"}, {"content", "two\n"}}), say("Made b with api_key = hunter2secret."),
                     call("write_file", {{"path", "a.txt"}, {"content", "changed\n"}}), say("Changed a.")};
    Agent agent(store, model, [](const ApprovalRequest&) { return true; }, [](const AgentEvent&) {});
    agent.run(session, "Make a");
    agent.run(session, "Make b in " + workspace.root());
    agent.run(session, "Change a");
    CHECK(session.turns.size() == 3);
    using Names = std::vector<std::string>;

    // A fork holds the conversation before the request, and can put the files back as they were then.
    CHECK_THROWS(transfer::fork(session, store, 7, false));
    const SessionFork plain = transfer::fork(session, store, 2, false);
    CHECK(plain.session.turns.size() == 2 && plain.request == "Change a" && plain.session.messages.back().content == "Made b with api_key = hunter2secret.");
    CHECK(plain.session.activities.size() == 2 && plain.session.permission == Permission::ReviewEdits && ends_with(plain.session.title, " · fork"));
    CHECK(box.read("a.txt") == "changed\n");  // files are left alone unless asked
    box.write("b.txt", "edited by hand\n");
    const SessionFork restored = transfer::fork(session, store, 1, true);
    CHECK(restored.session.turns.size() == 1 && box.read("a.txt") == "one\n");  // the later change to a.txt was undone
    CHECK(restored.restored == Names{"a.txt"} && restored.skipped == Names{"b.txt"});  // a file changed since is left alone and reported
    CHECK(restored.session.detail.find("changed since and left alone: b.txt") != std::string::npos);
    CHECK(!session.turns[0].undone && session.turns[1].undone && session.turns[2].undone);

    // An export can leave out the project's path, likely secrets and file contents.
    const std::string full = transfer::export_session(session, false);
    CHECK(full.find("hunter2secret") != std::string::npos && full.find(workspace.root()) != std::string::npos);
    const std::string clean = transfer::export_session(session, true);
    CHECK(clean.find("hunter2secret") == std::string::npos && clean.find(workspace.root()) == std::string::npos && clean.find("changed\\n") == std::string::npos);
    CHECK(clean.find("<redacted>") != std::string::npos && clean.find("<workspace>") != std::string::npos);

    const fs::path other = box.base / "elsewhere";
    fs::create_directories(other);
    const Session imported = transfer::import_session(clean, store, Workspace(other.string()).root());
    CHECK(imported.id != session.id && imported.turns.size() == 3 && imported.turns[1].answer == "Made b with api_key = <redacted>");
    for (const Turn& turn : imported.turns) CHECK(turn.changes.empty() && !turn.undone);
    CHECK(ends_with(imported.title, " · imported") && starts_with(imported.detail, "Imported a sanitized session."));
    CHECK_THROWS(transfer::import_session("{}", store, workspace.root()));
    CHECK(SessionStore(box.home).sessions().size() == 4);  // the original, two forks and the import
}

static void test_navigation() {
    Sandbox box;
    box.write("cart.py", "def total(items):\n    return sum(items)\n\n# total is used below\nprint(total([1, 2]))\nsubtotal = 3\n");
    box.write("tests/test_cart.py", "from cart import total\n\ndef test_total():\n    assert total([1]) == 1\n");
    box.write("sum.c", "int total(int a, int b) {\n    return a + b;\n}\n");
    box.write("notes.txt", "total total total");
    fs::remove(box.project / "calc.py");
    const Workspace workspace(box.project.string());
    const auto places = [](const NavigationResult& result) {
        std::vector<std::string> found;
        for (const NavigationHit& hit : result.hits) found.push_back(hit.path + ":" + std::to_string(hit.line));
        return found;
    };
    using Names = std::vector<std::string>;
    const NavigationResult definitions = navigation::query(workspace, ".", "total", "definitions");
    CHECK(places(definitions) == (Names{"cart.py:1", "sum.c:1"}));  // a def and a C function, not the import or the call
    CHECK(places(navigation::query(workspace, ".", "total", "references")) == (Names{"cart.py:5", "tests/test_cart.py:1", "tests/test_cart.py:4"}));  // whole words only, not the comment
    CHECK(places(navigation::query(workspace, ".", "total", "tests")) == (Names{"tests/test_cart.py:1", "tests/test_cart.py:4"}));
    CHECK(definitions.files == 3);  // only source files are read
    CHECK_THROWS(navigation::query(workspace, ".", "a b", "definitions"));
    CHECK_THROWS(navigation::query(workspace, ".", "total", "diagnostics"));
    CHECK(navigation::report(definitions, "total", "definitions").find("cart.py:1  [definition candidate]  def total(items):") != std::string::npos);

    // A stand-in for the helper: it answers with the position it was asked about and the files it was given.
    const fs::path tools = box.base / "tools";
    fs::create_directories(tools / "python" / "jedi");
    std::ofstream(tools / "python-symbols.py") << "import json, sys\nr = json.load(sys.stdin)\nprint(json.dumps({'engine': 'stand-in', 'asked': [r['path'], r['line'], r['column'], r['mode']], 'files': sorted(r['files'])}))\n";
    setenv("MASON_LANGUAGE_TOOLS", tools.c_str(), 1);
    if (transformers::python().empty()) {
        std::cout << "  (no python3 on this computer: the language service part was not run)\n";
    } else {
        CHECK(symbols::available());
        CHECK(symbols::query(workspace, "cart.py", 5, 7, "definitions") == R"({"engine": "stand-in", "asked": ["cart.py", 5, 7, "definitions"], "files": ["cart.py", "tests/test_cart.py"]})");
        CHECK_THROWS(symbols::query(workspace, "sum.c", 1, 1, "definitions"));  // only Python and JavaScript or TypeScript
        CHECK_THROWS(symbols::query(workspace, "cart.py", 0, 1, "definitions"));
        SessionStore store(box.home);
        Session session = store.create(workspace.root(), Permission::ReadOnly);
        ScriptedModel model;
        model.replies = {call("navigate_code", {{"path", "."}, {"symbol", "total"}, {"mode", "definitions"}}), call("inspect_symbol", {{"path", "cart.py"}, {"line", 5}, {"column", 7}, {"mode", "references"}}), say("Found.")};
        Agent(store, model, [](const ApprovalRequest&) { return true; }, [](const AgentEvent&) {}).run(session, "Where is total?");
        CHECK(session.activities.size() == 2 && session.activities[0].state == "completed" && session.activities[1].state == "completed");
        CHECK(session.activities[1].result.find("\"references\"") != std::string::npos);
    }
    unsetenv("MASON_LANGUAGE_TOOLS");
}

static void test_verification_app() {
    // The checkout fixture the Windows tests use: a WSGI shop with an order database.
    Sandbox box;
    const fs::path fixture = fs::path(__FILE__).parent_path().parent_path().parent_path() / "windows" / "tests" / "fixtures" / "checkout";
    fs::remove(box.project / "calc.py");
    for (const char* name : {"index.html", "store.py", "mason.acceptance.json"}) fs::copy_file(fixture / name, box.project / name);
    const Workspace workspace(box.project.string());
    if (transformers::python().empty()) {
        std::cout << "  (no python3 on this computer: the application was not run)\n";
        return;
    }
    CHECK_THROWS(VerificationApp(workspace, "store; import os", false));  // only module:callable
    fs::path copy;
    {
        VerificationApp app(workspace, "store:application", shell::sandbox_available());
        copy = app.folder();
        CHECK(starts_with(copy.filename().string(), "mason-verify-") && fs::exists(copy / "store.py"));
        CHECK(!app.answer("GET", "/__mason/harness?page=x", {}, ""));  // the harness stays with the page server
        const auto page = app.answer("GET", "/index.html", {}, "");
        CHECK(page && page->status == 200 && page->body.find("window.__masonErrors") != std::string::npos && page->body.find("<li>Honey</li>") != std::string::npos);
        const auto price = app.answer("GET", "/price?quantity=3", {}, "");
        CHECK(price && price->body == R"({"total": 27.0})");
        const auto missing = app.answer("GET", "/missing", {}, "");
        CHECK(missing && missing->status == 404);
        CHECK_THROWS(app.check("response_count", "POST /checkout", "1", ""));  // nothing was ordered yet
        const auto order = app.answer("POST", "/checkout", {{"content-type", "application/json"}}, "{}");
        const std::string id = order ? json::parse(order->body, nullptr, false).value("id", "") : "";
        CHECK(starts_with(id, "ORD-"));
        CHECK(app.check("response_count", "POST /checkout", "1", "") == "1 responses");
        CHECK(app.check("response_same", "POST /checkout", "id", id) == "1 responses matched " + id);
        CHECK_THROWS(app.check("response_same", "POST /checkout", "id", "ORD-other"));
        CHECK(app.check("sqlite_count", "orders.sqlite#orders", "1", "") == "1 rows");
        CHECK_THROWS(app.check("sqlite_count", "orders.sqlite#orders", "2", ""));
        CHECK_THROWS(app.check("sqlite_count", "../x.sqlite#orders", "1", ""));
    }
    CHECK(!fs::exists(copy));                            // the copy and its database are thrown away
    CHECK(!fs::exists(box.project / "orders.sqlite"));  // the project itself was never written to

    if (browser::find().empty()) {
        std::cout << "  (no browser on this computer: the checkout contract was not run)\n";
        return;
    }
    const std::vector<AcceptanceResult> passed = acceptance::run(workspace, acceptance::read(workspace));
    const bool all = std::all_of(passed.begin(), passed.end(), [](const AcceptanceResult& result) { return result.passed; });
    CHECK(all);
    if (!all) std::cerr << acceptance::receipt(passed) << "\n";
    // An order that is shown but never saved must fail the scenario that reloads the order page.
    box.write("store.py", replace_all(box.read("store.py"), "SAVE_ORDER = True", "SAVE_ORDER = False"));
    const std::vector<AcceptanceResult> broken = acceptance::run(workspace, acceptance::read(workspace));
    CHECK(!std::all_of(broken.begin(), broken.end(), [](const AcceptanceResult& result) { return result.passed; }));
}

static void test_derived() {
    Sandbox box;
    fs::remove(box.project / "calc.py");
    const Workspace workspace(box.project.string());
    using Names = std::vector<std::string>;
    const auto kinds = [](const DerivedPlan& plan) { Names found; for (const DerivedCheck& check : plan.checks) found.push_back(check.kind); return found; };

    // Requirements with an exact outcome become checks without a model. Quoted text is kept whole.
    CHECK(derived::units(R"(Show a heading "Orders, and more" and clicking "Add" must show "Added". Keep it tidy)") == (Names{R"(Show a heading "Orders, and more")", R"(clicking "Add" must show "Added")", "Keep it tidy"}));
    const DerivedPlan plan = derived::prepare(R"(In index.html show a heading "Orders"; clicking "Add" must show "Added"; write notes.md containing "Shipped")", workspace, nullptr);
    CHECK(kinds(plan) == (Names{"browser_heading", "browser_click", "document_text"}));
    CHECK(derived::description(plan.checks[0]) == "index.html shows a heading that reads “Orders”" && derived::description(plan.checks[1]) == "in index.html, clicking “Add” makes “Added” appear");
    CHECK(plan.unverified().empty() && starts_with(plan.brief(), "Mason will check these itself when you say you are done:\n- index.html shows a heading"));
    const DerivedPlan vague = derived::prepare("Make the totals right and tidy up", workspace, nullptr);
    CHECK(vague.checks.empty() && vague.unverified() == (Names{"Make the totals right", "tidy up"}));
    box.write("notes.md", "Shipped on Monday");
    DerivedPlan documents;
    documents.units = {"x"};
    documents.checks = {plan.checks[2], DerivedCheck{1, "x", "document_text", "missing.txt", "", "y", {}, ""}};
    const std::vector<DerivedResult> read = derived::verify(documents, workspace);
    CHECK(read.size() == 2 && read[0].state == "passed" && read[1].state == "failed");

    // A model's proposals are kept only when Mason can run them and they quote the request.
    {
        ScriptedModel model;
        model.replies = {say(R"({"checks": [{"Unit": 1, "Feature": "Banner text", "Kind": "browser_text", "Path": "shop.html", "Target": "", "Expected": "Open today"},
            {"Unit": 1, "Feature": "Invented", "Kind": "browser_text", "Path": "shop.html", "Target": "", "Expected": "Welcome back"},
            {"Unit": 2, "Feature": "Hiding", "Kind": "browser_text", "Path": "shop.html", "Target": "", "Expected": "Hide"},
            {"Unit": 2, "Feature": "Unknown kind", "Kind": "screenshot", "Path": "shop.html", "Target": "", "Expected": "Hide"},
            {"Unit": 9, "Feature": "No such requirement", "Kind": "browser_text", "Path": "shop.html", "Target": "", "Expected": "Hide"}]})")};
        const DerivedPlan proposed = derived::prepare(R"(In shop.html the banner should say "Open today" and clicking the "Hide" button hides it)", workspace, &model);
        CHECK(proposed.units.size() == 2 && proposed.checks.size() == 1 && proposed.checks[0].expected == "Open today");
        CHECK(proposed.unverified() == (Names{R"(clicking the "Hide" button hides it)"}));
        CHECK(model.seen[0][1].content.find("1. In shop.html the banner should say") != std::string::npos);
    }

    if (browser::find().empty()) {
        std::cout << "  (no browser on this computer: the checks were not run in one)\n";
        return;
    }
    // In Auto a failed check goes back to the model, and the receipt says what Mason found.
    SessionStore store(box.home);
    Session session = store.create(workspace.root(), Permission::Auto);
    const std::string wrong = "<!doctype html><h1>Order</h1><button onclick=\"document.getElementById('o').textContent='Added'\">Add</button><p id=o></p>";
    ScriptedModel model;
    model.replies = {call("write_file", {{"path", "index.html"}, {"content", wrong}}), say("Done."), call("write_file", {{"path", "index.html"}, {"content", replace_all(wrong, "<h1>Order</h1>", "<h1>Orders</h1>")}}), say("Fixed the heading.")};
    Agent(store, model, [](const ApprovalRequest&) { return true; }, [](const AgentEvent&) {}).run(session, R"(In index.html show a heading "Orders"; clicking "Add" must show "Added")");
    CHECK(model.seen[0].back().content.find("Mason will check these itself") != std::string::npos);  // the model is told what will be checked
    CHECK(model.seen.size() == 4 && model.seen[2].back().content.find("this does not hold yet:\n- index.html shows a heading that reads “Orders”") != std::string::npos);
    CHECK(session.turns[0].checks && session.turns[0].checks->results.size() == 2 && derived::passed(*session.turns[0].checks) && session.turns[0].checks->repairs == 1);
    if (session.turns[0].checks) CHECK(derived::summary(*session.turns[0].checks) == "Mason checked 2 requirements itself: all hold · handed back 1 time");
    CHECK(session.turns[0].answer == "Fixed the heading.");
    const std::vector<Session> saved = SessionStore(box.home).sessions();
    CHECK(saved.front().turns[0].checks && saved.front().turns[0].checks->results[1].check.kind == "browser_click");  // the receipt is saved with the session

    // Outside Auto nothing is derived or run.
    Session reviewed = store.create(workspace.root(), Permission::WorkspaceEdits);
    ScriptedModel plain;
    plain.replies = {say("Ok.")};
    Agent(store, plain, [](const ApprovalRequest&) { return true; }, [](const AgentEvent&) {}).run(reviewed, R"(In index.html show a heading "Nope")");
    CHECK(!reviewed.turns[0].checks);
}

static void test_catalog() {
    const std::string request = "Build a whimsical bear market as a single offline index.html in this directory. Honey costs $8 each. Berries are $3 each; buy three or more Berries and they are $2.50 each. Salmon is $12 each.\n"
                                "Shoppers can add items to a cart and see the total. Checkout must clear the cart and show a new receipt that starts with BEAR-, and the receipt persists after reload.";
    const std::string extraction = R"({"Items": [{"Name": "Honey", "Price": 8, "SalePrice": 8, "Threshold": 0}, {"Name": "Berries", "Price": 3, "SalePrice": 2.5, "Threshold": 3}, {"Name": "Salmon", "Price": 12, "SalePrice": 2.5, "Threshold": 3}], "ReceiptPrefix": "BEAR-", "PersistReceipt": true, "ClearCart": true, "OtherRequirements": ["Shoppers can add items to a cart and see the total."]})";
    const std::string design = R"rx({"Title": "The Bear Market", "Introduction": "Provisions for discerning bears, only $1!", "Accent": "#3b2f1e", "Background": "not a colour", "Descriptions": ["Golden <b>honey</b>", "Wild berries", "River salmon"]})rx";
    using Names = std::vector<std::string>;

    // The offers are read from the request and checked against it.
    CHECK(catalog::amounts(request) == (std::set<int>{800, 300, 250, 1200}));
    CHECK(catalog::explicit_receipt_prefix(request) == std::optional<std::string>("BEAR-") && catalog::requires_fresh_receipt(request));
    ScriptedModel reader;
    reader.replies = {say(extraction), say(replace_all(extraction, R"("Price": 12)", R"("Price": 11)")), say(replace_all(extraction, "Salmon", "Trout")),
                      say(replace_all(extraction, "Shoppers can add items to a cart and see the total.", "Email the receipt to the shopper."))};
    const std::optional<CatalogSpec> spec = catalog::extract(request, reader);
    // The discount the model copied onto Salmon is dropped: the request gives it only to Berries.
    CHECK(spec && spec->items == (std::vector<CatalogItem>{{"Honey", 800, 800, 0}, {"Berries", 300, 250, 3}, {"Salmon", 1200, 1200, 0}}));
    if (!spec) return;
    CHECK(spec->other_requirements.empty() && catalog::complete(*spec, request));  // a clause the component already covers is not an extra requirement
    const std::optional<CatalogSpec> corrected = catalog::extract(request, reader);
    CHECK(corrected && corrected->items.back() == (CatalogItem{"Salmon", 1200, 1200, 0}));  // a wrong price is put right from the request's own words
    CHECK(!catalog::extract(request, reader));                                              // a product the request never names is refused
    const std::optional<CatalogSpec> more = catalog::extract(request, reader);
    CHECK(more && !catalog::complete(*more, request));  // something the component does not do keeps the request on the ordinary path

    const std::vector<AcceptanceScenario> scenarios = catalog::scenarios(*spec, request);
    Names ids, discount, order;
    for (const AcceptanceScenario& scenario : scenarios) ids.push_back(scenario.id);
    CHECK(ids == (Names{"offer-0", "offer-1", "discount-1", "offer-2", "order"}));
    for (const AcceptanceStep& step : scenarios[2].steps) discount.push_back(step.op + " " + step.value);
    CHECK(discount == (Names{"click ", "click ", "money 6.00", "click ", "money 7.50"}));  // two at the full price, then three at the sale price
    for (const AcceptanceStep& step : scenarios[4].steps) order.push_back(step.op);
    CHECK(order == (Names{"click", "click", "capture", "money", "empty", "reload", "same", "click", "click", "new_id", "reload", "same"}));

    ScriptedModel writer;
    writer.replies = {say(design)};
    const CatalogDesign made = catalog::design(request, *spec, writer);
    CHECK(made.introduction.empty());  // copy that states a price is left out
    const std::string page = catalog::render(*spec, made);
    CHECK(page.find("<h1>The Bear Market</h1>") != std::string::npos && page.find("--accent: #3b2f1e") != std::string::npos && page.find("--paper: #f6f1e2") != std::string::npos);
    CHECK(page.find("Golden &lt;b&gt;honey&lt;/b&gt;") != std::string::npos && page.find(R"({name: "Berries", cents: 300, saleCents: 250, threshold: 3})") != std::string::npos);
    CHECK(page.find(R"(const prefix = "BEAR-";)") != std::string::npos && page.find("<p>Buy 3 or more for $2.50 each.</p>") != std::string::npos && page.find("@") == std::string::npos);

    // An empty project gets the store, and it passes its own scenarios.
    Sandbox box;
    fs::remove(box.project / "calc.py");
    const Workspace workspace(box.project.string());
    CHECK(catalog::applies(request, workspace) && !catalog::applies("Fix the login bug", workspace));
    SessionStore store(box.home);
    Session session = store.create(workspace.root(), Permission::Auto);
    ScriptedModel model;
    model.replies = {say(extraction), say(design)};
    Agent(store, model, [](const ApprovalRequest&) { return true; }, [](const AgentEvent&) {}).run(session, request);
    CHECK(session.state == "Completed" && model.seen.size() == 2);  // the model read the offers and wrote the copy; Mason wrote the page
    CHECK(box.read("index.html").find("The Bear Market") != std::string::npos);
    CHECK(session.turns[0].changes.size() == 1 && session.turns[0].changes[0].path == "index.html" && session.activities.size() == 1 && session.activities[0].tool == "write_file");
    CHECK(session.turns[0].answer.find("Honey at $8.00, Berries at $3.00 ($2.50 each from 3), Salmon at $12.00") != std::string::npos);
    CHECK(session.turns[0].checks && session.turns[0].checks->results.size() == 5);
    if (session.turns[0].checks) {
        const DerivedReceipt& receipt = *session.turns[0].checks;
        const bool wanted = browser::find().empty() ? std::all_of(receipt.results.begin(), receipt.results.end(), [](const DerivedResult& result) { return result.state == "notRun"; })
                                                    : std::all_of(receipt.results.begin(), receipt.results.end(), [](const DerivedResult& result) { return result.state == "passed"; });
        CHECK(wanted);
        if (!wanted) std::cerr << derived::report(receipt) << "\n";
    }
    CHECK(!catalog::applies(request, workspace));  // only an empty project gets the component
}

static void test_language_servers() {
    Sandbox box;
    fs::remove(box.project / "calc.py");
    box.write("sum.c", "int total(int a, int b) {\n    return a + b;\n}\nint main() { return total(1, count); }\n");
    const Workspace workspace(box.project.string());
    using Names = std::vector<std::string>;

    // What the server is sent is a well-formed conversation, and what it sends back is read frame by frame.
    Names methods;
    const std::vector<json> sent = language_servers::messages_in(language_servers::conversation(workspace.root(), workspace.root() + "/sum.c", "x", "c", 4, 21, "references"));
    for (const json& message : sent) methods.push_back(message.value("method", ""));
    CHECK(methods == (Names{"initialize", "initialized", "textDocument/didOpen", "textDocument/references", "shutdown", "exit"}));
    CHECK(sent.size() == 6 && sent[3]["params"]["position"] == json({{"line", 3}, {"character", 20}}));
    CHECK(!language_servers::server_for("txt"));

    const std::string python = transformers::python();
    const fs::path fake = fs::absolute(fs::path(__FILE__).parent_path().parent_path().parent_path() / "macos" / "Tests" / "fake-language-server.py").lexically_normal();
    if (python.empty() || !fs::exists(fake)) {
        std::cout << "  (no python3 on this computer: the stand-in server was not run)\n";
    } else {
        setenv("MASON_LANGUAGE_SERVERS", json{{"c", {python, fake.string()}}}.dump().c_str(), 1);
        const std::optional<LanguageServer> server = language_servers::server_for("c");
        CHECK(server && server->language_id == "c");
        if (server) {
            CHECK(language_servers::query(workspace, *server, "sum.c", 4, 21, "definitions", false) == server->name + ": 1 definitions in the project.\nsum.c:1:5  int total(int a, int b) {");
            const std::string references = language_servers::query(workspace, *server, "sum.c", 4, 21, "references", false);
            CHECK(starts_with(references, server->name + ": 2 references in the project, and 1 outside it (libraries).\nsum.c:1:5") && references.find("sum.c:4:21  int main() { return total(1, count); }") != std::string::npos);
            CHECK(language_servers::query(workspace, *server, "sum.c", 1, 1, "diagnostics", false) == server->name + ": 1 diagnostic for sum.c.\nsum.c:3:12  error  use of undeclared identifier 'count'");

            // Through the agent it asks first, like a command, and a read-only session may not start it.
            SessionStore store(box.home);
            Session session = store.create(workspace.root(), Permission::ReviewEdits);
            const ModelReply ask = call("inspect_symbol", {{"path", "sum.c"}, {"line", 4}, {"column", 21}, {"mode", "definitions"}});
            ScriptedModel model;
            model.replies = {ask, ask, say("Found.")};
            Names asked;
            std::deque<bool> answers = {true, false};
            Agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request.subject); const bool answer = answers.front(); answers.pop_front(); return answer; }, [](const AgentEvent&) {}).run(session, "Where is total defined?");
            CHECK(asked == (Names{"language server " + server->name, "language server " + server->name}));
            CHECK(session.activities.size() == 2 && session.activities[0].state == "completed" && session.activities[1].state == "denied" && session.activities[0].result.find("sum.c:1:5") != std::string::npos);
            session.permission = Permission::ReadOnly;
            session.ask_only = true;
            ScriptedModel again;
            again.replies = {ask, say("No.")};
            Agent(store, again, [](const ApprovalRequest&) { return false; }, [](const AgentEvent&) {}).run(session, "Again");
            CHECK(session.activities.back().state == "failed" && session.activities.back().result.find("read-only") != std::string::npos);
        }
        unsetenv("MASON_LANGUAGE_SERVERS");
    }

    // A real clangd, where there is one: the definition of total from its use, and the undeclared name.
    const std::optional<LanguageServer> clangd = language_servers::server_for("c");
    if (!clangd || clangd->name != "clangd") {
        std::cout << "  (no clangd on this computer: a real language server was not run)\n";
        return;
    }
    const std::string definition = language_servers::query(workspace, *clangd, "sum.c", 4, 21, "definitions", false);
    CHECK(definition.find("sum.c:1:5  int total(int a, int b) {") != std::string::npos);
    const std::string diagnostics = language_servers::query(workspace, *clangd, "sum.c", 1, 1, "diagnostics", false);
    CHECK(diagnostics.find("sum.c:4:") != std::string::npos && diagnostics.find("count") != std::string::npos);
    if (failures) std::cerr << definition << "\n" << diagnostics << "\n";
}

static void test_verification() {
    const auto approve = [](const ApprovalRequest&) { return true; };
    const auto edit = [] { return std::deque<ModelReply>{call("write_file", {{"path", "calc.py"}, {"content", "def add(a, b):\n    return a + b\n"}}), say("Done.")}; };
    // A project whose verify workflow passes when calc.py contains the needle, with the rule that lets Mason run it.
    auto prepare = [](Sandbox& box, SessionStore& store, const std::string& needle, bool allowed) {
        box.write("mason.workflows.json", R"({"version": 1, "workflows": {"verify": {"executable": "grep", "arguments": ["-q", ")" + needle + R"(", "calc.py"]}}})");
        Session session = store.create(Workspace(box.project.string()).root(), Permission::WorkspaceEdits);
        if (allowed) ApprovalRules(store.home(), session.workspace).add("run_workflow", "workflow verify");
        return session;
    };

    // A regression is handed back to the model, and the receipt records the repair.
    {
        Sandbox box;
        SessionStore store(box.home);
        Session session = prepare(box, store, "def add", true);
        ScriptedModel model;
        model.replies = {call("write_file", {{"path", "calc.py"}, {"content", "def plus(a, b):\n    return a + b\n"}}), say("Renamed it."),
                         call("write_file", {{"path", "calc.py"}, {"content", "def add(a, b):\n    return a + b\n"}}), say("Kept the name and fixed the sum.")};
        int verifying = 0;
        Agent(store, model, approve, [&](const AgentEvent& event) { verifying += event.kind == AgentEvent::Kind::Status && event.text == "Verifying" ? 1 : 0; }).run(session, "Fix add");
        const auto& receipt = session.turns[0].verification;
        CHECK(session.state == "Completed" && receipt && receipt->verdict == "passed" && receipt->repairs == 1 && receipt->baseline_exit_code == std::optional<int>(0));
        CHECK(session.turns[0].answer == "Kept the name and fixed the sum." && receipt && receipt->summary() == "verify passed");
        CHECK(model.seen[2].back().content.find("It passed before your changes and fails now") != std::string::npos);
        CHECK(verifying == 3);  // once before the first change, and once after each answer
        const SessionStore reloaded(box.home);
        CHECK(reloaded.sessions()[0].turns[0].verification && reloaded.sessions()[0].turns[0].verification->verdict == "passed");
    }

    // The receipt tells a regression from a failure that was already there.
    {
        Sandbox box;
        SessionStore store(box.home);
        Session session = prepare(box, store, "never-in-the-file", true);
        ScriptedModel model;
        model.replies = edit();
        Agent(store, model, approve, [](const AgentEvent&) {}).run(session, "Fix add");
        CHECK(session.turns[0].verification && session.turns[0].verification->verdict == "failingBefore" && model.seen.size() == 2);  // not sent back
    }
    {
        Sandbox box;
        SessionStore store(box.home);
        Session session = prepare(box, store, "a + b", true);
        ScriptedModel model;
        model.replies = edit();
        Agent(store, model, approve, [](const AgentEvent&) {}).run(session, "Fix add");
        CHECK(session.turns[0].verification && session.turns[0].verification->verdict == "fixed");
    }
    {
        // A regression the model cannot repair is recorded as one, after two attempts.
        Sandbox box;
        SessionStore store(box.home);
        Session session = prepare(box, store, "def add", true);
        ScriptedModel model;
        for (int number = 1; number <= 3; number++) {
            model.replies.push_back(call("write_file", {{"path", "calc.py"}, {"content", "broken " + std::to_string(number) + "\n"}}));
            model.replies.push_back(say("Attempt."));
        }
        Agent(store, model, approve, [](const AgentEvent&) {}).run(session, "Break it");
        CHECK(session.state == "Completed" && session.turns[0].verification && session.turns[0].verification->verdict == "regression" && session.turns[0].verification->repairs == 2);
    }

    // Verification needs permission and a change.
    {
        Sandbox box;
        SessionStore store(box.home);
        Session session = prepare(box, store, "def add", false);
        ScriptedModel model;
        model.replies = edit();
        Agent(store, model, approve, [](const AgentEvent&) {}).run(session, "Change it");
        CHECK(session.turns[0].verification && session.turns[0].verification->verdict == "notRun");
        ApprovalRules(store.home(), session.workspace).add("run_workflow", "workflow verify");
        ScriptedModel reading;
        reading.replies = {call("read_file", {{"path", "calc.py"}}), say("It adds.")};
        Agent(store, reading, approve, [](const AgentEvent&) {}).run(session, "Explain it");
        CHECK(!session.turns[1].verification);  // nothing changed, so nothing to verify
        fs::remove(box.project / "mason.workflows.json");
        ScriptedModel again;
        again.replies = edit();
        again.replies[0] = call("write_file", {{"path", "calc.py"}, {"content", "y\n"}});
        Agent(store, again, approve, [](const AgentEvent&) {}).run(session, "Change it");
        CHECK(!session.turns[2].verification);  // no verify workflow, no receipt
    }
}

int main() {
    const std::pair<const char*, void (*)()> tests[] = {
        {"workspace", test_workspace}, {"diff", test_diff}, {"stream and endpoint", test_stream_and_endpoint}, {"tool call recovery", test_recovery},
        {"agent edit and undo", test_agent_edit_undo}, {"agent refusals", test_agent_refusals}, {"commands and rules", test_commands_and_rules},
        {"undo of command changes", test_undo_of_command_changes}, {"custom agents and commands", test_custom_agents_and_commands}, {"compaction", test_compaction}, {"MCP servers", test_mcp}, {"chat", test_chat}, {"queue and steer", test_queue_and_steer}, {"schedules", test_schedules}, {"extra tools", test_extra_tools}, {"verification", test_verification}, {"browser checks", test_browser}, {"comments, milestones and search by meaning", test_planning}, {"Transformers models", test_transformers}, {"attached windows", test_desktop}, {"a window by sight", test_desktop_vision}, {"acceptance scenarios and review", test_acceptance}, {"fork, export and import", test_transfer}, {"code navigation", test_navigation}, {"an application under test", test_verification_app}, {"checks derived from a request", test_derived}, {"the catalog component", test_catalog}, {"language servers", test_language_servers},
        {"shell", test_shell}, {"sandbox", test_sandbox}, {"sha-256 and models", test_sha_and_models},
    };
    for (const auto& [name, function] : tests) {
        const int before = failures;
        try {
            function();
        } catch (const std::exception& error) {
            failures++;
            std::cerr << "  FAILED with an unexpected error: " << error.what() << "\n";
        }
        std::cout << (failures == before ? "ok      " : "FAILED  ") << name << "\n";
    }
    std::cout << (failures == 0 ? "All tests passed." : std::to_string(failures) + " check(s) failed.") << "\n";
    return failures == 0 ? 0 : 1;
}
