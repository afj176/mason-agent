import XCTest
@testable import MasonCore

/// The Moments feed's logic, mirroring the Windows viewer's feed, shelf and end card.
final class MomentsTests: XCTestCase {
    func call(_ tool: String, _ subject: String, _ result: String = "", _ state: Activity.State = .completed) -> Activity {
        Activity(tool: tool, subject: subject, state: state, result: result)
    }

    func testTimesAreRelative() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertEqual(Moments.ago(now.addingTimeInterval(-10), now: now), "just now")
        XCTAssertEqual(Moments.ago(now.addingTimeInterval(-50), now: now), "1 m")
        XCTAssertEqual(Moments.ago(now.addingTimeInterval(-5 * 60 - 20), now: now), "5 m")
        XCTAssertEqual(Moments.ago(now.addingTimeInterval(-2 * 3600 - 100), now: now), "2 h")
        let old = Moments.ago(now.addingTimeInterval(-3 * 86_400), now: now)
        XCTAssertFalse(old.hasSuffix(" h") || old.hasSuffix(" m") || old == "just now", "older than a day shows the date: \(old)")
    }

    func testTheFeedIsNewestFirstWithEachRequestBelowItsOwnMoments() {
        let calls = [call("read_file", "a"), call("edit_file", "a"), call("run_shell", "npm test", "exit 0\n"), call("read_file", "b")]
        let scenes = ActivityScenes.group(calls)
        var first = Turn(request: "  Fix a  ", firstActivity: 0)
        first.answer = "done"
        let second = Turn(request: "Now b", firstActivity: 3)
        let third = Turn(request: "And then?", firstActivity: 4)
        let requests = Moments.requests([first, second, third], activityCount: calls.count)
        XCTAssertEqual(requests.map(\.position), [0, 3, 4])
        XCTAssertEqual(requests.map(\.text), ["Fix a", "Now b", "And then?"])
        let feed = Moments.feed(scenes: scenes, requests: requests)
        XCTAssertEqual(feed, [
            .asked("And then?"),
            .scene(ActivityScene(kind: .read, first: 3, count: 1)),
            .asked("Now b"),
            .scene(ActivityScene(kind: .command, first: 2, count: 1)),
            .scene(ActivityScene(kind: .edit, first: 1, count: 1)),
            .scene(ActivityScene(kind: .read, first: 0, count: 1)),
            .asked("Fix a"),
        ])
    }

    func testALongRequestIsClipped() {
        let long = String(repeating: "x", count: 400)
        let text = Moments.requests([Turn(request: long)], activityCount: 0)[0].text
        XCTAssertEqual(text.count, 160)
        XCTAssertTrue(text.hasSuffix("…"))
    }

    func testAnEditSceneGivesOneCardPerFileNewestFirst() {
        let calls = [call("edit_file", "src/a.ts"), call("write_file", "./src/b.ts"), call("edit_file", "src/A.ts", "second")]
        let scene = ActivityScenes.group(calls)[0]
        XCTAssertEqual(Moments.editPaths(scene, calls), ["src/A.ts", "src/b.ts"])
        XCTAssertEqual(Moments.lastCall(scene, calls, path: "src/a.ts")?.result, "second")
    }

    func testQuietLinesStartWithACapitalAndNameWhereMasonLooked() {
        XCTAssertEqual(Moments.sentence("suggest note"), "Suggest note")
        XCTAssertEqual(Moments.sentence(""), "")
        let calls = [call("read_file", "src/a.ts"), call("read_file", "tests/a.ts"), call("read_file", "src/b.ts")]
        XCTAssertEqual(Moments.readPlaces(ActivityScenes.group(calls)[0], calls), "src/, tests/")
    }

    func testFileCardFacts() {
        XCTAssertEqual(Moments.lineCount(""), 0)
        XCTAssertEqual(Moments.lineCount("one\ntwo\n"), 2)
        XCTAssertEqual(Moments.lineCount("one\ntwo"), 2)
        XCTAssertEqual(Moments.head("\nimport a\n\nlet b = 1\nlet c = 2\nlet d = 3\n"), ["import a", "let b = 1", "let c = 2"])
        XCTAssertEqual(Moments.firstAdded(FileChange(path: "a", before: "one\ntwo\n", after: "one\n  2\ntwo\n")), "2")
        XCTAssertNil(Moments.firstAdded(FileChange(path: "a", before: "one\ntwo\n", after: "one\n")))
    }

    func testACommandFailsOnItsExitCodeOrFailingTests() {
        XCTAssertFalse(Moments.commandFailed(call("run_shell", "npm test", "exit 0\nTests: 4 passed\n")))
        XCTAssertTrue(Moments.commandFailed(call("run_shell", "npm test", "exit 1\n")))
        XCTAssertTrue(Moments.commandFailed(call("run_shell", "npm test", "exit 0\n2 failed, 3 passed\n")))
        XCTAssertTrue(Moments.commandFailed(call("run_shell", "npm test", "", .denied)))
    }

    func testTheEndCardCountsTheSessionsChangesAndTheLatestRequestsSteps() {
        var session = Session(workspace: "/tmp/project")
        session.activities = [call("read_file", "a"), call("edit_file", "src/a.ts"), call("run_shell", "npm test", "exit 0\n"), call("write_file", "src/b.ts")]
        var first = Turn(request: "Look", firstActivity: 0)
        first.changes = [FileChange(path: "old.txt", before: "a\n", after: "b\n")]
        var latest = Turn(request: "Fix", firstActivity: 1)
        latest.changes = [FileChange(path: "src/a.ts", before: "one\n", after: "one\ntwo\n"), FileChange(path: "src/b.ts", before: nil, after: "new\n")]
        session.turns = [first, latest]
        XCTAssertEqual(Moments.endFacts(session), ["3 files changed", "+3 −1", "1 command", "3 steps"])
        session.turns = [Turn(request: "Look", firstActivity: 3)]
        XCTAssertEqual(Moments.endFacts(session), ["0 files changed", "1 step"])
    }

    func testMadeSoFarListsTheLatestRunsFilesAndPagesNewestFirst() {
        let calls = [call("edit_file", "old.ts"), call("edit_file", "src/a.ts"), call("check_page", "http://localhost:3000/login"),
                     call("write_file", "src/b.ts"), call("edit_file", "src/a.ts"), call("check_page", "http://localhost:3000/"),
                     call("check_page", "http://localhost:3000/login"), call("edit_file", "gone.ts"), call("read_file", "src/a.ts")]
        let changes = [FileChange(path: "old.ts", before: "", after: "x"), FileChange(path: "src/a.ts", before: "a", after: "b"),
                       FileChange(path: "src/b.ts", before: nil, after: "new")]
        let made = Moments.madeSoFar(calls, from: 1, changes: changes)
        XCTAssertEqual(made.map(\.label), ["/login page", "localhost", "a.ts", "b.ts"])
        XCTAssertEqual(made.map(\.kind), [.page, .page, .file, .file])
        XCTAssertEqual(made[2].call, calls[4].id, "a file opens at its newest change")
        XCTAssertEqual(made.map(\.created), [false, false, false, true])
        XCTAssertEqual(Moments.madeSoFar(calls, from: 1, changes: changes, limit: 2).count, 2)
        XCTAssertEqual(Moments.pageLabel("site/index.html"), "index.html page")
    }

    func testAnOpenedMomentIsFoundByItsCallAndOptionalFile() {
        let calls = [call("read_file", "a"), call("edit_file", "src/a.ts"), call("edit_file", "src/b.ts"), call("run_shell", "ls")]
        let scenes = ActivityScenes.group(calls)
        let key = Moments.openKey(calls[2].id, path: "src/b.ts")
        let found = Moments.opened(key, activities: calls, scenes: scenes)
        XCTAssertEqual(found?.scene, ActivityScene(kind: .edit, first: 1, count: 2))
        XCTAssertEqual(found?.path, "src/b.ts")
        XCTAssertEqual(Moments.opened(Moments.openKey(calls[0].id), activities: calls, scenes: scenes)?.path, nil)
        XCTAssertEqual(Moments.opened(Moments.openKey(calls[0].id), activities: calls, scenes: scenes)?.scene.kind, .read)
        XCTAssertNil(Moments.opened(Moments.openKey(UUID()), activities: calls, scenes: scenes), "a moment that no longer exists closes")
        XCTAssertNil(Moments.opened(nil, activities: calls, scenes: scenes))
        XCTAssertNil(Moments.opened(ActivityScenes.endKey, activities: calls, scenes: scenes))
        XCTAssertEqual(Moments.newer(than: scenes[0], in: scenes), 2)
        XCTAssertEqual(Moments.newer(than: scenes[2], in: scenes), 0)
    }

    func testSessionChipsShowAnOpenPullRequestAndRunningTasks() {
        var draft = DeliveryDraft(id: "1", workspace: "/p", base: "main", baseCommit: "a", commit: "b", title: "t", body: "", repository: "o/n", branch: "x", paths: [], url: nil)
        XCTAssertNil(SessionChips.pullRequest(nil))
        XCTAssertNil(SessionChips.pullRequest(draft), "a draft that was never published has no pull request")
        draft.url = "  "
        XCTAssertNil(SessionChips.pullRequest(draft))
        draft.url = "https://github.com/o/n/pull/7"
        XCTAssertEqual(SessionChips.pullRequest(draft), "https://github.com/o/n/pull/7")
        func status(_ state: String) -> TaskStatus { TaskStatus(state: state, detail: "", pid: 0, started: Date(), session: nil, approval: nil) }
        let active = SessionChips.activeTasks([status("Running"), status("Completed"), status("Waiting for approval"), status("Failed"), status("Starting")])
        XCTAssertEqual(active, 3)
        XCTAssertEqual(SessionChips.backgroundLabel(active), "3 background")
        XCTAssertNil(SessionChips.backgroundLabel(0))
    }
}
