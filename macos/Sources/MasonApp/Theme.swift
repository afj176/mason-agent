import SwiftUI
import AppKit
import CoreText
import MasonCore

/// The theme family on screen, saved as "Family" in appearance.json in Mason's data folder (shared with the mason
/// command). Choosing one applies it live: every Brand colour reads the family when it is drawn, and the windows
/// rebuild their views when it changes.
@MainActor
final class ThemeStore: ObservableObject {
    static let shared = ThemeStore()
    /// The family the colour providers read. Set only on the main actor, read while AppKit draws.
    /// Loaded on first use, so a colour drawn before the store exists already has the saved family.
    nonisolated(unsafe) static var current: ThemeFamily = ThemeFamilies.saved(home: DataHome.url)

    @Published private(set) var family: ThemeFamily
    /// Why the last choice could not be saved, or nil. The family still applies until the app quits.
    @Published private(set) var problem: String?

    private init() { family = Self.current }

    /// Shows the family and saves it to appearance.json.
    func choose(_ chosen: ThemeFamily) {
        guard chosen != family else { return }
        Self.current = chosen
        do { try ThemeFamilies.save(chosen, home: DataHome.url); problem = nil } catch { problem = error.localizedDescription }
        family = chosen
    }
}

/// How the app looks: the active theme family's colours in light and dark, the poses, and the pieces every pane shares.
enum Brand {
    static func nsColor(_ rgb: UInt32, alpha: CGFloat = 1) -> NSColor {
        let c = ThemeFamilies.channels(rgb)
        return NSColor(srgbRed: CGFloat(c.red) / 255, green: CGFloat(c.green) / 255, blue: CGFloat(c.blue) / 255, alpha: alpha)
    }

    /// A colour worked out from the active family's tokens for the window's light or dark appearance, when it is drawn.
    static func themed(_ pick: @escaping (ThemeTokens, Bool) -> UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let dark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            return nsColor(pick(ThemeStore.current.tokens(dark: dark), dark))
        })
    }
    /// A colour that is the same in every family: one shade for light windows, one for dark.
    static func adaptive(light: UInt32, dark: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            nsColor(appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light)
        })
    }
    /// Secondary text: the text colour mixed toward the background, as the Windows app derives it.
    static func secondaryText(_ t: ThemeTokens, dark: Bool) -> UInt32 { ThemeFamilies.mix(t.text, t.background, dark ? 0.22 : 0.28) }

    static let background = themed { t, _ in t.background }
    static let sidebar = themed { t, _ in t.pane }
    static let surface = themed { t, _ in t.card }
    static let seam = themed { t, _ in t.line }
    static let ink = themed { t, _ in t.text }
    static let accent = themed { t, _ in t.accent }
    static let onAccent = themed { t, _ in t.onAccent }
    static let muted = themed { t, dark in secondaryText(t, dark: dark) }
    /// The groove behind a meter or a segmented row.
    static let track = themed { t, _ in ThemeFamilies.mix(t.pane, t.background, 0.4) }

    // Status colours are shared by every family so a failure reads the same in each theme.
    static let success = adaptive(light: 0x14734A, dark: 0x7EE0A6)
    static let caution = adaptive(light: 0x8A5A00, dark: 0xF2C66D)
    static let critical = adaptive(light: 0xAF2323, dark: 0xFF9D94)
    static let search = adaptive(light: 0x0F6E7A, dark: 0x8FD9E3)
    /// The colour of a run ending's tone, from the same status colours.
    static func tone(_ tone: RunEnding.Tone) -> Color {
        switch tone {
        case .success: return success
        case .warning: return caution
        case .accent: return accent
        case .neutral: return muted
        case .danger: return critical
        }
    }
    /// The SF Symbol of a run ending.
    static func endingIcon(_ code: String) -> String {
        switch code {
        case "done": return "checkmark.circle.fill"
        case "unverified": return "circle.lefthalf.filled"
        case "question": return "questionmark.circle"
        case "model_loop": return "arrow.triangle.2.circlepath"
        case "model_stuck", "blocked": return "nosign"
        case "step_limit", "time_limit": return "pause.circle"
        case "stopped": return "stop.circle"
        case "model_unavailable": return "powerplug"
        default: return "xmark.circle.fill"
        }
    }
    /// Model fit tones, each paired with its words in the panel.
    static let fitGood = success
    static let fitTight = caution
    static let fitOver = critical

    /// A file in the app's Resources folder, or nil when it is missing (a build run from the package without bundling).
    private static func resource(_ path: String) -> NSImage? {
        guard let url = Bundle.main.resourceURL?.appendingPathComponent(path) else { return nil }
        return NSImage(contentsOf: url)
    }
    /// The app icon, for the brand header and beside Mason's answers.
    static let iconImage: NSImage? = resource("icon.png")
    private static var poses: [Pose: NSImage] = [:]
    /// One pose still from Resources/Poses, loaded once.
    static func image(_ pose: Pose) -> NSImage? {
        if let cached = poses[pose] { return cached }
        let loaded = resource("Poses/\(pose.rawValue).png")
        poses[pose] = loaded
        return loaded
    }

    /// Manrope, the brand display face, when the app bundles it; otherwise the system font.
    static func display(_ size: CGFloat, weight: Font.Weight = .heavy) -> Font {
        manropeRegistered ? Font.custom("Manrope", size: size).weight(weight) : Font.system(size: size, weight: weight)
    }
    private static let manropeRegistered: Bool = {
        guard let url = Bundle.main.resourceURL?.appendingPathComponent("Fonts/Manrope.ttf"), FileManager.default.fileExists(atPath: url.path) else { return false }
        var error: Unmanaged<CFError>?
        // Already registered (a second window) counts as available.
        return CTFontManagerRegisterFontsForURL(url as CFURL, .process, &error) || NSFont(name: "Manrope", size: 12) != nil
    }()
}

