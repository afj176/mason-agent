import SwiftUI
import MasonCore

/// One session: the conversation, the composer, and optionally the files it changed.
struct SessionView: View {
    @EnvironmentObject var model: AppModel
    @Binding var showChanges: Bool

    var body: some View {
        if let session = model.session {
            HSplitView {
                VStack(spacing: 0) {
                    Transcript(session: session)
                    // The approval card has its own pose, so the status pose steps aside while it shows.
                    if let pending = model.approval { ApprovalCard(pending: pending) } else { StatusPose(session: session) }
                    Composer(session: session)
                }
                .frame(minWidth: 420)
                if showChanges { ChangesView(session: session).frame(minWidth: 380, idealWidth: 480) }
            }
            .navigationTitle(session.title)
            .navigationSubtitle(URL(fileURLWithPath: session.workspace).lastPathComponent)
        } else {
            WelcomeView()
        }
    }
}

struct Transcript: View {
    @EnvironmentObject var model: AppModel
    let session: Session

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 24) {
                    ForEach(Array(session.turns.enumerated()), id: \.element.id) { index, turn in
                        TurnView(turn: turn, activities: activities(for: index), live: model.running && index == session.turns.count - 1)
                    }
                    if session.turns.isEmpty {
                        VStack(spacing: 14) {
                            PoseImage(pose: .listening, height: 104)
                            Text("Ask a question about this project, or describe a change.")
                                .foregroundStyle(.secondary)
                        }
                        .frame(maxWidth: .infinity, alignment: .center)
                        .padding(.top, 56)
                    }
                    // An ending with a card under its turn (a loop, a limit, the model server) says it there instead.
                    if !model.running, !session.detail.isEmpty, !RunEnding.carded.contains(session.ending?.code ?? "") {
                        // A failed request's pose is in the status above the composer.
                        Label(session.detail, systemImage: session.state == "Failed" ? "xmark.octagon" : "info.circle")
                            .foregroundStyle(session.state == "Failed" ? Brand.critical : Color.secondary)
                            .font(.callout)
                    }
                    Color.clear.frame(height: 1).id("end")
                }
                .padding(24)
                .frame(maxWidth: 860, alignment: .leading)
                .frame(maxWidth: .infinity)
            }
            .onChange(of: model.liveText) { proxy.scrollTo("end", anchor: .bottom) }
            .onChange(of: model.liveActivities.count) { proxy.scrollTo("end", anchor: .bottom) }
            .onChange(of: session.turns.count) { proxy.scrollTo("end", anchor: .bottom) }
            .onAppear { proxy.scrollTo("end", anchor: .bottom) }
        }
    }

    /// Activities recorded for a finished turn; for the turn in progress, the ones reported live.
    func activities(for index: Int) -> [Activity] {
        if model.running, index == session.turns.count - 1 { return model.liveActivities }
        let start = session.turns[index].firstActivity
        let end = index + 1 < session.turns.count ? session.turns[index + 1].firstActivity : session.activities.count
        guard start <= end, end <= session.activities.count else { return [] }
        return Array(session.activities[start..<end])
    }
}

struct TurnView: View {
    @EnvironmentObject var model: AppModel
    let turn: Turn
    let activities: [Activity]
    let live: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Speaker(mason: false)
            Text(turn.request).textSelection(.enabled).messageBody()
                .contextMenu {
                    Button("Fork From Before This Request") { model.forkSession(before: turn, restoreFiles: false) }
                    Button("Fork and Put the Files Back as They Were") { model.forkSession(before: turn, restoreFiles: true) }
                }

