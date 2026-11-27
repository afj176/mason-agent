import XCTest
@testable import MasonCore

/// Replays fixed replies, like a model that always makes the same decisions.
final class ScriptedModel: ChatModel {
    let name = "scripted"
    var replies: [ModelReply]
    var seen: [[ChatMessage]] = []
    /// The names of the tools offered with each request.
    var seenTools: [[String]] = []
    var window: Int?
    init(_ replies: [ModelReply]) { self.replies = replies }
    func contextWindow() async -> Int? { window }
    /// Called at the start of each request, with its number, so a test can act while the "model" is busy.
    var onRequest: ((Int) -> Void)?
    var delay: UInt64 = 0
    func complete(messages: [ChatMessage], tools: [ToolSpec], onText: @escaping (String) -> Void) async throws -> ModelReply {
        seen.append(messages)
        seenTools.append(tools.map(\.name))
        onRequest?(seen.count)
        if delay > 0 { try await Task.sleep(nanoseconds: delay) }
        guard !replies.isEmpty else { throw MasonError("The script ran out of replies.") }
        let reply = replies.removeFirst()
        if !reply.text.isEmpty { onText(reply.text) }
        return reply
    }
}

/// A model whose every request fails with the same error: an unreachable server, a stop, anything else.
final class ThrowingModel: ChatModel {
    let name = "throwing"
    let error: Error
    init(_ error: Error) { self.error = error }
    func complete(messages: [ChatMessage], tools: [ToolSpec], onText: @escaping (String) -> Void) async throws -> ModelReply { throw error }
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

    // MARK: repeat stop

    func testARepeatedWriteWithTheSameResultIsWarnedThenSkippedAndTheWorkIsKept() async throws {
        var (store, session) = try session(.workspaceEdits)
        // The same call each time; spacing and key order differ, which does not make it a different call.
        let write = { (id: String, arguments: String) in ModelReply(toolCalls: [ToolCall(id: id, name: "write_file", arguments: arguments)]) }
        let model = ScriptedModel([
            write("w1", #"{"path":"index.html","content":"<h1>Hi</h1>"}"#),
            write("w2", #"{"content": "<h1>Hi</h1>", "path": "index.html"}"#),
            write("w3", #"{"path":"index.html","content":"<h1>Hi</h1>"}"#),
            write("w4", #"{"path":"index.html","content":"<h1>Hi</h1>"}"#),
            ModelReply(text: "Not reached."),
        ])
        let page = folder.appendingPathComponent("index.html")
        // Before the fourth request the file is changed by hand, so a fourth write would show.
        model.onRequest = { if $0 == 4 { try? "mine".write(to: page, atomically: true, encoding: .utf8) } }
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Make a page")
        // Created, then edited to the same text twice (the same result), then not run.
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed, .completed, .skipped])
        XCTAssertEqual(session.activities.last?.result, RepeatStop.result)
        XCTAssertEqual(try String(contentsOf: page, encoding: .utf8), "mine", "the skipped call is not run")
        XCTAssertEqual(model.replies.count, 1, "the request ends at the repeat")
        // The model was warned exactly once, after the second identical result, and the activity rows do not carry it.
        let tools = session.messages.filter { $0.role == "tool" }
        XCTAssertEqual(tools.filter { $0.content.hasSuffix(RepeatStop.warning) }.map(\.toolCallId), ["w3"])
        XCTAssertFalse(session.activities.contains { $0.result.contains("Mason will not run") })
        // A repeat stop is always Stopped, never Completed or Failed; what landed is kept.
        XCTAssertEqual(session.state, "Stopped")
        XCTAssertEqual(session.ending, RunEnding(code: "model_loop", kept: 1))
        XCTAssertEqual(session.repeatStop, RepeatStop(tool: "write_file", subject: "index.html", times: 4, model: "scripted"))
        XCTAssertEqual(session.detail, "scripted kept repeating write file index.html with the same result, so Mason stopped it. Your work so far is kept.")
        XCTAssertEqual(session.repeatStop?.explanation(kept: true),
                       "scripted asked for write file index.html 4 times in a row and got the same result each time. Mason skipped the last one and stopped here instead of looping. Your work so far is kept.")
        // The skipped call is still answered, so the next request is valid for the model.
        XCTAssertEqual(tools.last?.content, RepeatStop.result)
        XCTAssertEqual(tools.last?.toolCallId, "w4")
        // The record and the ending are kept with the session.
        let reloaded = try SessionStore(home: home).sessions.first { $0.id == session.id }
        XCTAssertEqual(reloaded?.repeatStop?.subject, "index.html")
        XCTAssertEqual(reloaded?.ending, RunEnding(code: "model_loop", kept: 1))
    }

    func testARepeatedReadWithNothingChangedStopsAndTheNextRequestClearsTheRecord() async throws {
        var (store, session) = try session(.reviewEdits)
        let read = call("read_file", ["path": "calc.py"])
        await Agent(store: store, model: ScriptedModel([read, read, read, ModelReply(text: "Not reached.")]), approve: { _ in true }, emit: { _ in }).run(&session, request: "Explain calc")
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed, .skipped])
        XCTAssertEqual(session.state, "Stopped")
        XCTAssertEqual(session.ending, RunEnding(code: "model_loop", kept: 0))
        XCTAssertEqual(session.repeatStop, RepeatStop(tool: "read_file", subject: "calc.py", times: 3, model: "scripted"))
        XCTAssertEqual(session.detail, "scripted kept repeating read file calc.py with the same result, so Mason stopped it. Nothing was changed.")
        XCTAssertEqual(session.repeatStop?.explanation(kept: false),
                       "scripted asked for read file calc.py 3 times in a row and got the same result each time. Mason skipped the last one and stopped here instead of looping. Nothing was changed.")

        await Agent(store: store, model: ScriptedModel([ModelReply(text: "It subtracts.")]), approve: { _ in true }, emit: { _ in }).run(&session, request: "And now?")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertNil(session.repeatStop)
        XCTAssertEqual(session.ending, RunEnding(code: "done", kept: 0))
        // Without a model name the sentence says "The model".
        XCTAssertTrue(RepeatStop(tool: "write_file", subject: "a.txt", model: "").detail(kept: true).hasPrefix("The model kept repeating write file a.txt"))
    }

