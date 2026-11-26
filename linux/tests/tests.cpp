// Engine tests. Each test is a function; a failed check prints its location and the run exits non-zero.
#include <chrono>
#include <deque>
#include <fstream>
#include <iostream>
#include <csignal>
#include <fcntl.h>
#include <sys/wait.h>
#include <unistd.h>

#include "agent.hpp"
#include "chat.hpp"
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

int main() {
    const std::pair<const char*, void (*)()> tests[] = {
        {"workspace", test_workspace}, {"diff", test_diff}, {"stream and endpoint", test_stream_and_endpoint}, {"tool call recovery", test_recovery},
        {"agent edit and undo", test_agent_edit_undo}, {"agent refusals", test_agent_refusals}, {"commands and rules", test_commands_and_rules},
        {"undo of command changes", test_undo_of_command_changes}, {"custom agents and commands", test_custom_agents_and_commands}, {"compaction", test_compaction}, {"MCP servers", test_mcp}, {"chat", test_chat}, {"queue and steer", test_queue_and_steer},
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
