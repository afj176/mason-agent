import XCTest
@testable import MasonCore

/// Replays fixed replies, like a model that always makes the same decisions.
final class ScriptedModel: ChatModel {
    let name = "scripted"
    var replies: [ModelReply]
    var seen: [[ChatMessage]] = []
    var window: Int?
    init(_ replies: [ModelReply]) { self.replies = replies }
    func contextWindow() async -> Int? { window }
    /// Called at the start of each request, with its number, so a test can act while the "model" is busy.
    var onRequest: ((Int) -> Void)?
    var delay: UInt64 = 0
    func complete(messages: [ChatMessage], tools: [ToolSpec], onText: @escaping (String) -> Void) async throws -> ModelReply {
        seen.append(messages)
        onRequest?(seen.count)
        if delay > 0 { try await Task.sleep(nanoseconds: delay) }
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

    func testAProjectOpenedThroughALinkListsTheSameRelativePaths() throws {
        let link = folder.deletingLastPathComponent().appendingPathComponent("alias")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: folder)
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("src"), withIntermediateDirectories: true)
        try "x".write(to: folder.appendingPathComponent("src/a.txt"), atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: link.path)
        XCTAssertEqual(try workspace.files().paths, ["calc.py", "src/a.txt"])
        XCTAssertEqual(try workspace.files(under: "src").paths, ["src/a.txt"])
        XCTAssertNoThrow(try workspace.write("src/b.txt", "y"))
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

    // MARK: undo of what commands change

