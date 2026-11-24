namespace Mason.Code;

internal static class CodeGitOptions
{
    public static async Task<string[]> ForLocalChange(CodeWorkspace scope,CancellationToken token)
    {
        var keys=await CodeOwnedHelper.Run(CodeCommand.ResolveExecutable("git",scope.Root),
            ["config","--null","--name-only","--list"],"",scope.Root,token);
        var options=new List<string>{"-c","core.hooksPath=NUL","-c","core.fsmonitor=false","-c","submodule.recurse=false"};
        foreach(var prefix in keys.Split('\0',StringSplitOptions.RemoveEmptyEntries).Where(k=>k.StartsWith("filter.",StringComparison.OrdinalIgnoreCase))
            .Select(k=>k[..k.LastIndexOf('.')]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach(var setting in new[]{".clean=",".smudge=",".process=",".required=false"}) {options.Add("-c");options.Add(prefix+setting);}
        }
        return options.ToArray();
    }
}
