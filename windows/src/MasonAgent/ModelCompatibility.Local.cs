namespace Mason;

public static partial class ModelCompatibility
{
    /// <summary>Verdict for a local file, using the architecture read from its own header.</summary>
    public static ModelVerdict Assess(LocalModel model, RuntimeFacts runtime)
    {
        var role = model.Role;
        if (role is AgentRole.NotUsed or AgentRole.Unknown) return new(role, ModelReadiness.NotSupported, model.Support);
        if (model.Support.StartsWith("Needs local files", StringComparison.Ordinal)) return new(role, ModelReadiness.CheckFiles, model.Support);
        if (role == AgentRole.Projector) return new(role, ModelReadiness.Ready, "Loaded automatically with a chat model in the same folder.");
        if (model.Format == "GGUF") return Gguf(role, model.Architecture, runtime);
        if (role == AgentRole.Adapter) return runtime.Python ? new(role, ModelReadiness.Ready, "Choose it when starting its base model.") : new(role, ModelReadiness.NeedsRuntime, "Needs the Transformers runtime.", "Choose Python in Runtimes.");
        if (!model.Launchable) return new(role, ModelReadiness.UseGgufBuild, model.Support, "Download a GGUF build of this model.");
        bool quantized = model.Format.Contains("GPTQ", StringComparison.Ordinal) || model.Format.Contains("AWQ", StringComparison.Ordinal);
        if (quantized ? !runtime.QuantizedPython : !runtime.Python) return new(role, ModelReadiness.NeedsRuntime, quantized ? "Needs the quantized GPTQ/AWQ runtime." : "Needs the Transformers runtime.", "Set it up in Runtimes.");
        return new(role, ModelReadiness.Ready, quantized ? "Runs with the quantized Transformers runtime." : "Runs with the local Transformers runtime.");
    }

}
