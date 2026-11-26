import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Checks a page of the project in a real browser: loads it, optionally clicks, fills and looks for text, and
/// reports what the page shows and any script errors. It drives an installed Chromium-family browser headless,
/// with no profile and no network beyond this computer, against a small server that serves only the project.
public enum BrowserCheck {
    public struct Step: Equatable {
        public var action: String
        public var ok: Bool
        public var detail: String
    }
    public struct Result: Equatable {
        public var title = ""
        public var text = ""
        public var errors: [String] = []
        public var steps: [Step] = []
        /// What can be clicked or filled, each as "selector | kind | label".
        public var controls: [String] = []
        /// True when every step did what it was asked and the page raised no script error.
        public var passed: Bool { errors.isEmpty && steps.allSatisfy(\.ok) }
    }

    /// Where Chromium-family browsers are usually installed. `MASON_BROWSER` names one explicitly.
    static var candidates: [String] {
        var paths = [ProcessInfo.processInfo.environment["MASON_BROWSER"]].compactMap { $0 }.filter { !$0.isEmpty }
        #if os(macOS)
        for name in ["Google Chrome", "Chromium", "Microsoft Edge", "Brave Browser"] {
            paths.append("/Applications/\(name).app/Contents/MacOS/\(name)")
            paths.append(NSHomeDirectory() + "/Applications/\(name).app/Contents/MacOS/\(name)")
        }
        #else
        for folder in ["/usr/bin", "/usr/local/bin", "/snap/bin", "/opt/google/chrome"] {
            for name in ["google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "brave-browser", "chrome"] { paths.append(folder + "/" + name) }
        }
        #endif
        return paths
    }

    public static func findBrowser() -> String? { candidates.first { FileManager.default.isExecutableFile(atPath: $0) } }

    /// Added at the top of every HTML page the server sends, so errors are recorded from the first script on.
    static let recorder = """
        <script>window.__masonErrors=[];addEventListener('error',function(e){__masonErrors.push(String(e.message||e)+(e.filename?' at '+e.filename.split('/').pop()+':'+e.lineno:''))});\
        addEventListener('unhandledrejection',function(e){__masonErrors.push('Unhandled rejection: '+String(e.reason))});\
        (function(c){console.error=function(){__masonErrors.push(Array.prototype.map.call(arguments,String).join(' '));c.apply(console,arguments)}})(console.error);\
        window.alert=function(m){__masonErrors.push('The page opened a dialog: '+m)};window.confirm=function(){return true};window.prompt=function(){return ''};</script>
        """

    /// The page that loads the project page in a frame, carries out the steps and writes the result into itself.
    static let harness = """
        <!doctype html><meta charset="utf-8"><title>Mason check</title>
        <iframe id="f" style="width:1280px;height:800px;border:0"></iframe><pre id="mason-result"></pre>
        <script>
        (async function () {
          var q = new URLSearchParams(location.search), out = { steps: [], errors: [], title: '', text: '' }, f = document.getElementById('f');
          function done() { document.getElementById('mason-result').textContent = 'MASON:' + btoa(unescape(encodeURIComponent(JSON.stringify(out)))) + ':MASON'; }
          function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
          try {
            var steps = JSON.parse(decodeURIComponent(escape(atob(q.get('steps') || 'W10='))));
            var captured = null;
            function unlabel(v) { var a = v.indexOf(':'), b = v.indexOf(String.fromCharCode(10)), i = a < 0 ? b : b < 0 ? a : Math.min(a, b); return i > 0 && i <= 64 && /^[A-Za-z -]+$/.test(v.slice(0, i)) ? v.slice(i + 1).trim() : v; }
            function cents(t) { var n = Number(String(t).replace(/[$, ]/g, '')); return isNaN(n) ? null : Math.round(n * 100); }
            function amount(v) { var t = unlabel(v).replace(/[ ]/g, ''); return /^-?[$]?-?[0-9][0-9,]*([.][0-9]+)?$/.test(t) ? cents(t) : null; }
            function hasId(v, prefix) { if (v.length === 0 || v.length > 200) return false; var id = prefix.length > 0 && v.indexOf(prefix) === 0 ? v : unlabel(v); return id.indexOf(prefix) === 0 && id.length > prefix.length; }
            if (steps.length && steps[0].action === 'viewport') { f.style.width = Number(steps[0].width) + 'px'; steps.shift(); }
            await new Promise(function (r) { f.onload = r; f.src = q.get('page'); });
            await sleep(300);
            function doc() { return f.contentDocument; }
            function find(s) {
              if (s.selector) return doc().querySelector(s.selector);
              var want = String(s.text || '').trim().toLowerCase();
              var all = Array.prototype.slice.call(doc().querySelectorAll('button,a,input,select,textarea,label,summary,[role=button],[onclick],li,td,th,span,p,h1,h2,h3,h4,div'));
              function label(e) { return String(e.innerText || e.value || e.placeholder || e.getAttribute('aria-label') || '').trim().toLowerCase(); }
              return all.filter(function (e) { return label(e) === want; })[0] || all.filter(function (e) { return e.children.length === 0 && label(e).indexOf(want) >= 0; })[0] || null;
            }
            for (var i = 0; i < steps.length; i++) {
              var s = steps[i], r = { action: s.action, ok: false, detail: '' };
              try {
                var body = function () { return String(doc().body.innerText || ''); };
                if (s.action === 'click') { var e = find(s); if (!e) throw new Error('nothing on the page matches'); e.click(); await sleep(s.wait || 300); r.ok = true; }
                else if (s.action === 'fill') {
                  var t = find(s); if (!t) throw new Error('nothing on the page matches');
                  if (t.tagName === 'LABEL' && t.control) t = t.control;
                  t.focus(); t.value = String(s.value == null ? '' : s.value);
                  t.dispatchEvent(new Event('input', { bubbles: true })); t.dispatchEvent(new Event('change', { bubbles: true })); r.ok = true;
                }
                else if (s.action === 'expect_text') { r.ok = body().indexOf(String(s.text)) >= 0; if (!r.ok) r.detail = 'the page does not show it'; }
                else if (s.action === 'expect_no_text') { r.ok = body().indexOf(String(s.text)) < 0; if (!r.ok) r.detail = 'the page shows it'; }
                else if (s.action === 'expect_heading') { var want = String(s.text).trim(); r.ok = Array.prototype.slice.call(doc().querySelectorAll('h1,h2,h3,h4,h5,h6,[role=heading]')).some(function (e) { return e.getClientRects().length > 0 && String(e.innerText || '').trim() === want; }); if (!r.ok) r.detail = 'no visible heading reads exactly that'; }
                else if (s.action === 'wait') { await sleep(Math.min(Number(s.ms) || 500, 5000)); r.ok = true; }
                else if (s.action === 'accept') {
                  var op = s.op, target = String(s.target || ''), expected = String(s.value == null ? '' : s.value);
                  var nodes = function () { return target ? Array.prototype.slice.call(doc().querySelectorAll(target)).filter(function (e) { return e.getClientRects().length > 0; }) : []; };
                  r.action = op + (target ? ' ' + target : '');
                  if (op === 'sqlite_count' || op === 'response_count' || op === 'response_same') {
                    var told = await (await fetch('/__mason/assert?op=' + encodeURIComponent(op) + '&target=' + encodeURIComponent(target) + '&value=' + encodeURIComponent(expected) + '&captured=' + encodeURIComponent(captured || ''))).json();
                    if (!told.ok) throw new Error(told.detail);
                    r.ok = true; r.detail = told.detail;
                  } else
                  if (op === 'reload') {
                    out.errors = out.errors.concat((f.contentWindow.__masonErrors || []).slice(0, 20));
                    await new Promise(function (done) { f.onload = done; f.contentWindow.location.reload(); });
                    await sleep(300); r.ok = true;
                  } else if (op === 'click' || op === 'double_click' || op === 'click_if_enabled' || op === 'fill') {
                    var n = nodes()[0]; if (!n) throw new Error('nothing visible matches');
                    if (op === 'click') n.click();
                    else if (op === 'double_click') { n.click(); n.click(); n.dispatchEvent(new MouseEvent('dblclick', { bubbles: true })); }
                    else if (op === 'click_if_enabled') { if (!n.disabled) n.click(); }
                    else { n.focus(); n.value = expected; n.dispatchEvent(new Event('input', { bubbles: true })); n.dispatchEvent(new Event('change', { bubbles: true })); n.blur(); }
                    await sleep(150); r.ok = true;
                  } else {
                    var started = Date.now(), actual = '', count = 0;
                    while (true) {
                      var values = nodes().map(function (e) { return String(e.innerText || '').trim(); });
                      count = values.length; actual = count === 1 ? values[0] : values.slice(0, 8).join(' | ');
                      var pass = false;
                      if (op === 'count_distinct') { var seen = {}; values.forEach(function (v) { seen[v.toLowerCase()] = 1; }); pass = count === Number(expected) && values.every(function (v) { return v.length > 0; }) && Object.keys(seen).length === count; }
                      else if (op === 'empty') pass = count === 0 || (count === 1 && actual.length === 0);
                      else if (op === 'shelf_price') { var m = /[$][ ]*([0-9]+([.][0-9]{1,2})?)/.exec(actual); pass = count === 1 && !!m && cents(m[1]) === cents(expected); }
                      else if (op === 'money') pass = count === 1 && amount(actual) !== null && amount(actual) === cents(expected);
                      else if (op === 'text') pass = count === 1 && actual === expected;
                      else if (op === 'capture') pass = count === 1 && hasId(actual, expected);
                      else if (op === 'same') pass = count === 1 && actual === captured;
                      else if (op === 'new_id') pass = count === 1 && hasId(actual, expected) && unlabel(actual) !== unlabel(captured || '');
                      else throw new Error('unknown step');
                      if (pass) break;
                      if (Date.now() - started >= 2200) throw new Error("expected '" + (op === 'same' ? captured : expected) + "', observed '" + actual.slice(0, 160) + "' (" + count + ' visible)');
                      await sleep(75);
                    }
                    if (op === 'capture' || op === 'new_id') captured = actual;
                    r.ok = true; r.detail = actual.slice(0, 160);
                  }
                }
                else throw new Error('unknown action; use click, fill, expect_text, expect_no_text or wait');
              } catch (error) { r.detail = String(error && error.message || error); }
              out.steps.push(r);
              if (!r.ok) break;
            }
            out.title = doc().title; out.text = String(doc().body.innerText || '').slice(0, 6000);
            out.errors = out.errors.concat((f.contentWindow.__masonErrors || []).slice(0, 20));
            out.controls = Array.prototype.slice.call(doc().querySelectorAll('button,a[href],input:not([type=hidden]):not([type=password]),select,textarea,[role=button]')).filter(function (e) { return e.getClientRects().length > 0; }).slice(0, 64).map(function (e) {
              var kind = e.tagName.toLowerCase(), selector = e.id ? '#' + e.id : kind + (e.name ? '[name="' + e.name + '"]' : '');
              return selector + ' | ' + kind + ' | ' + String(e.getAttribute('aria-label') || e.innerText || e.placeholder || e.value || e.name || e.type || '').trim().replace(/\\s+/g, ' ').slice(0, 80);
            });
          } catch (error) { out.errors.push('The check could not run: ' + String(error && error.message || error)); }
          done();
        })();
        </script>
        """

    static func contentType(_ path: String) -> String {
        switch (path as NSString).pathExtension.lowercased() {
        case "html", "htm": return "text/html; charset=utf-8"
        case "js", "mjs": return "text/javascript; charset=utf-8"
        case "css": return "text/css; charset=utf-8"
        case "json", "map": return "application/json"
        case "svg": return "image/svg+xml"
        case "png": return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "gif": return "image/gif"
        case "webp": return "image/webp"
        case "ico": return "image/x-icon"
        case "woff2": return "font/woff2"
        case "woff": return "font/woff"
        case "txt", "md", "csv": return "text/plain; charset=utf-8"
        case "wasm": return "application/wasm"
        default: return "application/octet-stream"
        }
    }

    /// The reply to one request: status, content type and body. Only project files and the harness are served.
    static func respond(to target: String, workspace: Workspace) -> (Int, String, Data) {
        let path = target.split(separator: "?", maxSplits: 1).first.map(String.init) ?? "/"
        if path == "/__mason/harness" { return (200, "text/html; charset=utf-8", Data(harness.utf8)) }
        var relative = (path.removingPercentEncoding ?? path).drop { $0 == "/" }.description
        if relative.isEmpty || relative.hasSuffix("/") { relative += "index.html" }
        guard let data = try? workspace.data(relative) else { return (404, "text/plain; charset=utf-8", Data("Not in the project.".utf8)) }
        let kind = contentType(relative)
        if kind.hasPrefix("text/html"), var page = String(data: data, encoding: .utf8) {
            // After the doctype when there is one, so the page keeps its rendering mode.
            if let doctype = page.range(of: "<!doctype[^>]*>", options: [.regularExpression, .caseInsensitive]) { page.insert(contentsOf: recorder, at: doctype.upperBound) }
            else { page = recorder + page }
            return (200, kind, Data(page.utf8))
        }
        return (200, kind, data)
    }

    /// Something that answers requests in place of the project's files: an application under test.
    public struct BackendReply {
        public var status: Int
        public var contentType: String
        public var headers: [(String, String)] = []
        public var body: Data
    }
    public protocol Backend: AnyObject {
        /// The reply, or nil to let the page server answer from the project's files and the harness.
        func answer(method: String, target: String, headers: [String: String], body: Data) -> BackendReply?
    }

    /// A page with the error recorder added at the top, after the doctype when there is one.
    static func withRecorder(_ html: String) -> String {
        var page = html
        if let doctype = page.range(of: "<!doctype[^>]*>", options: [.regularExpression, .caseInsensitive]) { page.insert(contentsOf: recorder, at: doctype.upperBound) }
        else { page = recorder + page }
        return page
    }

    /// A server on 127.0.0.1 that answers GET requests for project files until it is stopped.
    final class Server: @unchecked Sendable {
        let port: Int
        private let listener: Int32
        private let workspace: Workspace
        /// When set, it answers first; nil from it falls back to the project's files and the harness.
        private let backend: Backend?
        private let stopped = NSLock()
        private var isStopped = false

        init(workspace: Workspace, backend: Backend? = nil) throws {
            self.workspace = workspace
            self.backend = backend
            #if canImport(Darwin)
            let descriptor = socket(AF_INET, SOCK_STREAM, 0)
            #else
            let descriptor = socket(AF_INET, Int32(SOCK_STREAM.rawValue), 0)
            #endif
            guard descriptor >= 0 else { throw MasonError("The page server could not start.") }
            var address = sockaddr_in()
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = 0
            address.sin_addr.s_addr = inet_addr("127.0.0.1")
            let bound = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
            var length = socklen_t(MemoryLayout<sockaddr_in>.size)
            let named = withUnsafeMutablePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(descriptor, $0, &length) } }
            guard bound == 0, named == 0, listen(descriptor, 16) == 0 else { close(descriptor); throw MasonError("The page server could not start.") }
            listener = descriptor
            port = Int(UInt16(bigEndian: address.sin_port))
            let thread = Thread { [self] in serve() }
            thread.start()
        }

