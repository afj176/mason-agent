using System.Text.Json;
using Mason.Code;
static class CodingDeputyChecks
{
 public static async Task Run(string root,Action<bool,string> check)
 {
    if(!OperatingSystem.IsWindows())return;
    var workspace=Path.Combine(root,"git-untrusted");Directory.CreateDirectory(workspace);var metadata=Path.Combine(workspace,".git");Directory.CreateDirectory(metadata);
    File.WriteAllText(Path.Combine(metadata,"HEAD"),"ref: refs/heads/feature/safe-metadata\n");
    var canary=Path.Combine(root,"git-callback-should-not-exist.txt");
    var config="[core]\n repositoryformatversion = 0\n fsmonitor = !echo escaped > \""+canary+"\"\n[filter \"untrusted\"]\n clean = echo escaped > \""+canary+"\"\n process = echo escaped > \""+canary+"\"\n[include]\n path = outside-config-that-must-not-be-opened\n";
    File.WriteAllText(Path.Combine(metadata,"config"),config);File.WriteAllText(Path.Combine(workspace,".gitattributes"),"*.txt filter=untrusted\n");
    var status=await CodeGit.Inspect(workspace,default);
    check(status.Available&&status.Branch=="feature/safe-metadata"&&status.Detail.Contains("Metadata only")&&status.Files.Length==0,"automatic repository observation reads branch metadata and labels unavailable full status");
    check(!File.Exists(canary)&&File.ReadAllText(Path.Combine(metadata,"config"))==config,"automatic inspection does not run untrusted Git filters, fsmonitor or config includes");
    File.WriteAllText(Path.Combine(metadata,"HEAD"),new string('a',9000));var oversized=await CodeGit.Inspect(workspace,default);
    check(!oversized.Available&&oversized.Detail.Contains("8 KiB"),"Git metadata reads are bounded before content parsing");
    Directory.Delete(metadata,true);File.WriteAllText(metadata,"gitdir: "+Path.Combine(root,"outside-repository")+"\n");
    var linked=await CodeGit.Inspect(workspace,default);check(!linked.Available&&linked.Branch=="Linked worktree","automatic inspection never follows a linked metadata path outside the workspace");
    using var store=new CodeStore(Path.Combine(workspace,"harness-profile"));var session=store.New(workspace);session.Permission=CodePermission.Auto;bool rejected=false;
    try { await new CodeEngine(store,new NeverModel(),(_,_)=>throw new Exception("Unexpected review"),()=>{}).Run(session,"Must not edit the host profile",default); }catch(UnauthorizedAccessException){rejected=true;}
    check(rejected&&session.Activities.Count==0&&session.Turns.Count==0,"Auto rejects a workspace containing its session store before any model or file action");
    var temporaryRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Temp");
    var coordination=store.New(temporaryRoot);coordination.Permission=CodePermission.Auto;bool coordinationRejected=false;
    try{await new CodeEngine(store,new NeverModel(),(_,_)=>throw new Exception("Unexpected review"),()=>{}).Run(coordination,"Must not edit host coordination state",default);}catch(UnauthorizedAccessException){coordinationRejected=true;}
    check(coordinationRejected&&coordination.Messages.Count==0&&coordination.Activities.Count==0,"Auto rejects workspaces containing trusted coordination files before model/file actions");
    bool commandCoordinationRejected=false;
    try{await CodeCommand.Run(CodeCommand.ResolveExecutable("cmd.exe",temporaryRoot),["/d","/c","echo forbidden"],temporaryRoot,default,temporaryRoot);}catch(UnauthorizedAccessException){commandCoordinationRejected=true;}
    check(commandCoordinationRejected,"the direct command launcher independently protects host coordination files before ACL propagation");
    var hostRoot=Path.Combine(root,"host-containing-project");Directory.CreateDirectory(hostRoot);
    check(CodeSandbox.ContainsHost(hostRoot,Path.Combine(hostRoot,"app"))&&!CodeSandbox.ContainsHost(hostRoot,hostRoot+"-other/app"),"protected host-root checks distinguish descendants from prefix collisions");
    var deepRoot=hostRoot;while(deepRoot.Length<300)deepRoot=Path.Combine(deepRoot,"long-path-segment-0123456789");Directory.CreateDirectory(deepRoot);
    check(CodeSandbox.ContainsHost(hostRoot,deepRoot)&&!CodeSandbox.ContainsHost(deepRoot,hostRoot)&&CodeSandbox.ContainsHost(deepRoot,Path.Combine(deepRoot,"missing","app")),"protected host-root checks resolve existing directories past MAX_PATH");
    check(CodeSandbox.RuntimeWorkspaceIdentity(deepRoot)==CodeSandbox.RuntimeWorkspaceIdentity(Path.Combine(deepRoot,".")),"runtime workspace identity opens directories past MAX_PATH");
    if(OperatingSystem.IsWindows())
    {
        bool hostRejected=false;
        try { var cmd=CodeCommand.ResolveExecutable("cmd.exe",AppContext.BaseDirectory);await CodeCommand.Run(cmd,["/d","/c","echo forbidden"],AppContext.BaseDirectory,default,AppContext.BaseDirectory); }catch(UnauthorizedAccessException){hostRejected=true;}
        check(hostRejected,"direct Auto launcher also rejects a workspace containing its own executable directory");
        var aliasTarget=Path.Combine(workspace,"trusted-host");Directory.CreateDirectory(aliasTarget);var alias=Path.Combine(root,"host-alias");
        var shell=CodeCommand.ResolveExecutable("cmd.exe",workspace);var made=await CodeCommand.Run(shell,["/d","/c","mklink /J "+CodeCommand.Quote(alias)+" "+CodeCommand.Quote(aliasTarget)],workspace,default);
        if(made.ExitCode!=0)throw new Exception("Host alias fixture failed: "+made.Output);
        try { check(CodeSandbox.ContainsHost(workspace,alias),"protected-host containment resolves physical directory aliases rather than trusting drive/path spelling"); }
        finally { Directory.Delete(alias); }
    }
 }
 sealed class NeverModel:ICodeModel { public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages,Action<string> onMessage,CancellationToken cancellation)=>throw new Exception("Model reached protected workspace"); }
}