/// The app icon at a given size: the brand mark and the speaker mark beside Mason's answers.
struct AppIconMark: View {
    let size: CGFloat
    var body: some View {
        Group {
            if let image = Brand.iconImage { Image(nsImage: image).resizable().interpolation(.high) }
            else { Image(systemName: "hammer.circle.fill").resizable().foregroundStyle(Brand.accent) }
        }
        .frame(width: size, height: size)
        .accessibilityLabel("Mason Agent")
    }
}

/// The app icon with "Mason Agent" beside it, in Manrope when it is bundled.
struct BrandHeader: View {
    var iconSize: CGFloat = 26
    var textSize: CGFloat = 15
    var body: some View {
        HStack(spacing: 8) {
            AppIconMark(size: iconSize)
            Text("Mason Agent").font(Brand.display(textSize)).foregroundStyle(Brand.ink)
        }
        .accessibilityElement(children: .combine)
    }
}

/// One pose still, `height` points tall. Poses are wider than tall, so the width follows the picture.
struct PoseImage: View {
    let pose: Pose
    let height: CGFloat
    var meaning: String?

    var body: some View {
        if let image = Brand.image(pose), image.size.height > 0 {
            Image(nsImage: image).resizable().interpolation(.high)
                .frame(width: (height * image.size.width / image.size.height).rounded(), height: height)
                .accessibilityLabel("Mason Agent: " + (meaning ?? pose.meaning))
        } else {
            AppIconMark(size: height)
        }
    }
}