        private func serve() {
            while true {
                let client = accept(listener, nil, nil)
                if client < 0 { return }
                stopped.lock(); let over = isStopped; stopped.unlock()
                if over { close(client); return }
                handle(client)
                close(client)
            }
        }

        private func handle(_ client: Int32) {
            var timeout = timeval(tv_sec: 5, tv_usec: 0)
            setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
            var request = Data(), buffer = [UInt8](repeating: 0, count: 4096)
            while request.count < 16384, request.range(of: Data("\r\n\r\n".utf8)) == nil {
                let count = recv(client, &buffer, buffer.count, 0)
                if count <= 0 { break }
                request.append(buffer, count: count)
            }
            let line = String(decoding: request, as: UTF8.self).split(separator: "\r\n", maxSplits: 1).first.map(String.init) ?? ""
            let parts = line.split(separator: " ")
            var (status, kind, body) = (405, "text/plain; charset=utf-8", Data("Only GET is served.".utf8))
            var extra = ""
            var answered = false
            if let backend, parts.count >= 2, let end = request.range(of: Data("\r\n\r\n".utf8)) {
                // An application gets the whole request: its headers and, for a POST, its body.
                var headers: [String: String] = [:]
                for field in String(decoding: request[..<end.lowerBound], as: UTF8.self).components(separatedBy: "\r\n").dropFirst() {
                    if let colon = field.firstIndex(of: ":") { headers[field[..<colon].lowercased()] = field[field.index(after: colon)...].trimmingCharacters(in: .whitespaces) }
                }
                var sent = Data(request[end.upperBound...])
                let length = min(Int(headers["content-length"] ?? "") ?? 0, 128 * 1024)
                while sent.count < length {
                    let count = recv(client, &buffer, buffer.count, 0)
                    if count <= 0 { break }
                    sent.append(buffer, count: count)
                }
                if let reply = backend.answer(method: String(parts[0]), target: String(parts[1]), headers: headers, body: sent) {
                    (status, kind, body) = (reply.status, reply.contentType, reply.body)
                    extra = reply.headers.map { "\($0.0): \($0.1)\r\n" }.joined()
                    answered = true
                }
            }
            if !answered, parts.count >= 2, parts[0] == "GET" || parts[0] == "HEAD" { (status, kind, body) = BrowserCheck.respond(to: String(parts[1]), workspace: workspace) }
            let head = "HTTP/1.1 \(status) \((200..<400).contains(status) ? "OK" : "Error")\r\nContent-Type: \(kind)\r\nContent-Length: \(body.count)\r\n\(extra)Cache-Control: no-store\r\nConnection: close\r\n\r\n"
            var reply = Data(head.utf8)
            if parts.first != "HEAD" { reply.append(body) }
            reply.withUnsafeBytes { bytes in
                var sent = 0
                while sent < bytes.count {
                    #if canImport(Darwin)
                    let count = send(client, bytes.baseAddress! + sent, bytes.count - sent, 0)
                    #else
                    let count = send(client, bytes.baseAddress! + sent, bytes.count - sent, Int32(MSG_NOSIGNAL))
                    #endif
                    if count <= 0 { break }
                    sent += count
                }
            }
        }

