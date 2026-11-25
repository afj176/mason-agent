// Engine tests. Each test is a function; a failed check prints its location and the run exits non-zero.
#include <chrono>
#include <deque>
#include <fstream>
#include <iostream>
#include <unistd.h>

#include "agent.hpp"
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
    std::string name() const override { return "scripted"; }
    ModelReply complete(const std::vector<ChatMessage>& messages, const std::vector<ToolSpec>&, const std::function<void(const std::string&)>& on_text, const CancelToken&) override {
        seen.push_back(messages);
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

int main() {
    const std::pair<const char*, void (*)()> tests[] = {
        {"workspace", test_workspace}, {"diff", test_diff}, {"stream and endpoint", test_stream_and_endpoint}, {"tool call recovery", test_recovery},
        {"agent edit and undo", test_agent_edit_undo}, {"agent refusals", test_agent_refusals}, {"commands and rules", test_commands_and_rules},
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
