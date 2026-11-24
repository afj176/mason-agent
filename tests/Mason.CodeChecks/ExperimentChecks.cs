using Mason.Code;

internal static class ExperimentChecks
{
    public static async Task Run(string area)
    {
        var root=Directory.CreateDirectory(Path.Combine(area,"parent")).FullName;
        var scope=new CodeWorkspace(root);
        var git=CodeCommand.ResolveExecutable("git",root);
        await CodeOwnedHelper.Run(git,["init","-b","main"],"",root,default);
        await CodeOwnedHelper.Run(git,["config","user.name","Fixture author"],"",root,default);
        await CodeOwnedHelper.Run(git,["config","user.email","fixture@localhost"],"",root,default);
        File.WriteAllText(Path.Combine(root,"pricing.txt"),string.Join("\n",Enumerable.Range(1,30).Select(i=>"line "+i))+"\n");
        await CodeOwnedHelper.Run(git,["add","--","pricing.txt"],"",root,default);
        await CodeOwnedHelper.Run(git,["-c","user.name=QA","-c","user.email=qa@localhost","-c","commit.gpgsign=false","-c","core.hooksPath=NUL","commit","-m","baseline"],"",root,default);
        scope.Create("pending.txt","uncommitted");
        using var host=new CodeStore(Path.Combine(area,"profile"));
        var owner=host.New(root);
        var destination=Path.Combine(area,"fork");
        var origin=await CodeExperiment.Fork(owner,destination,"mason/price",default);
        var fork=new CodeWorkspace(destination);
        if(fork.Read("pending.txt").Content!="uncommitted")throw new Exception("Fork lost current dirty checkout.");
        var price=fork.Read("pricing.txt");
        File.WriteAllText(fork.Resolve(price.Path),price.Content.ReplaceLineEndings("\n").Replace("line 2\n","updated 2\n").Replace("line 28\n","updated 28\n"));
        File.WriteAllText(fork.Resolve("command.txt"),"created by a command");
        if(!(await CodeExperiment.Changed(fork,origin,default)).Contains("command.txt"))throw new Exception("Command edits absent from experiment.");
        var hunks=await CodeExperiment.Hunks(fork,origin,price.Path,default);
        if(hunks.Length!=2)throw new Exception("Expected independent hunks.");
        var current=scope.Read(price.Path);
        scope.Edit(price.Path,current.Sha256,"line 16\n","concurrent 16\n",(_,_)=>{});
        await CodeExperiment.Apply(fork,origin,price.Path,hunks[0].Id,default);
        var final=scope.Read(price.Path);
        if(!final.Content.Contains("updated 2\n")||final.Content.Contains("updated 28\n")||!final.Content.Contains("concurrent 16\n")||
            scope.Read("pending.txt").Content!="uncommitted")throw new Exception("Applying hunk lost a concurrent or unselected change.");
        var review=await CodeDelivery.Review(scope,"main",default);
        var chosen=review.Patches.First(p=>p.Path=="pricing.txt");
        var proposal=await CodeDelivery.Draft(scope,owner,"main",review.Revision,"Billing change",[chosen.Hunk.Id],default);
        var changed=await CodeOwnedHelper.Run(git,["diff","--name-only",proposal.BaseCommit,proposal.Commit],"",root,default);
        if(changed!="pricing.txt" || File.Exists(scope.Resolve("command.txt")))throw new Exception("Delivery draft contains an unreviewed change.");
        var feedback=CodeDelivery.ParseFeedback("""{"url":"https://github.com/acme/example/pull/1","headRefOid":"head","statusCheckRollup":[{"name":"unit","conclusion":"FAILURE"}],"reviews":[]}""","""[{"path":"pricing.txt","line":2,"body":"Check units"}]""");
        if(feedback.Observations.Length!=2 || !feedback.Observations.Last().Contains("pricing.txt:2"))throw new Exception("Lost CI or source feedback.");
        var add=(await CodeExperiment.Hunks(fork,origin,"command.txt",default)).Single();
        File.WriteAllText(fork.Resolve("command.txt"),"changed while reviewing");
        try{await CodeExperiment.Apply(fork,origin,"command.txt",add.Id,default);throw new Exception("Stale hunk accepted.");}catch(IOException){}
        Console.WriteLine("PASS experiment forks dirty checkout, includes command-created files, applies only reviewed hunk, preserves competing edits and rejects stale patches");
    }
}