            if !activities.isEmpty || live || !turn.answer.isEmpty || !turn.changes.isEmpty {
                Speaker(mason: true).padding(.top, 4)
                VStack(alignment: .leading, spacing: 10) {
                    if !activities.isEmpty {
                        VStack(alignment: .leading, spacing: 4) {
                            ForEach(activities) { ActivityRow(activity: $0) }
                        }
                        .padding(.horizontal, 10).padding(.vertical, 6)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .card()
                    }
                    if live {
                        HStack(spacing: 8) {
                            ProgressView().controlSize(.small)
                            Text(model.liveText.isEmpty ? model.liveStatus : String(model.liveText.suffix(400)))
                                .foregroundStyle(.secondary).lineLimit(3)
                        }
                    } else if !turn.answer.isEmpty {
                        MarkdownText(text: turn.answer)
                    }
                    if !live, let receipt = turn.checks {
                        // What Mason found when it ran the checks it worked out from the request.
                        let ran = receipt.results.contains { $0.state != "notRun" }
                        HStack(spacing: 10) {
                            if ran { PoseImage(pose: receipt.passed ? .runningChecks : .checkFailed, height: 40, meaning: receipt.passed ? "checks passed" : "a check failed") }
                            Label(receipt.summary, systemImage: !ran ? "minus.circle" : receipt.passed ? "checkmark.seal.fill" : "xmark.seal.fill")
                                .font(.callout)
                                .foregroundStyle(!ran ? Brand.caution : receipt.passed ? Brand.success : Brand.critical)
                                .help(receipt.report)
                        }
                    }
                    if !live, let receipt = turn.verification {
                        // What Mason found when it ran the project's verify workflow itself.
                        let good = receipt.verdict == .passed || receipt.verdict == .fixed
                        let quiet = receipt.verdict == .notRun || receipt.verdict == .failingBefore
                        HStack(spacing: 10) {
                            if !quiet { PoseImage(pose: good ? .runningChecks : .checkFailed, height: 40, meaning: good ? "checks passed" : "a check failed") }
                            Label("Verified by Mason: \(receipt.summary)" + (receipt.repairs > 0 ? " · after \(receipt.repairs) repair\(receipt.repairs == 1 ? "" : "s")" : ""),
                                  systemImage: good ? "checkmark.seal.fill" : quiet ? "minus.circle" : "xmark.seal.fill")
                                .font(.callout)
                                .foregroundStyle(good ? Brand.success : quiet ? Brand.caution : Brand.critical)
                                .help(receipt.command + (receipt.output.isEmpty ? "" : "\n\n" + String(receipt.output.suffix(600))))
                        }
                    }
                    if !live, let suggestion = model.session?.suggestedNote, suggestion.turn == turn.id {
                        // A note the model suggested in this request, for the user to remember or dismiss.
                        NoteSuggestionCard(suggestion: suggestion)
                    }
                    if !live, !activities.isEmpty || !turn.changes.isEmpty {
                        // What the request amounted to, in one line, and the way to its changes.
                        HStack(spacing: 10) {
                            Text(summary).font(.callout).foregroundStyle(Brand.muted)
                            if !turn.changes.isEmpty {
                                Button("\(turn.changes.count) changed file\(turn.changes.count == 1 ? "" : "s")") { model.panel = "changes"; model.showChanges = true }
                                    .buttonStyle(.plain).fontWeight(.semibold).foregroundStyle(Brand.accent)
                                    .help("Show what changed")
                            }
                            if turn.undone { Text("Undone").font(.caption).foregroundStyle(Brand.caution) }
                            if let first = activities.first {
                                Button("View moments") { model.showActivity(first.id) }
                                    .buttonStyle(.plain).foregroundStyle(Brand.accent)
                                    .help("Open this request's first step in Moments")
                            }
                        }
                    }
                    if !live, let session = model.session, let ending = session.ending, RunEnding.carded.contains(ending.code),
                       let style = ending.style, session.turns.last?.id == turn.id {
                        // The request ended on a loop, a limit, a block or the model server: said at the end of its turn.
                        EndingCard(session: session, ending: ending, style: style)
                    }
                }
                .messageBody()
            }
        }
    }

    /// "2 reads · 1 edit · 1 command", counting the steps that completed.
    var summary: String {
        var reads = 0, edits = 0, commands = 0, other = 0
        for activity in activities where activity.state == .completed {
            switch activity.tool {
            case "read_file", "read_lines", "list_files", "search_files", "search_next": reads += 1
            case "edit_file", "write_file", "apply_edits": edits += 1
            case "run_shell", "run_command", "run_workflow", "start_job", "start_terminal": commands += 1
            default: other += 1
            }
        }
        func count(_ number: Int, _ one: String, _ many: String) -> String? { number == 0 ? nil : "\(number) \(number == 1 ? one : many)" }
        // A call Mason skipped because the model repeated it is not a failed step; it is counted on its own.
        let repeats = activities.filter { $0.state == .skipped }.count
        let parts = [count(reads, "read", "reads"), count(edits, "edit", "edits"), count(commands, "command", "commands"), count(other, "other step", "other steps")].compactMap { $0 }
        let done = parts.isEmpty ? "No steps completed" : parts.joined(separator: " · ")
        return done + (repeats == 0 ? "" : " · \(repeats) repeat\(repeats == 1 ? "" : "s") skipped")
    }
}

