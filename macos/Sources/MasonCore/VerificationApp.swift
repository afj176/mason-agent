import Foundation

// An acceptance contract can name a Python WSGI application ("module:callable"). Mason copies the project to a
// temporary folder, runs the application there inside the sandbox with no network, and passes the browser's requests
// to it through files, so the application never opens a socket. The copy and its database are thrown away afterwards.

final class VerificationApp: BrowserCheck.Backend, @unchecked Sendable {
    /// The same bridge program the Windows app uses.
    static let bridge = #"""
"""Run a WSGI application in Mason's disposable LPAC workspace. The browser never opens a socket."""
import base64
import importlib
import io
import json
import os
from pathlib import Path
import sys
import time
import traceback
from urllib.parse import urlsplit, unquote
from wsgiref.util import setup_testing_defaults

mailbox = Path(__file__).resolve().parent
sys.path.insert(0, os.getcwd())
module, name = sys.argv[1].split(":")
application = getattr(importlib.import_module(module), name)

def publish(path, data):
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps(data), encoding="utf-8")
    os.replace(temp, path)

def dispatch(request):
    url = urlsplit(request["path"])
    body = base64.b64decode(request["body"])
    environ = {}
    setup_testing_defaults(environ)
    environ.update(REQUEST_METHOD=request["method"], PATH_INFO=unquote(url.path, encoding="latin1"),
                   QUERY_STRING=url.query, SERVER_NAME="workspace.mason.invalid", SERVER_PORT="443",
                   CONTENT_TYPE=request["contentType"], CONTENT_LENGTH=str(len(body)),
                   HTTP_COOKIE=request["cookie"], HTTP_HOST="workspace.mason.invalid")
    environ["wsgi.url_scheme"] = "https"
    environ["wsgi.input"] = io.BytesIO(body)
    response = {}
    output = io.BytesIO()
    def write(data):
        if output.tell() + len(data) > 2 * 1024 * 1024:
            raise ValueError("Response exceeds 2 MiB")
        output.write(data)
    def start(status, headers, exc_info=None):
        response.update(status=int(status.split()[0]), headers=dict((k.lower(), v) for k, v in headers))
        return write
    result = application(environ, start)
    try:
        for data in result:
            write(data)
    finally:
        if hasattr(result, "close"):
            result.close()
    response["body"] = base64.b64encode(output.getvalue()).decode("ascii")
    return response

publish(mailbox / "ready.json", {"ready": True})
while not (mailbox / "stop").exists():
    for path in mailbox.glob("*.request"):
        try:
            response = dispatch(json.loads(path.read_text(encoding="utf-8-sig")))
        except Exception:
            error = traceback.format_exc(limit=5)
            print(error, file=sys.stderr, flush=True)
            response = {"status": 500, "headers": {}, "body": "", "error": error[-1600:]}
        path.unlink()
        publish(path.with_suffix(".response"), response)
    time.sleep(.015)
