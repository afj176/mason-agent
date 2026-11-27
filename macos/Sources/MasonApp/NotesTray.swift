import SwiftUI
import AppKit
import MasonCore

// Notes: short facts Mason reads with every request in a project. A small tab on the top edge of the request box
// opens a tray that slides up from behind it, over the end of the transcript. A note the model suggests with
// suggest_note shows as a card at the end of that answer.

/// What the Undo at the bottom of the tray puts back: one removed note, or every cleared one.
struct NotesUndo: Equatable {
    let id = UUID()
    let label: String
    let notes: [ProjectNote]
}

extension AppModel {
    var notesHome: URL { store?.folder.deletingLastPathComponent() ?? DataHome.url }
    var notesWorkspace: Workspace? { session.flatMap { try? Workspace(path: $0.workspace) } }
    /// How many notes Mason reads: the current ones, up to 12.
    var notesSentCount: Int { min(ProjectNotes.sent, projectNotes.filter { !staleNotes.contains($0.id) }.count) }
    var notesFull: Bool { projectNotes.count >= ProjectNotes.limit }

    /// Reads the notes of the project in view again, and which of them are out of date.
    func reloadNotes() {
        guard let workspace = notesWorkspace else { projectNotes = []; staleNotes = []; return }
        let all = ProjectNotes.newest(home: notesHome, workspace: workspace.root.path)
        projectNotes = all
        staleNotes = Set(all.filter { !ProjectNotes.current($0, in: workspace) }.map(\.id))
    }

    /// Opens the tray, optionally with text in its field or with the clear-all question showing.
    func openNotes(text: String? = nil, confirmClear: Bool = false) {
        reloadNotes()
        if let text { noteField = String(text.prefix(ProjectNotes.textLimit)) }
        notesConfirmClear = confirmClear && !projectNotes.isEmpty
        notesOpen = true
    }

    func closeNotes() {
        notesOpen = false
        notesConfirmClear = false
    }

    func toggleNotes() { if notesOpen { closeNotes() } else { openNotes() } }

    /// Adds what is in the field, about the chosen file if there is one. Returns a problem to show, or nil.
    func addNote() -> String? {
        guard let workspace = notesWorkspace else { return "Open a project first." }
        do {
            try ProjectNotes.add(noteField, about: noteSource, workspace: workspace, home: notesHome)
            noteField = ""
            noteSource = nil
            reloadNotes()
            return nil
        } catch { return describe(error) }
    }

    /// Changes a note's text in place. Returns a problem to show, or nil.
    func editNote(_ note: ProjectNote, text: String) -> String? {
        guard let workspace = notesWorkspace else { return "Open a project first." }
        do {
            try ProjectNotes.edit(note.id, text: text, workspace: workspace, home: notesHome)
            reloadNotes()
            return nil
        } catch { return describe(error) }
    }

    /// Keep anyway: the note is saved against its file as it is now, so Mason reads it again.
    func keepNote(_ note: ProjectNote) {
        guard let workspace = notesWorkspace else { return }
        do { try ProjectNotes.refresh(note.id, workspace: workspace, home: notesHome) } catch { notice = describe(error) }
        reloadNotes()
    }

    func removeNote(_ note: ProjectNote) {
        guard let workspace = notesWorkspace else { return }
        do {
            try ProjectNotes.remove(note.id, workspace: workspace.root.path, home: notesHome)
            notesUndo = NotesUndo(label: "Note removed", notes: [note])
        } catch { notice = describe(error) }
        reloadNotes()
    }

    func clearNotes() {
        notesConfirmClear = false
        guard let workspace = notesWorkspace else { return }
        do {
            let previous = try ProjectNotes.clear(workspace: workspace.root.path, home: notesHome)
            if !previous.isEmpty { notesUndo = NotesUndo(label: "\(previous.count) notes cleared", notes: previous) }
        } catch { notice = describe(error) }
        reloadNotes()
    }

    func undoNotes() {
        guard let undo = notesUndo, let workspace = notesWorkspace else { return }
        notesUndo = nil
        do { try ProjectNotes.restore(undo.notes, workspace: workspace.root.path, home: notesHome) } catch { notice = describe(error) }
        reloadNotes()
    }

    // MARK: a note the model suggested

    func rememberSuggestion() {
        guard var current = session, let suggestion = current.suggestedNote, let workspace = notesWorkspace else { return }
        do {
            try ProjectNotes.add(suggestion.text, workspace: workspace, home: notesHome)
            current.suggestedNote = nil
            save(current)
        } catch { notice = describe(error) }
        reloadNotes()
    }

