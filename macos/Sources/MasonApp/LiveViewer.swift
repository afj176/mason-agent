import SwiftUI
import MasonCore

// Moments: the side panel's feed of what the run did, newest first. A step that made something (a file, a change, a
// command's report, a page) is a card with Open; reading and other steps are quiet one-line moments, and each request
// starts with a "You asked" line. Opening a change, report or set of reads shows it in the panel with ← Moments to go
// back; the opened moment stays on the shelf until it is closed. Every fact the old activity rows showed (state,
// subject, result, skipped repeats) is still under Step details or Raw there.

extension AppModel {
    /// Opens the side panel on the moment holding a call, or on the feed when there is none.
    func showActivity(_ call: UUID?) {
        if let call {
            activityPick = Moments.openKey(call)
            panel = "moment"
        } else {
            panel = "moments"
        }
        showChanges = true
    }

    /// Opens one moment of the feed: a scene by one of its calls, and optionally one file of an edit.
    func openMoment(_ call: UUID, path: String? = nil) {
        activityPick = Moments.openKey(call, path: path)
        panel = "moment"
    }

    /// Shows a file in the Files panel.
    func openFile(_ path: String) {
        fileLine = 1
        fileShown = path
        panel = "files"
    }

    /// Shows a page in the Website panel.
    func openPage(_ page: String) {
        pageShown = page
        panel = "website"
    }
}

extension Brand {
    static func callState(_ state: Activity.State) -> Color {
        switch state {
        case .completed: return success
        case .running: return accent
        case .skipped, .denied: return caution
        case .failed: return critical
        }
    }
}

/// A call's state in plain words.
func callStateWords(_ state: Activity.State) -> String {
    switch state {
    case .completed: return "Completed"
    case .running: return "Running"
    case .failed: return "Failed"
    case .denied: return "Declined"
    case .skipped: return "Repeat skipped"
    }
}

/// A small rounded label in a tone.
struct ScenePill: View {
    let text: String
    var color: Color = Brand.muted

    var body: some View {
        Text(text)
            .font(.caption).fontWeight(.medium)
            .foregroundStyle(color)
            .lineLimit(1)
            .padding(.horizontal, 7).padding(.vertical, 2)
            .background(color.opacity(0.13), in: Capsule())
    }
}

// MARK: The panel's header and shelf

/// The side panel's one header: "Moments" (live while running) on the feed, or the way back to it, then the shelf of
/// the panel's views, the opened moment and what the latest run made so far. The shown one is outlined.
struct SurfaceHeader: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    let calls: [Activity]

    static let views: [(key: String, label: String, icon: String)] = [
        ("changes", "Changes", "plusminus"), ("plan", "Plan", "list.bullet"), ("files", "Files", "folder"), ("website", "Website", "globe"),
    ]

    var onFeed: Bool { !["moment", "changes", "plan", "files", "website"].contains(model.panel) }

    var body: some View {
        let scenes = ActivityScenes.group(calls)
        let openedTitle = MomentDetail.title(pick: model.activityPick, session: session, calls: calls, scenes: scenes, running: model.running)
        let made = Moments.madeSoFar(calls, from: ActivityScenes.requestStart(session), changes: ActivityScenes.sessionChanges(session))
        HStack(spacing: 10) {
            if onFeed {
                HStack(spacing: 6) {
                    Text("Moments").font(.headline).foregroundStyle(Brand.ink)
                    if model.running {
                        ScenePill(text: "● Live", color: Brand.accent).accessibilityLabel("Live: the run is going")
                    }
                }
                .fixedSize()
            } else {
                Button { model.panel = "moments" } label: { Label("Moments", systemImage: "chevron.left") }
                    .controlSize(.small)
                    .help("Back to the Moments feed")
                    .accessibilityLabel("Back to Moments")
            }
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    if let openedTitle {
                        ShelfChip(label: openedTitle, icon: "sparkles", selected: model.panel == "moment",
                                  show: { model.panel = "moment" },
                                  close: {
                                      model.activityPick = nil
                                      if model.panel == "moment" { model.panel = "moments" }
                                  })
                    }
                    ForEach(Self.views, id: \.key) { view in
                        ShelfChip(label: view.label, icon: view.icon, selected: model.panel == view.key, show: { model.panel = view.key })
                    }
                    ForEach(made, id: \.call) { thing in
                        madeChip(thing)
                    }
                }
                .padding(.vertical, 2)
            }
            .accessibilityLabel("Shelf: the panel's views and what the run made")
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
    }

    @ViewBuilder
    func madeChip(_ thing: MadeThing) -> some View {
        switch thing.kind {
        case .file:
            let path = ActivityScenes.normalize(thing.target)
            let key = Moments.openKey(thing.call, path: path)
            if thing.created {
                ShelfChip(label: thing.label, icon: "doc.badge.plus", selected: model.panel == "files" && model.fileShown == thing.target,
                          show: { model.openFile(thing.target) })
            } else if key != model.activityPick {
                ShelfChip(label: thing.label, icon: "pencil", selected: false, show: { model.openMoment(thing.call, path: path) })
            }
        case .page:
            ShelfChip(label: thing.label, icon: "globe", selected: false, show: { model.openPage(thing.target) })
        }
    }
}

