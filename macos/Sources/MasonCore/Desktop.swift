import Foundation
#if os(macOS)
import AppKit
import ApplicationServices
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
            ToolSpec(name: "desktop_act", description: "Ask the user to invoke (press) or set a control from the most recent observation of the attached window. Returns a fresh observation. value is the text to set; use an empty string with invoke.",
                     parameters: schema(["operation": ["type": "string", "enum": ["invoke", "set"]], "id": text, "value": text], required: ["operation", "id", "value"])),
        ]
    }()

    /// The window tools a session is offered: none without an attached window, and no acting in a read-only session.
    static func desktopTools(for session: Session, available: Bool) -> [ToolSpec] {
        guard available, session.desktop != nil else { return [] }
        return session.askOnly || session.permission == .readOnly ? [desktopTools[0]] : desktopTools
    }

    func performDesktop(_ call: ToolCall, arguments: [String: Any], session: inout Session) async throws -> (Activity.State, String)? {
        guard call.name == "desktop_observe" || call.name == "desktop_act" else { return nil }
        guard let window = session.desktop else { throw MasonError("No window is attached to this session.") }
        guard let desktop = desktop ?? Desktop.system else { throw MasonError("This build cannot operate windows on this computer.") }
        if let problem = desktop.unavailable() { throw MasonError(problem) }
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

    func act(_ window: DesktopWindow, control: String, operation: String, value: String) throws {
        _ = try find(window)
        guard let element = elements[control] else { throw MasonError("Observe the window first, then use the id of a control from that observation.") }
        let result = operation == "invoke" ? AXUIElementPerformAction(element, kAXPressAction as CFString) : AXUIElementSetAttributeValue(element, kAXValueAttribute as CFString, value as CFString)
        guard result == .success else { throw MasonError("The app did not accept that (\(result.rawValue)). Observe the window again; it may have changed.") }
    }
}
#endif
