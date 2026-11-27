using System.Text.Json;
using System.Text.Json.Nodes;
using Mason.Code;

static class CodingProjectChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        string area=Path.Combine(root,"projects");Directory.CreateDirectory(area);
        string a=Path.Combine(area,"one","site"),b=Path.Combine(area,"two","site");Directory.CreateDirectory(a);Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a,"route.txt"),"project A");File.WriteAllText(Path.Combine(b,"route.txt"),"project B");
        string profile=Path.Combine(area,"profile");Guid aProject,aFirst,bSession;int count;
        using(var store=new CodeStore(profile))
        {
            var first=store.New(a);first.Title="First A";first.Permission=CodePermission.Auto;first.Draft="Retain A draft";first.SidebarVisible=false;first.ViewerDismissed=true;first.Model=Guid.NewGuid();store.Save(first);aFirst=first.Id;
            var other=store.New(b);other.Title="First B";other.Draft="Retain B draft";store.Save(other);bSession=other.Id;
            var second=store.New(a);second.Title="Second A";store.Save(second);
            aProject=store.ProjectFor(first)!.Id;var bProject=store.ProjectFor(other)!;
            check(store.Projects.Count==2&&store.Projects.All(p=>p.Name=="site")&&store.Projects.Select(p=>p.Id).Distinct().Count()==2,"same-named project folders retain separate identities and grouped sessions");
            count=store.Sessions.Count;
            var known=store.AddProject(Path.Combine(a,"."));
            check(known.Id==aProject&&store.Projects.Count==2&&store.Sessions.Count==count,"adding an existing normalized project does not create another project or empty session");
            store.ActivateProject(aProject,first.Id);store.ActivateProject(bProject.Id,other.Id);store.ActivateProject(aProject);
            check(store.SelectedSession==first&&store.SessionsFor(aProject).Length==2&&store.SessionsFor(bProject.Id).Single()==other,"switching projects restores their explicitly selected sessions without manufacturing conversations");
            check(first.Draft=="Retain A draft"&&other.Draft=="Retain B draft"&&!first.SidebarVisible&&first.ViewerDismissed&&other.Permission==CodePermission.ReadOnly,"project switching preserves drafts, panels, model and independent permissions");
            bool cross=false;try{store.ActivateProject(aProject,other.Id);}catch(ArgumentException){cross=true;}
            check(cross&&store.SelectedSession==first&&first.Workspace==a&&other.Workspace==b,"cross-project session selection is rejected before changing folder context");
            var model=new EditModel(new CodeWorkspace(a).Read("route.txt").Sha256);
            store.ActivateProject(bProject.Id,other.Id);
            await new CodeEngine(store,model,(_,_)=>throw new Exception("Unexpected review"),()=>{}).Run(first,"Edit route.txt in A while preserving the other project.",default);
            check(first.State=="Completed"&&File.ReadAllText(Path.Combine(a,"route.txt"))=="project A edited"&&File.ReadAllText(Path.Combine(b,"route.txt"))=="project B","the real engine remains bound to its session folder even when another project is selected");
            // A run consumes its draft and resets viewer dismissal for its new
            // request. Persist a new unsent draft/dismissal before restart.
            first.Draft="Retain A draft";first.SidebarVisible=false;first.ViewerDismissed=true;store.Save(first);
            var fresh=store.New(a);
            check(fresh.Permission==CodePermission.ReadOnly&&fresh.Workspace==a&&store.ProjectFor(fresh)!.Id==aProject,"a new project session never inherits Auto or another folder's scope");
            store.SetProjectShell(aProject,CodeShellKind.CommandPrompt);
            var configured=store.New(a);
            check(configured.Shell==CodeShellKind.CommandPrompt&&configured.ShellExecutable==CodeShells.Find(CodeShellKind.CommandPrompt)&&configured.Permission==CodePermission.ReadOnly&&other.Shell==CodeShellKind.Recommended,"project shell preference resolves its own executable without changing another project or inheriting Auto");
            store.ActivateProject(aProject,first.Id);store.SetProjectExpanded(bProject.Id,true);store.SetProjectExpanded(aProject,false);
        }
        using(var reopened=new CodeStore(profile))
        {
            check(reopened.SelectedProject!.Id==aProject&&reopened.SelectedSession!.Id==aFirst&&reopened.SelectedSession.Draft=="Retain A draft"&&!reopened.SelectedSession.SidebarVisible&&reopened.SelectedSession.ViewerDismissed,"selected project/session, draft and panel state survive a real store restart");
            check(!reopened.SelectedProject.Expanded&&reopened.Projects.Single(p=>p.Id!=aProject).Expanded,"project expansion state survives restart independently of session selection");
            string path=Path.Combine(profile,"code-projects.json"),before=File.ReadAllText(path);
            using(var reader=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                bool blocked=false;try{reopened.SetProjectExpanded(aProject,true);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){blocked=true;}
                check(blocked&&File.ReadAllText(path)==before&&!reopened.SelectedProject.Expanded,"a locked project snapshot preserves its prior bytes and rolls back unsaved UI state");
            }
            reopened.SetProjectExpanded(aProject,true);
            using(var reader=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                bool blocked=false;try{reopened.Delete(aFirst);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){blocked=true;}
                check(blocked&&reopened.SelectedSession!.Id==aFirst&&reopened.Sessions.Any(s=>s.Id==aFirst)&&File.Exists(Path.Combine(profile,"code-sessions",aFirst.ToString("N")+".json")),"a blocked project catalog prevents deletion before removing its selected session journal");
            }
            foreach(var session in reopened.SessionsFor(aProject))reopened.Delete(session.Id);
            check(reopened.SelectedProject!.Id==aProject&&reopened.SelectedSession==null&&reopened.Projects.Count==2&&reopened.Sessions.Single().Id==bSession&&File.ReadAllText(Path.Combine(a,"route.txt"))=="project A edited","deleting a project's last session keeps the project/files and does not select a different folder");
        }
        using(var empty=new CodeStore(profile))check(empty.SelectedProject!.Id==aProject&&empty.SelectedSession==null&&empty.Sessions.Count==1,"a saved project with no sessions remains selected after restart");

        string removal = Path.Combine(area, "removal");
        Guid removedId, lastId, keptId;
        Guid noteId;
        Dictionary<string, byte[]> keptBytes;
        using (var store = new CodeStore(removal))
        {
            var first = store.New(a);
            var last = store.New(a);
            last.Draft = "Recover this draft"; store.Save(last);
            removedId = store.ProjectFor(last)!.Id; lastId = last.Id;
            noteId = CodeNotes.Add(removal, new CodeWorkspace(a), "Remember the route", "route.txt").Id;
            var kept = store.New(b); keptId = store.ProjectFor(kept)!.Id;
            keptBytes = Directory.GetFiles(Path.Combine(removal, "code-sessions"), "*.json")
                .Concat(new[] { Path.Combine(a, "route.txt"), Path.Combine(b, "route.txt") })
                .ToDictionary(p => p, File.ReadAllBytes);
            first.State = "Awaiting review";
            bool busy = false;
            try { store.RemoveProject(removedId); } catch (InvalidOperationException) { busy = true; }
            first.State = "Ready";
            check(busy && store.Projects.Count == 2, "removal respects a pending run in another session of the target project");
            using (var reader = File.Open(Path.Combine(removal, "code-projects.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool blocked = false;
                try { store.RemoveProject(keptId); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { blocked = true; }
                check(blocked && store.SelectedSession == kept && store.Projects.Select(p => p.Id).SequenceEqual(new[] { removedId, keptId }),
                    "a catalog write failure rolls back project removal, ordering and selection");
            }
            store.RemoveProject(removedId);
            check(store.SelectedSession == kept && store.ProjectFor(last) == null && store.Projects.Single().Id == keptId,
                "removing an unselected project affects only that exact folder even when names match");
        }
        using (var store = new CodeStore(removal))
        {
            check(store.Projects.Single().Id == keptId && store.ProjectForWorkspace(a) == null,
                "retained session journals cannot repopulate a removed project on restart");
            var restored = store.AddProject(Path.Combine(a, "."));
            check(restored.Id == removedId && CodeNotes.Load(removal, a).Single().Id == noteId && store.LastSession(removedId)?.Id == lastId &&
                store.LastSession(removedId)?.Draft == "Recover this draft" && store.SessionsFor(removedId).Length == 2,
                "re-adding the folder restores notes, both conversations and the last selected draft");
            store.RemoveProject(keptId);
            check(store.SelectedProject == restored && store.SelectedSession?.Id == lastId,
                "removing the selected project activates the remaining project and its saved session");
            store.RemoveProject(removedId);
        }
        using (var store = new CodeStore(removal))
            check(store.Projects.Count == 0 && store.SelectedProject == null && store.SelectedSession == null &&
                keptBytes.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),
                "the final removal leaves an empty sidebar across restart with byte-identical files and journals");

        string legacy=Path.Combine(area,"legacy");string[] journals;
        using(var old=new CodeStore(legacy))
        {
            old.New(a);old.New(a);old.New(b);
            journals=Directory.GetFiles(Path.Combine(legacy,"code-sessions"),"*.json");
        }
        File.Delete(Path.Combine(legacy,"code-projects.json"));var originals=journals.ToDictionary(p=>p,File.ReadAllBytes);
        using(var migrated=new CodeStore(legacy))check(migrated.Projects.Count==2&&migrated.Sessions.Count==3&&originals.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"legacy journals migrate into grouped projects without rewriting history or adding sessions");

        string missing=Path.Combine(area,"temporarily-offline"),offlineProfile=Path.Combine(area,"offline-profile");Directory.CreateDirectory(missing);Guid offlineId;
        using(var setup=new CodeStore(offlineProfile)){offlineId=setup.AddProject(missing).Id;setup.ActivateProject(offlineId);}
        Directory.Delete(missing);
        using(var offline=new CodeStore(offlineProfile))
        {
            check(offline.SelectedProject!.Id==offlineId&&offline.SelectedSession==null,"an unavailable project folder remains in the saved list instead of losing its history");
            offline.RemoveProject(offlineId);
        }
        using(var removed=new CodeStore(offlineProfile))check(removed.Projects.Count==0&&!Directory.Exists(missing),"removing an offline folder never recreates it");

        int malformed=0;
        void RejectCatalog(JsonObject catalog)
        {
            string bad=Path.Combine(area,"bad-"+(malformed++));Directory.CreateDirectory(bad);string path=Path.Combine(bad,"code-projects.json");string text=catalog.ToJsonString();File.WriteAllText(path,text);
            bool rejected=false;try{using var invalid=new CodeStore(bad);}catch(Exception error)when(error is InvalidDataException or JsonException){rejected=true;}
            check(rejected&&File.ReadAllText(path)==text,"invalid project catalog is rejected and preserved (case "+malformed+")");
        }
        RejectCatalog(new(){["Version"]=99,["Projects"]=new JsonArray()});
        var id=Guid.NewGuid();
        RejectCatalog(JsonNode.Parse(JsonSerializer.Serialize(new{Version=1,Projects=new[]{new{Id=id,Workspace=a},new{Id=id,Workspace=b}}}))!.AsObject());
        RejectCatalog(JsonNode.Parse(JsonSerializer.Serialize(new{Version=1,Projects=new[]{new{Id=Guid.NewGuid(),Workspace=a},new{Id=Guid.NewGuid(),Workspace=Path.Combine(a,".")}}}))!.AsObject());
        RejectCatalog(JsonNode.Parse(JsonSerializer.Serialize(new{Version=1,Projects=new[]{new{Id=Guid.NewGuid(),Workspace="relative"}}}))!.AsObject());
    }
    sealed class EditModel(string hash):ICodeModel, ICodeAcceptancePlanner
    {
        public Task<List<AcceptanceCheck>> PlanAcceptance(string request,string[] units,CodeFiles files,CancellationToken ct) =>
            Task.FromResult<List<AcceptanceCheck>>([new(1,"Route in A","document_text","route.txt","","project A edited")]);
        int action;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> update,CancellationToken cancellation)=>Task.FromResult(action++==0
            ?new CodeAction("Updating the selected session's file.","edit_file",JsonSerializer.SerializeToElement(new{path="route.txt",expected_sha256=hash,old_text="project A",new_text="project A edited"}))
            :new CodeAction("Updated A only.",null,default));
    }
}
