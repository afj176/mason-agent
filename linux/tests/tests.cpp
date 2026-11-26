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

#include "notes.hpp"
#include "palette.hpp"
#include "gpu.hpp"
#include "generation.hpp"
#include "agent.hpp"
#include "derived.hpp"
#include "navigation.hpp"
#include "transfer.hpp"
#include "acceptance.hpp"
#include "browser.hpp"
#include "planning.hpp"
#include "chat.hpp"
#include "schedules.hpp"
#include "script.hpp"
#include "experiments.hpp"
#include "delivery.hpp"
#include "background.hpp"
#include "roles.hpp"
#include "project_tests.hpp"
#include "preview.hpp"
#include "network.hpp"
#include "bundles.hpp"
#include "browse.hpp"
#include "shell.hpp"
#include "models.hpp"
#include "model_memory.hpp"

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
    /// The names of the tools offered with each request.
    std::vector<std::vector<std::string>> seen_tools;
    int window = 0;
    int context_window() override { return window; }
    /// Called at the start of each request, with its number, so a test can act while the "model" is busy.
    std::function<void(size_t)> on_request;
    int delay_ms = 0;
    std::string name() const override { return "scripted"; }
    ModelReply complete(const std::vector<ChatMessage>& messages, const std::vector<ToolSpec>& tools, const std::function<void(const std::string&)>& on_text, const CancelToken& cancel) override {
        seen.push_back(messages);
        seen_tools.emplace_back();
        for (const ToolSpec& tool : tools) seen_tools.back().push_back(tool.name);
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

/// A GGUF header with chosen metadata, as llama.cpp writes it (little-endian), and no tensor data.
struct GgufHeader {
    std::string body;
    uint64_t count = 0;
    template <typename T>
    static void put(std::string& out, T value) { out.append(reinterpret_cast<const char*>(&value), sizeof value); }
    static void put_text(std::string& out, const std::string& text) { put<uint64_t>(out, text.size()); out += text; }
    GgufHeader& text(const std::string& key, const std::string& value) {
        put_text(body, key); put<uint32_t>(body, 8); put_text(body, value); count++;
        return *this;
    }
    GgufHeader& u32(const std::string& key, uint32_t value) {
        put_text(body, key); put<uint32_t>(body, 4); put<uint32_t>(body, value); count++;
        return *this;
    }
    /// A string array, like a tokenizer's vocabulary, which the reader must skip.
    GgufHeader& words(const std::string& key, size_t length) {
        put_text(body, key); put<uint32_t>(body, 9); put<uint32_t>(body, 8); put<uint64_t>(body, length);
        for (size_t i = 0; i < length; i++) put_text(body, "w" + std::to_string(i));
        count++;
        return *this;
    }
    /// Writes the header and makes the file `size` bytes long (sparse), so the weights weigh that much.
    void write(const fs::path& file, int64_t size, uint32_t magic = 0x46554747) const {
        std::string out;
        put<uint32_t>(out, magic); put<uint32_t>(out, 3); put<uint64_t>(out, 1); put<uint64_t>(out, count);
        out += body;
        fs::create_directories(file.parent_path());
        std::ofstream(file, std::ios::binary) << out;
        fs::resize_file(file, static_cast<uintmax_t>(std::max<int64_t>(size, static_cast<int64_t>(out.size()))));
    }
};

static void test_model_memory() {
    Sandbox box;
    const fs::path root = box.base / "models";
    const int64_t GiB = 1LL << 30;

    // KV per token = layers × KV heads × (key + value length) × 2 bytes; the key length defaults to width ÷ heads.
    GgufHeader().text("general.architecture", "qwen2").u32("qwen2.block_count", 28).u32("qwen2.attention.head_count", 28).u32("qwen2.attention.head_count_kv", 4)
        .u32("qwen2.embedding_length", 3584).u32("qwen2.context_length", 32768).words("tokenizer.ggml.tokens", 2000).write(root / "coder/Coder-7B-Q4_K_M.gguf", 4 * GiB);
    MemoryProfile qwen = read_gguf_profile(root / "coder/Coder-7B-Q4_K_M.gguf");
    CHECK(qwen.estimated() && qwen.architecture == "qwen2");
    CHECK(qwen.kv_bytes_per_token == 28.0 * 4 * (128 + 128) * 2);
    CHECK(qwen.workspace_bytes == 0.75 * kGiB && qwen.weight_bytes == 4.0 * GiB);
    CHECK(qwen.max_context == 32768 && qwen.context_declared);

    GgufHeader().text("general.architecture", "llama").u32("llama.block_count", 32).u32("llama.attention.head_count", 32).u32("llama.attention.head_count_kv", 8)
        .u32("llama.embedding_length", 4096).u32("llama.attention.key_length", 256).u32("llama.attention.value_length", 128).write(root / "llama/l.gguf", 100);
    const MemoryProfile llama = read_gguf_profile(root / "llama/l.gguf");
    CHECK(llama.kv_bytes_per_token == 32.0 * 8 * (256 + 128) * 2);
    CHECK(!llama.context_declared && llama.max_context == 4096 && llama.context_cap() == 32768);

    // Qwen 3.5 hybrid: only every fourth trunk layer keeps a KV cache; the recurrent layers add a fixed state.
    GgufHeader().text("general.architecture", "qwen35").u32("qwen35.block_count", 40).u32("qwen35.attention.head_count", 16).u32("qwen35.attention.head_count_kv", 2)
        .u32("qwen35.embedding_length", 4096).u32("qwen35.attention.key_length", 256).u32("qwen35.attention.value_length", 256).u32("qwen35.full_attention_interval", 4)
        .u32("qwen35.ssm.state_size", 128).u32("qwen35.ssm.inner_size", 4096).u32("qwen35.ssm.group_count", 16).u32("qwen35.ssm.conv_kernel", 4).u32("qwen35.ssm.time_step_rank", 32)
        .u32("qwen35.context_length", 262144).write(root / "hybrid/h.gguf", 100);
    const MemoryProfile hybrid = read_gguf_profile(root / "hybrid/h.gguf");
    CHECK(hybrid.estimated());
    CHECK(hybrid.kv_bytes_per_token == 10.0 * 2 * 512 * 2);
    CHECK(hybrid.workspace_bytes == 0.75 * kGiB + 30.0 * (128 * 4096 + 3 * (4096 + 2 * 16 * 128)) * 4 * 2);
    CHECK(hybrid.basis.find("10 full-attention + 30 recurrent") != std::string::npos);

    // An architecture without an estimate keeps its declared context and its weights.
    GgufHeader().text("general.architecture", "nomic-bert").u32("nomic-bert.context_length", 8192).write(root / "embed/nomic-embed-text.gguf", 2 * GiB);
    const MemoryProfile bert = read_gguf_profile(root / "embed/nomic-embed-text.gguf");
    CHECK(bert.unknown == "This GGUF architecture needs a runtime-specific memory estimate.");
    CHECK(bert.max_context == 8192 && bert.context_declared && bert.weight_bytes == 2.0 * GiB && bert.architecture == "nomic-bert");

    // Not a GGUF: no claim, but the size on disk is still known.
    GgufHeader().write(root / "bad/bad.gguf", 4096, 0x12345678);
    const MemoryProfile bad = read_gguf_profile(root / "bad/bad.gguf");
    CHECK(!bad.estimated() && bad.weight_bytes == 4096 && bad.unknown.find("could not be read safely") != std::string::npos);

    // A split GGUF weighs all its parts.
    GgufHeader().text("general.architecture", "llama").u32("llama.block_count", 2).u32("llama.attention.head_count", 2).u32("llama.embedding_length", 64)
        .write(root / "split/Big-00001-of-00002.gguf", 3 * GiB);
    std::ofstream(root / "split/Big-00002-of-00002.gguf", std::ios::binary) << "x";
    fs::resize_file(root / "split/Big-00002-of-00002.gguf", static_cast<uintmax_t>(2 * GiB));
    CHECK(gguf_parts(root / "split/Big-00001-of-00002.gguf").size() == 2);
    CHECK(read_gguf_profile(root / "split/Big-00001-of-00002.gguf").weight_bytes == 5.0 * GiB);
    CHECK(gguf_total_bytes(root / "coder/Coder-7B-Q4_K_M.gguf") == 4 * GiB);

    // Transformers folders: FP16 serving weights from the safetensors headers, KV from config.json.
    const fs::path folder = root / "hf/tiny";
    fs::create_directories(folder);
    write_file_atomic(folder / "config.json", R"({"model_type":"llama","num_hidden_layers":2,"num_attention_heads":4,"num_key_value_heads":2,"hidden_size":64,"max_position_embeddings":8192,"vocab_size":1000})");
    {
        const std::string header = R"({"w":{"dtype":"F32","shape":[2,3],"data_offsets":[0,24]},"__metadata__":{"format":"pt"}})";
        std::string file;
        GgufHeader::put<uint64_t>(file, header.size());
        file += header + std::string(24, '\0');
        std::ofstream(folder / "model.safetensors", std::ios::binary) << file;
    }
    const MemoryProfile tiny = read_transformers_profile(folder);
    CHECK(tiny.estimated() && tiny.weight_bytes == 12 && tiny.kv_bytes_per_token == 2.0 * 2 * 2 * 16 * 2 && tiny.workspace_per_token == 64.0 * 12 + 4000);
    CHECK(tiny.max_context == 8192 && tiny.context_declared && tiny.architecture == "llama");

    // Fit: 4 GiB of weights with 8.2 of 12 GiB free, 0.6 GiB kept spare.
    MemoryReading gpu{"NVIDIA GeForce RTX 4070", 8.2 * kGiB, 12.0 * kGiB, true};
    CHECK(gpu.label() == "RTX 4070 · 8.2 GiB free");
    FitAssessment at16 = assess(qwen, gpu, 16384);
    CHECK(at16.fit == Fit::Likely && at16.label() == "Likely fits");
    CHECK(std::abs(at16.low_gib - 5.625) < 1e-9 && std::abs(at16.high_gib - (4 * 1.12 + 0.875 * 1.15 + 0.75 * 1.5)) < 1e-9 && std::abs(at16.spare_gib - 0.6) < 1e-9);
    CHECK(fit_detail(at16, qwen, false) == "5.6–6.6 GiB at 16K · 0.6 GiB kept spare. An estimate, not a guarantee.");
    CHECK(assess(qwen, gpu, 32768).fit == Fit::Tight);
    CHECK(assess(qwen, gpu, 65536).tokens == 32768);  // never more than the model holds
    MemoryProfile big = qwen;
    big.weight_bytes = 7.9 * kGiB;
    const FitAssessment over = assess(big, gpu, 2048);
    CHECK(over.fit == Fit::TooLarge && !over.lower_bound && over.label() == "Too large for free memory");
    CHECK(fit_detail(over, big, false).find("more than the 8.2 GiB free.") != std::string::npos);
    CHECK(assess(qwen, MemoryReading{}, 2048).fit == Fit::Waiting && assess(qwen, MemoryReading{}, 2048).label() == "Waiting for a memory reading");
    MemoryProfile unknown_big = bert;
    unknown_big.weight_bytes = 16.4 * kGiB;
    const FitAssessment bound = assess(unknown_big, gpu, 2048);
    CHECK(bound.fit == Fit::TooLarge && bound.lower_bound);
    CHECK(fit_detail(bound, unknown_big, false) == "The weights alone are 16.4 GiB and 8.2 GiB is free. Context memory for nomic-bert is not estimated yet.");
    const FitAssessment unverified = assess(bert, gpu, 2048);
    CHECK(unverified.fit == Fit::Unverified && unverified.label() == "Fit unverified");
    CHECK(fit_detail(unverified, bert, false) == "The weights are 2.0 GiB. Context memory for nomic-bert is not estimated, so fit is checked when it starts.");
    CHECK(fit_detail(unverified, bert, true) == "Runs from system RAM. Available RAM is not checked here, and CPU turns are much slower.");

    // Ladder steps and labels.
    CHECK(context_label(2048) == "2K" && context_label(40960) == "40K" && context_label(131072) == "128K" && context_label(128000) == "128K" && context_label(16384) == "16K" &&
          context_label(32000) == "32K" && context_label(512) == "512");
    CHECK(ladder_steps(qwen) == (std::vector<int>{2048, 4096, 8192, 16384, 32768}));
    MemoryProfile odd = qwen;
    odd.max_context = 40960;
    CHECK(ladder_steps(odd) == (std::vector<int>{2048, 4096, 8192, 16384, 32768, 40960}));
    odd.max_context = 131072;
    CHECK(ladder_steps(odd) == (std::vector<int>{4096, 8192, 16384, 32768, 65536, 131072}));
    odd.max_context = 262144;
    CHECK(ladder_steps(odd) == (std::vector<int>{8192, 16384, 32768, 65536, 131072, 262144}));
    odd.max_context = 128000;
    CHECK(ladder_steps(odd) == (std::vector<int>{4096, 8192, 16384, 32768, 65536, 128000}) && context_label(ladder_steps(odd).back()) == "128K");
    CHECK(ladder_steps(llama) == (std::vector<int>{2048, 4096, 8192, 16384, 32768}));  // undeclared: up to 32K
    CHECK(parse_context_size("16384") == 16384 && parse_context_size("16k") == 16384 && parse_context_size("16K") == 16384 && parse_context_size(" 32k ") == 32768);
    CHECK(!parse_context_size("cpu") && !parse_context_size("100") && !parse_context_size("k") && !parse_context_size("-16k"));
    CHECK(group_digits(32768) == "32,768" && group_digits(262144) == "262,144" && group_digits(512) == "512");

    // Recommendations.
    const Recommendation fits = recommend(qwen, gpu);
    CHECK(fits.gpu && fits.context == 16384 && fits.text == "Suggested: 16K on GPU · largest likely fit");
    const Recommendation cpu = recommend(unknown_big, gpu);
    CHECK(!cpu.gpu && cpu.context == 2048 && cpu.text == "Suggested: CPU · weights exceed free memory");
    MemoryProfile tight = qwen;
    tight.weight_bytes = 7.5 * kGiB;
    const Recommendation headroom = recommend(tight, gpu);
    CHECK(!headroom.gpu && headroom.text == "Suggested: CPU · GPU headroom is limited");
    const Recommendation start = recommend(bert, gpu);
    CHECK(start.gpu && start.context == 2048 && start.text == "Suggested starting point: GPU · 2K · fit unverified");
    CHECK(recommend(qwen, MemoryReading{}).text == "Suggested starting point: GPU · 2K · fit unverified");

    // Readings.
    const auto nvidia = parse_nvidia_memory("NVIDIA GeForce RTX 4070, 8396, 12282\nNVIDIA RTX A2000, 9000, 12000\nnot a card\n");
    CHECK(nvidia && nvidia->device == "NVIDIA RTX A2000" && nvidia->gpu && nvidia->free_bytes == 9000.0 * 1048576 && nvidia->total_bytes == 12000.0 * 1048576);
    CHECK(!parse_nvidia_memory("") && !parse_nvidia_memory("No devices were found\n"));
    const auto system = parse_meminfo("MemTotal:       16384000 kB\nMemFree:  100 kB\nMemAvailable:    8192000 kB\n");
    CHECK(system && !system->gpu && system->device == "System memory" && system->total_bytes == 16384000.0 * 1024 && system->free_bytes == 8192000.0 * 1024);
    CHECK(system && system->label() == "System memory · " + gib_text(8192000.0 * 1024 / kGiB) + " GiB free");
    CHECK(!parse_meminfo("nothing"));

    // List summaries.
    LocalModel coder{"Coder-7B-Q4_K_M", (root / "coder/Coder-7B-Q4_K_M.gguf").string(), 4 * GiB, "Test"};
    const FitSummary summary = summarize_fit(coder, qwen, gpu);
    std::string marks;
    for (const FitMark& mark : summary.marks) marks += mark.symbol;
    CHECK(marks == "●●●●◐" && summary.note == "up to 16K" && summary.marks.back().tone == Tone::Tight);
    const FitSummary none = summarize_fit(coder, big, gpu);
    CHECK(none.marks.size() == 1 && none.marks[0].symbol == "✕" && starts_with(none.note, "needs ") && ends_with(none.note, "+ GiB · try CPU"));
    CHECK(summarize_fit(coder, unknown_big, gpu).note == "weights alone exceed free memory");
    CHECK(summarize_fit(coder, bert, gpu).note == "fit unverified");
    LocalModel embed{"nomic-embed-text", (root / "embed/nomic-embed-text.gguf").string(), 2 * GiB, "Test"};
    CHECK(summarize_fit(embed, bert, gpu).note == "fits");
    CHECK(quant_in_name("Qwen2.5-Coder-7B-Instruct-Q4_K_M") == "Q4_K_M" && quant_in_name("model-IQ4_XS") == "IQ4_XS" && quant_in_name("plain").empty());
    CHECK(LocalModel{"m", "/x/hub/models--Qwen--Qwen2.5-Coder-7B-Instruct-GGUF/snapshots/abc/m.gguf", 1, "Test"}.repository() == "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF");
    CHECK(coder.repository().empty() && coder.role_label() == "Coding agent");
    CHECK(fit_explanation(qwen).rfind("Model capacity: 32,768 tokens. GGUF file size, FP16 KV cache and bounded prompt batching. Based on", 0) == 0);
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

    // The map ranks a name, and scripts run single operations that print JSON Lines.
    {
        Sandbox box;
        box.write("main.py", "from calc import add\nprint(add(2, 2))\n");
        box.write("test_calc.py", "from calc import add\n\ndef test_add():\n    assert add(1, 2) == 3\n");
        const Workspace workspace(box.project.string());
        const MapResult found = repository_map::query(workspace, "calc.add");
        std::vector<std::string> kinds;
        for (const MapMatch& match : found.matches) kinds.push_back(match.kind);
        CHECK(!found.matches.empty() && found.matches[0].path == "calc.py" && found.matches[0].line == 1);  // the definition comes first
        CHECK(kinds == (std::vector<std::string>{"definition candidate", "import candidate", "import candidate", "reference candidate", "test reference candidate"}));
        CHECK(repository_map::report(found, "calc.add").find("main.py:2  [reference candidate]  print(add(2, 2))") != std::string::npos);

        std::vector<json> events;
        const auto collect = [&](const std::string& line) { events.push_back(json::parse(line)); };
        const std::string root = box.project.string();
        CHECK(script::run({"map", "--workspace", root, "--query", "add"}, collect) == 0);
        CHECK(events.size() == 1 && events[0]["type"] == "map.result" && events[0]["data"]["matches"].size() == 5);
        events.clear();
        CHECK(script::run({"map", "--workspace", root}, collect) == 1);
        CHECK(events.size() == 1 && events[0]["data"]["message"] == "--query is required.");
        events.clear();
        CHECK(script::run({"git-status", "--workspace", root}, collect) == 0);
        CHECK(events.size() == 1 && events[0]["data"]["repository"] == false);

        events.clear();
        ScriptedModel model;
        model.replies = {call("read_file", {{"path", "calc.py"}}), call("write_file", {{"path", "calc.py"}, {"content", "x"}}), say("It subtracts.")};
        CHECK(script::run({"exec", "--workspace", root, "--prompt", "What does add do?", "--profile", (box.home / "runs").string()}, collect, &model) == 0);
        std::vector<std::string> types;
        for (const json& event : events) types.push_back(event["type"].get<std::string>());
        CHECK(types == (std::vector<std::string>{"run.started", "tool.result", "tool.result", "run.finished"}));
        if (events.size() == 4) {
            CHECK(events[0]["data"]["mode"] == "ask" && events[1]["data"]["tool"] == "read_file" && events[3]["data"]["answer"] == "It subtracts.");
        }
        CHECK(box.read("calc.py") == "def add(a, b):\n    return a - b\n");  // without --auto the run only reads
        events.clear();
        CHECK(script::run({"exec", "--workspace", root, "--prompt", "x", "--profile", (box.project / "journal").string()}, collect, &model) == 1);  // the journal may not be inside the project
    }

    // An experiment starts from the project as it stands, and a hunk comes back.
    {
        Sandbox box;
        const Workspace workspace(box.project.string());
        const CommandResult made = shell::run("git init -q . && git add . && git -c user.name=t -c user.email=t@t.invalid commit -qm start", workspace, false);
        CHECK(made.exit_code == 0);
        if (made.exit_code == 0) {
            // An uncommitted edit and an untracked file: the experiment starts with both.
            box.write("calc.py", "def add(a, b):\n    return a - b\n\n\ndef twice(a):\n    return a * 2\n");
            box.write("notes.txt", "draft\n");
            const std::string said = experiments::command("new try", workspace, box.home);
            const std::vector<Experiment> mine = experiments::list(workspace, box.home);
            CHECK(mine.size() == 1);
            if (mine.size() == 1) {
                const Experiment experiment = mine[0];
                CHECK(said.find(experiment.path) != std::string::npos && experiment.branch == "mason/try");
                CHECK(read_file(fs::path(experiment.path) / "notes.txt") == "draft\n" && read_file(fs::path(experiment.path) / "calc.py").find("twice") != std::string::npos);
                CHECK(experiments::changed(experiment).empty());  // a new experiment has changed nothing
                const std::string index = shell::run("git status --porcelain", workspace, false).output;
                CHECK(index.find(" M calc.py") != std::string::npos && index.find("?? notes.txt") != std::string::npos);  // the project's own index is untouched

                write_file_atomic(fs::path(experiment.path) / "calc.py", "def add(a, b):\n    return a + b\n\n\ndef twice(a):\n    return a * 2\n");
                CHECK(experiments::changed(experiment) == std::vector<std::string>{"calc.py"});
                const std::vector<ExperimentHunk> hunks = experiments::hunks(experiment, "calc.py");
                CHECK(hunks.size() == 1);
                if (hunks.size() == 1) {
                    experiments::command("apply try calc.py " + hunks[0].id, workspace, box.home);
                    CHECK(box.read("calc.py").rfind("def add(a, b):\n    return a + b\n", 0) == 0);  // the hunk is in the project
                    bool refused = false;
                    try { experiments::apply(experiment, "calc.py", hunks[0].id); } catch (const Error&) { refused = true; }
                    CHECK(refused);  // a hunk that no longer fits is refused
                }
                bool refused = false;
                try { experiments::apply(experiment, "calc.py", "00000000"); } catch (const Error&) { refused = true; }
                CHECK(refused);  // an unknown hunk is refused
                refused = false;
                try { experiments::create_worktree(workspace, (box.project / "inside").string(), "x"); } catch (const Error&) { refused = true; }
                CHECK(refused);  // a worktree inside the project is refused
            }
        }
    }

    // A delivery is one commit of chosen hunks, and leaves the checkout alone.
    {
        Sandbox box;
        const Workspace workspace(box.project.string());
        std::vector<std::string> lines;
        for (int i = 1; i <= 30; i++) lines.push_back("line " + std::to_string(i));
        box.write("big.txt", join(lines, "\n") + "\n");
        const CommandResult made = shell::run("git init -q . && git symbolic-ref HEAD refs/heads/main && git config user.name t && git config user.email t@t.invalid && git add . && git commit -qm start && git remote add origin git@github.com:someone/shop.git", workspace, false);
        CHECK(made.exit_code == 0);
        if (made.exit_code == 0) {
            lines[1] = "line two";
            lines[27] = "line twenty-eight";
            box.write("big.txt", join(lines, "\n") + "\n");
            const std::string head = trim(shell::run("git rev-parse HEAD", workspace, false).output);
            const DeliveryReview reviewed = delivery::review(workspace, "main");
            CHECK(reviewed.patches.size() == 2);  // two separate hunks
            if (reviewed.patches.size() == 2) {
                const DeliveryDraft draft = delivery::draft(workspace, "main", reviewed.revision, "Spell out two", {reviewed.patches[0].hunk.id}, box.home);
                CHECK(draft.repository == "someone/shop" && draft.branch.rfind("mason/review-", 0) == 0 && draft.url.empty() && draft.paths == std::vector<std::string>{"big.txt"});
                const std::string shown = shell::run("git show " + draft.commit + ":big.txt && git log -1 --format=%s%n%an " + draft.commit, workspace, false).output;
                CHECK(shown.find("line two\n") != std::string::npos && shown.find("line 28\n") != std::string::npos && shown.find("twenty-eight") == std::string::npos);  // only the chosen hunk
                CHECK(shown.find("Spell out two\nt\n") != std::string::npos);  // the commit carries the user's name
                const std::string after = shell::run("git rev-parse HEAD && git status --porcelain", workspace, false).output;
                CHECK(after.rfind(head, 0) == 0 && after.find(" M big.txt") != std::string::npos);  // the branch, the index and the files are as they were
                const auto current = delivery::current(workspace, box.home);
                CHECK(current && current->commit == draft.commit);
                const delivery::Said all = delivery::command("draft main all Both lines", workspace, box.home);
                CHECK(all.text.find("\"Both lines\" onto main") != std::string::npos && all.text.find("someone/shop") != std::string::npos);
                bool refused = false;
                try { delivery::draft(workspace, "main", std::string(40, '0'), "x", {"a"}, box.home); } catch (const Error&) { refused = true; }
                CHECK(refused);  // a base that moved is refused
                refused = false;
                try { delivery::feedback(workspace, draft); } catch (const Error&) { refused = true; }
                CHECK(refused);  // feedback needs a published draft
            }
        }
        CHECK(delivery::repository("https://github.com/Owner/Repo.git") == "Owner/Repo");
        bool refused = false;
        try { delivery::repository("https://example.com/owner/repo.git"); } catch (const Error&) { refused = true; }
        CHECK(refused);
        const DeliveryFeedback feedback = delivery::parse_feedback(
            R"J({"url":"https://github.com/someone/shop/pull/7","headRefOid":"abc","statusCheckRollup":[{"name":"build","conclusion":"FAILURE","detailsUrl":"https://ci/1"},{"name":"lint","conclusion":"SUCCESS"},{"context":"deploy","state":"ERROR"}],"reviews":[{"state":"CHANGES_REQUESTED","body":"Rename it."},{"state":"APPROVED","body":"ok"}]})J",
            R"J([{"path":"big.txt","line":2,"body":"Why two?"}])J");
        CHECK(feedback.observations == (std::vector<std::string>{"CI · build · FAILURE · https://ci/1", "CI · deploy · ERROR · ", "Review · Rename it.", "big.txt:2 · Why two?"}));
        CHECK(feedback.head == "abc");
    }

    // A background task waits for its approval, finishes, and can be cancelled.
    {
        Sandbox box;
        const Workspace workspace(box.project.string());
        const std::string root = fs::weakly_canonical(box.project).string();
        auto record = [&](const std::string& id) {
            fs::create_directories(box.home / "background-tasks" / id);
            write_file_atomic(box.home / "background-tasks" / id / "request.json", json{{"id", id}, {"workspace", root}, {"prompt", "Fix add"}, {"endpoint", {{"url", "http://127.0.0.1:1"}, {"model", "m"}, {"key", ""}}}, {"auto", false}, {"created", 1}}.dump());
            write_file_atomic(box.home / "background-tasks" / id / "status.json", json{{"state", "Starting"}, {"detail", ""}, {"pid", 0}, {"started", static_cast<long long>(std::time(nullptr))}, {"session", ""}}.dump());
        };
        auto waiting = [&](const std::string& id) {
            TaskStatus state = background::status(id, box.home);
            for (int i = 0; i < 100 && state.state != "Waiting for approval"; i++) {
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                state = background::status(id, box.home);
            }
            return state;
        };
        // The worker runs here in the test; a real one is the same code in its own process.
        const std::string first = "aaaaaaaa0001";
        record(first);
        ScriptedModel fix;
        fix.replies = {call("write_file", {{"path", "calc.py"}, {"content", "def add(a, b):\n    return a + b\n"}}), say("Fixed add.")};
        int code = -1;
        std::thread worker([&] { code = background::work(first, box.home, &fix); });
        const TaskStatus asked = waiting(first);
        CHECK(asked.state == "Waiting for approval" && asked.approval && asked.approval->tool == "write_file");
        bool refused = false;
        try { background::decide(first, "not-this-one", true, box.home); } catch (const Error&) { refused = true; }
        CHECK(refused);  // an answer for another approval is refused
        CHECK(box.read("calc.py") == "def add(a, b):\n    return a - b\n");  // nothing is written while it waits
        const std::string listed = background::command("", workspace, std::nullopt, false, "/bin/sh", box.home);
        CHECK(listed.find(first) != std::string::npos && listed.find("waiting on:") != std::string::npos);
        background::command("allow " + first, workspace, std::nullopt, false, "/bin/sh", box.home);
        worker.join();
        CHECK(code == 0 && background::status(first, box.home).state == "Completed");
        CHECK(box.read("calc.py") == "def add(a, b):\n    return a + b\n");
        const std::string shown = background::command("show " + first, workspace, std::nullopt, false, "/bin/sh", box.home);
        CHECK(shown.find("Fixed add.") != std::string::npos && shown.find("write_file calc.py") != std::string::npos);

        const std::string second = "aaaaaaaa0002";
        record(second);
        ScriptedModel touch;
        touch.replies = {call("run_shell", {{"command", "touch made"}}), say("Done.")};
        code = -1;
        std::thread other([&] { code = background::work(second, box.home, &touch); });
        waiting(second);
        background::cancel(second, box.home);
        other.join();
        CHECK(code == 2 && background::status(second, box.home).state == "Cancelled");
        CHECK(!fs::exists(box.project / "made"));  // a cancelled task's pending command never ran
        refused = false;
        try { background::status("../../etc", box.home); } catch (const Error&) { refused = true; }
        CHECK(refused);  // an id is never a path
    }

    // A role counts its calls, writes a trace, and is chosen by command.
    {
        Sandbox box;
        const fs::path trace = box.home / "traces";
        ScriptedModel three;
        three.replies = {say("one"), say("two"), say("three")};
        RoleModel limited(three, "reviewer", 2, trace);
        const ModelReply first = limited.complete({ChatMessage{"user", "Look", {}, ""}}, {}, [](const std::string&) {}, nullptr);
        limited.complete({ChatMessage{"user", "Again", {}, ""}}, {}, [](const std::string&) {}, nullptr);
        CHECK(first.text == "one");
        bool refused = false;
        try { limited.complete({}, {}, [](const std::string&) {}, nullptr); } catch (const Error&) { refused = true; }
        CHECK(refused);  // the third call is past the limit
        std::vector<fs::path> written;
        for (const auto& entry : fs::directory_iterator(trace)) written.push_back(entry.path());
        std::sort(written.begin(), written.end());
        CHECK(written.size() == 2);
        if (written.size() == 2) {
            const json record = json::parse(read_file(written[0]));
            CHECK(record["role"] == "reviewer" && record["reply"]["text"] == "one" && record["messages"][0]["content"] == "Look");
        }
        CHECK(roles::command("", box.home).find("reviewer  the model doing the work") != std::string::npos);
        CHECK(roles::command("reviewer http://127.0.0.1:8081 judge", box.home).find("judge at http://127.0.0.1:8081") != std::string::npos);
        refused = false;
        try { roles::command("visual http://example.com seer", box.home); } catch (const std::exception&) { refused = true; }
        CHECK(refused);  // a role's model is on this computer, like any other
        roles::command("limit 3", box.home);
        roles::command("trace " + trace.string(), box.home);
        const ModelRoles chosen = roles::load(box.home);
        CHECK(chosen.reviewer && chosen.reviewer->model == "judge" && chosen.limit == 3);
        const Workspace workspace(box.project.string());
        ScriptedModel main_model;
        main_model.replies = {say("from the main model")};
        CHECK(roles::reviewer(main_model, &workspace, box.home)->name() == "judge");  // the chosen reviewer reviews
        CHECK(roles::implementer(main_model, &workspace, box.home) != nullptr);  // with tracing on, the working model is written down
        roles::command("trace " + (box.project / "t").string(), box.home);
        CHECK(roles::implementer(main_model, &workspace, box.home) == nullptr);  // a trace folder inside the project is not used
        roles::command("reviewer off", box.home);
        roles::command("trace off", box.home);
        CHECK(roles::reviewer(main_model, &workspace, box.home)->complete({}, {}, [](const std::string&) {}, nullptr).text == "from the main model");  // without a reviewer the main model reviews
    }

    // A project's own test is pinned and is the evidence for its requirement.
    {
        Sandbox box;
        box.write("mason.workflows.json", R"({"version": 1, "workflows": {"test": {"executable": "sh", "arguments": ["run-tests.sh"]}}})");
        box.write("run-tests.sh", "if grep -q 'a + b' calc.py; then echo 'ok 1 - adds'; else echo 'not ok 1 - adds'; fi\necho 'ok 2 - other'\n");
        fs::create_directories(box.project / ".mason");
        box.write(".mason/tests.json", R"([{"requirement": "Make add return the sum", "workflow": "test", "format": "tap", "report": "", "test": "adds", "testFiles": ["run-tests.sh"], "inputs": ["calc.py"]}])");
        const Workspace workspace(box.project.string());
        CHECK(project_tests::load(workspace).size() == 1 && project_tests::load(workspace)[0].test == "adds");

        const DerivedPlan plan = derived::prepare("Make add return the sum.", workspace, nullptr);
        CHECK(plan.checks.size() == 1 && plan.checks[0].kind == "project_test");
        if (plan.checks.size() == 1) {
            bool baseline_failed = false;
            for (const auto& step : plan.checks[0].steps) baseline_failed = baseline_failed || step == std::vector<std::string>{"baseline", "failed"};
            CHECK(baseline_failed);  // the baseline is run before any work
            std::vector<DerivedResult> results = derived::verify(plan, workspace);
            CHECK(results[0].state == "failed" && results[0].detail.find("baseline: failed; now: failed") != std::string::npos);
            box.write("calc.py", "def add(a, b):\n    return a + b\n");
            results = derived::verify(plan, workspace);
            CHECK(results[0].state == "passed" && results[0].detail.find("baseline: failed; now: passed") != std::string::npos);
            // Making the test itself say yes is not passing it.
            box.write("calc.py", "def add(a, b):\n    return a - b\n");
            box.write("run-tests.sh", "echo 'ok 1 - adds'\n");
            results = derived::verify(plan, workspace);
            CHECK(results[0].state == "failed" && results[0].detail.find("pinned") != std::string::npos);
        }
        bool bound_other = false;
        for (const DerivedCheck& check : derived::prepare("Rename the function.", workspace, nullptr).checks) bound_other = bound_other || check.kind == "project_test";
        CHECK(!bound_other);  // only the stated requirement is bound
        box.write(".mason/tests.json", R"([{"requirement": "x", "workflow": "missing", "format": "tap", "test": "t", "testFiles": ["a"], "inputs": ["b"]}])");
        bool refused = false;
        try { project_tests::load(workspace); } catch (const Error&) { refused = true; }
        CHECK(refused);  // a test must name a workflow the project has

        using Outcomes = std::map<std::string, std::string>;
        CHECK(project_tests::outcomes("junit", R"(<testsuite><testcase classname="Price" name="discount"/><testcase classname="Price" name="tax &amp; total"><failure message="no"/></testcase><testcase name="later"><skipped/></testcase></testsuite>)") ==
              (Outcomes{{"discount", "passed"}, {"Price.discount", "passed"}, {"tax & total", "failed"}, {"Price.tax & total", "failed"}, {"later", "skipped"}}));
        CHECK(project_tests::outcomes("trx", R"(<UnitTestResult testName="Adds" outcome="Passed" /><UnitTestResult testName="Subtracts" outcome="Failed">)") == (Outcomes{{"Adds", "passed"}, {"Subtracts", "failed"}}));
        CHECK(project_tests::outcomes("go-json", "{\"Action\":\"run\",\"Test\":\"TestAdd\"}\n{\"Action\":\"pass\",\"Test\":\"TestAdd\"}\n{\"Action\":\"fail\",\"Test\":\"TestSub\"}\n{\"Action\":\"pass\"}") ==
              (Outcomes{{"TestAdd", "passed"}, {"TestSub", "failed"}}));
        CHECK(project_tests::outcomes("flutter-json", "{\"type\":\"testStart\",\"test\":{\"id\":1,\"name\":\"adds\"}}\n{\"type\":\"testDone\",\"testID\":1,\"result\":\"success\",\"skipped\":false}\n{\"type\":\"testStart\",\"test\":{\"id\":2,\"name\":\"draws\"}}\n{\"type\":\"testDone\",\"testID\":2,\"result\":\"failure\"}") ==
              (Outcomes{{"adds", "passed"}, {"draws", "failed"}}));
        const Outcomes tap = project_tests::outcomes("tap", "TAP version 13\n    ok 1 - inner\nok 1 - suite discount\nnot ok 2 - tax\nok 3 - later # SKIP not yet\nok 4 - tax\n");
        CHECK(tap == (Outcomes{{"inner", "passed"}, {"suite discount", "passed"}, {"tax", "failed"}, {"later", "skipped"}}));
        CHECK(project_tests::outcome_of("discount", tap) == std::optional<std::string>("passed"));  // a name is found at the end of a longer one
        CHECK(!project_tests::outcome_of("missing", tap));
    }

    // The Website tab's pieces, and the tools that show things to the user.
    {
        Sandbox box;
        box.write("index.html", "<!doctype html><title>Shop</title><h1>Shop</h1><button id=\"add\">Add</button>");
        const Workspace workspace(box.project.string());
        {
            const browser::PageServer pages(workspace);
            CHECK(preview::listening(pages.port()));  // the project's pages are served on this computer
            CHECK(pages.address_of("sub dir/index.html") == "http://127.0.0.1:" + std::to_string(pages.port()) + "/sub%20dir/index.html");
        }
        const auto pick = preview::pick(R"J({"selector":"#add","kind":"button","label":"Add","html":"<button id=\"add\">Add</button>","console":["error: boom (app.js:3)"]})J", "index.html");
        CHECK(pick && pick->selector == "#add");
        if (pick) {
            const std::string note = preview::evidence(*pick);
            CHECK(note.find("selector: `#add`") != std::string::npos && note.find("label: “Add”") != std::string::npos && note.find("error: boom (app.js:3)") != std::string::npos);
        }
        CHECK(!preview::pick(R"({"kind":"button"})", "index.html"));  // a message without a selector is not a pick
        const std::string script = preview::kScript;
        CHECK(script.find("messageHandlers.mason.postMessage") != std::string::npos && script.rfind("(function () {", 0) == 0);

        box.write("mason.workflows.json", R"J({"version": 1, "workflows": {"dev": {"executable": "sh", "arguments": ["-c", "echo starting; exit 3"]}}})J");
        std::string problem;
        try { preview::DevServer server(workspace, "dev", 18473); } catch (const Error& error) { problem = error.what(); }
        CHECK(problem.find("ended before anything listened") != std::string::npos);  // a workflow that ends is not a server
        problem.clear();
        try { preview::DevServer server(workspace, "none", 18473); } catch (const Error& error) { problem = error.what(); }
        CHECK(!problem.empty());  // an unknown workflow is refused

        SessionStore store(box.home);
        Session session = store.create(workspace.root(), Permission::WorkspaceEdits);
        std::vector<std::string> shown;
        ScriptedModel model;
        model.replies = {call("open_file", {{"path", "calc.py"}, {"line", 2}}), call("open_preview", {{"path", "index.html"}}), call("open_preview", {{"path", "calc.py"}}), say("Look there.")};
        Agent agent(store, model, [](const ApprovalRequest&) { return true; }, quiet);
        agent.set_presenter([&](const std::string& kind, const std::string& path, int line) { shown.push_back(kind + " " + path + " " + std::to_string(line)); });
        agent.run(session, "Show me");
        CHECK(shown == (std::vector<std::string>{"file calc.py 2", "page index.html 1"}));  // a file and a page are shown; a page must be HTML
        bool offered = false;
        for (const ToolSpec& tool : Agent::extra_tools(session, workspace)) offered = offered || tool.name == "open_file";
        CHECK(!offered);  // without an app that can show things, the tools are not offered
    }

    // The network relay tunnels only what the user allowed.
    {
        Sandbox box;
        CHECK(network_policy::host("Registry.NPMJS.org", 443) == "registry.npmjs.org");
        for (const auto& [host, port] : std::vector<std::pair<std::string, int>>{{"registry.npmjs.org", 80}, {"localhost", 443}, {"printer.local", 443}, {"10.0.0.5", 443}, {"intranet", 443}, {"a..b.com", 443}, {"-bad.example.com", 443}, {"db.internal", 443}}) {
            bool refused = false;
            try { network_policy::host(host, port); } catch (const Error&) { refused = true; }
            CHECK(refused);
        }
        CHECK(network_policy::is_public({8, 8, 8, 8}) && network_policy::is_public({93, 184, 216, 34}) && network_policy::is_public({0x26, 0x06, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1}));
        for (const std::vector<uint8_t>& address : std::vector<std::vector<uint8_t>>{{127, 0, 0, 1}, {10, 1, 2, 3}, {192, 168, 1, 1}, {172, 20, 0, 1}, {169, 254, 169, 254}, {100, 64, 0, 1}, {224, 0, 0, 1}, {0, 0, 0, 0},
                                                                                     {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 127, 0, 0, 1}, {0xfe, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1},
                                                                                     {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1}, {0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1}}) {
            CHECK(!network_policy::is_public(address));  // not on the public internet
        }
        CHECK(network_settings::command("", box.home).find("no network") != std::string::npos);
        CHECK(network_settings::command("ask", box.home).find("asks first") != std::string::npos && network_settings::asks(box.home));

        // A stand-in for a site: the project's page server on this computer. The test's resolver points names at it.
        box.write("index.html", "<h1>upstream</h1>");
        const Workspace workspace(box.project.string());
        const browser::PageServer site(workspace);
        std::vector<std::string> asked;
        std::mutex lock;
        NetworkRelay relay([&](const std::string& host) { std::lock_guard<std::mutex> guard(lock); asked.push_back(host); return host != "denied.example.com"; },
                           [&](const std::string&) { return std::vector<RelayAddress>{{{127, 0, 0, 1}, site.port()}}; }, false);
        // A command outside any sandbox speaks to the relay's socket the way the forwarder does.
        auto exchange = [&](NetworkRelay& through, const std::string& request) {
            box.write("request.txt", request);
            const std::string script = "import socket,sys\ns=socket.socket(socket.AF_UNIX)\ns.settimeout(5)\ns.connect(sys.argv[1])\ns.sendall(open('request.txt','rb').read())\nd=b''\ntry:\n  while True:\n    c=s.recv(4096)\n    if not c: break\n    d+=c\n    if b'</h1>' in d: break\nexcept Exception: pass\nsys.stdout.write(d.decode('latin-1'))\n";
            box.write("exchange.py", script);
            return shell::run("python3 exchange.py " + shell::quote(through.socket_path().string()), workspace, false, 30).output;
        };
        const bool python = shell::run("command -v python3", workspace, false, 10).exit_code == 0;
        if (python) {
            const std::string through = exchange(relay, "CONNECT files.example.com:443 HTTP/1.1\r\nHost: files.example.com:443\r\n\r\nGET /index.html HTTP/1.1\r\nHost: files.example.com\r\nConnection: close\r\n\r\n");
            CHECK(through.rfind("HTTP/1.1 200 Connection established", 0) == 0 && through.find("<h1>upstream</h1>") != std::string::npos);  // an allowed host is tunnelled
            exchange(relay, "CONNECT files.example.com:443 HTTP/1.1\r\n\r\nGET /index.html HTTP/1.1\r\nConnection: close\r\n\r\n");
            CHECK(exchange(relay, "CONNECT denied.example.com:443 HTTP/1.1\r\n\r\n").rfind("HTTP/1.1 403", 0) == 0);  // a host the user refused is not reached
            CHECK(exchange(relay, "CONNECT localhost:443 HTTP/1.1\r\n\r\n").rfind("HTTP/1.1 403", 0) == 0);  // this computer is never a destination
            CHECK(exchange(relay, "CONNECT files.example.com:22 HTTP/1.1\r\n\r\n").rfind("HTTP/1.1 403", 0) == 0);  // only port 443
            CHECK(exchange(relay, "GET http://files.example.com/ HTTP/1.1\r\n\r\n").rfind("HTTP/1.1 405", 0) == 0);  // plain HTTP is not relayed
            CHECK(asked == (std::vector<std::string>{"files.example.com", "denied.example.com"}));  // each host is asked about once, and a refused destination never
            const std::vector<std::string> receipts = relay.receipts();
            CHECK(std::any_of(receipts.begin(), receipts.end(), [](const std::string& line) { return line.rfind("allowed: files.example.com", 0) == 0; }) &&
                  std::find(receipts.begin(), receipts.end(), "denied by you: denied.example.com") != receipts.end());
            // The same relay with its real rule refuses a name that resolves to this computer.
            NetworkRelay strict([](const std::string&) { return true; }, [&](const std::string&) { return std::vector<RelayAddress>{{{127, 0, 0, 1}, site.port()}}; });
            CHECK(exchange(strict, "CONNECT rebind.example.com:443 HTTP/1.1\r\n\r\n").rfind("HTTP/1.1 502", 0) == 0);

            // From inside the sandbox, the relay is the one thing a command can reach.
            if (shell::sandbox_available()) {
                box.write("inside.py", "import socket\ns=socket.create_connection(('127.0.0.1', 3128), timeout=5)\ns.sendall(b'CONNECT files.example.com:443 HTTP/1.1\\r\\n\\r\\nGET /index.html HTTP/1.1\\r\\nConnection: close\\r\\n\\r\\n')\nd=b''\ntry:\n  while b'</h1>' not in d:\n    c=s.recv(4096)\n    if not c: break\n    d+=c\nexcept Exception: pass\nprint(d.decode('latin-1'))\ntry:\n  socket.create_connection(('1.1.1.1', 443), timeout=3)\n  print('DIRECT REACHED')\nexcept Exception as e:\n  print('direct refused')\n");
                const CommandResult inside = shell::run("python3 inside.py; echo proxy=$HTTPS_PROXY", workspace, true, 60, nullptr, nullptr, -1, &relay);
                CHECK(inside.output.find("<h1>upstream</h1>") != std::string::npos);  // through the relay
                CHECK(inside.output.find("direct refused") != std::string::npos && inside.output.find("DIRECT REACHED") == std::string::npos);  // and nothing else
                CHECK(inside.output.find("proxy=http://127.0.0.1:3128") != std::string::npos);
                const CommandResult offline = shell::run("python3 inside.py", workspace, true, 60);
                CHECK(offline.output.find("<h1>upstream</h1>") == std::string::npos);  // without the relay the sandbox has no network at all
            }
        }
    }

    // A policy switches tools off, and hooks gate the work.
    {
        Sandbox box;
        const Workspace workspace(box.project.string());
        box.write("mason.workflows.json", R"J({"version": 1, "workflows": {"lint": {"executable": "sh", "arguments": ["-c", "! grep -q TODO calc.py"], "events": ["afterEdit"]}, "gate": {"executable": "sh", "arguments": ["-c", "grep -q 'a + b' calc.py"], "beforeVerify": true}, "deploy": {"executable": "sh", "arguments": ["-c", "touch deployed"]}}})J");
        CHECK(workflows::load(workspace)["gate"].events == std::vector<std::string>{"beforeVerify"});
        CHECK(policies::command("", workspace, box.home).find("no policy") != std::string::npos);
        policies::command("disable run_shell start_job", workspace, box.home);
        policies::command("workflows lint gate", workspace, box.home);
        const std::string described = policies::command("hooks on", workspace, box.home);
        CHECK(described.find("tools off   run_shell, start_job") != std::string::npos && described.find("workflows   gate, lint") != std::string::npos && described.find("hooks       on") != std::string::npos);
        {
            SessionStore store(box.home);
            Session session = store.create(workspace.root(), Permission::Auto);
            ScriptedModel model;
            model.replies = {call("run_shell", {{"command", "touch made"}}), call("run_workflow", {{"name", "deploy"}}), call("write_file", {{"path", "calc.py"}, {"content", "# TODO\ndef add(a, b):\n    return a - b\n"}}), say("Done."),
                             call("write_file", {{"path", "calc.py"}, {"content", "def add(a, b):\n    return a + b\n"}}), say("Fixed.")};
            Agent agent(store, model, [](const ApprovalRequest&) { return true; }, quiet);
            agent.set_hooks_need_sandbox(false);
            agent.derives_checks = false;
            agent.run(session, "Fix add");
            CHECK(session.activities.size() >= 2 && session.activities[0].result.find("does not allow run_shell") != std::string::npos && session.activities[1].result.find("does not allow the workflow deploy") != std::string::npos);
            CHECK(!fs::exists(box.project / "made") && !fs::exists(box.project / "deployed"));
            bool after_edit = false, gate = false;
            for (const ChatMessage& message : session.messages) {
                after_edit = after_edit || (message.role == "tool" && message.content.find("The hook lint (afterEdit) failed") != std::string::npos);
                gate = gate || (message.role == "user" && message.content.find("The hook gate (beforeVerify) failed") != std::string::npos);
            }
            CHECK(after_edit);  // an afterEdit hook's failure is on the edit's result
            CHECK(gate);        // a failed gate goes back to the model
            CHECK(session.state == "Completed" && session.turns.back().answer == "Fixed.");
        }
        {
            // A gate that keeps failing blocks completion.
            SessionStore store(box.home);
            Session stuck = store.create(workspace.root(), Permission::Auto);
            ScriptedModel model;
            model.replies = {call("write_file", {{"path", "calc.py"}, {"content", "def add(a, b):\n    return a - b\n"}}), say("Done."), say("Done."), say("Done."), say("Done.")};
            Agent agent(store, model, [](const ApprovalRequest&) { return true; }, quiet);
            agent.set_hooks_need_sandbox(false);
            agent.derives_checks = false;
            agent.run(stuck, "Fix add");
            CHECK(stuck.state == "Stopped" && stuck.detail.find("hook still fails") != std::string::npos);
        }
        policies::command("clear", workspace, box.home);
        CHECK(!policies::load(box.home, workspace.root()));
        ProjectPolicy lapsed{"old", fs::weakly_canonical(box.project).string(), {"read_file"}, std::nullopt, std::time(nullptr) - 60, false};
        policies::set(lapsed, box.home, workspace.root());
        CHECK(!policies::load(box.home, workspace.root()));  // a lapsed policy is not in force
        lapsed.expires = 0;
        policies::set(lapsed, box.home, workspace.root());
        bool refused = false;
        try { policies::load(box.home, workspace.root())->check("read_file"); } catch (const Error&) { refused = true; }
        CHECK(refused);
    }

    // Scripts can review a file and serve a project.
    {
        Sandbox box;
        const std::string root = box.project.string();
        std::vector<json> events;
        const auto collect = [&](const std::string& line) { events.push_back(json::parse(line)); };
        ScriptedModel finding;
        finding.replies = {say(R"J([{"path": "calc.py", "line": 2, "evidence": "return a - b", "problem": "It subtracts."}, {"path": "calc.py", "line": 1, "evidence": "not on that line", "problem": "Made up."}])J")};
        CHECK(script::run({"review", "--workspace", root, "--prompt", "add returns the sum", "--path", "calc.py"}, collect, &finding) == 2);  // a finding is exit 2
        CHECK(events.size() == 1 && events[0]["data"]["findings"].size() == 1 && events[0]["data"]["findings"][0]["line"] == 2 && events[0]["data"]["dropped"] == 1);
        events.clear();
        ScriptedModel nothing;
        nothing.replies = {say("[]")};
        CHECK(script::run({"review", "--workspace", root, "--prompt", "x", "--path", "calc.py"}, collect, &nothing) == 0);
        box.write("mason.workflows.json", R"J({"version": 1, "workflows": {"dev": {"executable": "sh", "arguments": ["-c", "exit 3"]}, "site": {"executable": "python3", "arguments": ["-m", "http.server", "18475", "--bind", "127.0.0.1"]}}})J");
        events.clear();
        CHECK(script::run({"serve", "--workspace", root, "--workflow", "dev", "--port", "18474"}, collect) == 1);
        CHECK(events.size() == 1 && events[0]["data"]["message"].get<std::string>().find("ended before anything listened") != std::string::npos);
        if (shell::run("command -v python3", Workspace(root), false, 10).exit_code == 0) {
            box.write("index.html", "<!doctype html><title>Served Shop</title><h1>Shop</h1>");
            events.clear();
            CHECK(script::run({"serve", "--workspace", root, "--workflow", "site", "--port", "18475", "--page", "index.html"}, collect) == 0);
            CHECK(events.size() == 3 && events[0]["type"] == "server.ready" && events[1]["data"]["title"] == "Served Shop");
            CHECK(!preview::listening(18475));  // the server is stopped afterwards
        }
    }

    // A bundle brings skills and documentation to every project.
    {
        Sandbox box;
        const Workspace workspace(box.project.string());
        const fs::path source = box.base / "shop-kit", data = box.base / "data";
        auto put = [](const fs::path& file, const std::string& text) {
            fs::create_directories(file.parent_path());
            write_file_atomic(file, text);
        };
        put(source / "bundle.json", R"({"name": "shop-kit", "version": "1.2.0", "description": "How the shop team works"})");
        put(source / "skills/release/SKILL.md", "---\ndescription: How to release the shop\n---\nRun the release checklist.\n");
        put(source / "docs/payments.md", "# Payments\nCards are charged at dispatch.\n\n## Refunds\nA refund is allowed within 30 days of delivery.\n\n## Fraud\nOrders over 500 are held.\n");
        put(source / "run.sh", "echo not copied\n");
        put(source / "mcp.json", R"([{"Name": "shop-docs", "Endpoint": "http://127.0.0.1:8088/mcp", "Tools": ["search_docs"]}])");
        CHECK(bundles::command("", data).find("No bundles") != std::string::npos);
        const std::string installed = bundles::command("install " + source.string(), data);
        CHECK(installed.find("Installed shop-kit 1.2.0: 2 files") != std::string::npos && installed.find("names 1 MCP server (shop-docs)") != std::string::npos);
        CHECK(mcp::enabled(data).empty());  // installing a bundle puts no server into use
        CHECK(bundles::command("servers shop-kit", data).find("shop-docs  ·  http://127.0.0.1:8088/mcp  ·  tools: search_docs") != std::string::npos);
        CHECK(bundles::command("servers shop-kit add", data).find("Added, switched off: shop-docs") != std::string::npos);
        CHECK(mcp::all(data).size() == 1 && mcp::all(data)[0].name == "shop-docs" && mcp::enabled(data).empty());  // an added server waits to be switched on
        CHECK(bundles::command("servers shop-kit add", data).find("already in your list") != std::string::npos);
        CHECK(bundles::list(data).size() == 1 && bundles::list(data)[0].name == "shop-kit");
        CHECK(!fs::exists(data / "bundles/shop-kit/run.sh"));  // only skills and docs are copied
        const std::vector<Definition> known = skills::list(workspace, data);
        CHECK(known.size() == 1 && known[0].name == "release");
        Session session;
        session.workspace = workspace.root();
        auto offers = [&](const fs::path& home) {
            for (const ToolSpec& tool : Agent::extra_tools(session, workspace, false, false, home)) {
                if (tool.name == "read_documentation") return true;
            }
            return false;
        };
        CHECK(offers(data) && !offers(box.base / "empty"));  // with nothing to read, the tool is not offered
        const std::string found = bundles::read("when is a refund allowed", data, workspace);
        CHECK(found.rfind("[shop-kit 1.2.0/docs/payments.md:4]\n## Refunds\nA refund is allowed within 30 days", 0) == 0);
        put(box.project / ".mason/docs/notes.md", "# Local notes\nRefunds over 100 need a manager.\n");
        CHECK(bundles::read("refund manager", data, workspace).rfind("[.mason/docs/notes.md:1]", 0) == 0);  // the project's own docs are read too
        CHECK(bundles::read("kubernetes", data, workspace).find("does not mention") != std::string::npos);
        // A file that is really a link to somewhere else is not taken.
        fs::create_symlink("/etc/hosts", source / "docs/secrets.md");
        bool refused = false;
        try { bundles::install(source.string(), data); } catch (const Error&) { refused = true; }
        CHECK(refused);
        refused = false;
        try { bundles::install(box.project.string(), data); } catch (const Error&) { refused = true; }
        CHECK(refused);  // a folder without bundle.json is not a bundle
        bundles::command("remove shop-kit", data);
        CHECK(bundles::list(data).empty());
    }

    // A model carries out a task on a page, one step at a time.
    if (!browser::find().empty()) {
        Sandbox box;
        box.write("index.html", "<!doctype html><title>Shop</title><h1>Corner Shop</h1><p id=\"status\">Nothing in the cart.</p><input id=\"name\" placeholder=\"Name\"><button id=\"add\" onclick=\"document.getElementById('status').textContent = '1 tea in the cart for ' + document.getElementById('name').value + '.'\">Add tea</button>");
        const Workspace workspace(box.project.string());
        ScriptedModel model;
        model.replies = {say(R"J({"action": "click", "selector": "#missing"})J"), say(R"J({"action": "fill", "selector": "#name", "value": "Ada"})J"),
                         say("I will press it now: {\"action\": \"click\", \"selector\": \"#add\"}"), say(R"J({"action": "done", "answer": "The cart shows 1 tea for Ada."})J")};
        const BrowseOutcome outcome = browse::run(workspace, "index.html", "Add a tea for Ada and say what the cart shows.", model);
        CHECK(outcome.completed && outcome.answer == "The cart shows 1 tea for Ada.");
        CHECK(outcome.steps.size() == 2);  // the step that did not work was taken back
        CHECK(model.seen.size() == 4);
        if (model.seen.size() == 4) {
            CHECK(model.seen[0].back().content.find("Nothing in the cart.") != std::string::npos && model.seen[0].back().content.find("#add") != std::string::npos);  // the model is shown the page and its controls
            CHECK(model.seen[1].back().content.find("did not work") != std::string::npos);  // a failed step is reported back
            CHECK(model.seen[3].back().content.find("1 tea in the cart for Ada.") != std::string::npos);  // the model sees what its steps really led to
        }
        // An independent check reads the page afresh, with a picture, and an answer it does not support is not counted as done.
        ScriptedModel claims, judge;
        claims.replies = {say(R"J({"action": "done", "answer": "The cart holds three teas."})J")};
        judge.replies = {say(R"J({"supported": false, "reason": "The page says nothing is in the cart."})J")};
        const BrowseOutcome checked = browse::run(workspace, "index.html", "Say what is in the cart.", claims, 12, nullptr, &judge, true);
        CHECK(!checked.completed && checked.reviewed && !checked.supported && checked.review == "The page says nothing is in the cart.");
        CHECK(judge.seen.size() == 1 && judge.seen[0].back().content.find("Nothing in the cart.") != std::string::npos);  // the checker is shown the page as it is
        CHECK(judge.seen.size() == 1 && judge.seen[0].back().images.size() == 1 && judge.seen[0].back().images[0].rfind("data:image/png;base64,iVBOR", 0) == 0);  // and a picture of it
        std::vector<json> events;
        ScriptedModel refusing;
        refusing.replies = {say(R"J({"action": "fail", "answer": "There is no checkout on this page."})J")};
        CHECK(script::run({"browse", "--workspace", box.project.string(), "--prompt", "Is there a checkout?"}, [&](const std::string& line) { events.push_back(json::parse(line)); }, &refusing) == 2);
        CHECK(events.size() == 1 && events[0]["data"]["answer"] == "There is no checkout on this page.");
    }

    // Chat attaches pictures and the text of documents.
    {
        Sandbox box;
        const std::string png("\x89PNG\r\n\x1a\n\0\0\0\0", 12);
        write_file_atomic(box.project / "shot.png", png);
        const ChatAttachment picture = ChatAttachment::from_file(box.project / "shot.png");
        CHECK(picture.image == "data:image/png;base64,iVBORw0KGgoAAAAA");
        CHECK(ChatAttachment::word_text(R"(<w:document><w:body><w:p><w:r><w:t>Terms &amp; conditions</w:t></w:r></w:p><w:p><w:r><w:t xml:space="preserve">Net </w:t><w:tab/><w:t>30 days</w:t></w:r></w:p></w:body></w:document>)") == "Terms & conditions\nNet \t30 days\n");
        write_file_atomic(box.project / "blob.bin", std::string("\0\1\2\3", 4));
        bool refused = false;
        try { ChatAttachment::from_file(box.project / "blob.bin"); } catch (const Error&) { refused = true; }
        CHECK(refused);  // a file that is neither text, a picture nor a document is refused
        ChatStore chats(box.home);
        ChatConversation conversation = chats.create();
        ScriptedModel model;
        model.replies = {say("A screenshot.")};
        chat::send(conversation, "What is this?", {picture, ChatAttachment{"notes.txt", "hello", ""}}, model, chats, [](const std::string&) {}, nullptr);
        const ChatMessage* sent = nullptr;
        for (const ChatMessage& message : conversation.messages) {
            if (message.role == "user") sent = &message;
        }
        CHECK(sent && sent->images.size() == 1 && sent->content.find("Attached picture: shot.png") != std::string::npos && sent->content.find("Attached file notes.txt") != std::string::npos);
        CHECK(!model.seen.empty() && model.seen[0].back().images.size() == 1);  // the picture goes to the model with the message
        ChatStore again(box.home);
        bool kept = false;
        for (const ChatConversation& saved : again.conversations()) {
            for (const ChatMessage& message : saved.messages) kept = kept || message.images.size() == 1;
        }
        CHECK(kept);  // and is kept with the conversation
    }

    // A table in an answer becomes a grid and a chart.
    {
        const std::string answer = "Here are the sales.\n\n| Month | Units | Revenue | Note |\n|:--|--:|--:|---|\n| Jan | 1,200 | $3,400.50 | slow |\n| Feb | 950 | $2,100 | |\n| Mar | 1,410 | $4,020 | best |\n\nAnd in code:\n```\n| a | b |\n|---|---|\n| 1 | 2 |\n```\n";
        const std::vector<TableData> found = tables::find(answer);
        CHECK(found.size() == 1);  // a table inside a code block is left alone
        if (found.size() == 1) {
            CHECK(found[0].headers == (std::vector<std::string>{"Month", "Units", "Revenue", "Note"}) && found[0].rows.size() == 3);
            CHECK(found[0].series.size() == 2 && found[0].series[0].name == "Units" && found[0].series[1].name == "Revenue");  // only columns that are all numbers are charted
            if (found[0].series.size() == 2) CHECK(found[0].series[0].values == (std::vector<double>{1200, 950, 1410}) && found[0].series[1].values == (std::vector<double>{3400.5, 2100, 4020}));
        }
        CHECK(tables::number("-12.5%") == std::optional<double>(-12.5) && !tables::number("12 apples"));
        CHECK(tables::find("| just | one row |\nno rule").empty());
        const std::string prose = tables::prose(answer);
        CHECK(prose.find("Here are the sales.") != std::string::npos && prose.find("| Jan |") == std::string::npos && prose.find("| a | b |") != std::string::npos);  // the table's own lines are left out, and code is kept
    }

    // A decision can be compared later, and the reviewer takes part in a request.
    {
        Sandbox box;
        const fs::path trace = box.home / "traces";
        ScriptedModel first;
        first.replies = {call("read_file", {{"path", "calc.py"}})};
        RoleModel recorded(first, "implementer", 0, trace);
        recorded.complete({ChatMessage{"user", "Look at calc.py", {}, ""}}, Agent::tools(), [](const std::string&) {}, nullptr);
        fs::path file;
        for (const auto& entry : fs::directory_iterator(trace)) file = entry.path();
        ScriptedModel again;
        again.replies = {call("read_file", {{"path", "calc.py"}})};
        CHECK(roles::compare(file, again)["same"] == true);
        ScriptedModel other;
        other.replies = {call("list_files", {{"path", "."}})};
        const json different = roles::compare(file, other);
        CHECK(different["same"] == false && different["sameTools"] == false);
        std::vector<json> events;
        ScriptedModel third;
        third.replies = {call("read_file", {{"path", "calc.py"}})};
        CHECK(script::run({"compare-decision", "--trace", file.string()}, [&](const std::string& line) { events.push_back(json::parse(line)); }, &third) == 0);
        CHECK(events.size() == 1 && events[0]["data"]["same"] == true);
    }

    // A chosen reviewer reads the changes before a request finishes.
    {
        Sandbox box;
        SessionStore store(box.home);
        Session session = store.create(Workspace(box.project.string()).root(), Permission::Auto);
        ScriptedModel worker, judge;
        worker.replies = {call("write_file", {{"path", "calc.py"}, {"content", "def add(a, b):\n    return a * b\n"}}), say("Done."), call("write_file", {{"path", "calc.py"}, {"content", "def add(a, b):\n    return a + b\n"}}), say("Fixed after review.")};
        judge.replies = {say(R"J([{"path": "calc.py", "line": 2, "evidence": "return a * b", "problem": "It multiplies instead of adding."}])J")};
        Agent agent(store, worker, [](const ApprovalRequest&) { return true; }, quiet);
        agent.derives_checks = false;
        agent.set_reviewer(&judge);
        agent.run(session, "Make add return the sum");
        CHECK(session.state == "Completed" && session.turns.back().answer == "Fixed after review.");
        CHECK(judge.seen.size() == 1);  // the reviewer reads the changes once, not after every repair
        bool told = false;
        for (const ChatMessage& message : session.messages) told = told || (message.role == "user" && message.content.find("A separate reviewer read your changes") != std::string::npos && message.content.find("calc.py:2") != std::string::npos);
        CHECK(told);
        CHECK(box.read("calc.py") == "def add(a, b):\n    return a + b\n");
    }

    // Probes go beyond a pinned test, and an afterPlan hook runs first.
    {
        Sandbox box;
        box.write("mason.workflows.json", R"J({"version": 1, "workflows": {"test": {"executable": "sh", "arguments": ["-c", "echo 'ok 1 - joins'"]}, "note": {"executable": "sh", "arguments": ["-c", "echo planned >> plan.log; exit 1"], "events": ["afterPlan"]}}})J");
        box.write("join.cjs", "exports.join = function (a, b) { return a + '-' + b; };\n");
        box.write("join.test.cjs", "const { join } = require('./join.cjs');\nconsole.log(join('x', \"y\") === 'x-y' ? 'ok 1 - joins' : 'not ok 1 - joins');\n");
        fs::create_directories(box.project / ".mason");
        box.write(".mason/tests.json", R"J([{"requirement": "Join two words", "workflow": "test", "format": "tap", "test": "joins", "testFiles": ["join.test.cjs"], "inputs": ["join.cjs"], "argumentOrder": "interchangeable", "roundTrip": {"module": "join.cjs", "encode": "join", "decode": "join", "alphabet": "ab"}}])J");
        const Workspace workspace(box.project.string());
        const std::vector<ProjectTest> listed = project_tests::load(workspace);
        CHECK(listed.size() == 1);
        if (listed.size() == 1) CHECK(project_tests::example(listed[0], workspace) == (std::vector<std::string>{"join.cjs", "join", "\"x\"", "\"y\""}));  // the example is taken from the test's own call
        const DerivedPlan plan = derived::prepare("Join two words", workspace, nullptr);
        CHECK(plan.checks.size() == 1);
        if (plan.checks.size() == 1) {
            const DerivedResult result = derived::verify(plan, workspace)[0];
            if (shell::run("command -v node", workspace, false, 10).exit_code == 0) {
                CHECK(result.state == "failed" && result.detail.find("argument order: join(\"x\", \"y\") gives \"x-y\" but exchanged it gives \"y-x\"") != std::string::npos && result.detail.find("round trip:") != std::string::npos);
            } else {
                CHECK(result.detail.find("not run") != std::string::npos);
            }
        }
        // afterPlan runs once, before the model is asked, and its failure is told to the model.
        policies::command("hooks on", workspace, box.home);
        SessionStore store(box.home);
        Session session = store.create(workspace.root(), Permission::Auto);
        ScriptedModel model;
        model.replies = {say("Nothing to do.")};
        Agent agent(store, model, [](const ApprovalRequest&) { return true; }, quiet);
        agent.set_hooks_need_sandbox(false);
        agent.derives_checks = false;
        agent.run(session, "Look around");
        CHECK(box.read("plan.log") == "planned\n");
        CHECK(!model.seen.empty() && model.seen[0].back().content.find("The project's afterPlan hook failed before work began") != std::string::npos);
    }

    // Network is chosen per session, and a host can be always allowed.
    {
        Sandbox box;
        SessionStore store(box.home);
        Session session = store.create(Workspace(box.project.string()).root(), Permission::Auto);
        CHECK(!network_settings::asks(session, box.home));
        CHECK(network_settings::session_command("ask", session, box.home).find("this session's own choice") != std::string::npos);
        CHECK(network_settings::asks(session, box.home) && !network_settings::asks(box.home));  // one session asking does not change the computer's setting
        network_settings::session_command("computer ask", session, box.home);
        network_settings::session_command("off", session, box.home);
        CHECK(!network_settings::asks(session, box.home));  // a session can stay offline while the computer's setting asks
        network_settings::session_command("default", session, box.home);
        CHECK(network_settings::asks(session, box.home));
        store.save(session);
        ApprovalRules rules(box.home, session.workspace);
        rules.add("network", "registry.npmjs.org");
        CHECK(network_settings::always_allowed(box.home, session.workspace) == std::set<std::string>{"registry.npmjs.org"});
        std::vector<std::string> asked;
        ScriptedModel model;
        Agent agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request.subject); return false; }, quiet);
        NetworkRelay* relay = agent.relay_for(session);
        CHECK(relay != nullptr);
        if (relay && shell::run("command -v python3", Workspace(box.project.string()), false, 10).exit_code == 0) {
            box.write("ask.py", "import socket,sys\ns=socket.socket(socket.AF_UNIX)\ns.settimeout(20)\ns.connect(sys.argv[1])\ns.sendall(('CONNECT '+sys.argv[2]+':443 HTTP/1.1\\r\\n\\r\\n').encode())\ntry:\n  print(s.recv(64).decode('latin-1'))\nexcept Exception:\n  print('no answer')\n");
            const Workspace workspace(box.project.string());
            const std::string longer = shell::run("python3 ask.py " + shell::quote(relay->socket_path().string()) + " registry.npmjs.org.evil.example", workspace, false, 40).output;
            CHECK(longer.rfind("HTTP/1.1 403", 0) == 0);  // a longer name that starts the same is not the allowed host
            CHECK(asked == std::vector<std::string>{"registry.npmjs.org.evil.example"});
            shell::run("python3 ask.py " + shell::quote(relay->socket_path().string()) + " registry.npmjs.org", workspace, false, 40);
            CHECK(asked == std::vector<std::string>{"registry.npmjs.org.evil.example"});  // the always-allowed host was not asked about
        }
        CHECK(json::parse(preview::kLoopbackOnly, nullptr, false).is_array());
    }

    // A chat carries its reasoning level, output limit and pinned reference.
    {
        Sandbox box;
        ChatStore chats(box.home);
        ChatConversation conversation = chats.create();
        CHECK(chat::extra_body(conversation).empty());  // nothing is added unless the user asked for it
        chat::configure(conversation, "low", 900, "Refunds are allowed within 30 days.");
        const json body = chat::extra_body(conversation);
        CHECK(body["max_tokens"] == 900 && body["thinking_budget_tokens"] == 512 && body["chat_template_kwargs"]["enable_thinking"] == true);
        ScriptedModel model;
        model.replies = {say("Within 30 days."), say("Still 30 days.")};
        chat::send(conversation, "When can I get a refund?", {}, model, chats, [](const std::string&) {}, nullptr);
        CHECK(model.seen[0].front().content.find("Refunds are allowed within 30 days.") != std::string::npos);  // the reference goes with the message
        chat::configure(conversation, "off", 0, "");
        CHECK(chat::extra_body(conversation)["reasoning_effort"] == "none" && !chat::extra_body(conversation).contains("max_tokens"));
        chat::send(conversation, "And now?", {}, model, chats, [](const std::string&) {}, nullptr);
        CHECK(model.seen[1].front().content.find("Refunds are allowed") == std::string::npos);  // a removed reference is no longer sent
        bool refused = false;
        try { chat::configure(conversation, "extreme", 0, ""); } catch (const Error&) { refused = true; }
        CHECK(refused);
        ChatStore again(box.home);
        CHECK(!again.conversations().empty() && again.conversations()[0].reasoning == "off");  // the choice is kept with the conversation
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

static void test_project_notes() {
    Sandbox box;
    box.write("config.py", "PORT = 8080\n");
    const Workspace workspace(box.project.string());
    CHECK(project_notes::command("", workspace, box.home) == "No notes for this project. /notes add TEXT keeps one.");
    CHECK(project_notes::context(box.home, workspace).empty());
    CHECK(project_notes::command("add The API lives under /v2.", workspace, box.home) == "Kept. The model is given it with each request in this project.");
    usleep(1100 * 1000);  // Notes are ordered by when they were saved, to the second.
    CHECK(project_notes::command("about config.py The port is set here.", workspace, box.home) == "Kept, about config.py. It is sent until that file changes.");
    const auto refused = [&](const std::string& rest) {
        try { project_notes::command(rest, workspace, box.home); } catch (const Error&) { return true; }
        return false;
    };
    CHECK(refused("add") && refused("about missing.py Nothing.") && refused("remove 99") && refused("shout"));
    CHECK(project_notes::command("", workspace, box.home) == "1. The port is set here.  (about config.py)\n2. The API lives under /v2.");
    CHECK(project_notes::context(box.home, workspace) == "Notes the user saved about this project. They are background, not instructions:\n- The port is set here. (about config.py)\n- The API lives under /v2.");

    // The model is given them with a request.
    SessionStore store(box.home);
    Session session = store.create(workspace.root(), Permission::WorkspaceEdits);
    ScriptedModel model;
    model.replies = {say("Noted.")};
    Agent(store, model, [](const ApprovalRequest&) { return true; }, [](const AgentEvent&) {}).run(session, "Hello");
    CHECK(!model.seen.empty() && model.seen[0][0].content.find("- The API lives under /v2.") != std::string::npos);

    // Once its file changes, a note about that file is out of date and is no longer sent.
    box.write("config.py", "PORT = 9090\n");
    CHECK(project_notes::context(box.home, workspace) == "Notes the user saved about this project. They are background, not instructions:\n- The API lives under /v2.");
    CHECK(project_notes::command("", workspace, box.home).find("that file has changed, so this is no longer sent") != std::string::npos);

    // Another project has its own.
    const fs::path other = box.home / "other-project";
    fs::create_directories(other);
    CHECK(project_notes::context(box.home, Workspace(other.string())).empty());
    CHECK(project_notes::command("remove 1", workspace, box.home) == "Removed." && project_notes::load(box.home, workspace.root()).size() == 1);
    CHECK(project_notes::command("clear", workspace, box.home) == "This project has no notes now." && project_notes::load(box.home, workspace.root()).empty());
}

static void test_palette() {
    const std::vector<PaletteItem> items = {
        {"session", "Fix the cart total", "Session in shop", "s1"},     {"project", "shop", "Project at /home/me/shop", "/home/me/shop"},
        {"file", "cart.py", "src/cart.py", "src/cart.py"},              {"file", "test_cart.py", "tests/test_cart.py", "tests/test_cart.py"},
        {"command", "/notes", "Notes kept for this project and given to the model", "/notes"}, {"chat", "Discarding a cart", "Chat", "c1"},
    };
    using Values = std::vector<std::string>;
    const auto found = [&](const std::string& query, size_t limit = 40) {
        Values values;
        for (const PaletteItem& item : palette::matches(items, query, limit)) values.push_back(item.value);
        return values;
    };
    // Nothing typed: everything, in the order given, up to the limit.
    CHECK(found("").size() == 6 && found("")[0] == "s1" && found("  ", 2).size() == 2);
    // A title that starts with what was typed comes before one where it starts a word, then one that only contains it.
    CHECK(found("CART") == (Values{"src/cart.py", "s1", "tests/test_cart.py", "c1"}));
    // Every word must appear, in the title or the detail.
    CHECK(found("cart tests") == (Values{"tests/test_cart.py"}));
    CHECK(found("model") == (Values{"/notes"}) && found("/no") == (Values{"/notes"}) && found("zebra").empty());
    bool has_languages = false;
    for (const auto& [name, detail] : palette::commands()) has_languages = has_languages || name == "/languages";
    CHECK(has_languages);
}

static void test_gpu_reading() {
    // What the NVIDIA driver's tool prints, in mebibytes.
    const std::vector<GpuAdapter> cards = gpu::parse_nvidia("NVIDIA GeForce RTX 4090, 37, 24564, 8123\nNVIDIA RTX A2000, [N/A], 6138, 12\nnot a card\n");
    CHECK(cards.size() == 2 && cards[0].name == "NVIDIA GeForce RTX 4090" && cards[0].busy_percent == 37 && cards[0].total_bytes == 24564LL * 1048576 && cards[0].used_bytes == 8123LL * 1048576);
    CHECK(cards.size() == 2 && cards[1].busy_percent == -1);
    CHECK(gpu::describe(cards[0]) == "37% busy · " + byte_text(8123LL * 1048576) + " of " + byte_text(24564LL * 1048576) + " in use");
    CHECK(gpu::describe(cards[1]).rfind("use not reported", 0) == 0);
    CHECK(gpu::parse_nvidia("").empty());
    // What the kernel publishes for AMD and Intel processors; outputs and other vendors are passed over.
    Sandbox box;
    const fs::path drm = box.home / "drm";
    const auto put = [&](const std::string& file, const std::string& text) {
        fs::create_directories((drm / file).parent_path());
        write_file_atomic(drm / file, text);
    };
    put("card0/device/vendor", "0x1002\n");
    put("card0/device/gpu_busy_percent", "12\n");
    put("card0/device/mem_info_vram_total", "8589934592\n");
    put("card0/device/mem_info_vram_used", "1073741824\n");
    put("card0-HDMI-A-1/status", "connected\n");
    put("card1/device/vendor", "0x8086\n");
    put("card2/device/vendor", "0x10de\n");
    const std::vector<GpuAdapter> found = gpu::read_drm(drm);
    CHECK(found.size() == 2 && found[0].name == "AMD graphics (card0)" && found[0].busy_percent == 12 && found[0].total_bytes == 8589934592LL && found[0].used_bytes == 1073741824LL);
    CHECK(found.size() == 2 && found[1].name == "Intel graphics (card1)" && found[1].busy_percent == -1 && found[1].total_bytes == 0);
    CHECK(found.size() == 2 && gpu::describe(found[1]) == "use not reported · shares the computer's memory");
    CHECK(gpu::read_drm(box.home / "nothing-here").empty());
}

static void test_generation_settings() {
    Sandbox box;
    const ModelEndpoint coder{"http://127.0.0.1:8080/", "coder", ""}, other{"http://127.0.0.1:8080/", "other", ""};
    const auto refused = [&](const std::string& rest, const std::optional<ModelEndpoint>& endpoint) {
        try { generation::command(rest, endpoint, box.home); } catch (const Error&) { return true; }
        return false;
    };
    CHECK(refused("", std::nullopt));
    CHECK(generation::command("", coder, box.home).find("temperature  ·  the server's default") != std::string::npos);
    CHECK(generation::command("temperature 0.2", coder, box.home) == "Temperature is 0.2 for coder.");
    CHECK(generation::command("top-k 40", coder, box.home) == "Top K is 40 for coder.");
    CHECK(generation::command("seed 7", other, box.home) == "Seed is 7 for other.");
    // Out of range, not a number, a fraction where a whole number is meant, and an unknown name are refused.
    for (const char* wrong : {"temperature 3", "top_p lots", "top_k 1.5", "warmth 1", "temperature"}) CHECK(refused(wrong, coder));
    const std::string shown = generation::command("", coder, box.home);
    CHECK(shown.find("temperature  ·  0.2") != std::string::npos && shown.find("top_k  ·  40") != std::string::npos && shown.find("seed  ·  the server's default") != std::string::npos);
    // What goes in a request: only what was set, for this connection, with whole numbers as whole numbers.
    const json body = generation::body(coder, box.home);
    CHECK(body.size() == 2 && body["temperature"] == 0.2 && body["top_k"].is_number_integer() && body["top_k"] == 40);
    CHECK(generation::body(other, box.home) == json({{"seed", 7}}));
    CHECK(generation::command("top_k off", coder, box.home) == "Top K is left to the server's default for coder.");
    CHECK(!generation::body(coder, box.home).contains("top_k"));
    CHECK(generation::command("clear", coder, box.home) == "Generation settings for coder are left to the server's defaults.");
    CHECK(generation::body(coder, box.home).empty() && generation::values(other, box.home).size() == 1);
}

static void test_tool_groups() {
    Sandbox box;
    SessionStore store(box.home);
    Session session = store.create(Workspace(box.project.string()).root(), Permission::Auto);
    ScriptedModel model;
    model.replies = {call("enable_tools", {{"group", "jobs"}}), call("git_status", {}), call("enable_tools", {{"group", "klingon"}}), say("Done.")};
    Agent(store, model, [](const ApprovalRequest&) { return true; }, [](const AgentEvent&) {}).run(session, "Look around.");
    const auto has = [&](size_t step, const char* name) { return step < model.seen_tools.size() && std::find(model.seen_tools[step].begin(), model.seen_tools[step].end(), name) != model.seen_tools[step].end(); };
    CHECK(model.seen_tools.size() == 4);
    // At first the model is sent the everyday tools and enable_tools, and none of a group's tools.
    CHECK(has(0, "read_file") && has(0, "edit_file") && has(0, "enable_tools") && !has(0, "start_job") && !has(0, "git_status") && !has(0, "repository_map"));
    // A loaded group's tools are sent from the next step on, and the others still wait.
    CHECK(has(1, "start_job") && has(1, "terminal_input") && !has(1, "git_status") && has(1, "enable_tools"));
    // A group's tool called before its group was loaded loads the group rather than being turned away.
    CHECK(has(2, "git_status") && has(2, "repository_map"));
    CHECK(session.activities.size() == 3 && session.activities[0].state == "completed" && session.activities[0].result.find("Loaded the jobs tools: start_job") != std::string::npos);
    CHECK(session.activities.size() == 3 && session.activities[1].state == "completed" && session.activities[2].state == "failed" && session.activities[2].result.find("code, docs, workflows, jobs, mcp") != std::string::npos);
    CHECK(!session.messages.empty() && session.messages[0].content.find("More tools load on request. Call enable_tools with one of: code for") != std::string::npos);
    CHECK(tool_groups::group_of("start_job") == "jobs" && tool_groups::group_of("read_file").empty());
}

static void test_language_servers() {
    Sandbox box;
    fs::remove(box.project / "calc.py");
    box.write("sum.c", "int total(int a, int b) {\n    return a + b;\n}\nint main() { return total(1, count); }\n");
    const Workspace workspace(box.project.string());
    using Names = std::vector<std::string>;

    // What a server writes is read message by message, and one still arriving is left for later.
    const std::string one = language_servers::frame({{"id", 1}, {"result", "a"}}), two = one + language_servers::frame({{"method", "note"}});
    CHECK(language_servers::frames(two.substr(0, two.size() - 5)).first.size() == 1 && language_servers::frames(two.substr(0, two.size() - 5)).second == one.size());
    CHECK(language_servers::frames(two).first.size() == 2 && language_servers::frames(two).second == two.size());
    CHECK(!language_servers::server_for("txt", box.home));
    CHECK(language_servers::kind_of("deploy/Dockerfile") == "dockerfile" && language_servers::kind_of("src/App.SCSS") == "scss");
    CHECK(language_servers::language_for("scss") && language_servers::language_for("scss")->key == "css");
    // Every extension belongs to one language.
    std::set<std::string> seen_kinds, seen_keys;
    for (const Language& language : language_servers::known()) {
        CHECK(seen_keys.insert(language.key).second && !language.candidates.empty());
        for (const std::string& kind : language.extensions) CHECK(seen_kinds.insert(kind).second);
    }

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
            CHECK(language_servers::query(workspace, *server, "sum.c", 1, 1, "diagnostics", false) == server->name + ": 2 diagnostics for sum.c.\nsum.c:3:12  error  use of undeclared identifier 'count'\nsum.c:1:1  warning  unused function");

            // Through the agent it asks first, like a command, and a read-only session may not start it.
            SessionStore store(box.home);
            Session session = store.create(workspace.root(), Permission::ReviewEdits);
            const ModelReply ask = call("inspect_symbol", {{"path", "sum.c"}, {"line", 4}, {"column", 21}, {"mode", "definitions"}});
            ScriptedModel model;
            // The server stays running for the request, so a second question in it does not ask again. The next request does.
            model.replies = {ask, ask, say("Found."), ask, say("No.")};
            Names asked;
            std::deque<bool> answers = {true, false};
            Agent agent(store, model, [&](const ApprovalRequest& request) { asked.push_back(request.subject); const bool answer = answers.front(); answers.pop_front(); return answer; }, [](const AgentEvent&) {});
            agent.run(session, "Where is total defined?");
            CHECK(asked == (Names{"language server " + server->name}));
            CHECK(session.activities.size() == 2 && session.activities[0].state == "completed" && session.activities[1].state == "completed" && session.activities[0].result.find("sum.c:1:5") != std::string::npos);
            agent.run(session, "And again?");
            CHECK(asked.size() == 2 && session.activities.back().state == "denied");
            session.permission = Permission::ReadOnly;
            session.ask_only = true;
            ScriptedModel again;
            again.replies = {ask, say("No.")};
            Agent(store, again, [](const ApprovalRequest&) { return false; }, [](const AgentEvent&) {}).run(session, "Again");
            CHECK(session.activities.back().state == "failed" && session.activities.back().result.find("read-only") != std::string::npos);

            // The errors a server finds in an edit go back with the edit's result; a warning does not. Outside Auto
            // the server is not sandboxed, so starting it is asked about once for the request.
            box.write("sum.c", "int total(int a, int b) {\n    return a + b;\n}\n");
            Session editing = store.create(workspace.root(), Permission::WorkspaceEdits);
            ScriptedModel edits;
            edits.replies = {call("edit_file", {{"path", "sum.c"}, {"old_text", "return a + b;"}, {"new_text", "return a + count;"}}),
                             call("edit_file", {{"path", "sum.c"}, {"old_text", "return a + count;"}, {"new_text", "return a + b + 1;"}}), say("Done.")};
            Names titles;
            Agent(store, edits, [&](const ApprovalRequest& request) { titles.push_back(request.title); return true; }, [](const AgentEvent&) {}).run(editing, "Add one.");
            CHECK(titles == (Names{"Start " + server->name + " to check this request's edits"}));
            CHECK(editing.activities.size() == 2);
            if (editing.activities.size() == 2) {
                const std::string first = editing.activities[0].result;
                CHECK(starts_with(first, "Edited sum.c (+1 −1).") && first.find("reports 1 error in sum.c after this edit") != std::string::npos);
                CHECK(first.find("sum.c:3:12  error  use of undeclared identifier 'count'") != std::string::npos && first.find("unused function") == std::string::npos);
                CHECK(editing.activities[1].result == "Edited sum.c (+1 −1).");
                // The model is given the same text the activity shows.
                bool told = false;
                for (const auto& message : editing.messages) told = told || (message.role == "tool" && message.content == first);
                CHECK(told);
                if (failures) std::cerr << first << "\n---\n" << editing.activities[1].result << "\n";
            }

            // A declined server is not started and not asked about again in the request.
            box.write("sum.c", "int total(int a, int b) {\n    return a + b;\n}\n");
            Session declined = store.create(workspace.root(), Permission::WorkspaceEdits);
            ScriptedModel twice;
            twice.replies = {call("edit_file", {{"path", "sum.c"}, {"old_text", "return a + b;"}, {"new_text", "return a + count;"}}),
                             call("edit_file", {{"path", "sum.c"}, {"old_text", "return a + count;"}, {"new_text", "return count;"}}), say("Done.")};
            int questions = 0;
            Agent(store, twice, [&](const ApprovalRequest&) { questions++; return false; }, [](const AgentEvent&) {}).run(declined, "Break it.");
            CHECK(questions == 1 && declined.activities.size() == 2 && declined.activities[0].result == "Edited sum.c (+1 −1).");

            // A server that only answers when asked is asked for the file's diagnostics.
            setenv("MASON_LANGUAGE_SERVERS", json{{"c", {python, fake.string(), "--pull"}}}.dump().c_str(), 1);
            const std::optional<LanguageServer> pulling = language_servers::server_for("c");
            CHECK(pulling.has_value());
            if (pulling) {
                LanguageSessions sessions;
                const std::string found = language_servers::errors(workspace, *pulling, "sum.c", "int total() {\n\n    return count;\n}\n", false, sessions);
                CHECK(found.find("sum.c:3:12  error  use of undeclared identifier 'count'") != std::string::npos);
                CHECK(language_servers::errors(workspace, *pulling, "sum.c", "int total() { return 1; }\n", false, sessions).empty());
                // In Auto the server runs inside the sandbox, and is talked to the same way.
                if (shell::sandbox_available()) {
                    LanguageSessions boxed;
                    CHECK(language_servers::errors(workspace, *pulling, "sum.c", "int total() {\n\n    return count;\n}\n", true, boxed).find("use of undeclared identifier 'count'") != std::string::npos);
                } else {
                    std::cout << "  (no sandbox on this computer: a sandboxed language server was not run)\n";
                }
            }
        }
        unsetenv("MASON_LANGUAGE_SERVERS");

        // A server the user names in their own file is used for that extension, known to Mason or not.
        fs::create_directories(box.home);
        write_file_atomic(box.home / "language-servers.json", json{{"klingon", {python, fake.string()}}}.dump());
        const std::optional<LanguageServer> own = language_servers::server_for("klingon", box.home);
        CHECK(own && own->language_id == "klingon");
        CHECK(language_servers::command("", &workspace, box.home).find(".klingon files") != std::string::npos);
        fs::remove(box.home / "language-servers.json");
    }

    // /languages lists the project's languages and says how to get a missing server.
    box.write("Main.hs", "module Main where\n");
    box.write("Dockerfile", "FROM scratch\n");
    const std::string listed = language_servers::command("", &workspace, box.home);
    CHECK(listed.find("Haskell [haskell]") != std::string::npos && listed.find("Dockerfiles [docker]") != std::string::npos && listed.find("Zig") == std::string::npos);
    CHECK(language_servers::command("all", &workspace, box.home).find("Zig [zig]") != std::string::npos);
    bool refused = false;
    try { language_servers::command("install haskell", &workspace, box.home); } catch (const Error& error) { refused = std::string(error.what()).find("ghcup") != std::string::npos; }
    CHECK(refused);
    refused = false;
    try { language_servers::command("install klingon", &workspace, box.home); } catch (const Error& error) { refused = std::string(error.what()).find("/languages all") != std::string::npos; }
    CHECK(refused);

    // A language with no server installed is said once in a request, with the edit's result.
    if (!language_servers::server_for("hs", box.home)) {
        SessionStore store(box.home);
        Session session = store.create(workspace.root(), Permission::WorkspaceEdits);
        ScriptedModel model;
        model.replies = {call("write_file", {{"path", "Main.hs"}, {"content", "module Main where\nmain = pure ()\n"}}), call("write_file", {{"path", "Other.hs"}, {"content", "module Other where\n"}}), say("Done.")};
        Agent(store, model, [](const ApprovalRequest&) { return false; }, [](const AgentEvent&) {}).run(session, "Write them.");
        CHECK(session.activities.size() == 2 && session.activities[0].result.find("No language server for Haskell is installed") != std::string::npos);
        CHECK(session.activities.size() == 2 && session.activities[1].result.find("No language server") == std::string::npos);
    }
    fs::remove(box.project / "Main.hs");
    fs::remove(box.project / "Other.hs");
    fs::remove(box.project / "Dockerfile");

    // A real clangd, where there is one: the definition of total from its use, and the undeclared name.
    const std::optional<LanguageServer> clangd = language_servers::server_for("c", box.home);
    if (!clangd || clangd->name != "clangd") {
        std::cout << "  (no clangd on this computer: a real language server was not run)\n";
        return;
    }
    // One running clangd answers all three: an edit's errors, a definition, and the file's diagnostics.
    LanguageSessions real;
    const std::string source = "int total(int a, int b) {\n    return a + b;\n}\nint main() { return total(1, count); }\n";
    box.write("sum.c", source);
    const std::string found = language_servers::errors(workspace, *clangd, "sum.c", source, false, real);
    CHECK(found.find("sum.c:4:30  error") != std::string::npos);
    const std::string definition = language_servers::query(workspace, *clangd, "sum.c", 4, 21, "definitions", false, real);
    CHECK(definition.find("sum.c:1:5  int total(int a, int b) {") != std::string::npos);
    const std::string diagnostics = language_servers::query(workspace, *clangd, "sum.c", 1, 1, "diagnostics", false, real);
    CHECK(diagnostics.find("sum.c:4:") != std::string::npos && diagnostics.find("count") != std::string::npos);
    const std::string mended = replace_all(source, "count", "2");
    box.write("sum.c", mended);
    CHECK(language_servers::errors(workspace, *clangd, "sum.c", mended, false, real).empty());
    if (failures) std::cerr << found << "\n" << definition << "\n" << diagnostics << "\n";
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

int main(int argc, char** argv) {
    // Inside the sandbox this program is started as the forwarder to Mason's network relay.
    if (const auto forwarded = mason::network::maybe_forward(argc, argv)) return *forwarded;
    const std::pair<const char*, void (*)()> tests[] = {
        {"workspace", test_workspace}, {"diff", test_diff}, {"stream and endpoint", test_stream_and_endpoint}, {"tool call recovery", test_recovery},
        {"agent edit and undo", test_agent_edit_undo}, {"agent refusals", test_agent_refusals}, {"commands and rules", test_commands_and_rules},
        {"undo of command changes", test_undo_of_command_changes}, {"custom agents and commands", test_custom_agents_and_commands}, {"compaction", test_compaction}, {"MCP servers", test_mcp}, {"chat", test_chat}, {"queue and steer", test_queue_and_steer}, {"schedules", test_schedules}, {"extra tools", test_extra_tools}, {"verification", test_verification}, {"browser checks", test_browser}, {"comments, milestones and search by meaning", test_planning}, {"Transformers models", test_transformers}, {"attached windows", test_desktop}, {"a window by sight", test_desktop_vision}, {"acceptance scenarios and review", test_acceptance}, {"fork, export and import", test_transfer}, {"code navigation", test_navigation}, {"an application under test", test_verification_app}, {"checks derived from a request", test_derived}, {"the catalog component", test_catalog}, {"project notes", test_project_notes}, {"palette", test_palette}, {"graphics reading", test_gpu_reading}, {"generation settings", test_generation_settings}, {"tool groups", test_tool_groups}, {"language servers", test_language_servers},
        {"shell", test_shell}, {"sandbox", test_sandbox}, {"sha-256 and models", test_sha_and_models}, {"model memory and fit", test_model_memory},
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
