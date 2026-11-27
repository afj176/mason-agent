import XCTest
@testable import MasonCore

/// The Live Viewer's logic, mirroring the CodeScenes checks of the Windows app.
final class ActivityScenesTests: XCTestCase {
    func call(_ tool: String, _ subject: String, _ result: String = "", _ state: Activity.State = .completed) -> Activity {
        Activity(tool: tool, subject: subject, state: state, result: result)
    }

    let jestOutput = "exit 1\nPASS auth.test.ts\nFAIL tests/upload.test.ts\n  ✕ returns 429 after the 10th upload (12 ms)\nTests:       1 failed, 41 passed, 42 total\n"

    var mixed: [Activity] {
        [call("read_file", "src/a.ts"), call("search_files", "limit"), call("read_file", "tests\\a.test.ts"),
         call("edit_file", "./src/a.ts"), call("write_file", "src/b.ts"), call("run_shell", "npm test", jestOutput, .failed),
         call("check_page", "index.html"), call("suggest_note", "x")]
    }

    func testRepeatedReadsOfOneFileFormOneReadScene() {
        let loop = [call("read_file", "a.txt"), call("read_file", "a.txt"), call("read_file", "a.txt", RepeatStop.result, .skipped)]
        let scenes = ActivityScenes.group(loop)
        XCTAssertEqual(scenes, [ActivityScene(kind: .read, first: 0, count: 3)])
        XCTAssertEqual(ActivityScenes.label(scenes[0], loop), "Read 1 file")
        XCTAssertEqual(ActivityScenes.tick(scenes[0], loop).tone, .warn, "a skipped repeat tints the step as caution, not failure")
    }

    func testEachCallLandsInExactlyOneSceneInOrder() {
        let scenes = ActivityScenes.group(mixed)
        XCTAssertEqual(scenes.map(\.kind), [.read, .edit, .command, .page, .other])
        XCTAssertEqual(scenes.reduce(0) { $0 + $1.count }, mixed.count)
    }

    func testSceneLabelsCountDistinctFilesAndNameTheCommand() {
        let scenes = ActivityScenes.group(mixed)
        XCTAssertEqual(ActivityScenes.label(scenes[0], mixed), "Read 2 files, searched 1 time")
        XCTAssertEqual(ActivityScenes.label(scenes[1], mixed), "Edited 2 files")
        XCTAssertEqual(ActivityScenes.label(scenes[2], mixed), "Ran npm test")
        XCTAssertEqual(ActivityScenes.label(scenes[3], mixed), "Checked the page")
        XCTAssertEqual(ActivityScenes.label(scenes[4], mixed), "suggest note")
        XCTAssertEqual(ActivityScenes.label(ActivityScene(kind: .edit, first: 3, count: 1), mixed), "Edited a.ts")
    }

    func testPathsAreNormalisedForMatchingAgainstChanges() {
        XCTAssertEqual(ActivityScenes.path(of: mixed[3]), "src/a.ts")
        XCTAssertEqual(ActivityScenes.path(of: mixed[2]), "tests/a.test.ts")
        XCTAssertNil(ActivityScenes.path(of: mixed[1]), "a search's subject is its query, not a path")
        XCTAssertNil(ActivityScenes.path(of: mixed[5]))
    }

    func testFoldersCountTheReadsUnderEachTopFolder() {
        XCTAssertEqual(ActivityScenes.folders(Array(mixed.prefix(3))), [FolderCount("src/", 1), FolderCount("tests/", 1)])
        let more = [call("read_file", "src/a.ts"), call("read_file", "src/b.ts"), call("read_file", "README.md")]
        XCTAssertEqual(ActivityScenes.folders(more), [FolderCount("src/", 2), FolderCount("README.md", 1)])
    }

    func testATestRunIsReadForExitCodeCountsAndFailingLinesNotTheSummaryLine() {
        let summary = ActivityScenes.command(mixed[5])
        XCTAssertEqual(summary.exitCode, 1)
        XCTAssertEqual(summary.passed, 41)
        XCTAssertEqual(summary.failed, 1)
        XCTAssertEqual(summary.failures, ["FAIL tests/upload.test.ts", "✕ returns 429 after the 10th upload (12 ms)"])
        XCTAssertFalse(summary.output.hasPrefix("exit"), "Mason's own exit line is not part of the output shown")
        XCTAssertFalse(summary.truncated)
    }

    func testDotnetTestCountsAreReadToo() {
        let dotnet = ActivityScenes.command(call("run_shell", "dotnet test", "exit 1\n  Failed Mason.Tests.Upload [12 ms]\nFailed!  - Failed:     2, Passed:    41, Skipped:     0, Total:    43\n", .failed))
        XCTAssertEqual(dotnet.passed, 41)
        XCTAssertEqual(dotnet.failed, 2)
    }

