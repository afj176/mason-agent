using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mason.Code;

public sealed record ProjectNote(Guid Id, string Text, string Source, string? SourceHash, DateTimeOffset Saved);

public sealed class CodeProject
{
    public Guid Id { get; init; }=Guid.NewGuid();
    public string Workspace { get; init; }="";
    public Guid? LastSessionId { get; set; }
    public bool Expanded { get; set; }
    /// <summary>Notes saved by earlier versions. CodeNotes moves them to the data folder the first time they are read.</summary>
    public ProjectNote[] Notes { get; set; } = [];
    public CodeShellKind? PreferredShell { get; set; }
    [JsonIgnore] public string Name=>Path.GetFileName(Workspace) is {Length:>0} name?name:Workspace;
}

public sealed partial class CodeStore
{
    sealed class ProjectState
    {
        public int Version { get; set; }=1;
        public Guid? SelectedProjectId { get; set; }
        public List<CodeProject> Projects { get; set; }=[];
        public List<CodeProject> RemovedProjects { get; set; }=[];
    }
    ProjectState projectState=new();
    string ProjectPath=>Path.Combine(Root,"code-projects.json");
    public IReadOnlyList<CodeProject> Projects=>projectState.Projects.AsReadOnly();
    public CodeProject? SelectedProject=>projectState.Projects.FirstOrDefault(p=>p.Id==projectState.SelectedProjectId);
    public CodeSession? SelectedSession=>SelectedProject is {} project?LastSession(project.Id):null;

