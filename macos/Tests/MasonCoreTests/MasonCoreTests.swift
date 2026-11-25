import XCTest
@testable import MasonCore

/// Replays fixed replies, like a model that always makes the same decisions.
final class ScriptedModel: ChatModel {
    let name = "scripted"
    var replies: [ModelReply]
    var seen: [[ChatMessage]] = []
    init(_ replies: [ModelReply]) { self.replies = replies }
    func complete(messages: [ChatMessage], tools: [ToolSpec], onText: @escaping (String) -> Void) async throws -> ModelReply {
        seen.append(messages)
        guard !replies.isEmpty else { throw MasonError("The script ran out of replies.") }
        let reply = replies.removeFirst()
        if !reply.text.isEmpty { onText(reply.text) }
        return reply
    }
}

func call(_ name: String, _ arguments: [String: String], id: String = UUID().uuidString) -> ModelReply {
    let json = String(decoding: try! JSONSerialization.data(withJSONObject: arguments), as: UTF8.self)
    return ModelReply(toolCalls: [ToolCall(id: id, name: name, arguments: json)])
}

final class MasonCoreTests: XCTestCase {
    var folder: URL!
    var home: URL!

    override func setUpWithError() throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-tests-" + UUID().uuidString)
        folder = base.appendingPathComponent("project"); home = base.appendingPathComponent("home")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try "def add(a, b):\n    return a - b\n".write(to: folder.appendingPathComponent("calc.py"), atomically: true, encoding: .utf8)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: folder.deletingLastPathComponent()) }

    func session(_ permission: Permission) throws -> (SessionStore, Session) {
        let store = try SessionStore(home: home)
        return (store, try store.create(workspace: try Workspace(path: folder.path).root.path, permission: permission))
    }

    // MARK: workspace

    func testWorkspaceKeepsPathsInsideTheProject() throws {
        let workspace = try Workspace(path: folder.path)
        XCTAssertThrowsError(try workspace.resolve("../outside.txt"))
        XCTAssertThrowsError(try workspace.resolve("/etc/passwd"))
        XCTAssertThrowsError(try workspace.resolve(".env"))
        XCTAssertThrowsError(try workspace.resolve("keys/id_rsa"))
        XCTAssertThrowsError(try workspace.resolve(".git/config"))
        XCTAssertNoThrow(try workspace.resolve("src/new/file.swift"))
    }

    func testWorkspaceRefusesALinkThatLeavesTheProject() throws {
        let outside = folder.deletingLastPathComponent().appendingPathComponent("outside")
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        try "secret".write(to: outside.appendingPathComponent("note.txt"), atomically: true, encoding: .utf8)
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("link"), withDestinationURL: outside)
        let workspace = try Workspace(path: folder.path)
        XCTAssertThrowsError(try workspace.read("link/note.txt"))
        XCTAssertFalse(try workspace.files().paths.contains { $0.hasPrefix("link") })
    }

    func testListingAndSearchSkipExcludedFolders() throws {
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("node_modules/x"), withIntermediateDirectories: true)
        try "return a - b".write(to: folder.appendingPathComponent("node_modules/x/index.js"), atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: folder.path)
        XCTAssertEqual(try workspace.files().paths, ["calc.py"])
        XCTAssertEqual(try workspace.search("RETURN A").map(\.path), ["calc.py"])
    }

    // MARK: diff

    func testDiffReportsOnlyTheChangedLine() {
        let rows = Diff.lines(before: "a\nb\nc\n", after: "a\nB\nc\n")
        XCTAssertEqual(rows.map { String($0.kind.rawValue) + $0.text }, [" a", "-b", "+B", " c"])
        XCTAssertEqual(Diff.counts(before: nil, after: "x\ny").added, 2)
        XCTAssertTrue(Diff.lines(before: "same\n", after: "same\n").allSatisfy { $0.kind == .same })
    }

    // MARK: streaming

    func testStreamAssemblerJoinsTextAndSplitToolCalls() throws {
        var assembler = StreamAssembler()
        XCTAssertEqual(try assembler.feed(line: #"data: {"choices":[{"delta":{"content":"Hel"}}]}"#), "Hel")
        XCTAssertEqual(try assembler.feed(line: #"data: {"choices":[{"delta":{"content":"lo"}}]}"#), "lo")
        _ = try assembler.feed(line: #"data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read_file","arguments":"{\"pa"}}]}}]}"#)
        _ = try assembler.feed(line: #"data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"calc.py\"}"}}]},"finish_reason":"tool_calls"}]}"#)
        _ = try assembler.feed(line: #"data: {"choices":[],"usage":{"prompt_tokens":42}}"#)
        XCTAssertNil(try assembler.feed(line: "data: [DONE]"))
        let reply = assembler.finish()
        XCTAssertEqual(reply.text, "Hello")
        XCTAssertEqual(reply.toolCalls, [ToolCall(id: "c1", name: "read_file", arguments: #"{"path":"calc.py"}"#)])
        XCTAssertEqual(reply.promptTokens, 42)
        XCTAssertThrowsError(try assembler.feed(line: #"data: {"error":{"message":"context too long"}}"#))
    }

    func testEndpointsMustBeOnThisComputer() {
        XCTAssertEqual(try ModelEndpoint.validated("http://localhost:8080/v1").absoluteString, "http://127.0.0.1:8080/")
        XCTAssertNoThrow(try ModelEndpoint.validated("http://127.0.0.1:1234"))
        XCTAssertThrowsError(try ModelEndpoint.validated("https://api.example.com/v1"))
        XCTAssertThrowsError(try ModelEndpoint.validated("http://user:pw@127.0.0.1:8080"))
        XCTAssertThrowsError(try ModelEndpoint.validated("http://127.0.0.1:8080/other"))
        XCTAssertThrowsError(try ModelEndpoint.validated("http://127.0.0.1.evil.example:8080"))
    }

    // MARK: agent

    func testAgentEditsAfterApprovalAndUndoRestoresTheFile() async throws {
        var (store, session) = try session(.reviewEdits)
        let model = ScriptedModel([
            call("read_file", ["path": "calc.py"]),
            call("edit_file", ["path": "calc.py", "old_text": "return a - b", "new_text": "return a + b"]),
            ModelReply(text: "Fixed add."),
        ])
        var asked: [ApprovalRequest] = []
        let agent = Agent(store: store, model: model, approve: { asked.append($0); return true }, emit: { _ in })
        await agent.run(&session, request: "Fix add")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(asked.map(\.tool), ["edit_file"])
        XCTAssertTrue(asked[0].detail.contains("+     return a + b"))
        let workspace = try Workspace(path: folder.path)
        XCTAssertEqual(try workspace.read("calc.py"), "def add(a, b):\n    return a + b\n")
        XCTAssertEqual(session.turns.last?.answer, "Fixed add.")
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed])
        // The model saw the numbered file before it edited.
        XCTAssertTrue(model.seen[1].last!.content.contains("2\t    return a - b"))

        let undo = try Agent.undoLatest(&session, store: store)
        XCTAssertEqual(undo?.restored, ["calc.py"])
        XCTAssertEqual(try workspace.read("calc.py"), "def add(a, b):\n    return a - b\n")
        let redo = try Agent.undoLatest(&session, store: store, redo: true)
        XCTAssertEqual(redo?.restored, ["calc.py"])
        XCTAssertEqual(try workspace.read("calc.py"), "def add(a, b):\n    return a + b\n")
        // A reloaded store has the same session.
        XCTAssertEqual(try SessionStore(home: home).sessions.first?.turns.count, 1)
    }

    func testUndoLeavesAFileTheUserChangedSince() async throws {
        var (store, session) = try session(.workspaceEdits)
        let agent = Agent(store: store, model: ScriptedModel([call("write_file", ["path": "notes.txt", "content": "one\n"]), ModelReply(text: "Done.")]), approve: { _ in XCTFail("workspace edits must not ask"); return false }, emit: { _ in })
        await agent.run(&session, request: "Add notes")
        try "mine\n".write(to: folder.appendingPathComponent("notes.txt"), atomically: true, encoding: .utf8)
        let undo = try Agent.undoLatest(&session, store: store)
        XCTAssertEqual(undo?.skipped, ["notes.txt"])
        XCTAssertEqual(try Workspace(path: folder.path).read("notes.txt"), "mine\n")
    }

    func testDeclinedEditAndReadOnlyChangeNothing() async throws {
        var (store, session) = try session(.reviewEdits)
        let edit = call("edit_file", ["path": "calc.py", "old_text": "return a - b", "new_text": "return a + b"])
        await Agent(store: store, model: ScriptedModel([edit, ModelReply(text: "Not changed.")]), approve: { _ in false }, emit: { _ in }).run(&session, request: "Fix add")
        XCTAssertEqual(session.activities.last?.state, .denied)

        var (store2, readOnly) = try self.session(.readOnly)
        await Agent(store: store2, model: ScriptedModel([edit, call("run_shell", ["command": "touch made"]), ModelReply(text: "I can only read.")]), approve: { _ in XCTFail("read-only must not ask"); return true }, emit: { _ in }).run(&readOnly, request: "Fix add")
        XCTAssertEqual(readOnly.activities.map(\.state), [.failed, .failed])
        XCTAssertEqual(try Workspace(path: folder.path).read("calc.py"), "def add(a, b):\n    return a - b\n")
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("made").path))
    }

    func testAmbiguousEditIsRejectedWithGuidance() async throws {
        try "x = 1\nx = 1\n".write(to: folder.appendingPathComponent("twice.py"), atomically: true, encoding: .utf8)
        var (store, session) = try session(.workspaceEdits)
        let model = ScriptedModel([call("edit_file", ["path": "twice.py", "old_text": "x = 1", "new_text": "x = 2"]), ModelReply(text: "Could not.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Change x")
        XCTAssertEqual(session.activities.last?.state, .failed)
        XCTAssertTrue(model.seen[1].last!.content.contains("matches 2 places"))
    }

    func testCommandAsksOnceThenASavedRuleAllowsIt() async throws {
        var (store, session) = try session(.workspaceEdits)
        var asked = 0
        let script = [call("run_shell", ["command": "echo hello"]), ModelReply(text: "Ran it.")]
        await Agent(store: store, model: ScriptedModel(script), approve: { _ in asked += 1; return true }, emit: { _ in }).run(&session, request: "Say hello")
        XCTAssertEqual(asked, 1)
        XCTAssertEqual(session.activities.last?.state, .completed)
        XCTAssertTrue(session.activities.last!.result.contains("hello"))

        var rules = ApprovalRules(home: home, workspace: session.workspace)
        XCTAssertEqual(ApprovalRules.suggestion(tool: "run_shell", subject: "echo hello world"), "echo hello")
        try rules.add(tool: "run_shell", prefix: "echo hello")
        await Agent(store: store, model: ScriptedModel(script), approve: { _ in asked += 1; return true }, emit: { _ in }).run(&session, request: "Again")
        XCTAssertEqual(asked, 1, "the saved rule answers the second prompt")
    }

    func testAStoppedRequestLeavesAConversationTheModelCanContinue() async throws {
        var (store, session) = try session(.workspaceEdits)
        session.messages = [ChatMessage(role: "user", content: "go"), ChatMessage(role: "assistant", content: "", toolCalls: [ToolCall(id: "a", name: "read_file", arguments: "{}"), ToolCall(id: "b", name: "read_file", arguments: "{}")]), ChatMessage(role: "tool", content: "ok", toolCallId: "a")]
        Agent.closeOpenCalls(&session)
        XCTAssertEqual(session.messages.map(\.toolCallId), [nil, nil, "a", "b"])
        _ = store
    }

    func testAutoRefusesToRunUnsandboxedWhereThereIsNoSandbox() async throws {
        #if os(macOS)
        throw XCTSkip("macOS has the sandbox; this checks the fallback elsewhere.")
        #else
        var (store, session) = try session(.auto)
        await Agent(store: store, model: ScriptedModel([call("run_shell", ["command": "touch made"]), ModelReply(text: "Could not run.")]), approve: { _ in true }, emit: { _ in }).run(&session, request: "Make a file")
        XCTAssertEqual(session.activities.last?.state, .failed)
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("made").path))
        #endif
    }

    func testSandboxProfileLimitsWritesToTheProjectAndDeniesNetwork() {
        let profile = SandboxProfile.profile(workspace: "/Users/me/My \"Project\"", scratch: "/private/tmp/mason-1")
        XCTAssertTrue(profile.contains(#"(allow file-write* (subpath "/Users/me/My \"Project\"") (subpath "/private/tmp/mason-1"))"#))
        XCTAssertTrue(profile.contains("(deny network*)"))
        XCTAssertTrue(profile.hasPrefix("(version 1)\n(deny default)"))
    }
}

final class ModelTests: XCTestCase {
    func testSha256MatchesKnownDigests() {
        var empty = Sha256()
        XCTAssertEqual(empty.hex(), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
        empty.update(Data("abc".utf8))
        XCTAssertEqual(empty.hex(), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
        // Fed in uneven pieces across block boundaries, the digest is the same as in one piece.
        let text = Data(String(repeating: "mason agent ", count: 1000).utf8)
        var whole = Sha256(); whole.update(text)
        var pieces = Sha256(); var offset = 0
        for size in [1, 63, 64, 65, 1000, 7] + [Int](repeating: 500, count: 30) where offset < text.count {
            let end = min(text.count, offset + size); pieces.update(text.subdata(in: offset..<end)); offset = end
        }
        if offset < text.count { pieces.update(text.subdata(in: offset..<text.count)) }
        XCTAssertEqual(whole.hex(), pieces.hex())
    }

    func testModelFilesAreClassifiedAndNeverHidden() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mason-models-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        for name in ["coder/Qwen-Coder-Q4_K_M.gguf", "vision/Seer-Q8_0.gguf", "vision/mmproj-Seer-f16.gguf", "search/nomic-embed-text-Q8.gguf", "split/Big-00001-of-00002.gguf", "split/Big-00002-of-00002.gguf", "tiny/stub.gguf"] {
            let file = root.appendingPathComponent(name)
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(count: name.contains("stub") ? 10 : 1_100_000).write(to: file)
        }
        let models = ModelLibrary.scan([(name: "Test", url: root)])
        XCTAssertEqual(models.map(\.name), ["Big-00001-of-00002", "Qwen-Coder-Q4_K_M", "Seer-Q8_0", "mmproj-Seer-f16", "nomic-embed-text-Q8"])
        XCTAssertEqual(models.map(\.role), [.coding, .coding, .vision, .projector, .embeddings])
        XCTAssertTrue(models.allSatisfy { !$0.verdict.isEmpty })
    }

    func testMemoryFitAndSuggestedBuild() {
        let budget = MemoryFit.budgetBytes
        XCTAssertGreaterThan(budget, 0)
        let repository = HubRepository(id: "a/b", revision: String(repeating: "a", count: 40), license: "mit", gated: false, builds: [
            HubBuild(file: "m-Q8_0.gguf", bytes: 400 << 30, sha256: ""), HubBuild(file: "m-Q4_K_M.gguf", bytes: 1 << 20, sha256: ""), HubBuild(file: "mmproj-Q8_0.gguf", bytes: 1 << 20, sha256: ""),
        ])
        XCTAssertEqual(repository.suggested(budget: 8 << 30)?.file, "m-Q4_K_M.gguf", "the best quality that fits wins, and projectors are not suggested")
        XCTAssertNil(repository.suggested(budget: 1 << 20))
        XCTAssertEqual(HubBuild(file: "x.Q5_K_M.gguf", bytes: 1, sha256: "").quant, "Q5_K_M")
    }

    func testHubResultsCarryAVerdict() {
        func model(_ id: String, _ tags: [String], _ task: String) -> HubModel { HubModel(id: id, downloads: 1, likes: 0, tags: tags, task: task, gated: false) }
        XCTAssertTrue(model("Qwen/Coder-GGUF", ["gguf"], "text-generation").verdict.hasPrefix("Coding agent · GGUF"))
        XCTAssertTrue(model("a/sdxl", [], "text-to-image").verdict.hasPrefix("Not used by the agent"))
        XCTAssertTrue(model("a/coder", ["safetensors"], "text-generation").verdict.contains("look for a GGUF build"))
        XCTAssertTrue(model("nomic/embed-GGUF", ["gguf"], "feature-extraction").verdict.hasPrefix("Code search"))
        XCTAssertThrowsError(try Hub.checked("../etc"))
    }
}

final class ShellTests: XCTestCase {
    func testOnlyTheCommandsOwnOutputIsReturned() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mason-shell-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let result = try await Shell.run("echo out; echo err 1>&2; exit 3", in: try Workspace(path: folder.path), sandboxed: false)
        XCTAssertEqual(result.exitCode, 3)
        XCTAssertEqual(result.output.split(separator: "\n").map(String.init).sorted(), ["err", "out"], "no job-control notices from the wrapper shell")
    }

    func testACommandPastItsDeadlineIsStopped() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mason-shell-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let started = Date()
        let result = try await Shell.run("echo before; sleep 30; echo after", in: try Workspace(path: folder.path), sandboxed: false, timeout: 1)
        XCTAssertTrue(result.timedOut)
        XCTAssertTrue(result.output.contains("before"))
        XCTAssertFalse(result.output.contains("after"))
        XCTAssertLessThan(Date().timeIntervalSince(started), 10)
    }
}

final class RecoveryTests: XCTestCase {
    func testACallWrittenAsTextIsRecoveredOnlyForOfferedTools() throws {
        let fenced = "I'll fix it.\n```json\n{\n  \"name\": \"edit_file\",\n  \"arguments\": {\"path\": \"calc.py\", \"old_text\": \"a - b\", \"new_text\": \"a + b\"}\n}\n```\nThen run it."
        let call = try XCTUnwrap(ToolCallRecovery.recover(from: fenced, tools: Agent.tools))
        XCTAssertEqual(call.name, "edit_file")
        XCTAssertEqual(try JSONSerialization.jsonObject(with: Data(call.arguments.utf8)) as? [String: String], ["path": "calc.py", "old_text": "a - b", "new_text": "a + b"])
        XCTAssertEqual(ToolCallRecovery.recover(from: "<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.txt\"}}\n</tool_call>", tools: Agent.tools)?.name, "read_file")
        XCTAssertEqual(ToolCallRecovery.recover(from: "{\"function\": {\"name\": \"run_shell\", \"arguments\": \"{\\\"command\\\": \\\"ls\\\"}\"}}", tools: Agent.tools)?.arguments, "{\"command\": \"ls\"}")
        XCTAssertNil(ToolCallRecovery.recover(from: "```json\n{\"name\": \"launch_rocket\", \"arguments\": {}}\n```", tools: Agent.tools))
        XCTAssertNil(ToolCallRecovery.recover(from: "```python\nprint(1)\n```\nDone.", tools: Agent.tools))
        XCTAssertNil(ToolCallRecovery.recover(from: "The config is {\"name\": \"edit_file\"} in prose.", tools: Agent.tools))
    }
}

#if os(macOS)
/// Runs real commands under the Auto sandbox. These only run on a Mac, where sandbox-exec exists.
final class SandboxTests: XCTestCase {
    func testAutoWritesInsideTheProjectButNotOutsideAndHasNoNetwork() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mason-sandbox-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let workspace = try Workspace(path: folder.path)
        let outside = NSHomeDirectory() + "/mason-sandbox-outside-" + UUID().uuidString
        defer { try? FileManager.default.removeItem(atPath: outside) }

        let inside = try await Shell.run("echo hello > inside.txt && cat inside.txt && ls", in: workspace, sandboxed: true)
        XCTAssertEqual(inside.exitCode, 0, inside.output)
        XCTAssertTrue(inside.output.contains("hello"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("inside.txt").path))

        let escape = try await Shell.run("echo leaked > '\(outside)'", in: workspace, sandboxed: true)
        XCTAssertNotEqual(escape.exitCode, 0, "a write outside the project must be refused")
        XCTAssertFalse(FileManager.default.fileExists(atPath: outside))

        let network = try await Shell.run("/usr/bin/curl -m 5 -s -o /dev/null https://example.com", in: workspace, sandboxed: true)
        XCTAssertNotEqual(network.exitCode, 0, "network access must be refused")

        let secrets = try await Shell.run("ls ~/.ssh", in: workspace, sandboxed: true)
        XCTAssertNotEqual(secrets.exitCode, 0, "credential folders must not be readable")

        let temporary = try await Shell.run("f=$(mktemp) && echo ok > \"$f\" && cat \"$f\"", in: workspace, sandboxed: true)
        XCTAssertEqual(temporary.exitCode, 0, "commands may use their own temporary folder: " + temporary.output)

        let started = Date()
        let hung = try await Shell.run("sleep 30", in: workspace, sandboxed: true, timeout: 1)
        XCTAssertTrue(hung.timedOut)
        XCTAssertLessThan(Date().timeIntervalSince(started), 10, "a sandboxed command past its deadline is stopped")
    }
}
#endif