    func testPythonUnittestCountsComeFromRanAndFailed() {
        let unittest = call("run_command", "/usr/bin/python3 -m unittest -v",
                            "exit 1\ntest_a ... ok\nFAIL: test_b (t.T.test_b)\n\nRan 6 tests in 0.002s\n\nFAILED (failures=1, errors=1)\n", .failed)
        let summary = ActivityScenes.command(unittest)
        XCTAssertEqual(summary.passed, 4)
        XCTAssertEqual(summary.failed, 2)
        XCTAssertTrue(summary.failures.contains("FAIL: test_b (t.T.test_b)"))
        XCTAssertFalse(summary.failures.contains { $0.hasPrefix("FAILED (") }, "the summary line is not a failing line")
        XCTAssertEqual(ActivityScenes.label(ActivityScene(kind: .command, first: 0, count: 1), [unittest]), "Ran python3 -m unittest -v",
                       "a command label names the program, not its folder")
        let passing = ActivityScenes.command(call("run_shell", "python3 -m unittest", "exit 0\n\nRan 3 tests in 0.001s\n\nOK\n"))
        XCTAssertEqual(passing.exitCode, 0)
        XCTAssertEqual(passing.passed, 3)
        XCTAssertNil(passing.failed)
    }

    func testJobResultsAndQuotedProgramsAreRead() {
        XCTAssertEqual(ActivityScenes.command(call("poll_job", "1", "job 1: exited 2\nboom")).exitCode, 2)
        XCTAssertNil(ActivityScenes.command(call("poll_job", "1", "job 1: running\n")).exitCode)
        XCTAssertEqual(ActivityScenes.commandLine("'/opt/homebrew/bin/node' --test"), "node --test")
        XCTAssertEqual(ActivityScenes.commandLine("npm test"), "npm test")
    }

    func testLongRunsSplitSoNoSceneGrowsWithoutBound() {
        let long = (0..<(ActivityScenes.maxSceneLength + 5)).map { call("read_file", "f\($0).txt") }
        XCTAssertEqual(ActivityScenes.group(long).map(\.count), [ActivityScenes.maxSceneLength, 5])
    }

    func testMacToolsMapToTheirKinds() {
        for tool in ["read_file", "read_lines", "list_files", "search_files", "search_next", "semantic_search", "repository_map", "inspect_symbol", "navigate_code", "git_status"] {
            XCTAssertEqual(ActivityScenes.kind(of: tool), .read, tool)
        }
        for tool in ["edit_file", "write_file", "apply_edits"] { XCTAssertEqual(ActivityScenes.kind(of: tool), .edit, tool) }
        for tool in ["run_shell", "run_command", "run_workflow", "start_job", "poll_job", "terminal_input", "verify_acceptance"] {
            XCTAssertEqual(ActivityScenes.kind(of: tool), .command, tool)
        }
        for tool in ["check_page", "open_preview", "desktop_observe", "desktop_act"] { XCTAssertEqual(ActivityScenes.kind(of: tool), .page, tool) }
        for tool in ["ask_user", "suggest_note", "enable_tools", "mcp_call"] { XCTAssertEqual(ActivityScenes.kind(of: tool), .other, tool) }
    }

    func testStepTicksAreTintedByKindAndOutcome() {
        let scenes = ActivityScenes.group(mixed)
        XCTAssertEqual(ActivityScenes.tick(scenes[0], mixed).text, "read×3")
        XCTAssertEqual(ActivityScenes.tick(scenes[1], mixed).tone, .accent)
        XCTAssertEqual(ActivityScenes.tick(scenes[2], mixed).tone, .bad, "a failed test run is tinted as a failure")
        XCTAssertEqual(ActivityScenes.tick(scenes[3], mixed).tone, .search)
        let declined = [call("edit_file", "a.txt", "The user declined this edit.", .denied)]
        XCTAssertEqual(ActivityScenes.tick(ActivityScenes.group(declined)[0], declined).tone, .bad)
        XCTAssertEqual(ActivityScenes.tone(RunEnding(code: "done")), .good)
        XCTAssertEqual(ActivityScenes.tone(RunEnding(code: "model_loop")), .warn)
        XCTAssertEqual(ActivityScenes.tone(RunEnding(code: "something new")), .neutral)
    }