    func testADifferentCallInBetweenResetsTheCount() async throws {
        var (store, session) = try session(.reviewEdits)
        let a = call("read_file", ["path": "calc.py"]), b = call("list_files", ["path": "."])
        await Agent(store: store, model: ScriptedModel([a, a, b, a, a, ModelReply(text: "Read it.")]), approve: { _ in true }, emit: { _ in }).run(&session, request: "Look around")
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed, .completed, .completed, .completed])
        XCTAssertEqual(session.state, "Completed")
        XCTAssertNil(session.repeatStop)
        XCTAssertEqual(session.detail, "")
    }

    func testIdenticalCallsWhoseResultChangesAreNeverStopped() async throws {
        var (store, session) = try session(.reviewEdits)
        let read = call("read_file", ["path": "calc.py"])
        let model = ScriptedModel([read, read, read, read, ModelReply(text: "It changes.")])
        let file = folder.appendingPathComponent("calc.py")
        // The file is rewritten before every request, so each identical read returns something new.
        model.onRequest = { try? "x = \($0)\n".write(to: file, atomically: true, encoding: .utf8) }
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Watch calc")
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed, .completed, .completed])
        XCTAssertEqual(session.state, "Completed")
        XCTAssertNil(session.repeatStop)
        XCTAssertFalse(session.messages.contains { $0.content.contains(RepeatStop.warning) })
    }

    func testLoopingNeedsTwoEqualResultsThatDidNotWait() {
        typealias Attempt = Agent.Attempt
        XCTAssertFalse(Agent.looping([]))
        XCTAssertFalse(Agent.looping([Attempt(result: "a", waited: false)]))
        XCTAssertTrue(Agent.looping([Attempt(result: "a", waited: false), Attempt(result: "a", waited: false)]))
        XCTAssertFalse(Agent.looping([Attempt(result: "a", waited: false), Attempt(result: "b", waited: false)]))
        // A poll that waited for its job is patience, not a loop, whichever of the two waited.
        XCTAssertFalse(Agent.looping([Attempt(result: "running", waited: true), Attempt(result: "running", waited: false)]))
        XCTAssertFalse(Agent.looping([Attempt(result: "running", waited: false), Attempt(result: "running", waited: true)]))
        // Only the last two count.
        XCTAssertTrue(Agent.looping([Attempt(result: "a", waited: true), Attempt(result: "b", waited: false), Attempt(result: "b", waited: false)]))
    }

    // MARK: run endings

    func testEveryEndingCodeHasAStyleAndAnUnknownOneHasNone() {
        XCTAssertEqual(RunEnding.codes.count, 11)
        for code in RunEnding.codes { XCTAssertNotNil(RunEnding.style(code), code) }
        XCTAssertNil(RunEnding.style("something_else"))
        XCTAssertNil(RunEnding.style(""))
        XCTAssertEqual(RunEnding.style("model_loop"), RunEnding.Style(glyph: "↻", tone: .warning, headline: "Model looped", cause: "model"))
        XCTAssertEqual(RunEnding.style("stopped")?.tone, .neutral)
        XCTAssertEqual(RunEnding.style("model_unavailable")?.tone, .danger)
        XCTAssertEqual(RunEnding.hint("step_limit"), "Send another message to continue from here.")
        XCTAssertNil(RunEnding.hint("done"))
        XCTAssertEqual(RunEnding.keptText(1), "1 file kept")
        XCTAssertEqual(RunEnding.keptText(2), "2 files kept")
    }

    func testAFinishedRequestIsDoneWithItsKeptFilesAndANewRequestClearsTheEnding() async throws {
        var (store, session) = try session(.workspaceEdits)
        await Agent(store: store, model: ScriptedModel([call("write_file", ["path": "a.txt", "content": "a"]), ModelReply(text: "Done.")]), approve: { _ in true }, emit: { _ in }).run(&session, request: "Add a")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(session.ending, RunEnding(code: "done", kept: 1))
        XCTAssertEqual(try SessionStore(home: home).sessions.first { $0.id == session.id }?.ending, RunEnding(code: "done", kept: 1))
        // The next request starts without one, and ends with its own.
        let id = session.id, home: URL = self.home
        var whileRunning: RunEnding? = RunEnding(code: "not read")
        let model = ScriptedModel([ModelReply(text: "Nothing to do.")])
        model.onRequest = { _ in whileRunning = (try? SessionStore(home: home))?.sessions.first(where: { $0.id == id })?.ending }
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Anything else?")
        XCTAssertNil(whileRunning, "the ending is cleared when a request starts")
        XCTAssertEqual(session.ending, RunEnding(code: "done", kept: 0))
    }

    func testAQuestionEndsAsQuestion() async throws {
        var (store, session) = try session(.reviewEdits)
        await Agent(store: store, model: ScriptedModel([call("ask_user", ["question": "Which file?"])]), approve: { _ in true }, emit: { _ in }).run(&session, request: "Fix it")
        XCTAssertEqual(session.state, "Needs your answer")
        XCTAssertEqual(session.ending, RunEnding(code: "question", kept: 0))
    }

    func testTheStepLimitPausesAsStoppedAndKeepsTheFiles() async throws {
        var (store, session) = try session(.workspaceEdits)
        let agent = Agent(store: store, model: ScriptedModel([call("write_file", ["path": "a.txt", "content": "a"]), call("write_file", ["path": "b.txt", "content": "b"]), ModelReply(text: "Not reached.")]),
                          approve: { _ in true }, emit: { _ in })
        agent.stepLimit = 2
        await agent.run(&session, request: "Add two files")
        XCTAssertEqual(session.state, "Stopped")
        XCTAssertEqual(session.ending, RunEnding(code: "step_limit", kept: 2))
        XCTAssertEqual(session.detail, "Stopped after 2 steps. Send another message to continue.")
    }

    func testTheSameFailureThreeTimesIsModelStuck() async throws {
        var (store, session) = try session(.workspaceEdits)
        // Three different edits of the same file, each failing the same way: not one repeated call, the same failure.
        let miss = { (text: String) in call("edit_file", ["path": "calc.py", "old_text": text, "new_text": "x"]) }
        let model = ScriptedModel([miss("nope one"), miss("nope two"), miss("nope three"), ModelReply(text: "Not reached.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Edit calc")
        XCTAssertEqual(session.activities.map(\.state), [.failed, .failed, .failed])
        XCTAssertEqual(model.replies.count, 1)
        XCTAssertEqual(session.state, "Stopped")
        XCTAssertEqual(session.ending, RunEnding(code: "model_stuck", kept: 0))
        XCTAssertEqual(session.detail, "The same operation failed three times without a successful tool action. See activity for the last error.")
    }

    func testASuccessfulCallInBetweenClearsTheFailures() async throws {
        var (store, session) = try session(.workspaceEdits)
        let miss = { (text: String) in call("edit_file", ["path": "calc.py", "old_text": text, "new_text": "x"]) }
        let model = ScriptedModel([miss("nope one"), miss("nope two"), call("read_file", ["path": "calc.py"]), miss("nope three"), ModelReply(text: "Gave up.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Edit calc")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(session.ending?.code, "done")
    }

    func testTwoRejectedPathsInARowAreModelStuck() async throws {
        var (store, session) = try session(.reviewEdits)
        let model = ScriptedModel([call("read_file", ["path": "../outside.txt"]), call("read_file", ["path": "/etc/hosts"]), ModelReply(text: "Not reached.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Read it")
        XCTAssertEqual(session.activities.map(\.state), [.failed, .failed])
        XCTAssertEqual(session.state, "Stopped")
        XCTAssertEqual(session.ending, RunEnding(code: "model_stuck", kept: 0))
        XCTAssertEqual(session.detail, "The model could not produce a valid path after correction. Use a relative path or try another model.")
        // A path the project rejects is told apart from other errors.
        do { _ = try Workspace(path: folder.path).resolve("../x"); XCTFail("../x must be rejected") }
        catch { XCTAssertEqual((error as? MasonError)?.invalidPath, true) }
        XCTAssertFalse(MasonError("Something else.").invalidPath)
    }

    func testTheTimeLimitPausesTheRequestAndAUserStopIsStillStopped() async throws {
        var (store, session) = try session(.reviewEdits)
        let slow = ScriptedModel([ModelReply(text: "Too late.")])
        slow.delay = 5_000_000_000
        let agent = Agent(store: store, model: slow, approve: { _ in true }, emit: { _ in })
        agent.timeLimit = 0.2
        await agent.run(&session, request: "Take your time")
        XCTAssertEqual(session.state, "Stopped")
        XCTAssertEqual(session.ending, RunEnding(code: "time_limit", kept: 0))
        XCTAssertTrue(session.detail.hasSuffix("time limit. Completed edits and receipts are kept."), session.detail)

        // The user's own stop, well inside the limit, ends as stopped.
        let (store2, start) = try self.session(.reviewEdits)
        let slower = ScriptedModel([ModelReply(text: "Too late.")])
        slower.delay = 5_000_000_000
        let stoppable = Agent(store: store2, model: slower, approve: { _ in true }, emit: { _ in })
        let running = Task { () -> Session in var working = start; await stoppable.run(&working, request: "Take your time"); return working }
        try await Task.sleep(nanoseconds: 200_000_000)
        running.cancel()
        let stopped = await running.value
        XCTAssertEqual(stopped.state, "Stopped")
        XCTAssertEqual(stopped.ending, RunEnding(code: "stopped", kept: 0))
        XCTAssertEqual(stopped.detail, "Stopped. Completed edits are kept.")
        XCTAssertEqual(Agent(store: store2, model: slower, approve: { _ in true }, emit: { _ in }).timeLimit, 30 * 60)
    }

    func testStoppingAnUnreachableModelAndOtherErrorsEachHaveTheirEnding() async throws {
        let cases: [(Error, String, String)] = [
            (CancellationError(), "Stopped", "stopped"),
            (MasonError.unavailable("The model server did not answer: Could not connect to the server."), "Failed", "model_unavailable"),
            (MasonError("Something else went wrong."), "Failed", "failed"),
        ]
        for (error, state, code) in cases {
            var (store, session) = try self.session(.reviewEdits)
            await Agent(store: store, model: ThrowingModel(error), approve: { _ in true }, emit: { _ in }).run(&session, request: "Fix add")
            XCTAssertEqual(session.state, state, code)
            XCTAssertEqual(session.ending, RunEnding(code: code, kept: 0))
        }
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

    func testAProjectsOwnTestIsPinnedAndIsTheEvidenceForItsRequirement() async throws {
        func put(_ name: String, _ text: String) throws {
            let file = folder.appendingPathComponent(name)
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try text.write(to: file, atomically: true, encoding: .utf8)
        }
        try put("mason.workflows.json", #"{"version": 1, "workflows": {"test": {"executable": "sh", "arguments": ["run-tests.sh"]}}}"#)
        try put("run-tests.sh", "if grep -q 'a + b' calc.py; then echo 'ok 1 - adds'; else echo 'not ok 1 - adds'; fi\necho 'ok 2 - other'\n")
        try put(".mason/tests.json", #"[{"requirement": "Make add return the sum", "workflow": "test", "format": "tap", "report": "", "test": "adds", "testFiles": ["run-tests.sh"], "inputs": ["calc.py"]}]"#)
        let workspace = try Workspace(path: folder.path)
        XCTAssertEqual(try ProjectTests.load(workspace).map(\.test), ["adds"])

        let plan = await Derived.prepare("Make add return the sum.", workspace: workspace, model: nil)
        XCTAssertEqual(plan.checks.map(\.kind), ["project_test"])
        XCTAssertEqual(ProjectTests.value(plan.checks[0], "baseline"), ["failed"], "the baseline is run before any work")
        var results = try await Derived.verify(plan, workspace: workspace)
        XCTAssertEqual(results[0].state, "failed")
        XCTAssertTrue(results[0].detail.contains("baseline: failed; now: failed"), results[0].detail)
        try put("calc.py", "def add(a, b):\n    return a + b\n")
        results = try await Derived.verify(plan, workspace: workspace)
        XCTAssertEqual(results[0].state, "passed", results[0].detail)
        XCTAssertTrue(results[0].detail.contains("baseline: failed; now: passed"), results[0].detail)
        // Making the test itself say yes is not passing it.
        try put("calc.py", "def add(a, b):\n    return a - b\n")
        try put("run-tests.sh", "echo 'ok 1 - adds'\n")
        results = try await Derived.verify(plan, workspace: workspace)
        XCTAssertEqual(results[0].state, "failed")
        XCTAssertTrue(results[0].detail.contains("pinned"), results[0].detail)
        let other = await Derived.prepare("Rename the function.", workspace: workspace, model: nil)
        XCTAssertFalse(other.checks.contains { $0.kind == "project_test" }, "only the stated requirement is bound")
        try put(".mason/tests.json", #"[{"requirement": "x", "workflow": "missing", "format": "tap", "test": "t", "testFiles": ["a"], "inputs": ["b"]}]"#)
        XCTAssertThrowsError(try ProjectTests.load(workspace), "a test must name a workflow the project has")

        let junit = #"<testsuite><testcase classname="Price" name="discount"/><testcase classname="Price" name="tax &amp; total"><failure message="no"/></testcase><testcase name="later"><skipped/></testcase></testsuite>"#
        XCTAssertEqual(ProjectTests.outcomes(format: "junit", text: junit), ["discount": "passed", "Price.discount": "passed", "tax & total": "failed", "Price.tax & total": "failed", "later": "skipped"])
        XCTAssertEqual(ProjectTests.outcomes(format: "trx", text: #"<UnitTestResult testName="Adds" outcome="Passed" /><UnitTestResult testName="Subtracts" outcome="Failed">"#), ["Adds": "passed", "Subtracts": "failed"])
        XCTAssertEqual(ProjectTests.outcomes(format: "go-json", text: "{\"Action\":\"run\",\"Test\":\"TestAdd\"}\n{\"Action\":\"pass\",\"Test\":\"TestAdd\"}\n{\"Action\":\"fail\",\"Test\":\"TestSub\"}\n{\"Action\":\"pass\"}"), ["TestAdd": "passed", "TestSub": "failed"])
        let flutter = "{\"type\":\"testStart\",\"test\":{\"id\":1,\"name\":\"adds\"}}\n{\"type\":\"testDone\",\"testID\":1,\"result\":\"success\",\"skipped\":false}\n{\"type\":\"testStart\",\"test\":{\"id\":2,\"name\":\"draws\"}}\n{\"type\":\"testDone\",\"testID\":2,\"result\":\"failure\"}"
        XCTAssertEqual(ProjectTests.outcomes(format: "flutter-json", text: flutter), ["adds": "passed", "draws": "failed"])
        let tap = ProjectTests.outcomes(format: "tap", text: "TAP version 13\n    ok 1 - inner\nok 1 - suite discount\nnot ok 2 - tax\nok 3 - later # SKIP not yet\nok 4 - tax\n")
        XCTAssertEqual(tap, ["inner": "passed", "suite discount": "passed", "tax": "failed", "later": "skipped"])
        XCTAssertEqual(ProjectTests.outcome(of: "discount", in: tap), "passed", "a name is found at the end of a longer one")
        XCTAssertNil(ProjectTests.outcome(of: "missing", in: tap))
    }

    func testTheWebsiteTabsPiecesAndTheToolsThatShowThingsToTheUser() async throws {
        let workspace = try Workspace(path: folder.path)
        try "<!doctype html><title>Shop</title><h1>Shop</h1><button id=\"add\">Add</button>".write(to: folder.appendingPathComponent("index.html"), atomically: true, encoding: .utf8)
        let preview = try PagePreview(workspace: workspace)
        XCTAssertTrue(SitePreview.listening(preview.port), "the project's pages are served on this computer")
        XCTAssertEqual(preview.address(of: "sub dir/index.html"), "http://127.0.0.1:\(preview.port)/sub%20dir/index.html")
        let served = try String(contentsOf: try XCTUnwrap(URL(string: preview.address(of: "index.html"))), encoding: .utf8)
        XCTAssertTrue(served.contains("<h1>Shop</h1>"), served)
        preview.stop()

        let message = ##"{"selector":"#add","kind":"button","label":"Add","html":"<button id=\"add\">Add</button>","console":["error: boom (app.js:3)"]}"##
        let pick = try XCTUnwrap(SitePreview.pick(from: message, page: "index.html"))
        XCTAssertEqual(pick.selector, "#add")
        let note = SitePreview.evidence(pick)
        XCTAssertTrue(note.contains("selector: `#add`") && note.contains("label: “Add”") && note.contains("error: boom (app.js:3)"), note)
        XCTAssertNil(SitePreview.pick(from: "{\"kind\":\"button\"}", page: "index.html"), "a message without a selector is not a pick")
        XCTAssertTrue(SitePreview.script.contains("messageHandlers.mason.postMessage") && SitePreview.script.hasPrefix("(function () {"))

        try #"{"version": 1, "workflows": {"dev": {"executable": "sh", "arguments": ["-c", "echo starting; exit 3"]}}}"#.write(to: folder.appendingPathComponent("mason.workflows.json"), atomically: true, encoding: .utf8)
        do { _ = try await SitePreview.DevServer(workspace: workspace, workflow: "dev", port: 18473); XCTFail("a workflow that ends is not a server") }
        catch { XCTAssertTrue("\(error)".contains("ended before anything listened"), "\(error)") }
        do { _ = try await SitePreview.DevServer(workspace: workspace, workflow: "none", port: 18473); XCTFail("an unknown workflow is refused") } catch {}

        var (store, session) = try session(.workspaceEdits)
        var shown: [String] = []
        let script = [call("open_file", ["path": "calc.py", "line": "2"]), call("open_preview", ["path": "index.html"]), call("open_preview", ["path": "calc.py"]), ModelReply(text: "Look there.")]
        let agent = Agent(store: store, model: ScriptedModel(script), approve: { _ in true }, emit: { _ in })
        agent.present = { kind, path, line in shown.append("\(kind) \(path) \(line)") }
        await agent.run(&session, request: "Show me")
        XCTAssertEqual(shown, ["file calc.py 2", "page index.html 1"], "a file and a page are shown; a page must be HTML")
        XCTAssertFalse(Agent.extraTools(session: session, workspace: workspace).contains { $0.name == "open_file" }, "without an app that can show things, the tools are not offered")
    }

    func testTheNetworkRelayTunnelsOnlyWhatTheUserAllowed() async throws {
        XCTAssertEqual(try NetworkPolicy.host("Registry.NPMJS.org", port: 443), "registry.npmjs.org")
        for (host, port) in [("registry.npmjs.org", 80), ("localhost", 443), ("printer.local", 443), ("10.0.0.5", 443), ("intranet", 443), ("a..b.com", 443), ("-bad.example.com", 443), ("db.internal", 443)] {
            XCTAssertThrowsError(try NetworkPolicy.host(host, port: port), "\(host):\(port) is not relayed")
        }
        for address: [UInt8] in [[8, 8, 8, 8], [93, 184, 216, 34], [0x26, 0x06, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]] { XCTAssertTrue(NetworkPolicy.isPublic(address), "\(address)") }
        let mapped: [UInt8] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 127, 0, 0, 1]
        for address: [UInt8] in [[127, 0, 0, 1], [10, 1, 2, 3], [192, 168, 1, 1], [172, 20, 0, 1], [169, 254, 169, 254], [100, 64, 0, 1], [224, 0, 0, 1], [0, 0, 0, 0], mapped,
                                 [0xfe, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1], [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1], [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]] {
            XCTAssertFalse(NetworkPolicy.isPublic(address), "\(address) is not on the public internet")
        }
        XCTAssertTrue(try NetworkSettings.command("", home: home).contains("no network"))
        XCTAssertTrue(try NetworkSettings.command("ask", home: home).contains("asks first"))
        XCTAssertTrue(NetworkSettings.asks(home: home))
        XCTAssertTrue(SandboxProfile.profile(workspace: "/w", scratch: "/s", relay: 4321).hasSuffix("(deny network*)\n(allow network-outbound (remote tcp \"localhost:4321\"))"))
        XCTAssertTrue(SandboxProfile.profile(workspace: "/w", scratch: "/s").hasSuffix("(deny network*)"), "without the relay the sandbox has no network at all")

        // A stand-in for a site: the project's page server on this computer. The test's resolver points names at it.
        try "<h1>upstream</h1>".write(to: folder.appendingPathComponent("index.html"), atomically: true, encoding: .utf8)
        let site = try PagePreview(workspace: try Workspace(path: folder.path))
        defer { site.stop() }
        var asked: [String] = []
        let lock = NSLock()
        let relay = try NetworkRelay(approve: { host in lock.lock(); asked.append(host); lock.unlock(); return host != "denied.example.com" },
                                     resolve: { _ in [([127, 0, 0, 1], site.port)] }, publicOnly: false)
        defer { relay.stop() }
        func exchange(_ request: String, then follow: String? = nil) -> String {
            let descriptor = NetworkRelay.connected(to: ([127, 0, 0, 1], relay.port))!
            defer { close(descriptor) }
            NetworkRelay.send(descriptor, Array(request.utf8))
            var received = [UInt8](), buffer = [UInt8](repeating: 0, count: 4096)
            var timeout = timeval(tv_sec: 5, tv_usec: 0)
            setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
            var sentFollow = follow == nil
            while true {
                let count = recv(descriptor, &buffer, buffer.count, 0)
                if count <= 0 { break }
                received.append(contentsOf: buffer[0..<count])
                if !sentFollow, NetworkRelay.ends(received) { NetworkRelay.send(descriptor, Array(follow!.utf8)); sentFollow = true }
                if String(decoding: received, as: UTF8.self).contains("</h1>") { break }
            }
            return String(decoding: received, as: UTF8.self)
        }
        let through = exchange("CONNECT files.example.com:443 HTTP/1.1\r\nHost: files.example.com:443\r\n\r\n", then: "GET /index.html HTTP/1.1\r\nHost: files.example.com\r\nConnection: close\r\n\r\n")
        XCTAssertTrue(through.hasPrefix("HTTP/1.1 200 Connection established") && through.contains("<h1>upstream</h1>"), "an allowed host is tunnelled: " + through.prefix(200))
        _ = exchange("CONNECT files.example.com:443 HTTP/1.1\r\n\r\n", then: "GET /index.html HTTP/1.1\r\nConnection: close\r\n\r\n")
        XCTAssertTrue(exchange("CONNECT denied.example.com:443 HTTP/1.1\r\n\r\n").hasPrefix("HTTP/1.1 403"), "a host the user refused is not reached")
        XCTAssertTrue(exchange("CONNECT localhost:443 HTTP/1.1\r\n\r\n").hasPrefix("HTTP/1.1 403"), "this computer is never a destination")
        XCTAssertTrue(exchange("CONNECT files.example.com:22 HTTP/1.1\r\n\r\n").hasPrefix("HTTP/1.1 403"), "only port 443")
        XCTAssertTrue(exchange("GET http://files.example.com/ HTTP/1.1\r\n\r\n").hasPrefix("HTTP/1.1 405"), "plain HTTP is not relayed")
        XCTAssertEqual(asked, ["files.example.com", "denied.example.com"], "each host is asked about once, and a refused destination never")
        XCTAssertTrue(relay.receipts.contains { $0.hasPrefix("allowed: files.example.com") } && relay.receipts.contains("denied by you: denied.example.com"), "\(relay.receipts)")

        // The same relay with its real rule refuses a name that resolves to this computer.
        let strict = try NetworkRelay(approve: { _ in true }, resolve: { _ in [([127, 0, 0, 1], site.port)] })
        defer { strict.stop() }
        let descriptor = try XCTUnwrap(NetworkRelay.connected(to: ([127, 0, 0, 1], strict.port)))
        NetworkRelay.send(descriptor, Array("CONNECT rebind.example.com:443 HTTP/1.1\r\n\r\n".utf8))
        var buffer = [UInt8](repeating: 0, count: 512)
        let count = recv(descriptor, &buffer, buffer.count, 0)
        close(descriptor)
        XCTAssertTrue(String(decoding: buffer[0..<max(0, count)], as: UTF8.self).hasPrefix("HTTP/1.1 502"), "a public name that points at a private address is not connected to")
    }

    func testAPolicySwitchesToolsOffAndHooksGateTheWork() async throws {
        let workspace = try Workspace(path: folder.path)
        try #"{"version": 1, "workflows": {"lint": {"executable": "sh", "arguments": ["-c", "! grep -q TODO calc.py"], "events": ["afterEdit"]}, "gate": {"executable": "sh", "arguments": ["-c", "grep -q 'a + b' calc.py"], "beforeVerify": true}, "deploy": {"executable": "sh", "arguments": ["-c", "touch deployed"]}}}"#
            .write(to: folder.appendingPathComponent("mason.workflows.json"), atomically: true, encoding: .utf8)
        XCTAssertEqual(try ProjectWorkflows.load(workspace).workflows["gate"]?.events, ["beforeVerify"])
        XCTAssertTrue(try Policies.command("", workspace: workspace, home: home).contains("no policy"))
        _ = try Policies.command("disable run_shell start_job", workspace: workspace, home: home)
        _ = try Policies.command("workflows lint gate", workspace: workspace, home: home)
        let described = try Policies.command("hooks on", workspace: workspace, home: home)
        XCTAssertTrue(described.contains("tools off   run_shell, start_job") && described.contains("workflows   gate, lint") && described.contains("hooks       on"), described)
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent(".mason/policy.json").path), "the policy is not kept in the project")

        var (store, session) = try session(.auto)
        let model = ScriptedModel([
            call("run_shell", ["command": "touch made"]),
            call("run_workflow", ["name": "deploy"]),
            call("write_file", ["path": "calc.py", "content": "# TODO\ndef add(a, b):\n    return a - b\n"]),
            ModelReply(text: "Done."),
            call("write_file", ["path": "calc.py", "content": "def add(a, b):\n    return a + b\n"]),
            ModelReply(text: "Fixed."),
        ])
        let agent = Agent(store: store, model: model, approve: { _ in true }, emit: { _ in })
        agent.hooksNeedSandbox = false
        agent.derivesChecks = false
        await agent.run(&session, request: "Fix add")
        XCTAssertFalse(model.seen.first?.contains { $0.role == "system" && $0.content.contains("run_shell") } ?? false)
        XCTAssertTrue(session.activities[0].result.contains("does not allow run_shell"), session.activities[0].result)
        XCTAssertTrue(session.activities[1].result.contains("does not allow the workflow deploy"), session.activities[1].result)
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("made").path) || FileManager.default.fileExists(atPath: folder.appendingPathComponent("deployed").path))
        let afterEdit = session.messages.first { $0.role == "tool" && $0.content.contains("The hook lint (afterEdit) failed") }
        XCTAssertNotNil(afterEdit, "an afterEdit hook's failure is on the edit's result")
        XCTAssertTrue(session.messages.contains { $0.role == "user" && $0.content.contains("The hook gate (beforeVerify) failed") }, "a failed gate goes back to the model")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(session.turns.last?.answer, "Fixed.")

        // A gate that keeps failing blocks completion.
        var (store2, stuck) = try self.session(.auto)
        let giving = Agent(store: store2, model: ScriptedModel([call("write_file", ["path": "calc.py", "content": "def add(a, b):\n    return a - b\n"]), ModelReply(text: "Done."), ModelReply(text: "Done."), ModelReply(text: "Done."), ModelReply(text: "Done.")]), approve: { _ in true }, emit: { _ in })
        giving.hooksNeedSandbox = false
        giving.derivesChecks = false
        await giving.run(&stuck, request: "Fix add")
        XCTAssertEqual(stuck.state, "Stopped")
        XCTAssertTrue(stuck.detail.contains("hook still fails"), stuck.detail)

        _ = try Policies.command("clear", workspace: workspace, home: home)
        XCTAssertNil(Policies.load(home: home, workspace: folder.path))
        var lapsed = ProjectPolicy(name: "old", workspace: realPath(folder.path), disabledTools: ["read_file"], workflows: nil, expires: Date().addingTimeInterval(-60), hooks: false)
        try Policies.set(lapsed, home: home, workspace: folder.path)
        XCTAssertNil(Policies.load(home: home, workspace: folder.path), "a lapsed policy is not in force")
        lapsed.expires = nil
        try Policies.set(lapsed, home: home, workspace: folder.path)
        XCTAssertThrowsError(try Policies.load(home: home, workspace: folder.path)?.check("read_file"))
    }

    func testScriptsCanReviewAFileAndServeAProject() async throws {
        var lines: [String] = []
        func events() -> [[String: Any]] { lines.compactMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any] } }
        let finding = ModelReply(text: #"[{"path": "calc.py", "line": 2, "evidence": "return a - b", "problem": "It subtracts."}, {"path": "calc.py", "line": 1, "evidence": "not on that line", "problem": "Made up."}]"#)
        var code = await ScriptOperations.run(["review", "--workspace", folder.path, "--prompt", "add returns the sum", "--path", "calc.py"], model: ScriptedModel([finding])) { lines.append($0) }
        XCTAssertEqual(code, 2, "a finding is exit 2: " + lines.joined())
        let result = events().first?["data"] as? [String: Any]
        XCTAssertEqual((result?["findings"] as? [[String: Any]])?.count, 1)
        XCTAssertEqual(((result?["findings"] as? [[String: Any]])?.first)?["line"] as? Int, 2)
        XCTAssertEqual(result?["dropped"] as? Int, 1, "a finding that does not quote its line is not reported as fact")
        lines = []
        code = await ScriptOperations.run(["review", "--workspace", folder.path, "--prompt", "x", "--path", "calc.py"], model: ScriptedModel([ModelReply(text: "[]")])) { lines.append($0) }
        XCTAssertEqual(code, 0)

        try #"{"version": 1, "workflows": {"dev": {"executable": "sh", "arguments": ["-c", "exit 3"]}}}"#.write(to: folder.appendingPathComponent("mason.workflows.json"), atomically: true, encoding: .utf8)
        lines = []
        code = await ScriptOperations.run(["serve", "--workspace", folder.path, "--workflow", "dev", "--port", "18474"]) { lines.append($0) }
        XCTAssertEqual(code, 1)
        XCTAssertTrue(((events().first?["data"] as? [String: Any])?["message"] as? String ?? "").contains("ended before anything listened"))
    }

    func testABundleBringsSkillsAndDocumentationToEveryProject() async throws {
        let workspace = try Workspace(path: folder.path)
        let source = home.appendingPathComponent("shop-kit")
        func put(_ name: String, _ text: String, under root: URL) throws {
            let file = root.appendingPathComponent(name)
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try text.write(to: file, atomically: true, encoding: .utf8)
        }
        try put("bundle.json", #"{"name": "shop-kit", "version": "1.2.0", "description": "How the shop team works"}"#, under: source)
        try put("skills/release/SKILL.md", "---\ndescription: How to release the shop\n---\nRun the release checklist.\n", under: source)
        try put("docs/payments.md", "# Payments\nCards are charged at dispatch.\n\n## Refunds\nA refund is allowed within 30 days of delivery.\n\n## Fraud\nOrders over 500 are held.\n", under: source)
        try put("run.sh", "echo not copied\n", under: source)
        try put("mcp.json", #"[{"Name": "shop-docs", "Endpoint": "http://127.0.0.1:8088/mcp", "Tools": ["search_docs"]}]"#, under: source)
        let data = home.appendingPathComponent("data")
        XCTAssertTrue(try Bundles.command("", home: data).contains("No bundles"))
        let installed = try Bundles.command("install \(source.path)", home: data)
        XCTAssertTrue(installed.contains("Installed shop-kit 1.2.0: 2 files") && installed.contains("names 1 MCP server (shop-docs)"), installed)
        XCTAssertTrue(Mcp.enabled(home: data).isEmpty, "installing a bundle puts no server into use")
        XCTAssertTrue(try Bundles.command("servers shop-kit", home: data).contains("shop-docs  ·  http://127.0.0.1:8088/mcp  ·  tools: search_docs"))
        XCTAssertTrue(try Bundles.command("servers shop-kit add", home: data).contains("Added, switched off: shop-docs"))
        XCTAssertEqual(try Mcp.all(home: data).map(\.name), ["shop-docs"])
        XCTAssertTrue(Mcp.enabled(home: data).isEmpty, "an added server waits to be switched on")
        XCTAssertTrue(try Bundles.command("servers shop-kit add", home: data).contains("already in your list"))
        XCTAssertEqual(Bundles.list(home: data).map(\.name), ["shop-kit"])
        XCTAssertFalse(FileManager.default.fileExists(atPath: data.appendingPathComponent("bundles/shop-kit/run.sh").path), "only skills and docs are copied")
        XCTAssertEqual(Skills.list(workspace, home: data).map(\.name), ["release"])
        XCTAssertTrue(Agent.extraTools(session: Session(workspace: folder.path), workspace: workspace, home: data).contains { $0.name == "read_documentation" })
        XCTAssertFalse(Agent.extraTools(session: Session(workspace: folder.path), workspace: workspace, home: home.appendingPathComponent("empty")).contains { $0.name == "read_documentation" }, "with nothing to read, the tool is not offered")

        let found = Bundles.read("when is a refund allowed", home: data, workspace: workspace)
        XCTAssertTrue(found.hasPrefix("[shop-kit 1.2.0/docs/payments.md:4]\n## Refunds\nA refund is allowed within 30 days"), found)
        try put(".mason/docs/notes.md", "# Local notes\nRefunds over 100 need a manager.\n", under: folder)
        XCTAssertTrue(Bundles.read("refund manager", home: data, workspace: workspace).hasPrefix("[.mason/docs/notes.md:1]"), "the project's own docs are read too")
        XCTAssertTrue(Bundles.read("kubernetes", home: data, workspace: workspace).contains("does not mention"))

        // A file that is really a link to somewhere else is not taken.
        try FileManager.default.createSymbolicLink(atPath: source.appendingPathComponent("docs/secrets.md").path, withDestinationPath: "/etc/hosts")
        XCTAssertThrowsError(try Bundles.install(from: source.path, home: data))
        XCTAssertThrowsError(try Bundles.install(from: folder.path, home: data), "a folder without bundle.json is not a bundle")
        _ = try Bundles.command("remove shop-kit", home: data)
        XCTAssertTrue(Bundles.list(home: data).isEmpty)
    }

    func testAModelCarriesOutATaskOnAPageOneStepAtATime() async throws {
        try XCTSkipIf(BrowserCheck.findBrowser() == nil, "no browser on this computer")
        try "<!doctype html><title>Shop</title><h1>Corner Shop</h1><p id=\"status\">Nothing in the cart.</p><input id=\"name\" placeholder=\"Name\"><button id=\"add\" onclick=\"document.getElementById('status').textContent = '1 tea in the cart for ' + document.getElementById('name').value + '.'\">Add tea</button>"
            .write(to: folder.appendingPathComponent("index.html"), atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: folder.path)
        let model = ScriptedModel([
            ModelReply(text: ##"{"action": "click", "selector": "#missing"}"##),
            ModelReply(text: ##"{"action": "fill", "selector": "#name", "value": "Ada"}"##),
            ModelReply(text: "I will press it now: {\"action\": \"click\", \"selector\": \"#add\"}"),
            ModelReply(text: #"{"action": "done", "answer": "The cart shows 1 tea for Ada."}"#),
        ])
        let outcome = try await Browse.run(workspace, page: "index.html", task: "Add a tea for Ada and say what the cart shows.", model: model)
        XCTAssertTrue(outcome.completed)
        XCTAssertEqual(outcome.answer, "The cart shows 1 tea for Ada.")
        XCTAssertEqual(outcome.steps.count, 2, "the step that did not work was taken back")
        let prompts = model.seen.map { $0.last?.content ?? "" }
        XCTAssertTrue(prompts[0].contains("Nothing in the cart.") && prompts[0].contains("#add"), "the model is shown the page and its controls")
        XCTAssertTrue(prompts[1].contains("did not work"), "a failed step is reported back")
        XCTAssertTrue(prompts[3].contains("1 tea in the cart for Ada."), "the model sees what its steps really led to")

        // An independent check reads the page afresh, with a picture, and an answer it does not support is not counted as done.
        let claims = ScriptedModel([ModelReply(text: #"{"action": "done", "answer": "The cart holds three teas."}"#)])
        let judge = ScriptedModel([ModelReply(text: #"{"supported": false, "reason": "The page says nothing is in the cart."}"#)])
        let checked = try await Browse.run(workspace, page: "index.html", task: "Say what is in the cart.", model: claims, reviewer: judge, picture: true)
        XCTAssertFalse(checked.completed)
        XCTAssertEqual(checked.supported, false)
        XCTAssertEqual(checked.review, "The page says nothing is in the cart.")
        XCTAssertTrue(judge.seen.first?.last?.content.contains("Nothing in the cart.") ?? false, "the checker is shown the page as it is")
        XCTAssertTrue(judge.seen.first?.last?.images?.first?.hasPrefix("data:image/png;base64,iVBOR") ?? false, "and a picture of it")

        var lines: [String] = []
        let code = await ScriptOperations.run(["browse", "--workspace", folder.path, "--prompt", "Is there a checkout?"], model: ScriptedModel([ModelReply(text: #"{"action": "fail", "answer": "There is no checkout on this page."}"#)])) { lines.append($0) }
        XCTAssertEqual(code, 2)
        XCTAssertTrue(lines.joined().contains("There is no checkout on this page."))
    }

    func testChatAttachesPicturesAndTheTextOfDocuments() async throws {
        let png = Data([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0])
        try png.write(to: folder.appendingPathComponent("shot.png"))
        let picture = try ChatAttachment(file: folder.appendingPathComponent("shot.png"))
        XCTAssertEqual(picture.image, "data:image/png;base64," + png.base64EncodedString())
        XCTAssertEqual(ChatAttachment.wordText(xml: #"<w:document><w:body><w:p><w:r><w:t>Terms &amp; conditions</w:t></w:r></w:p><w:p><w:r><w:t xml:space="preserve">Net </w:t><w:tab/><w:t>30 days</w:t></w:r></w:p></w:body></w:document>"#), "Terms & conditions\nNet \t30 days\n")
        try Data([0, 1, 2, 3]).write(to: folder.appendingPathComponent("blob.bin"))
        XCTAssertThrowsError(try ChatAttachment(file: folder.appendingPathComponent("blob.bin")), "a file that is neither text, a picture nor a document is refused")

        let chats = try ChatStore(home: home)
        var conversation = try chats.create()
        let model = ScriptedModel([ModelReply(text: "A screenshot.")])
        try await Chat.send(&conversation, text: "What is this?", attachments: [picture, ChatAttachment(name: "notes.txt", content: "hello")], model: model, store: chats) { _ in }
        let sent = try XCTUnwrap(conversation.messages.first { $0.role == "user" })
        XCTAssertEqual(sent.images?.count, 1)
        XCTAssertTrue(sent.content.contains("Attached picture: shot.png") && sent.content.contains("Attached file notes.txt"), sent.content)
        XCTAssertEqual(model.seen.first?.last?.images?.count, 1, "the picture goes to the model with the message")
        let again = try ChatStore(home: home)
        XCTAssertEqual(again.conversations.first?.messages.first { $0.role == "user" }?.images?.count, 1, "and is kept with the conversation")
    }

    func testATableInAnAnswerBecomesAGridAndAChart() {
        let answer = "Here are the sales.\n\n| Month | Units | Revenue | Note |\n|:--|--:|--:|---|\n| Jan | 1,200 | $3,400.50 | slow |\n| Feb | 950 | $2,100 | |\n| Mar | 1,410 | $4,020 | best |\n\nAnd in code:\n```\n| a | b |\n|---|---|\n| 1 | 2 |\n```\n"
        let tables = Tables.find(in: answer)
        XCTAssertEqual(tables.count, 1, "a table inside a code block is left alone")
        XCTAssertEqual(tables[0].headers, ["Month", "Units", "Revenue", "Note"])
        XCTAssertEqual(tables[0].labels, ["Jan", "Feb", "Mar"])
        XCTAssertEqual(tables[0].series.map(\.name), ["Units", "Revenue"], "only columns that are all numbers are charted")
        XCTAssertEqual(tables[0].series[0].values, [1200, 950, 1410])
        XCTAssertEqual(tables[0].series[1].values, [3400.5, 2100, 4020])
        XCTAssertEqual(Tables.number("-12.5%"), -12.5)
        XCTAssertNil(Tables.number("12 apples"))
        XCTAssertTrue(Tables.find(in: "| just | one row |\nno rule").isEmpty)
        let prose = Tables.prose(answer)
        XCTAssertTrue(prose.contains("Here are the sales.") && !prose.contains("| Jan |") && prose.contains("| a | b |"), "the table's own lines are left out of the prose, and code is kept: " + prose)
    }

    func testTheReviewerTakesPartInARequestAndADecisionCanBeComparedLater() async throws {
        _ = try Roles.command("reviewer http://127.0.0.1:9 judge", home: home)
        XCTAssertNotNil(Roles.load(home: home).reviewer)
        // The reviewer here is the agent's own scripted model: the chosen endpoint has nothing listening, so the role falls back to it only when the client cannot be made. The request shows the order of calls.
        let trace = home.appendingPathComponent("traces")
        let recorded = RoleModel(ScriptedModel([call("read_file", ["path": "calc.py"])]), role: "implementer", trace: trace)
        _ = try await recorded.complete(messages: [ChatMessage(role: "user", content: "Look at calc.py")], tools: Agent.tools) { _ in }
        let file = trace.appendingPathComponent(try XCTUnwrap(FileManager.default.contentsOfDirectory(atPath: trace.path).first))
        let same = try await Roles.compare(trace: file, model: ScriptedModel([call("read_file", ["path": "calc.py"])]))
        XCTAssertEqual(same["same"] as? Bool, true)
        let other = ScriptedModel([call("list_files", ["path": "."])])
        let different = try await Roles.compare(trace: file, model: other)
        XCTAssertEqual(different["same"] as? Bool, false)
        XCTAssertEqual(different["sameTools"] as? Bool, false)
        XCTAssertTrue(other.seenTools.first?.contains("read_file") ?? false, "the other model is offered the tools the first one had")
        var lines: [String] = []
        let code = await ScriptOperations.run(["compare-decision", "--trace", file.path], model: ScriptedModel([call("read_file", ["path": "calc.py"])])) { lines.append($0) }
        XCTAssertEqual(code, 0)
        XCTAssertTrue(lines.joined().contains("\"same\":true"), lines.joined())
    }

    func testAChosenReviewerReadsTheChangesBeforeARequestFinishes() async throws {
        var (store, session) = try session(.auto)
        let worker = ScriptedModel([
            call("write_file", ["path": "calc.py", "content": "def add(a, b):\n    return a * b\n"]),
            ModelReply(text: "Done."),
            call("write_file", ["path": "calc.py", "content": "def add(a, b):\n    return a + b\n"]),
            ModelReply(text: "Fixed after review."),
        ])
        let judge = ScriptedModel([ModelReply(text: #"[{"path": "calc.py", "line": 2, "evidence": "return a * b", "problem": "It multiplies instead of adding."}]"#)])
        let agent = Agent(store: store, model: worker, approve: { _ in true }, emit: { _ in })
        agent.derivesChecks = false
        agent.reviewer = judge
        await agent.run(&session, request: "Make add return the sum")
        XCTAssertEqual(session.state, "Completed")
        XCTAssertEqual(session.turns.last?.answer, "Fixed after review.")
        XCTAssertEqual(judge.seen.count, 1, "the reviewer reads the changes once, not after every repair")
        XCTAssertTrue(session.messages.contains { $0.role == "user" && $0.content.contains("A separate reviewer read your changes") && $0.content.contains("calc.py:2") && $0.content.contains("It multiplies instead of adding.") })
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("calc.py"), encoding: .utf8), "def add(a, b):\n    return a + b\n")

        // Outside Auto, and without a reviewer, a request finishes as before.
        var (store2, plain) = try self.session(.workspaceEdits)
        let quiet = Agent(store: store2, model: ScriptedModel([call("write_file", ["path": "notes.txt", "content": "x"]), ModelReply(text: "Done.")]), approve: { _ in true }, emit: { _ in })
        quiet.reviewer = ScriptedModel([])
        await quiet.run(&plain, request: "Write a note")
        XCTAssertEqual(plain.state, "Completed")
        XCTAssertEqual(plain.turns.last?.answer, "Done.")
    }

    func testProbesGoBeyondAPinnedTestAndAnAfterPlanHookRunsFirst() async throws {
        func put(_ name: String, _ text: String) throws {
            let file = folder.appendingPathComponent(name)
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try text.write(to: file, atomically: true, encoding: .utf8)
        }
        try put("mason.workflows.json", #"{"version": 1, "workflows": {"test": {"executable": "sh", "arguments": ["-c", "echo 'ok 1 - joins'"]}, "note": {"executable": "sh", "arguments": ["-c", "echo planned >> plan.log; exit 1"], "events": ["afterPlan"]}}}"#)
        try put("join.cjs", "exports.join = function (a, b) { return a + '-' + b; };\n")
        try put("join.test.cjs", "const { join } = require('./join.cjs');\nconsole.log(join('x', \"y\") === 'x-y' ? 'ok 1 - joins' : 'not ok 1 - joins');\n")
        try put(".mason/tests.json", #"[{"requirement": "Join two words", "workflow": "test", "format": "tap", "test": "joins", "testFiles": ["join.test.cjs"], "inputs": ["join.cjs"], "argumentOrder": "interchangeable", "roundTrip": {"module": "join.cjs", "encode": "join", "decode": "join", "alphabet": "ab"}}]"#)
        let workspace = try Workspace(path: folder.path)
        let test = try XCTUnwrap(try ProjectTests.load(workspace).first)
        XCTAssertEqual(ProjectTests.example(test, workspace: workspace), ["join.cjs", "join", "\"x\"", "\"y\""], "the example is taken from the test's own call")
        let plan = await Derived.prepare("Join two words", workspace: workspace, model: nil)
        XCTAssertEqual(ProjectTests.value(plan.checks[0], "argumentOrder"), ["join.cjs", "join", "\"x\"", "\"y\""])
        let result = try await Derived.verify(plan, workspace: workspace)[0]
        let node = (try? await Shell.run("command -v node", in: workspace, sandboxed: false))?.exitCode == 0
        if node {
            XCTAssertEqual(result.state, "failed", "the test passes, but join is not the same with its arguments exchanged: " + result.detail)
            XCTAssertTrue(result.detail.contains("argument order: join(\"x\", \"y\") gives \"x-y\" but exchanged it gives \"y-x\""), result.detail)
            XCTAssertTrue(result.detail.contains("round trip:"), result.detail)
        } else {
            XCTAssertTrue(result.detail.contains("not run"), result.detail)
        }

        // afterPlan runs once, before the model is asked, and its failure is told to the model.
        _ = try Policies.command("hooks on", workspace: workspace, home: home)
        var (store, session) = try session(.auto)
        let model = ScriptedModel([ModelReply(text: "Nothing to do.")])
        let agent = Agent(store: store, model: model, approve: { _ in true }, emit: { _ in })
        agent.hooksNeedSandbox = false
        agent.derivesChecks = false
        await agent.run(&session, request: "Look around")
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("plan.log"), encoding: .utf8), "planned\n")
        XCTAssertTrue(model.seen.first?.last?.content.contains("The project's afterPlan hook failed before work began") ?? false)
    }

    func testNetworkIsChosenPerSessionAndAHostCanBeAlwaysAllowed() async throws {
        var (store, session) = try session(.auto)
        XCTAssertFalse(NetworkSettings.asks(session, home: home))
        XCTAssertTrue(try NetworkSettings.sessionCommand("ask", session: &session, home: home).contains("this session's own choice"))
        XCTAssertTrue(NetworkSettings.asks(session, home: home) && !NetworkSettings.asks(home: home), "one session asking does not change the computer's setting")
        _ = try NetworkSettings.sessionCommand("computer ask", session: &session, home: home)
        _ = try NetworkSettings.sessionCommand("off", session: &session, home: home)
        XCTAssertFalse(NetworkSettings.asks(session, home: home), "a session can stay offline while the computer's setting asks")
        _ = try NetworkSettings.sessionCommand("default", session: &session, home: home)
        XCTAssertTrue(NetworkSettings.asks(session, home: home))
        try store.save(session)
        XCTAssertNil(try SessionStore(home: home).sessions.first { $0.id == session.id }?.network)

        var rules = ApprovalRules(home: home, workspace: session.workspace)
        try rules.add(tool: "network", prefix: "registry.npmjs.org")
        XCTAssertEqual(NetworkSettings.alwaysAllowed(home: home, workspace: session.workspace), ["registry.npmjs.org"])
        var asked: [String] = []
        let agent = Agent(store: store, model: ScriptedModel([]), approve: { asked.append($0.subject); return false }, emit: { _ in })
        let relay = try XCTUnwrap(agent.relay(session))
        defer { relay.stop() }
        func status(_ host: String) -> String {
            let descriptor = NetworkRelay.connected(to: ([127, 0, 0, 1], relay.port))!
            defer { close(descriptor) }
            NetworkRelay.send(descriptor, Array("CONNECT \(host):443 HTTP/1.1\r\n\r\n".utf8))
            var timeout = timeval(tv_sec: 20, tv_usec: 0)
            setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
            var buffer = [UInt8](repeating: 0, count: 256)
            let count = recv(descriptor, &buffer, buffer.count, 0)
            return String(decoding: buffer[0..<max(0, count)], as: UTF8.self)
        }
        XCTAssertTrue(status("registry.npmjs.org.evil.example").hasPrefix("HTTP/1.1 403"), "a longer name that starts the same is not the allowed host")
        XCTAssertEqual(asked, ["registry.npmjs.org.evil.example"], "it was asked about, and refused")
        _ = status("registry.npmjs.org")
        XCTAssertEqual(asked, ["registry.npmjs.org.evil.example"], "the always-allowed host was not asked about")
        XCTAssertTrue(SitePreview.loopbackOnly.contains("ignore-previous-rules") && (try? JSONSerialization.jsonObject(with: Data(SitePreview.loopbackOnly.utf8))) != nil)
    }

    func testAChatCarriesItsReasoningLevelOutputLimitAndPinnedReference() async throws {
        let chats = try ChatStore(home: home)
        var conversation = try chats.create()
        XCTAssertTrue(Chat.extraBody(conversation).isEmpty, "nothing is added unless the user asked for it")
        try Chat.configure(&conversation, reasoning: "low", maxTokens: 900, reference: "Refunds are allowed within 30 days.")
        let body = Chat.extraBody(conversation)
        XCTAssertEqual(body["max_tokens"] as? Int, 900)
        XCTAssertEqual(body["thinking_budget_tokens"] as? Int, 512)
        XCTAssertEqual((body["chat_template_kwargs"] as? [String: Bool])?["enable_thinking"], true)
        let model = ScriptedModel([ModelReply(text: "Within 30 days."), ModelReply(text: "Still 30 days.")])
        try await Chat.send(&conversation, text: "When can I get a refund?", model: model, store: chats) { _ in }
        XCTAssertTrue(model.seen[0].first?.content.contains("Refunds are allowed within 30 days.") ?? false, "the reference goes with the message")
        try Chat.configure(&conversation, reasoning: "off", maxTokens: 0, reference: "")
        XCTAssertEqual(Chat.extraBody(conversation)["reasoning_effort"] as? String, "none")
        XCTAssertNil(Chat.extraBody(conversation)["max_tokens"])
        try await Chat.send(&conversation, text: "And now?", model: model, store: chats) { _ in }
        XCTAssertFalse(model.seen[1].first?.content.contains("Refunds are allowed") ?? true, "a removed reference is no longer sent")
        XCTAssertThrowsError(try Chat.configure(&conversation, reasoning: "extreme", maxTokens: 0, reference: ""))
        XCTAssertThrowsError(try Chat.configure(&conversation, reasoning: "default", maxTokens: 3, reference: ""))
        XCTAssertEqual(try ChatStore(home: home).conversations.first?.reasoning, "off", "the choice is kept with the conversation")
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
        XCTAssertEqual(offered, ["read_lines", "git_status", "delegate_readonly", "repository_map", "ask_user", "suggest_note", "navigate_code", "read_skill"], "a read-only session gets no editing, job or workflow tools")
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

        // What a server writes is read message by message, and one still arriving is left for later.
        let two = LanguageServers.frame(["id": 1, "result": "a"]) + LanguageServers.frame(["method": "note"])
        let part = LanguageServers.frames(in: two.prefix(two.count - 5))
        XCTAssertEqual(part.messages.count, 1)
        XCTAssertEqual(part.used, LanguageServers.frame(["id": 1, "result": "a"]).count)
        XCTAssertEqual(LanguageServers.frames(in: two).messages.map { $0["method"] as? String ?? "" }, ["", "note"])
        XCTAssertEqual(LanguageServers.frames(in: two).used, two.count)

        let definition = try await LanguageServers.query(workspace, server: server, path: "sum.c", line: 4, column: 21, mode: "definitions", sandboxed: false)
        XCTAssertEqual(definition, "\(server.name): 1 definitions in the project.\nsum.c:1:5  int total(int a, int b) {")
        let references = try await LanguageServers.query(workspace, server: server, path: "sum.c", line: 4, column: 21, mode: "references", sandboxed: false)
        XCTAssertTrue(references.hasPrefix("\(server.name): 2 references in the project, and 1 outside it (libraries).\nsum.c:1:5"), references)
        XCTAssertTrue(references.contains("sum.c:4:21  int main() { return total(1, count); }"))
        let diagnostics = try await LanguageServers.query(workspace, server: server, path: "sum.c", line: 1, column: 1, mode: "diagnostics", sandboxed: false)
        XCTAssertEqual(diagnostics, "\(server.name): 2 diagnostics for sum.c.\nsum.c:3:12  error  use of undeclared identifier 'count'\nsum.c:1:1  warning  unused function")

        // Through the agent it asks first, like a command, and a read-only session may not start it.
        let store = try SessionStore(home: base.appendingPathComponent("home"))
        var session = try store.create(workspace: workspace.root.path, permission: .reviewEdits)
        XCTAssertTrue(Agent.extraTools(session: session, workspace: workspace).contains { $0.name == "inspect_symbol" })
        let ask = call("inspect_symbol", ["path": "sum.c", "line": "4", "column": "21", "mode": "definitions"])
        // The server stays running for the request, so a second question in it does not ask again. The next request does.
        var asked: [String] = [], answers = [true, false]
        let agent = Agent(store: store, model: ScriptedModel([ask, ask, ModelReply(text: "Found."), ask, ModelReply(text: "No.")]), approve: { asked.append($0.subject); return answers.removeFirst() }, emit: { _ in })
        await agent.run(&session, request: "Where is total defined?")
        XCTAssertEqual(asked, ["language server \(server.name)"])
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed])
        XCTAssertTrue(session.activities[0].result.contains("sum.c:1:5"))
        await agent.run(&session, request: "And again?")
        XCTAssertEqual(asked.count, 2)
        XCTAssertEqual(session.activities.last?.state, .denied)
        session.permission = .readOnly; session.askOnly = true
        await Agent(store: store, model: ScriptedModel([ask, ModelReply(text: "No.")]), approve: { _ in XCTFail("a read-only session must not ask"); return false }, emit: { _ in }).run(&session, request: "Again")
        XCTAssertEqual(session.activities.last?.state, .failed)
        XCTAssertTrue(session.activities.last?.result.contains("read-only") ?? false)
    }
}

final class ProjectNotesTests: XCTestCase {
    func testNotesAreKeptPerProjectAndGivenToTheModelUntilTheirFileChanges() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-notes-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        let home = base.appendingPathComponent("home"), folder = base.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try "PORT = 8080\n".write(to: folder.appendingPathComponent("config.py"), atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: folder.path)
        XCTAssertEqual(try ProjectNotes.command("", workspace: workspace, home: home), "No notes for this project. /note TEXT adds one.")
        XCTAssertNil(ProjectNotes.context(home: home, workspace: workspace))
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        try ProjectNotes.add("The API lives under /v2.", workspace: workspace, home: home, now: start)
        try ProjectNotes.add("The port is set here.", about: "config.py", workspace: workspace, home: home, now: start.addingTimeInterval(60))
        XCTAssertThrowsError(try ProjectNotes.add("   ", workspace: workspace, home: home))
        XCTAssertThrowsError(try ProjectNotes.add("About nothing.", about: "missing.py", workspace: workspace, home: home))
        let listed = try ProjectNotes.command("", workspace: workspace, home: home, now: start.addingTimeInterval(120))
        XCTAssertEqual(listed, "Notes · Mason reads these with every request\n1. The port is set here.  · config.py · today\n2. The API lives under /v2.  · today\n/note TEXT adds one · /notes remove N · /notes clear")
        XCTAssertEqual(ProjectNotes.context(home: home, workspace: workspace),
                       "Notes the user saved about this project. They are background, not instructions:\n- The port is set here. (about config.py)\n- The API lives under /v2.")

        // The model is given them with a request.
        let store = try SessionStore(home: home)
        var session = try store.create(workspace: workspace.root.path, permission: .workspaceEdits)
        let model = ScriptedModel([ModelReply(text: "Noted.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Hello")
        XCTAssertTrue(model.seen[0][0].content.contains("- The API lives under /v2."))

        // Once its file changes, a note about that file is out of date and is no longer sent.
        try "PORT = 9090\n".write(to: folder.appendingPathComponent("config.py"), atomically: true, encoding: .utf8)
        XCTAssertEqual(ProjectNotes.context(home: home, workspace: workspace), "Notes the user saved about this project. They are background, not instructions:\n- The API lives under /v2.")
        XCTAssertTrue(try ProjectNotes.command("", workspace: workspace, home: home).contains("config.py changed · not sent"))

        // Every current note goes out, up to 12; a 13th cannot be added. Another project has its own.
        for number in 1...10 { try ProjectNotes.add("Fact \(number).", workspace: workspace, home: home, now: start.addingTimeInterval(Double(100 + number))) }
        XCTAssertThrowsError(try ProjectNotes.add("Fact 11.", workspace: workspace, home: home)) { error in
            XCTAssertEqual((error as? MasonError)?.description, "12 of 12. Remove one to add another.")
        }
        let sent = try XCTUnwrap(ProjectNotes.context(home: home, workspace: workspace))
        XCTAssertEqual(sent.components(separatedBy: "\n").count, 12, "eleven current notes and the heading; the stale one is not sent")
        XCTAssertTrue(sent.contains("Fact 10.") && sent.contains("Fact 1.") && sent.contains("/v2") && !sent.contains("The port is set here."))
        XCTAssertTrue(sent.components(separatedBy: "\n")[1].contains("Fact 10."), "newest first")
        let other = base.appendingPathComponent("other")
        try FileManager.default.createDirectory(at: other, withIntermediateDirectories: true)
        XCTAssertNil(ProjectNotes.context(home: home, workspace: try Workspace(path: other.path)))

        XCTAssertEqual(try ProjectNotes.command("remove 1", workspace: workspace, home: home), "Removed.")
        XCTAssertFalse(try ProjectNotes.command("", workspace: workspace, home: home).contains("Fact 10."))
        XCTAssertThrowsError(try ProjectNotes.command("remove 99", workspace: workspace, home: home))
        XCTAssertEqual(try ProjectNotes.command("about config.py It moved.", workspace: workspace, home: home), "Kept, about config.py. It is sent until that file changes.")
        XCTAssertEqual(try ProjectNotes.command("clear", workspace: workspace, home: home), "This project has no notes now.")
        XCTAssertTrue(ProjectNotes.load(home: home, workspace: workspace.root.path).isEmpty)
        XCTAssertEqual(ProjectNotes.arguments("/note", "Use pnpm."), "add Use pnpm.")
        XCTAssertEqual(ProjectNotes.arguments("/note", ""), "")
        XCTAssertEqual(ProjectNotes.arguments("/notes", "remove 2"), "remove 2")
    }

    func notesProject() throws -> (base: URL, home: URL, workspace: Workspace) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-notes-" + UUID().uuidString)
        let folder = base.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try "PORT = 8080\n".write(to: folder.appendingPathComponent("config.py"), atomically: true, encoding: .utf8)
        return (base, base.appendingPathComponent("home"), try Workspace(path: folder.path))
    }

    func testANoteIsAtMost280CharactersButLongerSavedNotesStillLoadAndAreSent() throws {
        let (base, home, workspace) = try notesProject()
        defer { try? FileManager.default.removeItem(at: base) }
        XCTAssertThrowsError(try ProjectNotes.add(String(repeating: "a", count: 281), workspace: workspace, home: home))
        let longest = try ProjectNotes.add(String(repeating: "b", count: 280), workspace: workspace, home: home)
        XCTAssertThrowsError(try ProjectNotes.edit(longest.id, text: String(repeating: "c", count: 281), workspace: workspace, home: home))
        XCTAssertThrowsError(try ProjectNotes.edit(longest.id, text: "  ", workspace: workspace, home: home))

        // A note saved before the limits, and more notes than the limit, still load; the 12 newest current ones are sent.
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        let old = (1...14).map { ProjectNote(id: UUID(), text: $0 == 14 ? String(repeating: "x", count: 2000) : "Old \($0).", source: nil, sourceHash: nil, saved: start.addingTimeInterval(Double($0))) }
        try ProjectNotes.save(old, home: home, workspace: workspace.root.path)
        XCTAssertEqual(ProjectNotes.load(home: home, workspace: workspace.root.path).count, 14)
        let lines = try XCTUnwrap(ProjectNotes.context(home: home, workspace: workspace)).components(separatedBy: "\n")
        XCTAssertEqual(lines.count, 13)
        XCTAssertEqual(lines[1], "- " + String(repeating: "x", count: 2000))
        XCTAssertEqual(lines[12], "- Old 3.")
        XCTAssertThrowsError(try ProjectNotes.add("One more.", workspace: workspace, home: home))
    }

    func testEditRefreshClearAndRestore() throws {
        let (base, home, workspace) = try notesProject()
        defer { try? FileManager.default.removeItem(at: base) }
        // Whole seconds, as the notes file keeps them.
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        let plain = try ProjectNotes.add("Use pnpm.", workspace: workspace, home: home, now: start)
        let tied = try ProjectNotes.add("The port is set here.", about: "config.py", workspace: workspace, home: home, now: start.addingTimeInterval(60))

        // Editing keeps the id, the file and the date.
        let edited = try ProjectNotes.edit(plain.id, text: "  We use pnpm, not npm  ", workspace: workspace, home: home)
        XCTAssertEqual(edited.id, plain.id)
        XCTAssertEqual(edited.text, "We use pnpm, not npm")
        XCTAssertEqual(edited.saved, plain.saved)
        XCTAssertThrowsError(try ProjectNotes.edit(UUID(), text: "Gone.", workspace: workspace, home: home))

        // A note whose file changed is not sent until it is kept anyway, or edited with the fingerprint refreshed.
        try "PORT = 9090\n".write(to: workspace.root.appendingPathComponent("config.py"), atomically: true, encoding: .utf8)
        XCTAssertFalse(ProjectNotes.sentNotes(home: home, workspace: workspace).contains { $0.id == tied.id })
        let renamed = try ProjectNotes.edit(tied.id, text: "The port is 9090.", workspace: workspace, home: home)
        XCTAssertEqual(renamed.source, "config.py")
        XCTAssertFalse(ProjectNotes.current(renamed, in: workspace), "without a refresh, an edit leaves the note out of date")
        XCTAssertTrue(ProjectNotes.current(try ProjectNotes.refresh(tied.id, workspace: workspace, home: home), in: workspace))
        try "PORT = 7070\n".write(to: workspace.root.appendingPathComponent("config.py"), atomically: true, encoding: .utf8)
        XCTAssertTrue(ProjectNotes.current(try ProjectNotes.edit(tied.id, text: "The port is 7070.", refreshSource: true, workspace: workspace, home: home), in: workspace))
        XCTAssertEqual(ProjectNotes.sentNotes(home: home, workspace: workspace).count, 2)

        // Removing one and clearing all can both be undone.
        let removed = try XCTUnwrap(ProjectNotes.load(home: home, workspace: workspace.root.path).first { $0.id == plain.id })
        try ProjectNotes.remove(plain.id, workspace: workspace.root.path, home: home)
        try ProjectNotes.restore([removed], workspace: workspace.root.path, home: home)
        XCTAssertEqual(ProjectNotes.newest(home: home, workspace: workspace.root.path).map(\.text), ["The port is 7070.", "We use pnpm, not npm"])
        let cleared = try ProjectNotes.clear(workspace: workspace.root.path, home: home)
        XCTAssertEqual(cleared.count, 2)
        XCTAssertTrue(ProjectNotes.load(home: home, workspace: workspace.root.path).isEmpty)
        try ProjectNotes.restore(cleared, workspace: workspace.root.path, home: home)
        try ProjectNotes.restore(cleared, workspace: workspace.root.path, home: home)
        XCTAssertEqual(ProjectNotes.load(home: home, workspace: workspace.root.path), cleared, "restoring twice does not duplicate")
    }

    func testRelativeAge() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let saved = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertEqual(ProjectNotes.age(saved, now: saved.addingTimeInterval(3600), calendar: calendar), "today")
        XCTAssertEqual(ProjectNotes.age(saved, now: saved.addingTimeInterval(86_400), calendar: calendar), "yesterday")
        XCTAssertEqual(ProjectNotes.age(saved, now: saved.addingTimeInterval(2 * 86_400), calendar: calendar), "2 days ago")
    }

    func testDismissedSuggestionsAreKeptPerProjectUpTo50() throws {
        let (base, home, workspace) = try notesProject()
        defer { try? FileManager.default.removeItem(at: base) }
        let root = workspace.root.path
        XCTAssertTrue(ProjectNotes.dismissed(home: home, workspace: root).isEmpty)
        for number in 1...55 { try ProjectNotes.dismiss("Text \(number)", workspace: root, home: home) }
        try ProjectNotes.dismiss("  TEXT 55 ", workspace: root, home: home)
        let kept = ProjectNotes.dismissed(home: home, workspace: root)
        XCTAssertEqual(kept.count, 50)
        XCTAssertEqual(kept.first, "Text 6")
        XCTAssertEqual(kept.last, "TEXT 55", "a dismissal that matches one already kept replaces it")
        XCTAssertEqual(ProjectNotes.suggestionProblem("text 55", workspace: root, home: home), "The user dismissed this note before. Do not suggest it again.")
        XCTAssertNil(ProjectNotes.suggestionProblem("Text 1", workspace: root, home: home), "the oldest dismissals are forgotten")
        XCTAssertTrue(ProjectNotes.dismissed(home: home, workspace: base.path).isEmpty, "another project has its own")
    }

    func testTheModelCanSuggestOneNotePerRequest() async throws {
        let (base, home, workspace) = try notesProject()
        defer { try? FileManager.default.removeItem(at: base) }
        try ProjectNotes.add("We use pnpm, not npm", workspace: workspace, home: home)
        try ProjectNotes.dismiss("Tests live in tests/", workspace: workspace.root.path, home: home)
        let store = try SessionStore(home: home)
        var session = try store.create(workspace: workspace.root.path, permission: .workspaceEdits)

        let model = ScriptedModel([call("suggest_note", ["text": " Run the tests with make test. "]), call("suggest_note", ["text": "Another fact."]), ModelReply(text: "Done.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Fix the build")
        XCTAssertTrue(model.seen[0][0].content.contains("suggest_note"), "the system prompt mentions the tool")
        XCTAssertTrue(model.seenTools[0].contains("suggest_note"))
        XCTAssertEqual(session.suggestedNote, NoteSuggestion(text: "Run the tests with make test.", turn: session.turns[0].id))
        XCTAssertEqual(session.activities.map(\.state), [.completed, .failed])
        XCTAssertEqual(session.activities[0].result, "Shown to the user, who decides whether to keep it. Continue with the request.")
        XCTAssertEqual(session.activities[1].result, "Only one note can be suggested per request.")
        XCTAssertTrue(ProjectNotes.load(home: home, workspace: workspace.root.path).count == 1, "nothing is saved by the call")
        XCTAssertEqual(try SessionStore(home: home).sessions.first { $0.id == session.id }?.suggestedNote?.text, "Run the tests with make test.", "the suggestion is saved with the session")

        // Each request may suggest again, but not a note the user has, one they dismissed, or one of the wrong length.
        let next = ScriptedModel([call("suggest_note", ["text": "we use PNPM, not npm "]), call("suggest_note", ["text": "tests live in TESTS/"]),
                                  call("suggest_note", ["text": String(repeating: "z", count: 281)]), call("suggest_note", ["text": ""]),
                                  call("suggest_note", ["text": "Keep the public API stable"]), ModelReply(text: "Done.")])
        await Agent(store: store, model: next, approve: { _ in true }, emit: { _ in }).run(&session, request: "Tidy up")
        XCTAssertEqual(Array(session.activities.suffix(5).map(\.state)), [.failed, .failed, .failed, .failed, .completed])
        XCTAssertEqual(session.activities[2].result, "The user already has this note.")
        XCTAssertEqual(session.activities[3].result, "The user dismissed this note before. Do not suggest it again.")
        XCTAssertEqual(session.activities[4].result, "A suggested note is 1 to 280 characters.")
        XCTAssertEqual(session.suggestedNote, NoteSuggestion(text: "Keep the public API stable", turn: session.turns[1].id))
    }
}

final class PaletteTests: XCTestCase {
    func testThePaletteFindsWhatWasTypedBestMatchFirst() {
        let items = [
            PaletteItem(kind: .session, title: "Fix the cart total", detail: "Session in shop", value: "s1"),
            PaletteItem(kind: .project, title: "shop", detail: "Project at /home/me/shop", value: "/home/me/shop"),
            PaletteItem(kind: .file, title: "cart.py", detail: "src/cart.py", value: "src/cart.py"),
            PaletteItem(kind: .file, title: "test_cart.py", detail: "tests/test_cart.py", value: "tests/test_cart.py"),
            PaletteItem(kind: .command, title: "/notes", detail: "Notes kept for this project and given to the model", value: "/notes"),
            PaletteItem(kind: .chat, title: "Discarding a cart", detail: "Chat", value: "c1"),
        ]
        // Nothing typed: everything, in the order given, up to the limit.
        XCTAssertEqual(Palette.matches(items, query: "").map(\.value), items.map(\.value))
        XCTAssertEqual(Palette.matches(items, query: "  ", limit: 2).count, 2)
        // A title that starts with what was typed comes before one where it starts a word, then one that only contains it.
        XCTAssertEqual(Palette.matches(items, query: "CART").map(\.value), ["src/cart.py", "s1", "tests/test_cart.py", "c1"])
        // Every word must appear, in the title or the detail.
        XCTAssertEqual(Palette.matches(items, query: "cart tests").map(\.value), ["tests/test_cart.py"])
        XCTAssertEqual(Palette.matches(items, query: "model").map(\.value), ["/notes"])
        XCTAssertEqual(Palette.matches(items, query: "/no").map(\.value), ["/notes"])
        XCTAssertTrue(Palette.matches(items, query: "zebra").isEmpty)
        XCTAssertTrue(Palette.commands.contains { $0.name == "/languages" })
    }
}

final class GpuTests: XCTestCase {
    func testAGraphicsProcessorIsReadFromWhatItPublishes() {
        // Apple silicon: the processor shares the computer's memory and says how much of it it has in use.
        let shared: [String: Any] = ["model": "Apple M2 Pro", "PerformanceStatistics": ["Device Utilization %": 37, "In use system memory": 2_147_483_648] as [String: Any]]
        let apple = Gpu.adapter(from: shared, physicalMemory: 17_179_869_184)
        XCTAssertEqual(apple, GpuAdapter(name: "Apple M2 Pro", busyPercent: 37, totalBytes: 17_179_869_184, usedBytes: 2_147_483_648))
        XCTAssertEqual(apple.map(Gpu.describe), "37% busy · \(ByteText.size(2_147_483_648)) of \(ByteText.size(17_179_869_184)) in use")
        // A card with its own memory says how much is used and free.
        let own: [String: Any] = ["model": Data("Radeon Pro 5500M\0".utf8), "PerformanceStatistics": ["GPU Activity(%)": 140, "vramUsedBytes": 1_073_741_824, "vramFreeBytes": 3_221_225_472] as [String: Any]]
        XCTAssertEqual(Gpu.adapter(from: own, physicalMemory: 0), GpuAdapter(name: "Radeon Pro 5500M", busyPercent: 100, totalBytes: 4_294_967_296, usedBytes: 1_073_741_824))
        // One that says nothing about its use is still named, and one with no statistics is passed over.
        let quiet = Gpu.adapter(from: ["IOClass": "SomeAccelerator", "PerformanceStatistics": [:] as [String: Any]], physicalMemory: 8)
        XCTAssertEqual(quiet.map(Gpu.describe), "use not reported · shares the computer's memory")
        XCTAssertNil(Gpu.adapter(from: ["model": "x"], physicalMemory: 8))
    }
}

final class GenerationTests: XCTestCase {
    func testGenerationSettingsAreKeptPerConnectionAndSentWithItsRequests() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("mason-generation-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: home) }
        let coder = ModelEndpoint(url: "http://127.0.0.1:8080/", model: "coder"), other = ModelEndpoint(url: "http://127.0.0.1:8080/", model: "other")
        XCTAssertThrowsError(try Generation.command("", endpoint: nil, home: home))
        XCTAssertTrue(try Generation.command("", endpoint: coder, home: home).contains("temperature  ·  the server's default"))
        XCTAssertEqual(try Generation.command("temperature 0.2", endpoint: coder, home: home), "Temperature is 0.2 for coder.")
        XCTAssertEqual(try Generation.command("top-k 40", endpoint: coder, home: home), "Top K is 40 for coder.")
        XCTAssertEqual(try Generation.command("seed 7", endpoint: other, home: home), "Seed is 7 for other.")
        // Out of range, not a number, a fraction where a whole number is meant, and an unknown name are refused.
        for wrong in ["temperature 3", "top_p lots", "top_k 1.5", "warmth 1", "temperature"] {
            XCTAssertThrowsError(try Generation.command(wrong, endpoint: coder, home: home), wrong)
        }
        let shown = try Generation.command("", endpoint: coder, home: home)
        XCTAssertTrue(shown.contains("temperature  ·  0.2") && shown.contains("top_k  ·  40") && shown.contains("seed  ·  the server's default"), shown)
        // What goes in a request: only what was set, for this connection, with whole numbers as whole numbers.
        let body = Generation.body(for: coder, home: home)
        XCTAssertEqual(body.keys.sorted(), ["temperature", "top_k"])
        XCTAssertEqual(body["temperature"] as? Double, 0.2)
        XCTAssertEqual(body["top_k"] as? Int, 40)
        XCTAssertEqual(Generation.body(for: other, home: home)["seed"] as? Int, 7)
        // The client for a connection carries them.
        setenv("MASON_DATA_HOME", home.path, 1)
        defer { unsetenv("MASON_DATA_HOME") }
        XCTAssertEqual(try OpenAIClient(coder).extraBody["top_k"] as? Int, 40)
        XCTAssertEqual(try Generation.command("top_k off", endpoint: coder, home: home), "Top K is left to the server's default for coder.")
        XCTAssertNil(try OpenAIClient(coder).extraBody["top_k"])
        XCTAssertEqual(try Generation.command("clear", endpoint: coder, home: home), "Generation settings for coder are left to the server's defaults.")
        XCTAssertTrue(Generation.body(for: coder, home: home).isEmpty)
        XCTAssertEqual(Generation.values(for: other, home: home), ["seed": 7])
    }
}

final class ToolGroupTests: XCTestCase {
    func testSpecialisedToolsAreSentOnlyOnceTheirGroupIsLoaded() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-groups-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        let folder = base.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try "hello\n".write(to: folder.appendingPathComponent("notes.txt"), atomically: true, encoding: .utf8)
        let store = try SessionStore(home: base.appendingPathComponent("home"))
        var session = try store.create(workspace: try Workspace(path: folder.path).root.path, permission: .workspaceEdits)
        let model = ScriptedModel([call("enable_tools", ["group": "jobs"]), call("git_status", [:]), call("enable_tools", ["group": "klingon"]), ModelReply(text: "Done.")])
        await Agent(store: store, model: model, approve: { _ in true }, emit: { _ in }).run(&session, request: "Look around.")
        XCTAssertEqual(model.seenTools.count, 4)
        // At first the model is sent the everyday tools and enable_tools, and none of a group's tools.
        XCTAssertTrue(model.seenTools[0].contains("read_file") && model.seenTools[0].contains("edit_file") && model.seenTools[0].contains("enable_tools"))
        XCTAssertFalse(model.seenTools[0].contains("start_job") || model.seenTools[0].contains("git_status") || model.seenTools[0].contains("repository_map"))
        // A loaded group's tools are sent from the next step on, and the others still wait.
        XCTAssertTrue(model.seenTools[1].contains("start_job") && model.seenTools[1].contains("terminal_input") && model.seenTools[1].contains("enable_tools"))
        XCTAssertFalse(model.seenTools[1].contains("git_status"))
        // A group's tool called before its group was loaded loads the group rather than being turned away.
        XCTAssertTrue(model.seenTools[2].contains("git_status") && model.seenTools[2].contains("repository_map"))
        XCTAssertEqual(session.activities.map(\.state), [.completed, .completed, .failed])
        XCTAssertTrue(session.activities[0].result.hasPrefix("Loaded the jobs tools: start_job"), session.activities[0].result)
        XCTAssertTrue(session.activities[2].result.contains("code, docs, workflows, jobs, mcp"), session.activities[2].result)
        XCTAssertTrue(session.messages[0].content.contains("More tools load on request. Call enable_tools with one of: code for"))
        XCTAssertEqual(Agent.group(of: "start_job"), "jobs")
        XCTAssertNil(Agent.group(of: "read_file"))
    }
}

final class LanguageCheckTests: XCTestCase {
    /// A project, a data folder, and the stand-in server named for .c files.
    func project(_ arguments: String = "") throws -> (base: URL, workspace: Workspace, store: SessionStore) {
        guard let python = TransformersRuntime.python() else { throw XCTSkip("No python3 on this computer.") }
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-check-" + UUID().uuidString)
        let folder = base.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try "int total(int a, int b) {\n    return a + b;\n}\n".write(to: folder.appendingPathComponent("sum.c"), atomically: true, encoding: .utf8)
        let fake = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("fake-language-server.py").path
        setenv("MASON_LANGUAGE_SERVERS", "{\"c\": [\"\(python)\", \"\(fake)\"\(arguments)]}", 1)
        return (base, try Workspace(path: folder.path), try SessionStore(home: base.appendingPathComponent("home")))
    }

    func testTheErrorsAServerFindsInAnEditGoBackWithItsResult() async throws {
        let (base, workspace, store) = try project()
        defer { try? FileManager.default.removeItem(at: base); unsetenv("MASON_LANGUAGE_SERVERS") }
        var session = try store.create(workspace: workspace.root.path, permission: .workspaceEdits)
        let broken = call("edit_file", ["path": "sum.c", "old_text": "return a + b;", "new_text": "return a + count;"])
        let mended = call("edit_file", ["path": "sum.c", "old_text": "return a + count;", "new_text": "return a + b + 1;"])
        var asked: [String] = []
        let agent = Agent(store: store, model: ScriptedModel([broken, mended, ModelReply(text: "Done.")]), approve: { asked.append($0.title); return true }, emit: { _ in })
        await agent.run(&session, request: "Add one.")
        // Outside Auto the server is not sandboxed, so starting it is asked about once for the request.
        XCTAssertEqual(asked, ["Start python3 to check this request's edits"].map { $0.replacingOccurrences(of: "python3", with: LanguageServers.server(for: "c")!.name) })
        let first = session.activities[0].result, second = session.activities[1].result
        XCTAssertTrue(first.hasPrefix("Edited sum.c (+1 −1)."), first)
        // Errors go back; the warning the server also gave does not.
        XCTAssertTrue(first.contains("reports 1 error in sum.c after this edit"), first)
        XCTAssertTrue(first.contains("sum.c:3:12  error  use of undeclared identifier 'count'"), first)
        XCTAssertFalse(first.contains("unused function"))
        // The same server is told of the second edit, and a clean file adds nothing to the result.
        XCTAssertEqual(second, "Edited sum.c (+1 −1).")
    }

    func testADeclinedServerIsNotStartedAndNotAskedAboutAgain() async throws {
        let (base, workspace, store) = try project()
        defer { try? FileManager.default.removeItem(at: base); unsetenv("MASON_LANGUAGE_SERVERS") }
        var session = try store.create(workspace: workspace.root.path, permission: .workspaceEdits)
        let broken = call("edit_file", ["path": "sum.c", "old_text": "return a + b;", "new_text": "return a + count;"])
        let again = call("edit_file", ["path": "sum.c", "old_text": "return a + count;", "new_text": "return count;"])
        var asked = 0
        await Agent(store: store, model: ScriptedModel([broken, again, ModelReply(text: "Done.")]), approve: { _ in asked += 1; return false }, emit: { _ in }).run(&session, request: "Break it.")
        XCTAssertEqual(asked, 1)
        XCTAssertEqual(session.activities.map(\.result), ["Edited sum.c (+1 −1).", "Edited sum.c (+1 −1)."])
    }

    func testAServerThatOnlyAnswersWhenAskedIsAskedForTheFilesDiagnostics() async throws {
        let (base, workspace, _) = try project(", \"--pull\"")
        defer { try? FileManager.default.removeItem(at: base); unsetenv("MASON_LANGUAGE_SERVERS") }
        let server = try XCTUnwrap(LanguageServers.server(for: "c"))
        let sessions = LanguageSessions()
        let found = try await LanguageServers.errors(workspace, server: server, path: "sum.c", text: "int total() {\n\n    return count;\n}\n", sandboxed: false, sessions: sessions)
        XCTAssertTrue(found?.contains("sum.c:3:12  error  use of undeclared identifier 'count'") ?? false, found ?? "nothing")
        let clean = try await LanguageServers.errors(workspace, server: server, path: "sum.c", text: "int total() { return 1; }\n", sandboxed: false, sessions: sessions)
        XCTAssertNil(clean)
        await sessions.stopAll()
    }

    /// With real servers on this computer: the ones that come with Swift.
    func testARealLanguageServerReportsATypeErrorInAnEditedFile() async throws {
        unsetenv("MASON_LANGUAGE_SERVERS")
        guard let server = LanguageServers.server(for: "swift") else { throw XCTSkip("No sourcekit-lsp on this computer.") }
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-real-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        try FileManager.default.createDirectory(at: base.appendingPathComponent("Sources/demo"), withIntermediateDirectories: true)
        let manifest = "// swift-tools-version:5.9\nimport PackageDescription\nlet package = Package(name: \"demo\", targets: [.executableTarget(name: \"demo\")])\n"
        try manifest.write(to: base.appendingPathComponent("Package.swift"), atomically: true, encoding: .utf8)
        let wrong = "let total: Int = \"seven\"\nprint(total)\n"
        let right = "let total: Int = 7\nprint(total)\n"
        let file = base.appendingPathComponent("Sources/demo/main.swift")
        try wrong.write(to: file, atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: base.path)
        let sessions = LanguageSessions()
        let found = try await LanguageServers.errors(workspace, server: server, path: "Sources/demo/main.swift", text: wrong, sandboxed: false, sessions: sessions)
        XCTAssertTrue(found?.contains("Sources/demo/main.swift:1:") ?? false, found ?? "nothing")
        XCTAssertTrue(found?.contains("error") ?? false)
        try right.write(to: file, atomically: true, encoding: .utf8)
        let clean = try await LanguageServers.errors(workspace, server: server, path: "Sources/demo/main.swift", text: right, sandboxed: false, sessions: sessions)
        XCTAssertNil(clean, clean ?? "")
        await sessions.stopAll()
    }

    func testARealClangdFindsADefinitionAndAnErrorInOneRun() async throws {
        unsetenv("MASON_LANGUAGE_SERVERS")
        guard let server = LanguageServers.server(for: "c"), server.name == "clangd" else { throw XCTSkip("No clangd on this computer.") }
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-clangd-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let source = "int total(int a, int b) {\n    return a + b;\n}\nint main() { return total(1, count); }\n"
        try source.write(to: base.appendingPathComponent("sum.c"), atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: base.path)
        let sessions = LanguageSessions()
        let found = try await LanguageServers.errors(workspace, server: server, path: "sum.c", text: source, sandboxed: false, sessions: sessions)
        XCTAssertTrue(found?.contains("sum.c:4:30  error") ?? false, found ?? "nothing")
        // The same running server answers a question about a position.
        let definition = try await LanguageServers.query(workspace, server: server, path: "sum.c", line: 4, column: 21, mode: "definitions", sandboxed: false, sessions: sessions)
        XCTAssertTrue(definition.contains("sum.c:1:5  int total(int a, int b) {"), definition)
        await sessions.stopAll()
    }

    /// Downloads from the npm registry, so it runs only when asked for: MASON_TEST_INSTALL=1.
    func testInstallingAServerWithNpmMakesItCheckEdits() async throws {
        guard ProcessInfo.processInfo.environment["MASON_TEST_INSTALL"] == "1" else { throw XCTSkip("Set MASON_TEST_INSTALL=1 to install a real server from the npm registry.") }
        unsetenv("MASON_LANGUAGE_SERVERS")
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mason-install-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        let home = base.appendingPathComponent("home"), folder = base.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let wrong = "name: demo\nitems: [one, two\n"
        try wrong.write(to: folder.appendingPathComponent("config.yaml"), atomically: true, encoding: .utf8)
        let workspace = try Workspace(path: folder.path)
        let before = try await LanguageServers.command("", workspace: workspace, home: home)
        XCTAssertTrue(before.contains("YAML [yaml]  ·  not installed: /languages install yaml (uses npm)"), before)
        let said = try await LanguageServers.command("install yaml", workspace: workspace, home: home)
        XCTAssertEqual(said, "Installed yaml-language-server for YAML.")
        let server = try XCTUnwrap(LanguageServers.server(for: "yaml", home: home))
        XCTAssertTrue(server.command[0].hasPrefix(home.path), server.command[0])
        let sessions = LanguageSessions()
        let found = try await LanguageServers.errors(workspace, server: server, path: "config.yaml", text: wrong, sandboxed: false, sessions: sessions)
        XCTAssertTrue(found?.contains("config.yaml:") ?? false, found ?? "nothing")
        await sessions.stopAll()
    }

    func testAMissingServerIsSaidOnceAndTheLanguagesCommandSaysHowToGetOne() async throws {
        let (base, workspace, store) = try project()
        defer { try? FileManager.default.removeItem(at: base) }
        unsetenv("MASON_LANGUAGE_SERVERS")
        let home = base.appendingPathComponent("home")
        try "module Main where\n".write(to: workspace.root.appendingPathComponent("Main.hs"), atomically: true, encoding: .utf8)
        try "FROM scratch\n".write(to: workspace.root.appendingPathComponent("Dockerfile"), atomically: true, encoding: .utf8)
        XCTAssertEqual(LanguageServers.kind(of: "deploy/Dockerfile"), "dockerfile")
        XCTAssertEqual(LanguageServers.kind(of: "src/App.SCSS"), "scss")
        XCTAssertEqual(LanguageServers.language(for: "scss")?.key, "css")
        // Every extension belongs to one language, and every language can be named.
        var seen: Set<String> = []
        for language in LanguageServers.known {
            for kind in language.extensions { XCTAssertTrue(seen.insert(kind).inserted, kind) }
            XCTAssertFalse(language.candidates.isEmpty)
        }
        XCTAssertEqual(Set(LanguageServers.known.map(\.key)).count, LanguageServers.known.count)

        let listed = try await LanguageServers.command("", workspace: workspace, home: home)
        XCTAssertTrue(listed.contains("Haskell [haskell]"), listed)
        XCTAssertTrue(listed.contains("Dockerfiles [docker]"), listed)
        XCTAssertFalse(listed.contains("Zig"), listed)
        let all = try await LanguageServers.command("all", workspace: workspace, home: home)
        XCTAssertTrue(all.contains("Zig [zig]"))
        await XCTAssertThrowsErrorAsync(try await LanguageServers.command("install haskell", workspace: workspace, home: home), "ghcup")
        await XCTAssertThrowsErrorAsync(try await LanguageServers.command("install klingon", workspace: workspace, home: home), "/languages all")

        // A server the user names in their own file is used for that extension, known to Mason or not.
        if let python = TransformersRuntime.python() {
            let fake = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("fake-language-server.py").path
            try JSONSerialization.data(withJSONObject: ["klingon": [python, fake]]).write(to: home.appendingPathComponent("language-servers.json"))
            XCTAssertEqual(LanguageServers.server(for: "klingon", home: home)?.languageId, "klingon")
            let named = try await LanguageServers.command("", workspace: workspace, home: home)
            XCTAssertTrue(named.contains(".klingon files"), named)
        }

        guard LanguageServers.server(for: "hs") == nil else { return }
        var session = try store.create(workspace: workspace.root.path, permission: .workspaceEdits)
        let one = call("write_file", ["path": "Main.hs", "content": "module Main where\nmain = pure ()\n"])
        let two = call("write_file", ["path": "Other.hs", "content": "module Other where\n"])
        await Agent(store: store, model: ScriptedModel([one, two, ModelReply(text: "Done.")]), approve: { _ in XCTFail("nothing to ask"); return false }, emit: { _ in }).run(&session, request: "Write them.")
        XCTAssertTrue(session.activities[0].result.contains("No language server for Haskell is installed"), session.activities[0].result)
        XCTAssertFalse(session.activities[1].result.contains("No language server"))
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
