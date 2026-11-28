using System.Text;
using System.Text.Json;

namespace Mason.Code;

public sealed record CodeProjectPolicy(string Name,string Workspace,string[] DisabledTools,string[]? Workflows,DateTimeOffset? Expires,
    bool EnableHooks=false,string[]? DisabledSkills=null)
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
            p.DisabledTools is not {Length:<=128} || p.Workflows is {Length:>32} || p.DisabledSkills is {Length:>128} ||
            p.DisabledTools.Concat(p.Workflows??[]).Concat(p.DisabledSkills??[]).Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>80))
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
    /// <summary>Changes this project's policy, starting one named after the folder when there is none, and saves it.</summary>
    public static CodeProjectPolicy Edit(string host,string workspace,Func<CodeProjectPolicy,CodeProjectPolicy> change)
    {
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var current=Load(host,root,true)??new CodeProjectPolicy("for "+Path.GetFileName(root),root,[],null,null);
        var changed=change(current);
        Set(host,root,changed);
        return changed;
    }
    public const string Usage = """
        /policy                         This project's policy
        /policy disable TOOL…           Switch tools off for this project (enable TOOL… switches them back on)
        /policy workflows NAME…         The only workflows that may run (any: no limit)
        /policy hooks on|off            Whether workflows marked afterEdit or beforeVerify run by themselves, in Auto
        /policy expires DAYS            When the policy lapses (never: it stays)
        /policy name NAME               What the policy is called when it refuses something
        /policy clear                   Remove the policy
        """;

    public string Described()
    {
        var tools=DisabledTools.Length==0?"none":string.Join(", ",DisabledTools.Order(StringComparer.Ordinal));
        var flows=Workflows==null?"any":Workflows.Length==0?"none may run":string.Join(", ",Workflows.Order(StringComparer.Ordinal));
        var skills=DisabledSkills is {Length:>0}?string.Join(", ",DisabledSkills.Order(StringComparer.Ordinal)):"none";
        var expires=Expires is {} at?at.ToLocalTime().ToString("g")+(at<=DateTimeOffset.UtcNow?" (lapsed)":""):"never";
        return "Policy "+Name+"\ntools off   "+tools+"\nskills off  "+skills+"\nworkflows   "+flows+"\nhooks       "+(EnableHooks?"on: workflows marked afterEdit or beforeVerify run by themselves in Auto":"off")+"\nexpires     "+expires;
    }

    /// <summary>The /policy command, shared by the command line and the app. Only the user can run it; the model has no tool for it.</summary>
    public static string Command(string rest,string host,string workspace)
    {
        var words=rest.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if(words.Length==0)return Load(host,root,true)?.Described()??"This project has no policy: every tool is available, any workflow may run, and hooks are off.\n"+Usage;
        var args=words.Skip(1).ToArray();
        switch(words[0].ToLowerInvariant())
        {
            case "disable":
                if(args.Length==0)throw new ArgumentException("Name the tools to switch off.");
                foreach(var tool in args)if(CodeToolCatalog.Find(tool)?.AlwaysOn==true)throw new InvalidOperationException(tool+" is always on: Mason cannot work without it.");
                return Edit(host,root,p=>p with{DisabledTools=p.DisabledTools.Concat(args).Distinct().Order(StringComparer.Ordinal).ToArray()}).Described();
            case "enable":
                if(args.Length==0)throw new ArgumentException("Name the tools to switch back on.");
                return Edit(host,root,p=>p with{DisabledTools=p.DisabledTools.Where(t=>!args.Contains(t)).ToArray()}).Described();
            case "workflows":
                if(args.Length==0)throw new ArgumentException("Name the workflows that may run, or any.");
                return Edit(host,root,p=>p with{Workflows=args.Length==1&&args[0].Equals("any",StringComparison.OrdinalIgnoreCase)?null:args}).Described();
            case "hooks":
                if(args.Length!=1||args[0].ToLowerInvariant() is not ("on" or "off"))throw new ArgumentException("Use /policy hooks on or /policy hooks off.");
                return Edit(host,root,p=>p with{EnableHooks=args[0].Equals("on",StringComparison.OrdinalIgnoreCase)}).Described();
            case "expires":
                if(args.Length!=1)throw new ArgumentException("Use /policy expires DAYS, or /policy expires never.");
                if(args[0].Equals("never",StringComparison.OrdinalIgnoreCase))return Edit(host,root,p=>p with{Expires=null}).Described();
                if(!int.TryParse(args[0],out var days)||days is <1 or >3650)throw new ArgumentException("Use a number of days from 1 to 3650, or never.");
                return Edit(host,root,p=>p with{Expires=DateTimeOffset.UtcNow.AddDays(days)}).Described();
            case "name":
                if(args.Length==0)throw new ArgumentException("Give the policy a name.");
                return Edit(host,root,p=>p with{Name=string.Join(' ',args)}).Described();
            case "clear":
                Set(host,root,null);
                return "Removed this project's policy.";
            default:
                return Usage;
        }
    }
    /// <summary>Switches one tool on or off. Tools the loop cannot run without stay on.</summary>
    public static CodeProjectPolicy SwitchTool(string host,string workspace,string tool,bool on)
    {
        var info=CodeToolCatalog.Find(tool)??throw new ArgumentException("There is no tool named "+tool+". /tools lists them.");
        if(!on&&info.AlwaysOn)throw new InvalidOperationException(info.Name+" is always on: Mason cannot work without it.");
        return Edit(host,workspace,p=>p with{DisabledTools=on?p.DisabledTools.Where(t=>t!=info.Name).ToArray():p.DisabledTools.Append(info.Name).Distinct().Order(StringComparer.Ordinal).ToArray()});
    }
    public void Check(string tool,string? workflow=null)
    {
        if(DisabledTools.Contains(tool,StringComparer.Ordinal) && CodeToolCatalog.Find(tool)?.AlwaysOn!=true || tool=="run_workflow" && Workflows!=null && !Workflows.Contains(workflow,StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The active project policy "+Name+" disallows "+(workflow??tool)+".");
    }
}
