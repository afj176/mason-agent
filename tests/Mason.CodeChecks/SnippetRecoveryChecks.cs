using System.Text.Json;
using Mason.Code;

static class SnippetRecoveryChecks
{
    public static async Task Run(string root)
    {
        var workspace=Path.Combine(root,"workspace");Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace,"app.js"),"const value=1;\n// preserve this line\n");
        var scope=new CodeWorkspace(workspace);using var store=new CodeStore(Path.Combine(root,"profile"));var session=store.New(workspace);session.Permission=CodePermission.WorkspaceEdits;
        await new CodeEngine(store,new RecoveryModel(scope),(_,_)=>Task.FromResult(true),()=>{}).Run(session,"Update value and preserve the comment",default);
        if(session.State!="Completed"||scope.Read("app.js").Content!="const value=2;\n// preserve this line\n"||session.Changes.Count!=1||session.Activities[1].State!="Failed")throw new Exception("Snippet recovery changed the wrong file or permitted the unavailable tool");
        Console.WriteLine("PASS snippet conflict withdraws snippet tools, rejects bypass and recovers through current-hash replacement");
    }
    sealed class RecoveryModel(CodeWorkspace scope):ICodeModel
    {
        int step;
        public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> onMessage,CancellationToken ct)
        {
            if(step>0)
            {
                var tools=CodeSchema.Available(messages)!;
                if(tools.Contains("edit_file")||tools.Contains("apply_edits")||!tools.Contains("read_file")||!tools.Contains("replace_file")||!messages[0].Content.Contains("fresh SHA256"))throw new Exception("Missing recovery capabilities or guidance");
            }
            var action=step++ switch
            {
                0=>new CodeAction("","edit_file",JsonSerializer.SerializeToElement(new{path="app.js",expected_sha256=scope.Read("app.js").Sha256,old_text="missing snippet",new_text="bad"})),
                1=>new CodeAction("","edit_file",JsonSerializer.SerializeToElement(new{path="app.js",expected_sha256=scope.Read("app.js").Sha256,old_text="const value=1;",new_text="bad"})),
                2=>new CodeAction("","read_file",JsonSerializer.SerializeToElement(new{path="app.js"})),
                3=>new CodeAction("","replace_file",JsonSerializer.SerializeToElement(new{path="app.js",expected_sha256=scope.Read("app.js").Sha256,content="const value=2;\n// preserve this line\n"})),
                _=>new CodeAction("Updated using the current file hash.",null,default)
            };
            return Task.FromResult(action);
        }
    }
}
