import Foundation

/// The pose art of the bear and the level-bot, one still per state. The app bundles each as Poses/<rawValue>.png,
/// the same files and the same choices as the Windows app (MainWindow.Brand.cs and ShowMood in MainWindow.Code.cs).
public enum Pose: String, CaseIterable, Sendable {
    case openProject = "open-project", listening, readingFiles = "reading-files", planning, editing
    case runningChecks = "running-checks", readyToReview = "ready-to-review", chooseModel = "choose-model", downloading
    case loadingModel = "loading-model", connected, awaitingApproval = "awaiting-approval", checkFailed = "check-failed"
    case restored, needsAttention = "needs-attention", firstLaunch = "first-launch"

    /// What a running session is doing, from the tool it is using (read, edit, check), or planning between calls.
    public static func forTool(_ tool: String?) -> Pose {
        guard let tool, !tool.isEmpty else { return .planning }
        let name = tool.lowercased()
        func has(_ words: String...) -> Bool { words.contains { name.contains($0) } }
        if has("edit", "replace", "create", "write", "delete", "rename", "move", "patch") { return .editing }
        if has("run_", "shell", "check", "review", "test", "browser", "build", "verify", "workflow", "preview") { return .runningChecks }
        if has("read", "open", "list", "search", "find", "grep", "symbol", "definition", "reference", "documentation", "skill") { return .readingFiles }
        return .planning
    }

    /// The pose beside a session's status, or nil when there is nothing to show (no requests yet, or a quiet state).
    /// `runningTool` is the tool of the latest step still running; `restoredRecently` is true within 30 seconds of an
    /// undo, redo or restore; `reviewableChanges` is true when the session left changes that were not put back.
    public static func forStatus(state: String, hasTurns: Bool, runningTool: String?, restoredRecently: Bool, reviewableChanges: Bool) -> Pose? {
        guard hasTurns else { return nil }
        if state == "Running" { return forTool(runningTool) }
        if restoredRecently { return .restored }
        switch state {
        case "Awaiting review": return .awaitingApproval
        case "Waiting for input": return .listening
        case "Completed": return reviewableChanges ? .readyToReview : .connected
        case "Failed", "Blocked", "Needs verification": return .checkFailed
        default: return nil
        }
    }

    /// What assistive technology reads for the pose.
    public var meaning: String {
        switch self {
        case .openProject: return "opening the project"
        case .listening: return "listening"
        case .readingFiles: return "reading files"
        case .planning: return "planning"
        case .editing: return "editing"
        case .runningChecks: return "running checks"
        case .readyToReview: return "ready to review"
        case .chooseModel: return "choosing a model"
        case .downloading: return "downloading"
        case .loadingModel: return "loading the model"
        case .connected: return "connected"
        case .awaitingApproval: return "waiting for your decision"
        case .checkFailed: return "a check failed"
        case .restored: return "files restored"
        case .needsAttention: return "needs your attention"
        case .firstLaunch: return "switching on the lamp for your first project"
        }
    }
}