/// The line that says who is speaking: "You", or the app icon and "Mason Agent".
struct Speaker: View {
    let mason: Bool
    var body: some View {
        HStack(spacing: 10) {
            if mason {
                AppIconMark(size: 28)
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

/// The five theme families as cards, each split into the family's light and dark window colours with its accent.
struct ThemeFamilyPicker: View {
    @ObservedObject var theme = ThemeStore.shared

    var body: some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 8), count: 5), spacing: 8) {
            ForEach(ThemeFamilies.all) { family in
                let on = family == theme.family
                Button { theme.choose(family) } label: {
                    VStack(spacing: 0) {
                        HStack(spacing: 0) { half(family.light); half(family.dark) }
                            .frame(height: 34)
                        Text(family.name).font(.caption).lineLimit(1).truncationMode(.tail)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 8).padding(.vertical, 5)
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                    .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(on ? Brand.accent : Brand.seam, lineWidth: on ? 2 : 1))
                    .contentShape(RoundedRectangle(cornerRadius: 6))
                }
                .buttonStyle(.plain)
                .help("\(family.name): light and dark colours")
                .accessibilityLabel("\(family.name) theme")
                .accessibilityAddTraits(on ? .isSelected : [])
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Theme")
    }

    func half(_ tokens: ThemeTokens) -> some View {
        Color(nsColor: Brand.nsColor(tokens.background))
            .overlay(alignment: .bottomLeading) {
                RoundedRectangle(cornerRadius: 3).fill(Color(nsColor: Brand.nsColor(tokens.accent)))
                    .frame(width: 16, height: 8).padding(.leading, 8).padding(.bottom, 7)
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

/// The session's state with its pose, about 46 points tall, above the composer: reading, editing, running checks
/// or planning while it runs; then waiting, ready to review, finished, restored or a failed check.
struct StatusPose: View {
    @EnvironmentObject var model: AppModel
    let session: Session

    var body: some View {
        // Re-read every few seconds so "restored" gives way once its 30 seconds are over.
        TimelineView(.periodic(from: .now, by: 5)) { context in
            let shown = currentPose(at: context.date)
            if shown != nil || ended != nil {
                HStack(spacing: 10) {
                    if let shown { PoseImage(pose: shown, height: 46) }
                    if model.running { ProgressView().controlSize(.small) }
                    if let ended { EndingPill(code: ended.code, style: ended.style) }
                    Text(text).font(.callout).foregroundStyle(Brand.muted).lineLimit(2)
                    Spacer(minLength: 0)
                }
                .frame(maxWidth: 900)
                .frame(maxWidth: .infinity)
                .padding(.horizontal, 20).padding(.top, 4)
                .transition(.opacity)
            }
        }
    }

    func currentPose(at now: Date) -> Pose? {
        let tool = model.running ? model.liveActivities.last(where: { $0.state == .running })?.tool : nil
        let restored = model.restoredAt.map { now.timeIntervalSince($0) < 30 } ?? false
        let reviewable = session.turns.contains { !$0.undone && !$0.changes.isEmpty }
        return Pose.forStatus(state: model.running ? "Running" : session.state, hasTurns: !session.turns.isEmpty,
                              runningTool: tool, restoredRecently: restored, reviewableChanges: reviewable, ending: session.ending?.code)
    }

    /// How the latest request ended, when it is a known ending and nothing is running.
    var ended: (code: String, style: RunEnding.Style)? {
        guard !model.running, !session.turns.isEmpty, let ending = session.ending, let style = ending.style else { return nil }
        return (ending.code, style)
    }

    var text: String {
        if model.running { return model.liveStatus.isEmpty ? "Working" : model.liveStatus }
        // The pill already names the ending, so the text is what happened.
        if ended != nil { return session.detail }
        return session.state + (session.detail.isEmpty ? "" : " · " + session.detail)
    }
}

/// How a request ended, as a small pill in the ending's tone: its icon and headline.
struct EndingPill: View {
    let code: String
    let style: RunEnding.Style

    var body: some View {
        let color = Brand.tone(style.tone)
        Label(style.headline, systemImage: Brand.endingIcon(code))
            .font(.caption.weight(.semibold))
            .foregroundStyle(color)
            .padding(.horizontal, 8).padding(.vertical, 3)
            .background(color.opacity(0.14), in: Capsule())
            .fixedSize()
    }
}

/// A message from Mason in a sheet, with a pose when it reports a problem: needs-attention when the Mac needs the
/// person to act (a runtime, a download, a model folder), check-failed when something broke.
struct NoticeSheet: View {
    @EnvironmentObject var model: AppModel
    let text: String

    /// Phrases of the notices that ask the person to set something up on this Mac.
    static let attention = ["no model is running", "needs a running model", "install llama.cpp", "llama.cpp was not installed",
                            "download did not finish", "hugging face", "model folder", "data folder", "does not include the mason tool"]

    var pose: Pose? {
        let lower = text.lowercased()
        if Self.attention.contains(where: { lower.contains($0) }) { return .needsAttention }
        if let failed = model.lastError, !failed.isEmpty, text.contains(failed) { return .checkFailed }
        return nil
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .top, spacing: 16) {
                if let shown = pose { PoseImage(pose: shown, height: shown == .needsAttention ? 80 : 66) } else { AppIconMark(size: 48) }
                VStack(alignment: .leading, spacing: 6) {
                    Text("Mason Agent").font(.headline)
                    if text.count > 600 {
                        ScrollView {
                            Text(text).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .frame(height: 320)
                    } else {
                        Text(text).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
            }
            HStack {
                Spacer()
                Button("OK") { model.notice = nil }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 460)
    }
}

/// The page shown before any project is open: the first-launch pose until there is a project, then open-project.
struct WelcomeView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        VStack(spacing: 14) {
            if model.sessions.isEmpty {
                PoseImage(pose: .firstLaunch, height: 148)
            } else {
                PoseImage(pose: .openProject, height: 124)
            }
            Text("Make the next move clear").font(Brand.display(30))
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