    func testTheViewerFollowsTheRunUntilAnotherSceneIsPicked() {
        let calls = mixed
        let scenes = ActivityScenes.group(calls)
        // Running: follow the newest scene.
        XCTAssertEqual(ActivityScenes.live(scenes, running: true, hasPlan: true, hasEnd: false), scenes.last!.key)
        var shown = ActivityScenes.selection(pick: nil, activities: calls, scenes: scenes, running: true, hasPlan: false, hasEnd: false)
        XCTAssertEqual(shown.key, scenes.last!.key)
        XCTAssertTrue(shown.following)
        // Picking an earlier scene stops following, and stays on it as the run grows.
        let pick = ActivityScenes.pick(scenes[1].key, activities: calls, scenes: scenes, running: true, hasPlan: false, hasEnd: false)
        XCTAssertEqual(pick, calls[3].id.uuidString)
        let grown = calls + [call("read_file", "c.ts")]
        shown = ActivityScenes.selection(pick: pick, activities: grown, scenes: ActivityScenes.group(grown), running: true, hasPlan: false, hasEnd: false)
        XCTAssertEqual(shown.key, scenes[1].key)
        XCTAssertFalse(shown.following)
        // Picking the live one follows again.
        XCTAssertNil(ActivityScenes.pick(scenes.last!.key, activities: calls, scenes: scenes, running: true, hasPlan: false, hasEnd: false))
        // A call picked from a turn lands on the scene that holds it.
        shown = ActivityScenes.selection(pick: calls[1].id.uuidString, activities: calls, scenes: scenes, running: false, hasPlan: false, hasEnd: true)
        XCTAssertEqual(shown.key, scenes[0].key)
        // Ended: the receipt is the live scene; a pick that no longer exists follows again.
        XCTAssertEqual(ActivityScenes.live(scenes, running: false, hasPlan: false, hasEnd: true), ActivityScenes.endKey)
        shown = ActivityScenes.selection(pick: UUID().uuidString, activities: calls, scenes: scenes, running: false, hasPlan: false, hasEnd: true)
        XCTAssertEqual(shown.key, ActivityScenes.endKey)
        XCTAssertTrue(shown.following)
        XCTAssertEqual(ActivityScenes.selection(pick: ActivityScenes.planKey, activities: calls, scenes: scenes, running: false, hasPlan: true, hasEnd: true).key, ActivityScenes.planKey)
        XCTAssertEqual(ActivityScenes.live([], running: true, hasPlan: true, hasEnd: false), ActivityScenes.planKey)
        XCTAssertEqual(ActivityScenes.live([], running: true, hasPlan: false, hasEnd: false), "")
    }

    func testTheReceiptCoversTheLatestRequest() {
        var session = Session(workspace: "/tmp/project")
        session.activities = [call("read_file", "old.txt")]
        var first = Turn(request: "Look around", firstActivity: 0)
        first.changes = [FileChange(path: "old.txt", before: "a\n", after: "b\n")]
        var latest = Turn(request: "Fix the upload limit", firstActivity: 1)
        latest.changes = [FileChange(path: "src/a.ts", before: "one\ntwo\n", after: "one\n2\nthree\n"), FileChange(path: "src/b.ts", before: nil, after: "new\n")]
        session.turns = [first, latest]
        var steps = mixed
        steps[0].at = Date(timeIntervalSince1970: 1000)
        steps[steps.count - 1].at = Date(timeIntervalSince1970: 1125)
        session.activities += steps
        session.ending = RunEnding(code: "model_loop", kept: 2)
        session.repeatStop = RepeatStop(tool: "read_file", subject: "src/a.ts", model: "Tiny")
        let receipt = ActivityScenes.receipt(session)
        XCTAssertEqual(ActivityScenes.requestStart(session), 1)
        XCTAssertEqual(receipt.request, "Fix the upload limit")
        XCTAssertEqual(receipt.steps, steps.count)
        XCTAssertEqual(receipt.duration, "2 min 5 s")
        XCTAssertEqual(receipt.files.map(\.path), ["src/a.ts", "src/b.ts"])
        XCTAssertEqual(receipt.added, 3)
        XCTAssertEqual(receipt.removed, 1)
        XCTAssertTrue(receipt.files[1].created)
        XCTAssertEqual(receipt.commands, 1)
        XCTAssertEqual(receipt.commandsFailed, 1)
        XCTAssertEqual(receipt.notes.first, session.repeatStop!.explanation(kept: true))
        XCTAssertEqual(receipt.notes.last, RunEnding.hint("model_loop", terminal: false))
        // The Changes panel's merge: the original before, the latest after.
        var again = Turn(request: "Again", firstActivity: session.activities.count)
        again.changes = [FileChange(path: "src/a.ts", before: "one\n2\nthree\n", after: "final\n")]
        session.turns.append(again)
        let merged = ActivityScenes.sessionChanges(session)
        XCTAssertEqual(merged.map(\.path), ["old.txt", "src/a.ts", "src/b.ts"])
        XCTAssertEqual(ActivityScenes.change(for: "src/a.ts", in: merged)?.before, "one\ntwo\n")
        XCTAssertEqual(ActivityScenes.change(for: "src/a.ts", in: merged)?.after, "final\n")
        XCTAssertEqual(ActivityScenes.receipt(session).steps, 0, "a request with no steps yet has an empty receipt")
    }

    func testARunCommandActivityReadsAsTheLineItRuns() {
        let call = ToolCall(id: "1", name: "run_command", arguments: #"{"program":"python3","arguments":["-m","unittest","-v"],"directory":"."}"#)
        let arguments = (try? JSONSerialization.jsonObject(with: Data(call.arguments.utf8)) as? [String: Any]) ?? [:]
        XCTAssertEqual(Agent.subject(of: call, arguments: arguments), "python3 -m unittest -v")
    }
}
