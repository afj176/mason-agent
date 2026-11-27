import SwiftUI
import MasonCore

// Comments pinned to lines, the saved plan of milestones, and the choice of an embeddings server for code search.

extension AppModel {
    /// Pins a note to a line of a diff. It is sent with the next request.
    func addComment(path: String, row: DiffLine, note: String) -> Bool {
        guard var current = session, let workspace = try? Workspace(path: current.workspace) else { return false }
        do {
            guard (current.comments ?? []).count < Review.limit else { throw MasonError("At most \(Review.limit) comments can wait at once.") }
            let made = try Review.comment(workspace, path: path, line: row.number, note: note, removed: row.kind == .removed ? row.text : nil)
            current.comments = (current.comments ?? []) + [made]
            save(current)
            return true
        } catch {
            notice = describe(error)
            return false
        }
    }

    /// Has the model read what this session changed. What it finds is pinned as comments, to go with the next request.
    func reviewChanges() {
        guard !reviewing, !running, let current = session, let workspace = try? Workspace(path: current.workspace) else { return }
        guard let endpoint, let client = try? OpenAIClient(endpoint) else { notice = "No model is running. Open Models to start one."; return }
        reviewing = true
        Task {
            do {
                let found = try await Review.findings(current, workspace: workspace, model: Roles.reviewer(main: client, workspace: workspace))
                if var latest = sessions.first(where: { $0.id == current.id }) {
                    latest.comments = Array(((latest.comments ?? []) + found.comments).prefix(Review.limit))
                    if latest.comments?.isEmpty == true { latest.comments = nil }
                    save(latest)
                }
                notice = found.comments.isEmpty ? Review.summary(found) : "The review pinned \(found.comments.count) comment\(found.comments.count == 1 ? "" : "s") above the request box. A model wrote them: check each before sending."
            } catch {
                notice = describe(error)
            }
            reviewing = false
        }
    }

    func removeComment(_ id: UUID) {
        guard var current = session else { return }
        current.comments?.removeAll { $0.id == id }
        if current.comments?.isEmpty == true { current.comments = nil }
        save(current)
    }

    func milestoneStatus(_ session: Session) -> [MilestoneStatus] {
        guard let plan = session.milestones, let workspace = try? Workspace(path: session.workspace) else { return [] }
        return Milestones.inspect(workspace, plan: plan)
    }

    func workflowNames(_ session: Session) -> [String] {
        guard let workspace = try? Workspace(path: session.workspace), let found = try? ProjectWorkflows.load(workspace) else { return [] }
        return found.workflows.keys.sorted()
    }

    func putMilestone(_ item: Milestone) -> Bool {
        guard var current = session, let workspace = try? Workspace(path: current.workspace) else { return false }
        do {
            current.milestones = try Milestones.put(workspace, current: current.milestones ?? [], item: item)
            save(current)
            return true
        } catch {
            notice = describe(error)
            return false
        }
    }

    func removeMilestone(_ id: UUID) {
        guard var current = session else { return }
        let plan = Milestones.remove(id, from: current.milestones ?? [])
        current.milestones = plan.isEmpty ? nil : plan
        save(current)
    }

    /// Runs the milestone's workflow, or records what the user saw.
    func checkMilestone(_ id: UUID, observation: String) {
        guard milestoneBusy == nil, let current = session, let plan = current.milestones, let item = plan.first(where: { $0.id == id }),
              let workspace = try? Workspace(path: current.workspace) else { return }
        let allowed = current.permission == .auto || ApprovalRules(workspace: current.workspace).allows(tool: "run_workflow", subject: "workflow " + item.workflow)
        milestoneBusy = id
        Task {
            do {
                let receipt = try await Milestones.check(workspace, item: item, plan: plan, runWorkflow: (allowed, current.permission == .auto), observation: observation)
                // The session may have moved on while the workflow ran; only the receipt is added.
                if var latest = sessions.first(where: { $0.id == current.id }), let index = latest.milestones?.firstIndex(where: { $0.id == id }) {
                    latest.milestones?[index].receipt = receipt
                    save(latest)
                }
            } catch {
                notice = describe(error)
            }
            milestoneBusy = nil
        }
    }
}

