import Foundation

public struct CommandResult: Equatable {
    public let exitCode: Int32
    public let output: String
    public let truncated: Bool
    public let timedOut: Bool
}

/// The macOS sandbox profile for Auto: the command may read the system and the project, write only inside
/// the project and temporary folders, and has no network access.
public enum SandboxProfile {
    public static let tool = "/usr/bin/sandbox-exec"

    static func quoted(_ path: String) -> String {
        "\"" + path.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }

    public static func profile(workspace: String, scratch: String) -> String {
        let home = NSHomeDirectory()
        // Later rules win, so the denials for credentials follow the general read allowance.
        return """
        (version 1)
        (deny default)
        (allow process-fork)
        (allow process-exec)
        (allow signal (target same-sandbox))
        (allow sysctl-read)
        (allow mach-lookup)
        (allow ipc-posix-shm-read*)
        (allow file-read*)
        (deny file-read* (subpath \(quoted(home + "/.ssh"))) (subpath \(quoted(home + "/.aws"))) (subpath \(quoted(home + "/.gnupg"))) (subpath \(quoted(home + "/Library/Keychains"))))
        (allow file-write* (subpath \(quoted(workspace))) (subpath \(quoted(scratch))))
        (allow file-write* (literal "/dev/null") (literal "/dev/stdout") (literal "/dev/stderr") (literal "/dev/tty") (regex #"^/dev/ttys[0-9]+$"))
        (allow file-ioctl (literal "/dev/null") (literal "/dev/tty"))
        (deny network*)
        """
    }
}

public enum Shell {
    public static let outputLimit = 64 * 1024

    /// The user's shell for commands: zsh on macOS, bash elsewhere.
    public static var path: String {
        for candidate in ["/bin/zsh", "/bin/bash", "/usr/bin/bash", "/bin/sh"] where FileManager.default.isExecutableFile(atPath: candidate) {
            #if !os(macOS)
            if candidate == "/bin/zsh" { continue }
            #endif
            return candidate
        }
        return "/bin/sh"
    }

    public static var sandboxAvailable: Bool {
        #if os(macOS)
        return FileManager.default.isExecutableFile(atPath: SandboxProfile.tool)
        #else
        return false
        #endif
    }

    /// Runs one shell command in the project folder and returns its combined output.
    /// The command gets no terminal input. The shell is stopped at the deadline or when the task is cancelled; programs it left running in the background are not tracked yet.
    public static func run(_ command: String, in workspace: Workspace, sandboxed: Bool, timeout: TimeInterval = 120) async throws -> CommandResult {
        guard !command.isEmpty, command.utf8.count <= 8000, !command.contains("\0") else { throw MasonError("Use a shell command of 1–8,000 characters.") }
        let process = Process()
        var scratch: URL?
        if sandboxed {
            guard sandboxAvailable else { throw MasonError("The macOS sandbox is unavailable, so Auto cannot run commands here. No unrestricted fallback was run.") }
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mason-" + UUID().uuidString)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            scratch = folder
            process.executableURL = URL(fileURLWithPath: SandboxProfile.tool)
            process.arguments = ["-p", SandboxProfile.profile(workspace: workspace.root.path, scratch: folder.resolvingSymlinksInPath().path), path, "-c", command]
            var environment = ProcessInfo.processInfo.environment
            environment["TMPDIR"] = folder.path
            process.environment = environment
        } else {
            process.executableURL = URL(fileURLWithPath: path)
            process.arguments = ["-c", command]
        }
        defer { if let scratch { try? FileManager.default.removeItem(at: scratch) } }
        process.currentDirectoryURL = workspace.root
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        process.standardInput = FileHandle.nullDevice

        let collected = OutputBuffer(limit: outputLimit)
        pipe.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if !data.isEmpty { collected.append(data) }
        }
        let finished = AsyncSignal()
        process.terminationHandler = { _ in finished.fire() }
        do { try process.run() } catch { throw MasonError("The command could not start: \(error.localizedDescription)") }

        let deadline = Task {
            try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
            if !Task.isCancelled, process.isRunning { collected.markTimedOut(); process.terminate() }
        }
        await withTaskCancellationHandler { await finished.wait() } onCancel: { if process.isRunning { process.terminate() } }
        deadline.cancel()
        pipe.fileHandleForReading.readabilityHandler = nil
        if let rest = try? pipe.fileHandleForReading.readToEnd(), !rest.isEmpty { collected.append(rest) }
        try Task.checkCancellation()
        let (text, truncated, timedOut) = collected.snapshot()
        return CommandResult(exitCode: process.terminationStatus, output: text + (truncated ? "\n[Output clipped at 64 KiB.]" : "") + (timedOut ? "\n[Stopped: the command exceeded \(Int(timeout)) seconds.]" : ""),
                             truncated: truncated, timedOut: timedOut)
    }
}

final class OutputBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()
    private var truncated = false
    private var timedOut = false
    private let limit: Int
    init(limit: Int) { self.limit = limit }
    func append(_ more: Data) {
        lock.lock(); defer { lock.unlock() }
        let room = limit - data.count
        if more.count > room { truncated = true }
        if room > 0 { data.append(more.prefix(room)) }
    }
    func markTimedOut() { lock.lock(); timedOut = true; lock.unlock() }
    func snapshot() -> (String, Bool, Bool) {
        lock.lock(); defer { lock.unlock() }
        return (String(decoding: data, as: UTF8.self), truncated, timedOut)
    }
}

/// A one-shot signal that an async caller can wait for, safe to fire before or after the wait begins.
final class AsyncSignal: @unchecked Sendable {
    private let lock = NSLock()
    private var fired = false
    private var waiter: CheckedContinuation<Void, Never>?
    func fire() {
        lock.lock(); fired = true; let pending = waiter; waiter = nil; lock.unlock()
        pending?.resume()
    }
    func wait() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            lock.lock()
            if fired { lock.unlock(); continuation.resume() } else { waiter = continuation; lock.unlock() }
        }
    }
}
