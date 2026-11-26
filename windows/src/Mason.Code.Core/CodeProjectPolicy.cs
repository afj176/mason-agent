using System.Text;
using System.Text.Json;

namespace Mason.Code;

public sealed record CodeProjectPolicy(string Name,string Workspace,string[] DisabledTools,string[]? Workflows,DateTimeOffset? Expires,
    bool EnableHooks=false)
{
    static string Key(string workspace)=>CodeProtocol.Hash(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)).ToLowerInvariant()));
    static string Location(string host,string workspace)=>Path.Combine(host,"project-policies",Key(workspace)+".json");
    public static CodeProjectPolicy? Load(string host,string workspace,bool includeExpired=false)
    {
        string path=Location(host,workspace);
        if(!File.Exists(path))return null;
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        if(stream.Length>32768)throw new InvalidDataException("Project policy is too large.");
        var p=JsonSerializer.Deserialize<CodeProjectPolicy>(stream)??throw new InvalidDataException("Invalid project policy.");
        Validate(p,workspace);
        return !includeExpired && p.Expires<=DateTimeOffset.UtcNow?null:p;
    }
    static void Validate(CodeProjectPolicy p,string workspace)
    {
        if(!string.Equals(p.Workspace,workspace,StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length>80 ||
            p.DisabledTools is not {Length:<=128} || p.Workflows is {Length:>32} || p.DisabledTools.Concat(p.Workflows??[]).Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>80))
            throw new InvalidDataException("Invalid project policy name, workspace, tools or workflows.");
    }
    public static void Set(string host,string workspace,CodeProjectPolicy? policy)
    {
        string path=Location(host,workspace);
        if(CodeSandbox.ContainsHost(workspace,path))throw new InvalidOperationException("Store policies outside the project.");
        if(policy==null){if(File.Exists(path))File.Delete(path);return;}
        Validate(policy,workspace);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(policy));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public void Check(string tool,string? workflow=null)
    {
        if(DisabledTools.Contains(tool,StringComparer.Ordinal) || tool=="run_workflow" && Workflows!=null && !Workflows.Contains(workflow,StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The active project policy "+Name+" disallows "+(workflow??tool)+".");
    }
}
