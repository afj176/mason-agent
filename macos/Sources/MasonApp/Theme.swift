import SwiftUI
import AppKit
import MasonCore

/// How the app looks: Mason's colours in light and dark, the mascot, and the pieces every pane shares.
enum Brand {
    /// A colour that follows the window's light or dark appearance.
    static func adaptive(light: UInt32, dark: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let hex = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255, blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
        })
    }

    static let background = adaptive(light: 0xF8F6F2, dark: 0x171A2B)
    static let sidebar = adaptive(light: 0xF1EEF6, dark: 0x131626)
    static let surface = adaptive(light: 0xFFFFFF, dark: 0x24283E)
    static let accent = adaptive(light: 0x4849A8, dark: 0xC3C2FF)
    static let onAccent = adaptive(light: 0xFFFFFF, dark: 0x171A2B)
    static let muted = adaptive(light: 0x535B72, dark: 0xC3C9E0)
    static let seam = adaptive(light: 0xD9DBE1, dark: 0x3B4059)

    private static func resource(_ name: String) -> NSImage? {
        guard let url = Bundle.main.resourceURL?.appendingPathComponent(name) else { return nil }
        return NSImage(contentsOf: url)
    }
    /// The mascot's face, for the brand mark and beside Mason's answers. Missing art falls back to a symbol.
    static let mascotImage: NSImage? = resource("mascot.png")
    static let welcomeImage: NSImage? = resource("welcome.png")
}

/// The mascot at a given size.
struct Mascot: View {
    let size: CGFloat
    var body: some View {
        Group {
            if let image = Brand.mascotImage { Image(nsImage: image).resizable().interpolation(.high) }
            else { Image(systemName: "hammer.circle.fill").resizable().foregroundStyle(Brand.accent) }
        }
        .frame(width: size, height: size)
        .accessibilityLabel("Mason Agent")
    }
}

/// The line that says who is speaking: "You", or the mascot and "Mason Agent".
struct Speaker: View {
    let mason: Bool
    var body: some View {
        HStack(spacing: 10) {
            if mason {
                Mascot(size: 28)
                Text("Mason Agent").fontWeight(.bold)
            } else {
                Image(systemName: "person.fill")
                    .font(.system(size: 13))
                    .frame(width: 28, height: 28)
                    .background(Color.primary.opacity(0.09), in: Circle())
                Text("You").fontWeight(.semibold).foregroundStyle(Brand.muted)
            }
        }
    }
}

extension View {
    /// Indents a message under its speaker line.
    func messageBody() -> some View { padding(.leading, 38) }

    /// A raised card in Mason's surface colour.
    func card(radius: CGFloat = 12) -> some View {
        background(Brand.surface, in: RoundedRectangle(cornerRadius: radius))
            .overlay(RoundedRectangle(cornerRadius: radius).stroke(Brand.seam, lineWidth: 1))
    }

    /// A small rounded control, like the choices in the composer.
    func chip() -> some View {
        padding(.horizontal, 10).padding(.vertical, 5)
            .background(Color.primary.opacity(0.07), in: RoundedRectangle(cornerRadius: 10))
    }
}

/// Follow the system, or always light or dark.
enum AppearanceChoice: String, CaseIterable, Identifiable {
    case system = "Follow System", light = "Light", dark = "Dark"
    var id: String { rawValue }
    var appearance: NSAppearance? {
        switch self {
        case .system: return nil
        case .light: return NSAppearance(named: .aqua)
        case .dark: return NSAppearance(named: .darkAqua)
        }
    }
}

/// A quiet line at the bottom of the window: what Mason is doing, and where the session lives.
struct StatusBar: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        HStack(spacing: 12) {
            Text(left).lineLimit(1)
            Spacer()
            Text(right).lineLimit(1).truncationMode(.head)
        }
        .font(.caption)
        .foregroundStyle(Brand.muted)
        .padding(.horizontal, 16).padding(.vertical, 5)
        .background(Brand.background)
        .overlay(alignment: .top) { Rectangle().fill(Brand.seam).frame(height: 1) }
    }

    var left: String {
        if model.pane == .chat { return model.chatRunning ? "Answering" : "Ready" }
        if model.running { return model.liveStatus.isEmpty ? "Working" : model.liveStatus }
        if !model.scheduleStatus.isEmpty { return model.scheduleStatus }
        guard let session = model.session, session.state != "Ready" else { return "Ready" }
        return session.state + (session.detail.isEmpty ? "" : " · " + session.detail)
    }

    var right: String {
        guard model.pane == .code, let session = model.session else { return "Everything stays on this Mac" }
        return URL(fileURLWithPath: session.workspace).lastPathComponent + " · " + (session.askOnly ? Permission.readOnly : session.permission).label.lowercased() + " · Session saved on this Mac"
    }
}

/// The page shown before any project is open.
struct WelcomeView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        VStack(spacing: 14) {
            if let image = Brand.welcomeImage {
                Image(nsImage: image).resizable().interpolation(.high).scaledToFit().frame(width: 230, height: 230)
                    .accessibilityLabel("Mortar, the Mason Agent mascot, holding a wrench")
            } else {
                Mascot(size: 120)
            }
            Text("Make the next move clear").font(.system(size: 30, weight: .heavy))
            Text("Open a project folder and ask Mason to read, explain or change it.\nIt runs on your own local model, and asks before each edit and command.")
                .font(.title3).foregroundStyle(Brand.muted).multilineTextAlignment(.center)
            Button("Open Project…") { model.openFolder() }
                .buttonStyle(.borderedProminent).controlSize(.large).keyboardShortcut(.defaultAction)
                .padding(.top, 8)
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