"""#

    let copy: Workspace
    let folder: URL
    private let mailbox: URL
    private var task: Task<CommandResult?, Never>?
    private let lock = NSLock()
    private var responses: [(route: String, json: Data)] = []
    private var log: [String] = []

    private init(folder: URL) throws {
        self.folder = folder
        copy = try Workspace(path: folder.path)
        mailbox = folder.appendingPathComponent("mason-bridge-" + UUID().uuidString.prefix(12).lowercased())
        try FileManager.default.createDirectory(at: mailbox, withIntermediateDirectories: true)
    }

    static func python() -> String? { TransformersRuntime.python() }

    /// Copies the project and starts its application. `sandboxed` runs it inside the command sandbox.
    static func start(_ project: Workspace, entry: String, sandboxed: Bool) async throws -> VerificationApp {
        guard entry.range(of: #"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*:[A-Za-z_]\w*$"#, options: .regularExpression) != nil else { throw MasonError("Application must name a local Python WSGI module:callable.") }
        guard let python = python() else { throw MasonError("No python3 was found to run the application.") }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mason-verify-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let app: VerificationApp
        do { app = try VerificationApp(folder: folder) } catch { try? FileManager.default.removeItem(at: folder); throw error }
        do {
            let listing = try project.files(limit: 1000)
            guard !listing.truncated else { throw MasonError("The application has more than 1,000 files, which is more than a verification copy holds.") }
            var total = 0
            for path in listing.paths {
                try Task.checkCancellation()
                guard let data = try project.data(path) else { continue }
                total += data.count
                guard data.count <= 4 * 1024 * 1024, total <= 32 * 1024 * 1024 else { throw MasonError("The application is larger than a verification copy holds (4 MiB a file, 32 MiB in all).") }
                try app.copy.write(path, data: data)
            }
            try bridge.write(to: app.mailbox.appendingPathComponent("runner.py"), atomically: true, encoding: .utf8)
            let command = [python, "-B", app.mailbox.lastPathComponent + "/runner.py", entry].map(ProjectWorkflows.quoted).joined(separator: " ")
            let workspace = app.copy
            app.task = Task { [app] in
                var output = ""
                do {
                    let result = try await Shell.run(command, in: workspace, sandboxed: sandboxed, timeout: 600)
                    app.end(result)
                    return result
                } catch {
                    output = "\(error)"
                    app.end(CommandResult(exitCode: -1, output: output, truncated: false, timedOut: false))
                    return nil
                }
            }
            let deadline = Date().addingTimeInterval(60)
            while !FileManager.default.fileExists(atPath: app.mailbox.appendingPathComponent("ready.json").path) {
                try Task.checkCancellation()
                if Date() > deadline { throw MasonError("The application did not start within a minute.") }
                if let ended = app.finished() { throw MasonError("The application stopped as it started (exit \(ended.exitCode)): \(ended.output.suffix(1200))") }
                try await Task.sleep(nanoseconds: 30_000_000)
            }
            return app
        } catch {
            await app.stop()
            throw error
        }
    }

    /// The command's result once it has ended. The task records it, so asking never waits.
    private var ended: CommandResult?
    private func finished() -> CommandResult? { lock.lock(); defer { lock.unlock() }; return ended }
    private func end(_ result: CommandResult?) { lock.lock(); ended = result ?? CommandResult(exitCode: -1, output: "", truncated: false, timedOut: false); lock.unlock() }

    func stop() async {
        try? Data().write(to: mailbox.appendingPathComponent("stop"))
        try? await Task.sleep(nanoseconds: 200_000_000)
        task?.cancel()
        _ = await task?.value
        // Only ever a folder this made in the temporary directory.
        if folder.lastPathComponent.hasPrefix("mason-verify-") { try? FileManager.default.removeItem(at: folder) }
    }

    func answer(method: String, target: String, headers: [String: String], body: Data) -> BrowserCheck.BackendReply? {
        let path = target.split(separator: "?", maxSplits: 1).first.map(String.init) ?? "/"
        if path == "/__mason/harness" { return nil }
        func json(_ object: [String: Any], _ status: Int = 200) -> BrowserCheck.BackendReply {
            BrowserCheck.BackendReply(status: status, contentType: "application/json", body: (try? JSONSerialization.data(withJSONObject: object)) ?? Data())
        }
        if path == "/__mason/assert" {
            let items = URLComponents(string: "http://x" + target)?.queryItems ?? []
            func value(_ name: String) -> String { items.first { $0.name == name }?.value ?? "" }
            do { return json(["ok": true, "detail": try check(op: value("op"), target: value("target"), value: value("value"), captured: value("captured"))]) }
            catch { return json(["ok": false, "detail": "\(error)"]) }
        }
        guard ["GET", "POST", "HEAD", "PUT", "PATCH", "DELETE"].contains(method), body.count <= 128 * 1024 else { return json(["error": "Unsupported request."], 405) }
        let id = UUID().uuidString
        let request: [String: Any] = ["method": method, "path": target, "body": body.base64EncodedString(), "contentType": headers["content-type"] ?? "", "cookie": headers["cookie"] ?? ""]
        guard let payload = try? JSONSerialization.data(withJSONObject: request) else { return json(["error": "Bad request."], 400) }
        let writing = mailbox.appendingPathComponent(id + ".writing"), asked = mailbox.appendingPathComponent(id + ".request"), told = mailbox.appendingPathComponent(id + ".response")
        try? payload.write(to: writing)
        try? FileManager.default.moveItem(at: writing, to: asked)
        let deadline = Date().addingTimeInterval(8)
        while !FileManager.default.fileExists(atPath: told.path) {
            if Date() > deadline { return json(["error": "The application did not answer \(path) within eight seconds."], 504) }
            usleep(10_000)
        }
        guard let data = try? Data(contentsOf: told), data.count <= 3 * 1024 * 1024, let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let status = object["status"] as? Int else {
            return json(["error": "The application's answer could not be read."], 502)
        }
        try? FileManager.default.removeItem(at: told)
        let given = object["headers"] as? [String: String] ?? [:]
        var kind = given["content-type"] ?? "application/octet-stream"
        var out = Data(base64Encoded: object["body"] as? String ?? "") ?? Data()
        lock.lock()
        log.append("\(method) \(path) \(status)")
        if (200..<400).contains(status) { responses.append(("\(method) \(path)", kind.lowercased().contains("json") && out.count <= 65536 ? out : Data())) }
        lock.unlock()
        if kind.lowercased().hasPrefix("text/html"), let page = String(data: out, encoding: .utf8) { out = Data(BrowserCheck.withRecorder(page).utf8) }
        if kind.isEmpty { kind = "application/octet-stream" }
        var passed: [(String, String)] = []
        for name in ["set-cookie", "location"] { if let header = given[name] { passed.append((name, header)) } }
        return BrowserCheck.BackendReply(status: status, contentType: kind, headers: passed, body: out)
    }

    /// The steps that look at the application itself rather than the page.
    func check(op: String, target: String, value: String, captured: String) throws -> String {
        if op == "sqlite_count" {
            let parts = target.split(separator: "#", maxSplits: 1).map(String.init)
            guard parts.count == 2, parts[0].range(of: #"^(?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_-]+\.(?:sqlite3?|db)$"#, options: .regularExpression) != nil,
                  parts[1].range(of: #"^[A-Za-z_][A-Za-z0-9_]*$"#, options: .regularExpression) != nil, let expected = Int(value) else {
                throw MasonError("Use a database filename in the project, #table and the expected number of rows.")
            }
            let file = folder.appendingPathComponent(parts[0])
            guard FileManager.default.fileExists(atPath: file.path) else { throw MasonError("The application has not made the database \(parts[0]).") }
            // A separate Python reads the committed rows, apart from the application and its open connection.
            let query = "import pathlib, sqlite3, sys\nwith sqlite3.connect(pathlib.Path(sys.argv[1]).resolve().as_uri() + '?mode=ro', uri=True, timeout=2) as db:\n    db.execute('PRAGMA trusted_schema=OFF')\n    name = sys.argv[2]\n    if not any(row[0] == 'main' and row[1] == name and row[2] == 'table' for row in db.execute('PRAGMA table_list')):\n        raise ValueError('Expected a regular stored table')\n    print(db.execute('SELECT count(*) FROM \"' + name + '\"').fetchone()[0])\n"
            let process = Process(), output = Pipe()
            process.executableURL = URL(fileURLWithPath: VerificationApp.python() ?? "/usr/bin/python3")
            process.arguments = ["-I", "-B", "-c", query, file.path, parts[1]]
            process.standardOutput = output; process.standardError = FileHandle.nullDevice; process.standardInput = FileHandle.nullDevice
            try process.run()
            let text = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            process.waitUntilExit()
            guard process.terminationStatus == 0, let rows = Int(text) else { throw MasonError("The committed rows of \(target) could not be read.") }
            guard rows == expected else { throw MasonError("expected \(expected), observed \(rows) committed rows") }
            return "\(rows) rows"
        }
        lock.lock(); let matched = responses.filter { $0.route == target }.map(\.json); lock.unlock()
        if op == "response_count" {
            guard let expected = Int(value) else { throw MasonError("Give the expected number of responses.") }
            guard matched.count == expected else { throw MasonError("expected \(expected), observed \(matched.count) responses") }
            return "\(matched.count) responses"
        }
        guard op == "response_same" else { throw MasonError("unknown step") }
        guard !matched.isEmpty else { throw MasonError("no response was observed from \(target)") }
        for data in matched {
            guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let field = object[value] as? String, field == captured else {
                throw MasonError("field \(value) differed from the captured value \(captured)")
            }
        }
        return "\(matched.count) responses matched \(captured)"
    }
}
