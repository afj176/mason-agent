import XCTest
@testable import MasonCore

/// Writes GGUF headers the way llama.cpp lays them out, so the reader is tested against real bytes.
struct GgufHeader {
    enum Value {
        case u32(UInt32), i32(Int32), f32(Float), f64(Double), bool(Bool), text(String), texts([String]), ints([Int32])
    }
    var entries: [(String, Value)]
    var magic: UInt32 = 0x4655_4747
    var version: UInt32 = 3

    func bytes() -> Data {
        var data = Data()
        func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) } }
        func u64(_ v: UInt64) { withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) } }
        func text(_ s: String) { u64(UInt64(s.utf8.count)); data.append(contentsOf: Array(s.utf8)) }
        u32(magic); u32(version); u64(1); u64(UInt64(entries.count))
        for (key, value) in entries {
            text(key)
            switch value {
            case .u32(let v): u32(4); u32(v)
            case .i32(let v): u32(5); u32(UInt32(bitPattern: v))
            case .f32(let v): u32(6); u32(v.bitPattern)
            case .f64(let v): u32(12); u64(v.bitPattern)
            case .bool(let v): u32(7); data.append(v ? 1 : 0)
            case .text(let v): u32(8); text(v)
            case .texts(let list): u32(9); u32(8); u64(UInt64(list.count)); list.forEach(text)
            case .ints(let list): u32(9); u32(5); u64(UInt64(list.count)); list.forEach { u32(UInt32(bitPattern: $0)) }
            }
        }
        return data
    }
}

final class ModelMemoryTests: XCTestCase {
    var folder: URL!
    let gib = 1_073_741_824.0

