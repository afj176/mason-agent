using System.Text.Json;
using Mason.Code;

static class SnippetRecoveryChecks
{
    public static async Task Run(string root)
    {
        var workspace=Path.Combine(root,"workspace");Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace,"app.js"),"const value=1;\n// preserve this line\n");
        File.WriteAllText(Path.Combine(workspace,"other.txt"),"before");
        var scope=new CodeWorkspace(workspace);using var store=new CodeStore(Path.Combine(root,"profile"));var session=store.New(workspace);session.Permission=CodePermission.WorkspaceEdits;
        await new CodeEngine(store,new RecoveryModel(scope),(_,_)=>Task.FromResult(true),()=>{}).Run(session,"Update value and preserve the comment",default);
        if(session.State!="Needs verification"||scope.Read("app.js").Content!="const value=2;\n// preserve this line\n// external change\n"||scope.Read("other.txt").Content!="after"||session.Changes.Count!=2||session.Activities[1].State!="Failed")throw new Exception("Snippet recovery changed the wrong file or permitted the unavailable tool");
        Console.WriteLine("PASS a conflicting file needs a fresh read while independent patches and concurrent edits survive");
    }
    sealed class RecoveryModel(CodeWorkspace scope):ICodeModel
    {
        int step;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> onMessage,CancellationToken ct)
        {
            if (step == 2) File.AppendAllText(scope.Resolve("app.js"), "// external change\n");
            var action=step++ switch
            {
                0=>new CodeAction("","edit_file",JsonSerializer.SerializeToElement(new{path="app.js",expected_sha256=scope.Read("app.js").Sha256,old_text="missing snippet",new_text="bad"})),
                1=>new CodeAction("","edit_file",JsonSerializer.SerializeToElement(new{path="app.js",expected_sha256=scope.Read("app.js").Sha256,old_text="const value=1;",new_text="bad"})),
                2=>new CodeAction("","edit_file",JsonSerializer.SerializeToElement(new{path="other.txt",expected_sha256=scope.Read("other.txt").Sha256,old_text="before",new_text="after"})),
                3=>new CodeAction("","read_file",JsonSerializer.SerializeToElement(new{path="app.js"})),
                4=>new CodeAction("","edit_file",JsonSerializer.SerializeToElement(new{path="app.js",expected_sha256=scope.Read("app.js").Sha256,old_text="const value=1;",new_text="const value=2;"})),
                _=>new CodeAction("Updated using the current file hash.",null,default)
            };
            return Task.FromResult(action);
        }
    }
}