/// One item on the shelf: a view, the opened moment, or something the run made. The shown one is outlined; the opened
/// moment can be closed.
struct ShelfChip: View {
    let label: String
    var icon: String?
    let selected: Bool
    let show: () -> Void
    var close: (() -> Void)?

    var shortLabel: String { label.count > 28 ? String(label.prefix(27)) + "…" : label }

    var body: some View {
        HStack(spacing: 0) {
            Button(action: show) {
                HStack(spacing: 4) {
                    if let icon { Image(systemName: icon).font(.caption).accessibilityHidden(true) }
                    Text(shortLabel).lineLimit(1)
                }
                .font(.callout)
                .padding(.leading, 9).padding(.trailing, close == nil ? 9 : 3).padding(.vertical, 4)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(selected ? Brand.ink : Brand.muted)
            .help(label)
            .accessibilityLabel((selected ? "Showing " : "Show ") + label)
            if let close {
                Button(action: close) {
                    Image(systemName: "xmark").font(.system(size: 9, weight: .semibold))
                        .padding(.horizontal, 6).padding(.vertical, 7)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(Brand.muted)
                .help("Close " + label)
                .accessibilityLabel("Close " + label)
            }
        }
        .fixedSize()
        .background(selected ? Brand.surface : Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(selected ? Brand.accent : Brand.seam, lineWidth: selected ? 1.5 : 1))
    }
}

// MARK: The feed

struct MomentsFeed: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    let calls: [Activity]

    var body: some View {
        if calls.isEmpty {
            MomentsEmpty(running: model.running)
        } else {
            let scenes = ActivityScenes.group(calls)
            let items = Array(Moments.feed(scenes: scenes, requests: Moments.requests(session.turns, activityCount: calls.count)).prefix(Moments.feedLimit))
            let rows: [(id: String, item: MomentItem)] = items.enumerated().map { offset, item in
                if case .scene(let scene) = item { return (scene.key, item) }
                return ("a\(offset)", item)
            }
            let changes = ActivityScenes.sessionChanges(session)
            let newest = model.running ? scenes.last?.key : nil
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10) {
                    if !model.running, session.ending != nil { EndMoment(session: session) }
                    ForEach(rows, id: \.id) { row in
                        switch row.item {
                        case .asked(let text): AskedLine(text: text)
                        case .scene(let scene): SceneMoments(scene: scene, calls: calls, changes: changes, newest: scene.key == newest)
                        }
                    }
                }
                .padding(.horizontal, 14).padding(.vertical, 10)
            }
        }
    }
}

/// "You asked: …", where a request began.
struct AskedLine: View {
    let text: String

    var body: some View {
        Text("You asked: " + text)
            .font(.callout).foregroundStyle(Brand.muted)
            .lineLimit(2)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 14).padding(.top, 10).padding(.bottom, 2)
            .textSelection(.enabled)
    }
}

/// One edit card per file an edit scene touched.
struct EditPick: Identifiable {
    let path: String
    let call: Activity
    var id: String { path.lowercased() }
}

/// The moments of one scene: a card per file for edits, a card for a command or a page check, a quiet line otherwise.
struct SceneMoments: View {
    @EnvironmentObject var model: AppModel
    let scene: ActivityScene
    let calls: [Activity]
    let changes: [FileChange]
    let newest: Bool

    var body: some View {
        let steps = scene.indices.filter { $0 < calls.count }.map { calls[$0] }
        let when = steps.last.map { Moments.ago($0.at) } ?? ""
        let edits: [EditPick] = scene.kind == .edit
            ? Moments.editPaths(scene, calls).compactMap { path in Moments.lastCall(scene, calls, path: path).map { EditPick(path: path, call: $0) } }
            : []
        if !edits.isEmpty {
            VStack(spacing: 10) {
                ForEach(edits) { pick in
                    EditMoment(call: pick.call, path: pick.path, change: ActivityScenes.change(for: pick.path, in: changes), when: when, newest: newest)
                }
            }
        } else if scene.kind == .command, let last = steps.last {
            CommandMoment(title: ActivityScenes.label(scene, calls), call: last, when: when, newest: newest)
        } else if scene.kind == .page, let last = steps.last {
            PageMoment(title: ActivityScenes.label(scene, calls), steps: steps, open: last.id, when: when, newest: newest)
        } else if let last = steps.last {
            QuietMoment(label: Moments.sentence(ActivityScenes.label(scene, calls)), detail: Moments.readPlaces(scene, calls), when: when,
                        failed: steps.contains { ActivityScenes.failed($0) }, open: { model.openMoment(last.id) })
        }
    }
}

extension View {
    /// A moment's card, outlined in the accent while it is the newest step of a running request.
    func momentCard(newest: Bool) -> some View {
        padding(.horizontal, 14).padding(.vertical, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Brand.surface, in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(newest ? Brand.accent : Brand.seam, lineWidth: newest ? 1.5 : 1))
    }

