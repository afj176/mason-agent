namespace Mason;

/// <summary>Find existing executables; never installs, executes or infers package capability.</summary>
public static class RuntimeDiscovery
{
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
        var llama=First(new[]{env("MASON_LLAMA_SERVER"),env(LegacyLlamaServer)}.Concat(OnPath("llama-server.exe")).Concat(new[]{Path.Combine(local,"llama.cpp","llama-server.exe"),Path.Combine(home,"llama.cpp","build","bin","Release","llama-server.exe"),Path.Combine(home,".unsloth","llama.cpp","build","bin","Release","llama-server.exe")}));
        var python=First(new[]{env("MASON_PYTHON"),env(LegacyPython),In("VIRTUAL_ENV",Path.Combine("Scripts","python.exe")),In("CONDA_PREFIX","python.exe")}.Concat(OnPath("python.exe")).Concat(OnPath("python3.exe")).Concat(new[]{Path.Combine(home,".unsloth","studio","unsloth_studio","Scripts","python.exe")}));
        return(llama,python);
    }
}