/// The notes waiting to go with the next request, above the request box.
struct CommentChips: View {
    @EnvironmentObject var model: AppModel
    let comments: [ReviewComment]

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(comments) { comment in
                HStack(spacing: 6) {
                    Image(systemName: "text.bubble").foregroundStyle(Brand.accent)
                    Text(comment.location).fontWeight(.medium)
                    Text(comment.note).foregroundStyle(.secondary).lineLimit(1)
                    Spacer(minLength: 0)
                    Button { model.removeComment(comment.id) } label: { Image(systemName: "xmark") }
                        .buttonStyle(.plain).foregroundStyle(.secondary)
                        .accessibilityLabel("Remove the comment on \(comment.location)")
                }
                .font(.callout)
                .padding(.horizontal, 8).padding(.vertical, 4)
                .background(Brand.accent.opacity(0.10), in: RoundedRectangle(cornerRadius: 8))
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Comments sent with the next request")
    }
}

/// Where a note for one diff line is written.
struct CommentEditor: View {
    let location: String
    let add: (String) -> Bool
    @State private var note = ""
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Comment on \(location)").font(.headline)
            TextField("What should change here?", text: $note, axis: .vertical)
                .lineLimit(2...5).textFieldStyle(.roundedBorder).frame(width: 320)
                .onSubmit { submit() }
            HStack {
                Text("Sent with your next request.").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                Button("Add Comment") { submit() }.keyboardShortcut(.defaultAction).disabled(note.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(14)
    }

    func submit() { if add(note) { dismiss() } }
}

/// The saved plan: each milestone, where it stands, and how to check it.
struct MilestonesView: View {
    @EnvironmentObject var model: AppModel
    let session: Session
    @State private var adding = false
    @State private var title = ""
    @State private var files = ""
    @State private var decision = ""
    @State private var workflow = ""
    @State private var after = Set<UUID>()
    @State private var observing: UUID?
    @State private var observation = ""

    func tint(_ state: MilestoneStatus.State) -> Color {
        switch state {
        case .verified, .reviewed: return Brand.success
        case .ready: return Brand.accent
        case .recheck: return Brand.caution
        case .blocked: return .secondary
        }
    }

