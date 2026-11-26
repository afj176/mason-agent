import Foundation

/// What the Website tab is built on: a server for the project's own pages, a development server started from a
/// project workflow, the script that lets the user pick an element, and the note a pick becomes.

/// Serves the project's files on 127.0.0.1 for as long as it is kept.
public final class PagePreview {
    private let server: BrowserCheck.Server
    public var port: Int { server.port }
    public init(workspace: Workspace) throws { server = try BrowserCheck.Server(workspace: workspace) }
    /// The address of a project page, given its path relative to the project.
    public func address(of page: String) -> String {
        "http://127.0.0.1:\(port)/" + page.split(separator: "/").map { $0.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? String($0) }.joined(separator: "/")
    }
    public func stop() { server.stop() }
    deinit { server.stop() }
}

public struct PreviewPick: Equatable {
    public var page: String
    public var selector: String
    public var kind: String
    public var label: String
    public var html: String
    public var console: [String]
}

public enum SitePreview {
    /// Added to every page the Website tab shows, before the page's own scripts. It keeps the latest console errors
    /// and warnings, and waits to be armed; once armed, the next click picks an element instead of using it, and the
    /// pick is posted to the app as JSON through the message handler named "mason".
    public static let script = #"""
        (function () {
          if (window.__masonPicker) return;
          var log = [];
          function note(kind, args) {
            try {
              log.push(kind + ': ' + Array.prototype.map.call(args, function (a) { return typeof a === 'string' ? a : JSON.stringify(a); }).join(' ').slice(0, 300));
              if (log.length > 20) log.shift();
            } catch (e) {}
          }
          ['error', 'warn'].forEach(function (kind) {
            var original = console[kind];
            console[kind] = function () { note(kind, arguments); return original.apply(console, arguments); };
          });
          window.addEventListener('error', function (event) {
            note('error', [event.message + ' (' + String(event.filename || '').split('/').pop() + ':' + event.lineno + ')']);
          });
          function selector(element) {
            var parts = [];
            while (element && element.nodeType === 1 && element !== document.documentElement) {
              if (element.id) { parts.unshift('#' + CSS.escape(element.id)); break; }
              var part = element.tagName.toLowerCase();
              var same = Array.prototype.filter.call(element.parentNode ? element.parentNode.children : [], function (c) { return c.tagName === element.tagName; });
              if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(element) + 1) + ')';
              parts.unshift(part);
              element = element.parentElement;
            }
            return parts.join(' > ');
          }
          var armed = false, box = null;
          // The element under the pointer is framed by a box laid over it, so the element itself is never changed.
          function mark(element) {
            if (!element) { if (box) box.style.display = 'none'; return; }
            if (!box) {
              box = document.createElement('div');
              box.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;border:2px solid #e8590c;border-radius:2px;box-sizing:border-box';
              (document.body || document.documentElement).appendChild(box);
            }
            var place = element.getBoundingClientRect();
            box.style.display = 'block';
            box.style.left = place.left + 'px'; box.style.top = place.top + 'px'; box.style.width = place.width + 'px'; box.style.height = place.height + 'px';
          }
          document.addEventListener('mouseover', function (event) { if (armed) mark(event.target); }, true);
          document.addEventListener('click', function (event) {
            if (!armed) return;
            event.preventDefault(); event.stopPropagation(); event.stopImmediatePropagation();
            var element = event.target;
            mark(null); armed = false;
            var label = String(element.getAttribute('aria-label') || element.innerText || element.value || element.getAttribute('alt') || element.getAttribute('placeholder') || '').trim().replace(/\s+/g, ' ').slice(0, 120);
            var picked = { selector: selector(element), kind: element.getAttribute('role') || element.tagName.toLowerCase(), label: label, html: element.outerHTML.slice(0, 600), console: log.slice() };
            try { window.webkit.messageHandlers.mason.postMessage(JSON.stringify(picked)); } catch (e) {}
          }, true);
          window.__masonPicker = { arm: function () { armed = true; }, disarm: function () { armed = false; mark(null); } };
        })();
        """#

    /// Run in the page when the user chooses to pick an element.
    public static let arm = "window.__masonPicker && window.__masonPicker.arm();"
    public static let disarm = "window.__masonPicker && window.__masonPicker.disarm();"

    /// The pick a page posted, or nil when the message is not one.
    public static func pick(from message: String, page: String) -> PreviewPick? {
        guard let object = try? JSONSerialization.jsonObject(with: Data(message.utf8)) as? [String: Any], let selector = object["selector"] as? String, !selector.isEmpty, selector.count <= 1000 else { return nil }
        func text(_ key: String, _ limit: Int) -> String { String((object[key] as? String ?? "").prefix(limit)) }
        return PreviewPick(page: page, selector: selector, kind: text("kind", 40), label: text("label", 120), html: text("html", 600), console: Array((object["console"] as? [String] ?? []).suffix(20)).map { String($0.prefix(300)) })
    }

    /// The pick as a note for the next request. Everything in it came from the page, so it is quoted as data.
    public static func evidence(_ pick: PreviewPick) -> String {
        var lines = ["In \(pick.page) I picked this element (what follows is copied from the page):",
                     "- selector: `\(pick.selector)`",
                     "- kind: \(pick.kind)" + (pick.label.isEmpty ? "" : ", label: “\(pick.label)”"),
                     "- HTML: `\(pick.html.replacingOccurrences(of: "\n", with: " "))`"]
        lines.append(pick.console.isEmpty ? "- console: no errors or warnings" : "- console, latest last:\n" + pick.console.map { "    " + $0 }.joined(separator: "\n"))
        return lines.joined(separator: "\n")
    }

    /// Clicks the picked element in a fresh headless browser at a phone width and a desktop width, and says what happened.
    public static func verifyClick(_ workspace: Workspace, page: String, selector: String) async throws -> String {
        var lines: [String] = []
        for width in [390, 1280] {
            let result = try await BrowserCheck.run(workspace, page: page, steps: [["action": "viewport", "width": width], ["action": "click", "selector": selector]])
            let step = result.steps.last
            lines.append("\(width) px: " + (result.passed ? "clicked, and the page raised no error" : step?.ok == false ? "could not click: \(step?.detail ?? "")" : "clicked, and the page raised: " + result.errors.prefix(3).joined(separator: "; ")))
        }
        return "Clicking `\(selector)` in \(page), in a fresh browser:\n" + lines.joined(separator: "\n")
    }

    /// A development server started from one of the project's workflows, for the Website tab to show. It runs with
    /// the user's account, as the workflow would from a terminal, and is stopped with `stop`.
    public final class DevServer {
        let jobs = Jobs()
        let id: String
        public let port: Int
        public var address: String { "http://127.0.0.1:\(port)/" }

        /// Starts the workflow and waits until something answers on the port.
        public init(workspace: Workspace, workflow name: String, port: Int) async throws {
            guard (1024...65535).contains(port) else { throw MasonError("Use a port from 1024 to 65535.") }
            guard let workflow = try ProjectWorkflows.load(workspace).workflows[name] else { throw MasonError("No workflow named \(name) in mason.workflows.json.") }
            guard !SitePreview.listening(port) else { throw MasonError("Something is already listening on port \(port). Stop it, or choose another port.") }
            self.port = port
            id = try jobs.start(ProjectWorkflows.commandLine(workflow), workspace: workspace, sandboxed: false)
            let deadline = Date().addingTimeInterval(45)
            while !SitePreview.listening(port) {
                let state = try await jobs.observe(id, stop: false, waitMs: 0)
                if !state.hasPrefix("job \(id): running") {
                    await jobs.stopAll()
                    throw MasonError("The workflow \(name) ended before anything listened on port \(port).\n" + String(state.suffix(1200)))
                }
                if Date() > deadline {
                    await jobs.stopAll()
                    throw MasonError("Nothing listened on port \(port) within 45 seconds of starting the workflow \(name).")
                }
                try await Task.sleep(nanoseconds: 200_000_000)
            }
        }

        public func output() async -> String { (try? await jobs.observe(id, stop: false, waitMs: 0)) ?? "" }
        public func stop() async { await jobs.stopAll() }
    }

    /// True when something accepts connections on 127.0.0.1 at `port`.
    public static func listening(_ port: Int) -> Bool {
        #if canImport(Darwin)
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        #else
        let descriptor = socket(AF_INET, Int32(SOCK_STREAM.rawValue), 0)
        #endif
        guard descriptor >= 0 else { return false }
        defer { close(descriptor) }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        return withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } } == 0
    }
}
