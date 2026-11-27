import SwiftUI
import MasonCore

// The Live Viewer: the Activity panel shown as scenes. Consecutive calls of one kind form a scene, and each kind has its
// own lens: a diff for edits, a summary-first terminal for commands, a map for reads, the page for browser checks, the
// plan, and a receipt when the run ends. The viewer follows the newest scene until the user picks another one.
// Every fact the activity rows show (state, subject, result, skipped repeats) is still under Step details or Raw.

extension AppModel {
    /// Opens the Activity panel on the step holding a call, or following the run when there is none.
    func showActivity(_ call: UUID?) {
        activityPick = call?.uuidString
        panel = "activity"
        showChanges = true
    }
}

extension Brand {
    /// The colour of a step in the strip, from the shared status colours.
    static func sceneTone(_ tone: SceneTone) -> Color {
        switch tone {
        case .neutral: return muted
        case .accent: return accent
        case .good: return success
        case .search: return search
        case .bad: return critical
        case .warn: return caution
        }
    }

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

struct LiveViewer: View {
    @EnvironmentObject var model: AppModel
    let session: Session

    /// The session's calls, with the ones the running request has reported so far.
    var allCalls: [Activity] { model.running ? session.activities + model.liveActivities : session.activities }

    var body: some View {
        let calls = allCalls
        let running = model.running
        let scenes = ActivityScenes.group(calls)
        let hasPlan = !(session.milestones ?? []).isEmpty
        let hasEnd = !running && session.ending != nil && !calls.isEmpty
        let shown = ActivityScenes.selection(pick: model.activityPick, activities: calls, scenes: scenes, running: running, hasPlan: hasPlan, hasEnd: hasEnd)
        let scene = scenes.first { $0.key == shown.key }
        VStack(spacing: 0) {
            header(key: shown.key, following: shown.following, scene: scene, calls: calls, running: running)
            Divider()
            ScrollView {
                lens(key: shown.key, scene: scene, calls: calls)
                    .padding(14)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .id(shown.key)
            if hasPlan || !scenes.isEmpty {
                Divider()
                strip(selected: shown.key, scenes: scenes, calls: calls, running: running, hasPlan: hasPlan, hasEnd: hasEnd)
            }
        }
    }

    // MARK: Header

    func header(key: String, following: Bool, scene: ActivityScene?, calls: [Activity], running: Bool) -> some View {
        HStack(spacing: 10) {
            if following {
                ScenePill(text: running ? "● Following" : key == ActivityScenes.endKey ? "Run ended" : "Latest", color: running ? Brand.accent : Brand.muted)
                    .accessibilityLabel(running ? "Following the run as it happens" : "Showing the latest step")
            } else {
                Button("Jump to latest") { model.activityPick = nil }
                    .controlSize(.small)
                    .help("Jump to the latest step and follow the run")
            }
            Text(title(key: key, scene: scene, calls: calls))
                .fontWeight(.semibold).foregroundStyle(Brand.ink)
                .lineLimit(1).truncationMode(.tail)
            Spacer(minLength: 0)
            Toggle("Raw", isOn: $model.activityRaw)
                .toggleStyle(.button).controlSize(.small)
                .help("Show each call's subject and result exactly as Mason recorded them")
            Menu {
                Button("Changes") { model.panel = "changes" }
                Button("Plan") { model.panel = "plan" }
                Button("Files") { model.panel = "files" }
                Button("Website") { model.panel = "website" }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
            .help("More pages about this run")
            .accessibilityLabel("More pages about this run")
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
    }

    func title(key: String, scene: ActivityScene?, calls: [Activity]) -> String {
        switch key {
        case ActivityScenes.planKey: return "Plan"
        case ActivityScenes.endKey: return session.ending?.style?.headline ?? "Run ended"
        case "": return "Live"
        default:
            guard let scene else { return "" }
            let place = scene.count > 1 ? "steps \(scene.first + 1)–\(scene.last + 1) of \(calls.count)" : "step \(scene.first + 1) of \(calls.count)"
            return ActivityScenes.label(scene, calls) + " · " + place
        }
    }

    // MARK: Lens

    @ViewBuilder
    func lens(key: String, scene: ActivityScene?, calls: [Activity]) -> some View {
        if key.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                Text("Nothing has run yet").font(.title3).foregroundStyle(Brand.ink)
                Text("When Mason reads, edits, runs or checks something, it shows here as it happens.")
                    .foregroundStyle(Brand.muted).fixedSize(horizontal: false, vertical: true)
            }
        } else if key == ActivityScenes.planKey {
            PlanLens(session: session)
        } else if key == ActivityScenes.endKey {
            ReceiptLens(session: session)
        } else if let scene {
            let steps = scene.indices.filter { $0 < calls.count }.map { calls[$0] }
            VStack(alignment: .leading, spacing: 14) {
                if model.activityRaw {
                    ForEach(steps) { RawCallCard(activity: $0) }
                } else {
                    switch scene.kind {
                    case .edit: EditLens(session: session, calls: steps)
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

    // MARK: Step strip

    func strip(selected: String, scenes: [ActivityScene], calls: [Activity], running: Bool, hasPlan: Bool, hasEnd: Bool) -> some View {
        let limit = 150
        let recent = Array(scenes.suffix(limit))
        let owner = self.model
        let choose: (String) -> Void = { key in
            owner.activityPick = ActivityScenes.pick(key, activities: calls, scenes: scenes, running: running, hasPlan: hasPlan, hasEnd: hasEnd)
        }
        return ScrollViewReader { proxy in
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 4) {
                    if hasPlan {
                        let count = session.milestones?.count ?? 0
                        StepTick(text: "plan", tone: .neutral, tip: "Plan · \(count) step\(count == 1 ? "" : "s")", selected: selected == ActivityScenes.planKey) { choose(ActivityScenes.planKey) }
                            .id(ActivityScenes.planKey)
                    }
                    if scenes.count > limit {
                        Text("\(scenes.count - limit) earlier").font(.caption2).foregroundStyle(Brand.muted)
                    }
                    ForEach(recent, id: \.first) { item in
                        let tick = ActivityScenes.tick(item, calls)
                        let skipped = item.indices.contains { $0 < calls.count && calls[$0].state == .skipped }
                        let outcome = tick.tone == .bad ? " · failed" : skipped ? " · repeat skipped" : ""
                        StepTick(text: tick.text, tone: tick.tone, tip: ActivityScenes.label(item, calls) + outcome, selected: selected == item.key) { choose(item.key) }
                            .id(item.key)
                    }
                    if hasEnd, let ending = session.ending {
                        StepTick(text: "end", tone: ActivityScenes.tone(ending), tip: ending.style?.headline ?? "Run ended", selected: selected == ActivityScenes.endKey) { choose(ActivityScenes.endKey) }
                            .id(ActivityScenes.endKey)
                    }
                }
                .padding(.horizontal, 12).padding(.vertical, 8)
            }
            .accessibilityLabel("Steps of this run")
            .onAppear { proxy.scrollTo(selected) }
            .onChange(of: selected) { _, key in proxy.scrollTo(key) }
            .onChange(of: scenes.count) { _, _ in proxy.scrollTo(selected) }
        }
    }
}

/// One step of the strip, tinted by kind and outcome; the one shown is outlined.
struct StepTick: View {
    let text: String
    let tone: SceneTone
    let tip: String
    let selected: Bool
    let action: () -> Void

    var body: some View {
        let color = Brand.sceneTone(tone)
        Button(action: action) {
            Text(text)
                .font(.system(size: 11, design: .monospaced))
                .foregroundStyle(color)
                .padding(.horizontal, 7).padding(.vertical, 3)
                .frame(minWidth: 28)
                .background(color.opacity(0.14), in: RoundedRectangle(cornerRadius: 4))
                .overlay(RoundedRectangle(cornerRadius: 4).stroke(selected ? Brand.accent : color.opacity(0.35), lineWidth: selected ? 2 : 1))
        }
        .buttonStyle(.plain)
        .help(tip)
        .accessibilityLabel(tip + (selected ? ", shown" : ""))
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

/// The saved plan's milestones and where each stands.
struct PlanLens: View {
    @EnvironmentObject var model: AppModel
    let session: Session

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Plan").font(.title3).fontWeight(.semibold).foregroundStyle(Brand.ink)
            ForEach(model.milestoneStatus(session), id: \.item.id) { row in
                HStack(alignment: .top, spacing: 10) {
                    Image(systemName: row.done ? "checkmark.circle.fill" : "circle")
                        .foregroundStyle(row.done ? Brand.success : Brand.muted)
                        .accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(row.item.title).fixedSize(horizontal: false, vertical: true)
                        if !row.reason.isEmpty {
                            Text(row.reason).font(.caption).foregroundStyle(Brand.muted).fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    Spacer(minLength: 0)
                    ScenePill(text: row.state.rawValue, color: row.done ? Brand.success : row.state == .blocked || row.state == .recheck ? Brand.caution : Brand.muted)
                }
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .card()
            }
            Button("Open the plan") { model.panel = "plan" }
                .buttonStyle(.plain).foregroundStyle(Brand.accent).fontWeight(.semibold)
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