        func stop() {
            stopped.lock(); isStopped = true; stopped.unlock()
            shutdown(listener, Int32(SHUT_RDWR))
            close(listener)
        }
    }

    /// Loads `page` (a project-relative path to an HTML file) and carries out `steps`, each a dictionary such as
    /// {"action": "click", "text": "Add"} or {"action": "fill", "selector": "#name", "value": "Ada"}.
    /// `screenshot`, when given, is where a picture of the page is written after the steps.
    public static func run(_ workspace: Workspace, page: String, steps: [[String: Any]] = [], backend: Backend? = nil, screenshot: URL? = nil) async throws -> Result {
        guard let browser = findBrowser() else {
            throw MasonError("No Chrome, Chromium, Edge or Brave was found on this computer, so the page cannot be checked. Install one, or set MASON_BROWSER to its program.")
        }
        // An application serves its own pages, so the page need not be a file.
        guard backend != nil || (workspace.exists(page) && ["html", "htm"].contains((page as NSString).pathExtension.lowercased())) else { throw MasonError("\(page) is not an HTML file in the project.") }
        guard steps.count <= 40 else { throw MasonError("Use at most 40 steps.") }
        let encoded = try JSONSerialization.data(withJSONObject: steps).base64EncodedString()
        let server = try Server(workspace: workspace, backend: backend)
        defer { server.stop() }
        let profile = FileManager.default.temporaryDirectory.appendingPathComponent("mason-browser-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: profile) }
        var query = URLComponents()
        query.queryItems = [URLQueryItem(name: "page", value: "/" + page), URLQueryItem(name: "steps", value: encoded)]
        let address = "http://127.0.0.1:\(server.port)/__mason/harness?" + (query.percentEncodedQuery ?? "").replacingOccurrences(of: "+", with: "%2B")
        // No profile, no extensions, no first-run screens, and no name resolves except this computer.
        let arguments = ["--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync", "--mute-audio",
                         "--user-data-dir=" + profile.path, "--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 127.0.0.1", "--window-size=1320,900",
                         "--virtual-time-budget=20000"] + (screenshot.map { ["--screenshot=" + $0.path] } ?? []) + ["--dump-dom", address]
        let process = Process()
        process.executableURL = URL(fileURLWithPath: browser)
        process.arguments = arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice
        do { try process.run() } catch { throw MasonError("The browser could not start: \(error.localizedDescription)") }
        // Reading stops when the result has arrived, when the browser closes its output, or after 45 seconds. Waiting for
        // the output to close is not enough: a helper process of the browser can keep it open after the browser is gone.
        let collected = OutputBuffer(limit: 64 * 1024 * 1024)
        let done = AsyncSignal()
        output.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if data.isEmpty { done.fire(); return }
            collected.append(data)
            if collected.snapshot().0.contains(":MASON") { done.fire() }
        }
        let deadline = Task { try? await Task.sleep(nanoseconds: 45_000_000_000); done.fire() }
        await withTaskCancellationHandler { await done.wait() } onCancel: { done.fire() }
        deadline.cancel()
        output.fileHandleForReading.readabilityHandler = nil
        // The answer is in, or will not come. A browser that lingers is stopped rather than waited for.
        let grace = Date().addingTimeInterval(3)
        while process.isRunning, Date() < grace { try? await Task.sleep(nanoseconds: 50_000_000) }
        if process.isRunning {
            process.terminate()
            try? await Task.sleep(nanoseconds: 500_000_000)
            if process.isRunning { kill(process.processIdentifier, SIGKILL) }
        }
        // Not waitUntilExit: on macOS it can block without end here even after the browser has been killed, and the
        // result does not depend on how the browser ended.
        let gone = Date().addingTimeInterval(2)
        while process.isRunning, Date() < gone { try? await Task.sleep(nanoseconds: 50_000_000) }
        try Task.checkCancellation()
        let dom = collected.snapshot().0
        guard let start = dom.range(of: "MASON:"), let end = dom.range(of: ":MASON", range: start.upperBound..<dom.endIndex),
              let json = Data(base64Encoded: String(dom[start.upperBound..<end.lowerBound])), let object = try? JSONSerialization.jsonObject(with: json) as? [String: Any] else {
            throw MasonError("The browser gave no result for \(page). It may have been stopped, or it could not load the page.")
        }
        var result = Result()
        result.title = object["title"] as? String ?? ""
        result.text = object["text"] as? String ?? ""
        result.errors = object["errors"] as? [String] ?? []
        result.controls = object["controls"] as? [String] ?? []
        result.steps = (object["steps"] as? [[String: Any]] ?? []).map { Step(action: $0["action"] as? String ?? "", ok: $0["ok"] as? Bool ?? false, detail: $0["detail"] as? String ?? "") }
        return result
    }

    /// The result as the model reads it.
    public static func report(_ result: Result, page: String) -> String {
        var lines = ["\(result.passed ? "PASSED" : "FAILED"): \(page)" + (result.title.isEmpty ? "" : "  ·  title: \(result.title)")]
        for (index, step) in result.steps.enumerated() { lines.append("\(index + 1). \(step.action): \(step.ok ? "ok" : "FAILED" + (step.detail.isEmpty ? "" : " (" + step.detail + ")"))") }
        if !result.errors.isEmpty { lines.append("Script errors:"); lines += result.errors.map { "  " + $0 } }
        if !result.controls.isEmpty { lines.append("Controls (selector | kind | label):"); lines += result.controls.map { "  " + $0 } }
        lines.append("What the page shows:")
        lines.append(result.text.isEmpty ? "  (nothing)" : result.text)
        return lines.joined(separator: "\n")
    }
}
