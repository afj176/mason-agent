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
}