    /// A text button in the accent, for a moment's secondary actions.
    func momentLink() -> some View {
        buttonStyle(.plain).foregroundStyle(Brand.accent).font(.callout)
    }
}

/// A moment's main action: prominent on the newest card.
struct MomentButton: View {
    let title: String
    let primary: Bool
    let action: () -> Void

    var body: some View {
        if primary {
            Button(title, action: action).buttonStyle(.borderedProminent).tint(Brand.accent).controlSize(.small)
        } else {
            Button(title, action: action).controlSize(.small)
        }
    }
}

/// The badge that says what kind of moment a card is.
struct MomentBadge: View {
    let glyph: String
    let color: Color

    var body: some View {
        Text(glyph)
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(color)
            .frame(width: 26, height: 26)
            .background(color.opacity(0.15), in: RoundedRectangle(cornerRadius: 7))
            .accessibilityHidden(true)
    }
}

/// A card's top line: its badge, title and facts, and how long ago it happened.
struct MomentTitle<Extras: View>: View {
    let glyph: String
    let color: Color
    let title: String
    let when: String
    let extras: Extras

    init(glyph: String, color: Color, title: String, when: String, @ViewBuilder extras: () -> Extras) {
        self.glyph = glyph; self.color = color; self.title = title; self.when = when; self.extras = extras()
    }

    var body: some View {
        HStack(spacing: 10) {
            MomentBadge(glyph: glyph, color: color)
            Text(title).fontWeight(.semibold).foregroundStyle(Brand.ink).lineLimit(1).truncationMode(.middle)
            extras
            Spacer(minLength: 0)
            Text(when).font(.caption).foregroundStyle(Brand.muted).fixedSize()
        }
    }
}

extension MomentTitle where Extras == EmptyView {
    init(glyph: String, color: Color, title: String, when: String) {
        self.init(glyph: glyph, color: color, title: title, when: when) { EmptyView() }
    }
}

/// A file the run created or changed.
struct EditMoment: View {
    @EnvironmentObject var model: AppModel
    let call: Activity
    let path: String
    let change: FileChange?
    let when: String
    let newest: Bool

    var name: String { (path as NSString).lastPathComponent }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let change, change.created {
                let lines = Moments.lineCount(change.after)
                let head = Moments.head(change.after)
                MomentTitle(glyph: "+", color: Brand.success, title: "Created " + name, when: when) {
                    Text(lines == 1 ? "1 line" : "\(lines) lines").font(.caption).foregroundStyle(Brand.muted).fixedSize()
                }
                if !head.isEmpty {
                    Text(head.joined(separator: "\n"))
                        .font(.system(size: 12, design: .monospaced)).foregroundStyle(Brand.muted)
                        .lineLimit(3).truncationMode(.tail)
                }
                HStack(spacing: 10) {
                    MomentButton(title: "Open", primary: newest) { model.openFile(change.path) }
                    Button("Diff") { model.openMoment(call.id, path: path) }.momentLink()
                }
            } else if let change {
                let counts = ActivityScenes.lineCounts(change)
                MomentTitle(glyph: "✎", color: Brand.accent, title: (change.deleted ? "Deleted " : "Changed ") + name, when: when) {
                    ScenePill(text: "+\(counts.added)", color: Brand.success)
                    if counts.removed > 0 { ScenePill(text: "−\(counts.removed)", color: Brand.critical) }
                }
                if let line = Moments.firstAdded(change), !line.isEmpty {
                    Text(line)
                        .font(.system(size: 12, design: .monospaced))
                        .lineLimit(1).truncationMode(.tail)
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Brand.success.opacity(0.13), in: RoundedRectangle(cornerRadius: 4))
                }
                MomentButton(title: "Open", primary: newest) { model.openMoment(call.id, path: path) }
            } else {
                let failed = ActivityScenes.failed(call)
                MomentTitle(glyph: failed ? "✕" : "✎", color: failed ? Brand.critical : Brand.accent, title: (failed ? "Couldn't change " : "Changing ") + name, when: when) {
                    if failed {
                        ScenePill(text: callStateWords(call.state), color: Brand.callState(call.state))
                    } else if call.state == .running {
                        ScenePill(text: "Writing…", color: Brand.accent)
                    }
                }
                MomentButton(title: "Open", primary: newest) { model.openMoment(call.id, path: path) }
            }
        }
        .momentCard(newest: newest)
        .accessibilityElement(children: .contain)
        .accessibilityLabel((change?.created == true ? "Created " : "Changed ") + path + ", " + when)
    }
}

/// A command or test run: passed and failed counts, a pass/fail bar, the first failing lines, and the report.
struct CommandMoment: View {
    @EnvironmentObject var model: AppModel
    let title: String
    let call: Activity
    let when: String
    let newest: Bool

