import XCTest
@testable import MasonCore

final class PosesTests: XCTestCase {
    func testToolsMapToTheirPose() {
        XCTAssertEqual(Pose.forTool("edit_file"), .editing)
        XCTAssertEqual(Pose.forTool("apply_edits"), .editing)
        XCTAssertEqual(Pose.forTool("run_shell"), .runningChecks)
        XCTAssertEqual(Pose.forTool("run_workflow"), .runningChecks)
        XCTAssertEqual(Pose.forTool("read_file"), .readingFiles)
        XCTAssertEqual(Pose.forTool("search_files"), .readingFiles)
        XCTAssertEqual(Pose.forTool("inspect_symbol"), .readingFiles)
        XCTAssertEqual(Pose.forTool("ask_user"), .planning)
        XCTAssertEqual(Pose.forTool(nil), .planning)
    }

    func testStatusPoses() {
        func pose(_ state: String, turns: Bool = true, tool: String? = nil, restored: Bool = false, changes: Bool = false) -> Pose? {
            Pose.forStatus(state: state, hasTurns: turns, runningTool: tool, restoredRecently: restored, reviewableChanges: changes)
        }
        XCTAssertNil(pose("Completed", turns: false))
        XCTAssertEqual(pose("Running", tool: "write_file"), .editing)
        XCTAssertEqual(pose("Running"), .planning)
        XCTAssertEqual(pose("Completed", restored: true), .restored)
        XCTAssertEqual(pose("Awaiting review"), .awaitingApproval)
        XCTAssertEqual(pose("Waiting for input"), .listening)
        XCTAssertEqual(pose("Completed", changes: true), .readyToReview)
        XCTAssertEqual(pose("Completed"), .connected)
        XCTAssertEqual(pose("Failed"), .checkFailed)
        XCTAssertNil(pose("Stopped"))
    }

    func testEndingsDecideThePose() {
        func pose(_ state: String, _ ending: String?, changes: Bool = false) -> Pose? {
            Pose.forStatus(state: state, hasTurns: true, runningTool: nil, restoredRecently: false, reviewableChanges: changes, ending: ending)
        }
        XCTAssertEqual(pose("Completed", "done"), .connected)
        XCTAssertEqual(pose("Completed", "done", changes: true), .readyToReview)
        XCTAssertEqual(pose("Needs your answer", "question"), .listening)
        for failed in ["unverified", "model_unavailable", "failed"] { XCTAssertEqual(pose("Failed", failed), .checkFailed, failed) }
        // The model's loop, a limit, a project block or the user's stop is calm, never the failed pose.
        for calm in ["model_loop", "model_stuck", "step_limit", "time_limit", "blocked", "stopped"] { XCTAssertNil(pose("Stopped", calm), calm) }
        // An unknown code falls back to the state.
        XCTAssertEqual(pose("Failed", "from_the_future"), .checkFailed)
        XCTAssertEqual(pose("Running", "done"), .planning)
    }
}
