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
                    if let pending = model.approval { ApprovalCard(pending: pending) }
                    Composer(session: session)
                }
                .frame(minWidth: 420)
                if showChanges { ChangesView(session: session).frame(minWidth: 320, idealWidth: 460) }
            }
            .navigationTitle(session.title)
            .navigationSubtitle(URL(fileURLWithPath: session.workspace).lastPathComponent)
        } else {
            ContentUnavailableView {
                Label("Make the next move clear", systemImage: "hammer")
            } description: {
                Text("Open a project folder and ask Mason to read, explain or change it. Sessions start by asking before each edit and command.")
            } actions: {
                Button("Open Project…") { model.openFolder() }.keyboardShortcut(.defaultAction)
            }
        }
    }
}

struct Transcript: View {
    @EnvironmentObject var model: AppModel
    let session: Session

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 18) {
                    ForEach(Array(session.turns.enumerated()), id: \.element.id) { index, turn in
                        TurnView(turn: turn, activities: activities(for: index), live: model.running && index == session.turns.count - 1)
                    }
                    if session.turns.isEmpty {
                        Text("Ask a question about this project, or describe a change.")
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .center)
                            .padding(.top, 80)
                    }
                    if !model.running, !session.detail.isEmpty {
                        Label(session.detail, systemImage: session.state == "Failed" ? "xmark.octagon" : "info.circle")
                            .foregroundStyle(session.state == "Failed" ? Color.red : Color.secondary)
                            .font(.callout)
                    }
                    Color.clear.frame(height: 1).id("end")
                }
                .padding(20)
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
            Text(turn.request)
                .textSelection(.enabled)
                .padding(.horizontal, 14).padding(.vertical, 10)
                .background(Color.accentColor.opacity(0.14), in: RoundedRectangle(cornerRadius: 12))
                .frame(maxWidth: .infinity, alignment: .trailing)
            if !activities.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(activities) { ActivityRow(activity: $0) }
                }
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
            if !turn.changes.isEmpty, !live {
                HStack(spacing: 10) {
                    ForEach(turn.changes.prefix(6), id: \.path) { change in
                        let counts = Diff.counts(before: change.before, after: change.deleted ? "" : change.after)
                        Label(change.isOpaque ? "\(change.path)  binary" : "\(change.path)  +\(counts.added) −\(counts.removed)",
                              systemImage: change.deleted ? "trash" : change.created ? "doc.badge.plus" : change.byCommand == true ? "terminal" : "pencil")
                            .font(.caption).padding(.horizontal, 8).padding(.vertical, 4)
                            .background(.quaternary, in: Capsule())
                    }
                    if turn.undone { Text("Undone").font(.caption).foregroundStyle(.orange) }
                }
            }
        }
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
                case .completed: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                case .failed: Image(systemName: "xmark.circle.fill").foregroundStyle(.red)
                case .denied: Image(systemName: "nosign").foregroundStyle(.orange)
                }
                Text(activity.tool.replacingOccurrences(of: "_", with: " ")).fontWeight(.medium)
                Text(activity.subject).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                if activity.state == .denied { Text("declined").foregroundStyle(.orange) }
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
            Label("Review \(tool)", systemImage: "hand.raised.fill").font(.headline)
            Text(pending.request.title).font(.system(.body, design: .monospaced)).textSelection(.enabled).lineLimit(6)
            if !pending.request.detail.isEmpty {
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        ForEach(Array(pending.request.detail.components(separatedBy: "\n").prefix(200).enumerated()), id: \.offset) { _, line in
                            Text(line.isEmpty ? " " : line)
                                .font(.system(.caption, design: .monospaced))
                                .foregroundStyle(line.hasPrefix("+") ? Color.green : line.hasPrefix("-") ? Color.red : Color.secondary)
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
        .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Color.orange.opacity(0.5)))
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

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            TextEditor(text: $model.draft)
                .font(.body)
                .focused($focused)
                .frame(minHeight: 44, maxHeight: 140)
                .scrollContentBackground(.hidden)
                .padding(8)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
                .overlay(alignment: .topLeading) {
                    if model.draft.isEmpty {
                        Text("Ask about this project or describe a change").foregroundStyle(.tertiary).padding(.horizontal, 13).padding(.vertical, 8).allowsHitTesting(false)
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
                    guard let pending = model.approval else { return .ignored }
                    model.answer(pending, allow: false)
                    return .handled
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
                Spacer()
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
                    Button { model.send() } label: { Label("Send", systemImage: "arrow.up.circle.fill") }
                        .buttonStyle(.borderedProminent)
                        .disabled(model.draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                        .help("Send (⌘Return)")
                }
            }
        }
        .padding(16)
        .onAppear { focused = true }
        .onChange(of: model.approval == nil) { _, answered in focused = answered }
    }
}

/// The files this session changed, as one diff per file, with undo and redo of the latest request.
struct ChangesView: View {
    @EnvironmentObject var model: AppModel
    let session: Session

    var changes: [FileChange] {
        var merged: [String: FileChange] = [:], order: [String] = []
        for turn in session.turns where !turn.undone {
            for change in turn.changes {
                if var existing = merged[change.path] { existing.after = change.after; existing.afterBlob = change.afterBlob; existing.afterMissing = change.afterMissing; merged[change.path] = existing } else { merged[change.path] = change; order.append(change.path) }
            }
        }
        return order.compactMap { merged[$0] }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Changes").font(.headline)
                Spacer()
                Button("Undo") { model.undo() }.disabled(model.running).help("Put back the files the latest request edited")
                Button("Redo") { model.undo(redo: true) }.disabled(model.running)
            }
            .padding(12)
            Divider()
            if changes.isEmpty {
                ContentUnavailableView("No changes", systemImage: "doc.text", description: Text("Files this session edits appear here."))
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 16) {
                        ForEach(changes, id: \.path) { FileDiff(change: $0) }
                        Text("Undo covers files changed by edits. Files created or changed by commands are not tracked yet.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    .padding(12)
                }
            }
        }
    }
}

struct FileDiff: View {
    let change: FileChange

    var body: some View {
        // A binary or very large file has no text to compare, so it is listed without lines.
        let rows = change.isOpaque ? [] : Diff.lines(before: change.before, after: change.deleted ? "" : change.after)
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(change.path).fontWeight(.medium)
                Text("+\(rows.filter { $0.kind == .added }.count)").foregroundStyle(.green)
                Text("−\(rows.filter { $0.kind == .removed }.count)").foregroundStyle(.red)
                if change.deleted { Text("deleted").foregroundStyle(.secondary) } else if change.created { Text("new file").foregroundStyle(.secondary) }
                if change.isOpaque { Text("binary or large file").foregroundStyle(.secondary) }
                if change.byCommand == true { Text("by a command").foregroundStyle(.secondary) }
            }
            .font(.callout)
            ScrollView(.horizontal) {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(visible(rows).enumerated()), id: \.offset) { _, row in
                        if let row {
                            Text("\(String(row.number).leftPadded(5)) \(String(row.kind.rawValue)) \(row.text)")
                                .font(.system(.caption, design: .monospaced))
                                .foregroundStyle(row.kind == .added ? Color.green : row.kind == .removed ? Color.red : Color.primary)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .background(row.kind == .added ? Color.green.opacity(0.10) : row.kind == .removed ? Color.red.opacity(0.10) : Color.clear)
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