    var body: some View {
        let summary = ActivityScenes.command(call)
        let failed = Moments.commandFailed(call)
        let active = call.state == .running
        VStack(alignment: .leading, spacing: 8) {
            MomentTitle(glyph: active ? "…" : failed ? "✕" : "✓", color: active ? Brand.accent : failed ? Brand.critical : Brand.success, title: title, when: when) {
                if let passed = summary.passed { ScenePill(text: "\(passed) passed", color: Brand.success) }
                if let failedCount = summary.failed, failedCount > 0 { ScenePill(text: "\(failedCount) failed", color: Brand.critical) }
                if summary.passed == nil, summary.failed == nil, let exit = summary.exitCode {
                    ScenePill(text: "exit \(exit)", color: exit == 0 ? Brand.success : Brand.critical)
                }
                if call.state == .skipped { ScenePill(text: "Repeat skipped", color: Brand.caution) }
            }
            if let passed = summary.passed, passed + (summary.failed ?? 0) > 0 {
                PassBar(passed: passed, failed: summary.failed ?? 0)
            }
            ForEach(Array(summary.failures.prefix(3)), id: \.self) { failure in
                Text(failure).font(.system(size: 12, design: .monospaced)).lineLimit(1).truncationMode(.tail)
            }
            Button(failed || summary.passed != nil ? "Open the report" : "Open the output") { model.openMoment(call.id) }.momentLink()
        }
        .momentCard(newest: newest)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(title + (failed ? ", failed" : "") + ", " + when)
    }
}

/// How many tests passed and failed, as one bar.
struct PassBar: View {
    let passed: Int
    let failed: Int

    var body: some View {
        GeometryReader { geometry in
            let total = CGFloat(max(1, passed + failed))
            let gap: CGFloat = passed > 0 && failed > 0 ? 2 : 0
            let room = max(0, geometry.size.width - gap)
            HStack(spacing: gap) {
                if passed > 0 { Rectangle().fill(Brand.success).frame(width: room * CGFloat(passed) / total) }
                if failed > 0 { Rectangle().fill(Brand.critical).frame(width: room * CGFloat(failed) / total) }
            }
        }
        .frame(height: 6)
        .clipShape(RoundedRectangle(cornerRadius: 3))
        .accessibilityElement()
        .accessibilityLabel("\(passed) passed, \(failed) failed")
    }
}

/// A page Mason checked: where, what it found, and the way to the page.
struct PageMoment: View {
    @EnvironmentObject var model: AppModel
    let title: String
    let steps: [Activity]
    let open: UUID
    let when: String
    let newest: Bool

    var body: some View {
        let page = steps.last { ($0.tool == "check_page" || $0.tool == "open_preview") && !$0.subject.trimmingCharacters(in: .whitespaces).isEmpty }?.subject
        let subject = page ?? steps.last { !$0.subject.isEmpty }?.subject
        let said = steps.last { !$0.result.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }?.result.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let failed = steps.contains { ActivityScenes.failed($0) }
        VStack(alignment: .leading, spacing: 6) {
            MomentTitle(glyph: "◫", color: failed ? Brand.critical : Brand.search, title: title, when: when)
            if let subject {
                Text(subject).font(.system(size: 12, design: .monospaced)).lineLimit(1).truncationMode(.middle)
            }
            if !said.isEmpty {
                Text(said.count > 220 ? String(said.prefix(219)) + "…" : said)
                    .font(.callout).foregroundStyle(Brand.muted).lineLimit(3)
            }
            HStack(spacing: 10) {
                if let page { MomentButton(title: "Open page", primary: newest) { model.openPage(page) } }
                Button("Details") { model.openMoment(open) }.momentLink()
            }
        }
        .momentCard(newest: newest)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(title + ", " + when)
    }
}

/// A reading or other step, as one quiet line that opens it.
struct QuietMoment: View {
    let label: String
    let detail: String
    let when: String
    let failed: Bool
    let open: () -> Void

    var body: some View {
        Button(action: open) {
            HStack(spacing: 10) {
                Circle().fill(failed ? Brand.critical : Brand.muted.opacity(0.6)).frame(width: 6, height: 6)
                Text(label).fontWeight(.semibold).foregroundStyle(Brand.ink).lineLimit(1)
                if !detail.isEmpty {
                    Text("· " + detail).foregroundStyle(Brand.muted).lineLimit(1).truncationMode(.tail)
                }
                Spacer(minLength: 0)
                Text(when).font(.caption).foregroundStyle(Brand.muted).fixedSize()
            }
            .font(.callout)
            .padding(.horizontal, 14).padding(.vertical, 6)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Open this step")
        .accessibilityLabel(label + (detail.isEmpty ? "" : ", " + detail) + (failed ? ", failed" : "") + ", " + when)
    }
}

/// On top of the feed once a run ended: how it ended, what was asked, what it amounted to, and the way to ship it.
struct EndMoment: View {
    @EnvironmentObject var model: AppModel
    let session: Session