/// At the end of a request that ended on a model loop, a stuck model, a limit, a project block or an unreachable
/// model server: a calm card with a bar in the ending's tone, what happened, what to do next and what was kept.
struct EndingCard: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    let ending: RunEnding
    let style: RunEnding.Style

    /// The explanation for a model loop, otherwise the session's detail.
    var message: String {
        if ending.code == "model_loop", let repeated = session.repeatStop { return repeated.explanation(kept: ending.kept > 0) }
        return session.detail
    }

    var body: some View {
        let color = Brand.tone(style.tone)
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: Brand.endingIcon(ending.code))
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(color)
                .frame(width: 22)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(style.headline).fontWeight(.semibold).foregroundStyle(Brand.ink)
                if !message.isEmpty {
                    Text(message).foregroundStyle(Brand.ink)
                        .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                }
                if let hint = RunEnding.hint(ending.code, terminal: false) {
                    Text(hint).font(.caption).foregroundStyle(Brand.muted).fixedSize(horizontal: false, vertical: true)
                }
                if ending.kept > 0 {
                    HStack(spacing: 6) {
                        Text(RunEnding.keptText(ending.kept)).foregroundStyle(Brand.muted)
                        Text("·").foregroundStyle(Brand.muted)
                        Button("Review changes") { model.panel = "changes"; model.showChanges = true }
                            .buttonStyle(.plain).fontWeight(.semibold).foregroundStyle(Brand.accent)
                            .help("Show what changed")
                    }
                    .font(.caption)
                }
            }
            Spacer(minLength: 0)
        }
        .font(.callout)
        .padding(12).padding(.leading, 4)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(color.opacity(0.08))
        // The tone bar down the leading edge, as tall as the card.
        .overlay(alignment: .leading) { Rectangle().fill(color).frame(width: 4).accessibilityHidden(true) }
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(color.opacity(0.30)))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(style.headline)
    }
}

struct ActivityRow: View {
    let activity: Activity
    @State private var expanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            Text(activity.result.isEmpty ? "No output." : activity.result)
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(8)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
        } label: {
            HStack(spacing: 6) {
                switch activity.state {
                case .running: ProgressView().controlSize(.mini)
                case .completed: Image(systemName: "checkmark.circle.fill").foregroundStyle(Brand.success)
                case .failed: Image(systemName: "xmark.circle.fill").foregroundStyle(Brand.critical)
                case .denied: Image(systemName: "nosign").foregroundStyle(Brand.caution)
                case .skipped: Image(systemName: "arrow.triangle.2.circlepath").foregroundStyle(Brand.caution)
                }
                Text(Activity.label(activity.tool)).fontWeight(.medium)
                Text(activity.subject).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                if activity.state == .denied { Text("declined").foregroundStyle(Brand.caution) }
                if activity.state == .skipped { Text("Skipped · repeated").foregroundStyle(Brand.caution) }
            }
            .font(.callout)
            .accessibilityElement(children: .combine)
        }
    }
}

