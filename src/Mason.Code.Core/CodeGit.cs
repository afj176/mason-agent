using System.Text;

namespace Mason.Code;

public sealed record CodeGitFile(string Path, string Status, string? PreviousPath = null);
public sealed record CodeGitStatus(bool Available, string Branch, CodeGitFile[] Files, string Worktrees, string Detail);

/// <summary>Bounded metadata-only Git observation. Never starts Git, hooks, filters or repository-configured programs.</summary>
public static class CodeGit
{
    // Git status can execute clean/process filters from agent-writable repository configuration.
    // Automatic observation therefore parses bounded metadata, never an external Git process.
    public static Task<CodeGitStatus> Inspect(string workspace, CancellationToken ct)
        => Task.Run(() => InspectMetadata(workspace,ct),ct);

    static CodeGitStatus InspectMetadata(string workspace,CancellationToken ct)
    {
        try
        {
            var scope=new CodeWorkspace(workspace);var metadata=Path.Combine(scope.Root,".git");
            ct.ThrowIfCancellationRequested();
            CodeWorkspace.CheckLinks(metadata);
            if(File.Exists(metadata))return new(false,"Linked worktree",[],"Not inspected","Linked Git metadata is not followed automatically. Session changes remain available.");
            if(!Directory.Exists(metadata))return new(false,"Folder workspace",[],"","The selected folder has no local Git metadata directory.");
            string Read(string path)
            {
                ct.ThrowIfCancellationRequested();CodeWorkspace.CheckLinks(path);
                using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                if(input.Length>8192)throw new InvalidDataException("Git metadata exceeds 8 KiB.");
                using var reader=new StreamReader(input,new UTF8Encoding(false,true),true);return reader.ReadToEnd().Trim();
            }
            var head=Read(Path.Combine(metadata,"HEAD"));string branch;
            if(head.StartsWith("ref: refs/heads/",StringComparison.Ordinal))branch=head[16..];
            else if(head.Length is 40 or 64&&head.All(Uri.IsHexDigit))branch="Detached · "+head[..12];
            else throw new InvalidDataException("Unsupported HEAD metadata.");
            if(branch.Length is 0 or >240||branch.Any(char.IsControl))throw new InvalidDataException("Unsupported branch name.");
            return new(true,branch,[],"Linked worktrees are not followed automatically.","Metadata only · full Git status unavailable; review session changes. Repository programs/filters are never run automatically.");
        }
        catch(Exception e)when(e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {return new(false,"Repository unavailable",[],"Not inspected","Safe Git metadata unavailable · "+e.Message);}
    }
    public static CodeGitStatus Parse(string porcelain, string worktrees)
    {
        var records = porcelain.Split('\0', StringSplitOptions.RemoveEmptyEntries); string branch = "Git repository"; var files = new List<CodeGitFile>();
        for (int i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.StartsWith("## ")) { branch = record[3..]; continue; }
            if (record.Length < 4 || record[2] != ' ') throw new InvalidDataException("Invalid Git status record.");
            var status = record[..2]; string? previous = null;
            if (status.Contains('R') || status.Contains('C')) { if (++i >= records.Length) throw new InvalidDataException("Incomplete Git rename."); previous = records[i]; }
            files.Add(new(record[3..].Replace('\\', '/'), status, previous));
        }
        return new(true, branch, files.ToArray(), worktrees, files.Count == 0 ? "Working tree clean" : $"{files.Count} working-tree changes · includes edits outside this session");
    }
}