    var request: String {
        let asked = (session.turns.last?.request ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let text = asked.isEmpty ? (session.ending?.style?.headline ?? "Run ended") : asked
        return text.count > 90 ? String(text.prefix(89)) + "…" : text
    }

    var body: some View {
        let facts = Moments.endFacts(session)
        let changed = !ActivityScenes.sessionChanges(session).isEmpty
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                if let style = session.ending?.style { ScenePill(text: style.glyph + " " + style.headline, color: Brand.tone(style.tone)).fixedSize() }
                Text(request).font(.headline).foregroundStyle(Brand.ink).lineLimit(1).truncationMode(.tail)
            }
            Text(facts.joined(separator: " · ")).font(.callout).foregroundStyle(Brand.muted)
            HStack(spacing: 10) {
                if changed {
                    Button("Review all changes") { model.panel = "changes" }.controlSize(.small)
                    SettingsLink { Text("Commit & open pull request…") }
                        .buttonStyle(.borderedProminent).tint(Brand.accent).controlSize(.small)
                        .help("Opens Settings, where the Project tab drafts and publishes the pull request")
                }
                Button("Summary") {
                    model.activityPick = ActivityScenes.endKey
                    model.panel = "moment"
                }
                .momentLink()
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
        .accessibilityElement(children: .contain)
        .accessibilityLabel((session.ending?.style?.headline ?? "Run ended") + ". " + facts.joined(separator: ", "))
    }
}

/// Moments before any step: a pose, what the feed will show, and the kinds of moment.
struct MomentsEmpty: View {
    let running: Bool

    struct Kind {
        let tick: String
        let color: Color
        let meaning: String
    }

    var kinds: [Kind] {
        [Kind(tick: "+", color: Brand.success, meaning: "A new file · opens the file"),
         Kind(tick: "✎", color: Brand.accent, meaning: "A change · opens the lines that changed"),
         Kind(tick: "✓", color: Brand.success, meaning: "Tests and commands · opens the report, failures first"),
         Kind(tick: "◫", color: Brand.search, meaning: "A page Mason checked · opens the page"),
         Kind(tick: "•", color: Brand.muted, meaning: "Reading and searching · a quiet line")]
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 14) {
                PoseImage(pose: running ? .planning : .readingFiles, height: 132,
                          meaning: running ? "planning the first step" : "waiting to read the project")
                Text(running ? "Working out the first step" : "Moments show up here")
                    .font(.title2).fontWeight(.semibold).foregroundStyle(Brand.ink)
                    .multilineTextAlignment(.center)
                Text(running ? "The first thing Mason reads, changes or runs appears here as a moment."
                             : "Each thing Mason does becomes a moment, newest first. Open one to see the file, change, test report or page it made.")
                    .foregroundStyle(Brand.muted)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                    ForEach(kinds, id: \.meaning) { kind in
                        GridRow {
                            Text(kind.tick)
                                .font(.system(size: 11, design: .monospaced))
                                .foregroundStyle(kind.color)
                                .frame(minWidth: 12)
                                .padding(.horizontal, 7).padding(.vertical, 2)
                                .background(kind.color.opacity(0.14), in: RoundedRectangle(cornerRadius: 4))
                                .accessibilityHidden(true)
                            Text(kind.meaning).font(.caption).foregroundStyle(Brand.muted)
                        }
                    }
                }
                .padding(.top, 10)
                .accessibilityElement(children: .contain)
                .accessibilityLabel("The kinds of moment")
            }
            .frame(maxWidth: 440)
            .padding(24).padding(.top, 24)
            .frame(maxWidth: .infinity)
        }
    }
}

// MARK: An opened moment

/// A moment opened from the feed: its title, how much has happened since, Raw, and the scene's lens.
struct MomentDetail: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    let calls: [Activity]

    /// What the opened moment is called on the shelf and above it, or nil when nothing is open or it is gone.
    static func title(pick: String?, session: Session, calls: [Activity], scenes: [ActivityScene], running: Bool) -> String? {
        guard let pick else { return nil }
        if pick == ActivityScenes.endKey {
            return !running && session.ending != nil ? "Summary · " + (session.ending?.style?.headline ?? "Run ended") : nil
        }
        guard let found = Moments.opened(pick, activities: calls, scenes: scenes) else { return nil }
        if let path = found.path { return (path as NSString).lastPathComponent }
        return ActivityScenes.label(found.scene, calls)
    }

    var body: some View {
        let scenes = ActivityScenes.group(calls)
        let isEnd = model.activityPick == ActivityScenes.endKey && !model.running && session.ending != nil
        let found = isEnd ? nil : Moments.opened(model.activityPick, activities: calls, scenes: scenes)
        if !isEnd, found == nil {
            VStack(alignment: .leading, spacing: 10) {
                Text("Nothing is open").font(.title3).foregroundStyle(Brand.ink)
                Text("Open a moment from the feed to see its file, change, report or page here.")
                    .foregroundStyle(Brand.muted).fixedSize(horizontal: false, vertical: true)
                Button("Back to Moments") { model.activityPick = nil; model.panel = "moments" }
            }
            .padding(16)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        } else {
            VStack(spacing: 0) {
                HStack(spacing: 8) {
                    Text(Self.title(pick: model.activityPick, session: session, calls: calls, scenes: scenes, running: model.running) ?? "")
                        .fontWeight(.semibold).foregroundStyle(Brand.ink)
                        .lineLimit(1).truncationMode(.tail)
                    Spacer(minLength: 0)
                    if let found {
                        let newer = Moments.newer(than: found.scene, in: scenes)
                        if newer > 0 {
                            Button(newer == 1 ? "1 new moment" : "\(newer) new moments") { model.panel = "moments" }
                                .controlSize(.small)
                                .help("Back to the feed, where the newer moments are")
                        } else if model.running {
                            ScenePill(text: "● Live", color: Brand.accent)
                        }
                        Toggle("Raw", isOn: $model.activityRaw)
                            .toggleStyle(.button).controlSize(.small)
                            .help("Show each call's subject and result exactly as Mason recorded them")
                    }
                }
                .padding(.horizontal, 14).padding(.vertical, 8)
                Divider()
                ScrollView {
                    Group {
                        if isEnd {
                            ReceiptLens(session: session)
                        } else if let found {
                            lens(scene: found.scene, path: found.path)
                        }
                    }
                    .padding(14)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .id(model.activityPick ?? "")
            }
        }
    }

    @ViewBuilder
    func lens(scene: ActivityScene, path: String?) -> some View {
        let steps = scene.indices.filter { $0 < calls.count }.map { calls[$0] }
        VStack(alignment: .leading, spacing: 14) {
            if model.activityRaw {
                ForEach(steps) { RawCallCard(activity: $0) }
            } else {
                switch scene.kind {
                case .edit:
                    let mine = path.map { only in steps.filter { ActivityScenes.path(of: $0)?.lowercased() == only.lowercased() } } ?? steps
                    EditLens(session: session, calls: mine.isEmpty ? steps : mine)
                case .command: CommandLens(calls: steps)
                case .read: ReadLens(calls: steps)
                case .page: PageLens(calls: steps)
                case .other: VStack(alignment: .leading, spacing: 4) { ForEach(steps) { ActivityRow(activity: $0) } }
                }
                if scene.kind != .other { StepDetails(calls: steps) }
            }
        }
    }
}