    var body: some View {
        let rows = model.milestoneStatus(session)
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                if rows.isEmpty && !adding {
                    ContentUnavailableView("No milestones", systemImage: "flag.checkered",
                                           description: Text("Break the work into steps. Each names the files it covers and how it is checked: a project workflow, or what you saw yourself. Mason is told the plan with every request."))
                }
                ForEach(Array(rows.enumerated()), id: \.element.item.id) { index, row in
                    VStack(alignment: .leading, spacing: 6) {
                        HStack(alignment: .firstTextBaseline) {
                            Text("\(index + 1). \(row.item.title)").fontWeight(.semibold)
                            Spacer()
                            Text(row.state.rawValue).font(.caption).fontWeight(.semibold).foregroundStyle(tint(row.state))
                                .padding(.horizontal, 8).padding(.vertical, 2).background(tint(row.state).opacity(0.14), in: Capsule())
                        }
                        if !row.item.decision.isEmpty { Text(row.item.decision).font(.callout) }
                        Text(row.item.files.joined(separator: ", ")).font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary)
                        let first = row.reason.split(separator: "\n").first.map(String.init) ?? ""
                        Text((row.item.workflow.isEmpty ? "" : "Workflow \(row.item.workflow) · ") + first).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                        if observing == row.item.id {
                            TextField("What did you observe?", text: $observation, axis: .vertical).lineLimit(1...4).textFieldStyle(.roundedBorder)
                            HStack {
                                Button("Record") { model.checkMilestone(row.item.id, observation: observation); observing = nil; observation = "" }
                                    .disabled(observation.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                                Button("Cancel", role: .cancel) { observing = nil; observation = "" }
                            }
                        } else {
                            HStack {
                                if model.milestoneBusy == row.item.id { ProgressView().controlSize(.small); Text("Checking…").font(.caption).foregroundStyle(.secondary) }
                                Button(row.item.workflow.isEmpty ? "Record What You Saw…" : "Run Check") {
                                    if row.item.workflow.isEmpty { observing = row.item.id } else { model.checkMilestone(row.item.id, observation: "") }
                                }
                                .disabled(row.state == .blocked || model.milestoneBusy != nil || model.running)
                                .help(row.item.workflow.isEmpty ? "Record what you observed as this milestone's evidence" : "Run the workflow \(row.item.workflow). Mason runs it in Auto, or when you chose Always Allow for it.")
                                Spacer()
                                Button(role: .destructive) { model.removeMilestone(row.item.id) } label: { Image(systemName: "trash") }
                                    .buttonStyle(.borderless).accessibilityLabel("Remove \(row.item.title)")
                            }
                        }
                    }
                    .padding(12)
                    .card()
                }
                if adding { form(rows) } else {
                    Button { adding = true } label: { Label("Add Milestone", systemImage: "plus") }
                }
            }
            .padding(12)
        }
    }

    @ViewBuilder func form(_ rows: [MilestoneStatus]) -> some View {
        let names = model.workflowNames(session)
        VStack(alignment: .leading, spacing: 8) {
            Text("New milestone").font(.headline)
            TextField("Title, for example: Totals include tax", text: $title).textFieldStyle(.roundedBorder)
            TextField("Files it covers, separated by commas", text: $files).textFieldStyle(.roundedBorder)
            TextField("Decision or note (optional)", text: $decision, axis: .vertical).lineLimit(1...3).textFieldStyle(.roundedBorder)
            Picker("Checked by", selection: $workflow) {
                Text("What I observe").tag("")
                ForEach(names, id: \.self) { Text("Workflow \($0)").tag($0) }
            }
            if !rows.isEmpty {
                Text("Comes after").font(.caption).foregroundStyle(.secondary)
                ForEach(rows, id: \.item.id) { row in
                    Toggle(row.item.title, isOn: Binding(get: { after.contains(row.item.id) }, set: { if $0 { after.insert(row.item.id) } else { after.remove(row.item.id) } }))
                }
            }
            HStack {
                Button("Add") {
                    let paths = files.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
                    if model.putMilestone(Milestone(title: title, decision: decision, files: paths, dependsOn: Array(after), workflow: workflow)) {
                        title = ""; files = ""; decision = ""; workflow = ""; after = []; adding = false
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(title.trimmingCharacters(in: .whitespaces).isEmpty || files.trimmingCharacters(in: .whitespaces).isEmpty)
                Button("Cancel", role: .cancel) { adding = false }
            }
        }
        .padding(12)
        .card()
    }
}

/// Settings: the embeddings server Mason uses to search code by meaning.
struct CodeSearchSection: View {
    @State private var url = ""
    @State private var name = ""
    @State private var rerankUrl = ""
    @State private var rerankName = ""
    @State private var status = ""
    @State private var testing = false

    var body: some View {
        Section("Code search by meaning") {
            TextField("Embeddings server", text: $url, prompt: Text("http://127.0.0.1:8081/"))
            TextField("Model", text: $name, prompt: Text("nomic-embed-text"))
            TextField("Reranker server (optional)", text: $rerankUrl, prompt: Text("http://127.0.0.1:8082/"))
            TextField("Reranker model", text: $rerankName)
            HStack {
                Button("Save") { save() }.disabled(url.trimmingCharacters(in: .whitespaces).isEmpty || name.trimmingCharacters(in: .whitespaces).isEmpty)
                Button("Test") { test() }.disabled(testing || EmbeddingSettings.load() == nil)
                Button("Turn Off", role: .destructive) { try? EmbeddingSettings.save(nil); url = ""; name = ""; rerankUrl = ""; rerankName = ""; status = "Code search by meaning is off." }
                    .disabled(EmbeddingSettings.load() == nil)
                if testing { ProgressView().controlSize(.small) }
            }
            Text(status.isEmpty ? "With a server chosen, Mason can find code from a description such as “where are invoice totals calculated”. Any server on this Mac that answers /v1/embeddings works, for example llama-server --embedding with an embeddings model." : status)
                .font(.callout).foregroundStyle(.secondary)
        }
        .onAppear {
            if let saved = EmbeddingSettings.load() { url = saved.url; name = saved.model; rerankUrl = saved.rerankUrl; rerankName = saved.rerankModel }
        }
    }

    func save() {
        do {
            try EmbeddingSettings.save(EmbeddingSettings(url: url.trimmingCharacters(in: .whitespaces), model: name.trimmingCharacters(in: .whitespaces),
                                                         rerankUrl: rerankUrl.trimmingCharacters(in: .whitespaces), rerankModel: rerankName.trimmingCharacters(in: .whitespaces)))
            status = "Saved. Mason offers the model semantic_search from the next request."
        } catch {
            status = (error as? MasonError)?.description ?? error.localizedDescription
        }
    }

    func test() {
        guard let saved = EmbeddingSettings.load() else { return }
        testing = true
        Task {
            do { status = try await EmbeddingServers(saved).probe() } catch { status = (error as? MasonError)?.description ?? error.localizedDescription }
            testing = false
        }
    }
}