    override func setUpWithError() throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("mason-memory-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: folder) }

    /// A GGUF file with the header and `padding` bytes standing in for tensor data.
    @discardableResult
    func write(_ name: String, _ header: GgufHeader, padding: Int = 4096) throws -> URL {
        let file = folder.appendingPathComponent(name)
        try (header.bytes() + Data(count: padding)).write(to: file)
        return file
    }
    func size(_ file: URL) -> Double { Double((try? FileManager.default.attributesOfItem(atPath: file.path))?[.size] as? NSNumber ?? 0) }

    func llama(context: UInt32 = 32768, architecture: String = "llama", extra: [(String, GgufHeader.Value)] = []) -> GgufHeader {
        GgufHeader(entries: [
            ("general.architecture", .text(architecture)),
            ("general.size_label", .text("7.6B")),
            ("tokenizer.ggml.tokens", .texts((0..<2000).map { "token\($0)" })),
            ("tokenizer.ggml.token_type", .ints(Array(repeating: 1, count: 2000))),
            ("\(architecture).block_count", .u32(32)),
            ("\(architecture).attention.head_count", .u32(32)),
            ("\(architecture).attention.head_count_kv", .u32(8)),
            ("\(architecture).embedding_length", .u32(4096)),
            ("\(architecture).context_length", .u32(context)),
            ("\(architecture).rope.freq_base", .f32(1_000_000)),
            ("general.flag", .bool(true)),
            ("general.ratio", .f64(0.5)),
        ] + extra)
    }

    // MARK: GGUF

    func testGgufKvPerTokenFromHeads() throws {
        let file = try write("model-Q4_K_M.gguf", llama(), padding: 2 * 1024 * 1024)
        let profile = ModelMemory.readGguf(file)
        XCTAssertNil(profile.unknownReason)
        XCTAssertEqual(profile.architecture, "llama")
        XCTAssertEqual(profile.sizeLabel, "7.6B")
        // 32 layers × 8 KV heads × (128 + 128) × 2 bytes: head size is the embedding over the heads.
        XCTAssertEqual(profile.kvBytesPerToken, 32 * 8 * 256 * 2)
        XCTAssertEqual(profile.weightBytes, size(file))
        XCTAssertEqual(profile.workspaceBytes, 0.75 * gib)
        XCTAssertEqual(profile.workspacePerToken, 0)
        XCTAssertEqual(profile.maxContext, 32768)
        XCTAssertTrue(profile.contextDeclared)
    }

    func testGgufExplicitKeyAndValueLengths() throws {
        let file = try write("m.gguf", llama(architecture: "qwen3", extra: [("qwen3.attention.key_length", .u32(128)), ("qwen3.attention.value_length", .u32(64))]))
        XCTAssertEqual(ModelMemory.readGguf(file).kvBytesPerToken, 32 * 8 * (128 + 64) * 2)
    }

    func testUnsupportedArchitectureIsUnknownButKeepsMaxContext() throws {
        let file = try write("inkling.gguf", GgufHeader(entries: [("general.architecture", .text("inkling2_loop")), ("inkling2_loop.context_length", .u32(40960))]), padding: 1_000_000)
        let profile = ModelMemory.readGguf(file)
        XCTAssertEqual(profile.unknownReason, "This GGUF architecture needs a runtime-specific memory estimate.")
        XCTAssertEqual(profile.maxContext, 40960)
        XCTAssertTrue(profile.contextDeclared)
        XCTAssertEqual(profile.weightBytes, size(file))
        XCTAssertEqual(profile.architecture, "inkling2_loop")
    }

    func testUndeclaredContextAndBadHeaders() throws {
        var header = llama()
        header.entries.removeAll { $0.0 == "llama.context_length" }
        let undeclared = ModelMemory.readGguf(try write("undeclared.gguf", header))
        XCTAssertFalse(undeclared.contextDeclared)
        XCTAssertEqual(undeclared.maxContext, 4096)
        XCTAssertEqual(undeclared.capacity, 32768)
        XCTAssertEqual(ModelMemory.ladder(undeclared), [2048, 4096, 8192, 16384, 32768])

        var wrongMagic = llama(); wrongMagic.magic = 0x1234_5678
        let bad = ModelMemory.readGguf(try write("bad.gguf", wrongMagic))
        XCTAssertEqual(bad.unknownReason, ModelMemory.unreadable)
        XCTAssertEqual(bad.weightBytes, 0)
        var wrongVersion = llama(); wrongVersion.version = 1
        XCTAssertEqual(ModelMemory.readGguf(try write("v1.gguf", wrongVersion)).unknownReason, ModelMemory.unreadable)
        // A header cut short reads as unreadable, not as a crash.
        let whole = llama().bytes()
        let cut = folder.appendingPathComponent("cut.gguf")
        try whole.prefix(whole.count / 2).write(to: cut)
        XCTAssertEqual(ModelMemory.readGguf(cut).unknownReason, ModelMemory.unreadable)

        var missing = llama()
        missing.entries.removeAll { $0.0 == "llama.block_count" }
        XCTAssertEqual(ModelMemory.readGguf(try write("missing.gguf", missing)).unknownReason, "GGUF attention metadata is insufficient for a fit estimate.")
    }

    func qwen35(layers: UInt32 = 40, mtp: UInt32 = 0, inner: UInt32 = 4096, extra: [(String, GgufHeader.Value)] = []) -> GgufHeader {
        var entries: [(String, GgufHeader.Value)] = [
            ("general.architecture", .text("qwen35")),
            ("qwen35.block_count", .u32(layers)),
            ("qwen35.context_length", .u32(262144)),
            ("qwen35.attention.head_count", .u32(16)),
            ("qwen35.attention.head_count_kv", .u32(2)),
            ("qwen35.attention.key_length", .u32(256)),
            ("qwen35.attention.value_length", .u32(256)),
            ("qwen35.full_attention_interval", .u32(4)),
            ("qwen35.ssm.state_size", .u32(128)),
            ("qwen35.ssm.inner_size", .u32(inner)),
            ("qwen35.ssm.group_count", .u32(16)),
            ("qwen35.ssm.conv_kernel", .u32(4)),
            ("qwen35.ssm.time_step_rank", .u32(32)),
        ]
        if mtp > 0 { entries.append(("qwen35.nextn_predict_layers", .u32(mtp))) }
        return GgufHeader(entries: entries + extra)
    }

    func testQwenHybridCountsOnlyAttentionLayersAndAddsRecurrentState() throws {
        let profile = ModelMemory.readGguf(try write("hybrid.gguf", qwen35()))
        XCTAssertNil(profile.unknownReason)
        // 40 layers, every 4th full attention: 10 attention layers keep a KV cache, 30 recurrent layers keep state.
        XCTAssertEqual(profile.kvBytesPerToken, 10 * 2 * (256 + 256) * 2)
        let perLayer: Double = 128 * 4096 + 3 * (4096 + 2 * 16 * 128)
        let state: Double = 30 * perLayer * 8
        XCTAssertEqual(profile.workspaceBytes, 0.75 * gib + state)
        XCTAssertEqual(profile.maxContext, 262144)
        XCTAssertTrue(profile.basis.hasPrefix("Qwen hybrid: 10 full-attention + 30 recurrent layers"))

        // Prediction blocks sit outside the trunk and are budgeted as attention layers.
        let predicting = ModelMemory.readGguf(try write("mtp.gguf", qwen35(layers: 41, mtp: 1)))
        XCTAssertEqual(predicting.kvBytesPerToken, 11 * 2 * (256 + 256) * 2)

        let inconsistent = ModelMemory.readGguf(try write("odd.gguf", qwen35(inner: 4000)))
        XCTAssertEqual(inconsistent.unknownReason, "Qwen hybrid metadata is missing or outside the supported estimate.")
        XCTAssertEqual(inconsistent.maxContext, 262144)
        let custom = ModelMemory.readGguf(try write("custom.gguf", qwen35(extra: [("qwen35.attention.recurrent_layers", .ints([1, 2, 3]))])))
        XCTAssertEqual(custom.unknownReason, "Custom recurrent-layer maps need a dedicated estimate.")
    }

    func testSplitGgufWeighsEveryPart() throws {
        let first = try write("big-00001-of-00003.gguf", llama(), padding: 100_000)
        let second = folder.appendingPathComponent("big-00002-of-00003.gguf"), third = folder.appendingPathComponent("big-00003-of-00003.gguf")
        try Data(count: 300_000).write(to: second)
        try Data(count: 500_000).write(to: third)
        XCTAssertEqual(ModelMemory.parts(of: first).map(\.lastPathComponent), ["big-00001-of-00003.gguf", "big-00002-of-00003.gguf", "big-00003-of-00003.gguf"])
        XCTAssertEqual(ModelMemory.readGguf(first).weightBytes, size(first) + 300_000 + 500_000)
        let model = LocalModel(name: "big-00001-of-00003", path: first.path, bytes: 0, source: "test")
        XCTAssertEqual(model.trashItems.count, 3)
    }

    func testSafetensorsFolder() throws {
        let model = folder.appendingPathComponent("tiny")
        try FileManager.default.createDirectory(at: model, withIntermediateDirectories: true)
        let config: [String: Any] = ["model_type": "llama", "num_hidden_layers": 4, "num_attention_heads": 8, "num_key_value_heads": 2, "hidden_size": 512,
                                     "max_position_embeddings": 8192, "vocab_size": 1000, "intermediate_size": 1024]
        try JSONSerialization.data(withJSONObject: config).write(to: model.appendingPathComponent("config.json"))
        let header = try JSONSerialization.data(withJSONObject: ["w": ["dtype": "BF16", "shape": [256, 256], "data_offsets": [0, 131072]], "__metadata__": ["format": "pt"]])
        var file = Data()
        withUnsafeBytes(of: UInt64(header.count).littleEndian) { file.append(contentsOf: $0) }
        file.append(header); file.append(Data(count: 131072))
        try file.write(to: model.appendingPathComponent("model.safetensors"))
        let profile = ModelMemory.readSafetensors(model)
        XCTAssertNil(profile.unknownReason)
        XCTAssertEqual(profile.weightBytes, 256 * 256 * 2)
        XCTAssertEqual(profile.kvBytesPerToken, 2 * 4 * 2 * 64 * 2)
        XCTAssertEqual(profile.workspacePerToken, 512 * 12 + 1000 * 4)
        XCTAssertEqual(profile.maxContext, 8192)

        try JSONSerialization.data(withJSONObject: ["model_type": "mixtral"]).write(to: model.appendingPathComponent("config.json"))
        XCTAssertEqual(ModelMemory.readSafetensors(model).unknownReason, "Encoder-decoder and expert-model peaks need a runtime-specific estimate.")
    }

    // MARK: fit

    /// 4 GiB of weights, 128 KiB of KV cache per token (1 GiB at 8K) and 0.75 GiB of workspace.
    let profile = ModelMemoryProfile(weightBytes: 4 * 1_073_741_824.0, kvBytesPerToken: 131072, workspaceBytes: 0.75 * 1_073_741_824.0, maxContext: 32768, architecture: "qwen2")
    /// 8 GiB free of 16: 0.8 GiB is kept spare.
    let memory = MemoryReading(freeBytes: 8 * 1_073_741_824.0, totalBytes: 16 * 1_073_741_824.0, label: "Unified memory")

    func testAssessFitsTightAndTooLarge() {
        let fits = ModelMemory.assess(profile, memory: memory, context: 8192)
        XCTAssertEqual(fits.state, .likelyFits)
        XCTAssertEqual(fits.lowGiB!, 5.75, accuracy: 1e-9)
        XCTAssertEqual(fits.highGiB!, 4 * 1.12 + 1.15 + 0.75 * 1.5, accuracy: 1e-9)
        XCTAssertEqual(fits.spareGiB!, 0.8, accuracy: 1e-9)
        XCTAssertEqual(ModelMemory.card(fits, step: 8192, architecture: "qwen2", cpu: false, running: false).detail, "5.8–6.8 GiB at 8K · 0.8 GiB kept spare. An estimate, not a guarantee.")
        XCTAssertEqual(ModelMemory.accessibleName(fits, step: 8192), "8,192 context tokens, 5.8–6.8 GiB, Likely fits")
        XCTAssertEqual(ModelMemory.stepMemory(fits), "5.8 GiB")

        let tight = ModelMemory.assess(profile, memory: memory, context: 16384)
        XCTAssertEqual(tight.state, .tight)
        XCTAssertEqual(ModelMemory.card(tight, step: 16384, architecture: "qwen2", cpu: false, running: false).detail,
                       "6.8–7.9 GiB at 16K · other apps could push it over. Try a smaller context or close apps using the GPU.")

        let over = ModelMemory.assess(profile, memory: memory, context: 32768)
        XCTAssertEqual(over.state, .tooLarge)
        XCTAssertFalse(over.lowerBound)
        let overCard = ModelMemory.card(over, step: 32768, architecture: "qwen2", cpu: false, running: false)
        XCTAssertEqual(overCard.title, "Too large for free memory")
        XCTAssertEqual(overCard.detail, "8.8–10.2 GiB at 32K · more than the 8.0 GiB free.")

        // A context beyond the model's capacity is estimated at the capacity.
        XCTAssertEqual(ModelMemory.assess(profile, memory: memory, context: 65536).tokens, 32768)
        // The spare is 5% of the total once that is more than half a GiB.
        XCTAssertEqual(ModelMemory.assess(profile, memory: MemoryReading(freeBytes: 4 * gib, totalBytes: 8 * gib, label: "x"), context: 2048).spareGiB!, 0.5, accuracy: 1e-9)
    }

    func testUnknownEstimateIsLowerBoundOrUnverified() {
        let large = ModelMemoryProfile(weightBytes: 10 * gib, kvBytesPerToken: 0, workspaceBytes: 0, maxContext: 40960, unknownReason: ModelMemory.unsupportedArchitecture, architecture: "inkling2_loop")
        let fit = ModelMemory.assess(large, memory: memory, context: 4096)
        XCTAssertEqual(fit.state, .tooLarge)
        XCTAssertTrue(fit.lowerBound)
        XCTAssertNil(fit.lowGiB)
        XCTAssertEqual(ModelMemory.stepMemory(fit), "10.0+ GiB")
        XCTAssertEqual(ModelMemory.card(fit, step: 4096, architecture: "inkling2_loop", cpu: false, running: false).detail,
                       "The weights alone are 10.0 GiB and 8.0 GiB is free. Context memory for inkling2_loop is not estimated yet.")
        XCTAssertEqual(ModelMemory.recommend(large, memory: memory), ContextRecommendation(gpu: false, context: 2048, text: "Suggested: CPU · weights exceed free memory"))
        XCTAssertEqual(ModelMemory.summary(large, memory: memory, role: .coding), FitSummary(marks: [("✕", .tooLarge)], note: "weights alone exceed free memory"))

        let small = ModelMemoryProfile(weightBytes: 2 * gib, kvBytesPerToken: 0, workspaceBytes: 0, maxContext: 40960, unknownReason: ModelMemory.unsupportedArchitecture, architecture: "inkling2_loop")
        let unverified = ModelMemory.assess(small, memory: memory, context: 4096)
        XCTAssertEqual(unverified.state, .unverified)
        XCTAssertEqual(ModelMemory.card(unverified, step: 4096, architecture: "inkling2_loop", cpu: false, running: false).detail,
                       "The weights are 2.0 GiB. Context memory for inkling2_loop is not estimated, so fit is checked when it starts.")
        XCTAssertEqual(ModelMemory.recommend(small, memory: memory), ContextRecommendation(gpu: true, context: 2048, text: "Suggested starting point: GPU · 2K · fit unverified"))
        XCTAssertEqual(ModelMemory.summary(small, memory: memory, role: .coding).note, "fit unverified")
        XCTAssertEqual(ModelMemory.ladder(small), [2048, 4096, 8192, 16384, 32768, 40960])
    }

    func testWaitingWithoutAReading() {
        let fit = ModelMemory.assess(profile, memory: nil, context: 8192)
        XCTAssertEqual(fit.state, .waiting)
        XCTAssertEqual(ModelMemory.stepMemory(fit), "— GiB")
        XCTAssertEqual(ModelMemory.assess(profile, memory: MemoryReading(freeBytes: 0, totalBytes: 0, label: "x"), context: 8192).state, .waiting)
    }

    func testCardForCpuAndRunning() {
        let fit = ModelMemory.assess(profile, memory: memory, context: 8192)
        XCTAssertEqual(ModelMemory.card(fit, step: 8192, architecture: "qwen2", cpu: true, running: false).detail, "Runs from system RAM. Available RAM is not checked here, and CPU turns are much slower.")
        XCTAssertEqual(ModelMemory.card(fit, step: 8192, architecture: "qwen2", cpu: false, running: true).title, "Running · memory is already in use")
    }

    // MARK: ladder and recommendation

    func testLadderSteps() {
        func steps(_ max: Int) -> [Int] { ModelMemory.ladder(ModelMemoryProfile(weightBytes: 1, kvBytesPerToken: 1, workspaceBytes: 0, maxContext: max)) }
        XCTAssertEqual(steps(32768), [2048, 4096, 8192, 16384, 32768])
        XCTAssertEqual(steps(40960), [2048, 4096, 8192, 16384, 32768, 40960])
        XCTAssertEqual(steps(40960).map(ModelMemory.label), ["2K", "4K", "8K", "16K", "32K", "40K"])
        // More than six steps: the smallest are dropped.
        XCTAssertEqual(steps(131072), [4096, 8192, 16384, 32768, 65536, 131072])
        XCTAssertEqual(steps(262144), [8192, 16384, 32768, 65536, 131072, 262144])
        XCTAssertEqual(ModelMemory.label(131072), "128K")
        XCTAssertEqual(ModelMemory.label(128000), "128K")
        XCTAssertEqual(ModelMemory.label(32000), "32K")
        XCTAssertEqual(ModelMemory.label(16384), "16K")
        XCTAssertEqual(ModelMemory.label(2048), "2K")
        XCTAssertEqual(ModelMemory.label(512), "512")
        XCTAssertEqual(steps(128000), [4096, 8192, 16384, 32768, 65536, 128000])
        XCTAssertEqual(steps(128000).map(ModelMemory.label).last, "128K")
        XCTAssertEqual(steps(1024), [1024])
        XCTAssertEqual(steps(3000), [2048, 3000])
    }

    func testRecommend() {
        XCTAssertEqual(ModelMemory.recommend(profile, memory: memory), ContextRecommendation(gpu: true, context: 8192, text: "Suggested: 8K on GPU · largest likely fit"))
        // Room for everything: the suggestion stops at 32K even when the model goes further.
        let roomy = MemoryReading(freeBytes: 64 * gib, totalBytes: 128 * gib, label: "Unified memory")
        var long = profile; long.maxContext = 131072
        XCTAssertEqual(ModelMemory.recommend(long, memory: roomy).context, 32768)
        // Weights fit but even a small context is tight: the CPU, because the GPU has little headroom.
        let heavy = ModelMemoryProfile(weightBytes: 6 * gib, kvBytesPerToken: 131072, workspaceBytes: 0.75 * gib, maxContext: 32768)
        XCTAssertEqual(ModelMemory.assess(heavy, memory: memory, context: 512).state, .tight)
        XCTAssertEqual(ModelMemory.recommend(heavy, memory: memory), ContextRecommendation(gpu: false, context: 2048, text: "Suggested: CPU · GPU headroom is limited"))
        // Weights larger than free memory.
        let huge = ModelMemoryProfile(weightBytes: 9 * gib, kvBytesPerToken: 131072, workspaceBytes: 0.75 * gib, maxContext: 32768)
        XCTAssertEqual(ModelMemory.recommend(huge, memory: memory).text, "Suggested: CPU · weights exceed free memory")
        // No reading: a starting point whose fit is unverified.
        XCTAssertEqual(ModelMemory.recommend(profile, memory: nil), ContextRecommendation(gpu: true, context: 2048, text: "Suggested starting point: GPU · 2K · fit unverified"))
    }

    func testListSummary() {
        let summary = ModelMemory.summary(profile, memory: memory, role: .coding)
        XCTAssertEqual(summary.symbols, "●●●◐○")
        XCTAssertEqual(summary.note, "up to 8K")
        let heavy = ModelMemoryProfile(weightBytes: 6 * gib, kvBytesPerToken: 131072, workspaceBytes: 0.75 * gib, maxContext: 32768)
        XCTAssertEqual(ModelMemory.summary(heavy, memory: memory, role: .coding).note, "tight at 2K")
        let huge = ModelMemoryProfile(weightBytes: 9 * gib, kvBytesPerToken: 131072, workspaceBytes: 0.75 * gib, maxContext: 32768)
        let none = ModelMemory.summary(huge, memory: memory, role: .coding)
        XCTAssertEqual(none.symbols, "✕")
        XCTAssertEqual(none.note, "needs \(ModelMemory.one(ModelMemory.assess(huge, memory: memory, context: 2048).lowGiB!))+ GiB · try CPU")
        XCTAssertEqual(ModelMemory.summary(profile, memory: memory, role: .embeddings).note, "fits")
        XCTAssertEqual(ModelMemory.summary(profile, memory: memory, role: .projector).note, "vision companion")
        // A model that declares 8K gets three dots.
        var short = profile; short.maxContext = 8192
        XCTAssertEqual(ModelMemory.summary(short, memory: memory, role: .coding).symbols, "●●●")
    }

    // MARK: words

    func testParseContextQuantizationAndRepository() {
        XCTAssertEqual(ModelMemory.parseContext("16384"), 16384)
        XCTAssertEqual(ModelMemory.parseContext("16k"), 16384)
        XCTAssertEqual(ModelMemory.parseContext("16K"), 16384)
        XCTAssertNil(ModelMemory.parseContext("sixteen"))
        XCTAssertNil(ModelMemory.parseContext("k"))
        XCTAssertEqual(ModelMemory.quantization("Qwen2.5-Coder-7B-Instruct-Q4_K_M"), "Q4_K_M")
        XCTAssertEqual(ModelMemory.quantization("granite-IQ4_XS-00001-of-00002"), "IQ4_XS")
        XCTAssertEqual(ModelMemory.quantization("model.bf16"), "BF16")
        XCTAssertEqual(ModelMemory.quantization("qwen2.5-coder-7b"), "")
        XCTAssertEqual(ModelMemory.grouped(32768), "32,768")
        XCTAssertEqual(ModelMemory.grouped(262144), "262,144")
        XCTAssertEqual(ModelMemory.grouped(512), "512")

        let cached = LocalModel(name: "qwen2.5-coder-7b-instruct-q4_k_m", path: "/Users/me/.cache/huggingface/hub/models--Qwen--Qwen2.5-Coder-7B-Instruct-GGUF/snapshots/abc/qwen2.5-coder-7b-instruct-q4_k_m.gguf", bytes: 1, source: "Hugging Face cache")
        XCTAssertEqual(cached.repository, "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF")
        let downloaded = LocalModel(name: "m", path: ModelLibrary.downloadFolder.appendingPathComponent("bartowski--Some-GGUF/m.gguf").path, bytes: 1, source: "Mason downloads")
        XCTAssertEqual(downloaded.repository, "bartowski/Some-GGUF")
        XCTAssertNil(LocalModel(name: "m", path: "/tmp/models/m.gguf", bytes: 1, source: "Folder").repository)
        XCTAssertEqual(cached.quantization, "Q4_K_M")
        XCTAssertEqual(cached.roleLabel, "Coding agent")
    }

    func testExplanationMentionsCapacityAndBasis() {
        let text = ModelMemory.explanation(profile)
        XCTAssertTrue(text.hasPrefix("Model capacity: 32,768 tokens. "))
        var unknown = profile; unknown.unknownReason = ModelMemory.unsupportedArchitecture; unknown.contextDeclared = false
        XCTAssertTrue(ModelMemory.explanation(unknown).hasPrefix("Model capacity unavailable. This GGUF architecture needs a runtime-specific memory estimate. Based on"))
    }
}