/// The calls of a scene as the activity rows show them, folded away under the lens.
struct StepDetails: View {
    let calls: [Activity]
    @State private var open = false

    var body: some View {
        DisclosureGroup(isExpanded: $open) {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(calls) { ActivityRow(activity: $0) }
            }
            .padding(.top, 4)
        } label: {
            Text(calls.count == 1 ? "Step details" : "Step details (\(calls.count))").font(.callout).fontWeight(.medium)
        }
    }
}

/// One call exactly as Mason recorded it.
struct RawCallCard: View {
    let activity: Activity

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(activity.tool).font(.system(.callout, design: .monospaced)).fontWeight(.semibold)
                Spacer(minLength: 0)
                ScenePill(text: callStateWords(activity.state), color: Brand.callState(activity.state))
            }
            Text(activity.subject.isEmpty ? "No subject" : activity.subject)
                .font(.system(.caption, design: .monospaced)).foregroundStyle(Brand.muted)
                .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            Text(activity.result.isEmpty ? "No output." : activity.result)
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }
}

// MARK: Lenses

/// Edits: each file's diff against the session's original, with what the edit did and the way to the file.
struct EditLens: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    let calls: [Activity]

    var paths: [String] {
        var seen: Set<String> = [], ordered: [String] = []
        for call in calls { if let path = ActivityScenes.path(of: call), seen.insert(path.lowercased()).inserted { ordered.append(path) } }
        return ordered
    }

    var body: some View {
        let changes = ActivityScenes.sessionChanges(session)
        VStack(alignment: .leading, spacing: 16) {
            ForEach(paths, id: \.self) { path in
                let mine = calls.filter { ActivityScenes.path(of: $0)?.lowercased() == path.lowercased() }
                let change = ActivityScenes.change(for: path, in: changes)
                VStack(alignment: .leading, spacing: 6) {
                    HStack(spacing: 6) {
                        if change == nil { Text(path).fontWeight(.semibold).lineLimit(1).truncationMode(.middle) }
                        if let failure = mine.last(where: { ActivityScenes.failed($0) }) {
                            ScenePill(text: callStateWords(failure.state), color: Brand.callState(failure.state))
                        } else if mine.contains(where: { $0.state == .running }) {
                            ScenePill(text: "Writing…", color: Brand.accent)
                        }
                        Spacer(minLength: 0)
                        Button("Open file") {
                            model.fileLine = 1
                            model.fileShown = change?.path ?? path
                            model.panel = "files"
                        }
                        .buttonStyle(.plain).foregroundStyle(Brand.accent).font(.callout)
                        .help("Show \(path) in the Files panel")
                    }
                    if let change {
                        FileDiff(change: change)
                        Text(change.created ? "The whole file, as this session wrote it." : "Everything this session has changed in the file so far, against its original.")
                            .font(.caption).foregroundStyle(Brand.muted)
                    } else if let last = mine.last, !last.result.isEmpty {
                        Text(last.result.count > 800 ? String(last.result.prefix(800)) + "…" : last.result)
                            .font(.callout).foregroundStyle(Brand.muted)
                            .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            ForEach(calls.filter { ActivityScenes.path(of: $0) == nil }) { ActivityRow(activity: $0) }
            if !changes.isEmpty {
                Button("Review all changes") { model.panel = "changes" }
                    .buttonStyle(.plain).foregroundStyle(Brand.accent).fontWeight(.semibold)
            }
        }
    }
}

/// Commands: the last few, each summary first: exit code, test counts, failing lines, then the output folded away.
struct CommandLens: View {
    let calls: [Activity]
    let shown = 6

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if calls.count > shown {
                Text("\(calls.count - shown) earlier commands are under Step details.").font(.caption).foregroundStyle(Brand.muted)
            }
            ForEach(Array(calls.suffix(shown))) { call in
                CommandCard(call: call).id(call.id)
            }
        }
    }
}

