import Foundation

// How much memory a model needs at a given context length, read from its metadata only (never its tensor data),
// and whether that fits the memory free now. The app's Ladder panel and the command line's /models flow both use
// it, so the numbers and words are the same in both. The maths follows Mason for Windows (ModelMemory.cs).

/// What a model needs: its weights, the KV cache per token of context and the runtime's working memory.
public struct ModelMemoryProfile: Equatable {
    public var weightBytes: Double
    public var kvBytesPerToken: Double
    public var workspaceBytes: Double
    public var workspacePerToken: Double
    /// The model's declared context capacity, or 4096 when it declares none.
    public var maxContext: Int
    public var contextDeclared: Bool
    /// Why there is no estimate of context memory. The weights may still be known.
    public var unknownReason: String?
    public var architecture: String
    /// How the estimate was made, for the explanation.
    public var basis: String
    /// The parameter count the file declares, such as "7.6B"; empty when it says nothing.
    public var sizeLabel: String

    public init(weightBytes: Double, kvBytesPerToken: Double, workspaceBytes: Double, workspacePerToken: Double = 0, maxContext: Int, contextDeclared: Bool = true,
                unknownReason: String? = nil, architecture: String = "", basis: String = "", sizeLabel: String = "") {
        self.weightBytes = weightBytes; self.kvBytesPerToken = kvBytesPerToken; self.workspaceBytes = workspaceBytes; self.workspacePerToken = workspacePerToken
        self.maxContext = maxContext; self.contextDeclared = contextDeclared; self.unknownReason = unknownReason; self.architecture = architecture
        self.basis = basis; self.sizeLabel = sizeLabel
    }

    public var isUnknown: Bool { unknownReason != nil }
    /// The context the ladder and estimates go up to: the declared capacity, or 32K when the model declares none.
    public var capacity: Int { contextDeclared ? maxContext : 32768 }
}

/// Memory free for a model now. On a Mac the CPU and GPU share memory: free is the budget Mason keeps for a model.
public struct MemoryReading: Equatable {
    public var freeBytes: Double
    public var totalBytes: Double
    public var label: String
    public init(freeBytes: Double, totalBytes: Double, label: String) { self.freeBytes = freeBytes; self.totalBytes = totalBytes; self.label = label }

    public var freeGiB: Double { freeBytes / ModelMemory.gib }
    public var totalGiB: Double { totalBytes / ModelMemory.gib }
    public var usable: Bool { freeBytes > 0 && totalBytes > 0 && freeBytes <= totalBytes }
    /// The device line in the panel, such as "Unified memory · 19.2 GiB free".
    public var deviceLabel: String { "\(label) · \(ModelMemory.one(freeGiB)) GiB free" }

    /// This Mac's reading: the memory budget Mason uses for a model, out of the physical memory.
    public static func unified() -> MemoryReading {
        MemoryReading(freeBytes: Double(MemoryFit.budgetBytes), totalBytes: Double(ProcessInfo.processInfo.physicalMemory), label: "Unified memory")
    }
}

public enum FitState: String, Equatable {
    case likelyFits = "Likely fits"
    case tight = "Tight fit"
    case tooLarge = "Too large for free memory"
    case unverified = "Fit unverified"
    case waiting = "Waiting for a memory reading"
}

/// One context length judged against the memory free now.
public struct FitAssessment: Equatable {
    public var state: FitState
    /// The context the estimate is for, after the model's capacity is applied.
    public var tokens: Int
    /// nil when context memory is not estimated for this model.
    public var lowGiB: Double?
    public var highGiB: Double?
    public var weightsGiB: Double
    /// nil without a usable memory reading.
    public var freeGiB: Double?
    public var spareGiB: Double?
    /// True when "too large" was judged from the weights alone, because context memory is not estimated.
    public var lowerBound: Bool
}

public struct ContextRecommendation: Equatable {
    public var gpu: Bool
    public var context: Int
    public var text: String
}