    func dismissSuggestion() {
        guard var current = session, let suggestion = current.suggestedNote else { return }
        do { try ProjectNotes.dismiss(suggestion.text, workspace: notesWorkspace?.root.path ?? current.workspace, home: notesHome) } catch { notice = describe(error) }
        current.suggestedNote = nil
        save(current)
    }

    /// Edit: the suggestion goes into the tray's field, for the user to change and add.
    func editSuggestion() {
        guard var current = session, let suggestion = current.suggestedNote else { return }
        current.suggestedNote = nil
        save(current)
        openNotes(text: suggestion.text)
    }
}

/// The tab on the composer's top edge and, when open, the tray behind it. It is drawn behind the composer, so the
/// tray slides up from under the request box and nothing else moves.
struct NotesDrawer: View {
    /// How far the drawer reaches down behind the composer.
    static let tuck: CGFloat = 10
    @EnvironmentObject var model: AppModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let session: Session

    var body: some View {
        // The tab sits toward the trailing end, clear of the status pose at the leading end above the request box.
        ZStack(alignment: .bottomTrailing) {
            if model.notesOpen {
                NotesTray(session: session)
                    .transition(reduceMotion ? AnyTransition.opacity : AnyTransition.move(edge: .bottom))
            }
            NotesTab().padding(.trailing, 22)
        }
        .frame(maxWidth: .infinity, alignment: .bottomTrailing)
        .fixedSize(horizontal: false, vertical: true)
        .clipped()
        .animation(reduceMotion ? Animation.easeInOut(duration: 0.15) : Animation.spring(response: 0.38, dampingFraction: 0.86), value: model.notesOpen)
        .task(id: session.workspace) { model.reloadNotes() }
    }
}

/// "Notes" with the number of notes, tucked behind the top edge of the request box.
struct NotesTab: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        Button { model.toggleNotes() } label: {
            HStack(spacing: 6) {
                Image(systemName: "note.text")
                Text("Notes").fontWeight(.semibold)
                if !model.projectNotes.isEmpty {
                    Text("\(model.projectNotes.count)")
                        .font(.caption2).fontWeight(.bold)
                        .foregroundStyle(Brand.onAccent)
                        .padding(.horizontal, 5).padding(.vertical, 1)
                        .background(Brand.accent, in: Capsule())
                }
            }
            .font(.caption)
            .foregroundStyle(model.notesOpen ? Brand.accent : Brand.muted)
            .padding(.horizontal, 10)
            .padding(.top, 5)
            .padding(.bottom, 5 + NotesDrawer.tuck)
            .background(Brand.surface, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(Brand.seam, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(model.notesOpen ? "Close Notes (⌥⌘N)" : "Notes Mason reads with every request (⌥⌘N)")
        .accessibilityLabel("Notes, \(model.projectNotes.count)")
    }
}

/// The tray: adding a note, the notes newest first, and how many Mason reads.
struct NotesTray: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    @State private var problem: String?
    @State private var choosingFile = false
    @State private var typedPath = ""
    @FocusState private var fieldFocused: Bool

