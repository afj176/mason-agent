import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
#if os(macOS)
import AppKit
import ApplicationServices
import ScreenCaptureKit
#endif

// Operating a window of another app on this computer, through the accessibility interface the app itself offers:
// the names of its controls, pressing one, and setting the value of one. The user attaches one window to a session;
// Mason reads only that window, and asks before every action.

public struct DesktopWindow: Codable, Equatable {
    public var pid: Int32
    public var app: String
    public var title: String
    public init(pid: Int32, app: String, title: String) { self.pid = pid; self.app = app; self.title = title }
    public var label: String { title.isEmpty || title == app ? app : "\(app) — \(title)" }
}

public struct DesktopControl: Equatable {
    /// Names the control in the most recent observation of the window, such as "c12".
    public let id: String
    public let role: String
    public let name: String
    public let value: String
    public let canPress: Bool
    public let canSet: Bool
    public init(id: String, role: String, name: String, value: String = "", canPress: Bool = false, canSet: Bool = false) {
        self.id = id; self.role = role; self.name = name; self.value = value; self.canPress = canPress; self.canSet = canSet
    }
}

public protocol DesktopAutomation: AnyObject {
    /// Why windows cannot be operated right now (a missing permission, no accessibility service), or nil.
    func unavailable() -> String?
    /// The windows of other apps that are open now.
    func windows() throws -> [DesktopWindow]
    /// The controls of the window, in the order the app presents them. Ids are valid until the next observation.
    func observe(_ window: DesktopWindow) throws -> [DesktopControl]
    /// Presses a control ("invoke") or sets its value ("set"). The id comes from the latest observation.
    func act(_ window: DesktopWindow, control: String, operation: String, value: String) throws
    /// A picture of the window as it is now, or nil where this computer cannot take one.
    func frame(_ window: DesktopWindow) async throws -> DesktopFrame?
    /// Clicks at a point of the window's picture, or drags from it to a second point. Coordinates are the picture's pixels.
    func pointer(_ window: DesktopWindow, action: String, x: Int, y: Int, endX: Int, endY: Int) async throws
}

extension DesktopAutomation {
    public func frame(_ window: DesktopWindow) async throws -> DesktopFrame? { nil }
    public func pointer(_ window: DesktopWindow, action: String, x: Int, y: Int, endX: Int, endY: Int) async throws { throw MasonError("This computer cannot click in a window by position.") }
}

/// A picture of a window: a PNG and its size in pixels.
public struct DesktopFrame: Equatable {
    public let width: Int, height: Int
    public let png: Data
    public init(width: Int, height: Int, png: Data) { self.width = width; self.height = height; self.png = png }
    /// Names this exact picture; an action is only carried out while the window still looks the same.
    public var hash: String { sha256Hex(png) }
}

/// What a vision model made of a picture of the window: what it sees, and at most one click or drag it proposes.
public struct PixelDecision: Equatable {
    public var action: String
    public var x = 0, y = 0, endX = 0, endY = 0
    public var observation: String
    public init(action: String, x: Int = 0, y: Int = 0, endX: Int = 0, endY: Int = 0, observation: String) {
        self.action = action; self.x = x; self.y = y; self.endX = endX; self.endY = endY; self.observation = observation
    }
}

public protocol DesktopVision {
    func see(task: String, frame: DesktopFrame) async throws -> PixelDecision
}

public enum Vision {
    public static func prompt(_ task: String, frame: DesktopFrame) -> String {
        """
        This is a picture of an application window, \(frame.width) by \(frame.height) pixels, with (0, 0) at its top left corner.
        Task: \(task)
        Reply with only a JSON object: {"observation": what you see that matters for the task, "action": "none", "click" or "drag", "x": pixel, "y": pixel, "endX": pixel, "endY": pixel}.
        Use "none" when the task only asks what is shown, or when nothing in the picture does what is asked. Propose at most one click or drag; endX and endY are only for a drag.
        """
    }

