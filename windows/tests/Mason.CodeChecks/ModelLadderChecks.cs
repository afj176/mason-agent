using System.Text;
using Mason;

/// <summary>The Library ladder and CLI picker: GGUF memory metadata, fit verdicts, suggested size and the offered sizes. No GPU.</summary>
internal static class ModelLadderChecks
{
    const double GiB = 1073741824;

    public static void Run(string folder, Action<bool, string> check)
    {
        static LocalModel Gguf(string path) => new(Path.GetFileNameWithoutExtension(path), path, "", "GGUF", "Chat", new FileInfo(path).Length, "");
        var qwen = Write(Path.Combine(folder, "ladder-qwen2.gguf"), "qwen2", ("block_count", 28u), ("attention.head_count", 28u), ("attention.head_count_kv", 4u), ("embedding_length", 3584u), ("context_length", 32768u));
        var profile = ModelMemory.Read(Gguf(qwen));
        check(profile.Unknown == null && profile.KvBytesPerToken == 28 * 4 * (128 + 128) * 2 && profile.MaxContext == 32768 && profile.ContextDeclared,
            "a GGUF header gives the context cache per token (layers × KV heads × key+value length × 2 bytes) and the declared capacity");

        var odd = Write(Path.Combine(folder, "ladder-inkling.gguf"), "inkling2_loop", ("block_count", 40u), ("context_length", 131072u));
        var unknown = ModelMemory.Read(Gguf(odd));
        check(unknown.Unknown != null && unknown.WeightBytes == new FileInfo(odd).Length && unknown.MaxContext == 131072 && unknown.ContextDeclared,
            "an architecture without an estimate keeps its weights as a lower bound and its declared capacity");

        // 12 GiB card with 8.2 GiB free; 0.6 GiB kept spare.
        var sample = new GpuSample(DateTimeOffset.Now, [new GpuReading("0", "NVIDIA GeForce RTX 4070", 10, 12288, 12288 - 8.2 * 1024, 8.2 * 1024)], [], null, null);
        var seven = new ModelMemoryProfile(4.36 * GiB, 57344, .75 * GiB, 32768, "test");
        check(ModelMemory.Assess(seven, sample, 16384).State == ModelFit.Fits && ModelMemory.Assess(seven, sample, 32768).State == ModelFit.Tight,
            "16K likely fits and 32K is a tight fit for a 7B Q4 model with 8.2 GiB free");
        var suggested = ModelMemory.Recommend(seven, sample);
        check(suggested is (true, 16384, "Suggested: 16K on GPU · largest likely fit"), "the suggestion is the largest ladder step that likely fits: " + suggested.Text);
        var fit = ModelMemory.Assess(seven, sample, 16384);
        check(ModelLadder.Memory(fit) == "6.0 GiB" && ModelLadder.Detail(fit, 16384, "qwen2", false) == "6.0–7.0 GiB at 16K · 0.6 GiB kept spare. An estimate, not a guarantee." && ModelLadder.Device(sample) == "RTX 4070 · 8.2 GiB free",
            "the ladder cell, fit card and device label read the same numbers");

        var big = new ModelMemoryProfile(16.4 * GiB, 0, 0, 32768, "", "This GGUF architecture needs a runtime-specific memory estimate.");
        var bound = ModelMemory.Assess(big, sample, 2048);
        check(bound.State == ModelFit.TooLarge && !bound.Estimated && ModelLadder.Memory(bound) == "16.4+ GiB" && ModelMemory.Recommend(big, sample) is (false, 2048, "Suggested: CPU · weights exceed free memory"),
            "weights larger than free memory are too large even without a context estimate, and the suggestion is the CPU");
        var small = big with { WeightBytes = 3 * GiB };
        check(ModelMemory.Assess(small, sample, 2048).State == ModelFit.Unverified && ModelMemory.Recommend(small, sample) is (true, 2048, "Suggested starting point: GPU · 2K · fit unverified"),
            "weights that fit without a context estimate are unverified, not a promise");
        check(ModelLadder.Detail(ModelMemory.Assess(small, sample, 2048), 2048, "lfm3", true).StartsWith("Runs from system RAM."), "the CPU choice explains that system RAM is not checked");

        int[] Steps(int max, bool declared = true) => ModelLadder.Steps(new ModelMemoryProfile(1, 1, 1, max, "", ContextDeclared: declared));
        check(Steps(4096, false).SequenceEqual([2048, 4096, 8192, 16384, 32768]) && Steps(40960).SequenceEqual([2048, 4096, 8192, 16384, 32768, 40960]) &&
              Steps(128000).SequenceEqual([4096, 8192, 16384, 32768, 65536, 128000]) && Steps(262144).SequenceEqual([8192, 16384, 32768, 65536, 131072, 262144]) && Steps(1000).SequenceEqual([1000]),
            "the ladder offers up to six sizes: powers of two up to the capacity, the capacity itself, 32K when none is declared");
        check(ModelLadder.Label(16384) == "16K" && ModelLadder.Label(40960) == "40K" && ModelLadder.Label(128000) == "128K" && ModelLadder.Label(131072) == "128K",
            "ladder labels read 16K, 40K and 128K");
        check(ModelLadder.Parse("16k") == 16384 && ModelLadder.Parse("16K") == 16384 && ModelLadder.Parse("16384") == 16384 && ModelLadder.Parse("cpu") == null,
            "a typed size accepts 16384, 16k or 16K");
    }

    /// <summary>A GGUF v3 header with general.architecture and ARCH.* uint32 keys, followed by 4 KiB of padding.</summary>
    static string Write(string path, string architecture, params (string Key, uint Value)[] keys)
    {
        using var output = new BinaryWriter(File.Create(path), Encoding.UTF8);
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); output.Write((ulong)bytes.Length); output.Write(bytes); }
        output.Write(0x46554747u); output.Write(3u); output.Write(1ul); output.Write((ulong)(keys.Length + 1));
        Text("general.architecture"); output.Write(8u); Text(architecture);
        foreach (var (key, value) in keys) { Text(architecture + "." + key); output.Write(4u); output.Write(value); }
        output.Write(new byte[4096]);
        return path;
    }
}