/// How the fit column of the command line's model list reads for one model.
public struct FitSummary: Equatable {
    /// Symbols in order, each with the fit it shows (nil for a neutral mark).
    public var marks: [(symbol: String, state: FitState?)]
    public var note: String
    public init(marks: [(symbol: String, state: FitState?)], note: String) { self.marks = marks; self.note = note }
    public static func == (a: FitSummary, b: FitSummary) -> Bool {
        a.note == b.note && a.marks.map(\.symbol) == b.marks.map(\.symbol) && a.marks.map(\.state) == b.marks.map(\.state)
    }
    public var symbols: String { marks.map(\.symbol).joined() }
}

public enum ModelMemory {
    public static let gib = 1_073_741_824.0
    static let headerLimit: Int64 = 16 * 1024 * 1024
    public static let candidates = [2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144]
    public static let unreadable = "Model metadata could not be read safely. Check files and compatibility; no fit claim is made."
    public static let unsupportedArchitecture = "This GGUF architecture needs a runtime-specific memory estimate."
    static let architectures: Set<String> = ["qwen35", "llama", "qwen2", "qwen3", "gemma", "gemma2", "gemma3", "phi2", "phi3", "mistral", "gptneox", "gpt2", "qwen2moe", "qwen3moe"]

    // MARK: words and numbers

    /// One decimal, as the panel and the command line show GiB.
    public static func one(_ value: Double) -> String { String(format: "%.1f", value) }
    /// A whole number with thousands separators, such as 32,768.
    public static func grouped(_ value: Int) -> String {
        let digits = String(abs(value))
        var out = ""
        for (index, character) in digits.enumerated() {
            if index > 0, (digits.count - index) % 3 == 0 { out.append(",") }
            out.append(character)
        }
        return (value < 0 ? "-" : "") + out
    }
    /// A context length as the ladder labels it: under 1000 as is; whole thousands as thousands (128000 → "128K",
    /// 32000 → "32K"); otherwise in units of 1024 (2048 → "2K", 40960 → "40K", 131072 → "128K").
    public static func label(_ tokens: Int) -> String {
        if tokens < 1000 { return String(tokens) }
        if tokens % 1000 == 0 { return "\(tokens / 1000)K" }
        return "\(Int((Double(tokens) / 1024).rounded()))K"
    }

    /// A context size as typed: 16384, 16k or 16K. nil for anything else.
    public static func parseContext(_ text: String) -> Int? {
        let word = text.trimmingCharacters(in: .whitespaces)
        if let tokens = Int(word), tokens > 0 { return tokens }
        guard let last = word.last, last == "k" || last == "K", let thousands = Int(word.dropLast()), thousands > 0, thousands <= 4096 else { return nil }
        return thousands * 1024
    }

