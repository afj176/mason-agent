using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason.Code;

public sealed record ReviewedPatch(string Path,ExperimentHunk Hunk);
public sealed record DeliveryDraft(Guid Id,string Base,string BaseCommit,string Commit,string Title,string Body,string Repository,
    string Branch,string[] Paths,string? Url=null);
public sealed record DeliveryFeedback(string Head,string Url,string[] Observations);
public static class CodeDelivery
{
    static void BranchName(string name)
    {
        if(!Regex.IsMatch(name,@"\A[A-Za-z0-9][A-Za-z0-9_./-]{0,99}\z")||name.Contains("..")||name.Contains("//")||name.EndsWith('/')||name.EndsWith(".lock"))
            throw new ArgumentException("Use the target branch name, such as main.");
    }
    static bool Oid(string value)=>Regex.IsMatch(value,@"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z");
    public static async Task<(string Revision,ReviewedPatch[] Patches)> Review(CodeWorkspace workspace,string branch,CancellationToken ct)
    {
        BranchName(branch);
        string oid;
        try{oid=await CodeExperiment.Git(workspace,["rev-parse","--verify","refs/remotes/origin/"+branch+"^{commit}"],ct);}
        catch(InvalidDataException){oid=await CodeExperiment.Git(workspace,["rev-parse","--verify","refs/heads/"+branch+"^{commit}"],ct);}
        if(!Oid(oid))throw new InvalidDataException("Cannot resolve target branch.");
        var origin=new ExperimentOrigin(workspace.Root,Guid.Empty,null,oid);var patches=new List<ReviewedPatch>();
        foreach(var path in await CodeExperiment.Changed(workspace,origin,ct)){
            foreach(var h in await CodeExperiment.Hunks(workspace,origin,path,ct))patches.Add(new(path,h));
            if(patches.Count>128 || patches.Sum(p=>p.Hunk.Patch.Length)>1500000)throw new InvalidDataException("Narrow the delivery to a smaller branch.");
        }
        return(oid,patches.ToArray());
    }
    public static string Repository(string remote)
    {
        var m=Regex.Match(remote.Trim(),@"\A(?:https://github\.com/|git@github\.com:)([\w.-]+/[\w.-]+?)(?:\.git)?/?\z",RegexOptions.IgnoreCase);
        if(!m.Success||m.Groups[1].Value.Split('/').Any(x=>x is "." or ".."))throw new InvalidDataException("Choose a checkout with a GitHub origin.");
        return m.Groups[1].Value;
    }
    public static async Task<DeliveryDraft> Draft(CodeWorkspace scope,CodeSession session,string branch,string pinned,string title,IReadOnlyCollection<string> selected,CancellationToken ct)
    {
        if(title.Trim().Length is 0 or >120 || selected.Count is <1 or >128)throw new ArgumentException("Choose reviewed hunks and a pull request title.");
        var review=await Review(scope,branch,ct);
        if(review.Revision!=pinned)throw new IOException("The base moved. Review again.");
        var patches=review.Patches.Where(p=>selected.Contains(p.Hunk.Id)).ToArray();
        if(patches.Length!=selected.Distinct().Count())throw new IOException("A selected hunk changed. Review it again.");
        string index=Path.Combine(Path.GetTempPath(),"mason-delivery-"+Guid.NewGuid().ToString("N"));
        string tree;
        try {
            await CodeExperiment.Git(scope,["read-tree",pinned],ct,index:index);
            // Hunks for a file share a header. Git applies the combined patch against the same starting index.
            var patch=new System.Text.StringBuilder();
            foreach(var group in patches.GroupBy(p=>p.Path)){
                bool first=true;
                foreach(var h in group){
                    string text=h.Hunk.Patch;
                    if(!first){int at=text.IndexOf("\n@@ ",StringComparison.Ordinal);if(at<0)throw new InvalidDataException("Invalid hunk group.");text=text[(at+1)..];}
                    patch.Append(text);first=false;
                }
            }
            await CodeExperiment.Git(scope,["apply","--cached","--whitespace=nowarn","-"],ct,patch.ToString(),index);
            tree=await CodeExperiment.Git(scope,["write-tree"],ct,index:index);
        } finally {if(File.Exists(index))File.Delete(index);if(File.Exists(index+".lock"))File.Delete(index+".lock");}
        string commit=await CodeExperiment.Git(scope,["-c","commit.gpgsign=false","commit-tree",tree,"-p",pinned,"-m",title.Trim()],ct,snapshotIdentity:false);
        string remote="";
        try{remote=Repository(await CodeExperiment.Git(scope,["remote","get-url","origin"],ct));}catch(InvalidDataException){}
        string[] paths=patches.Select(p=>p.Path).Distinct().ToArray();
        var plan=session.Turns.LastOrDefault(t=>t.Acceptance!=null)?.Acceptance;
        string evidence="No passing receipt for this selected tree.";
        if(plan?.VerifiedRevision!=null && plan.Results.Count>0 && plan.Results.All(r=>r.Passed) && tree==await CodeExperiment.Snapshot(scope,ct) &&
            CodeRevision.Measure(scope,plan,ct)==plan.VerifiedRevision)evidence=CodeAcceptance.Receipt(plan);
        var id=Guid.NewGuid();
        string body="## Changes\n\n"+string.Join("\n",paths.Select(p=>"- "+p))+"\n\n## Verification\n\n"+evidence[..Math.Min(12000,evidence.Length)];
        return new(id,branch,pinned,commit,title.Trim(),body,remote,"mason/review-"+id.ToString("N")[..12],paths);
    }
    static Task<string> Gh(CodeWorkspace scope,string[] args,CancellationToken ct)=>CodeOwnedHelper.Run(CodeCommand.ResolveExecutable("gh",scope.Root),args,"",scope.Root,ct,60);
    public static async Task<string> Publish(CodeWorkspace workspace,DeliveryDraft draft,CancellationToken ct)
    {
        BranchName(draft.Base);BranchName(draft.Branch);
        if(!Oid(draft.Commit)||!Oid(draft.BaseCommit)||draft.Repository!=Repository(await CodeExperiment.Git(workspace,["remote","get-url","origin"],ct)))
            throw new InvalidDataException("The selected repository changed.");
        string tip=await Gh(workspace,["api","repos/"+draft.Repository+"/commits/"+Uri.EscapeDataString(draft.Base),"--jq",".sha"],ct);
        if(tip!=draft.BaseCommit)throw new IOException("The remote base moved. Refresh it and review again.");
        // No force option. Retry can only publish the same immutable commit or fast-forward it.
        await CodeExperiment.Git(workspace,["push","https://github.com/"+draft.Repository+".git",draft.Commit+":refs/heads/"+draft.Branch],ct);
        var prior=await Gh(workspace,["pr","list","--repo",draft.Repository,"--head",draft.Branch,"--base",draft.Base,"--state","all","--json","url,headRefOid"],ct);
        using(var list=JsonDocument.Parse(prior))foreach(var pr in list.RootElement.EnumerateArray()){
            if(pr.GetProperty("headRefOid").GetString()!=draft.Commit)throw new IOException("A pull request already uses this branch with other content.");
            return pr.GetProperty("url").GetString()!;
        }
        return await Gh(workspace,["pr","create","--draft","--repo",draft.Repository,"--base",draft.Base,"--head",draft.Branch,"--title",draft.Title,"--body",draft.Body],ct);
    }
    public static async Task<DeliveryFeedback> Feedback(CodeWorkspace scope,DeliveryDraft draft,CancellationToken ct)
    {
        if(draft.Url==null || !Regex.IsMatch(draft.Url,@"\Ahttps://github\.com/"+Regex.Escape(draft.Repository)+@"/pull/\d+\z",RegexOptions.IgnoreCase))
            throw new InvalidDataException("Publish or attach a pull request for this draft first.");
        string number=draft.Url[(draft.Url.LastIndexOf('/')+1)..];
        string status=await Gh(scope,["pr","view",number,"--repo",draft.Repository,"--json","url,headRefOid,statusCheckRollup,reviews"],ct);
        string comments=await Gh(scope,["api","repos/"+draft.Repository+"/pulls/"+number+"/comments?per_page=100"],ct);
        return ParseFeedback(status,comments);
    }
    public static DeliveryFeedback ParseFeedback(string state,string comments)
    {
        using var pr=JsonDocument.Parse(state);using var notes=JsonDocument.Parse(comments);var root=pr.RootElement;
        var result=new List<string>();
        string Field(JsonElement o,string name)=>o.TryGetProperty(name,out var s)?s.ToString():"";
        void Add(string s){if(s.Trim().Length>0 && result.Count<64)result.Add(s[..Math.Min(s.Length,1200)]);}
        foreach(var check in root.GetProperty("statusCheckRollup").EnumerateArray()){
            string outcome=Field(check,"conclusion");if(outcome.Length==0)outcome=Field(check,"state");
            if(outcome is "FAILURE" or "ERROR" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED")
                Add("CI · "+Field(check,"name")+Field(check,"context")+" · "+outcome+" · "+Field(check,"detailsUrl"));
        }
        foreach(var review in root.GetProperty("reviews").EnumerateArray())if(Field(review,"state")=="CHANGES_REQUESTED")Add("Review · "+Field(review,"body"));
        foreach(var c in notes.RootElement.EnumerateArray())Add(Field(c,"path")+":"+Field(c,"line")+" · "+Field(c,"body"));
        return new(Field(root,"headRefOid"),Field(root,"url"),result.ToArray());
    }
}