struct CommandCard: View {
    let call: Activity
    let summary: CommandSummary
    @State private var expanded: Bool

    init(call: Activity) {
        self.call = call
        let read = ActivityScenes.command(call)
        summary = read
        // The output opens by itself only when the command failed and nothing in it named the failure.
        _expanded = State(initialValue: read.failures.isEmpty && ((read.exitCode ?? 0) != 0 || call.state == .failed || call.state == .running))
    }

    var body: some View {
        let output = summary.output.trimmingCharacters(in: .whitespacesAndNewlines)
        let lines = output.split(separator: "\n", omittingEmptySubsequences: false)
        VStack(alignment: .leading, spacing: 8) {
            Text(call.subject.isEmpty ? Activity.label(call.tool) : String(call.subject.prefix(160)))
                .font(.system(.callout, design: .monospaced))
                .textSelection(.enabled).lineLimit(4)
                .padding(.horizontal, 8).padding(.vertical, 4)
                .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 6))
            HStack(spacing: 6) {
                if let exit = summary.exitCode {
                    ScenePill(text: "exit \(exit)", color: exit == 0 ? Brand.success : Brand.critical)
                } else {
                    ScenePill(text: callStateWords(call.state), color: Brand.callState(call.state))
                }
                if let passed = summary.passed { ScenePill(text: "\(passed) passed", color: Brand.success) }
                if let failed = summary.failed, failed > 0 { ScenePill(text: "\(failed) failed", color: Brand.critical) }
                if summary.truncated { ScenePill(text: "output clipped", color: Brand.caution) }
            }
            ForEach(summary.failures, id: \.self) { failure in
                Text(failure)
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    .padding(.leading, 11)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .overlay(alignment: .leading) { Rectangle().fill(Brand.critical).frame(width: 3).accessibilityHidden(true) }
            }
            if !output.isEmpty {
                DisclosureGroup(isExpanded: $expanded) {
                    ScrollView(.horizontal) {
                        Text(lines.count > 400 ? "[earlier output omitted · all of it is under Raw]\n" + lines.suffix(400).joined(separator: "\n") : output)
                            .font(.system(.caption, design: .monospaced))
                            .textSelection(.enabled)
                            .padding(8)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
                } label: {
                    Text(lines.count == 1 ? "Output · 1 line" : "Output · \(lines.count) lines").font(.callout)
                }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }
}

/// Reads and searches: how many, where Mason looked, and each one with the first lines it found.
struct ReadLens: View {
    let calls: [Activity]

    var body: some View {
        let files = Set(calls.filter { !ActivityScenes.isSearch($0.tool) }.compactMap { ActivityScenes.path(of: $0)?.lowercased() }).count
        let searches = calls.filter { ActivityScenes.isSearch($0.tool) }.count
        let failed = calls.filter { ActivityScenes.failed($0) }.count
        let folders = Array(ActivityScenes.folders(calls).prefix(8))
        let most = max(1, folders.map(\.count).max() ?? 1)
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 6) {
                if files > 0 { ScenePill(text: files == 1 ? "1 file" : "\(files) files") }
                if searches > 0 { ScenePill(text: searches == 1 ? "1 search" : "\(searches) searches") }
                if failed > 0 { ScenePill(text: "\(failed) failed", color: Brand.critical) }
            }
            if folders.count > 1 {
                Text("Where Mason looked").font(.headline).foregroundStyle(Brand.ink)
                Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 4) {
                    ForEach(folders, id: \.folder) { folder in
                        GridRow {
                            Text(folder.folder)
                                .font(.system(.caption, design: .monospaced))
                                .lineLimit(1).truncationMode(.middle)
                                .frame(width: 140, alignment: .leading)
                            Capsule().fill(Brand.accent)
                                .frame(width: max(8, 180 * CGFloat(folder.count) / CGFloat(most)), height: 8)
                                .accessibilityLabel("\(folder.folder): \(folder.count)")
                            Text("\(folder.count)").font(.caption).foregroundStyle(Brand.muted)
                        }
                    }
                }
            }
            VStack(alignment: .leading, spacing: 6) {
                ForEach(calls) { call in ReadLine(call: call) }
            }
        }
    }
}

struct ReadLine: View {
    let call: Activity

