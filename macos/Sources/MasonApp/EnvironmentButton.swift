import SwiftUI
import MasonCore

/// The project environment for commands Mason runs in this session: the shell they run in and whether they (and the
/// web tools) may reach the internet. Matches the Environment popover on Windows and Linux; the network choice runs
/// the same code as /network in the session.
struct EnvironmentButton: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    @State private var open = false

    private var shell: String { (Shell.path as NSString).lastPathComponent }
    private var home: URL { model.store?.folder.deletingLastPathComponent() ?? DataHome.url }
    private var choice: String { session.network ?? "default" }

    var body: some View {
        Button { open.toggle() } label: { Label(shell, systemImage: "terminal").lineLimit(1) }
            .buttonStyle(.plain).chip().fixedSize()
            .disabled(model.running)
            .help("Project environment: the command shell and network access for this session")
            .accessibilityLabel("Project coding environment")
            .popover(isPresented: $open, arrowEdge: .bottom) {
                VStack(alignment: .leading, spacing: 10) {
                    Text("Project environment").font(.title3.weight(.semibold))
                    Text("Command shell").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    Text(Shell.path).font(.system(.body, design: .monospaced))
                    Text(Shell.sandboxAvailable ? "Auto runs commands in the macOS sandbox, limited to this project." : "The macOS sandbox is unavailable, so Auto refuses commands.")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    Text("Network").font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 4)
                    Picker("Network", selection: Binding(get: { choice }, set: { model.runControl("/network", $0) })) {
                        Text("Default: this computer's setting").tag("default")
                        Text("Ask for commands and web").tag("ask")
                        Text("Off: no internet").tag("off")
                    }
                    .labelsHidden().accessibilityLabel("Command network policy")
                    Text(NetworkSettings.describe(NetworkSettings.mode(session, home: home)))
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    Text("Any source language · execution depends on the toolchains installed on this Mac.")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                .padding(16)
                .frame(width: 340)
            }
    }
}