    func testUndoPutsBackWhatACommandCreatedChangedAndDeleted() async throws {
        var (store, session) = try session(.workspaceEdits)
        try "keep me\n".write(to: folder.appendingPathComponent("keep.txt"), atomically: true, encoding: .utf8)
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("node_modules"), withIntermediateDirectories: true)
        let original = try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8)
        let command = "printf 'made' > made.txt && printf 'changed' > calc.py && rm keep.txt && printf '\\377\\376\\000x' > image.bin && printf dep > node_modules/dep.js"
        let script = [call("run_shell", ["command": command]), ModelReply(text: "Done.")]
        await Agent(store: store, model: ScriptedModel(script), approve: { _ in true }, emit: { _ in }).run(&session, request: "Run the script")

        let changes = session.turns.last!.changes
        XCTAssertEqual(changes.map(\.path).sorted(), ["calc.py", "image.bin", "keep.txt", "made.txt"], "dependency folders are not followed")
        XCTAssertTrue(changes.allSatisfy { $0.byCommand == true })
        XCTAssertTrue(changes.first { $0.path == "image.bin" }!.isOpaque, "a binary file is kept as a blob")
        XCTAssertTrue(changes.first { $0.path == "keep.txt" }!.deleted)
        XCTAssertTrue(session.messages.last { $0.role == "tool" }!.content.contains("Files changed by this command"))

        let undone = try XCTUnwrap(Agent.undoLatest(&session, store: store))
        XCTAssertEqual(undone.restored.sorted(), ["calc.py", "image.bin", "keep.txt", "made.txt"])
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8), original)
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("keep.txt"), encoding: .utf8), "keep me\n")
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("made.txt").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("image.bin").path))

        let redone = try XCTUnwrap(Agent.undoLatest(&session, store: store, redo: true))
        XCTAssertEqual(redone.restored.count, 4)
        XCTAssertEqual(try Data(contentsOf: folder.appendingPathComponent("image.bin")), Data([0xFF, 0xFE, 0x00, 0x78]))
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("keep.txt").path))

        // A file the user changed after the command is left alone.
        try "mine now".write(to: folder.appendingPathComponent("made.txt"), atomically: true, encoding: .utf8)
        let again = try XCTUnwrap(Agent.undoLatest(&session, store: store))
        XCTAssertEqual(again.skipped, ["made.txt"])
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("made.txt"), encoding: .utf8), "mine now")
    }

    func testAnEditThenACommandOnTheSameFileUndoesToTheStart() async throws {
        var (store, session) = try session(.workspaceEdits)
        let original = try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8)
        let script = [call("write_file", ["path": "calc.py", "content": "one\n"]), call("run_shell", ["command": "printf 'two\\n' >> calc.py"]), ModelReply(text: "Done.")]
        await Agent(store: store, model: ScriptedModel(script), approve: { _ in true }, emit: { _ in }).run(&session, request: "Rewrite it")
        XCTAssertEqual(session.turns.last!.changes.count, 1)
        XCTAssertEqual(session.turns.last!.changes[0].after, "one\ntwo\n")
        _ = try Agent.undoLatest(&session, store: store)
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8), original)
    }

    // MARK: custom agents and commands

    func testCustomCommandsExpandAndTheSelectedAgentFramesTheRequest() async throws {
        var (store, session) = try session(.readOnly)
        let commands = folder.appendingPathComponent(".claude/commands"), agents = folder.appendingPathComponent(".mason/agents")
        try FileManager.default.createDirectory(at: commands, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: agents, withIntermediateDirectories: true)
        try "---\ndescription: Review one file\n---\nReview $1 for $2. All: $ARGUMENTS".write(to: commands.appendingPathComponent("review.md"), atomically: true, encoding: .utf8)
        try "Explain the tests.".write(to: commands.appendingPathComponent("tests.md"), atomically: true, encoding: .utf8)
        try "not a command".write(to: commands.appendingPathComponent("undo.md"), atomically: true, encoding: .utf8)
        try "---\nname: careful\ndescription: Slow and careful\n---\nNever guess.".write(to: agents.appendingPathComponent("careful.md"), atomically: true, encoding: .utf8)

        let found = Customization.commands(home: home, workspace: session.workspace)
        XCTAssertEqual(found.map(\.name), ["review", "tests"], "reserved names are skipped")
        XCTAssertEqual(found[0].description, "Review one file")
        XCTAssertEqual(found[0].source, ".claude/commands/review.md")
        XCTAssertEqual(Customization.expand(found, "/review calc.py \"off by one\""), "Review calc.py for off by one. All: calc.py \"off by one\"")
        XCTAssertEqual(Customization.expand(found, "/tests quickly"), "Explain the tests.\n\nquickly")
        XCTAssertNil(Customization.expand(found, "/missing"))
        XCTAssertNil(Customization.expand(found, "review calc.py"))

        session.agent = "careful"
        let model = ScriptedModel([ModelReply(text: "Reviewed.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "/review calc.py bugs")
        let sent = try XCTUnwrap(model.seen.first?.last?.content)
        XCTAssertTrue(sent.contains("Never guess.") && sent.contains("Review calc.py for bugs."))
        XCTAssertEqual(session.turns.last?.request, "/review calc.py bugs", "the session shows what was typed")
    }

    // MARK: the extra tools

    func testLineRangesSeveralEditsAtOnceAndADelegatedQuestion() async throws {
        var (store, session) = try session(.workspaceEdits)
        try (1...500).map { "line \($0)" }.joined(separator: "\n").write(to: folder.appendingPathComponent("big.txt"), atomically: true, encoding: .utf8)
        let model = ScriptedModel([
            call("read_lines", ["path": "big.txt", "start": "250", "count": "3"]),
            ModelReply(toolCalls: [ToolCall(id: "e1", name: "apply_edits", arguments: #"{"path":"calc.py","edits":[{"old_text":"a - b","new_text":"a + b"},{"old_text":"def add","new_text":"def plus"}]}"#)]),
            ModelReply(toolCalls: [ToolCall(id: "e2", name: "apply_edits", arguments: #"{"path":"calc.py","edits":[{"old_text":"plus","new_text":"sum"},{"old_text":"not there","new_text":"x"}]}"#)]),
            ModelReply(toolCalls: [ToolCall(id: "d1", name: "delegate_readonly", arguments: #"{"question":"What does it return?","paths":["calc.py"]}"#)]),
            ModelReply(text: "calc.py:2 returns a + b."),
            call("git_status", [:]),
            ModelReply(text: "Done."),
        ])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Work on it")
        XCTAssertEqual(session.state, "Completed")
        let results = session.activities.map(\.result)
        XCTAssertTrue(results[0].hasPrefix("250\tline 250\n251\tline 251\n252\tline 252") && results[0].hasSuffix("[lines 250–252 of 500]"))
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed, .failed, .completed, .completed])
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8), "def plus(a, b):\n    return a + b\n", "the second batch changed nothing because one of its edits did not apply")
        XCTAssertTrue(results[2].contains("Edit 2") && results[2].contains("Nothing was changed"))
        XCTAssertEqual(results[3], "calc.py:2 returns a + b.")
        XCTAssertTrue(model.seen[4][1].content.contains("2\t    return a + b") && model.seen[4].count == 2, "the separate context sees only the files and the question")
        XCTAssertEqual(results[4], "This project is not a Git repository.")
        XCTAssertEqual(session.turns[0].changes.count, 1)
        _ = try Agent.undoLatest(&session, store: store)
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8), "def add(a, b):\n    return a - b\n")
    }

    func testACommandWithExactArgumentsSearchPagingAndATerminal() async throws {
        var (store, session) = try session(.workspaceEdits)
        try (1...150).map { "needle \($0)" }.joined(separator: "\n").write(to: folder.appendingPathComponent("many.txt"), atomically: true, encoding: .utf8)
        let exact = ModelReply(toolCalls: [ToolCall(id: "exact", name: "run_command", arguments: #"{"program":"printf","arguments":["%s|","a b","$HOME","it's","*"],"directory":"."}"#)])
        let script = [exact, call("search_files", ["query": "needle"]), call("search_next", ["cursor": "100"]), ModelReply(text: "Done.")]
        var asked: [String] = []
        await Agent(store: store, model: ScriptedModel(script), approve: { asked.append($0.tool); return true }, emit: { _ in }).run(&session, request: "Try them")
        XCTAssertEqual(asked, ["run_command"], "a command with exact arguments asks like any command outside Auto")
        XCTAssertTrue(session.activities[0].result.contains("a b|$HOME|it's|*|"), "nothing in an argument is expanded or split: " + session.activities[0].result)
        let first = session.activities[1].result, second = session.activities[2].result
        XCTAssertTrue(first.contains("many.txt:100: needle 100") && !first.contains("needle 101") && first.contains("search_next and cursor \"100\""), first)
        XCTAssertTrue(second.contains("many.txt:101: needle 101") && second.contains("needle 150") && !second.contains("search_next"), second)

        let jobs = Jobs()
        let workspace = try Workspace(path: folder.path)
        let id = try jobs.start("read a; echo got $a; read b; echo end $b", workspace: workspace, sandboxed: false, interactive: true)
        _ = try jobs.send(id, text: "one", eof: false)
        var seen = ""
        for _ in 0..<40 where !seen.contains("got one") { seen = try await jobs.observe(id, stop: false, waitMs: 250) }
        XCTAssertTrue(seen.contains("got one") && seen.contains("running"), "the program answered the line and waits for the next: " + seen)
        _ = try jobs.send(id, text: "", eof: true)
        seen = try await jobs.observe(id, stop: false, waitMs: 5000)
        XCTAssertTrue(seen.contains("exited 0") && seen.contains("end"), "closing the input lets the program finish: " + seen)
        XCTAssertThrowsError(try jobs.send(id, text: "late", eof: false), "a terminal that has ended takes no more input")
        let plain = try jobs.start("sleep 30", workspace: workspace, sandboxed: false)
        XCTAssertThrowsError(try jobs.send(plain, text: "x", eof: false), "a job started without input takes none")
        await jobs.stopAll()
    }

    func testTheMapRanksANameAndScriptsRunSingleOperations() async throws {
        try "from calc import add\nprint(add(2, 2))\n".write(to: folder.appendingPathComponent("main.py"), atomically: true, encoding: .utf8)
        try "from calc import add\n\ndef test_add():\n    assert add(1, 2) == 3\n".write(to: folder.appendingPathComponent("test_calc.py"), atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: folder.path)
        let found = try RepositoryMap.query(workspace, "calc.add")
        XCTAssertEqual(found.matches.first.map { "\($0.path):\($0.line) \($0.kind)" }, "calc.py:1 definition candidate", "the definition comes first")
        XCTAssertEqual(found.matches.map(\.kind), ["definition candidate", "import candidate", "import candidate", "reference candidate", "test reference candidate"])
        XCTAssertTrue(RepositoryMap.report(found, query: "calc.add").contains("main.py:2  [reference candidate]  print(add(2, 2))"))

        var lines: [String] = []
        func events() -> [[String: Any]] { lines.compactMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any] } }
        var code = await ScriptOperations.run(["map", "--workspace", folder.path, "--query", "add"]) { lines.append($0) }
        XCTAssertEqual(code, 0)
        XCTAssertEqual(events().first?["type"] as? String, "map.result")
        XCTAssertEqual(((events().first?["data"] as? [String: Any])?["matches"] as? [[String: Any]])?.count, 5)

        lines = []
        code = await ScriptOperations.run(["map", "--workspace", folder.path]) { lines.append($0) }
        XCTAssertEqual(code, 1)
        XCTAssertEqual((events().first?["data"] as? [String: Any])?["message"] as? String, "--query is required.")

        lines = []
        code = await ScriptOperations.run(["git-status", "--workspace", folder.path]) { lines.append($0) }
        XCTAssertEqual(code, 0)
        XCTAssertEqual((events().first?["data"] as? [String: Any])?["repository"] as? Bool, false)

        lines = []
        let journal = home.appendingPathComponent("runs").path
        let script = ScriptedModel([call("read_file", ["path": "calc.py"]), call("write_file", ["path": "calc.py", "content": "x"]), ModelReply(text: "It subtracts.")])
        code = await ScriptOperations.run(["exec", "--workspace", folder.path, "--prompt", "What does add do?", "--profile", journal], model: script) { lines.append($0) }
        XCTAssertEqual(code, 0, lines.joined(separator: "\n"))
        XCTAssertEqual(events().map { $0["type"] as? String }, ["run.started", "tool.result", "tool.result", "run.finished"])
        XCTAssertEqual((events()[0]["data"] as? [String: Any])?["mode"] as? String, "ask")
        XCTAssertEqual((events()[1]["data"] as? [String: Any])?["tool"] as? String, "read_file")
        XCTAssertEqual((events().last?["data"] as? [String: Any])?["answer"] as? String, "It subtracts.")
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8), "def add(a, b):\n    return a - b\n", "without --auto the run only reads")

        lines = []
        code = await ScriptOperations.run(["exec", "--workspace", folder.path, "--prompt", "x", "--profile", folder.appendingPathComponent("journal").path], model: script) { lines.append($0) }
        XCTAssertEqual(code, 1, "the journal may not be inside the project")
    }

    func testAnExperimentStartsFromTheProjectAsItStandsAndAHunkComesBack() async throws {
        let workspace = try Workspace(path: folder.path)
        let made = try await Shell.run("git init -q . && git add . && git -c user.name=t -c user.email=t@t.invalid commit -qm start", in: workspace, sandboxed: false)
        try XCTSkipIf(made.exitCode != 0, "git is not available: " + made.output)
        // An uncommitted edit and an untracked file: the experiment starts with both.
        try "def add(a, b):\n    return a - b\n\n\ndef twice(a):\n    return a * 2\n".write(to: folder.appendingPathComponent("calc.py"), atomically: true, encoding: .utf8)
        try "draft\n".write(to: folder.appendingPathComponent("notes.txt"), atomically: true, encoding: .utf8)
        let said = try await Experiments.command("new try", workspace: workspace, home: home)
        let experiment = try XCTUnwrap(Experiments.list(workspace, home: home).first)
        defer { try? FileManager.default.removeItem(atPath: experiment.path) }
        XCTAssertTrue(said.contains(experiment.path) && experiment.branch == "mason/try", said)
        XCTAssertEqual(try String(contentsOfFile: experiment.path + "/notes.txt", encoding: .utf8), "draft\n")
        XCTAssertTrue(try String(contentsOfFile: experiment.path + "/calc.py", encoding: .utf8).contains("twice"))
        let nothing = try await Experiments.changed(experiment)
        XCTAssertEqual(nothing, [], "a new experiment has changed nothing")
        let index = try await Shell.run("git status --porcelain", in: workspace, sandboxed: false).output
        XCTAssertTrue(index.contains(" M calc.py") && index.contains("?? notes.txt"), "the project's own index is untouched: " + index)

        try "def add(a, b):\n    return a + b\n\n\ndef twice(a):\n    return a * 2\n".write(toFile: experiment.path + "/calc.py", atomically: true, encoding: .utf8)
        let changed = try await Experiments.changed(experiment)
        XCTAssertEqual(changed, ["calc.py"])
        let hunks = try await Experiments.hunks(experiment, path: "calc.py")
        XCTAssertEqual(hunks.count, 1)
        _ = try await Experiments.command("apply try calc.py \(hunks[0].id)", workspace: workspace, home: home)
        XCTAssertTrue(try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8).hasPrefix("def add(a, b):\n    return a + b\n"), "the hunk is in the project")
        do { try await Experiments.apply(experiment, path: "calc.py", hunk: hunks[0].id); XCTFail("a hunk that no longer fits is refused") } catch {}
        do { try await Experiments.apply(experiment, path: "calc.py", hunk: "00000000"); XCTFail("an unknown hunk is refused") } catch {}
        do { _ = try await Experiments.createWorktree(workspace, destination: folder.appendingPathComponent("inside").path, branch: "x"); XCTFail("a worktree inside the project is refused") } catch {}
    }

    func testADeliveryIsOneCommitOfChosenHunksAndLeavesTheCheckoutAlone() async throws {
        let workspace = try Workspace(path: folder.path)
        let lines = (1...30).map { "line \($0)" }
        try (lines.joined(separator: "\n") + "\n").write(to: folder.appendingPathComponent("big.txt"), atomically: true, encoding: .utf8)
        let made = try await Shell.run("git init -q . && git symbolic-ref HEAD refs/heads/main && git config user.name t && git config user.email t@t.invalid && git add . && git commit -qm start && git remote add origin git@github.com:someone/shop.git", in: workspace, sandboxed: false)
        try XCTSkipIf(made.exitCode != 0, "git is not available: " + made.output)
        var edited = lines; edited[1] = "line two"; edited[27] = "line twenty-eight"
        try (edited.joined(separator: "\n") + "\n").write(to: folder.appendingPathComponent("big.txt"), atomically: true, encoding: .utf8)
        let head = try await Shell.run("git rev-parse HEAD", in: workspace, sandboxed: false).output

        let reviewed = try await Delivery.review(workspace, base: "main")
        XCTAssertEqual(reviewed.patches.map(\.path), ["big.txt", "big.txt"], "two separate hunks")
        let draft = try await Delivery.draft(workspace, base: "main", pinned: reviewed.revision, title: "Spell out two", selected: [reviewed.patches[0].hunk.id], home: home)
        XCTAssertEqual(draft.repository, "someone/shop")
        XCTAssertTrue(draft.branch.hasPrefix("mason/review-") && draft.url == nil && draft.paths == ["big.txt"])
        let shown = try await Shell.run("git show \(draft.commit):big.txt && git log -1 --format=%s%n%an \(draft.commit)", in: workspace, sandboxed: false).output
        XCTAssertTrue(shown.contains("line two\n") && shown.contains("line 28\n") && !shown.contains("twenty-eight"), "only the chosen hunk is in the commit")
        XCTAssertTrue(shown.contains("Spell out two\nt\n"), "the commit carries the user's name: " + shown)
        let after = try await Shell.run("git rev-parse HEAD && git status --porcelain", in: workspace, sandboxed: false).output
        XCTAssertTrue(after.hasPrefix(head) && after.contains(" M big.txt"), "the branch, the index and the files are as they were: " + after)
        XCTAssertEqual(Delivery.current(workspace, home: home), draft)
        let all = try await Delivery.command("draft main all Both lines", workspace: workspace, home: home)
        XCTAssertTrue(all.text.contains("\"Both lines\" onto main") && all.text.contains("someone/shop"), all.text)
        do { _ = try await Delivery.draft(workspace, base: "main", pinned: String(repeating: "0", count: 40), title: "x", selected: ["a"], home: home); XCTFail("a base that moved is refused") } catch {}
        do { _ = try await Delivery.feedback(workspace, draft); XCTFail("feedback needs a published draft") } catch {}

        XCTAssertEqual(try Delivery.repository("https://github.com/Owner/Repo.git"), "Owner/Repo")
        XCTAssertThrowsError(try Delivery.repository("https://example.com/owner/repo.git"))
        let state = #"{"url":"https://github.com/someone/shop/pull/7","headRefOid":"abc","statusCheckRollup":[{"name":"build","conclusion":"FAILURE","detailsUrl":"https://ci/1"},{"name":"lint","conclusion":"SUCCESS"},{"context":"deploy","state":"ERROR"}],"reviews":[{"state":"CHANGES_REQUESTED","body":"Rename it."},{"state":"APPROVED","body":"ok"}]}"#
        let feedback = try Delivery.parseFeedback(state: state, comments: #"[{"path":"big.txt","line":2,"body":"Why two?"}]"#)
        XCTAssertEqual(feedback.observations, ["CI · build · FAILURE · https://ci/1", "CI · deploy · ERROR · ", "Review · Rename it.", "big.txt:2 · Why two?"])
        XCTAssertEqual(feedback.head, "abc")
    }

    func testABackgroundTaskWaitsForItsApprovalAndCanBeCancelled() async throws {
        let workspace = try Workspace(path: folder.path)
        func record(_ id: String) throws {
            try FileManager.default.createDirectory(at: BackgroundTasks.folder(id, home), withIntermediateDirectories: true)
            BackgroundTasks.write(BackgroundTask(id: id, workspace: realPath(folder.path), prompt: "Fix add", endpoint: ModelEndpoint(url: "http://127.0.0.1:1", model: "m"), auto: false, created: Date()), BackgroundTasks.folder(id, home).appendingPathComponent("request.json"))
            BackgroundTasks.write(TaskStatus(state: "Starting", detail: "", pid: 0, started: Date()), BackgroundTasks.folder(id, home).appendingPathComponent("status.json"))
        }
        func waiting(_ id: String) async throws -> TaskStatus {
            var state = try BackgroundTasks.status(id, home: home)
            for _ in 0..<100 where state.state != "Waiting for approval" { try await Task.sleep(nanoseconds: 100_000_000); state = try BackgroundTasks.status(id, home: home) }
            return state
        }
        // The worker runs here in the test; a real one is the same code in its own process.
        let first = "aaaaaaaa0001"
        try record(first)
        let fix = ScriptedModel([call("write_file", ["path": "calc.py", "content": "def add(a, b):\n    return a + b\n"]), ModelReply(text: "Fixed add.")])
        async let finished = BackgroundTasks.work(first, home: home, model: fix)
        let asked = try await waiting(first)
        XCTAssertEqual(asked.state, "Waiting for approval")
        XCTAssertEqual(asked.approval?.tool, "write_file")
        XCTAssertThrowsError(try BackgroundTasks.decide(first, request: "not-this-one", allow: true, home: home), "an answer for another approval is refused")
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8), "def add(a, b):\n    return a - b\n", "nothing is written while it waits")
        let listed = try BackgroundTasks.command("", workspace: workspace, endpoint: nil, auto: false, program: "/bin/sh", home: home)
        XCTAssertTrue(listed.contains(first) && listed.contains("waiting on:"), listed)
        _ = try BackgroundTasks.command("allow \(first)", workspace: workspace, endpoint: nil, auto: false, program: "/bin/sh", home: home)
        let code = await finished
        XCTAssertEqual(code, 0)
        XCTAssertEqual(try BackgroundTasks.status(first, home: home).state, "Completed")
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8), "def add(a, b):\n    return a + b\n")
        let shown = try BackgroundTasks.command("show \(first)", workspace: workspace, endpoint: nil, auto: false, program: "/bin/sh", home: home)
        XCTAssertTrue(shown.contains("Fixed add.") && shown.contains("write_file calc.py"), shown)

        let second = "aaaaaaaa0002"
        try record(second)
        async let stopped = BackgroundTasks.work(second, home: home, model: ScriptedModel([call("run_shell", ["command": "touch made"]), ModelReply(text: "Done.")]))
        _ = try await waiting(second)
        try BackgroundTasks.cancel(second, home: home)
        let ended = await stopped
        XCTAssertEqual(ended, 2)
        XCTAssertEqual(try BackgroundTasks.status(second, home: home).state, "Cancelled")
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("made").path), "a cancelled task's pending command never ran")
        XCTAssertThrowsError(try BackgroundTasks.status("../../etc", home: home), "an id is never a path")
    }

    func testARoleCountsItsCallsWritesATraceAndIsChosenByCommand() async throws {
        let trace = home.appendingPathComponent("traces")
        let limited = RoleModel(ScriptedModel([ModelReply(text: "one"), ModelReply(text: "two"), ModelReply(text: "three")]), role: "reviewer", limit: 2, trace: trace)
        let first = try await limited.complete(messages: [ChatMessage(role: "user", content: "Look")], tools: []) { _ in }
        _ = try await limited.complete(messages: [ChatMessage(role: "user", content: "Again")], tools: []) { _ in }
        XCTAssertEqual(first.text, "one")
        do { _ = try await limited.complete(messages: [], tools: []) { _ in }; XCTFail("the third call is past the limit") } catch {}
        let written = try FileManager.default.contentsOfDirectory(atPath: trace.path).sorted()
        XCTAssertEqual(written.count, 2)
        let record = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: trace.appendingPathComponent(written[0]))) as? [String: Any])
        XCTAssertEqual(record["role"] as? String, "reviewer")
        XCTAssertEqual((record["reply"] as? [String: Any])?["text"] as? String, "one")
        XCTAssertEqual(((record["messages"] as? [[String: Any]])?.first)?["content"] as? String, "Look")

        XCTAssertTrue(try Roles.command("", home: home).contains("reviewer  the model doing the work"))
        XCTAssertTrue(try Roles.command("reviewer http://127.0.0.1:8081 judge", home: home).contains("judge at http://127.0.0.1:8081"))
        XCTAssertThrowsError(try Roles.command("visual http://example.com seer", home: home), "a role's model is on this computer, like any other")
        _ = try Roles.command("limit 3", home: home)
        _ = try Roles.command("trace \(trace.path)", home: home)
        let roles = Roles.load(home: home)
        XCTAssertEqual(roles.reviewer?.model, "judge")
        XCTAssertEqual(roles.limit, 3)
        let workspace = try Workspace(path: folder.path)
        let main = ScriptedModel([ModelReply(text: "from the main model")])
        XCTAssertEqual(Roles.reviewer(main: main, workspace: workspace, home: home).name, "judge", "the chosen reviewer reviews")
        XCTAssertTrue(Roles.implementer(main, workspace: workspace, home: home) is RoleModel, "with tracing on, the working model is written down")
        _ = try Roles.command("trace \(folder.appendingPathComponent("t").path)", home: home)
        XCTAssertFalse(Roles.implementer(main, workspace: workspace, home: home) is RoleModel, "a trace folder inside the project is not used")
        _ = try Roles.command("reviewer off", home: home)
        _ = try Roles.command("trace off", home: home)
        let fallback = Roles.reviewer(main: main, workspace: workspace, home: home)
        let answer = try await fallback.complete(messages: [], tools: []) { _ in }
        XCTAssertEqual(answer.text, "from the main model", "without a reviewer the main model reviews")
    }

    func testAJobRunsInTheBackgroundAndStopsWhenTheRequestEnds() async throws {
        var (store, session) = try session(.workspaceEdits)
        var asked: [String] = []
        let agent = Agent(store: store, model: ScriptedModel([]), approve: { asked.append($0.tool); return true }, emit: { _ in })
        let model = ScriptedModel([
            call("start_job", ["command": "for i in 1 2 3; do echo tick $i; sleep 0.1; done"]),
            call("start_job", ["command": "sleep 60"]),
            ModelReply(text: "Started."),
        ])
        let running = Agent(store: store, model: model, approve: { asked.append($0.tool); return true }, emit: { _ in })
        // The first job is polled between the model's steps by a tool call made from the script.
        model.replies.insert(call("poll_job", ["id": "PLACEHOLDER", "wait_ms": "3000"]), at: 1)
        model.onRequest = { number in
            // Fill in the id the first start_job returned.
            if number == 2, let started = model.seen.last?.last?.content, let id = started.split(separator: " ").dropFirst(2).first {
                model.replies[0] = call("poll_job", ["id": String(id.dropLast()), "wait_ms": "3000"])
            }
        }
        let began = Date()
        await running.run(&session, request: "Start them")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(asked, ["start_job", "start_job"], "starting a job asks, like any command outside Auto")
        let polled = session.activities[1].result
        XCTAssertTrue(polled.contains("exited 0") && polled.contains("tick 1") && polled.contains("tick 3"), polled)
        XCTAssertTrue(running.jobs.isEmpty, "every job is stopped when the request ends")
        XCTAssertLessThan(Date().timeIntervalSince(began), 20, "the 60 second job did not hold the request open")
        _ = agent
    }

    func testProjectWorkflowsAndSkillsAreOfferedOnlyWhenTheProjectHasThem() async throws {
        var (store, session) = try session(.workspaceEdits)
        let workspace = try Workspace(path: folder.path)
        XCTAssertFalse(Agent.extraTools(session: session, workspace: workspace).contains { $0.name == "run_workflow" || $0.name == "read_skill" })
        try #"{"version": 1, "workflows": {"stamp": {"executable": "sh", "arguments": ["-c", "printf done > stamp.txt; echo \"it's stamped\""]}}}"#
            .write(to: folder.appendingPathComponent("mason.workflows.json"), atomically: true, encoding: .utf8)
        try FileManager.default.createDirectory(at: folder.appendingPathComponent(".mason/skills/release"), withIntermediateDirectories: true)
        try "---\ndescription: How we cut a release\n---\nTag, then publish.".write(to: folder.appendingPathComponent(".mason/skills/release/SKILL.md"), atomically: true, encoding: .utf8)
        XCTAssertEqual(Agent.extraTools(session: session, workspace: workspace).map(\.name).filter { $0 == "run_workflow" || $0 == "read_skill" }, ["run_workflow", "read_skill"])
        XCTAssertEqual(ProjectWorkflows.commandLine(.init(executable: "sh", arguments: ["-c", "echo it's"], directory: "src")), #"cd 'src' && 'sh' '-c' 'echo it'\''s'"#)

        var asked: [String] = []
        let model = ScriptedModel([call("read_skill", ["name": "release"]), call("run_workflow", ["name": "stamp"]), call("run_workflow", ["name": "missing"]), ModelReply(text: "Done.")])
        await Agent(store: store, model: model, approve: { asked.append($0.subject); return true }, emit: { _ in }).run(&session, request: "Release it")
        XCTAssertTrue(model.seen[0][0].content.contains("release (How we cut a release)"), "the model is told which skills exist")
        XCTAssertTrue(session.activities[0].result.contains("Tag, then publish."))
        XCTAssertEqual(asked, ["workflow stamp"])
        XCTAssertTrue(session.activities[1].result.contains("it's stamped") && session.activities[1].result.contains("stamp.txt"), session.activities[1].result)
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed, .failed])
        XCTAssertEqual(session.turns[0].changes.map(\.path), ["stamp.txt"], "what a workflow changes can be undone")

        var reading = try store.create(workspace: session.workspace, permission: .readOnly)
        reading.askOnly = true
        // check_page is offered wherever a browser is installed, which depends on the computer.
        let offered = Agent.extraTools(session: reading, workspace: workspace).map(\.name).filter { $0 != "check_page" && $0 != "inspect_symbol" }
        XCTAssertEqual(offered, ["read_lines", "git_status", "delegate_readonly", "repository_map", "ask_user", "navigate_code", "read_skill"], "a read-only session gets no editing, job or workflow tools")
        try "{\"version\": 2}".write(to: folder.appendingPathComponent("mason.workflows.json"), atomically: true, encoding: .utf8)
        XCTAssertThrowsError(try ProjectWorkflows.load(workspace))
    }

    func testAQuestionStopsTheRequestUntilTheUserAnswers() async throws {
        var (store, session) = try session(.readOnly)
        let model = ScriptedModel([call("ask_user", ["question": "Which currency should totals use?"]), ModelReply(text: "Never reached in the first request."), ModelReply(text: "Using EUR.")])
        model.replies.remove(at: 1)
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Add totals")
        XCTAssertEqual(session.state, "Needs your answer")
        XCTAssertEqual(session.turns[0].answer, "Which currency should totals use?")
        XCTAssertEqual(model.seen.count, 1, "the model is not asked again until the user replies")
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "EUR")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(session.turns[1].answer, "Using EUR.")
        XCTAssertEqual(model.seen[1].last?.content, "EUR")
        XCTAssertTrue(model.seen[1].contains { $0.role == "tool" && $0.content.hasPrefix("Asked the user") }, "the conversation stays valid for the model")
    }

    func testTheRepositoryMapListsWhatEachFileDefines() throws {
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("src"), withIntermediateDirectories: true)
        try "export function total(items) {}\nclass Cart {}\nconst TAX = 0.2\n".write(to: folder.appendingPathComponent("src/cart.ts"), atomically: true, encoding: .utf8)
        try "public struct Money {}\nfunc format(_ value: Money) -> String { \"\" }\n".write(to: folder.appendingPathComponent("src/Money.swift"), atomically: true, encoding: .utf8)
        try "just words\n".write(to: folder.appendingPathComponent("notes.txt"), atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: folder.path)
        let map = try RepositoryMap.outline(workspace)
        XCTAssertTrue(map.contains("calc.py  (3 lines): add"), map)
        XCTAssertTrue(map.contains("src/cart.ts  (4 lines): total, Cart, TAX"), map)
        XCTAssertTrue(map.contains("src/Money.swift  (3 lines): Money, format"), map)
        XCTAssertTrue(map.contains("notes.txt  (2 lines)") && !map.contains("notes.txt  (2 lines):"), "a file with nothing to outline is still listed")
        XCTAssertFalse(try RepositoryMap.outline(workspace, under: "src").contains("calc.py"))
    }

    // MARK: verification

    /// A project whose verify workflow passes when calc.py contains `needle`, with the rule that lets Mason run it.
    func verifying(_ needle: String, allowed: Bool = true) throws -> (SessionStore, Session) {
        let manifest = #"{"version": 1, "workflows": {"verify": {"executable": "grep", "arguments": ["-q", "NEEDLE", "calc.py"]}}}"#.replacingOccurrences(of: "NEEDLE", with: needle)
        try manifest.write(to: folder.appendingPathComponent("mason.workflows.json"), atomically: true, encoding: .utf8)
        let (store, session) = try session(.workspaceEdits)
        if allowed {
            var rules = ApprovalRules(home: home, workspace: session.workspace)
            try rules.add(tool: "run_workflow", prefix: "workflow verify")
        }
        return (store, session)
    }

    func testARegressionIsHandedBackToTheModelAndTheReceiptRecordsTheRepair() async throws {
        var (store, session) = try verifying("def add")
        let model = ScriptedModel([
            call("write_file", ["path": "calc.py", "content": "def plus(a, b):\n    return a + b\n"]), ModelReply(text: "Renamed it."),
            call("write_file", ["path": "calc.py", "content": "def add(a, b):\n    return a + b\n"]), ModelReply(text: "Kept the name and fixed the sum."),
        ])
        var statuses: [String] = []
        await Agent(store: store, model: model, approve: { _ in true }, emit: { if case .status(let text) = $0 { statuses.append(text) } }).run(&session, request: "Fix add")
        XCTAssertEqual(session.state, "Completed")
        let receipt = try XCTUnwrap(session.turns[0].verification)
        XCTAssertEqual(receipt.verdict, .passed)
        XCTAssertEqual(receipt.repairs, 1)
        XCTAssertEqual(receipt.baselineExitCode, 0)
        XCTAssertEqual(session.turns[0].answer, "Kept the name and fixed the sum.")
        XCTAssertTrue(model.seen[2].last!.content.contains("It passed before your changes and fails now"), "the model is told what Mason found")
        XCTAssertEqual(statuses.filter { $0 == "Verifying" }.count, 3, "once before the first change, and once after each answer")
        XCTAssertEqual(receipt.summary, "verify passed")
    }

    func testTheReceiptTellsARegressionFromAFailureThatWasAlreadyThere() async throws {
        // It failed before and still fails: not the request's doing, so the model is not sent back.
        var (store, session) = try verifying("never-in-the-file")
        let edit = [call("write_file", ["path": "calc.py", "content": "def add(a, b):\n    return a + b\n"]), ModelReply(text: "Done.")]
        let before = ScriptedModel(edit)
        await Agent(store: store, model: before, approve: { _ in true }, emit: { _ in }).run(&session, request: "Fix add")
        XCTAssertEqual(session.turns[0].verification?.verdict, .failingBefore)
        XCTAssertEqual(before.seen.count, 2)

        // It failed before and passes after.
        _ = try Agent.undoLatest(&session, store: store)
        (store, session) = try verifying("a + b")
        await Agent(store: store, model: ScriptedModel(edit), approve: { _ in true }, emit: { _ in }).run(&session, request: "Fix add")
        XCTAssertEqual(session.turns.last?.verification?.verdict, .fixed)
        XCTAssertEqual(session.turns.last?.verification?.summary, "verify passes now; it failed before this request")

        // A regression the model cannot repair is recorded as one, after two attempts.
        (store, session) = try verifying("a + b")
        let stubborn = ScriptedModel((1...3).flatMap { number in [call("write_file", ["path": "calc.py", "content": "broken \(number)\n"]), ModelReply(text: "Attempt \(number).")] })
        await Agent(store: store, model: stubborn, approve: { _ in true }, emit: { _ in }).run(&session, request: "Break it")
        XCTAssertEqual(session.turns.last?.verification?.verdict, .regression)
        XCTAssertEqual(session.turns.last?.verification?.repairs, 2)
        XCTAssertEqual(session.state, "Completed")
    }

    func testVerificationNeedsPermissionAndAChange() async throws {
        // Without Auto or a saved rule, Mason does not run it, and says so.
        var (store, session) = try verifying("def add", allowed: false)
        await Agent(store: store, model: ScriptedModel([call("write_file", ["path": "calc.py", "content": "x\n"]), ModelReply(text: "Done.")]), approve: { _ in true }, emit: { _ in }).run(&session, request: "Change it")
        XCTAssertEqual(session.turns[0].verification?.verdict, .notRun)
        // A request that changes nothing has nothing to verify.
        (store, session) = try verifying("def add")
        await Agent(store: store, model: ScriptedModel([call("read_file", ["path": "calc.py"]), ModelReply(text: "It adds.")]), approve: { _ in true }, emit: { _ in }).run(&session, request: "Explain it")
        XCTAssertNil(session.turns.last?.verification)
        // A project without a verify workflow gets no receipt.
        try FileManager.default.removeItem(at: folder.appendingPathComponent("mason.workflows.json"))
        await Agent(store: store, model: ScriptedModel([call("write_file", ["path": "calc.py", "content": "y\n"]), ModelReply(text: "Done.")]), approve: { _ in true }, emit: { _ in }).run(&session, request: "Change it")
        XCTAssertNil(session.turns.last?.verification)
        XCTAssertNotNil(try SessionStore(home: home).sessions.first)
    }

    // MARK: queue and steer

    func testAQueuedMessageRunsAfterTheRequestCompletes() async throws {
        var (store, session) = try session(.readOnly)
        let model = ScriptedModel([ModelReply(text: "One."), ModelReply(text: "Two.")])
        let agent = Agent(store: store, model: model, approve: { _ in true }, emit: { _ in })
        model.onRequest = { number in if number == 1 { try? agent.sendWhileRunning("second question", steer: false) } }
        await agent.run(&session, request: "first question")
        XCTAssertEqual(session.turns.map(\.request), ["first question", "second question"])
        XCTAssertEqual(session.turns.map(\.answer), ["One.", "Two."])
        XCTAssertNil(session.pendingInput)
        XCTAssertThrowsError(try agent.sendWhileRunning("too late", steer: false), "nothing is running any more")
    }

    func testAQueuedMessageWaitsAfterAFailureAndQueueResumesIt() async throws {
        var (store, session) = try session(.readOnly)
        let failing = ScriptedModel([])
        let agent = Agent(store: store, model: failing, approve: { _ in true }, emit: { _ in })
        failing.onRequest = { _ in try? agent.sendWhileRunning("do this next", steer: false) }
        await agent.run(&session, request: "first question")
        XCTAssertEqual(session.state, "Failed")
        XCTAssertEqual(session.pendingInput?.map(\.text), ["do this next"], "a queued message does not run after a failure")
        XCTAssertEqual(try SessionStore(home: home).sessions[0].pendingInput?.count, 1, "and it is saved")

        await Agent(store: store, model: ScriptedModel([ModelReply(text: "Done next.")]), approve: { _ in true }, emit: { _ in }).run(&session, request: "/queue")
        XCTAssertEqual(session.turns.last?.request, "do this next")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertNil(session.pendingInput)
        await Agent(store: store, model: ScriptedModel([]), approve: { _ in true }, emit: { _ in }).run(&session, request: "/queue")
        XCTAssertEqual(session.detail, "No messages are queued.")
    }

    func testSteeringInterruptsTheRequestAndIsHandledNext() async throws {
        var (store, session) = try session(.readOnly)
        // The first request is interrupted while the model is still working, so it never takes a reply from the script.
        let model = ScriptedModel([ModelReply(text: "Steered answer.")])
        model.delay = 300_000_000
        let agent = Agent(store: store, model: model, approve: { _ in true }, emit: { _ in })
        model.onRequest = { number in if number == 1 { try? agent.sendWhileRunning("actually, do this instead", steer: true) } }
        let started = Date()
        await agent.run(&session, request: "long task")
        XCTAssertEqual(session.turns.map(\.request), ["long task", "actually, do this instead"])
        XCTAssertEqual(session.turns[0].answer, "", "the interrupted request has no answer")
        XCTAssertEqual(session.turns[1].answer, "Steered answer.")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(model.seen[1].filter { $0.role == "user" }.count, 2, "the model sees both requests in order")
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)
    }

    // MARK: MCP servers

    static let python: String? = ["/usr/bin/python3", "/usr/local/bin/python3", "/opt/homebrew/bin/python3"].first { FileManager.default.isExecutableFile(atPath: $0) }
    static let testServer = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("mcp-test-server.py").path

    func testMcpRegistrationsAreValidatedAndReadLikeTheWindowsFile() throws {
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try #"[{"Name":"docs","Endpoint":"http://127.0.0.1:8088/mcp","Tools":["search_docs"]},{"name":"off","endpoint":"http://127.0.0.1:9/mcp","tools":[],"enabled":false}]"#
            .write(to: home.appendingPathComponent("mcp-servers.json"), atomically: true, encoding: .utf8)
        XCTAssertEqual(try Mcp.all(home: home).map(\.name), ["docs", "off"])
        XCTAssertEqual(Mcp.enabled(home: home), [McpServer(name: "docs", endpoint: "http://127.0.0.1:8088/mcp", tools: ["search_docs"])])
        for bad in ["http://example.com/mcp", "https://127.0.0.1/mcp", "http://127.0.0.1.evil.example/mcp", "http://user@127.0.0.1/mcp", "http://localhost:1/mcp"] {
            XCTAssertThrowsError(try Mcp.validate(McpServer(name: "x", endpoint: bad)), bad)
        }
        XCTAssertThrowsError(try Mcp.validate(McpServer(name: "x", executable: "relative/program")))
        XCTAssertThrowsError(try Mcp.validate(McpServer(name: "a/b", endpoint: "http://127.0.0.1:1/mcp")))
        XCTAssertThrowsError(try Mcp.save([McpServer(name: "same", endpoint: "http://127.0.0.1:1/mcp"), McpServer(name: "same", endpoint: "http://127.0.0.1:2/mcp")], home: home))
        try Mcp.save([McpServer(name: "docs", endpoint: "http://127.0.0.1:8088/mcp", tools: ["a"])], home: home)
        XCTAssertTrue(try String(contentsOf: home.appendingPathComponent("mcp-servers.json"), encoding: .utf8).contains("\"Name\""), "written with the keys Windows uses")
    }

    func testAnMcpProgramIsListedCalledAndHeldToItsAllowlist() async throws {
        let python = try XCTUnwrap(Self.python, "python3 is needed for the MCP test server")
        var server = McpServer(name: "test", tools: ["echo", "fail"], executable: python, arguments: [Self.testServer, "stdio"], resources: ["note://hello"])
        let offered = try await Mcp.discover(server)
        XCTAssertEqual(offered.map(\.name), ["echo", "fail"])
        let echoed = try await Mcp.request(server, .call(tool: "echo", arguments: ["text": "hi"]))
        XCTAssertTrue(echoed.contains("echo: hi"))
        let note = try await Mcp.request(server, .read(uri: "note://hello"))
        XCTAssertTrue(note.contains("hello from the test server"))
        do { _ = try await Mcp.request(server, .call(tool: "fail", arguments: [:])); XCTFail("a tool failure must be an error") } catch { XCTAssertTrue("\(error)".contains("it broke")) }
        server.tools = ["fail"]
        let visible = try await Mcp.request(server, .tools(all: false))
        XCTAssertFalse(visible.contains("\"echo\""), "the agent only sees allowed tools")
        do { _ = try await Mcp.request(server, .call(tool: "echo", arguments: ["text": "hi"])); XCTFail("a tool outside the allowlist must be refused") } catch {}
        do { _ = try await Mcp.request(server, .read(uri: "note://other")); XCTFail("a resource outside the allowlist must be refused") } catch {}

        // A program that exits without answering is an error straight away, not a wait.
        let silent = McpServer(name: "silent", executable: python, arguments: ["-c", "pass"])
        let started = Date()
        do { _ = try await Mcp.discover(silent); XCTFail("a server that says nothing must be an error") } catch { XCTAssertTrue("\(error)".contains("stopped before answering"), "\(error)") }
        XCTAssertLessThan(Date().timeIntervalSince(started), 10)
    }

    func testAnMcpServerOverHttpAnswersInJsonAndAsAnEventStream() async throws {
        let python = try XCTUnwrap(Self.python, "python3 is needed for the MCP test server")
        let port = 18600 + Int.random(in: 0..<300)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: python); process.arguments = [Self.testServer, "http", String(port)]
        process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
        try process.run()
        defer { process.terminate() }
        let server = McpServer(name: "web", endpoint: "http://127.0.0.1:\(port)/mcp", tools: ["echo"])
        var offered: [McpTool] = []
        for _ in 0..<40 { if let found = try? await Mcp.discover(server) { offered = found; break }; try await Task.sleep(nanoseconds: 150_000_000) }
        XCTAssertEqual(offered.map(\.name), ["echo", "fail"], "the session id from initialize is sent with later requests")
        let echoed = try await Mcp.request(server, .call(tool: "echo", arguments: ["text": "over http"]))
        XCTAssertTrue(echoed.contains("echo: over http"), "a reply sent as an event stream is read")
    }

    func testTheAgentCallsAnMcpToolOnlyAfterTheUserAllowsIt() async throws {
        let python = try XCTUnwrap(Self.python, "python3 is needed for the MCP test server")
        var (store, session) = try session(.auto)
        try Mcp.save([McpServer(name: "test", tools: ["echo"], executable: python, arguments: [Self.testServer, "stdio"])], home: home)
        var asked: [ApprovalRequest] = []
        let model = ScriptedModel([call("mcp_call", ["server": "test", "tool": "echo", "arguments_json": "{\"text\":\"from the agent\"}"]), ModelReply(text: "Called it.")])
        await Agent(store: store, model: model, approve: { asked.append($0); return true }, emit: { _ in }).run(&session, request: "Use the test server")
        XCTAssertEqual(asked.map(\.subject), ["test/echo"], "even in Auto an MCP call asks, because it is not sandboxed")
        XCTAssertTrue(asked[0].detail.contains("from the agent"))
        XCTAssertEqual(session.activities.last?.state, .completed)
        XCTAssertTrue(session.activities.last!.result.contains("echo: from the agent"))
        XCTAssertTrue(model.seen[0][0].content.contains("MCP servers: test"))

        let declined = ScriptedModel([call("mcp_call", ["server": "test", "tool": "echo", "arguments_json": "{}"]), call("mcp_call", ["server": "test", "tool": "fail", "arguments_json": "{}"]), ModelReply(text: "Stopped.")])
        await Agent(store: store, model: declined, approve: { _ in false }, emit: { _ in }).run(&session, request: "Again")
        XCTAssertEqual(session.activities.suffix(2).map(\.state), [.denied, .failed], "a declined call is not made, and a tool outside the allowlist is refused without asking")
    }

    // MARK: compaction

    func testCompactionReplacesOlderMessagesWithASummaryAndKeepsThem() async throws {
        var (store, session) = try session(.readOnly)
        session.messages = [ChatMessage(role: "system", content: "sys"), ChatMessage(role: "user", content: "first"),
                            ChatMessage(role: "assistant", content: "", toolCalls: [ToolCall(id: "a", name: "read_file", arguments: "{\"path\":\"calc.py\"}")]),
                            ChatMessage(role: "tool", content: "1\tdef add", toolCallId: "a"), ChatMessage(role: "assistant", content: "It subtracts."),
                            ChatMessage(role: "user", content: "second"),
                            ChatMessage(role: "assistant", content: "", toolCalls: [ToolCall(id: "b", name: "read_file", arguments: "{}")]),
                            ChatMessage(role: "tool", content: "more", toolCallId: "b")]
        XCTAssertEqual(Compaction.cut(session.messages, keep: 1), 6, "a tool result stays with the call that asked for it")
        let model = ScriptedModel([ModelReply(text: "The user asked about calc.py; add subtracts.")])
        let compacted = try await Compaction.compact(&session, model: model, keep: 1)
        XCTAssertTrue(compacted)
        XCTAssertEqual(session.messages.map(\.role), ["system", "user", "assistant", "tool"])
        XCTAssertTrue(session.messages[1].content.contains("add subtracts"))
        XCTAssertEqual(session.archived?.count, 5)
        XCTAssertTrue(model.seen[0][1].content.contains("calls read_file"))

        // A manual compaction keeps nothing; the summary travels with the next request.
        session.messages += [ChatMessage(role: "assistant", content: "Done.")]
        let again = try await Compaction.compact(&session, model: ScriptedModel([ModelReply(text: "All of it.")]), keep: 0)
        XCTAssertTrue(again)
        XCTAssertEqual(session.messages.map(\.role), ["system"])
        XCTAssertTrue(session.pendingNote?.contains("All of it.") == true)
        XCTAssertEqual(session.compactions, 2)
        _ = store
    }

    func testARunCompactsByItselfWhenTheContextIsNearlyFull() async throws {
        var (store, session) = try session(.readOnly)
        let first = ScriptedModel((1...4).map { ModelReply(text: "Answer \($0).") })
        for number in 1...4 { await Agent(store: store, model: first, approve: { _ in true }, emit: { _ in }).run(&session, request: "question \(number)") }
        session.promptTokens = 900
        XCTAssertTrue(Compaction.needed(session, window: 1000))
        XCTAssertFalse(Compaction.needed(session, window: 4000))
        XCTAssertFalse(Compaction.needed(session, window: nil))
        let model = ScriptedModel([ModelReply(text: "Earlier: two questions were answered."), ModelReply(text: "Five.")])
        model.window = 1000
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "fifth question")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(session.compactions, 1)
        XCTAssertTrue(model.seen[1].contains { $0.content.contains("two questions were answered") }, "the model continues from the summary")
        XCTAssertTrue(model.seen[1].last!.content.contains("fifth question"))
        XCTAssertLessThan(model.seen[1].count, 10)
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

