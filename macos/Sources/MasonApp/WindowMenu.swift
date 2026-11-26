import SwiftUI
import ApplicationServices
import MasonCore

// Attaching a window of another app to the session, so Mason can read its controls and, with approval, operate it.

extension AppModel {
    func attachWindow(_ window: DesktopWindow?) {
        guard var current = session else { return }
        current.desktop = window
        save(current)
    }

    /// The windows of other apps, or none when Mason has not been allowed to read them.
    func openWindows() -> [DesktopWindow] {
        guard let desktop = Desktop.system, desktop.unavailable() == nil else { return [] }
        return (try? desktop.windows()) ?? []
    }

    var canReadWindows: Bool { Desktop.system?.unavailable() == nil }

    /// Shows the macOS prompt that leads to System Settings → Privacy & Security → Accessibility.
    func askForAccessibility() {
        _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
    }
}

struct WindowMenu: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    @State private var open: [DesktopWindow] = []
    @State private var allowed = false

    var body: some View {
        Menu {
            if let attached = session.desktop {
                Button("Detach \(attached.label)") { model.attachWindow(nil) }
                Divider()
            }
            if !allowed {
                Button("Allow Mason to Read Windows…") { model.askForAccessibility() }
            } else if open.isEmpty {
                Text("No windows of other apps are open")
            }
            ForEach(Array(open.enumerated()), id: \.offset) { _, window in
                Button(window.label) { model.attachWindow(window) }
            }
            Divider()
            Button("Refresh") { refresh() }
        } label: {
            Label(session.desktop?.app ?? "Window", systemImage: "macwindow").lineLimit(1)
        }
        .menuStyle(.borderlessButton).fixedSize().chip()
        .disabled(model.running)
        .help(session.desktop.map { "Mason can read \($0.label), and asks before pressing or changing anything in it." } ?? "Attach a window of another app so Mason can read its controls and, with your approval each time, operate it.")
        .accessibilityLabel(session.desktop.map { "Attached window: \($0.label)" } ?? "Attach a window")
        .onAppear { refresh() }
    }

    func refresh() {
        allowed = model.canReadWindows
        open = model.openWindows()
    }
}
