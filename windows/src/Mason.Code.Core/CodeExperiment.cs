using System.Text.RegularExpressions;

namespace Mason.Code;

public sealed record ExperimentOrigin(string Parent,Guid Session,Guid? Turn,string Revision);
public sealed record ExperimentHunk(string Title,string Patch,string Id);

public static class CodeExperiment
{
    internal static async Task<string> Git(CodeWorkspace scope,IEnumerable<string> args,CancellationToken ct,string input="",string? index=null,bool raw=false,bool snapshotIdentity=true) =>
        await CodeOwnedHelper.Run(CodeCommand.ResolveExecutable("git",scope.Root),
            (await CodeGitOptions.ForLocalChange(scope,ct)).Concat(new[]{"--no-optional-locks","--literal-pathspecs"}).Concat(snapshotIdentity?new[]{"-c","user.name=Mason Experiment","-c","user.email=mason@localhost"}:[]).Concat(args),
            input,scope.Root,ct,90,2*1024*1024,index,raw);

    internal static async Task<string> Snapshot(CodeWorkspace scope,CancellationToken ct)
    {
        var files=(await CodeGit.FullStatus(scope.Root,ct)).Files;
        var paths=files.SelectMany(f=>f.PreviousPath==null?new[]{f.Path}:new[]{f.Path,f.PreviousPath}).Distinct(StringComparer.Ordinal).ToArray();
        if(paths.Length>2000)throw new InvalidDataException("The experiment exceeds 2,000 changed paths.");
        foreach(var path in paths)scope.Resolve(path);
        string index=Path.Combine(Path.GetTempPath(),"mason-index-"+Guid.NewGuid().ToString("N"));
        try {
            await Git(scope,["read-tree","HEAD"],ct,index:index);
            if(paths.Length>0)
                await Git(scope,["add","--all","--pathspec-from-file=-","--pathspec-file-nul"],ct,string.Join('\0',paths)+"\0",index);
            return await Git(scope,["write-tree"],ct,index:index);
        } finally {if(File.Exists(index))File.Delete(index);if(File.Exists(index+".lock"))File.Delete(index+".lock");}
    }
    public static async Task<ExperimentOrigin> Fork(CodeSession session,string destination,string branch,CancellationToken ct)
    {
        var parent=new CodeWorkspace(session.Workspace);
        var head=await Git(parent,["rev-parse","HEAD"],ct);
        string tree=await Snapshot(parent,ct);
        if(tree!=await Snapshot(parent,ct) || head!=await Git(parent,["rev-parse","HEAD"],ct))
            throw new IOException("The checkout changed while capturing the experiment. Retry.");
        string revision=await Git(parent,["-c","commit.gpgsign=false","commit-tree",tree,"-p",head,"-m","Mason experiment starting state"],ct);
        await CodeWorktree.Create(parent.Root,destination,branch,ct,revision);
        return new(parent.Root,session.Id,session.Turns.LastOrDefault()?.Id,revision);
    }
    public static async Task<string[]> Changed(CodeWorkspace fork,ExperimentOrigin origin,CancellationToken ct)
    {
        string tree=await Snapshot(fork,ct);
        var raw=await Git(fork,["diff","--no-renames","--name-only","-z",origin.Revision,tree,"--"],ct,raw:true);
        var paths=raw.Split('\0',StringSplitOptions.RemoveEmptyEntries);
        foreach(var path in paths)fork.Resolve(path);
        return paths;
    }
    public static async Task<ExperimentHunk[]> Hunks(CodeWorkspace fork,ExperimentOrigin origin,string path,CancellationToken ct)
    {
        path=fork.Relative(fork.Resolve(path));
        var tree=await Snapshot(fork,ct);
        var diff=await Git(fork,["diff","--no-color","--no-ext-diff","--no-textconv","--no-renames","--binary",origin.Revision,tree,"--",path],ct,raw:true);
        if(diff.Length==0)return [];
        var matches=Regex.Matches(diff,@"(?m)^@@ [-+0-9, ]+ @@[^\n]*");
        ExperimentHunk Hunk(string title,string patch)=>new(title,patch,CodeProtocol.Hash(System.Text.Encoding.UTF8.GetBytes(patch)));
        if(matches.Count==0 || diff.Contains("\nnew file mode ") || diff.Contains("\ndeleted file mode ") || diff.Contains("\nold mode "))
            return [Hunk("Whole file · "+path,diff)];
        var header=diff[..matches[0].Index];
        return matches.Select((m,i)=>Hunk(m.Value,header+diff[m.Index..(i+1<matches.Count?matches[i+1].Index:diff.Length)])).ToArray();
    }
    public static async Task Apply(CodeWorkspace fork,ExperimentOrigin origin,string path,string selected,CancellationToken ct)
    {
        var patch=(await Hunks(fork,origin,path,ct)).SingleOrDefault(h=>h.Id==selected)??throw new IOException("The experiment changed. Review the hunk again.");
        var parent=new CodeWorkspace(origin.Parent);parent.Resolve(path);
        if(patch.Title.StartsWith("@@",StringComparison.Ordinal))
        {
            var file=parent.Read(path);
            var lines=Regex.Matches(file.Content,@"[^\r\n]*(?:\r\n|\n|\r|$)").Select(m=>m.Value).Where(l=>l.Length>0).ToList();
            var patchLines=patch.Patch.ReplaceLineEndings("\n").Split('\n').SkipWhile(l=>!l.StartsWith("@@ ")).Skip(1).Where(l=>l.Length>0).ToArray();
            var ops=new List<(char Op,string Text,bool End)>();
            foreach(string l in patchLines) {
                if(l.StartsWith(@"\ No newline") && ops.Count>0){var previous=ops[^1];ops[^1]=(previous.Op,previous.Text,false);continue;}
                if(l[0] is not (' ' or '-' or '+'))throw new InvalidDataException("Unsupported hunk record.");
                ops.Add((l[0],l[1..],true));
            }
            var expected=ops.Where(x=>x.Op!='+').ToArray();
            static string Body(string line)=>line.TrimEnd('\r','\n');
            var matches=Enumerable.Range(0,Math.Max(0,lines.Count-expected.Length+1)).Where(at=>
                expected.Select((x,k)=>Body(lines[at+k])==x.Text && (lines[at+k].Length>Body(lines[at+k]).Length)==x.End).All(b=>b)).Take(2).ToArray();
            if(expected.Length==0 || matches.Length!=1)throw new IOException("The target hunk is missing or ambiguous in the parent. Review the current diff.");
            int index=matches[0],i=index;
            string ending=lines.Skip(index).Concat(lines).Select(l=>l[Body(l).Length..]).FirstOrDefault(e=>e.Length>0)??Environment.NewLine;
            var rebuilt=new System.Text.StringBuilder();
            foreach(var op in ops) {
                if(op.Op==' ')rebuilt.Append(lines[i++]);
                else if(op.Op=='-')i++;
                else rebuilt.Append(op.Text).Append(op.End?ending:"");
            }
            string updated=string.Concat(lines.Take(index))+rebuilt+string.Concat(lines.Skip(i));
            parent.Edit(path,file.Sha256,file.Content,updated,(_,_)=>{});return;
        }
        // git apply checks the hunk's original context. The user's index is left intact.
        await Git(parent,["apply","--check","--whitespace=nowarn","-"],ct,patch.Patch);
        await Git(parent,["apply","--whitespace=nowarn","-"],ct,patch.Patch);
    }
}