/// Renders an answer: fenced code as monospaced blocks, everything else with inline Markdown.
struct MarkdownText: View {
    let text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ForEach(Array(blocks.enumerated()), id: \.offset) { _, block in
                if block.code {
                    ScrollView(.horizontal) {
                        Text(block.text).font(.system(.callout, design: .monospaced)).textSelection(.enabled).padding(10)
                    }
                    .background(.quaternary.opacity(0.6), in: RoundedRectangle(cornerRadius: 8))
                } else {
                    Text((try? AttributedString(markdown: block.text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(block.text))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
    }

    var blocks: [(code: Bool, text: String)] {
        var result: [(Bool, String)] = []
        var current: [String] = [], inCode = false
        func flush() {
            let joined = current.joined(separator: "\n").trimmingCharacters(in: .newlines)
            if !joined.isEmpty { result.append((inCode, joined)) }
            current = []
        }
        for line in text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            if line.trimmingCharacters(in: .whitespaces).hasPrefix("```") { flush(); inCode.toggle() } else { current.append(line) }
        }
        flush()
        return result.map { (code: $0.0, text: $0.1) }
    }
}

struct ApprovalCard: View {
    @EnvironmentObject var model: AppModel
    let pending: PendingApproval
    @State private var showRule = false
    @State private var prefix = ""

    var body: some View {
        let tool = pending.request.tool.replacingOccurrences(of: "_", with: " ")
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .center, spacing: 12) {
                PoseImage(pose: .awaitingApproval, height: 70)
                Label("Review \(tool)", systemImage: "hand.raised.fill").font(.headline)
            }
            Text(pending.request.title).font(.system(.body, design: .monospaced)).textSelection(.enabled).lineLimit(6)
            if !pending.request.detail.isEmpty {
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        ForEach(Array(pending.request.detail.components(separatedBy: "\n").prefix(200).enumerated()), id: \.offset) { _, line in
                            Text(line.isEmpty ? " " : line)
                                .font(.system(.caption, design: .monospaced))
                                .foregroundStyle(line.hasPrefix("+") ? Brand.success : line.hasPrefix("-") ? Brand.critical : Color.secondary)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                }
                .frame(maxHeight: 180)
            }
            HStack {
                Button("Allow Once") { model.answer(pending, allow: true) }.keyboardShortcut(.defaultAction)
                Button("Always Allow…") { prefix = ApprovalRules.suggestion(tool: pending.request.tool, subject: pending.request.subject); showRule = true }
                Spacer()
                Button("Decline", role: .cancel) { model.answer(pending, allow: false) }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(14)
        .background(Brand.caution.opacity(0.12), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Brand.caution.opacity(0.5)))
        .padding(.horizontal, 16).padding(.top, 8)
        .popover(isPresented: $showRule) {
            VStack(alignment: .leading, spacing: 10) {
                Text("Always allow \(tool)").font(.headline)
                TextField("Allow when it starts with", text: $prefix).font(.system(.body, design: .monospaced)).frame(width: 340)
                Text("Saved for this project on this Mac. Rules only skip this question; they never widen the sandbox.")
                    .font(.caption).foregroundStyle(.secondary).frame(width: 340, alignment: .leading)
                Button("Save Rule and Allow") { showRule = false; model.answer(pending, allow: true, alwaysPrefix: prefix) }
                    .disabled(prefix.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .padding(16)
        }
    }
}

struct Composer: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    @FocusState private var focused: Bool
    var nothingToSend: Bool { model.draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && (session.comments ?? []).isEmpty }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let comments = session.comments, !comments.isEmpty { CommentChips(comments: comments) }
            TextEditor(text: $model.draft)
                .font(.body)
                .focused($focused)
                .frame(height: 62)
                .scrollContentBackground(.hidden)
                .overlay(alignment: .topLeading) {
                    if model.draft.isEmpty {
                        Text("Describe a change, or ask about the project…").foregroundStyle(.tertiary).padding(.horizontal, 5).allowsHitTesting(false)
                    }
                }
                .accessibilityLabel("Request")
                // While an approval is waiting, Return answers "Allow Once" and Escape declines, even when the
                // request box still has the keyboard, instead of typing into the box.
                .onKeyPress(.return) {
                    guard let pending = model.approval else { return .ignored }
                    model.answer(pending, allow: true)
                    return .handled
                }
                .onKeyPress(.escape) {
                    if let pending = model.approval {
                        model.answer(pending, allow: false)
                        return .handled
                    }
                    // Escape also closes the Notes tray.
                    if model.notesOpen {
                        model.closeNotes()
                        return .handled
                    }
                    return .ignored
                }
            HStack {
                Picker("Permission", selection: Binding(get: { session.permission }, set: { model.setPermission($0) })) {
                    Text("Read-only").tag(Permission.readOnly)
                    Text("Review edits").tag(Permission.reviewEdits)
                    Text("Workspace edits").tag(Permission.workspaceEdits)
                    Text("Auto (sandboxed)").tag(Permission.auto)
                }
                .labelsHidden().fixedSize().disabled(model.running)
                .help("What Mason may do without asking. Auto runs commands in the macOS sandbox, limited to this project, with network access off.")
                Button { model.pane = .models } label: { Label(model.endpoint == nil ? "No model running" : model.endpointName, systemImage: "cpu").lineLimit(1) }
                    .buttonStyle(.plain).chip().fixedSize()
                    .help(model.endpoint == nil ? "No model is running. Open Models to start one." : "Running at \(model.endpoint?.url ?? "")")
                WindowMenu(session: session)
                SessionStatusChips(session: session)
                Spacer()
                // With Moments open the request box is narrow: the hint goes when it no longer fits, rather than wrapping a letter per line.
                if !model.running {
                    ViewThatFits(in: .horizontal) {
                        Text("⌘Return to send").font(.caption).foregroundStyle(Brand.muted).lineLimit(1).fixedSize()
                        Color.clear.frame(width: 0, height: 0)
                    }
                }
                if model.running {
                    if model.waitingFollowUps > 0 { Text("\(model.waitingFollowUps) waiting").font(.caption).foregroundStyle(.secondary) }
                    let empty = model.draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    Button { model.followUp(steer: true) } label: { Label("Steer", systemImage: "arrow.uturn.right") }
                        .disabled(empty || model.approval != nil)
                        .help("Interrupt what Mason is doing and handle this message next")
                    Button { model.followUp(steer: false) } label: { Label("Queue", systemImage: "text.badge.plus") }
                        .disabled(empty || model.approval != nil)
                        .help("Handle this message after the current request completes (⌘Return)")
                    Button(role: .cancel) { model.stop() } label: { Label("Stop", systemImage: "stop.fill") }
                } else {
                    if let waiting = session.pendingInput, !waiting.isEmpty {
                        Button { model.runQueued() } label: { Label("Run \(waiting.count) Queued", systemImage: "play") }
                            .help("Run the messages that were waiting when the last request stopped")
                    }
                    Button { model.send() } label: {
                        Image(systemName: "arrow.up").font(.system(size: 15, weight: .bold)).foregroundStyle(Brand.onAccent)
                            .frame(width: 34, height: 34).background(Brand.accent, in: Circle())
                    }
                    .buttonStyle(.plain)
                    .disabled(nothingToSend)
                    .opacity(nothingToSend ? 0.45 : 1)
                    .accessibilityLabel("Send")
                    .help("Send (⌘Return)")
                }
            }
        }
        .padding(12)
        .card(radius: 18)
        .shadow(color: .black.opacity(0.14), radius: 14, y: 6)
        // The Notes tab and tray sit behind the request box, reaching up from its top edge over the transcript.
        .background(alignment: .top) {
            NotesDrawer(session: session)
                .alignmentGuide(.top) { dimensions in dimensions[.bottom] - NotesDrawer.tuck }
        }
        .frame(maxWidth: 900)
        .frame(maxWidth: .infinity)
        .padding(.horizontal, 16).padding(.bottom, 14).padding(.top, 6)
        .onAppear { focused = true }
        .onChange(of: model.approval == nil) { _, answered in focused = answered }
        .onChange(of: model.notesOpen) { _, open in if !open { focused = true } }
    }
}