    var body: some View {
        let found: [String] = call.result.split(separator: "\n").map(String.init).filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        let hits: [String] = ActivityScenes.isSearch(call.tool) && call.state == .completed ? found : []
        VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                Text(Activity.label(call.tool)).font(.caption).foregroundStyle(Brand.muted).frame(width: 100, alignment: .leading)
                Text(call.subject.count > 140 ? String(call.subject.prefix(139)) + "…" : call.subject)
                    .font(.system(.caption, design: .monospaced)).lineLimit(2).textSelection(.enabled)
                Spacer(minLength: 0)
                if call.state != .completed { ScenePill(text: callStateWords(call.state), color: Brand.callState(call.state)) }
            }
            if !hits.isEmpty {
                Text(hits.prefix(5).joined(separator: "\n") + (hits.count > 5 ? "\n… \(hits.count - 5) more" : ""))
                    .font(.system(size: 11, design: .monospaced)).foregroundStyle(Brand.muted)
                    .lineLimit(6).textSelection(.enabled)
                    .padding(.leading, 110)
            }
        }
    }
}

/// Page and window checks: what was checked and what came back, with the way to the page.
struct PageLens: View {
    @EnvironmentObject var model: AppModel
    let calls: [Activity]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(calls) { call in
                let result = call.result.trimmingCharacters(in: .whitespacesAndNewlines)
                VStack(alignment: .leading, spacing: 8) {
                    HStack(spacing: 6) {
                        Text(Activity.label(call.tool)).fontWeight(.semibold)
                        ScenePill(text: callStateWords(call.state), color: Brand.callState(call.state))
                        Spacer(minLength: 0)
                        if call.tool == "check_page" || call.tool == "open_preview", !call.subject.isEmpty {
                            Button("Open page") { model.pageShown = call.subject; model.panel = "website" }
                                .buttonStyle(.plain).foregroundStyle(Brand.accent).font(.callout)
                                .help("Show \(call.subject) in the Website panel")
                        }
                    }
                    if !call.subject.isEmpty {
                        Text(String(call.subject.prefix(160)))
                            .font(.system(.callout, design: .monospaced)).lineLimit(2)
                            .padding(.horizontal, 8).padding(.vertical, 4)
                            .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 6))
                    }
                    if !result.isEmpty {
                        Text(result.count > 2400 ? String(result.prefix(2400)) + "…" : result)
                            .font(.system(.caption, design: .monospaced))
                            .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .padding(12)
                .frame(maxWidth: .infinity, alignment: .leading)
                .card()
            }
        }
    }
}


/// When the run ended: how it ended, what the request did, and the way to review and ship it.
struct ReceiptLens: View {
    @EnvironmentObject var model: AppModel
    let session: Session

    var body: some View {
        let receipt = ActivityScenes.receipt(session)
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 6) {
                if let style = session.ending?.style { ScenePill(text: style.glyph + " " + style.headline, color: Brand.tone(style.tone)) }
                ScenePill(text: receipt.duration)
                ScenePill(text: receipt.steps == 1 ? "1 step" : "\(receipt.steps) steps")
            }
            Text(receipt.request)
                .font(.title3).fontWeight(.semibold).foregroundStyle(Brand.ink)
                .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
            HStack(spacing: 0) {
                tile("\(receipt.files.count)", receipt.files.count == 1 ? "file changed" : "files changed")
                Divider()
                tile("+\(receipt.added) −\(receipt.removed)", "lines")
                Divider()
                tile("\(receipt.commands)", receipt.commands == 1 ? "command run" : "commands run")
                Divider()
                tile("\(receipt.commandsFailed)", receipt.commandsFailed == 1 ? "command failed" : "commands failed")
            }
            .fixedSize(horizontal: false, vertical: true)
            .card()
            if !receipt.files.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(Array(receipt.files.prefix(40)), id: \.path) { file in
                        HStack(spacing: 6) {
                            Text(file.path).font(.system(.caption, design: .monospaced)).lineLimit(1).truncationMode(.middle)
                            if file.created { ScenePill(text: "new") } else if file.deleted { ScenePill(text: "deleted") }
                            ScenePill(text: "+\(file.added)", color: Brand.success)
                            if file.removed > 0 { ScenePill(text: "−\(file.removed)", color: Brand.critical) }
                            Spacer(minLength: 0)
                            if !file.deleted {
                                Button("Open") { model.fileLine = 1; model.fileShown = file.path; model.panel = "files" }
                                    .buttonStyle(.plain).foregroundStyle(Brand.accent).font(.callout)
                                    .help("Show \(file.path) in the Files panel")
                            }
                        }
                    }
                    if receipt.files.count > 40 {
                        Text("\(receipt.files.count - 40) more in Changes").font(.caption).foregroundStyle(Brand.muted)
                    }
                }
            }
            ForEach(receipt.notes, id: \.self) { note in
                Text(note).foregroundStyle(Brand.muted).fixedSize(horizontal: false, vertical: true)
            }
            if !receipt.files.isEmpty {
                HStack(spacing: 10) {
                    Button("Review all changes") { model.panel = "changes" }
                    SettingsLink { Text("Commit & open pull request…") }
                        .help("Opens Settings, where the Project tab drafts and publishes the pull request")
                }
            }
        }
    }

    func tile(_ value: String, _ label: String) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(value).font(.title2).fontWeight(.semibold).monospacedDigit().foregroundStyle(Brand.ink).lineLimit(1).minimumScaleFactor(0.6)
            Text(label).font(.caption).foregroundStyle(Brand.muted).lineLimit(1)
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