    static string ProjectFolder(string folder)
    {
        if(string.IsNullOrWhiteSpace(folder)||folder.Length>4096||!Path.IsPathFullyQualified(folder))throw new InvalidDataException("A project requires an absolute folder path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }
    static bool SameProject(string a,string b)=>ProjectFolder(a).Equals(ProjectFolder(b),StringComparison.OrdinalIgnoreCase);
    CodeProject RequireProject(Guid id)=>projectState.Projects.SingleOrDefault(p=>p.Id==id)??throw new ArgumentException("The selected project is not registered.");
    public CodeProject? ProjectFor(CodeSession session)=>projectState.Projects.FirstOrDefault(p=>SameProject(p.Workspace,session.Workspace));
    public CodeProject? ProjectForWorkspace(string workspace)=>projectState.Projects.FirstOrDefault(p=>SameProject(p.Workspace,workspace));
    public static bool NoteCurrent(CodeWorkspace scope, ProjectNote note) => CodeNotes.Current(scope, note);
    public void SetProjectShell(Guid id,CodeShellKind shell)
    {
        if(!Enum.IsDefined(shell))throw new ArgumentException("Unknown shell.");
        var project=RequireProject(id);var previous=project.PreferredShell;project.PreferredShell=shell;
        try{SaveProjects();}catch{project.PreferredShell=previous;throw;}
    }
    public CodeSession[] SessionsFor(Guid projectId)
    {
        var project=RequireProject(projectId);
        return Sessions.Where(s=>SameProject(s.Workspace,project.Workspace)).OrderByDescending(s=>s.Updated).ToArray();
    }
    public CodeSession? LastSession(Guid projectId)
    {
        var project=RequireProject(projectId);var sessions=SessionsFor(projectId);
        return sessions.FirstOrDefault(s=>s.Id==project.LastSessionId)??sessions.FirstOrDefault();
    }
    public CodeProject AddProject(string folder)
    {
        // Reuse the existing physical-folder/link checks; adding a project never
        // changes an existing conversation's workspace or permission.
        string workspace=new CodeWorkspace(folder).Root;
        if(projectState.Projects.FirstOrDefault(p=>SameProject(p.Workspace,workspace)) is {} known)return known;
        if(projectState.Projects.Count>=512)throw new InvalidOperationException("The project list is limited to 512 folders.");
        var removed = projectState.RemovedProjects.FirstOrDefault(p => SameProject(p.Workspace, workspace));
        var project = removed ?? new CodeProject { Workspace = workspace, Expanded = true };
        if (removed != null) projectState.RemovedProjects.Remove(removed);
        projectState.Projects.Add(project);
        try { SaveProjects(); }
        catch
        {
            projectState.Projects.Remove(project);
            if (removed != null) projectState.RemovedProjects.Add(removed);
            throw;
        }
        return project;
    }
    public void RemoveProject(Guid id)
    {
        var project = RequireProject(id);
        if (SessionsFor(id).Any(s => s.State is "Running" or "Awaiting review"))
            throw new InvalidOperationException("Finish or stop the project's active run before removing it.");

        // Only move the catalog entry. Journals, workspace files and offline folders are never opened.
        var projects = projectState.Projects;
        int index = projects.IndexOf(project);
        var previous = projectState.SelectedProjectId;
        projects.RemoveAt(index);
        projectState.RemovedProjects.Add(project);
        if (previous == id)
            projectState.SelectedProjectId = projects.Count == 0 ? null : projects[Math.Min(index, projects.Count - 1)].Id;
        try { SaveProjects(); }
        catch
        {
            projectState.RemovedProjects.Remove(project);
            projects.Insert(index, project);
            projectState.SelectedProjectId = previous;
            throw;
        }
    }
    public void ActivateProject(Guid projectId,Guid? sessionId=null)
    {
        var project=RequireProject(projectId);
        var selected=sessionId.HasValue?Sessions.SingleOrDefault(s=>s.Id==sessionId.Value)??throw new ArgumentException("The selected session does not exist."):LastSession(projectId);
        if(selected!=null&&!SameProject(selected.Workspace,project.Workspace))throw new ArgumentException("A session can only be selected inside its own project.");
        var previous=projectState.SelectedProjectId;var last=project.LastSessionId;bool expanded=project.Expanded;
        projectState.SelectedProjectId=projectId;project.LastSessionId=selected?.Id;project.Expanded=true;
        try{SaveProjects();}catch{projectState.SelectedProjectId=previous;project.LastSessionId=last;project.Expanded=expanded;throw;}
    }
    public void SetProjectExpanded(Guid id,bool expanded)
    {
        var project=RequireProject(id);if(project.Expanded==expanded)return;
        bool previous=project.Expanded;project.Expanded=expanded;
        try{SaveProjects();}catch{project.Expanded=previous;throw;}
    }
    void SaveProjects()
    {
        var data=JsonSerializer.SerializeToUtf8Bytes(projectState,Json);
        if(data.Length>2*1024*1024)throw new InvalidDataException("The saved project list exceeds 2 MiB.");
        WriteSnapshot(ProjectPath,data);
    }
    void LoadProjects()
    {
        bool changed=false;
        if(File.Exists(ProjectPath))
        {
            if(new FileInfo(ProjectPath).Length>2*1024*1024)throw new InvalidDataException("The saved project list exceeds 2 MiB.");
            projectState=JsonSerializer.Deserialize<ProjectState>(File.ReadAllBytes(ProjectPath),Json)??throw new InvalidDataException("Empty project list.");
            if(projectState.Version!=1||projectState.Projects==null||projectState.RemovedProjects==null||projectState.Projects.Count>512)throw new InvalidDataException("Unsupported project list.");
            var ids=new HashSet<Guid>();var folders=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var project in projectState.Projects.Concat(projectState.RemovedProjects))
            {
                if(project==null||project.Id==Guid.Empty||!ids.Add(project.Id)||!folders.Add(ProjectFolder(project.Workspace)))throw new InvalidDataException("Duplicate or invalid project identity.");
                if(project.PreferredShell.HasValue&&!Enum.IsDefined(project.PreferredShell.Value))throw new InvalidDataException("Unknown project shell.");
                if(project.LastSessionId is Guid id&&Sessions.FirstOrDefault(s=>s.Id==id) is {} session&&!SameProject(session.Workspace,project.Workspace))throw new InvalidDataException("A saved project refers to another project's session.");
            }
            if(projectState.SelectedProjectId.HasValue && SelectedProject == null)throw new InvalidDataException("The saved selected project is not registered.");
        }
        // Migration is derived from journals, without rewriting or relocating
        // them. Missing/offline folders remain visible and recoverable.
        var removedFolders = projectState.RemovedProjects.Select(p => ProjectFolder(p.Workspace)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var session in Sessions.OrderByDescending(s=>s.Updated))
        {
            if(removedFolders.Contains(ProjectFolder(session.Workspace)) || ProjectFor(session)!=null)continue;
            if(projectState.Projects.Count>=512)throw new InvalidDataException("The saved project list exceeds 512 folders.");
            projectState.Projects.Add(new(){Workspace=ProjectFolder(session.Workspace),LastSessionId=session.Id});changed=true;
        }
        foreach(var project in projectState.Projects)
        {
            var last=LastSession(project.Id)?.Id;
            if(project.LastSessionId!=last){project.LastSessionId=last;changed=true;}
        }
        if(projectState.SelectedProjectId==null&&projectState.Projects.Count>0)
        {
            var project=Sessions.OrderByDescending(s=>s.Updated).Select(ProjectFor).FirstOrDefault(p=>p!=null)??projectState.Projects[0];
            projectState.SelectedProjectId=project.Id;project.Expanded=true;changed=true;
        }
        if(changed)SaveProjects();
    }
}
