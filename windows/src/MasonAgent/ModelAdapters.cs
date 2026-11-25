using System.Text.Json;

namespace Mason;

/// <summary>One packaged family registry is shared by discovery and the Python loader.</summary>
public static class ModelAdapters
{
    static readonly Dictionary<string,string> Families = JsonSerializer.Deserialize<Dictionary<string,string>>(
        typeof(ModelAdapters).Assembly.GetManifestResourceStream("Mason.ModelAdapters.json")!)!;

    public static IReadOnlyCollection<string> TextFamilies => Families.Keys;
    public static bool UsesPython(LocalModel model) => model.Task != "Chat" || model.Format != "GGUF";
    public static bool IsQuantized(LocalModel model) => model.Format.Contains("GPTQ",StringComparison.Ordinal) || model.Format.Contains("AWQ",StringComparison.Ordinal);

    public static string Quantization(JsonElement config)
    {
        if(!config.TryGetProperty("quantization_config",out var q))return "";
        if(q.ValueKind!=JsonValueKind.Object)return "Unsupported quantization metadata";
        var method=q.TryGetProperty("quant_method",out var m) && m.ValueKind==JsonValueKind.String?m.GetString()?.ToLowerInvariant():null;
        if(method is not ("gptq" or "awq"))return "Unsupported quantization method";
        if(!q.TryGetProperty("bits",out var bits) || bits.ValueKind!=JsonValueKind.Number || !bits.TryGetInt32(out var n) || n!=4)return "Only 4-bit GPTQ/AWQ checkpoints are supported";
        var group=q.TryGetProperty("group_size",out var g)?g.ValueKind==JsonValueKind.Number && g.TryGetInt32(out var groupSize)?groupSize:0:128;
        if(group is not (-1 or 32 or 64 or 128))return "Unsupported quantization group size";
        string format=method=="gptq"?"gptq":"gemm";
        foreach(var key in method=="gptq"?new[]{"checkpoint_format","format"}:new[]{"format","version"})
            if(q.TryGetProperty(key,out var f) && f.ValueKind==JsonValueKind.String) {format=f.GetString()!.ToLowerInvariant();break;}
        if(method=="gptq" && format is not ("gptq" or "gptq_v2") || method=="awq" && format!="gemm")return "Unsupported packed format: " + format;
        if(q.TryGetProperty("dynamic",out var dynamicRules) && dynamicRules.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))return "Mixed/dynamic quantization requires a dedicated adapter";
        return method.ToUpperInvariant()+" · 4-bit";
    }

    public static string? TextSupport(string folder, JsonElement config)
    {
        string type=config.TryGetProperty("model_type",out var t)?t.GetString()??"":"";
        if(!Families.TryGetValue(type,out var architecture))return null;
        if(!config.TryGetProperty("architectures",out var architectures) || architectures.ValueKind!=JsonValueKind.Array || !architectures.EnumerateArray().Any(x=>x.GetString()==architecture))
            return "Inventory only: this configuration is not the supported text-generation architecture for " + type + ".";
        var quantization=Quantization(config);
        if(File.Exists(Path.Combine(folder,"adapter_config.json")))return "Inventory only: adapter-only bundles require a dedicated runtime.";
        if(quantization.Length>0 && !quantization.StartsWith("GPTQ") && !quantization.StartsWith("AWQ"))return "Inventory only: "+quantization+". Use a compatible external server.";
        if(quantization.Length>0 && type is "t5" or "bart")return "Inventory only: quantized encoder-decoder models are not supported by this adapter.";
        if(!File.Exists(Path.Combine(folder,"tokenizer_config.json")) ||
           !new[]{"tokenizer.json","tokenizer.model","spiece.model","vocab.json"}.Any(x=>File.Exists(Path.Combine(folder,x))))
            return "Needs local files: tokenizer configuration or tokenizer assets are missing. Add the complete model folder and scan again.";
        string? issue=WeightIssue(folder);
        return issue??(quantization.Length>0?"Python · "+quantization+" with the quantized text runtime. Setup and capability are checked before use.":"Python · Transformers text generation. Local setup and capability are checked when you start. No model downloads.");
    }

    public static string? WeightIssue(string folder)
    {
        var index=Path.Combine(folder,"model.safetensors.index.json");
        if(File.Exists(index))
        {
            if(new FileInfo(index).Length>4*1024*1024)return "Needs local files: weight index exceeds the supported size.";
            using var doc=JsonDocument.Parse(File.ReadAllText(index));
            if(!doc.RootElement.TryGetProperty("weight_map",out var map) || map.ValueKind!=JsonValueKind.Object)
                return "Needs local files: invalid Safetensors shard index.";
            var shards=map.EnumerateObject().Select(x=>x.Value.GetString()??"").Distinct(StringComparer.Ordinal).Take(257).ToArray();
            if(shards.Length==0 || shards.Length>256 || shards.Any(x=>x!=Path.GetFileName(x) || !x.EndsWith(".safetensors",StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(folder,x))))
                return "Needs local files: a Safetensors shard is missing or its index is invalid. Add the complete model folder and scan again.";
        }
        else if(!File.Exists(Path.Combine(folder,"model.safetensors")))
            return "Needs local files: model.safetensors or a complete shard index is required.";
        return null;
    }
}