    /// Reads the model's reply. A point outside the picture is refused rather than clamped.
    public static func parse(_ text: String, frame: DesktopFrame) throws -> PixelDecision {
        guard let start = text.firstIndex(of: "{"), let end = text.lastIndex(of: "}"), start < end, let object = try? JSONSerialization.jsonObject(with: Data(text[start...end].utf8)) as? [String: Any] else {
            throw MasonError("The vision model did not answer in the form asked for.")
        }
        func whole(_ key: String) -> Int { (object[key] as? Int) ?? (object[key] as? Double).map { Int($0.rounded()) } ?? 0 }
        let action = (object["action"] as? String ?? "none").lowercased()
        let decision = PixelDecision(action: action, x: whole("x"), y: whole("y"), endX: whole("endX"), endY: whole("endY"), observation: String((object["observation"] as? String ?? "").prefix(1200)))
        guard ["none", "click", "drag"].contains(action) else { throw MasonError("The vision model proposed something other than a click or a drag.") }
        func inside(_ x: Int, _ y: Int) -> Bool { (0..<frame.width).contains(x) && (0..<frame.height).contains(y) }
        if action != "none", !inside(decision.x, decision.y) || (action == "drag" && !inside(decision.endX, decision.endY)) { throw MasonError("The vision model pointed outside the window's picture.") }
        return decision
    }
}

/// The running model, asked about a picture. It must be a model that takes images (with llama.cpp, one started with its projector).
public struct ModelVision: DesktopVision {
    public let endpoint: ModelEndpoint
    public init(_ endpoint: ModelEndpoint) { self.endpoint = endpoint }

    public func see(task: String, frame: DesktopFrame) async throws -> PixelDecision {
        var request = URLRequest(url: try ModelEndpoint.validated(endpoint.url).appendingPathComponent("v1/chat/completions"))
        request.httpMethod = "POST"
        request.timeoutInterval = 300
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if !endpoint.key.isEmpty { request.setValue("Bearer " + endpoint.key, forHTTPHeaderField: "Authorization") }
        let content: [[String: Any]] = [["type": "text", "text": Vision.prompt(task, frame: frame)], ["type": "image_url", "image_url": ["url": "data:image/png;base64," + frame.png.base64EncodedString()]]]
        request.httpBody = try JSONSerialization.data(withJSONObject: ["model": endpoint.model, "stream": false, "temperature": 0, "max_tokens": 400, "messages": [["role": "user", "content": content]]] as [String: Any])
        let data: Data
        do { data = try await Net.data(request) } catch { throw MasonError("The model could not look at the picture. It needs a model that takes images: \(error)") }
        let answer = ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["choices"] as? [[String: Any]]
        guard let text = (answer?.first?["message"] as? [String: Any])?["content"] as? String else { throw MasonError("The model did not answer about the picture.") }
        return try Vision.parse(text, frame: frame)
    }
}

public enum Desktop {
    public static let controlLimit = 200

    /// The accessibility interface of this computer, or nil where this build has none.
    public static let system: DesktopAutomation? = {
        #if os(macOS)
        return AccessibilityDesktop()
        #else
        return nil
        #endif
    }()

    /// The observation as the model reads it.
    public static func report(_ window: DesktopWindow, controls: [DesktopControl]) -> String {
        var lines = ["Window: \(window.label)", "Controls (id · kind · name · what it accepts):"]
        for control in controls {
            var line = "  \(control.id) · \(control.role)" + (control.name.isEmpty ? "" : " · \(control.name)")
            if !control.value.isEmpty { line += " = \(control.value.prefix(120))" }
            let accepts = [control.canPress ? "invoke" : nil, control.canSet ? "set" : nil].compactMap { $0 }
            if !accepts.isEmpty { line += "  [" + accepts.joined(separator: ", ") + "]" }
            lines.append(line)
        }
        if controls.isEmpty { lines.append("  (the window exposes no controls)") }
        if controls.count >= controlLimit { lines.append("  (only the first \(controlLimit) are listed)") }
        return lines.joined(separator: "\n")
    }

    /// The typed form: what followed "/window". Lists windows, attaches one by number, or detaches.
    public static func command(_ rest: String, session: inout Session, desktop: DesktopAutomation? = system) throws -> String {
        let word = rest.trimmingCharacters(in: .whitespaces).lowercased()
        if word == "off" || word == "none" || word == "detach" {
            session.desktop = nil
            return "No window is attached to this session."
        }
        guard let desktop else { throw MasonError("This build cannot operate windows on this computer.") }
        if let problem = desktop.unavailable() { throw MasonError(problem) }
        let open = try desktop.windows()
        if word.isEmpty {
            var lines = open.enumerated().map { "\($0.offset + 1). \($0.element.label)" }
            if open.isEmpty { lines.append("No windows of other apps are open.") }
            lines.append(session.desktop.map { "Attached: \($0.label). /window off detaches it." } ?? "Attach one with /window NUMBER. Mason then reads that window's controls, and asks before pressing or changing anything.")
            return lines.joined(separator: "\n")
        }
        guard let number = Int(word), open.indices.contains(number - 1) else { throw MasonError("Give the number of a window. /window lists them.") }
        session.desktop = open[number - 1]
        return "Attached \(open[number - 1].label). Mason can read its controls, and asks before pressing or changing anything."
    }
}

