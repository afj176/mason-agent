import SwiftUI
import AppKit
import MasonCore

/// One model's Ladder panel: what it is, the context sizes with the memory each needs, how that fits the memory free
/// now, and what can be done with it. The same layout and words as the Library panel in Mason for Windows and Linux.
struct ModelPanel: View {
    @EnvironmentObject var model: AppModel
    let item: LocalModel
    let back: () -> Void

    @State private var tokens = 0
    @State private var cpu = false
    @State private var explaining = false
    @State private var confirmingTrash = false
    @State private var showingRuntime = false
    @State private var copied = false
    @State private var build: String?
    @State private var reading = MemoryReading.unified()

    private var profile: ModelMemoryProfile { model.memoryProfiles[item.path] ?? ModelMemory.read(item) }
    private var running: ModelServer? { model.server?.model.path == item.path ? model.server : nil }
    private var starting: Bool { model.startingPath == item.path }
    private var steps: [Int] { ModelMemory.ladder(profile) }
    private var chosen: Int { steps.contains(tokens) ? tokens : (steps.last(where: { $0 <= tokens }) ?? steps.first ?? 2048) }

    static func tone(_ state: FitState) -> Color {
        switch state {
        case .likelyFits: return Brand.fitGood
        case .tight: return Brand.fitTight
        case .tooLarge: return Brand.fitOver
        case .unverified, .waiting: return Brand.muted
        }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Button(action: back) { Label("Library", systemImage: "chevron.left") }
                    .buttonStyle(.plain).foregroundStyle(Brand.muted)
                heading
                if item.startable {
                    ladder
                    fitCard
                    if running == nil {
                        HStack {
                            Text(ModelMemory.recommend(profile, memory: reading).text).font(.callout).foregroundStyle(Brand.muted)
                            Spacer()
                            Button("Use suggested") { useSuggested() }.controlSize(.small)
                        }
                    }
                    startRow
                }
                Divider()
                actions
                Divider()
                footer
            }
            .padding(24)
            .frame(maxWidth: 520, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .navigationTitle(item.name)
        .onAppear {
            reading = MemoryReading.unified()
            if tokens == 0 { useSuggested() }
        }
        .task {
            if let runtime = model.runtimePath { build = await Task.detached { LlamaRuntime.build(at: runtime) }.value }
        }
        .confirmationDialog("Move \(item.name) to the Trash?", isPresented: $confirmingTrash, titleVisibility: .visible) {
            Button("Move to Trash", role: .destructive) { model.trash(item); back() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("\(item.size) will be freed. Mason stops listing it. You can put it back from the Trash.")
        }
    }

    // MARK: parts

    var heading: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let repository = item.repository, let org = repository.split(separator: "/").first {
                Text(org).font(.callout).foregroundStyle(Brand.muted)
            }
            Text(item.name).font(.title2).fontWeight(.semibold).lineLimit(2).textSelection(.enabled)
            HStack(spacing: 6) {
                ForEach([item.quantization, item.format, item.size].filter { !$0.isEmpty }, id: \.self) { chip in
                    Text(chip).font(.caption).fontWeight(.semibold)
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .overlay(Capsule().strokeBorder(Brand.seam))
                }
            }
            HStack(spacing: 8) {
                Circle().fill(running != nil || (item.startable && !starting) ? Brand.fitGood : starting ? Brand.accent : Brand.muted).frame(width: 8, height: 8)
                Text(status).font(.callout).foregroundStyle(Brand.muted)
            }
        }
    }

    var status: String {
        if let running { return "Running · \(running.endpoint.url)" }
        if starting { return "Starting · \(model.startStage?.rawValue ?? "inspect")" }
        if !item.startable { return item.verdict }
        if !item.transformers, model.runtimePath == nil { return "Install llama.cpp to start · \(item.roleLabel)" }
        return "Ready to start · \(item.roleLabel)"
    }

    var ladder: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Text("Context length").fontWeight(.semibold)
                Text("· memory use").foregroundStyle(Brand.muted)
                Button { explaining.toggle() } label: { Image(systemName: "questionmark.circle") }
                    .buttonStyle(.plain).foregroundStyle(Brand.muted)
                    .accessibilityLabel("How the memory estimate works")
                    .popover(isPresented: $explaining) {
                        Text(ModelMemory.explanation(profile)).font(.callout).padding(14).frame(width: 320).fixedSize(horizontal: false, vertical: true)
                    }
                Spacer()
                Text(profile.contextDeclared ? "max \(ModelMemory.grouped(profile.maxContext))" : "max not declared").font(.caption).foregroundStyle(Brand.muted)
            }
            HStack(spacing: 2) {
                ForEach(steps, id: \.self) { step in
                    let fit = ModelMemory.assess(profile, memory: reading, context: step)
                    let selected = step == chosen
                    Button { tokens = step } label: {
                        VStack(spacing: 3) {
                            Text(ModelMemory.label(step)).font(.system(size: 15, weight: .bold))
                            Text(ModelMemory.stepMemory(fit)).font(.caption).monospacedDigit()
                                .foregroundStyle(cpu || running != nil ? Brand.muted : Self.tone(fit.state))
                        }
                        .frame(maxWidth: .infinity, minHeight: 44)
                        .background(selected ? Brand.surface : .clear, in: RoundedRectangle(cornerRadius: 7))
                        .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(selected ? Brand.seam : .clear))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(ModelMemory.accessibleName(fit, step: step))
                    .accessibilityAddTraits(selected ? .isSelected : [])
                }
            }
            .padding(3)
            .background(Brand.track, in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Brand.seam))
            .disabled(starting)
            .focusable()
            .onMoveCommand { direction in
                guard let at = steps.firstIndex(of: chosen) else { return }
                if direction == .left, at > 0 { tokens = steps[at - 1] }
                if direction == .right, at + 1 < steps.count { tokens = steps[at + 1] }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Context length")
        }
    }

    var fitCard: some View {
        let fit = ModelMemory.assess(profile, memory: reading, context: chosen)
        let card = ModelMemory.card(fit, step: chosen, architecture: profile.architecture, cpu: cpu, running: running != nil)
        let color = cpu || running != nil ? Brand.muted : Self.tone(fit.state)
        let fill = cpu || running != nil ? 0 : min(1, max(0, (fit.highGiB ?? fit.weightsGiB) / max(0.01, fit.freeGiB ?? 0)))
        return VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Circle().fill(color).frame(width: 8, height: 8)
                Text(card.title).fontWeight(.semibold)
                Spacer()
                if !cpu, running == nil { Text(reading.deviceLabel).font(.caption).foregroundStyle(Brand.muted) }
            }
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule().fill(Brand.track)
                    Capsule().fill(color).frame(width: geometry.size.width * fill)
                }
            }
            .frame(height: 6)
            .accessibilityHidden(true)
            Text(card.detail).font(.caption).foregroundStyle(Brand.muted).fixedSize(horizontal: false, vertical: true)
        }
        .padding(14)
        .background(Brand.surface, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Brand.seam))
    }

    var startRow: some View {
        HStack(spacing: 10) {
            Picker("Compute", selection: $cpu) {
                Text("GPU").tag(false)
                Text("CPU").tag(true)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 120)
            .disabled(starting || running != nil)
            if starting {
                Button("Cancel startup") { model.cancelModelWork() }.frame(maxWidth: .infinity)
            } else if !item.transformers, model.runtimePath == nil {
                Button("Install llama.cpp") { model.installRuntime() }.buttonStyle(.borderedProminent).frame(maxWidth: .infinity)
                    .disabled(model.modelBusy != nil)
            } else {
                Button { model.start(item, context: chosen, cpuOnly: cpu) } label: {
                    Text("Start & connect · \(ModelMemory.label(chosen))").frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent).controlSize(.large)
                .disabled(model.modelBusy != nil || running != nil)
            }
        }
    }

    var actions: some View {
        VStack(alignment: .leading, spacing: 2) {
            if item.startable {
                row("chevron.left.forwardslash.chevron.right", "Use for coding", note: running == nil ? "after start" : "", enabled: running != nil) {
                    if let running { model.use(running.endpoint, name: item.name); model.pane = .code }
                }
                row("bubble.left", "Open in chat", enabled: running != nil) {
                    if let running { model.use(running.endpoint, name: item.name); model.pane = .chat }
                }
            }
            row("doc.on.doc", "Copy model ID", note: copied ? "Copied" : "") {
                let id = running?.endpoint.model ?? URL(fileURLWithPath: item.path).deletingPathExtension().lastPathComponent
                NSPasteboard.general.clearContents(); NSPasteboard.general.setString(id, forType: .string); copied = true
            }
            row("folder", "Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: item.path)]) }
            if let repository = item.repository, let url = URL(string: "https://huggingface.co/" + repository) {
                row("arrow.up.right.square", "View on Hugging Face") { NSWorkspace.shared.open(url) }
            }
            row("trash", "Delete", note: item.size, enabled: running == nil && !starting && model.modelBusy == nil) { confirmingTrash = true }
        }
        .padding(.horizontal, -8)
    }

    func row(_ symbol: String, _ title: String, note: String = "", enabled: Bool = true, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Image(systemName: symbol).frame(width: 18).foregroundStyle(Brand.muted)
                Text(title)
                Spacer()
                Text(note).font(.caption).foregroundStyle(Brand.muted)
            }
            .padding(.horizontal, 8).frame(minHeight: 36).contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.5)
        .accessibilityLabel(title)
    }

    var footer: some View {
        HStack {
            Text([profile.architecture, item.transformers ? "" : "llama.cpp " + (build ?? "not installed")].filter { !$0.isEmpty }.joined(separator: " · "))
                .font(.caption).foregroundStyle(Brand.muted).lineLimit(1)
            Spacer()
            Button("Runtime") { showingRuntime.toggle() }.controlSize(.small)
                .popover(isPresented: $showingRuntime) {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(model.runtimePath ?? "llama.cpp is not installed").font(.caption).textSelection(.enabled)
                        Button(model.runtimePath == nil ? "Install llama.cpp" : "Update llama.cpp") { model.installRuntime(); showingRuntime = false }
                            .disabled(model.modelBusy != nil)
                    }
                    .padding(14).frame(width: 320, alignment: .leading)
                }
        }
    }

    func useSuggested() {
        let suggestion = ModelMemory.recommend(profile, memory: reading)
        tokens = suggestion.context; cpu = !suggestion.gpu
    }
}
