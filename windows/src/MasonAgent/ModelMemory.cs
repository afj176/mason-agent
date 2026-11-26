using System.Text;
using System.Text.Json;

namespace Mason;

public sealed record ModelLaunchOptions(int ContextTokens=2048)
{
    /// <summary>Optional GGUF draft model for speculative decoding (faster agent turns).</summary>
    public string? DraftPath {get;init;}
    /// <summary>Optional GGUF LoRA adapter applied on top of the base weights.</summary>
    public string? LoraPath {get;init;}
    public void Validate()
    {
        if(ContextTokens < 64)throw new ArgumentException("Choose a whole context window of at least 64 tokens, within the model's declared capacity.");
        foreach(var path in new[]{DraftPath,LoraPath})if(path!=null&&(!Path.IsPathFullyQualified(path)||!File.Exists(path)||!path.EndsWith(".gguf",StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Draft models and adapters must be local GGUF files.");
    }
}
public sealed record ModelMemoryProfile(double WeightBytes,double KvBytesPerToken,double WorkspaceBytes,int MaxContext,string Basis,string? Unknown=null,double WorkspacePerToken=0,bool ContextDeclared=true);
/// <summary>A fit verdict. <see cref="Estimated"/> is false when only the weights are known (a lower bound), and
/// <see cref="FreeGiB"/>/<see cref="SpareGiB"/> are set whenever a usable memory reading was compared.</summary>
public sealed record ModelFit(string State,string Detail,double? LowGiB=null,double? HighGiB=null,double? FreeGiB=null,double? SpareGiB=null,bool Estimated=true)
{
    public const string Fits="Likely fits",Tight="Tight fit",TooLarge="Too large for free memory",Unverified="Fit unverified",Waiting="Waiting for a memory reading";
    public string Summary=>LowGiB.HasValue?(Estimated?$"{State} · {LowGiB:0.0}–{HighGiB:0.0} GiB estimated":$"{State} · {LowGiB:0.0}+ GiB"):State;
}

/// <summary>Bounded, metadata-only estimates for the actual single-request serving routes. Never reads tensor payloads.</summary>
public static class ModelMemory
{
    const double GiB=1073741824;
    const long HeaderLimit=16*1024*1024;
    public static ModelMemoryProfile Read(LocalModel model,CancellationToken ct=default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if(!model.Launchable)return Unknown("This file has no managed serving adapter.");
            if(model.Format=="GGUF")return ReadGguf(model.Path,ct);
            using var config=ReadJson(Path.Combine(model.Path,"config.json"),1024*1024);
            var c=config.RootElement;var family=String(c,"model_type");
            if(family is "t5" or "bart" or "qwen3_moe" or "mixtral")return Unknown("Encoder-decoder and expert-model peaks need a runtime-specific estimate.");
            double weights=0;long headerBudget=HeaderLimit;
            string[] files;
            var index=Path.Combine(model.Path,"model.safetensors.index.json");
            if(File.Exists(index)) {using var doc=ReadJson(index,4*1024*1024);files=doc.RootElement.GetProperty("weight_map").EnumerateObject().Select(x=>x.Value.GetString()!).Distinct().Take(257).ToArray();}
            else files=["model.safetensors"];
            if(files.Length is 0 or >256)throw new InvalidDataException();
            const int floatBytes=2;
            foreach(var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if(file!=Path.GetFileName(file) || !file.EndsWith(".safetensors",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException();
                using var input=File.OpenRead(Path.Combine(model.Path,file));using var reader=new BinaryReader(input);
                var size=reader.ReadUInt64();if(size==0 || size>(ulong)headerBudget || size>(ulong)Math.Max(0,input.Length-8))throw new InvalidDataException();
                headerBudget-=(long)size;using var doc=JsonDocument.Parse(reader.ReadBytes((int)size));
                foreach(var tensor in doc.RootElement.EnumerateObject())
                {
                    if(tensor.Name=="__metadata__")continue;
                    var t=tensor.Value;var shape=t.GetProperty("shape");double elements=1;
                    if(shape.GetArrayLength()>8)throw new InvalidDataException();
                    foreach(var dimension in shape.EnumerateArray()){var n=dimension.GetInt64();if(n<0 || n>1_000_000_000)throw new InvalidDataException();elements*=n;}
                    var dtype=String(t,"dtype");var offsets=t.GetProperty("data_offsets");var begin=offsets[0].GetInt64();var end=offsets[1].GetInt64();
                    if(begin<0 || end<begin || end>input.Length-8-(long)size || !double.IsFinite(elements))throw new InvalidDataException();
                    if(dtype is "F16" or "BF16" or "F32" or "F64")weights+=elements*floatBytes;
                    else if(dtype is "I64" or "I32" or "I16" or "I8" or "U8" or "U16" or "U32" or "U64" or "BOOL")weights+=end-begin;
                    else throw new InvalidDataException();
                }
            }
            if(weights<=0 || weights>1e15)throw new InvalidDataException();
            var layers=Number(c,"num_hidden_layers",Number(c,"n_layer"));var heads=Number(c,"num_attention_heads",Number(c,"n_head"));var hidden=Number(c,"hidden_size",Number(c,"n_embd"));
            var kvHeads=Number(c,"num_key_value_heads",heads);var headDim=Number(c,"head_dim",heads>0?hidden/heads:0);
            int capacity=(int)Math.Clamp(Number(c,"max_position_embeddings",Number(c,"n_positions",4096)),64,int.MaxValue);
            if(layers<=0 || layers>1000 || heads<=0 || heads>1024 || kvHeads<=0 || kvHeads>heads || headDim<=0 || headDim>8192 || hidden<=0 || hidden>1_000_000 || Number(c,"vocab_size")>1_000_000 || Number(c,"intermediate_size")>10_000_000 || new[]{layers,heads,kvHeads,headDim,hidden}.Any(x=>x!=Math.Floor(x)))return Unknown("Attention geometry is missing or outside the supported estimate.");
            var workspace=GiB*.75;
            if(ModelAdapters.IsQuantized(model))workspace+=Math.Max(hidden*Number(c,"intermediate_size",hidden*4)*4,GiB*.25);
            return new(weights,2*layers*kvHeads*headDim*2,workspace,capacity,"Tensor headers, FP16/BF16 activations and full-context KV cache"+(ModelAdapters.IsQuantized(model)?"; packed weights plus temporary dequantization":""),WorkspacePerToken:hidden*12+Number(c,"vocab_size")*4);
        }
        catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException){return Unknown("Model metadata could not be read safely. Check files and compatibility; no fit claim is made.");}
    }
    static ModelMemoryProfile Unknown(string why)=>new(0,0,0,4096,"",why,ContextDeclared:false);
    static JsonDocument ReadJson(string file,long max){using var stream=File.OpenRead(file);if(stream.Length>max)throw new InvalidDataException();return JsonDocument.Parse(stream);}
    static double Number(JsonElement value,string key,double fallback=0)=>value.TryGetProperty(key,out var n)&&n.ValueKind==JsonValueKind.Number&&n.TryGetDouble(out var d)&&double.IsFinite(d)&&d>=0?d:fallback;
    static string String(JsonElement value,string key)=>value.TryGetProperty(key,out var s)&&s.ValueKind==JsonValueKind.String?s.GetString()!:"";

    static ModelMemoryProfile ReadGguf(string file,CancellationToken ct)
    {
        using var input=File.OpenRead(file);using var reader=new BinaryReader(input,Encoding.UTF8);
        if(reader.ReadUInt32()!=0x46554747 || reader.ReadUInt32() is not (2 or 3))throw new InvalidDataException();
        var tensors=reader.ReadUInt64();var count=reader.ReadUInt64();if(tensors==0 || tensors>200_000 || count>100_000)throw new InvalidDataException();
        var values=new Dictionary<string,object>();
        void Bound(){ct.ThrowIfCancellationRequested();if(input.Position>HeaderLimit || input.Position>input.Length)throw new InvalidDataException();}
        string Text(bool keep){Bound();var size=reader.ReadUInt64();if(size>(ulong)Math.Min(HeaderLimit,input.Length-input.Position))throw new InvalidDataException();if(keep&&size<=1024)return Encoding.UTF8.GetString(reader.ReadBytes((int)size));input.Seek((long)size,SeekOrigin.Current);Bound();return "";}
        object? Value(uint type,bool keep)
        {
            Bound();if(type==8)return Text(keep);
            if(type==9){var element=reader.ReadUInt32();var length=reader.ReadUInt64();if(element==9 || length>1_000_000)throw new InvalidDataException();for(ulong j=0;j<length;j++)Value(element,false);return null;}
            object v=type switch{0=>reader.ReadByte(),1=>reader.ReadSByte(),2=>reader.ReadUInt16(),3=>reader.ReadInt16(),4=>reader.ReadUInt32(),5=>reader.ReadInt32(),6=>reader.ReadSingle(),7=>reader.ReadBoolean(),10=>reader.ReadUInt64(),11=>reader.ReadInt64(),12=>reader.ReadDouble(),_=>throw new InvalidDataException()};Bound();return v;
        }
        for(ulong i=0;i<count;i++){var key=Text(true);var type=reader.ReadUInt32();var keep=key=="general.architecture" || key.EndsWith(".block_count") || key.EndsWith(".embedding_length") || key.Contains(".attention.head_count") || key.EndsWith(".attention.key_length") || key.EndsWith(".attention.value_length") || key.EndsWith(".context_length") || key.Contains(".ssm.") || key.EndsWith(".full_attention_interval") || key.EndsWith(".nextn_predict_layers") || key.EndsWith(".attention.recurrent_layers");var value=Value(type,keep);if(keep)values[key]=value??"unsupported array";}
        var architecture=values.GetValueOrDefault("general.architecture") as string??"";
        bool contextDeclared=values.TryGetValue(architecture+".context_length",out var declared)&&declared is not string and not bool&&double.IsFinite(Convert.ToDouble(declared))&&Convert.ToDouble(declared)>=64;
        int capacity=contextDeclared?(int)Math.Clamp(Convert.ToDouble(declared),64,int.MaxValue):4096;
        // The weights are still known when the context cost is not, so a lower bound can be compared with free memory.
        ModelMemoryProfile Unavailable(string why)=>Unknown(why) with {MaxContext=capacity,ContextDeclared=contextDeclared,WeightBytes=SplitBytes(file,input.Length)};
        if(architecture is not ("qwen35" or "llama" or "qwen2" or "qwen3" or "gemma" or "gemma2" or "gemma3" or "phi2" or "phi3" or "mistral" or "gptneox" or "gpt2"))return Unavailable("This GGUF architecture needs a runtime-specific memory estimate.");
        double N(string key,double fallback=0){var v=values.GetValueOrDefault(architecture+"."+key);if(v==null || v is string or bool)return fallback;var n=Convert.ToDouble(v);return double.IsFinite(n)&&n>0?n:fallback;}
        var layers=N("block_count");var heads=N("attention.head_count");var kvHeads=N("attention.head_count_kv",heads);var hidden=N("embedding_length");
        var keyLength=N("attention.key_length",heads>0?hidden/heads:0);var valueLength=N("attention.value_length",keyLength);
        if(layers<=0 || layers>1000 || kvHeads<=0 || kvHeads>1024 || keyLength<=0 || keyLength>8192 || valueLength<=0 || valueLength>8192)return Unavailable("GGUF attention metadata is insufficient for a fit estimate.");
        if(architecture=="qwen35")
        {
            // One request/slot, no speculative decoder. Include a second FP32 state allowance.
            if(values.ContainsKey("qwen35.attention.recurrent_layers"))return Unavailable("Custom recurrent-layer maps need a dedicated estimate.");
            var mtp=N("nextn_predict_layers");var trunk=layers-mtp;var interval=N("full_attention_interval");
            var state=N("ssm.state_size");var inner=N("ssm.inner_size");var groups=N("ssm.group_count");var kernel=N("ssm.conv_kernel");var rank=N("ssm.time_step_rank");
            var geometry=new[]{layers,mtp,trunk,interval,state,inner,groups,kernel,rank};
            if(geometry.Any(x=>x!=Math.Floor(x)) || mtp>8 || trunk<=0 || interval<1 || interval>trunk || state<=0 || state>4096 || inner<=0 || inner>1_000_000 || groups<=0 || groups>1024 || kernel<1 || kernel>64 || rank<=0 || rank>1024 || inner!=state*rank)return Unavailable("Qwen hybrid metadata is missing or outside the supported estimate.");
            var attention=Math.Floor(trunk/interval);var recurrent=trunk-attention;
            var stateBytes=recurrent*(state*inner+(kernel-1)*(inner+2*groups*state))*4*2;
            return new(input.Length,(attention+mtp)*kvHeads*(keyLength+valueLength)*2,GiB*.75+stateBytes,capacity,$"Qwen hybrid: {attention:0} full-attention + {recurrent:0} recurrent layers; {mtp:0} optional prediction blocks budgeted conservatively. FP16 KV + two FP32 recurrent-state allowances; one slot, no speculative decoding",ContextDeclared:contextDeclared);
        }
        return new(SplitBytes(file,input.Length),layers*kvHeads*(keyLength+valueLength)*2,GiB*.75,capacity,"GGUF file size, FP16 KV cache and bounded prompt batching",ContextDeclared:contextDeclared);
    }
    /// <summary>The bytes of every part of a split GGUF ("-00001-of-00003"), or the one file's size.</summary>
    static double SplitBytes(string file,long first)
    {
        var match=System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file),@"^(?<stem>.+)-00001-of-(?<count>\d{5})\.gguf$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if(!match.Success)return first;
        int count=int.Parse(match.Groups["count"].Value);double total=0;
        for(int part=1;part<=count;part++)
        {
            var path=Path.Combine(Path.GetDirectoryName(file)??"",$"{match.Groups["stem"].Value}-{part:00000}-of-{match.Groups["count"].Value}.gguf");
            if(!File.Exists(path))return first;
            total+=new FileInfo(path).Length;
        }
        return total;
    }
    public static ModelFit Assess(ModelMemoryProfile? profile,GpuSample? sample,int context,DateTimeOffset? now=null)
    {
        if(profile==null)return new("Estimate unavailable","Scan the library to read model metadata.");
        bool estimated=profile.Unknown==null;
        if(!estimated && profile.WeightBytes<=0)return new("Estimate unavailable",profile.Unknown!);
        var tokens=Math.Min(context,profile.MaxContext);var cache=profile.KvBytesPerToken*tokens;
        var workspace=profile.WorkspaceBytes+profile.WorkspacePerToken*tokens;
        double low=estimated?(profile.WeightBytes+cache+workspace)/GiB:profile.WeightBytes/GiB;
        double high=estimated?(profile.WeightBytes*1.12+cache*1.15+workspace*1.5)/GiB:low;
        var range=estimated?$"Estimated {low:0.0}–{high:0.0} GiB at {tokens:N0} context tokens.":$"The weights alone are {low:0.0} GiB.";
        if(sample==null || sample.Error!=null || (now??DateTimeOffset.Now)-sample.At>TimeSpan.FromSeconds(15))return new(ModelFit.Waiting,range+" A fresh driver reading is needed.",low,high,Estimated:estimated);
        if(sample.Adapters.Length!=1 || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES")))return new("Estimate unavailable",range+" GPU mapping is ambiguous; capacity is never pooled across adapters.",low,high,Estimated:estimated);
        var gpu=sample.Adapters[0];
        if(!gpu.FreeMiB.HasValue || !gpu.TotalMiB.HasValue || gpu.TotalMiB<=0 || gpu.FreeMiB<0 || gpu.FreeMiB>gpu.TotalMiB)return new("GPU memory unavailable",range+" The driver did not report usable free memory.",low,high,Estimated:estimated);
        double free=gpu.FreeMiB.Value/1024,reserve=Math.Max(.5,gpu.TotalMiB.Value/1024*.05);
        if(!estimated)
            return low+reserve>free
                ?new(ModelFit.TooLarge,$"{range} {free:0.0} GiB is free. {profile.Unknown}",low,high,free,reserve,false)
                :new(ModelFit.Unverified,$"{range} {profile.Unknown} Fit is checked when it starts.",low,high,free,reserve,false);
        string state=high+reserve<=free?ModelFit.Fits:low+reserve<=free?ModelFit.Tight:ModelFit.TooLarge;
        return new(state,$"{range} {free:0.0} GiB free; {reserve:0.0} GiB kept spare. Estimate, not a guarantee.",low,high,free,reserve);
    }
    public static (bool Gpu,int Context,string Text) Recommend(ModelMemoryProfile? profile,GpuSample? sample)
    {
        // Largest ladder step that likely fits, up to the model's declared capacity. 32K is the ceiling: beyond it
        // prompt processing on one consumer GPU gets slow enough that a bigger default would not help.
        int cap=Math.Clamp(profile?.MaxContext??4096,64,32768);
        foreach(var context in ModelLadder.Steps(profile).Where(c=>c<=cap).Reverse().Concat(new[]{1024,512}.Where(c=>c<=cap)))
            if(Assess(profile,sample,context).State==ModelFit.Fits)return (true,context,$"Suggested: {ModelLadder.Label(context)} on GPU · largest likely fit");
        int start=Math.Min(2048,cap);var fit=Assess(profile,sample,Math.Min(512,cap));
        if(fit.State==ModelFit.TooLarge)return (false,start,fit.Estimated?"Suggested: CPU · GPU headroom is limited":"Suggested: CPU · weights exceed free memory");
        if(fit.State==ModelFit.Tight)return (false,start,"Suggested: CPU · GPU headroom is limited");
        return (true,start,$"Suggested starting point: GPU · {ModelLadder.Label(start)} · fit unverified");
    }
}

public enum FitTone{Fits,Tight,TooLarge,Muted}

/// <summary>The context sizes offered for a model and the words shown for each fit, shared by the Library panel and the CLI.</summary>
public static class ModelLadder
{
    static readonly int[] Candidates=[2048,4096,8192,16384,32768,65536,131072,262144];
    /// <summary>Up to six context sizes: powers of two up to the declared capacity (32K when none is declared), plus the capacity itself when it is not one of them.</summary>
    public static int[] Steps(ModelMemoryProfile? profile)
    {
        bool declared=profile is {ContextDeclared:true};
        int max=declared?profile!.MaxContext:32768;
        var steps=Candidates.Where(c=>c<=max).ToList();
        if(steps.Count==0)steps.Add(Math.Max(64,max));
        else if(declared&&max>steps[^1])steps.Add(max);
        return steps.Skip(Math.Max(0,steps.Count-6)).ToArray();
    }
    /// <summary>"16K" for 16,384; a capacity in whole thousands reads in thousands, so 128,000 is "128K" (not 125K).</summary>
    public static string Label(int tokens)=>tokens<1000?tokens.ToString():tokens%1000==0?$"{tokens/1000}K":$"{Math.Round(tokens/1024.0):0}K";
    /// <summary>Reads 16384, 16k or 16K. Returns null for anything else.</summary>
    public static int? Parse(string text)
    {
        text=text.Trim();
        if(text.EndsWith("k",StringComparison.OrdinalIgnoreCase)&&int.TryParse(text[..^1],out var k)&&k>0&&k<=1024)return k*1024;
        return int.TryParse(text,out var n)&&n>=512?n:null;
    }
    public static FitTone Tone(ModelFit fit)=>fit.State switch{ModelFit.Fits=>FitTone.Fits,ModelFit.Tight=>FitTone.Tight,ModelFit.TooLarge=>FitTone.TooLarge,_=>FitTone.Muted};
    /// <summary>The memory shown under a step: the low estimate, the weights as a lower bound, or a dash.</summary>
    public static string Memory(ModelFit fit)=>fit.LowGiB is not {} low?"— GiB":fit.Estimated?$"{low:0.0} GiB":$"{low:0.0}+ GiB";
    /// <summary>The fit card's one-line explanation.</summary>
    public static string Detail(ModelFit fit,int tokens,string architecture,bool cpu)
    {
        if(cpu)return "Runs from system RAM. Available RAM is not checked here, and CPU turns are much slower.";
        string label=Label(tokens),arch=architecture.Length>0?architecture:"this architecture";
        string range=fit.LowGiB.HasValue?$"{fit.LowGiB:0.0}–{fit.HighGiB:0.0} GiB at {label}":"";
        return fit.State switch
        {
            ModelFit.Fits=>$"{range} · {fit.SpareGiB:0.0} GiB kept spare. An estimate, not a guarantee.",
            ModelFit.Tight=>$"{range} · other apps could push it over. Try a smaller context or close apps using the GPU.",
            ModelFit.TooLarge when fit.Estimated=>$"{range} · more than the {fit.FreeGiB:0.0} GiB free.",
            ModelFit.TooLarge=>$"The weights alone are {fit.LowGiB:0.0} GiB and {fit.FreeGiB:0.0} GiB is free. Context memory for {arch} is not estimated yet.",
            ModelFit.Unverified=>$"The weights are {fit.LowGiB:0.0} GiB. Context memory for {arch} is not estimated, so fit is checked when it starts.",
            _=>fit.Detail
        };
    }
    /// <summary>"RTX 4070 · 8.2 GiB free", from the one adapter Assess compares against.</summary>
    public static string Device(GpuSample? sample)
    {
        if(sample is not {Error:null,Adapters.Length:1})return "GPU memory unavailable";
        var gpu=sample.Adapters[0];
        return gpu.FreeMiB is {} free?$"{Short(gpu.Name)} · {free/1024:0.0} GiB free":Short(gpu.Name);
    }
    /// <summary>The quantisation in a GGUF file name, such as Q4_K_M, IQ4_XS or UD-Q4_K_XL.</summary>
    public static string? Quant(LocalModel model)
    {
        var match=System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(model.Path),@"(?<![A-Za-z0-9])((UD-)?(IQ\d_[A-Z0-9_]+|Q\d(_[A-Z0-9]+)*)|BF16|F16|F32)(?![A-Za-z0-9])",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success?match.Value.ToUpperInvariant():null;
    }
    /// <summary>The Hugging Face repository a local file came from: a Mason download record or the hub cache folder name.</summary>
    public static string? Repository(LocalModel model)
    {
        if(System.Text.RegularExpressions.Regex.IsMatch(model.Source,@"^[\w.-]+/[\w.-]+$"))return model.Source;
        var cache=System.Text.RegularExpressions.Regex.Match(model.Path,@"models--(?<org>[^\\/]+?)--(?<name>[^\\/]+)");
        return cache.Success?cache.Groups["org"].Value+"/"+cache.Groups["name"].Value:null;
    }
    /// <summary>"NVIDIA GeForce RTX 4070 Laptop GPU" → "RTX 4070 Laptop GPU".</summary>
    public static string Short(string name)=>System.Text.RegularExpressions.Regex.Replace(name,@"^(NVIDIA\s+)?(GeForce\s+)?","",System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
}