extension Agent {
    static let desktopTools: [ToolSpec] = {
        func schema(_ properties: [String: Any], required: [String]) -> [String: Any] { ["type": "object", "properties": properties, "required": required, "additionalProperties": false] }
        let text: [String: Any] = ["type": "string"]
        return [
            ToolSpec(name: "desktop_observe", description: "Read the control names and available actions from the window the user attached to this session.", parameters: schema([:], required: [])),
            ToolSpec(name: "desktop_visual", description: "Look at a picture of the attached window with the vision model, for things its controls do not name: a canvas, a chart, a drawing. Describe what to look for, or one click or drag to make; a click or drag asks the user first and happens only if the window still looks the same.",
                     parameters: schema(["task": text], required: ["task"])),
            ToolSpec(name: "desktop_act", description: "Ask the user to invoke (press) or set a control from the most recent observation of the attached window. Returns a fresh observation. value is the text to set; use an empty string with invoke.",
                     parameters: schema(["operation": ["type": "string", "enum": ["invoke", "set"]], "id": text, "value": text], required: ["operation", "id", "value"])),
        ]
    }()

    /// The window tools a session is offered: none without an attached window, and no acting in a read-only session.
    static func desktopTools(for session: Session, available: Bool) -> [ToolSpec] {
        guard available, session.desktop != nil else { return [] }
        return session.askOnly || session.permission == .readOnly ? Array(desktopTools.prefix(2)) : desktopTools
    }

    func performDesktop(_ call: ToolCall, arguments: [String: Any], session: inout Session) async throws -> (Activity.State, String)? {
        guard call.name == "desktop_observe" || call.name == "desktop_act" || call.name == "desktop_visual" else { return nil }
        guard let window = session.desktop else { throw MasonError("No window is attached to this session.") }
        guard let desktop = desktop ?? Desktop.system else { throw MasonError("This build cannot operate windows on this computer.") }
        if let problem = desktop.unavailable() { throw MasonError(problem) }
        if call.name == "desktop_visual" {
            guard let task = (arguments["task"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines), !task.isEmpty, task.count <= 2000 else { throw MasonError("desktop_visual needs a task of up to 2,000 characters.") }
            guard let seer = vision ?? (model as? OpenAIClient).map({ ModelVision($0.endpoint) }) else { throw MasonError("No vision model is available to look at the window.") }
            guard let frame = try await desktop.frame(window) else { throw MasonError("This computer cannot take a picture of that window.") }
            emit(.status("Looking at \(window.app)"))
            let decision = try await seer.see(task: task, frame: frame)
            guard decision.action != "none" else { return (.completed, "Seen in \(window.label): \(decision.observation)") }
            if session.askOnly || session.permission == .readOnly { throw MasonError("This session is read-only. It can look at the window but not click in it. The model saw: \(decision.observation)") }
            let what = decision.action == "click" ? "Click at (\(decision.x), \(decision.y))" : "Drag from (\(decision.x), \(decision.y)) to (\(decision.endX), \(decision.endY))"
            let asked = ApprovalRequest(tool: call.name, title: "\(what) in \(window.label)", detail: "The vision model saw: \(decision.observation)\nThis acts on another app's window, outside the project and outside the sandbox.", subject: "window " + window.app)
            guard await approve(asked) else { return (.denied, "The user declined. Do not retry it; ask what they want instead.") }
            // The point was chosen on one picture; it is only used while the window still shows that picture.
            guard let again = try await desktop.frame(window), again.hash == frame.hash else { return (.failed, "The window changed before the \(decision.action) could be made, so nothing was done. Look again.") }
            try await desktop.pointer(window, action: decision.action, x: decision.x, y: decision.y, endX: decision.endX, endY: decision.endY)
            try? await Task.sleep(nanoseconds: 400_000_000)
            lastObservation = (try? desktop.observe(window)) ?? []
            return (.completed, "\(what) was made. Before it, the model saw: \(decision.observation)\n" + Desktop.report(window, controls: lastObservation))
        }
        if call.name == "desktop_act" {
            if session.askOnly || session.permission == .readOnly { throw MasonError("This session is read-only. It can observe the window but not act on it.") }
            guard let operation = arguments["operation"] as? String, operation == "invoke" || operation == "set", let id = arguments["id"] as? String else {
                throw MasonError("desktop_act needs an operation (invoke or set) and the id of a control from the latest observation.")
            }
            let value = arguments["value"] as? String ?? ""
            guard value.count <= 1200 else { throw MasonError("A value can be up to 1,200 characters.") }
            guard let control = lastObservation.first(where: { $0.id == id }) else { throw MasonError("Observe the window first, then use the id of a control from that observation.") }
            guard operation == "invoke" ? control.canPress : control.canSet else { throw MasonError("\(id) does not accept \(operation).") }
            // Acting on another app is outside the project and the sandbox, so it asks every time, in every mode.
            let what = operation == "invoke" ? "Press \(control.role) \(control.name.isEmpty ? id : "“\(control.name)”")" : "Set \(control.role) \(control.name.isEmpty ? id : "“\(control.name)”") to “\(value.prefix(200))”"
            let asked = ApprovalRequest(tool: call.name, title: "\(what) in \(window.label)", detail: "This acts on another app's window, outside the project and outside the sandbox.", subject: "window " + window.app)
            guard await approve(asked) else { return (.denied, "The user declined. Do not retry it; ask what they want instead.") }
            try desktop.act(window, control: id, operation: operation, value: value)
            try? await Task.sleep(nanoseconds: 400_000_000)
        }
        emit(.status("Reading \(window.app)"))
        lastObservation = try desktop.observe(window)
        return (.completed, Desktop.report(window, controls: lastObservation))
    }
}

#if os(macOS)
/// macOS: the Accessibility interface (AXUIElement). The user grants it in System Settings → Privacy & Security → Accessibility.
final class AccessibilityDesktop: DesktopAutomation {
    private var elements: [String: AXUIElement] = [:]

