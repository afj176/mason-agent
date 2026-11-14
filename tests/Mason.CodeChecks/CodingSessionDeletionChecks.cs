using System.Text.Json;
using Mason.Code;

static class CodingSessionDeletionChecks
{
    public static void Run(string root,string workspace,Action<bool,string> check)
    {
        var location=Path.Combine(root,"delete-sessions");var sentinel=Path.Combine(workspace,"deletion-preserves.txt");File.WriteAllText(sentinel,"keep these bytes");Guid keepId,deleteId;
        using(var store=new CodeStore(location))
        {
            var keep=store.New(workspace);keepId=keep.Id;var removed=store.New(workspace);deleteId=removed.Id;
            removed.Messages.Add(new("user","discarded fixture history"));store.Save(removed);
            var path=Path.Combine(location,"code-sessions",removed.Id.ToString("N")+".json");File.WriteAllText(path+".tmp","orphaned pending snapshot");
            check(store.Delete(removed.Id)&&!File.Exists(path)&&!File.Exists(path+".tmp")&&!store.Sessions.Contains(removed),"session deletion removes only the selected stored journal and pending snapshot");
            check(File.ReadAllText(sentinel)=="keep these bytes"&&store.Sessions.Contains(keep),"session deletion preserves workspace files and other sessions");
            bool stale=false;try{store.Save(removed);}catch(InvalidOperationException){stale=true;}
            check(stale&&!File.Exists(path),"stale draft saves cannot resurrect a deleted session");
            keep.State="Running";bool running=false;try{store.Delete(keep.Id);}catch(InvalidOperationException){running=true;}
            keep.State="Awaiting review";bool reviewing=false;try{store.Delete(keep.Id);}catch(InvalidOperationException){reviewing=true;}
            check(running&&reviewing&&File.Exists(Path.Combine(location,"code-sessions",keep.Id.ToString("N")+".json")),"active and awaiting-review sessions cannot be deleted");
            keep.State="Ready";store.Save(keep);check(!store.Delete(Guid.NewGuid()),"deleting an unknown session never targets another journal");
        }
        using var reopened=new CodeStore(location);
        check(reopened.Sessions.Count==1&&reopened.Sessions[0].Id==keepId&&!reopened.Sessions.Any(s=>s.Id==deleteId),"deleted sessions remain absent after restart");
    }
}
