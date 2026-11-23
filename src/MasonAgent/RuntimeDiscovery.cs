namespace Mason;

/// <summary>Find existing executables; never installs, executes or infers package capability.</summary>
public static class RuntimeDiscovery
{
    static string UnslothLlamaServer(string home) => Path.Combine(home, ".unsloth", "llama.cpp", "build", "bin", "Release", "llama-server.exe");
    static string UnslothPython(string home) => Path.Combine(home, ".unsloth", "studio", "unsloth_studio", "Scripts", "python.exe");

    internal static IEnumerable<string?> CudaLibraryCandidates(string runtime, string python)
    {
        // Prefer a self-contained runtime or CUDA libraries already on PATH.
        yield return null;
        var selected = TorchCudaLibraries(python);
        if (selected != null) yield return selected;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.GetFullPath(runtime).Equals(UnslothLlamaServer(home), StringComparison.OrdinalIgnoreCase)) yield break;
        // The Unsloth build may rely on its Studio environment even when the
        // selected Transformers interpreter belongs to a different environment.
        var bundled = TorchCudaLibraries(UnslothPython(home));
        if (bundled != null && !bundled.Equals(selected, StringComparison.OrdinalIgnoreCase)) yield return bundled;
    }

    static string? TorchCudaLibraries(string python)
    {
        if (string.IsNullOrWhiteSpace(python) || !Path.IsPathFullyQualified(python)) return null;
        var root = Path.GetDirectoryName(Path.GetFullPath(python));
        if (root == null) return null;
        if (Path.GetFileName(root).Equals("Scripts", StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(root);
        if (root == null) return null;
        var libraries = Path.Combine(root, "Lib", "site-packages", "torch", "lib");
        return File.Exists(Path.Combine(libraries, "cublas64_13.dll")) ? libraries : null;
    }

    // Pre-rename override names; still honoured after the MASON_ ones so an existing setup keeps working. Not documented.
    const string LegacyLlamaServer="DBCOWBOY_LLAMA_SERVER",LegacyPython="DBCOWBOY_PYTHON";
    public static (string LlamaServer,string Python) Find() => Find(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),File.Exists);

    public static (string LlamaServer,string Python) Find(Func<string,string?> env,string home,string local,Func<string,bool> exists)
    {
        string First(IEnumerable<string?> candidates) => candidates.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x!.Trim().Trim('"'))
            .FirstOrDefault(x=>Path.IsPathFullyQualified(x)&&exists(x)&&!x.Contains(Path.DirectorySeparatorChar+"WindowsApps"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))??"";
        IEnumerable<string> OnPath(string executable)=>(env("PATH")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries).Select(x=>Path.Combine(x.Trim().Trim('"'),executable));
        string? In(string variable,string executable)=>env(variable) is {Length:>0} folder?Path.Combine(folder,executable):null;
        var llama=First(new[]{env("MASON_LLAMA_SERVER"),env(LegacyLlamaServer)}.Concat(OnPath("llama-server.exe")).Concat(new[]{Path.Combine(local,"llama.cpp","llama-server.exe"),Path.Combine(home,"llama.cpp","build","bin","Release","llama-server.exe"),UnslothLlamaServer(home)}));
        var python=First(new[]{env("MASON_PYTHON"),env(LegacyPython),In("VIRTUAL_ENV",Path.Combine("Scripts","python.exe")),In("CONDA_PREFIX","python.exe")}.Concat(OnPath("python.exe")).Concat(OnPath("python3.exe")).Concat(new[]{UnslothPython(home)}));
        return(llama,python);
    }
}