    func unavailable() -> String? {
        AXIsProcessTrusted() ? nil : "Mason needs the Accessibility permission to read another app's window. Allow it in System Settings → Privacy & Security → Accessibility, then try again."
    }

    private func attribute(_ element: AXUIElement, _ name: String) -> AnyObject? {
        var value: AnyObject?
        return AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success ? value : nil
    }

    private func text(_ element: AXUIElement, _ name: String) -> String {
        switch attribute(element, name) {
        case let string as String: return string
        case let number as NSNumber: return number.stringValue
        default: return ""
        }
    }

    func windows() throws -> [DesktopWindow] {
        var found: [DesktopWindow] = []
        for app in NSWorkspace.shared.runningApplications where app.activationPolicy == .regular && app.processIdentifier != ProcessInfo.processInfo.processIdentifier {
            let element = AXUIElementCreateApplication(app.processIdentifier)
            for window in attribute(element, kAXWindowsAttribute) as? [AXUIElement] ?? [] {
                found.append(DesktopWindow(pid: app.processIdentifier, app: app.localizedName ?? "App", title: text(window, kAXTitleAttribute)))
            }
        }
        return found.sorted { $0.label.lowercased() < $1.label.lowercased() }
    }

    private func find(_ window: DesktopWindow) throws -> AXUIElement {
        guard NSRunningApplication(processIdentifier: window.pid) != nil else { throw MasonError("\(window.app) is no longer running. Attach a window again.") }
        let all = attribute(AXUIElementCreateApplication(window.pid), kAXWindowsAttribute) as? [AXUIElement] ?? []
        guard let match = all.first(where: { text($0, kAXTitleAttribute) == window.title }) ?? all.first else { throw MasonError("\(window.app) has no open window. Attach a window again.") }
        return match
    }