    static let examples = ["We use pnpm, not npm", "Tests live in tests/", "Keep the public API stable"]

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text("Notes").fontWeight(.bold).foregroundStyle(Brand.ink)
                Text("Mason reads these with every request").font(.callout).foregroundStyle(Brand.muted)
                Spacer(minLength: 0)
            }
            addRow
            if let source = model.noteSource {
                HStack(spacing: 4) {
                    Image(systemName: "doc.text")
                    Text(source).font(.system(.caption, design: .monospaced)).lineLimit(1).truncationMode(.head)
                    Button { model.noteSource = nil } label: { Image(systemName: "xmark") }
                        .buttonStyle(.plain)
                        .accessibilityLabel("Remove \(source)")
                }
                .font(.caption)
                .foregroundStyle(Brand.ink)
                .padding(.horizontal, 8).padding(.vertical, 3)
                .background(Brand.accent.opacity(0.14), in: Capsule())
            }
            if let problem { Text(problem).font(.caption).foregroundStyle(Brand.critical) }
            if model.projectNotes.isEmpty {
                emptyState
            } else if model.projectNotes.count > 5 {
                ScrollView {
                    rows
                }
                .frame(height: 290)
            } else {
                rows
            }
            if model.running {
                Text("Changes apply from the next request.").font(.caption).foregroundStyle(Brand.muted)
            }
            if let undo = model.notesUndo {
                HStack(spacing: 6) {
                    Text(undo.label).foregroundStyle(Brand.muted)
                    Text("·").foregroundStyle(Brand.muted)
                    Button("Undo") { model.undoNotes() }.buttonStyle(.link)
                    Spacer(minLength: 0)
                }
                .font(.callout)
            }
            if model.notesConfirmClear {
                HStack(spacing: 8) {
                    Text("Clear all \(model.projectNotes.count) notes? Mason stops reading them.").font(.callout).foregroundStyle(Brand.ink)
                    Spacer(minLength: 0)
                    Button("Clear", role: .destructive) { model.clearNotes() }
                    Button("Keep") { model.notesConfirmClear = false }
                }
            } else if !model.projectNotes.isEmpty {
                HStack {
                    Text("\(model.notesSentCount) of \(model.projectNotes.count) sent · up to \(ProjectNotes.sent)").font(.caption).foregroundStyle(Brand.muted)
                    Spacer(minLength: 0)
                    Button("Clear all") { model.notesConfirmClear = true }.buttonStyle(.link).font(.caption)
                }
            }
        }
        .padding(14)
        // Room for the tab, and for the part of the tray that sits behind the request box.
        .padding(.bottom, 30 + NotesDrawer.tuck)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Brand.sidebar, in: RoundedRectangle(cornerRadius: 16))
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(Brand.seam, lineWidth: 1))
        .onAppear { fieldFocused = true }
        .task(id: model.notesUndo?.id) {
            // The Undo shows for a few seconds.
            guard let shown = model.notesUndo?.id else { return }
            try? await Task.sleep(nanoseconds: 6_000_000_000)
            if model.notesUndo?.id == shown { model.notesUndo = nil }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Notes")
    }

    var addRow: some View {
        let full = model.notesFull
        let prompt: String = full ? "\(ProjectNotes.limit) of \(ProjectNotes.limit). Remove one to add another." : "Something Mason should always know about this project"
        return HStack(spacing: 8) {
            TextField(prompt, text: $model.noteField)
                .textFieldStyle(.roundedBorder)
                .focused($fieldFocused)
                .disabled(full)
                .onSubmit(add)
                .onKeyPress(.escape) {
                    model.closeNotes()
                    return .handled
                }
                .onChange(of: model.noteField) { _, typed in
                    if typed.count > ProjectNotes.textLimit { model.noteField = String(typed.prefix(ProjectNotes.textLimit)) }
                }
            Button("About a file…") {
                typedPath = model.noteSource ?? model.fileShown ?? ""
                choosingFile = true
            }
            .controlSize(.small)
            .disabled(full)
            .help("Tie the note to one file. Mason stops reading it once that file changes.")
            .popover(isPresented: $choosingFile, arrowEdge: .bottom) { pathPicker }
            Button("Add", action: add)
                .buttonStyle(.borderedProminent)
                .disabled(full || model.noteField.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        }
    }

    var rows: some View {
        VStack(spacing: 6) {
            ForEach(model.projectNotes) { note in
                NoteRow(note: note, stale: model.staleNotes.contains(note.id))
            }
        }
    }

    var emptyState: some View {
        VStack(spacing: 8) {
            PoseImage(pose: .planning, height: 76)
            Text("Nothing to remember yet").fontWeight(.bold).foregroundStyle(Brand.ink)
            Text("Notes are facts Mason should know every time: how to run things, where things live, what not to touch.")
                .font(.callout).foregroundStyle(Brand.muted)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 6) {
                ForEach(NotesTray.examples, id: \.self) { example in
                    Button(example) {
                        model.noteField = example
                        fieldFocused = true
                    }
                    .buttonStyle(.plain)
                    .font(.caption)
                    .foregroundStyle(Brand.ink)
                    .chip()
                }
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 6)
    }

    var pathPicker: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("About a file").font(.headline)
            TextField("Path in the project, such as src/app.ts", text: $typedPath)
                .textFieldStyle(.roundedBorder)
                .font(.system(.body, design: .monospaced))
                .frame(width: 320)
                .onSubmit(useTypedPath)
            HStack {
                Button("Choose…", action: choosePath)
                Spacer()
                Button("Cancel") { choosingFile = false }
                Button("Use", action: useTypedPath)
                    .keyboardShortcut(.defaultAction)
                    .disabled(typedPath.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(14)
    }

    func add() {
        guard !model.notesFull, !model.noteField.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        problem = model.addNote()
        fieldFocused = true
    }

    func useTypedPath() {
        let path = typedPath.trimmingCharacters(in: .whitespaces)
        guard !path.isEmpty else { return }
        guard let workspace = model.notesWorkspace, workspace.exists(path) else {
            problem = "\(path) is not a file in the project."
            choosingFile = false
            return
        }
        problem = nil
        model.noteSource = path
        choosingFile = false
    }

    /// A file chosen in the open panel, as a path inside the project.
    func choosePath() {
        let root = URL(fileURLWithPath: session.workspace).resolvingSymlinksInPath().path
        let panel = NSOpenPanel()
        panel.canChooseFiles = true; panel.canChooseDirectories = false; panel.allowsMultipleSelection = false
        panel.directoryURL = URL(fileURLWithPath: root)
        panel.prompt = "Choose"
        panel.message = "Choose a file in this project for the note to be about."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let chosen = url.resolvingSymlinksInPath().path
        guard chosen.hasPrefix(root + "/") else { problem = "Choose a file inside this project."; return }
        typedPath = String(chosen.dropFirst(root.count + 1))
    }
}

/// One note: its text (click to edit in place), its file and age, and × to remove it.
struct NoteRow: View {
    @EnvironmentObject var model: AppModel
    let note: ProjectNote
    let stale: Bool
    @State private var editing = false
    @State private var text = ""
    @State private var problem: String?
    @FocusState private var focused: Bool

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            VStack(alignment: .leading, spacing: 3) {
                if editing {
                    TextField("Note", text: $text)
                        .textFieldStyle(.roundedBorder)
                        .focused($focused)
                        .onSubmit(save)
                        .onKeyPress(.escape) {
                            editing = false
                            problem = nil
                            return .handled
                        }
                        .onChange(of: text) { _, typed in
                            if typed.count > ProjectNotes.textLimit { text = String(typed.prefix(ProjectNotes.textLimit)) }
                        }
                        .onAppear { focused = true }
                    if let problem { Text(problem).font(.caption).foregroundStyle(Brand.critical) }
                } else {
                    Text(note.text)
                        .foregroundStyle(Brand.ink)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(Rectangle())
                        .onTapGesture {
                            text = note.text
                            problem = nil
                            editing = true
                        }
                        .help("Click to edit")
                }
                HStack(spacing: 4) {
                    if let source = note.source {
                        Text(source).font(.system(.caption, design: .monospaced)).lineLimit(1).truncationMode(.head)
                        Text(stale ? "changed · not sent" : "· " + ProjectNotes.age(note.saved))
                    } else {
                        Text(ProjectNotes.age(note.saved))
                    }
                }
                .font(.caption)
                .foregroundStyle(stale ? Brand.caution : Brand.muted)
                if stale {
                    HStack(spacing: 8) {
                        Button("Keep anyway") { model.keepNote(note) }
                            .help("Mason reads it again, as the file is now")
                        Button("Remove") { model.removeNote(note) }
                    }
                    .controlSize(.small)
                }
            }
            Button { model.removeNote(note) } label: { Image(systemName: "xmark") }
                .buttonStyle(.plain)
                .foregroundStyle(Brand.muted)
                .help("Remove")
                .accessibilityLabel("Remove the note \(note.text)")
        }
        .padding(10)
        .background(Brand.surface, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Brand.seam, style: StrokeStyle(lineWidth: 1, dash: stale ? [4, 3] : [])))
        .opacity(stale ? 0.72 : 1)
    }

    func save() {
        problem = model.editNote(note, text: text)
        if problem == nil { editing = false }
    }
}

/// "Remember this for next time?" at the end of the answer in which the model suggested a note.
struct NoteSuggestionCard: View {
    @EnvironmentObject var model: AppModel
    let suggestion: NoteSuggestion

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Remember this for next time?", systemImage: "note.text.badge.plus")
                .font(.callout).fontWeight(.semibold).foregroundStyle(Brand.ink)
            Text(verbatim: "“" + suggestion.text + "”").textSelection(.enabled).foregroundStyle(Brand.ink)
            HStack(spacing: 8) {
                Button("Remember") { model.rememberSuggestion() }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.notesFull || model.running)
                    .help(model.notesFull ? "12 of 12. Remove one to add another." : "Keep this note. Mason reads it with every request.")
                Button("Edit") { model.editSuggestion() }
                    .disabled(model.running)
                    .help("Change it in Notes before adding it")
                Button("Dismiss") { model.dismissSuggestion() }
                    .disabled(model.running)
                    .help("Mason will not suggest it again for this project")
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Brand.accent.opacity(0.08), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Brand.accent.opacity(0.4)))
        .onAppear { model.reloadNotes() }
    }
}
