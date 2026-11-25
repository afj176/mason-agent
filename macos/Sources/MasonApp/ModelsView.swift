import SwiftUI
import MasonCore

/// Models on this Mac: what is running, the files found, the llama.cpp runtime, and Hugging Face search and download.
struct ModelsView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                if let busy = model.modelBusy {
                    HStack(spacing: 10) {
                        ProgressView().controlSize(.small)
                        Text(busy).lineLimit(2)
                        Spacer()
                        Button("Cancel") { model.cancelModelWork() }
                    }
                    .padding(12)
                    .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
                }
                if let download = model.download {
                    VStack(alignment: .leading, spacing: 6) {
                        HStack {
                            Text("Downloading \(download.title)").fontWeight(.medium)
                            Spacer()
                            Button("Cancel") { model.cancelModelWork() }
                        }
                        ProgressView(value: download.fraction)
                        Text(download.detail).font(.caption).foregroundStyle(.secondary)
                    }
                    .padding(12)
                    .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
                }
                running
                runtime
                local
                discover
            }
            .padding(24)
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .navigationTitle("Models")
        .navigationSubtitle("\(ByteText.size(MemoryFit.budgetBytes)) of memory available for a model")
        .task { await model.refreshModels() }
    }

    // MARK: sections

    var running: some View {
        Section2(title: "Running now", note: "Mason uses any OpenAI-compatible server on this Mac: its own, llama.cpp, LM Studio or Ollama.") {
            if model.servers.isEmpty {
                Text("Nothing is answering. Start a model below, or start your own server and refresh.").foregroundStyle(.secondary)
            }
            ForEach(Array(model.servers.enumerated()), id: \.offset) { _, item in
                HStack {
                    Image(systemName: "circle.fill").foregroundStyle(.green).font(.caption2)
                    VStack(alignment: .leading) {
                        Text(model.server?.endpoint == item.endpoint ? (model.server?.model.name ?? item.endpoint.model) : item.endpoint.model)
                        Text("\(item.name) · \(item.endpoint.url)").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if model.endpoint == item.endpoint { Text("In use").foregroundStyle(.secondary) }
                    else { Button("Use") { model.use(item.endpoint, name: item.endpoint.model) } }
                    if model.server?.endpoint == item.endpoint { Button("Stop") { model.stopServer() } }
                }
            }
            Button { Task { await model.refreshModels() } } label: { Label("Refresh", systemImage: "arrow.clockwise") }
        }
    }

    var runtime: some View {
        Section2(title: "Runtime", note: "llama.cpp runs GGUF models. Mason can install the official build for this Mac, verified against its published SHA-256.") {
            HStack {
                if let path = model.runtimePath {
                    Label("llama.cpp is installed", systemImage: "checkmark.seal.fill").foregroundStyle(.green)
                    Text(path).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                } else {
                    Label("llama.cpp is not installed", systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
                }
                Spacer()
                Button(model.runtimePath == nil ? "Install llama.cpp" : "Update") { model.installRuntime() }.disabled(model.modelBusy != nil)
            }
        }
    }

    var local: some View {
        Section2(title: "On this Mac", note: "Every model file found is listed with what the agent can do with it.") {
            if model.localModels.isEmpty {
                Text("No model files were found. Search below, or let Mason suggest one that fits.").foregroundStyle(.secondary)
            }
            ForEach(model.localModels) { item in
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.name).fontWeight(item.startable ? .medium : .regular)
                        Text(item.verdict).font(.caption).foregroundStyle(.secondary)
                        Text("\(item.size) · \(item.source)" + (item.startable && !MemoryFit.fits(weights: item.bytes) ? " · larger than the memory available" : ""))
                            .font(.caption).foregroundStyle(item.startable && !MemoryFit.fits(weights: item.bytes) ? Color.orange : Color.secondary)
                    }
                    Spacer()
                    if model.server?.model.path == item.path { Button("Stop") { model.stopServer() } }
                    else if item.startable { Button("Start") { model.start(item) }.disabled(model.modelBusy != nil) }
                }
                Divider()
            }
        }
    }

    var discover: some View {
        Section2(title: "Find a model", note: "Search Hugging Face. Nothing is hidden: each result says what it is for and whether Mason can run it.") {
            HStack {
                TextField("Search Hugging Face, for example qwen coder", text: $model.searchText)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { model.search() }
                Button("Search") { model.search() }.disabled(model.modelBusy != nil || model.searchText.trimmingCharacters(in: .whitespaces).isEmpty)
                Button("Suggest for this Mac") { model.suggest() }.disabled(model.modelBusy != nil)
                    .help("Find coding models whose best build fits this Mac's memory")
            }
            ForEach(Array(model.suggestions.enumerated()), id: \.offset) { _, pick in
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("\(pick.repository.id) · \(pick.build.file)").fontWeight(.medium)
                        Text("\(pick.build.quant) is the highest-quality build that fits · \(ByteText.size(pick.build.bytes)) · license \(pick.repository.license.isEmpty ? "not stated" : pick.repository.license)")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Download and Start") { model.fetch(pick.build, from: pick.repository) }.disabled(model.download != nil)
                }
                Divider()
            }
            if let repository = model.repository {
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text(repository.id).font(.headline)
                        Text("license \(repository.license.isEmpty ? "not stated" : repository.license)" + (repository.gated ? " · access terms apply" : "")).font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button("Back to Results") { model.repository = nil }
                    }
                    if repository.builds.isEmpty { Text("This repository has no single-file GGUF builds to download.").foregroundStyle(.secondary) }
                    ForEach(repository.builds, id: \.file) { build in
                        HStack {
                            Text(build.file).lineLimit(1).truncationMode(.middle)
                            Spacer()
                            Text(ByteText.size(build.bytes)).foregroundStyle(.secondary)
                            if build.isProjector { Text("vision companion").foregroundStyle(.secondary) }
                            else if MemoryFit.fits(weights: build.bytes) { Text("fits").foregroundStyle(.green) }
                            else { Text("needs \(ByteText.size(MemoryFit.neededBytes(weights: build.bytes)))").foregroundStyle(.orange) }
                            if build == repository.suggested() { Text("suggested").font(.caption).padding(.horizontal, 6).background(Color.accentColor.opacity(0.2), in: Capsule()) }
                            Button("Download") { model.fetch(build, from: repository) }.disabled(model.download != nil)
                        }
                        .font(.callout)
                    }
                }
            } else {
                ForEach(model.searchResults.prefix(40), id: \.id) { result in
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(result.id).fontWeight(result.isGGUF ? .medium : .regular)
                            Text(result.verdict).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Text("\(result.downloads) downloads").font(.caption).foregroundStyle(.secondary)
                        Button("Builds") { model.inspect(result) }.disabled(model.modelBusy != nil || !result.isGGUF)
                            .help(result.isGGUF ? "See this repository's builds and which fit" : "Mason runs GGUF builds; look for one of this model")
                    }
                    Divider()
                }
            }
        }
    }
}

/// A titled group with a one-line explanation, used for each part of the Models page.
struct Section2<Content: View>: View {
    let title: String
    let note: String
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.title3).fontWeight(.semibold)
                Text(note).font(.callout).foregroundStyle(.secondary)
            }
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