    /// The quantisation named in a file name, such as Q4_K_M, IQ4_XS or BF16; empty when there is none.
    public static func quantization(_ name: String) -> String {
        guard let range = name.range(of: #"(?<![A-Za-z0-9])(I?Q[1-8](_[A-Za-z0-9]+)*|BF16|F16|F32)(?![A-Za-z0-9])"#, options: [.regularExpression, .caseInsensitive]) else { return "" }
        return name[range].uppercased()
    }

    // MARK: the ladder

    /// Every context step up to the model's capacity, plus the capacity itself when it is not a step.
    static func allSteps(_ profile: ModelMemoryProfile) -> [Int] {
        let cap = profile.capacity
        var steps = candidates.filter { $0 <= cap }
        if !candidates.contains(cap), cap > (steps.last ?? 0) { steps.append(cap) }
        return steps
    }
    /// The steps the ladder shows: at most six, dropping the smallest first.
    public static func ladder(_ profile: ModelMemoryProfile) -> [Int] { Array(allSteps(profile).suffix(6)) }

    // MARK: estimate

    /// Judges one context length against the memory free now.
    public static func assess(_ profile: ModelMemoryProfile, memory: MemoryReading?, context: Int) -> FitAssessment {
        let tokens = max(1, min(context, profile.capacity))
        let weights = profile.weightBytes / gib
        var low: Double?, high: Double?
        if !profile.isUnknown {
            let cache = profile.kvBytesPerToken * Double(tokens)
            let work = profile.workspaceBytes + profile.workspacePerToken * Double(tokens)
            low = (profile.weightBytes + cache + work) / gib
            high = (profile.weightBytes * 1.12 + cache * 1.15 + work * 1.5) / gib
        }
        guard let memory, memory.usable else {
            return FitAssessment(state: .waiting, tokens: tokens, lowGiB: low, highGiB: high, weightsGiB: weights, freeGiB: nil, spareGiB: nil, lowerBound: false)
        }
        let free = memory.freeGiB, spare = max(0.5, memory.totalGiB * 0.05)
        guard let low, let high else {
            let over = weights + spare > free
            return FitAssessment(state: over ? .tooLarge : .unverified, tokens: tokens, lowGiB: nil, highGiB: nil, weightsGiB: weights, freeGiB: free, spareGiB: spare, lowerBound: over)
        }
        let state: FitState = high + spare <= free ? .likelyFits : low + spare <= free ? .tight : .tooLarge
        return FitAssessment(state: state, tokens: tokens, lowGiB: low, highGiB: high, weightsGiB: weights, freeGiB: free, spareGiB: spare, lowerBound: false)
    }

    /// The context and device to start with: the largest step up to 32K that likely fits, else the CPU when even a
    /// small context is tight, else a 2K starting point whose fit is checked when it starts.
    public static func recommend(_ profile: ModelMemoryProfile, memory: MemoryReading?) -> ContextRecommendation {
        let cap = max(64, min(profile.capacity, 32768))
        for step in allSteps(profile).filter({ $0 <= cap }).reversed() where assess(profile, memory: memory, context: step).state == .likelyFits {
            return ContextRecommendation(gpu: true, context: step, text: "Suggested: \(label(step)) on GPU · largest likely fit")
        }
        let start = min(2048, cap)
        let small = assess(profile, memory: memory, context: min(512, cap))
        if small.state == .tight || small.state == .tooLarge {
            let weightsAlone = small.lowerBound || (small.spareGiB.map { small.weightsGiB + $0 } ?? 0) > (small.freeGiB ?? .infinity)
            return ContextRecommendation(gpu: false, context: start, text: "Suggested: CPU · " + (weightsAlone ? "weights exceed free memory" : "GPU headroom is limited"))
        }
        return ContextRecommendation(gpu: true, context: start, text: "Suggested starting point: GPU · \(label(start)) · fit unverified")
    }

    /// The memory text under one ladder step: the low estimate, the weights as a lower bound, or a dash without a reading.
    public static func stepMemory(_ fit: FitAssessment) -> String {
        if fit.state == .waiting { return "— GiB" }
        if let low = fit.lowGiB { return "\(one(low)) GiB" }
        return fit.weightsGiB > 0 ? "\(one(fit.weightsGiB))+ GiB" : "— GiB"
    }

    /// What assistive technology reads for one ladder step.
    public static func accessibleName(_ fit: FitAssessment, step: Int) -> String {
        let range: String
        if let low = fit.lowGiB, let high = fit.highGiB { range = "\(one(low))–\(one(high)) GiB" }
        else if fit.weightsGiB > 0 { range = "\(one(fit.weightsGiB))+ GiB" }
        else { range = "memory not estimated" }
        return "\(grouped(step)) context tokens, \(range), \(fit.state.rawValue)"
    }

    /// The fit card's title and detail line for the chosen step.
    public static func card(_ fit: FitAssessment, step: Int, architecture: String, cpu: Bool, running: Bool) -> (title: String, detail: String) {
        if running { return ("Running · memory is already in use", "The free memory reading already includes this model. Stop it before estimating a new load.") }
        if cpu { return ("CPU · system RAM", "Runs from system RAM. Available RAM is not checked here, and CPU turns are much slower.") }
        let name = label(step), arch = architecture.isEmpty ? "this architecture" : architecture
        let range = fit.lowGiB.flatMap { low in fit.highGiB.map { "\(one(low))–\(one($0)) GiB" } } ?? ""
        switch fit.state {
        case .likelyFits: return (fit.state.rawValue, "\(range) at \(name) · \(one(fit.spareGiB ?? 0)) GiB kept spare. An estimate, not a guarantee.")
        case .tight: return (fit.state.rawValue, "\(range) at \(name) · other apps could push it over. Try a smaller context or close apps using the GPU.")
        case .tooLarge where fit.lowerBound:
            return (fit.state.rawValue, "The weights alone are \(one(fit.weightsGiB)) GiB and \(one(fit.freeGiB ?? 0)) GiB is free. Context memory for \(arch) is not estimated yet.")
        case .tooLarge: return (fit.state.rawValue, "\(range) at \(name) · more than the \(one(fit.freeGiB ?? 0)) GiB free.")
        case .unverified: return (fit.state.rawValue, "The weights are \(one(fit.weightsGiB)) GiB. Context memory for \(arch) is not estimated, so fit is checked when it starts.")
        case .waiting: return (fit.state.rawValue, range.isEmpty ? "Mason needs a reading of free memory to estimate the fit." : "\(range) at \(name) · Mason needs a reading of free memory to check the fit.")
        }
    }

    /// How the estimate works, for the "?" button.
    public static func explanation(_ profile: ModelMemoryProfile) -> String {
        let capacity = profile.contextDeclared ? "Model capacity: \(grouped(profile.maxContext)) tokens. " : "Model capacity unavailable. "
        var basis = profile.unknownReason ?? (profile.basis.isEmpty ? "Metadata unavailable" : profile.basis)
        while basis.hasSuffix(".") { basis.removeLast() }
        return capacity + basis + ". Based on metadata from the last scan and the memory Mason keeps for a model on this Mac: 60% of physical memory, which the CPU and GPU share. "
            + "Estimates include runtime headroom, not model quality or speed. Other apps can change free memory before Start. One request at a time. "
            + "Context includes prompt, reasoning and output. Restart the model to apply a different loaded window."
    }

    /// The command line list's fit column: a dot per step from 2K to 32K, or a single mark, and a short note.
    public static func summary(_ profile: ModelMemoryProfile, memory: MemoryReading?, role: LocalModel.Role) -> FitSummary {
        switch role {
        case .projector: return FitSummary(marks: [("─", nil)], note: "vision companion")
        case .embeddings, .reranker:
            let fit = assess(profile, memory: memory, context: 2048)
            if fit.state == .waiting { return FitSummary(marks: [("?", nil)], note: "waiting for a memory reading") }
            let over = fit.weightsGiB + (fit.spareGiB ?? 0) > (fit.freeGiB ?? 0)
            return over ? FitSummary(marks: [("✕", .tooLarge)], note: "weights alone exceed free memory") : FitSummary(marks: [("●", .likelyFits)], note: "fits")
        case .coding, .vision: break
        }
        let first = assess(profile, memory: memory, context: 2048)
        if first.state == .waiting { return FitSummary(marks: [("?", nil)], note: "waiting for a memory reading") }
        if profile.isUnknown {
            return first.lowerBound ? FitSummary(marks: [("✕", .tooLarge)], note: "weights alone exceed free memory") : FitSummary(marks: [("?", .unverified)], note: "fit unverified")
        }
        let steps = candidates.filter { $0 <= min(32768, profile.capacity) }
        let fits = (steps.isEmpty ? [profile.capacity] : steps).map { (step: $0, fit: assess(profile, memory: memory, context: $0)) }
        if !fits.contains(where: { $0.fit.state == .likelyFits || $0.fit.state == .tight }) {
            return FitSummary(marks: [("✕", .tooLarge)], note: "needs \(one(first.lowGiB ?? 0))+ GiB · try CPU")
        }
        let marks = fits.map { item -> (symbol: String, state: FitState?) in
            switch item.fit.state {
            case .likelyFits: return ("●", .likelyFits)
            case .tight: return ("◐", .tight)
            default: return ("○", .tooLarge)
            }
        }
        if let best = fits.last(where: { $0.fit.state == .likelyFits }) { return FitSummary(marks: marks, note: "up to \(label(best.step))") }
        return FitSummary(marks: marks, note: "tight at \(label(fits[0].step))")
    }

    // MARK: reading metadata

    /// The memory profile of a model file or folder. Problems become an unknown estimate, never an error.
    public static func read(_ model: LocalModel) -> ModelMemoryProfile {
        if model.transformers { return readSafetensors(URL(fileURLWithPath: model.path)) }
        return readGguf(URL(fileURLWithPath: model.path))
    }

    static func unknown(_ why: String, weights: Double = 0, maxContext: Int = 4096, declared: Bool = false, architecture: String = "", sizeLabel: String = "") -> ModelMemoryProfile {
        ModelMemoryProfile(weightBytes: weights, kvBytesPerToken: 0, workspaceBytes: 0, maxContext: maxContext, contextDeclared: declared, unknownReason: why, architecture: architecture, sizeLabel: sizeLabel)
    }

    enum Value { case number(Double), text(String), flag(Bool) }
    struct Invalid: Error {}

    /// Reads a GGUF file's header in chunks, bounded to 16 MiB.
    final class HeaderReader {
        let handle: FileHandle
        let length: Int64
        private var buffer: [UInt8] = []
        private var bufferStart: Int64 = 0
        private(set) var position: Int64 = 0

        init(_ url: URL) throws {
            handle = try FileHandle(forReadingFrom: url)
            length = Int64(try handle.seekToEnd())
        }
        deinit { try? handle.close() }

        func bound() throws { if position > ModelMemory.headerLimit || position > length { throw Invalid() } }
        func skip(_ count: Int64) { position += count }

        private func take(_ count: Int) throws -> ArraySlice<UInt8> {
            if position < bufferStart || position + Int64(count) > bufferStart + Int64(buffer.count) {
                guard position + Int64(count) <= length else { throw Invalid() }
                try handle.seek(toOffset: UInt64(position))
                buffer = [UInt8](try handle.read(upToCount: max(count, 1 << 20)) ?? Data())
                bufferStart = position
                guard buffer.count >= count else { throw Invalid() }
            }
            let offset = Int(position - bufferStart)
            position += Int64(count)
            return buffer[offset..<(offset + count)]
        }
        func unsigned(_ count: Int) throws -> UInt64 {
            var value: UInt64 = 0
            for (shift, byte) in try take(count).enumerated() { value |= UInt64(byte) << (8 * UInt64(shift)) }
            return value
        }
        func u32() throws -> UInt32 { UInt32(try unsigned(4)) }
        func u64() throws -> UInt64 { try unsigned(8) }
        func text(_ count: Int) throws -> String { String(decoding: try take(count), as: UTF8.self) }
        func data(_ count: Int) throws -> Data { Data(try take(count)) }
    }

    /// The parts of a split GGUF (name-00001-of-00003.gguf) that exist, or just the file.
    public static func parts(of file: URL) -> [URL] {
        let name = file.lastPathComponent
        guard let range = name.range(of: #"-\d{5}-of-\d{5}\.gguf$"#, options: [.regularExpression, .caseInsensitive]) else { return [file] }
        let tail = name[range]
        guard let total = Int(tail.dropFirst(10).prefix(5)), total >= 1, total <= 999 else { return [file] }
        let stem = name[..<range.lowerBound], folder = file.deletingLastPathComponent()
        let found = (1...total).map { folder.appendingPathComponent(String(stem) + String(format: "-%05d-of-%05d.gguf", $0, total)) }
            .filter { FileManager.default.fileExists(atPath: $0.path) }
        return found.isEmpty ? [file] : found
    }

    static func size(_ file: URL) -> Int64 {
        ((try? FileManager.default.attributesOfItem(atPath: file.resolvingSymlinksInPath().path))?[.size] as? NSNumber)?.int64Value ?? 0
    }

    public static func readGguf(_ file: URL) -> ModelMemoryProfile {
        do { return try parseGguf(file) } catch { return unknown(unreadable) }
    }

    static func parseGguf(_ file: URL) throws -> ModelMemoryProfile {
        let reader = try HeaderReader(file.resolvingSymlinksInPath())
        guard try reader.u32() == 0x4655_4747 else { throw Invalid() }
        let version = try reader.u32()
        guard version == 2 || version == 3 else { throw Invalid() }
        let tensors = try reader.u64(), count = try reader.u64()
        guard tensors > 0, tensors <= 200_000, count <= 100_000 else { throw Invalid() }

        func text(keep: Bool) throws -> String {
            try reader.bound()
            let size = try reader.u64()
            guard size <= UInt64(max(0, min(headerLimit, reader.length - reader.position))) else { throw Invalid() }
            if keep, size <= 1024 { return try reader.text(Int(size)) }
            reader.skip(Int64(size)); try reader.bound()
            return ""
        }
        let widths: [UInt32: Int64] = [0: 1, 1: 1, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 7: 1, 10: 8, 11: 8, 12: 8]
        func value(_ type: UInt32, keep: Bool) throws -> Value? {
            try reader.bound()
            switch type {
            case 8: return .text(try text(keep: keep))
            case 9:
                let element = try reader.u32(), length = try reader.u64()
                guard element != 9, length <= 1_000_000 else { throw Invalid() }
                if let width = widths[element] { reader.skip(width * Int64(length)); try reader.bound() }
                else { for _ in 0..<length { _ = try value(element, keep: false) } }
                return nil
            default:
                guard let width = widths[type] else { throw Invalid() }
                let bits = try reader.unsigned(Int(width))
                let result: Value
                switch type {
                case 0: result = .number(Double(UInt8(truncatingIfNeeded: bits)))
                case 1: result = .number(Double(Int8(truncatingIfNeeded: bits)))
                case 2: result = .number(Double(UInt16(truncatingIfNeeded: bits)))
                case 3: result = .number(Double(Int16(truncatingIfNeeded: bits)))
                case 4: result = .number(Double(UInt32(truncatingIfNeeded: bits)))
                case 5: result = .number(Double(Int32(truncatingIfNeeded: bits)))
                case 6: result = .number(Double(Float(bitPattern: UInt32(truncatingIfNeeded: bits))))
                case 7: result = .flag(bits != 0)
                case 10: result = .number(Double(bits))
                case 11: result = .number(Double(Int64(bitPattern: bits)))
                default: result = .number(Double(bitPattern: bits))
                }
                try reader.bound()
                return result
            }
        }

        var values: [String: Value] = [:]
        for _ in 0..<count {
            let key = try text(keep: true)
            let type = try reader.u32()
            let keep = key == "general.architecture" || key == "general.size_label" || key.hasSuffix(".block_count") || key.hasSuffix(".embedding_length")
                || key.contains(".attention.head_count") || key.hasSuffix(".attention.key_length") || key.hasSuffix(".attention.value_length")
                || key.hasSuffix(".context_length") || key.contains(".ssm.") || key.hasSuffix(".full_attention_interval")
                || key.hasSuffix(".nextn_predict_layers") || key.hasSuffix(".attention.recurrent_layers")
            let found = try value(type, keep: keep)
            if keep { values[key] = found ?? .text("unsupported array") }
        }

        var architecture = "", sizeLabel = ""
        if case .text(let name)? = values["general.architecture"] { architecture = name }
        if case .text(let name)? = values["general.size_label"] { sizeLabel = name }
        var declared = false, capacity = 4096
        if case .number(let tokens)? = values[architecture + ".context_length"], tokens.isFinite, tokens >= 64 {
            declared = true; capacity = Int(min(tokens, Double(Int32.max)))
        }
        let weights = Double(parts(of: file).reduce(Int64(0)) { $0 + size($1) })
        func unavailable(_ why: String) -> ModelMemoryProfile {
            unknown(why, weights: weights, maxContext: capacity, declared: declared, architecture: architecture, sizeLabel: sizeLabel)
        }
        guard architectures.contains(architecture) else { return unavailable(unsupportedArchitecture) }
        func number(_ key: String, _ fallback: Double = 0) -> Double {
            guard case .number(let n)? = values[architecture + "." + key], n.isFinite, n > 0 else { return fallback }
            return n
        }
        let layers = number("block_count"), heads = number("attention.head_count"), kvHeads = number("attention.head_count_kv", heads), hidden = number("embedding_length")
        let keyLength = number("attention.key_length", heads > 0 ? hidden / heads : 0), valueLength = number("attention.value_length", keyLength)
        guard layers > 0, layers <= 1000, kvHeads > 0, kvHeads <= 1024, keyLength > 0, keyLength <= 8192, valueLength > 0, valueLength <= 8192 else {
            return unavailable("GGUF attention metadata is insufficient for a fit estimate.")
        }
        if architecture == "qwen35" {
            // One request, no speculative decoder. Two FP32 copies of the recurrent state are allowed for.
            if values["qwen35.attention.recurrent_layers"] != nil { return unavailable("Custom recurrent-layer maps need a dedicated estimate.") }
            let mtp = number("nextn_predict_layers"), trunk = layers - mtp, interval = number("full_attention_interval")
            let state = number("ssm.state_size"), inner = number("ssm.inner_size"), groups = number("ssm.group_count"), kernel = number("ssm.conv_kernel"), rank = number("ssm.time_step_rank")
            let geometry = [layers, mtp, trunk, interval, state, inner, groups, kernel, rank]
            guard !geometry.contains(where: { $0 != $0.rounded(.down) }), mtp <= 8, trunk > 0, interval >= 1, interval <= trunk, state > 0, state <= 4096, inner > 0, inner <= 1_000_000,
                  groups > 0, groups <= 1024, kernel >= 1, kernel <= 64, rank > 0, rank <= 1024, inner == state * rank else {
                return unavailable("Qwen hybrid metadata is missing or outside the supported estimate.")
            }
            let attention = (trunk / interval).rounded(.down), recurrent = trunk - attention
            let stateBytes = recurrent * (state * inner + (kernel - 1) * (inner + 2 * groups * state)) * 4 * 2
            return ModelMemoryProfile(weightBytes: weights, kvBytesPerToken: (attention + mtp) * kvHeads * (keyLength + valueLength) * 2, workspaceBytes: gib * 0.75 + stateBytes,
                                      maxContext: capacity, contextDeclared: declared, architecture: architecture,
                                      basis: "Qwen hybrid: \(Int(attention)) full-attention + \(Int(recurrent)) recurrent layers; \(Int(mtp)) optional prediction blocks budgeted conservatively. FP16 KV + two FP32 recurrent-state allowances; one slot, no speculative decoding",
                                      sizeLabel: sizeLabel)
        }
        return ModelMemoryProfile(weightBytes: weights, kvBytesPerToken: layers * kvHeads * (keyLength + valueLength) * 2, workspaceBytes: gib * 0.75,
                                  maxContext: capacity, contextDeclared: declared, architecture: architecture,
                                  basis: "GGUF file size, FP16 KV cache and bounded prompt batching", sizeLabel: sizeLabel)
    }

    /// A Transformers folder: safetensors headers for the weights and config.json for the attention geometry.
    public static func readSafetensors(_ folder: URL) -> ModelMemoryProfile {
        do { return try parseSafetensors(folder) } catch { return unknown(unreadable) }
    }

    static func json(_ file: URL, max: Int) throws -> [String: Any] {
        let data = try Data(contentsOf: file.resolvingSymlinksInPath())
        guard data.count <= max, let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw Invalid() }
        return object
    }

    static func parseSafetensors(_ folder: URL) throws -> ModelMemoryProfile {
        let config = try json(folder.appendingPathComponent("config.json"), max: 1024 * 1024)
        func number(_ key: String, _ fallback: Double = 0) -> Double {
            guard let n = config[key] as? NSNumber else { return fallback }
            let value = n.doubleValue
            return value.isFinite && value >= 0 ? value : fallback
        }
        let family = config["model_type"] as? String ?? ""
        if ["t5", "bart", "qwen3_moe", "mixtral"].contains(family) { return unknown("Encoder-decoder and expert-model peaks need a runtime-specific estimate.", architecture: family) }
        var files: [String]
        let index = folder.appendingPathComponent("model.safetensors.index.json")
        if FileManager.default.fileExists(atPath: index.path) {
            guard let map = try json(index, max: 4 * 1024 * 1024)["weight_map"] as? [String: Any] else { throw Invalid() }
            var seen = Set<String>(); files = []
            for name in map.values.compactMap({ $0 as? String }).sorted() where seen.insert(name).inserted { files.append(name); if files.count >= 257 { break } }
        } else { files = ["model.safetensors"] }
        guard !files.isEmpty, files.count <= 256 else { throw Invalid() }
        var weights = 0.0, budget = headerLimit
        let floats: Set<String> = ["F16", "BF16", "F32", "F64"], whole: Set<String> = ["I64", "I32", "I16", "I8", "U8", "U16", "U32", "U64", "BOOL"]
        for file in files {
            guard file == (file as NSString).lastPathComponent, !file.isEmpty, file.lowercased().hasSuffix(".safetensors") else { throw Invalid() }
            let reader = try HeaderReader(folder.appendingPathComponent(file).resolvingSymlinksInPath())
            let size = Int64(clamping: try reader.u64())
            guard size > 0, size <= budget, size <= max(0, reader.length - 8) else { throw Invalid() }
            budget -= size
            guard let header = try JSONSerialization.jsonObject(with: try reader.data(Int(size))) as? [String: Any] else { throw Invalid() }
            for (name, item) in header where name != "__metadata__" {
                guard let tensor = item as? [String: Any], let shape = tensor["shape"] as? [Any], shape.count <= 8,
                      let dtype = tensor["dtype"] as? String, let offsets = tensor["data_offsets"] as? [Any], offsets.count >= 2,
                      let begin = (offsets[0] as? NSNumber)?.int64Value, let end = (offsets[1] as? NSNumber)?.int64Value else { throw Invalid() }
                var elements = 1.0
                for dimension in shape {
                    guard let n = dimension as? NSNumber, n.doubleValue == n.doubleValue.rounded(), n.int64Value >= 0, n.int64Value <= 1_000_000_000 else { throw Invalid() }
                    elements *= Double(n.int64Value)
                }
                guard begin >= 0, end >= begin, end <= reader.length - 8 - size, elements.isFinite else { throw Invalid() }
                if floats.contains(dtype) { weights += elements * 2 }
                else if whole.contains(dtype) { weights += Double(end - begin) }
                else { throw Invalid() }
            }
        }
        guard weights > 0, weights <= 1e15 else { throw Invalid() }
        let layers = number("num_hidden_layers", number("n_layer")), heads = number("num_attention_heads", number("n_head")), hidden = number("hidden_size", number("n_embd"))
        let kvHeads = number("num_key_value_heads", heads), headDim = number("head_dim", heads > 0 ? hidden / heads : 0)
        let capacity = Int(min(max(number("max_position_embeddings", number("n_positions", 4096)), 64), Double(Int32.max)))
        let geometry = [layers, heads, kvHeads, headDim, hidden]
        guard layers > 0, layers <= 1000, heads > 0, heads <= 1024, kvHeads > 0, kvHeads <= heads, headDim > 0, headDim <= 8192, hidden > 0, hidden <= 1_000_000,
              number("vocab_size") <= 1_000_000, number("intermediate_size") <= 10_000_000, !geometry.contains(where: { $0 != $0.rounded(.down) }) else {
            return unknown("Attention geometry is missing or outside the supported estimate.", weights: weights, maxContext: capacity, declared: true, architecture: family)
        }
        let quantization = (config["quantization_config"] as? [String: Any])?["quant_method"] as? String ?? ""
        let quantized = ["gptq", "awq"].contains(quantization.lowercased())
        var workspace = gib * 0.75
        if quantized { workspace += max(hidden * number("intermediate_size", hidden * 4) * 4, gib * 0.25) }
        return ModelMemoryProfile(weightBytes: weights, kvBytesPerToken: 2 * layers * kvHeads * headDim * 2, workspaceBytes: workspace,
                                  workspacePerToken: hidden * 12 + number("vocab_size") * 4, maxContext: capacity, contextDeclared: true, architecture: family,
                                  basis: "Tensor headers, FP16/BF16 activations and full-context KV cache" + (quantized ? "; packed weights plus temporary dequantization" : ""))
    }
}