/// Beside the session's controls: "Pull request" while the project's delivery has an open pull request, and
/// "N background" while background tasks run for the project. Each opens Settings, where the Project tab has them.
struct SessionStatusChips: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    @State private var pullRequest: String?
    @State private var background = 0

    var home: URL { model.store?.folder.deletingLastPathComponent() ?? DataHome.url }

    var body: some View {
        HStack(spacing: 6) {
            if let pullRequest {
                SettingsLink { Label("Pull request", systemImage: "arrow.triangle.pull").lineLimit(1) }
                    .buttonStyle(.plain).chip()
                    .help("Opens Settings, where the Project tab shows the pull request (\(pullRequest)) and its checks")
            }
            if let label = SessionChips.backgroundLabel(background) {
                SettingsLink { Label(label, systemImage: "gearshape.2").lineLimit(1) }
                    .buttonStyle(.plain).chip()
                    .help("Opens Settings, where the Project tab lists this project's background tasks")
            }
        }
        .task(id: session.workspace) {
            // Both live in files other processes write, so they are read again every few seconds.
            while !Task.isCancelled {
                refresh()
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }

    func refresh() {
        guard let workspace = try? Workspace(path: session.workspace) else { pullRequest = nil; background = 0; return }
        pullRequest = SessionChips.pullRequest(Delivery.current(workspace, home: home))
        background = SessionChips.activeTasks(BackgroundTasks.all(workspace: workspace, home: home).map { $0.status })
    }
}

/// The side panel: one Moments-first surface. Its header carries "Moments" or the way back to it, and a shelf of the
/// panel's other views (Changes, Plan, Files, Website), the opened moment, and what the latest run made so far.
struct ChangesView: View {
    @EnvironmentObject var model: AppModel
    let session: Session

    var changes: [FileChange] { ActivityScenes.sessionChanges(session) }

    var body: some View {
        // The session's calls, with the ones the running request has reported so far.
        let calls = model.running ? session.activities + model.liveActivities : session.activities
        VStack(spacing: 0) {
            SurfaceHeader(session: session, calls: calls)
            Divider()
            if model.panel == "changes" {
                HStack {
                    Text("Changes").font(.headline).foregroundStyle(Brand.ink)
                    Spacer()
                    if model.reviewing { ProgressView().controlSize(.small) }
                    Button("Review") { model.reviewChanges() }.disabled(model.running || model.reviewing || changes.isEmpty)
                        .help("Have the model read these changes and pin what it finds as comments")
                    Button("Undo") { model.undo() }.disabled(model.running).help("Put back everything the latest request changed")
                    Button("Redo") { model.undo(redo: true) }.disabled(model.running)
                }
                .padding(.horizontal, 12).padding(.vertical, 8)
                Divider()
            }
            if model.panel == "moment" {
                MomentDetail(session: session, calls: calls)
            } else if model.panel == "plan" {
                MilestonesView(session: session)
            } else if model.panel == "files" {
                FilesPanel(session: session)
            } else if model.panel == "website" {
                WebsitePanel(state: model.website, session: session)
            } else if model.panel != "changes" {
                MomentsFeed(session: session, calls: calls)
            } else if changes.isEmpty {
                ContentUnavailableView("No changes", systemImage: "doc.text", description: Text("Files this session edits appear here."))
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 16) {
                        ForEach(changes, id: \.path) { FileDiff(change: $0) }
                        Text("Use the bubble beside a line to pin a comment to it. Comments go with your next request.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    .padding(12)
                }
            }
        }
    }
}

struct FileDiff: View {
    @EnvironmentObject var model: AppModel
    let change: FileChange
    /// The row whose comment is being written.
    @State private var commenting: Int?

    var body: some View {
        // A binary or very large file has no text to compare, so it is listed without lines.
        let rows = change.isOpaque ? [] : Diff.lines(before: change.before, after: change.deleted ? "" : change.after)
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(change.path).fontWeight(.medium)
                Text("+\(rows.filter { $0.kind == .added }.count)").foregroundStyle(Brand.success)
                Text("−\(rows.filter { $0.kind == .removed }.count)").foregroundStyle(Brand.critical)
                if change.deleted { Text("deleted").foregroundStyle(.secondary) } else if change.created { Text("new file").foregroundStyle(.secondary) }
                if change.isOpaque { Text("binary or large file").foregroundStyle(.secondary) }
                if change.byCommand == true { Text("by a command").foregroundStyle(.secondary) }
            }
            .font(.callout)
            ScrollView(.horizontal) {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(visible(rows).enumerated()), id: \.offset) { offset, row in
                        if let row {
                            HStack(spacing: 4) {
                                let place = "\(change.path) · \(row.kind == .removed ? "original" : "updated") line \(row.number)"
                                Button { commenting = offset } label: { Image(systemName: "plus.bubble").font(.caption2) }
                                    .buttonStyle(.plain).foregroundStyle(.secondary)
                                    .help("Comment on this line").accessibilityLabel("Comment on \(place)")
                                    .popover(isPresented: Binding(get: { commenting == offset }, set: { if !$0 { commenting = nil } }), arrowEdge: .leading) {
                                        CommentEditor(location: place) { model.addComment(path: change.path, row: row, note: $0) }
                                    }
                                Text("\(String(row.number).leftPadded(5)) \(String(row.kind.rawValue)) \(row.text)")
                                    .font(.system(.caption, design: .monospaced))
                                    .foregroundStyle(row.kind == .added ? Brand.success : row.kind == .removed ? Brand.critical : Color.primary)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .background(row.kind == .added ? Brand.success.opacity(0.10) : row.kind == .removed ? Brand.critical.opacity(0.10) : Color.clear)
                        } else {
                            Text("      ⋯").font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary)
                        }
                    }
                }
                .textSelection(.enabled)
            }
            .padding(8)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
        }
    }

    /// Changed lines with two lines of context; nil marks a gap. Capped so a huge file stays responsive.
    func visible(_ rows: [DiffLine]) -> [DiffLine?] {
        var keep = [Bool](repeating: false, count: rows.count)
        for (index, row) in rows.enumerated() where row.kind != .same {
            for near in max(0, index - 2)...min(rows.count - 1, index + 2) { keep[near] = true }
        }
        var result: [DiffLine?] = [], gap = false
        for (index, row) in rows.enumerated() where result.count < 600 {
            if !keep[index] { gap = true; continue }
            if gap, !result.isEmpty { result.append(nil) }
            gap = false
            result.append(row)
        }
        return result
    }
}

extension String {
    func leftPadded(_ width: Int) -> String { count >= width ? self : String(repeating: " ", count: width - count) + self }
}