final class BrowserTests: XCTestCase {
    var folder: URL!
    override func setUpWithError() throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("mason-browser-tests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try """
            <!doctype html><html><head><title>Cart</title></head><body>
            <h1>Cart</h1><label for="item">Item</label> <input id="item"><button id="add">Add item</button><ul id="list"></ul><p id="count">0 items</p>
            <script src="cart.js"></script></body></html>
            """.write(to: folder.appendingPathComponent("index.html"), atomically: true, encoding: .utf8)
        try """
            var items = [];
            document.getElementById('add').addEventListener('click', function () {
              var name = document.getElementById('item').value || 'unnamed';
              items.push(name);
              var row = document.createElement('li'); row.textContent = name; document.getElementById('list').appendChild(row);
              document.getElementById('count').textContent = items.length + (items.length === 1 ? ' item' : ' items');
            });
            """.write(to: folder.appendingPathComponent("cart.js"), atomically: true, encoding: .utf8)
        try "<!doctype html><title>Broken</title><p>before</p><script>missingFunction();</script>".write(to: folder.appendingPathComponent("broken.html"), atomically: true, encoding: .utf8)
        try "SECRET=1".write(to: folder.appendingPathComponent(".env"), atomically: true, encoding: .utf8)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: folder) }

    func testTheServerSendsOnlyProjectFilesAndRecordsErrorsFromTheFirstScript() throws {
        let workspace = try Workspace(path: folder.path)
        let (status, kind, body) = BrowserCheck.respond(to: "/index.html?x=1", workspace: workspace)
        let page = String(decoding: body, as: UTF8.self)
        XCTAssertEqual(status, 200)
        XCTAssertTrue(kind.hasPrefix("text/html"))
        XCTAssertTrue(page.hasPrefix("<!doctype html><script>window.__masonErrors=[]"), "the recorder comes right after the doctype")
        XCTAssertEqual(BrowserCheck.respond(to: "/cart.js", workspace: workspace).1, "text/javascript; charset=utf-8")
        XCTAssertEqual(BrowserCheck.respond(to: "/", workspace: workspace).0, 200, "a folder serves its index.html")
        for outside in ["/.env", "/../outside.txt", "/%2e%2e/outside.txt", "/missing.html"] {
            XCTAssertEqual(BrowserCheck.respond(to: outside, workspace: workspace).0, 404, outside)
        }
        XCTAssertTrue(String(decoding: BrowserCheck.respond(to: "/__mason/harness?page=/index.html", workspace: workspace).2, as: UTF8.self).contains("mason-result"))
    }

    func testAPageIsClickedFilledAndReadInARealBrowser() async throws {
        try XCTSkipIf(BrowserCheck.findBrowser() == nil, "no Chromium-family browser on this machine")
        let workspace = try Workspace(path: folder.path)
        let result = try await BrowserCheck.run(workspace, page: "index.html", steps: [
            ["action": "expect_text", "text": "0 items"],
            ["action": "fill", "selector": "#item", "value": "Wrench"],
            ["action": "click", "text": "Add item"],
            ["action": "expect_text", "text": "1 item"],
            ["action": "fill", "text": "Item", "value": "Hammer"],
            ["action": "click", "selector": "#add"],
            ["action": "expect_text", "text": "2 items"],
            ["action": "expect_no_text", "text": "unnamed"],
        ])
        XCTAssertTrue(result.passed, BrowserCheck.report(result, page: "index.html"))
        XCTAssertEqual(result.title, "Cart")
        XCTAssertTrue(result.controls.count >= 2 && result.controls.allSatisfy { $0.components(separatedBy: " | ").count == 3 }, "\(result.controls)")
        XCTAssertTrue(result.text.contains("Wrench") && result.text.contains("Hammer"))
        XCTAssertEqual(result.steps.count, 8)

        // A step that fails stops the run and says why.
        let missing = try await BrowserCheck.run(workspace, page: "index.html", steps: [["action": "click", "text": "Checkout"], ["action": "expect_text", "text": "never reached"]])
        XCTAssertFalse(missing.passed)
        XCTAssertEqual(missing.steps.count, 1)
        XCTAssertTrue(missing.steps[0].detail.contains("nothing on the page matches"))

        // A script error on the page fails the check even with no steps.
        let broken = try await BrowserCheck.run(workspace, page: "broken.html")
        XCTAssertFalse(broken.passed)
        XCTAssertTrue(broken.errors.contains { $0.contains("missingFunction") }, "\(broken.errors)")
        XCTAssertTrue(BrowserCheck.report(broken, page: "broken.html").hasPrefix("FAILED: broken.html"))
    }

    func testOnlyProjectPagesCanBeChecked() async throws {
        let workspace = try Workspace(path: folder.path)
        for bad in ["cart.js", "missing.html", "../index.html"] {
            do { _ = try await BrowserCheck.run(workspace, page: bad); XCTFail("\(bad) must be refused") } catch {}
        }
    }
}

final class ScheduleTests: XCTestCase {
    var base: URL!, home: URL!, project: URL!
    var calendar: Calendar = { var c = Calendar(identifier: .gregorian); c.timeZone = TimeZone(identifier: "UTC")!; return c }()
    let endpoint = ModelEndpoint(url: "http://127.0.0.1:1/", model: "scripted")
    override func setUpWithError() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-schedule-" + UUID().uuidString)
        home = base.appendingPathComponent("home"); project = base.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        try "x = 1\n".write(to: project.appendingPathComponent("a.py"), atomically: true, encoding: .utf8)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: base) }

    func date(_ text: String) -> Date { ISO8601DateFormatter().date(from: text)! }

    func testNextRunFollowsTheCadence() {
        func schedule(_ cadence: Schedule.Cadence, _ hour: Int, _ minute: Int, weekday: Int = 1) -> Schedule {
            Schedule(name: "s", workspace: project.path, prompt: "p", endpoint: endpoint, auto: false, cadence: cadence, hour: hour, minute: minute, weekday: weekday)
        }
        // 2026-11-27 is a Friday.
        let friday = date("2026-11-27T10:30:00Z")
        XCTAssertEqual(schedule(.hourly, 0, 45).next(after: friday, calendar: calendar), date("2026-11-27T10:45:00Z"))
        XCTAssertEqual(schedule(.hourly, 0, 15).next(after: friday, calendar: calendar), date("2026-11-27T11:15:00Z"))
        XCTAssertEqual(schedule(.daily, 9, 0).next(after: friday, calendar: calendar), date("2026-11-28T09:00:00Z"))
        XCTAssertEqual(schedule(.daily, 18, 0).next(after: friday, calendar: calendar), date("2026-11-27T18:00:00Z"))
        XCTAssertEqual(schedule(.weekdays, 9, 0).next(after: friday, calendar: calendar), date("2026-11-30T09:00:00Z"), "the weekend is skipped")
        XCTAssertEqual(schedule(.weekly, 9, 0, weekday: 3).next(after: friday, calendar: calendar), date("2026-12-02T09:00:00Z"), "the next Wednesday")
        XCTAssertEqual(schedule(.weekly, 9, 0, weekday: 3).summary, "Every Wednesday at 09:00")
        XCTAssertEqual(schedule(.hourly, 0, 5).summary, "Every hour at :05")
    }

    func testTheScheduleCommandAddsListsSwitchesAndRemoves() throws {
        let now = date("2026-11-27T10:30:00Z")
        XCTAssertTrue(try Schedules.command("", home: home, workspace: project.path, endpoint: endpoint).hasPrefix("No schedules."))
        let added = try Schedules.command("add tidy weekly wed 09:00 auto -- Run the linter -- and fix what it finds", home: home, workspace: project.path, endpoint: endpoint, now: now)
        XCTAssertTrue(added.contains("every wednesday at 09:00") && added.contains("Auto"))
        _ = try Schedules.command("add report hourly :15 -- Summarise the open TODOs", home: home, workspace: project.path, endpoint: endpoint, now: now)
        let saved = Schedules.load(home: home)
        XCTAssertEqual(saved.map(\.name), ["tidy", "report"])
        XCTAssertEqual(saved[0].prompt, "Run the linter -- and fix what it finds")
        XCTAssertTrue(saved[0].auto && !saved[1].auto)
        XCTAssertEqual(saved[1].cadence, .hourly)
        XCTAssertTrue(try Schedules.command("list", home: home, workspace: project.path, endpoint: endpoint, now: now).contains("report  ·  Every hour at :15  ·  read-only"))

        for bad in ["add tidy daily 09:00 -- again", "add x daily 25:00 -- p", "add x weekly 09:00 -- p", "add x daily 09:00", "add two words daily 09:00 -- p", "remove nothing", "nonsense"] {
            XCTAssertThrowsError(try Schedules.command(bad, home: home, workspace: project.path, endpoint: endpoint, now: now), bad)
        }
        XCTAssertThrowsError(try Schedules.command("add x daily 09:00 -- p", home: home, workspace: project.path, endpoint: nil), "a schedule needs a model")
        XCTAssertEqual(try Schedules.command("off tidy", home: home, workspace: project.path, endpoint: endpoint), "tidy is off.")
        XCTAssertFalse(Schedules.load(home: home)[0].enabled)
        XCTAssertEqual(try Schedules.command("remove report", home: home, workspace: project.path, endpoint: endpoint), "Removed report.")
        XCTAssertEqual(Schedules.load(home: home).map(\.name), ["tidy"])
    }

    func testADueScheduleRunsOnceUnattendedAndRecordsItsOutcome() async throws {
        let created = date("2026-11-27T10:30:00Z")
        _ = try Schedules.command("add report hourly :45 -- Read a.py and report", home: home, workspace: project.path, endpoint: endpoint, now: created)
        XCTAssertTrue(Schedules.due(Schedules.load(home: home), now: date("2026-11-27T10:40:00Z"), calendar: calendar).isEmpty)
        // Three hours pass with nothing checking: the schedule is due once.
        let later = date("2026-11-27T13:50:00Z")
        let due = Schedules.due(Schedules.load(home: home), now: later, calendar: calendar)
        XCTAssertEqual(due.map(\.name), ["report"])

        let store = try SessionStore(home: home)
        let model = ScriptedModel([call("read_file", ["path": "a.py"]), call("write_file", ["path": "a.py", "content": "x = 2\n"]), ModelReply(text: "a.py sets x to 1.")])
        let ran = await Schedules.run(due[0], home: home, store: store, model: model)
        let session = try XCTUnwrap(ran)
        XCTAssertEqual(session.title, "Scheduled: report")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(session.activities.map(\.state), [.completed, .failed], "a read-only scheduled run cannot edit")
        XCTAssertEqual(try String(contentsOf: project.appendingPathComponent("a.py"), encoding: .utf8), "x = 1\n")

        let after = Schedules.load(home: home)[0]
        XCTAssertEqual(after.lastOutcome, "Completed")
        XCTAssertEqual(after.lastSession, session.id)
        XCTAssertNotNil(after.lastRun)
        XCTAssertGreaterThan(after.next(after: after.lastRun!, calendar: calendar), after.lastRun!, "the missed hours are not made up: the next run is in the future")

        // A model that is not answering is recorded as skipped, and no session is made.
        let skipped = await Schedules.run(after, home: home, store: store)
        XCTAssertNil(skipped)
        XCTAssertTrue(Schedules.load(home: home)[0].lastOutcome.hasPrefix("Skipped:"))
    }
}

final class ChatTests: XCTestCase {
    var home: URL!
    override func setUpWithError() throws { home = FileManager.default.temporaryDirectory.appendingPathComponent("mason-chat-" + UUID().uuidString) }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: home) }

    func testAChatIsSavedCarriesAttachmentsAndOffersNoTools() async throws {
        let store = try ChatStore(home: home)
        var chat = try store.create()
        let notes = home.appendingPathComponent("notes.txt")
        try "line one\n```\nfenced\n```".write(to: notes, atomically: true, encoding: .utf8)
        let attachment = try ChatAttachment(file: notes)
        let model = ScriptedModel([ModelReply(text: "It has a fenced block."), ModelReply(text: "Second answer.")])
        var streamed = ""
        try await Chat.send(&chat, text: "What is in this file?", attachments: [attachment], model: model, store: store) { streamed += $0 }
        XCTAssertEqual(streamed, "It has a fenced block.")
        XCTAssertEqual(chat.title, "What is in this file?")
        XCTAssertEqual(chat.messages.map(\.role), ["system", "user", "assistant"])
        XCTAssertTrue(chat.messages[1].content.contains("Attached file notes.txt:\n````\nline one"), "a file that contains a fence is wrapped in a longer one")
        XCTAssertTrue(chat.messages[0].content.contains("no tools"))
        try await Chat.send(&chat, text: "And again?", model: model, store: store) { _ in }
        XCTAssertEqual(model.seen[1].map(\.role), ["system", "user", "assistant", "user"])

        let reloaded = try ChatStore(home: home)
        XCTAssertEqual(reloaded.conversations.count, 1)
        XCTAssertEqual(reloaded.conversations[0].messages.count, 5)
        XCTAssertEqual(reloaded.conversations[0].model, "scripted")
        reloaded.delete(chat.id)
        XCTAssertTrue(try ChatStore(home: home).conversations.isEmpty)
    }

    func testAttachmentsMustBeSmallTextAndAFailedRequestKeepsTheQuestion() async throws {
        let store = try ChatStore(home: home)
        let binary = home.appendingPathComponent("image.bin"), large = home.appendingPathComponent("large.txt")
        try Data([0xFF, 0xFE, 0x00, 0x01]).write(to: binary)
        try String(repeating: "x", count: ChatAttachment.maxBytes + 1).write(to: large, atomically: true, encoding: .utf8)
        XCTAssertThrowsError(try ChatAttachment(file: binary))
        XCTAssertThrowsError(try ChatAttachment(file: large))

        var chat = try store.create()
        do { try await Chat.send(&chat, text: "Hello?", model: ScriptedModel([]), store: store) { _ in }; XCTFail("a model that fails must be an error") } catch {}
        XCTAssertEqual(chat.messages.map(\.role), ["system", "user"], "the question is kept")
        try await Chat.send(&chat, text: "Still there?", model: ScriptedModel([ModelReply(text: "Yes.")]), store: store) { _ in }
        XCTAssertEqual(chat.messages.map(\.role), ["system", "user", "assistant"], "the next message joins the unanswered one")
        XCTAssertTrue(chat.messages[1].content.contains("Hello?") && chat.messages[1].content.contains("Still there?"))
        do { try await Chat.send(&chat, text: "   ", model: ScriptedModel([]), store: store) { _ in }; XCTFail("an empty message must be refused") } catch {}
    }

    func testALongChatIsCompactedBeforeTheNextAnswer() async throws {
        let store = try ChatStore(home: home)
        var chat = try store.create()
        let model = ScriptedModel((1...4).map { ModelReply(text: "Answer \($0).") })
        for number in 1...4 { try await Chat.send(&chat, text: "Question \(number)", model: model, store: store) { _ in } }
        chat.promptTokens = 900
        let full = ScriptedModel([ModelReply(text: "Earlier: four questions were answered."), ModelReply(text: "Answer 5.")])
        full.window = 1000
        try await Chat.send(&chat, text: "Question 5", model: full, store: store) { _ in }
        XCTAssertEqual(chat.compactions, 1)
        XCTAssertTrue(full.seen[1].contains { $0.content.contains("four questions were answered") })
        XCTAssertTrue(full.seen[1].last!.content.contains("Question 5"))
        XCTAssertFalse(chat.archived?.isEmpty ?? true, "the replaced messages are kept")
    }
}

/// Embeds text as counts of a few known words, so ranking is predictable.
struct WordModels: SemanticModels {
    static let words = ["invoice", "total", "tax", "login", "password", "color"]
    let identity = "words"
    var canRerank = false
    var calls = Counter()
    final class Counter { var texts = 0 }
    func embed(_ texts: [String]) async throws -> [[Float]] {
        calls.texts += texts.count
        return texts.map { text in WordModels.words.map { word in Float(text.lowercased().components(separatedBy: word).count - 1) } + [0.01] }
    }
    func rerank(_ query: String, documents: [String]) async throws -> [Double] { documents.map { $0.contains("File: auth.py") ? 9 : 0 } }
}

final class PlanningTests: XCTestCase {
    var folder: URL!
    var home: URL!

    override func setUpWithError() throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-plan-" + UUID().uuidString)
        folder = base.appendingPathComponent("project"); home = base.appendingPathComponent("home")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try write("calc.py", "def add(a, b):\n    return a - b\n")
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: folder.deletingLastPathComponent()) }
    func write(_ name: String, _ text: String) throws { try text.write(to: folder.appendingPathComponent(name), atomically: true, encoding: .utf8) }

    func testCommentsTravelWithTheNextRequestWhileTheirFilesAreUnchanged() async throws {
        let workspace = try Workspace(path: folder.path)
        let store = try SessionStore(home: home)
        var session = try store.create(workspace: workspace.root.path, permission: .workspaceEdits)
        XCTAssertThrowsError(try Review.comment(workspace, path: "calc.py", line: 3, note: "x"))
        XCTAssertThrowsError(try Review.comment(workspace, path: "calc.py", line: 1, note: "  "))
        let comment = try Review.comment(workspace, path: "calc.py", line: 2, note: "This subtracts.")
        XCTAssertEqual(comment.quote, "    return a - b")
        let removed = try Review.comment(workspace, path: "calc.py", line: 7, note: "Why was this removed?", removed: "old()")
        XCTAssertEqual(removed.location, "calc.py · original line 7")
        session.comments = [comment, removed]

        let model = ScriptedModel([ModelReply(text: "Fixed."), ModelReply(text: "Again.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Fix it")
        let sent = model.seen[0].last!.content
        XCTAssertTrue(sent.hasPrefix("Fix it\ncalc.py · updated line 2 (return a - b) — This subtracts.\ncalc.py · original line 7 (old()) — Why was this removed?"), sent)
        XCTAssertNil(session.comments, "sent comments are not sent twice")
        XCTAssertEqual(session.turns[0].request, sent)

        // Comments alone are a request; a comment on a file that changed since is refused, and kept.
        session.comments = [try Review.comment(workspace, path: "calc.py", line: 1, note: "Rename it.")]
        try write("calc.py", "def add(a, b):\n    return a + b\n")
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "")
        XCTAssertEqual(session.state, "Failed")
        XCTAssertTrue(session.detail.contains("changed since the comment was written"), session.detail)
        XCTAssertEqual(session.comments?.count, 1)
        session.comments = [try Review.comment(workspace, path: "calc.py", line: 1, note: "Rename it.")]
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: " ")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertTrue(model.seen[1].last!.content.hasPrefix("Address these review comments.\ncalc.py · updated line 1"))
    }

    func testMilestonesFollowTheFilesTheyCover() async throws {
        let workspace = try Workspace(path: folder.path)
        try write("mason.workflows.json", #"{"version": 1, "workflows": {"tests": {"executable": "sh", "arguments": ["-c", "grep -q 'a + b' calc.py"]}}}"#)
        var plan: [Milestone] = []
        XCTAssertThrowsError(try Milestones.put(workspace, current: plan, item: Milestone(title: "", files: ["calc.py"])))
        XCTAssertThrowsError(try Milestones.put(workspace, current: plan, item: Milestone(title: "Outside", files: ["../x"])))
        var first = Milestone(title: "Add works", decision: "Use +", files: ["./calc.py"], workflow: "tests")
        plan = try Milestones.put(workspace, current: plan, item: first)
        XCTAssertEqual(plan[0].files, ["calc.py"])
        let second = Milestone(title: "Docs", files: ["calc.py"], dependsOn: [first.id])
        plan = try Milestones.put(workspace, current: plan, item: second)
        first.dependsOn = [second.id]
        XCTAssertThrowsError(try Milestones.put(workspace, current: plan, item: first), "a circle is refused")
        XCTAssertEqual(Milestones.inspect(workspace, plan: plan).map(\.state), [.ready, .blocked])
        XCTAssertTrue(Milestones.context(workspace, plan: plan).contains("Next: Add works"))

        // The workflow fails until the file is right, and Mason only runs it when it may.
        await XCTAssertThrowsErrorAsync(try await Milestones.check(workspace, item: plan[0], plan: plan, runWorkflow: (false, false), observation: ""))
        await XCTAssertThrowsErrorAsync(try await Milestones.check(workspace, item: plan[0], plan: plan, runWorkflow: (true, false), observation: ""))
        await XCTAssertThrowsErrorAsync(try await Milestones.check(workspace, item: plan[1], plan: plan, runWorkflow: (true, false), observation: "Looks right"), "blocked by its prerequisite")
        try write("calc.py", "def add(a, b):\n    return a + b\n")
        plan[0].receipt = try await Milestones.check(workspace, item: plan[0], plan: plan, runWorkflow: (true, false), observation: "")
        XCTAssertEqual(Milestones.inspect(workspace, plan: plan).map(\.state), [.verified, .ready])
        await XCTAssertThrowsErrorAsync(try await Milestones.check(workspace, item: plan[1], plan: plan, runWorkflow: (true, false), observation: ""), "a manual check needs an observation")
        plan[1].receipt = try await Milestones.check(workspace, item: plan[1], plan: plan, runWorkflow: (false, false), observation: "Read the docstring.")
        XCTAssertEqual(Milestones.inspect(workspace, plan: plan).map(\.state), [.verified, .reviewed])

        // Changing a covered file asks for both to be looked at again.
        try write("calc.py", "def add(a, b):\n    return a + b  # sum\n")
        XCTAssertEqual(Milestones.inspect(workspace, plan: plan).map(\.state), [.recheck, .blocked])
        plan = Milestones.remove(plan[0].id, from: plan)
        XCTAssertEqual(plan.count, 1)
        XCTAssertEqual(Milestones.inspect(workspace, plan: plan).map(\.state), [.ready])

        // The model is told the plan.
        let store = try SessionStore(home: home)
        var session = try store.create(workspace: workspace.root.path, permission: .readOnly)
        session.milestones = plan
        let model = ScriptedModel([ModelReply(text: "Ok.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "What is next?")
        XCTAssertTrue(model.seen[0][0].content.contains("The user's saved milestones") && model.seen[0][0].content.contains("- Docs [Ready]"))
        XCTAssertEqual(try SessionStore(home: home).sessions.first?.milestones?.first?.title, "Docs", "the plan is saved with the session")
    }

    func testTheTypedFormsOfCommentsMilestonesAndEmbeddings() async throws {
        let workspace = try Workspace(path: folder.path)
        let store = try SessionStore(home: home)
        var session = try store.create(workspace: workspace.root.path, permission: .reviewEdits)
        XCTAssertTrue(try Review.command("", workspace: workspace, session: &session).hasPrefix("No comments are waiting."))
        XCTAssertThrowsError(try Review.command("calc.py nothing", workspace: workspace, session: &session))
        XCTAssertEqual(try Review.command("calc.py:2 Use plus here.", workspace: workspace, session: &session), "Noted on calc.py · updated line 2: return a - b")
        XCTAssertTrue(try Review.command("", workspace: workspace, session: &session).hasPrefix("1. calc.py · updated line 2 — Use plus here."))
        XCTAssertEqual(try Review.command("remove 1", workspace: workspace, session: &session), "Dropped the comment on calc.py · updated line 2.")
        XCTAssertNil(session.comments)

        try write("mason.workflows.json", #"{"version": 1, "workflows": {"tests": {"executable": "sh", "arguments": ["-c", "true"]}}}"#)
        var said = try await Milestones.command("add Add works | calc.py | workflow tests | decision Use plus", workspace: workspace, session: &session, home: home)
        XCTAssertEqual(said, "Added milestone 1: Add works")
        said = try await Milestones.command("add Documented | calc.py | after 1", workspace: workspace, session: &session, home: home)
        await XCTAssertThrowsErrorAsync(try await Milestones.command("add Bad | calc.py | workflow missing", workspace: workspace, session: &session, home: home))
        said = try await Milestones.command("", workspace: workspace, session: &session, home: home)
        XCTAssertTrue(said.contains("1. [Ready] Add works  ·  calc.py  ·  workflow tests") && said.contains("2. [Blocked] Documented  ·  calc.py  ·  after 1"), said)
        await XCTAssertThrowsErrorAsync(try await Milestones.command("check 1", workspace: workspace, session: &session, home: home), "outside Auto a workflow needs a saved rule")
        session.permission = .auto
        if Shell.sandboxAvailable {
            said = try await Milestones.command("check 1", workspace: workspace, session: &session, home: home)
            XCTAssertEqual(said, "Add works: Verified.")
            said = try await Milestones.command("check 2 Read it through.", workspace: workspace, session: &session, home: home)
            XCTAssertEqual(said, "Documented: Reviewed.")
        }
        said = try await Milestones.command("remove 1", workspace: workspace, session: &session, home: home)
        XCTAssertEqual(session.milestones?.map(\.title), ["Documented"])
        XCTAssertEqual(session.milestones?.first?.dependsOn, [])

        said = try await EmbeddingSettings.command("", home: home)
        XCTAssertTrue(said.hasPrefix("Code search by meaning is off"))
        await XCTAssertThrowsErrorAsync(try await EmbeddingSettings.command("http://example.com/ m", home: home), "only servers on this computer")
        said = try await EmbeddingSettings.command("http://127.0.0.1:9/ embed", home: home)
        said = try await EmbeddingSettings.command("rerank http://127.0.0.1:9/ ranker", home: home)
        XCTAssertEqual(EmbeddingSettings.load(home: home), EmbeddingSettings(url: "http://127.0.0.1:9/", model: "embed", rerankUrl: "http://127.0.0.1:9/", rerankModel: "ranker"))
        await XCTAssertThrowsErrorAsync(try await EmbeddingSettings.command("test", home: home), "nothing answers on that port")
        said = try await EmbeddingSettings.command("off", home: home)
        XCTAssertNil(EmbeddingSettings.load(home: home))
    }

    func testCodeIsFoundByMeaningWithEachPassageEmbeddedOnce() async throws {
        let workspace = try Workspace(path: folder.path)
        try write("billing.py", "def invoice_total(items):\n    # the invoice total with tax\n    return sum(items) * 1.2\n")
        try write("auth.py", "def login(user, password):\n    # check the password for a login\n    return user == 'ada'\n")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("node_modules/x"), withIntermediateDirectories: true)
        try write("node_modules/x/invoice.js", "function invoice() { return 'invoice invoice invoice total total'; }\n")
        try write("logo.png", "invoice total invoice total invoice total")
        let models = WordModels()
        let result = try await SemanticSearch.search(workspace, models: models, query: "where is the invoice total and tax worked out")
        XCTAssertEqual(result.hits.first?.path, "billing.py")
        XCTAssertEqual(result.hits.first?.startLine, 1)
        XCTAssertFalse(result.hits.contains { $0.path.contains("node_modules") || $0.path.hasSuffix(".png") })
        XCTAssertEqual(result.files, 3)
        XCTAssertTrue(result.report.contains("billing.py lines 1–") && result.report.contains("invoice_total"), result.report)
        let first = models.calls.texts
        _ = try await SemanticSearch.search(workspace, models: models, query: "login")
        XCTAssertEqual(models.calls.texts, first + 1, "only the new question is embedded the second time")
        var reranking = models
        reranking.canRerank = true
        let reordered = try await SemanticSearch.search(workspace, models: reranking, query: "invoice total")
        XCTAssertTrue(reordered.reranked)
        XCTAssertEqual(reordered.hits.first?.path, "auth.py", "the reranker's order wins")
        XCTAssertEqual(SemanticSearch.chunks(path: "a.py", content: (1...100).map { "line number \($0)" }.joined(separator: "\n")).map(\.start), [1, 33, 65])

        // The tool is offered only when an embeddings server is chosen, and the agent can call it.
        let store = try SessionStore(home: home)
        var session = try store.create(workspace: workspace.root.path, permission: .readOnly)
        XCTAssertFalse(Agent.extraTools(session: session, workspace: workspace).contains { $0.name == "semantic_search" })
        XCTAssertNil(EmbeddingSettings.load(home: home))
        XCTAssertThrowsError(try EmbeddingSettings.save(EmbeddingSettings(url: "ftp://x", model: "m"), home: home))
        try EmbeddingSettings.save(EmbeddingSettings(url: "http://127.0.0.1:9/", model: "embed"), home: home)
        XCTAssertEqual(EmbeddingSettings.load(home: home)?.model, "embed")
        try EmbeddingSettings.save(nil, home: home)
        XCTAssertNil(EmbeddingSettings.load(home: home))
        let model = ScriptedModel([call("semantic_search", ["query": "invoice total", "path": "."]), ModelReply(text: "Found.")])
        let agent = Agent(store: store, model: model, approve: { _ in true }, emit: { _ in })
        agent.semantic = models
        await agent.run(&session, request: "Where are totals?")
        XCTAssertEqual(session.activities.first?.state, .completed)
        XCTAssertTrue(session.activities.first?.result.contains("billing.py lines") ?? false)
    }
}

func XCTAssertThrowsErrorAsync<T>(_ expression: @autoclosure () async throws -> T, _ message: String = "", file: StaticString = #filePath, line: UInt = #line) async {
    do { _ = try await expression(); XCTFail("expected an error. " + message, file: file, line: line) } catch {}
}

/// A window with a text field, a button and a label that shows what was last saved.
final class FakeDesktop: DesktopAutomation {
    var trusted = true
    var field = "", saved = ""
    var acted: [String] = []
    let window = DesktopWindow(pid: 42, app: "Notes", title: "Shopping")
    func unavailable() -> String? { trusted ? nil : "Mason needs the Accessibility permission." }
    func windows() throws -> [DesktopWindow] { [DesktopWindow(pid: 43, app: "Calculator", title: "Calculator"), window] }
    func observe(_ window: DesktopWindow) throws -> [DesktopControl] {
        [DesktopControl(id: "c1", role: "textfield", name: "Item", value: field, canSet: true), DesktopControl(id: "c2", role: "button", name: "Save", canPress: true),
         DesktopControl(id: "c3", role: "statictext", name: "", value: saved.isEmpty ? "Nothing saved" : "Saved " + saved)]
    }
    func act(_ window: DesktopWindow, control: String, operation: String, value: String) throws {
        acted.append("\(operation) \(control) \(value)")
        if control == "c1" { field = value } else { saved = field }
    }
    /// The window's picture changes whenever something is drawn on its canvas.
    var strokes = 0
    func frame(_ window: DesktopWindow) async throws -> DesktopFrame? { DesktopFrame(width: 200, height: 100, png: Data("picture \(strokes)".utf8)) }
    func pointer(_ window: DesktopWindow, action: String, x: Int, y: Int, endX: Int, endY: Int) async throws {
        acted.append(action == "drag" ? "drag \(x),\(y) to \(endX),\(endY)" : "click \(x),\(y)")
        strokes += 1
    }
}

/// Answers about a picture from a script, and records what it was asked.
final class ScriptedVision: DesktopVision {
    var replies: [String]
    var asked: [String] = []
    init(_ replies: [String]) { self.replies = replies }
    func see(task: String, frame: DesktopFrame) async throws -> PixelDecision {
        asked.append(Vision.prompt(task, frame: frame))
        return try Vision.parse(replies.removeFirst(), frame: frame)
    }
}

final class DesktopTests: XCTestCase {
    func testAnAttachedWindowIsReadAndOperatedWithApprovalEachTime() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-desk-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        try FileManager.default.createDirectory(at: base.appendingPathComponent("project"), withIntermediateDirectories: true)
        let store = try SessionStore(home: base.appendingPathComponent("home"))
        var session = try store.create(workspace: try Workspace(path: base.appendingPathComponent("project").path).root.path, permission: .auto)
        let desktop = FakeDesktop()

        // Attaching is the user's act; without a window the tools are not offered.
        XCTAssertTrue(Agent.desktopTools(for: session, available: true).isEmpty)
        XCTAssertTrue(try Desktop.command("", session: &session, desktop: desktop).hasPrefix("1. Calculator\n2. Notes — Shopping\nAttach one with /window NUMBER."))
        XCTAssertThrowsError(try Desktop.command("9", session: &session, desktop: desktop))
        XCTAssertTrue(try Desktop.command("2", session: &session, desktop: desktop).hasPrefix("Attached Notes — Shopping."))
        XCTAssertEqual(Agent.desktopTools(for: session, available: true).map(\.name), ["desktop_observe", "desktop_visual", "desktop_act"])
        XCTAssertTrue(Agent.desktopTools(for: session, available: false).isEmpty)
        desktop.trusted = false
        XCTAssertThrowsError(try Desktop.command("", session: &session, desktop: desktop), "a missing permission is said plainly")
        desktop.trusted = true

        func act(_ operation: String, _ id: String, _ value: String) -> ModelReply { call("desktop_act", ["operation": operation, "id": id, "value": value]) }
        let model = ScriptedModel([act("set", "c1", "Milk"), call("desktop_observe", [:]), act("set", "c1", "Milk"), act("invoke", "c2", ""), act("invoke", "c3", ""), act("invoke", "c2", ""), ModelReply(text: "Saved.")])
        var asked: [String] = [], answers = [true, true, false]
        let agent = Agent(store: store, model: model, approve: { asked.append($0.title); return answers.removeFirst() }, emit: { _ in })
        agent.desktop = desktop
        await agent.run(&session, request: "Add milk to the list")
        XCTAssertTrue(model.seen[0][0].content.contains("The user attached a window of another app: Notes — Shopping."))
        XCTAssertEqual(session.activities.map(\.state), [.failed, .completed, .completed, .completed, .failed, .denied], "an action needs an observation first, a control that accepts it, and the user's yes")
        XCTAssertTrue(session.activities[1].result.contains("c1 · textfield · Item  [set]") && session.activities[1].result.contains("c2 · button · Save  [invoke]"), session.activities[1].result)
        XCTAssertEqual(asked, ["Set textfield “Item” to “Milk” in Notes — Shopping", "Press button “Save” in Notes — Shopping", "Press button “Save” in Notes — Shopping"], "it asks every time, even in Auto")
        XCTAssertEqual(desktop.acted, ["set c1 Milk", "invoke c2 "])
        XCTAssertTrue(session.activities[3].result.contains("c3 · statictext = Saved Milk"), "each action returns a fresh observation")

        // A read-only session may look but not touch.
        session.permission = .readOnly; session.askOnly = true
        XCTAssertEqual(Agent.desktopTools(for: session, available: true).map(\.name), ["desktop_observe", "desktop_visual"])
        XCTAssertEqual(try Desktop.command("off", session: &session, desktop: desktop), "No window is attached to this session.")
        XCTAssertNil(session.desktop)
    }
}

final class AcceptanceTests: XCTestCase {
    var folder: URL!
    override func setUpWithError() throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("mason-accept-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        // A small shop: three products, ten percent off at three units, and an order number that survives a reload.
        try """
        <!doctype html><title>Shop</title><ul aria-label="Products"><li>Wrench</li><li>Hammer</li><li>Saw</li></ul>
        <input id="qty" value="1"><p id="total">Total: $10.00</p><button id="checkout">Check out</button><p id="order"></p>
        <script>
        var qty = document.getElementById('qty'), total = document.getElementById('total'), order = document.getElementById('order');
        qty.addEventListener('input', function () { var n = Number(qty.value) || 0, sum = n * 10; if (n >= 3) sum = sum * 0.9; total.textContent = 'Total: $' + sum.toFixed(2); });
        order.textContent = localStorage.getItem('order') || '';
        document.getElementById('checkout').onclick = function () { var id = 'ORD-' + (Number(localStorage.getItem('count') || 0) + 1); localStorage.setItem('count', id.slice(4)); localStorage.setItem('order', id); order.textContent = id; };
        </script>
        """.write(to: folder.appendingPathComponent("index.html"), atomically: true, encoding: .utf8)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: folder) }

    func contract(discount: String = "27.00") -> String {
        """
        {"Version": 1, "Scenarios": [
          {"Id": "products", "Requirement": "List three distinct products", "Page": "index.html", "Steps": [{"Op": "count_distinct", "Target": "ul[aria-label='Products'] li", "Value": "3"}]},
          {"Id": "discount", "Requirement": "Ten percent off at three units", "Page": "index.html",
           "Steps": [{"Op": "fill", "Target": "#qty", "Value": "3"}, {"Op": "money", "Target": "#total", "Value": "\(discount)"}, {"Op": "fill", "Target": "#qty", "Value": "2"}, {"Op": "money", "Target": "#total", "Value": "20.00"}]},
          {"Id": "order", "Requirement": "Checkout makes an order number that survives a reload", "Page": "index.html",
           "Steps": [{"Op": "empty", "Target": "#order", "Value": ""}, {"Op": "click", "Target": "#checkout", "Value": ""}, {"Op": "capture", "Target": "#order", "Value": "ORD-"},
                     {"Op": "reload", "Target": "", "Value": ""}, {"Op": "same", "Target": "#order", "Value": ""}, {"Op": "click", "Target": "#checkout"}, {"Op": "new_id", "Target": "#order", "Value": "ORD-"}]}
        ]}
        """
    }

    func testAContractIsCheckedBeforeItIsRun() throws {
        let workspace = try Workspace(path: folder.path)
        let parsed = try Acceptance.parse(workspace, contract())
        XCTAssertEqual(parsed.scenarios.map(\.id), ["products", "discount", "order"])
        XCTAssertEqual(parsed.scenarios[2].steps.count, 7)
        XCTAssertNil(parsed.application)
        func refused(_ source: String, _ why: String) { XCTAssertThrowsError(try Acceptance.parse(workspace, source), why) }
        refused(##"{"Version": 2, "Scenarios": []}"##, "only version 1")
        refused(##"{"Version": 1, "Scenarios": [{"Id": "a", "Requirement": "r", "Page": "index.html", "Steps": [{"Op": "click", "Target": "#x"}]}]}"##, "a scenario must end with an assertion")
        refused(##"{"Version": 1, "Scenarios": [{"Id": "a", "Requirement": "r", "Page": "index.html", "Steps": [{"Op": "same", "Target": "#x"}]}]}"##, "nothing was captured to compare")
        refused(##"{"Version": 1, "Scenarios": [{"Id": "a", "Requirement": "r", "Page": "../x.html", "Steps": [{"Op": "text", "Target": "#x", "Value": "y"}]}]}"##, "the page must be in the project")
        refused(##"{"Version": 1, "Scenarios": [{"Id": "a", "Requirement": "r", "Page": "index.html", "Steps": [{"Op": "hover", "Target": "#x"}, {"Op": "text", "Target": "#x", "Value": "y"}]}]}"##, "an unknown step")
        refused(##"{"Version": 1, "Scenarios": [{"Id": "a", "Requirement": "r", "Page": "index.html", "Steps": [{"Op": "money", "Target": "#x", "Value": "ten"}]}]}"##, "an amount must be a number")
        let twice = ##"{"Id": "a", "Requirement": "r", "Page": "index.html", "Steps": [{"Op": "text", "Target": "#x", "Value": "y"}]}"##
        refused("{\"Version\": 1, \"Scenarios\": [\(twice), \(twice)]}", "ids must be unique")
    }

    func testScenariosRunInARealBrowserAtBothWidths() async throws {
        guard BrowserCheck.findBrowser() != nil else { throw XCTSkip("No Chromium-family browser on this computer.") }
        let workspace = try Workspace(path: folder.path)
        let passed = try await Acceptance.run(workspace, try Acceptance.parse(workspace, contract()))
        XCTAssertEqual(passed.map(\.passed), [true, true, true], Acceptance.receipt(passed))
        XCTAssertTrue(Acceptance.receipt(passed).hasPrefix("3 of 3 acceptance scenarios passed, each at 390px and 1280px."))
        XCTAssertTrue(passed[1].detail.contains("money #total = Total: $27.00"), "\(passed[1].detail)")
        XCTAssertTrue(passed[2].detail.contains("new_id #order = ORD-2"), "\(passed[2].detail)")

        let failed = try await Acceptance.run(workspace, try Acceptance.parse(workspace, contract(discount: "30.00")))
        XCTAssertEqual(failed.map(\.passed), [true, false, true])
        XCTAssertEqual(failed[1].detail, ["at 390px, step 2, money #total: expected '30.00', observed 'Total: $27.00' (1 visible)"])

        // The agent runs them as a tool, asking first outside Auto.
        try contract().write(to: folder.appendingPathComponent(Acceptance.filename), atomically: true, encoding: .utf8)
        let store = try SessionStore(home: folder.appendingPathComponent("home"))
        var session = try store.create(workspace: workspace.root.path, permission: .reviewEdits)
        XCTAssertTrue(Agent.extraTools(session: session, workspace: workspace).contains { $0.name == "verify_acceptance" })
        let model = ScriptedModel([call("verify_acceptance", [:]), ModelReply(text: "All three hold.")])
        var asked: [String] = []
        await Agent(store: store, model: model, approve: { asked.append($0.subject); return true }, emit: { _ in }).run(&session, request: "Check the shop")
        XCTAssertEqual(asked, ["mason.acceptance.json"])
        XCTAssertEqual(session.activities.first?.state, .completed)
        XCTAssertTrue(session.activities.first?.result.contains("PASSED order — Checkout makes an order number that survives a reload (index.html)") ?? false)
        XCTAssertTrue(model.seen[0][0].content.contains("acceptance scenarios in mason.acceptance.json"))
    }

    /// The checkout fixture the Windows tests use: a WSGI shop with an order database.
    func checkout() throws -> Workspace {
        let fixture = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("windows/tests/fixtures/checkout")
        let project = folder.appendingPathComponent("checkout")
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        for name in ["index.html", "store.py", "mason.acceptance.json"] { try FileManager.default.copyItem(at: fixture.appendingPathComponent(name), to: project.appendingPathComponent(name)) }
        return try Workspace(path: project.path)
    }

    func testAnApplicationRunsInAThrowawayCopyAndIsAskedThroughFiles() async throws {
        guard VerificationApp.python() != nil else { throw XCTSkip("No python3 on this computer.") }
        let workspace = try checkout()
        await XCTAssertThrowsErrorAsync(try await VerificationApp.start(workspace, entry: "store; import os", sandboxed: false), "only module:callable")
        let app = try await VerificationApp.start(workspace, entry: "store:application", sandboxed: Shell.sandboxAvailable)
        let copy = app.folder
        XCTAssertTrue(copy.lastPathComponent.hasPrefix("mason-verify-") && FileManager.default.fileExists(atPath: copy.appendingPathComponent("store.py").path))
        XCTAssertNil(app.answer(method: "GET", target: "/__mason/harness?page=x", headers: [:], body: Data()), "the harness stays with the page server")

        let page = try XCTUnwrap(app.answer(method: "GET", target: "/index.html", headers: [:], body: Data()))
        XCTAssertEqual(page.status, 200)
        XCTAssertTrue(String(decoding: page.body, as: UTF8.self).contains("window.__masonErrors") && String(decoding: page.body, as: UTF8.self).contains("<li>Honey</li>"), "the application's page, with the error recorder added")
        let price = try XCTUnwrap(app.answer(method: "GET", target: "/price?quantity=3", headers: [:], body: Data()))
        XCTAssertEqual(String(decoding: price.body, as: UTF8.self), #"{"total": 27.0}"#)
        XCTAssertEqual(app.answer(method: "GET", target: "/missing", headers: [:], body: Data())?.status, 404)

        XCTAssertThrowsError(try app.check(op: "response_count", target: "POST /checkout", value: "1", captured: ""), "nothing was ordered yet")
        let order = try XCTUnwrap(app.answer(method: "POST", target: "/checkout", headers: ["content-type": "application/json"], body: Data("{}".utf8)))
        let id = try XCTUnwrap((try JSONSerialization.jsonObject(with: order.body) as? [String: String])?["id"])
        XCTAssertEqual(try app.check(op: "response_count", target: "POST /checkout", value: "1", captured: ""), "1 responses")
        XCTAssertEqual(try app.check(op: "response_same", target: "POST /checkout", value: "id", captured: id), "1 responses matched \(id)")
        XCTAssertThrowsError(try app.check(op: "response_same", target: "POST /checkout", value: "id", captured: "ORD-other"))
        XCTAssertEqual(try app.check(op: "sqlite_count", target: "orders.sqlite#orders", value: "1", captured: ""), "1 rows")
        XCTAssertThrowsError(try app.check(op: "sqlite_count", target: "orders.sqlite#orders", value: "2", captured: ""))
        XCTAssertThrowsError(try app.check(op: "sqlite_count", target: "../x.sqlite#orders", value: "1", captured: ""))

        await app.stop()
        XCTAssertFalse(FileManager.default.fileExists(atPath: copy.path), "the copy and its database are thrown away")
        XCTAssertFalse(FileManager.default.fileExists(atPath: workspace.root.appendingPathComponent("orders.sqlite").path), "the project itself was never written to")
    }

    func testTheCheckoutContractPassesInARealBrowserAndCatchesABrokenShop() async throws {
        guard BrowserCheck.findBrowser() != nil, VerificationApp.python() != nil else { throw XCTSkip("Needs a Chromium-family browser and python3.") }
        let workspace = try checkout()
        let passed = try await Acceptance.run(workspace, try Acceptance.read(workspace))
        XCTAssertTrue(passed.allSatisfy(\.passed), Acceptance.receipt(passed))
        // An order that is shown but never saved must fail the scenario that reloads the order page.
        let store = workspace.root.appendingPathComponent("store.py")
        try String(contentsOf: store, encoding: .utf8).replacingOccurrences(of: "SAVE_ORDER = True", with: "SAVE_ORDER = False").write(to: store, atomically: true, encoding: .utf8)
        let broken = try await Acceptance.run(workspace, try Acceptance.read(workspace))
        XCTAssertFalse(broken.allSatisfy(\.passed), Acceptance.receipt(broken))
    }

    func testAReviewKeepsOnlyFindingsThatQuoteTheLineTheyName() async throws {
        let workspace = try Workspace(path: folder.path)
        try "def add(a, b):\n    return a - b\n".write(to: folder.appendingPathComponent("calc.py"), atomically: true, encoding: .utf8)
        let store = try SessionStore(home: folder.appendingPathComponent("home"))
        var session = try store.create(workspace: workspace.root.path, permission: .workspaceEdits)
        do { _ = try await Review.findings(session, workspace: workspace, model: ScriptedModel([])); XCTFail("nothing was changed yet") } catch {}
        var turn = Turn(request: "Add an add function")
        turn.changes = [FileChange(path: "calc.py", before: nil, after: "def add(a, b):\n    return a - b\n")]
        session.turns = [turn]
        let answer = """
        Here is what I found:
        [{"path": "calc.py", "line": 2, "evidence": "return a - b", "problem": "It subtracts instead of adding."},
         {"path": "calc.py", "line": 1, "evidence": "return a - b", "problem": "Quoted from the wrong line."},
         {"path": "other.py", "line": 1, "evidence": "x", "problem": "A file that was not changed."}]
        """
        let model = ScriptedModel([ModelReply(text: answer), ModelReply(text: "[]")])
        let found = try await Review.findings(session, workspace: workspace, model: model)
        XCTAssertEqual(found.comments.map(\.location), ["calc.py · updated line 2"])
        XCTAssertEqual(found.comments[0].note, "It subtracts instead of adding.")
        XCTAssertEqual(found.dropped, 2)
        XCTAssertTrue(model.seen[0][1].content.contains("2\t    return a - b"), "the model reads numbered lines")
        XCTAssertTrue(Review.summary(found).hasPrefix("The review found 1 thing in calc.py:\ncalc.py · updated line 2 — It subtracts instead of adding.\n2 findings were left out"))
        XCTAssertNoThrow(try Review.validate(workspace, comments: found.comments), "a finding is a comment that can be sent")
        let nothing = try await Review.findings(session, workspace: workspace, model: model)
        XCTAssertEqual(Review.summary(nothing), "The review of calc.py found nothing to report.")
    }
}

final class SessionTransferTests: XCTestCase {
    func testASessionIsForkedExportedAndImported() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-transfer-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        let folder = base.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let workspace = try Workspace(path: folder.path)
        let store = try SessionStore(home: base.appendingPathComponent("home"))
        var session = try store.create(workspace: workspace.root.path, permission: .auto)
        let model = ScriptedModel([call("write_file", ["path": "a.txt", "content": "one\n"]), ModelReply(text: "Made a."),
                                   call("write_file", ["path": "b.txt", "content": "two\n"]), ModelReply(text: "Made b with api_key = hunter2secret."),
                                   call("write_file", ["path": "a.txt", "content": "changed\n"]), ModelReply(text: "Changed a.")])
        let agent = Agent(store: store, model: model, approve: { _ in true }, emit: { _ in })
        await agent.run(&session, request: "Make a")
        await agent.run(&session, request: "Make b in \(workspace.root.path)")
        await agent.run(&session, request: "Change a")
        XCTAssertEqual(session.turns.count, 3)

        // A fork holds the conversation before the request, and can put the files back as they were then.
        XCTAssertThrowsError(try SessionTransfer.fork(&session, store: store, turn: 7, restoreFiles: false))
        let plain = try SessionTransfer.fork(&session, store: store, turn: 2, restoreFiles: false)
        XCTAssertEqual(plain.session.turns.map(\.request), ["Make a", "Make b in \(workspace.root.path)"])
        XCTAssertEqual(plain.request, "Change a")
        XCTAssertEqual(plain.session.messages.last?.content, "Made b with api_key = hunter2secret.")
        XCTAssertEqual(plain.session.activities.count, 2)
        XCTAssertEqual(plain.session.permission, .reviewEdits, "a fork does not start in Auto")
        XCTAssertTrue(plain.session.title.hasSuffix(" · fork"))
        XCTAssertEqual(try workspace.read("a.txt"), "changed\n", "files are left alone unless asked")

        try "edited by hand\n".write(to: folder.appendingPathComponent("b.txt"), atomically: true, encoding: .utf8)
        let restored = try SessionTransfer.fork(&session, store: store, turn: 1, restoreFiles: true)
        XCTAssertEqual(restored.session.turns.map(\.request), ["Make a"])
        XCTAssertEqual(try workspace.read("a.txt"), "one\n", "the later change to a.txt was undone")
        XCTAssertEqual(restored.restored, ["a.txt"])
        XCTAssertEqual(restored.skipped, ["b.txt"], "a file changed since is left alone and reported")
        XCTAssertTrue(restored.session.detail.contains("changed since and left alone: b.txt"), restored.session.detail)
        XCTAssertEqual(session.turns.map(\.undone), [false, true, true])

        // An export can leave out the project's path, likely secrets and file contents.
        let full = String(decoding: try SessionTransfer.export(session, sanitize: false), as: UTF8.self)
        XCTAssertTrue(full.contains("hunter2secret") && full.contains(workspace.root.path.replacingOccurrences(of: "/", with: "\\/")))
        let clean = String(decoding: try SessionTransfer.export(session, sanitize: true), as: UTF8.self)
        XCTAssertFalse(clean.contains("hunter2secret") || clean.contains(folder.lastPathComponent + "\\/") || clean.contains("changed\\n"), clean)
        XCTAssertTrue(clean.contains("<redacted>") && clean.contains("<workspace>"))

        let other = base.appendingPathComponent("elsewhere")
        try FileManager.default.createDirectory(at: other, withIntermediateDirectories: true)
        let imported = try SessionTransfer.importSession(Data(clean.utf8), store: store, workspace: try Workspace(path: other.path).root.path)
        XCTAssertNotEqual(imported.id, session.id)
        XCTAssertEqual(imported.turns.map(\.answer), ["Made a.", "Made b with api_key = <redacted>", "Changed a."])
        XCTAssertTrue(imported.turns.allSatisfy { $0.changes.isEmpty && !$0.undone })
        XCTAssertTrue(imported.title.hasSuffix(" · imported") && imported.detail.hasPrefix("Imported a sanitized session."))
        XCTAssertThrowsError(try SessionTransfer.importSession(Data("{}".utf8), store: store, workspace: workspace.root.path))
        XCTAssertEqual(try SessionStore(home: base.appendingPathComponent("home")).sessions.count, 4, "the original, two forks and the import")
    }
}

final class NavigationTests: XCTestCase {
    func testNamesAreFoundAsLabelledTextCandidatesAndThroughALanguageService() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-nav-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        let folder = base.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("tests"), withIntermediateDirectories: true)
        try "def total(items):\n    return sum(items)\n\n# total is used below\nprint(total([1, 2]))\nsubtotal = 3\n".write(to: folder.appendingPathComponent("cart.py"), atomically: true, encoding: .utf8)
        try "from cart import total\n\ndef test_total():\n    assert total([1]) == 1\n".write(to: folder.appendingPathComponent("tests/test_cart.py"), atomically: true, encoding: .utf8)
        try "int total(int a, int b) {\n    return a + b;\n}\n".write(to: folder.appendingPathComponent("sum.c"), atomically: true, encoding: .utf8)
        try "total total total".write(to: folder.appendingPathComponent("notes.txt"), atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: folder.path)

        let definitions = try CodeNavigation.query(workspace, directory: ".", symbol: "total", mode: "definitions")
        XCTAssertEqual(definitions.hits.map { "\($0.path):\($0.line)" }, ["cart.py:1", "sum.c:1"], "a def and a C function, not the import or the call")
        let references = try CodeNavigation.query(workspace, directory: ".", symbol: "total", mode: "references")
        XCTAssertEqual(references.hits.map { "\($0.path):\($0.line)" }, ["cart.py:5", "tests/test_cart.py:1", "tests/test_cart.py:4"], "whole words only, and not the comment")
        XCTAssertEqual(try CodeNavigation.query(workspace, directory: ".", symbol: "total", mode: "tests").hits.map(\.path), ["tests/test_cart.py", "tests/test_cart.py"])
        XCTAssertEqual(definitions.files, 3, "only source files are read")
        XCTAssertThrowsError(try CodeNavigation.query(workspace, directory: ".", symbol: "a b", mode: "definitions"))
        XCTAssertThrowsError(try CodeNavigation.query(workspace, directory: ".", symbol: "total", mode: "diagnostics"))
        XCTAssertTrue(CodeNavigation.report(definitions, symbol: "total", mode: "definitions").contains("cart.py:1  [definition candidate]  def total(items):"))

        // A stand-in for the helper: it answers with the position it was asked about and how many files it was given.
        let tools = base.appendingPathComponent("tools")
        try FileManager.default.createDirectory(at: tools.appendingPathComponent("python/jedi"), withIntermediateDirectories: true)
        try "import json, sys\nr = json.load(sys.stdin)\nprint(json.dumps({'engine': 'stand-in', 'asked': [r['path'], r['line'], r['column'], r['mode']], 'files': sorted(r['files'])}))\n"
            .write(to: tools.appendingPathComponent("python-symbols.py"), atomically: true, encoding: .utf8)
        setenv("MASON_LANGUAGE_TOOLS", tools.path, 1)
        defer { unsetenv("MASON_LANGUAGE_TOOLS") }
        guard TransformersRuntime.python() != nil else { throw XCTSkip("No python3 on this computer.") }
        XCTAssertTrue(SymbolService.available)
        let answer = try await SymbolService.query(workspace, path: "cart.py", line: 5, column: 7, mode: "definitions")
        XCTAssertEqual(answer, #"{"engine": "stand-in", "asked": ["cart.py", 5, 7, "definitions"], "files": ["cart.py", "tests/test_cart.py"]}"#)
        await XCTAssertThrowsErrorAsync(try await SymbolService.query(workspace, path: "sum.c", line: 1, column: 1, mode: "definitions"), "only Python and JavaScript or TypeScript")
        await XCTAssertThrowsErrorAsync(try await SymbolService.query(workspace, path: "cart.py", line: 0, column: 1, mode: "definitions"))

        let store = try SessionStore(home: base.appendingPathComponent("home"))
        var session = try store.create(workspace: workspace.root.path, permission: .readOnly)
        XCTAssertTrue(Agent.extraTools(session: session, workspace: workspace).map(\.name).contains("inspect_symbol"))
        let model = ScriptedModel([call("navigate_code", ["path": ".", "symbol": "total", "mode": "definitions"]), call("inspect_symbol", ["path": "cart.py", "line": "5", "column": "7", "mode": "references"]), ModelReply(text: "Found.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Where is total?")
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed])
        XCTAssertTrue(session.activities[1].result.contains(#""references""#))
    }
}

final class DesktopVisionTests: XCTestCase {
    func testAWindowIsOperatedBySightOnlyWithApprovalAndWhileItLooksTheSame() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-sight-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        try FileManager.default.createDirectory(at: base.appendingPathComponent("project"), withIntermediateDirectories: true)
        let store = try SessionStore(home: base.appendingPathComponent("home"))
        var session = try store.create(workspace: try Workspace(path: base.appendingPathComponent("project").path).root.path, permission: .auto)
        let desktop = FakeDesktop()
        session.desktop = desktop.window
        let frame = DesktopFrame(width: 200, height: 100, png: Data("x".utf8))

        // The model's reply is read strictly: a point outside the picture is refused.
        XCTAssertEqual(try Vision.parse(#"Sure: {"observation": "A red circle", "action": "click", "x": 40.4, "y": 20}"#, frame: frame), PixelDecision(action: "click", x: 40, y: 20, observation: "A red circle"))
        XCTAssertThrowsError(try Vision.parse(#"{"action": "click", "x": 400, "y": 20, "observation": ""}"#, frame: frame))
        XCTAssertThrowsError(try Vision.parse(#"{"action": "drag", "x": 4, "y": 2, "endX": 4, "endY": 200, "observation": ""}"#, frame: frame))
        XCTAssertThrowsError(try Vision.parse(#"{"action": "type", "observation": ""}"#, frame: frame))
        XCTAssertThrowsError(try Vision.parse("I cannot see images.", frame: frame))
        XCTAssertTrue(Vision.prompt("Find the circle", frame: frame).contains("200 by 100 pixels"))

        func look(_ task: String) -> ModelReply { call("desktop_visual", ["task": task]) }
        let vision = ScriptedVision([#"{"observation": "A chart with three bars", "action": "none"}"#,
                                     #"{"observation": "A canvas with a red circle at the left", "action": "click", "x": 40, "y": 20}"#,
                                     #"{"observation": "The circle", "action": "drag", "x": 40, "y": 20, "endX": 150, "endY": 60}"#,
                                     #"{"observation": "A Clear button", "action": "click", "x": 10, "y": 10}"#,
                                     #"{"observation": "The canvas", "action": "click", "x": 5, "y": 5}"#])
        let model = ScriptedModel([look("What does the chart show?"), look("Click the red circle"), look("Drag the circle to the right"), look("Click Clear"), look("Click the corner"), ModelReply(text: "Done.")])
        var asked: [String] = [], answers = [true, true, false, true]
        let agent = Agent(store: store, model: model, approve: { asked.append($0.title + " | " + $0.detail.split(separator: "\n")[0]); return answers.removeFirst() }, emit: { _ in })
        agent.desktop = desktop
        agent.vision = vision
        await agent.run(&session, request: "Use the drawing app")
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed, .completed, .denied, .completed])
        XCTAssertEqual(session.activities[0].result, "Seen in Notes — Shopping: A chart with three bars", "looking needs no approval")
        XCTAssertEqual(asked, ["Click at (40, 20) in Notes — Shopping | The vision model saw: A canvas with a red circle at the left",
                               "Drag from (40, 20) to (150, 60) in Notes — Shopping | The vision model saw: The circle",
                               "Click at (10, 10) in Notes — Shopping | The vision model saw: A Clear button",
                               "Click at (5, 5) in Notes — Shopping | The vision model saw: The canvas"], "every click and drag asks, even in Auto")
        XCTAssertEqual(desktop.acted, ["click 40,20", "drag 40,20 to 150,60", "click 5,5"], "the declined click was not made")
        XCTAssertTrue(session.activities[1].result.hasPrefix("Click at (40, 20) was made.") && session.activities[1].result.contains("c2 · button · Save"), "an action returns the controls as they are afterwards")

        // A window that changed between the look and the approval is left alone.
        final class Shifting: DesktopVision {
            let desktop: FakeDesktop
            init(_ desktop: FakeDesktop) { self.desktop = desktop }
            func see(task: String, frame: DesktopFrame) async throws -> PixelDecision { desktop.strokes += 1; return PixelDecision(action: "click", x: 1, y: 1, observation: "A button") }
        }
        let before = desktop.acted
        let second = Agent(store: store, model: ScriptedModel([look("Click the button"), ModelReply(text: "It changed.")]), approve: { _ in true }, emit: { _ in })
        second.desktop = desktop
        second.vision = Shifting(desktop)
        await second.run(&session, request: "Again")
        XCTAssertEqual(session.activities.last?.state, .failed)
        XCTAssertTrue(session.activities.last?.result.contains("The window changed before the click could be made") ?? false)
        XCTAssertEqual(desktop.acted, before, "nothing was clicked")

        // A read-only session may look, and is told what was seen, but may not click.
        session.permission = .readOnly; session.askOnly = true
        let third = Agent(store: store, model: ScriptedModel([look("Click it"), ModelReply(text: "Read-only.")]), approve: { _ in true }, emit: { _ in })
        third.desktop = desktop
        third.vision = ScriptedVision([#"{"observation": "A big button", "action": "click", "x": 3, "y": 3}"#])
        await third.run(&session, request: "Look")
        XCTAssertEqual(session.activities.last?.state, .failed)
        XCTAssertTrue(session.activities.last?.result.contains("The model saw: A big button") ?? false)
        XCTAssertEqual(desktop.acted, before)
    }
}

final class DerivedTests: XCTestCase {
    var folder: URL!
    override func setUpWithError() throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("mason-derived-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("project"), withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: folder) }

    func testRequirementsWithAnExactOutcomeBecomeChecksWithoutAModel() async throws {
        let workspace = try Workspace(path: folder.appendingPathComponent("project").path)
        XCTAssertEqual(Derived.units(#"Show a heading "Orders, and more" and clicking "Add" must show "Added". Keep it tidy"#), [#"Show a heading "Orders, and more""#, #"clicking "Add" must show "Added""#, "Keep it tidy"], "quoted text is kept whole")
        let plan = await Derived.prepare(#"In index.html show a heading "Orders"; clicking "Add" must show "Added"; write notes.md containing "Shipped""#, workspace: workspace, model: nil)
        XCTAssertEqual(plan.checks.map(\.kind), ["browser_heading", "browser_click", "document_text"])
        XCTAssertEqual(plan.checks.map(\.description), ["index.html shows a heading that reads “Orders”", "in index.html, clicking “Add” makes “Added” appear", "notes.md contains “Shipped”"])
        XCTAssertTrue(plan.unverified.isEmpty)
        XCTAssertTrue(plan.brief.hasPrefix("Mason will check these itself when you say you are done:\n- index.html shows a heading"))
        let vague = await Derived.prepare("Make the totals right and tidy up", workspace: workspace, model: nil)
        XCTAssertTrue(vague.checks.isEmpty)
        XCTAssertEqual(vague.unverified, ["Make the totals right", "tidy up"])

        // A document check needs no browser.
        try "Shipped on Monday".write(to: workspace.root.appendingPathComponent("notes.md"), atomically: true, encoding: .utf8)
        let results = try await Derived.verify(DerivedPlan(units: ["x"], checks: [plan.checks[2], DerivedCheck(unit: 1, feature: "x", kind: "document_text", path: "missing.txt", expected: "y")]), workspace: workspace)
        XCTAssertEqual(results.map(\.state), ["passed", "failed"])
    }

    func testAModelsProposedChecksAreKeptOnlyWhenMasonCanRunThemAndTheyQuoteTheRequest() async throws {
        let workspace = try Workspace(path: folder.appendingPathComponent("project").path)
        let request = #"In shop.html the banner should say "Open today" and clicking the "Hide" button hides it"#
        let proposals = """
        {"checks": [{"Unit": 1, "Feature": "Banner text", "Kind": "browser_text", "Path": "shop.html", "Target": "", "Expected": "Open today"},
                    {"Unit": 1, "Feature": "Invented", "Kind": "browser_text", "Path": "shop.html", "Target": "", "Expected": "Welcome back"},
                    {"Unit": 2, "Feature": "Hiding", "Kind": "browser_text", "Path": "shop.html", "Target": "", "Expected": "Hide"},
                    {"Unit": 2, "Feature": "Unknown kind", "Kind": "screenshot", "Path": "shop.html", "Target": "", "Expected": "Hide"},
                    {"Unit": 9, "Feature": "No such requirement", "Kind": "browser_text", "Path": "shop.html", "Target": "", "Expected": "Hide"}]}
        """
        let model = ScriptedModel([ModelReply(text: proposals)])
        let plan = await Derived.prepare(request, workspace: workspace, model: model)
        XCTAssertEqual(plan.units.count, 2)
        XCTAssertEqual(plan.checks.map(\.expected), ["Open today"], "text the request never gave, a check that ignores the click, an unknown kind and an unknown requirement are all dropped")
        XCTAssertEqual(plan.unverified, [#"clicking the "Hide" button hides it"#])
        XCTAssertTrue(model.seen[0][1].content.contains("1. In shop.html the banner should say"))
    }

    func testInAutoAFailedCheckGoesBackToTheModelAndTheReceiptSaysWhatMasonFound() async throws {
        guard BrowserCheck.findBrowser() != nil else { throw XCTSkip("No Chromium-family browser on this computer.") }
        let workspace = try Workspace(path: folder.appendingPathComponent("project").path)
        let store = try SessionStore(home: folder.appendingPathComponent("home"))
        var session = try store.create(workspace: workspace.root.path, permission: .auto)
        let wrong = "<!doctype html><h1>Order</h1><button onclick=\"document.getElementById('o').textContent='Added'\">Add</button><p id=o></p>"
        let right = wrong.replacingOccurrences(of: "<h1>Order</h1>", with: "<h1>Orders</h1>")
        let model = ScriptedModel([call("write_file", ["path": "index.html", "content": wrong]), ModelReply(text: "Done."), call("write_file", ["path": "index.html", "content": right]), ModelReply(text: "Fixed the heading.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: #"In index.html show a heading "Orders"; clicking "Add" must show "Added""#)
        XCTAssertTrue(model.seen[0].last!.content.contains("Mason will check these itself"), "the model is told what will be checked")
        XCTAssertTrue(model.seen[2].last!.content.contains("this does not hold yet:\n- index.html shows a heading that reads “Orders”"), model.seen[2].last!.content)
        let receipt = try XCTUnwrap(session.turns[0].checks)
        XCTAssertEqual(receipt.results.map(\.state), ["passed", "passed"])
        XCTAssertEqual(receipt.repairs, 1)
        XCTAssertEqual(receipt.summary, "Mason checked 2 requirements itself: all hold · handed back 1 time")
        XCTAssertEqual(session.turns[0].answer, "Fixed the heading.")

        // Outside Auto nothing is derived or run.
        var reviewed = try store.create(workspace: workspace.root.path, permission: .workspaceEdits)
        await Agent(store: store, model: ScriptedModel([ModelReply(text: "Ok.")]), approve: { _ in true }, emit: { _ in }).run(&reviewed, request: #"In index.html show a heading "Nope""#)
        XCTAssertNil(reviewed.turns[0].checks)
    }
}

final class CatalogTests: XCTestCase {
    let request = """
    Build a whimsical bear market as a single offline index.html in this directory. Honey costs $8 each. Berries are $3 each; buy three or more Berries and they are $2.50 each. Salmon is $12 each.
    Shoppers can add items to a cart and see the total. Checkout must clear the cart and show a new receipt that starts with BEAR-, and the receipt persists after reload.
    """
    let extraction = #"{"Items": [{"Name": "Honey", "Price": 8, "SalePrice": 8, "Threshold": 0}, {"Name": "Berries", "Price": 3, "SalePrice": 2.5, "Threshold": 3}, {"Name": "Salmon", "Price": 12, "SalePrice": 2.5, "Threshold": 3}], "ReceiptPrefix": "BEAR-", "PersistReceipt": true, "ClearCart": true, "OtherRequirements": ["Shoppers can add items to a cart and see the total."]}"#
    let design = ##"{"Title": "The Bear Market", "Introduction": "Provisions for discerning bears, only $1!", "Accent": "#3b2f1e", "Background": "not a colour", "Descriptions": ["Golden <b>honey</b>", "Wild berries", "River salmon"]}"##

    func testTheOffersAreReadFromTheRequestAndCheckedAgainstIt() async throws {
        XCTAssertEqual(Catalog.amounts(request), [800, 300, 250, 1200])
        XCTAssertEqual(Catalog.explicitReceiptPrefix(request), "BEAR-")
        XCTAssertTrue(Catalog.requiresFreshReceipt(request))
        let read = await Catalog.extract(request, model: ScriptedModel([ModelReply(text: extraction)]))
        let spec = try XCTUnwrap(read)
        XCTAssertEqual(spec.items, [CatalogItem(name: "Honey", cents: 800), CatalogItem(name: "Berries", cents: 300, saleCents: 250, threshold: 3), CatalogItem(name: "Salmon", cents: 1200)],
                       "the discount the model copied onto Salmon is dropped: the request gives it only to Berries")
        XCTAssertTrue(spec.otherRequirements.isEmpty, "a clause the component already covers is not an extra requirement")
        XCTAssertTrue(Catalog.complete(spec, request: request))

        // A price the model got wrong is put right from the request's own words; a product the request never names is refused.
        let invented = extraction.replacingOccurrences(of: #""Price": 12"#, with: #""Price": 11"#)
        let corrected = await Catalog.extract(request, model: ScriptedModel([ModelReply(text: invented)]))
        XCTAssertEqual(corrected?.items.last, CatalogItem(name: "Salmon", cents: 1200))
        let stranger = extraction.replacingOccurrences(of: "Salmon", with: "Trout")
        let unknown = await Catalog.extract(request, model: ScriptedModel([ModelReply(text: stranger)]))
        XCTAssertNil(unknown)
        // Something the component does not do keeps the request on the ordinary path.
        let extra = extraction.replacingOccurrences(of: "Shoppers can add items to a cart and see the total.", with: "Email the receipt to the shopper.")
        let other = await Catalog.extract(request, model: ScriptedModel([ModelReply(text: extra)]))
        let more = try XCTUnwrap(other)
        XCTAssertFalse(Catalog.complete(more, request: request))

        let scenarios = Catalog.scenarios(spec, request: request)
        XCTAssertEqual(scenarios.map(\.id), ["offer-0", "offer-1", "discount-1", "offer-2", "order"])
        XCTAssertEqual(scenarios[2].steps.map { "\($0.op) \($0.value)" }, ["click ", "click ", "money 6.00", "click ", "money 7.50"], "two at the full price, then three at the sale price")
        XCTAssertEqual(scenarios[4].steps.map(\.op), ["click", "click", "capture", "money", "empty", "reload", "same", "click", "click", "new_id", "reload", "same"])

        let made = await Catalog.design(request, spec: spec, model: ScriptedModel([ModelReply(text: design)]))
        XCTAssertEqual(made.introduction, "", "copy that states a price is left out")
        let page = Catalog.render(spec, made)
        XCTAssertTrue(page.contains("<h1>The Bear Market</h1>") && page.contains("--accent: #3b2f1e") && page.contains("--paper: #f6f1e2"), "a colour that is not one falls back")
        XCTAssertTrue(page.contains("Golden &lt;b&gt;honey&lt;/b&gt;") && page.contains(#"{name: "Berries", cents: 300, saleCents: 250, threshold: 3}"#) && page.contains(#"const prefix = "BEAR-";"#))
        XCTAssertTrue(page.contains("<p>Buy 3 or more for $2.50 each.</p>"))
    }

    func testAnEmptyProjectGetsTheStoreAndItPassesItsOwnScenarios() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-catalog-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        try FileManager.default.createDirectory(at: base.appendingPathComponent("project"), withIntermediateDirectories: true)
        let workspace = try Workspace(path: base.appendingPathComponent("project").path)
        XCTAssertTrue(Catalog.applies(request, workspace: workspace))
        XCTAssertFalse(Catalog.applies("Fix the login bug", workspace: workspace))
        let store = try SessionStore(home: base.appendingPathComponent("home"))
        var session = try store.create(workspace: workspace.root.path, permission: .auto)
        let model = ScriptedModel([ModelReply(text: extraction), ModelReply(text: design)])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: request)
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(model.seen.count, 2, "the model read the offers and wrote the copy; Mason wrote the page")
        XCTAssertTrue(try workspace.read("index.html").contains("The Bear Market"))
        XCTAssertEqual(session.turns[0].changes.map(\.path), ["index.html"], "the page can be undone like any other change")
        XCTAssertEqual(session.activities.map(\.tool), ["write_file"])
        XCTAssertTrue(session.turns[0].answer.contains("Honey at $8.00, Berries at $3.00 ($2.50 each from 3), Salmon at $12.00"))
        let receipt = try XCTUnwrap(session.turns[0].checks)
        if BrowserCheck.findBrowser() != nil {
            XCTAssertEqual(receipt.results.map(\.state), ["passed", "passed", "passed", "passed", "passed"], receipt.report)
        } else {
            XCTAssertTrue(receipt.results.allSatisfy { $0.state == "notRun" })
            XCTAssertTrue(session.turns[0].answer.contains("the page was not checked"))
        }
        XCTAssertFalse(Catalog.applies(request, workspace: workspace), "only an empty project gets the component")
    }
}

final class LanguageServerTests: XCTestCase {
    func testAnInstalledLanguageServerAnswersForItsLanguageAfterApproval() async throws {
        guard let python = TransformersRuntime.python() else { throw XCTSkip("No python3 on this computer.") }
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-lsp-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        let folder = base.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try "int total(int a, int b) {\n    return a + b;\n}\nint main() { return total(1, count); }\n".write(to: folder.appendingPathComponent("sum.c"), atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: folder.path)
        let fake = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("fake-language-server.py").path
        setenv("MASON_LANGUAGE_SERVERS", "{\"c\": [\"\(python)\", \"\(fake)\"]}", 1)
        defer { unsetenv("MASON_LANGUAGE_SERVERS") }
        let server = try XCTUnwrap(LanguageServers.server(for: "c"))
        XCTAssertEqual(server.languageId, "c")
        XCTAssertNil(LanguageServers.server(for: "txt"))

        // What the server is sent is a well-formed conversation, and what it sends back is read frame by frame.
        let sent = LanguageServers.messages(in: LanguageServers.conversation(root: workspace.root, file: workspace.root.appendingPathComponent("sum.c"), text: "x", languageId: "c", line: 4, column: 21, mode: "references"))
        XCTAssertEqual(sent.map { $0["method"] as? String ?? "" }, ["initialize", "initialized", "textDocument/didOpen", "textDocument/references", "shutdown", "exit"])
        XCTAssertEqual(((sent[3]["params"] as? [String: Any])?["position"] as? [String: Int]), ["line": 3, "character": 20])

        let definition = try await LanguageServers.query(workspace, server: server, path: "sum.c", line: 4, column: 21, mode: "definitions", sandboxed: false)
        XCTAssertEqual(definition, "\(server.name): 1 definitions in the project.\nsum.c:1:5  int total(int a, int b) {")
        let references = try await LanguageServers.query(workspace, server: server, path: "sum.c", line: 4, column: 21, mode: "references", sandboxed: false)
        XCTAssertTrue(references.hasPrefix("\(server.name): 2 references in the project, and 1 outside it (libraries).\nsum.c:1:5"), references)
        XCTAssertTrue(references.contains("sum.c:4:21  int main() { return total(1, count); }"))
        let diagnostics = try await LanguageServers.query(workspace, server: server, path: "sum.c", line: 1, column: 1, mode: "diagnostics", sandboxed: false)
        XCTAssertEqual(diagnostics, "\(server.name): 1 diagnostic for sum.c.\nsum.c:3:12  error  use of undeclared identifier 'count'")

        // Through the agent it asks first, like a command, and a read-only session may not start it.
        let store = try SessionStore(home: base.appendingPathComponent("home"))
        var session = try store.create(workspace: workspace.root.path, permission: .reviewEdits)
        XCTAssertTrue(Agent.extraTools(session: session, workspace: workspace).contains { $0.name == "inspect_symbol" })
        let ask = call("inspect_symbol", ["path": "sum.c", "line": "4", "column": "21", "mode": "definitions"])
        var asked: [String] = [], answers = [true, false]
        let agent = Agent(store: store, model: ScriptedModel([ask, ask, ModelReply(text: "Found.")]), approve: { asked.append($0.subject); return answers.removeFirst() }, emit: { _ in })
        await agent.run(&session, request: "Where is total defined?")
        XCTAssertEqual(asked, ["language server \(server.name)", "language server \(server.name)"])
        XCTAssertEqual(session.activities.map(\.state), [.completed, .denied])
        XCTAssertTrue(session.activities[0].result.contains("sum.c:1:5"))
        session.permission = .readOnly; session.askOnly = true
        await Agent(store: store, model: ScriptedModel([ask, ModelReply(text: "No.")]), approve: { _ in XCTFail("a read-only session must not ask"); return false }, emit: { _ in }).run(&session, request: "Again")
        XCTAssertEqual(session.activities.last?.state, .failed)
        XCTAssertTrue(session.activities.last?.result.contains("read-only") ?? false)
    }
}

final class TransformersTests: XCTestCase {
    /// A stand-in for Python: it writes the status file the real server writes, then serves the stub model.
    func testAFolderOfTransformersWeightsIsListedCheckedAndServed() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-tf-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        let folder = base.appendingPathComponent("hub/models--acme--tiny-coder/snapshots/abc"), scripts = base.appendingPathComponent("scripts")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: scripts, withIntermediateDirectories: true)
        for name in ["config.json", "tokenizer_config.json", "tokenizer.json"] { try "{}".write(to: folder.appendingPathComponent(name), atomically: true, encoding: .utf8) }
        try Data(count: 4096).write(to: folder.appendingPathComponent("model.safetensors"))
        try "# stand-in".write(to: scripts.appendingPathComponent("Serve-Transformers.py"), atomically: true, encoding: .utf8)
        let stub = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("stub-model.py").path
        let python = base.appendingPathComponent("fake-python")
        try """
        #!/bin/sh
        while [ $# -gt 0 ]; do case "$1" in --model-id) id="$2"; shift;; --port) port="$2"; shift;; --manifest) manifest="$2"; shift;; --model-dir) dir="$2"; shift;; --check-only) check=1;; esac; shift; done
        if [ -f "$dir/broken" ]; then printf '{"status": "Tokenizer files are missing. Use the complete local model folder.", "error": true}' > "$manifest"; exit 1; fi
        printf '{"status": "Setup ready.", "contextTokens": 2048}' > "$manifest"
        [ -n "$check" ] && exit 0
        STUB_NAME="$id" exec python3 '\(stub)' "$port"
        """.write(to: python, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: python.path)
        setenv("MASON_TRANSFORMERS_DIR", scripts.path, 1); setenv("MASON_PYTHON", python.path, 1); setenv("MASON_DATA_HOME", base.appendingPathComponent("home").path, 1)
        defer { unsetenv("MASON_TRANSFORMERS_DIR"); unsetenv("MASON_PYTHON"); unsetenv("MASON_DATA_HOME") }

        let listed = ModelLibrary.scan([("Hugging Face cache", base.appendingPathComponent("hub"))])
        XCTAssertEqual(listed.map(\.name), ["acme/tiny-coder"])
        let model = listed[0]
        XCTAssertTrue(model.transformers && model.startable)
        XCTAssertEqual(model.bytes, 4096)
        XCTAssertEqual(model.verdict, "Coding agent · Transformers weights · runs through Python with PyTorch")
        XCTAssertThrowsError(try TransformersRuntime.model(at: base.path), "a folder without weights is not a model")
        XCTAssertEqual(try TransformersRuntime.model(at: folder.path).name, "acme/tiny-coder")

        let checked = try await TransformersRuntime.check(model)
        XCTAssertTrue(checked.hasPrefix("Setup ready. Context: 2048 tokens."), checked)

        let server = try await ModelServer.start(model, runtime: "", progress: { _ in })
        XCTAssertEqual(server.context, 2048, "the context the server settled on")
        let names = await OpenAIClient.models(at: server.endpoint.url)
        XCTAssertEqual(names, [server.endpoint.model])
        server.stop()
        for _ in 0..<40 where await OpenAIClient.models(at: server.endpoint.url, timeout: 0.3) != nil { try await Task.sleep(nanoseconds: 200_000_000) }
        let after = await OpenAIClient.models(at: server.endpoint.url, timeout: 0.3)
        XCTAssertNil(after, "stopping ends the Python process")

        // What the Python program reports is what the user is told.
        try "".write(to: folder.appendingPathComponent("broken"), atomically: true, encoding: .utf8)
        do { _ = try await ModelServer.start(model, runtime: "", progress: { _ in }); XCTFail("expected an error") }
        catch { XCTAssertEqual("\(error)", "Tokenizer files are missing. Use the complete local model folder.") }
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