    func observe(_ window: DesktopWindow) throws -> [DesktopControl] {
        let root = try find(window)
        elements = [:]
        var controls: [DesktopControl] = []
        // Breadth first, so a deep tree still shows the controls nearest the top of the window.
        var queue: [(AXUIElement, Int)] = [(root, 0)], visited = 0
        while !queue.isEmpty, controls.count < Desktop.controlLimit, visited < 4000 {
            let (element, depth) = queue.removeFirst()
            visited += 1
            let role = text(element, kAXRoleAttribute)
            var names: CFArray?
            let actions = AXUIElementCopyActionNames(element, &names) == .success ? (names as? [String] ?? []) : []
            var settable = DarwinBoolean(false)
            AXUIElementIsAttributeSettable(element, kAXValueAttribute as CFString, &settable)
            let canPress = actions.contains(kAXPressAction as String), canSet = settable.boolValue && ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField", "AXSlider", "AXCheckBox"].contains(role)
            let name = [text(element, kAXTitleAttribute), text(element, kAXDescriptionAttribute)].first { !$0.isEmpty } ?? ""
            let value = text(element, kAXValueAttribute)
            if canPress || canSet || (role == "AXStaticText" && !value.isEmpty) {
                let id = "c\(controls.count + 1)"
                elements[id] = element
                controls.append(DesktopControl(id: id, role: String(role.dropFirst(2)).lowercased(), name: name, value: value, canPress: canPress, canSet: canSet))
            }
            if depth < 24 { for child in attribute(element, kAXChildrenAttribute) as? [AXUIElement] ?? [] { queue.append((child, depth + 1)) } }
        }
        return controls
    }

    /// The window as ScreenCaptureKit knows it. Taking its picture needs the Screen Recording permission.
    private func shareable(_ window: DesktopWindow) async throws -> SCWindow {
        let content: SCShareableContent
        do { content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true) }
        catch { throw MasonError("Mason needs the Screen Recording permission to take a picture of a window. Allow it in System Settings → Privacy & Security → Screen Recording, then try again.") }
        let mine = content.windows.filter { $0.owningApplication?.processID == window.pid && $0.windowLayer == 0 }
        guard let match = mine.first(where: { $0.title == window.title }) ?? mine.max(by: { $0.frame.width * $0.frame.height < $1.frame.width * $1.frame.height }) else {
            throw MasonError("\(window.app) has no window on screen. Attach a window again.")
        }
        return match
    }

    func frame(_ window: DesktopWindow) async throws -> DesktopFrame? {
        let target = try await shareable(window)
        let configuration = SCStreamConfiguration()
        // One pixel a point, so a point in the picture is a point on screen.
        configuration.width = max(1, Int(target.frame.width)); configuration.height = max(1, Int(target.frame.height))
        configuration.showsCursor = false
        let image = try await SCScreenshotManager.captureImage(contentFilter: SCContentFilter(desktopIndependentWindow: target), configuration: configuration)
        guard let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else { throw MasonError("The window's picture could not be made.") }
        return DesktopFrame(width: image.width, height: image.height, png: png)
    }

    func pointer(_ window: DesktopWindow, action: String, x: Int, y: Int, endX: Int, endY: Int) async throws {
        let target = try await shareable(window)
        NSRunningApplication(processIdentifier: window.pid)?.activate()
        try? await Task.sleep(nanoseconds: 200_000_000)
        func post(_ type: CGEventType, _ px: Int, _ py: Int) throws {
            guard let event = CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: CGPoint(x: target.frame.minX + CGFloat(px), y: target.frame.minY + CGFloat(py)), mouseButton: .left) else {
                throw MasonError("The click could not be made.")
            }
            event.post(tap: .cghidEventTap)
        }
        try post(.mouseMoved, x, y)
        try post(.leftMouseDown, x, y)
        if action == "drag" {
            for step in 1...8 {
                try post(.leftMouseDragged, x + (endX - x) * step / 8, y + (endY - y) * step / 8)
                try? await Task.sleep(nanoseconds: 20_000_000)
            }
            try post(.leftMouseUp, endX, endY)
        } else {
            try post(.leftMouseUp, x, y)
        }
    }

    func act(_ window: DesktopWindow, control: String, operation: String, value: String) throws {
        _ = try find(window)
        guard let element = elements[control] else { throw MasonError("Observe the window first, then use the id of a control from that observation.") }
        let result = operation == "invoke" ? AXUIElementPerformAction(element, kAXPressAction as CFString) : AXUIElementSetAttributeValue(element, kAXValueAttribute as CFString, value as CFString)
        guard result == .success else { throw MasonError("The app did not accept that (\(result.rawValue)). Observe the window again; it may have changed.") }
    }
}
#endif
