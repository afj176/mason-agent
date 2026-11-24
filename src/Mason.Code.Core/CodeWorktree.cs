using System.Text.RegularExpressions;

namespace Mason.Code;

public static class CodeWorktree
{
    // Explicit user operation. The coding model has no authority to select another workspace.
    public static async Task<string> Create(string project,string destination,string branch,CancellationToken ct,string revision="HEAD")
    {
        if(revision!="HEAD" && !Regex.IsMatch(revision,@"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z"))throw new ArgumentException("Invalid base revision.");
        var source=new CodeWorkspace(project);
        var target=Path.GetFullPath(destination);
        if(!Regex.IsMatch(branch,@"\A[\w][\w/-]{0,100}\z")||branch.Contains("..")||branch.Contains("//")||branch.EndsWith('/'))
            throw new ArgumentException("Use a new Git branch name.");
        bool Overlaps(string a,string b)=>a.Equals(b,StringComparison.OrdinalIgnoreCase) || a.StartsWith(Path.TrimEndingDirectorySeparator(b)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        if(File.Exists(target)||Directory.Exists(target)||Overlaps(target,source.Root)||Overlaps(source.Root,target))
            throw new ArgumentException("Choose a new worktree path outside the source checkout.");
        var parent=Path.GetDirectoryName(target)??throw new ArgumentException("Missing destination parent.");
        _=new CodeWorkspace(parent);
        var git=CodeCommand.ResolveExecutable("git",source.Root);
        await CodeOwnedHelper.Run(git,[..await CodeGitOptions.ForLocalChange(source,ct),"worktree","add","-b",branch,"--",target,revision],"",source.Root,ct,120);
        return new CodeWorkspace(target).Root;
    }
}
