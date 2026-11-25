import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

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

    /// The per-user temporary folder that macOS programs use even when TMPDIR points elsewhere (mktemp, compilers).
    public static var userTemporaryFolder: String? {
        #if os(macOS)
        var buffer = [CChar](repeating: 0, count: Int(PATH_MAX))
        guard confstr(_CS_DARWIN_USER_TEMP_DIR, &buffer, buffer.count) > 0 else { return nil }
        return realPath(String(cString: buffer))
        #else
        return nil
        #endif
    }

    public static func profile(workspace: String, scratch: String, temporary: String? = nil) -> String {
        let home = NSHomeDirectory()
        let temporaryRule = temporary.map { " (subpath \(quoted($0)))" } ?? ""
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
        (allow file-write* (subpath \(quoted(workspace))) (subpath \(quoted(scratch)))\(temporaryRule))
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

    /// The command runs in its own process group under a small wrapper. Removing the run file makes the wrapper stop
    /// the whole group with SIGKILL, so a stop also ends programs the command started, and it cannot be blocked by an
    /// inherited signal mask. The wrapper also stops the group if Mason itself goes away.
    /// Inside the sandbox the wrapper may not signal Mason, so only "no such process" counts as Mason being gone.
    /// The wrapper silences its own messages (job-control notices) so only the command's output is returned.
    static let wrapper = """
        set -m
        exec 3>&1 4>&2 2>/dev/null
        "$1" -c "$2" >&3 2>&4 3>&- 4>&- &
        child=$!
        ( while kill -0 $child 2>/dev/null; do gone=; case "$(LC_ALL=C kill -0 $PPID 2>&1)" in *"No such process"*) gone=1;; esac; if [ ! -e "$3" ] || [ -n "$gone" ]; then kill -9 -$child 2>/dev/null; break; fi; sleep 0.2; done ) >/dev/null 2>&1 &
        wait $child
        """

    public static func run(_ command: String, in workspace: Workspace, sandboxed: Bool, timeout: TimeInterval = 120) async throws -> CommandResult {
        guard !command.isEmpty, command.utf8.count <= 8000, !command.contains("\0") else { throw MasonError("Use a shell command of 1–8,000 characters.") }
        if sandboxed, !sandboxAvailable { throw MasonError("The macOS sandbox is unavailable, so Auto cannot run commands here. No unrestricted fallback was run.") }
        let scratch = FileManager.default.temporaryDirectory.appendingPathComponent("mason-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: scratch) }
        let runFile = scratch.appendingPathComponent("running")
        _ = FileManager.default.createFile(atPath: runFile.path, contents: nil)
        let bash = FileManager.default.isExecutableFile(atPath: "/bin/bash") ? "/bin/bash" : "/bin/sh"
        let wrapped = [bash, "-c", wrapper, "mason-command", path, command, runFile.path]
        let process = Process()
        if sandboxed {
            process.executableURL = URL(fileURLWithPath: SandboxProfile.tool)
            process.arguments = ["-p", SandboxProfile.profile(workspace: workspace.root.path, scratch: realPath(scratch.path), temporary: SandboxProfile.userTemporaryFolder)] + wrapped
            var environment = ProcessInfo.processInfo.environment
            environment["TMPDIR"] = scratch.path
            process.environment = environment
        } else {
            process.executableURL = URL(fileURLWithPath: bash)
            process.arguments = Array(wrapped.dropFirst())
        }
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

        let stop: @Sendable () -> Void = { try? FileManager.default.removeItem(at: runFile) }
        let deadline = Task {
            try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
            if !Task.isCancelled, process.isRunning { collected.markTimedOut(); stop() }
        }
        await withTaskCancellationHandler { await finished.wait() } onCancel: { stop() }
        deadline.cancel()
        stop()
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
