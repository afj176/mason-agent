using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mason;
using Mason.Code;

// Checks for the Windows engine; they run on Windows alone.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

if(args.Length==3 && args[0]=="--maintenance-oracle"){
    var ws=new CodeWorkspace(args[1]);
    var marker="oracle-finished-"+Guid.NewGuid().ToString("N");
    var run=await CodeCommand.Run(CodeCommand.ResolveExecutable("node",ws.Root),["-e","(async()=>{\n"+args[2]+"\nconsole.log('"+marker+"');\n})().catch(e=>{console.error(e);process.exitCode=1;});"],ws.Root,default,ws.Root);
    Console.WriteLine(run.Output);
    Environment.ExitCode=run.ExitCode==0 && !run.Truncated && run.Output.Split('\n').Any(line=>line.TrimEnd('\r')==marker)?0:1;
    return;
}
if(args.Length==2 && args[0]=="--modern-checks"){await ModernHarnessChecks.Run(Path.GetFullPath(args[1]));return;}
if(args.Length==4 && args[0]=="--worker-check"){await ModernHarnessChecks.LiveWorker(args[1],args[2],Path.GetFullPath(args[3]));return;}

if(args.Length==2 && args[0]=="--checkout-controls"){await CheckoutEvaluation.Run(Path.GetFullPath(args[1]),new ScriptedModel());return;}
if(args.Length==4 && args[0]=="--concurrent-repair"){await ConcurrentRepairEvaluation.Run(args[1],args[2],Path.GetFullPath(args[3]));return;}
if(args.Length==4 && args[0]=="--double-checkout"){await DoubleCheckoutEvaluation.Run(args[1],args[2],Path.GetFullPath(args[3]));return;}
if(args.Length==4 && args[0]=="--visual-check"){await VisualEvaluation.Run(args[1],args[2],Path.GetFullPath(args[3]));return;}
if(args.Length==6 && args[0]=="--model-benchmark"){await ModelBenchmark.Run(args[1],args[2],Path.GetFullPath(args[3]),int.Parse(args[4]),args[5]);return;}
if(args.Length==7 && args[0]=="--model-scenario"){await ModelBenchmark.Run(args[1],args[2],Path.GetFullPath(args[3]),int.Parse(args[4]),args[5],args[6]);return;}

if (args.Length == 2 && args[0] == "--prepare-ui") { await UiFixture.Prepare(args[1]); return; }
if (args.Length == 2 && args[0] == "--harness-recovery-checks")
{
    await HarnessRecoveryChecks.Run(Path.GetFullPath(args[1]), (passed, name) =>
    {
        if (!passed) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    });
    return;
}
if (args.Length is 4 or 5 && args[0] == "--evaluate-empty-project")
{
    await HarnessRecoveryChecks.EvaluateEmptyProject(args[1], args[2], Path.GetFullPath(args[3]), args.Length == 5 ? args[4] : null);
    return;
}
if(args.Length==6 && args[0]=="--evaluate-empty-project" && args[4]=="--request-file")
{
    await HarnessRecoveryChecks.EvaluateEmptyProject(args[1],args[2],Path.GetFullPath(args[3]),requestText:File.ReadAllText(args[5]));return;
}
if(args.Length==2 && args[0]=="--model-catalog-checks"){
    await ModelCatalogChecks.Run(Path.GetFullPath(args[1]),(passed,name)=>{if(!passed)throw new Exception(name);Console.WriteLine("PASS "+name);});return;
}
if(args.Length==2 && args[0]=="--harness-contract-checks"){
    await HarnessContractChecks.Run(Path.GetFullPath(args[1]),(passed,name)=>{if(!passed)throw new Exception(name);Console.WriteLine("PASS "+name);});return;
}
if(args.Length==2 && args[0]=="--skills-tools-checks"){
    await SkillsToolsWebChecks.Run(Path.GetFullPath(args[1]),(passed,name)=>{if(!passed)throw new Exception(name);Console.WriteLine("PASS "+name);});return;
}
if(args.Length==2 && args[0]=="--extension-checks"){
    await HarnessExtensionChecks.Run(Path.GetFullPath(args[1]),(passed,name)=>{if(!passed)throw new Exception(name);Console.WriteLine("PASS "+name);});return;
}
if(args.Length==1 && args[0]=="--native-window-check"){await DesktopIntegration.Run();return;}
if(args.Length==2 && args[0]=="--quick-setup-probe"){using var hub=new HubClient(()=>"");var found=await hub.Search("coder","","GGUF",CancellationToken.None);var picks=new List<QuickSetup.Choice>();foreach(var m in found.Where(m=>m.Role==AgentRole.Implementer&&QuickSetup.Suggestible(m.Id)).OrderByDescending(m=>QuickSetup.Trusted(m.Id)).Take(10)){try{var repo=await hub.Inspect(m.Id,CancellationToken.None);if(!repo.Gated&&QuickSetup.Fit(repo,double.Parse(args[1],System.Globalization.CultureInfo.InvariantCulture)) is {} fit)picks.Add(fit);}catch(Exception e){Console.WriteLine("skip "+m.Id+": "+e.Message);}if(picks.Count>=4)break;}foreach(var p in QuickSetup.Rank(picks))Console.WriteLine(p.Repository.Id+" · "+p.Variant.Name+" · "+p.Reason);return;}
if(args.Length==2 && args[0]=="--language-checks"){
    await LanguageChecks.Run(Path.GetFullPath(args[1]),(passed,name)=>{if(!passed)throw new Exception(name);Console.WriteLine("PASS "+name);});return;
}
if(args.Length==2 && args[0]=="--gap-feature-checks"){GapFeatureChecks.Run(Path.GetFullPath(args[1]));return;}
if(args.Length==3 && args[0]=="--embedded-llama-check"){await EmbeddedLlamaChecks.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]));return;}
if (args.Length == 4 && args[0] == "--evaluate-acceptance")
{
    await HarnessRecoveryChecks.EvaluateEmptyProject(args[1],args[2],Path.GetFullPath(args[3]),acceptanceTrial:true); return;
}
if (args.Length==2 && args[0]=="--acceptance-runner-checks")
{
    await HarnessRecoveryChecks.CheckNamedTests(Path.GetFullPath(args[1]),(passed,name)=>{if(!passed)throw new Exception(name); Console.WriteLine("PASS "+name);}); return;
}
if(args.Length==2&&args[0]=="--snippet-recovery-checks"){await SnippetRecoveryChecks.Run(Path.GetFullPath(args[1]));return;}
if(args.Length==2&&args[0]=="--node-path-checks"){await NodePathChecks.Run(Path.GetFullPath(args[1]));return;}
if(args.Length==4&&args[0]=="--evaluate-html"){await HtmlModelEvaluation.Run(args[1],args[2],args[3]);return;}
if(args.Length==6&&args[0]=="--evaluate-html"){await HtmlModelEvaluation.Run(args[1],args[2],args[3],args[4],args[5]);return;}
if(args.Length==3&&args[0]=="--resume-html"){await HtmlModelEvaluation.Resume(args[1],args[2]);return;}
if(args.Length==2&&args[0]=="--powershell-checks"){await PowerShellChecks.Run(Path.GetFullPath(args[1]));return;}
if(args.Length==2&&args[0]=="--network-lifecycle-checks"){await NetworkLifecycleChecks.Run(Path.GetFullPath(args[1]));return;}
if(args.Length==4&&args[0]=="--package-network-probe")
{
    var area=Path.GetFullPath(args[1]);Directory.CreateDirectory(area);
    using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var network=new CodeNetworkAccess((host,_)=>{Console.WriteLine("QA destination: "+host);return Task.FromResult(new[]{"api.nuget.org","pypi.org","files.pythonhosted.org"}.Contains(host));});
    try{var result=await CodeCommand.Run(CodeCommand.ResolveExecutable(args[2],area),JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[3]))!,area,deadline.Token,area,network);Console.WriteLine(JsonSerializer.Serialize(result));Environment.ExitCode=result.ExitCode;}
    finally{foreach(var receipt in network.Receipts)Console.WriteLine(receipt);}return;
}
if(args.Length==2&&args[0]=="--network-isolation-checks"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await NetworkIsolationChecks.Run(Path.GetFullPath(args[1]));return;}
if(args.Length==1&&args[0]=="--environment-checks"){EnvironmentChecks.Run((passed,name)=>{Console.WriteLine((passed?"PASS ":"FAIL ")+name);if(!passed)throw new Exception(name);});return;}
if(args.Length==4&&args[0]=="--toolchain-probe")
{
    var area=Path.GetFullPath(args[1]);Directory.CreateDirectory(area);
    var commandArguments=JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[3]))!;
    var result=await CodeCommand.Run(CodeCommand.ResolveExecutable(args[2],area),commandArguments,area,default,area);
    Console.WriteLine(JsonSerializer.Serialize(result));Environment.ExitCode=result.ExitCode;return;
}
if(args.Length==3&&args[0]=="--network-node-probe")
{
    var area=Path.GetFullPath(args[1]);Directory.CreateDirectory(area);
    var network=new CodeNetworkAccess((host,_)=>{Console.WriteLine("QA destination: "+host);return Task.FromResult(host=="registry.npmjs.org");});
    try{var result=await CodeCommand.Run(CodeCommand.ResolveExecutable("node.exe",area),["-e",File.ReadAllText(args[2])],area,default,area,network);Console.WriteLine(JsonSerializer.Serialize(result));Environment.ExitCode=result.ExitCode;}
    finally{Console.WriteLine(JsonSerializer.Serialize(network.Receipts));}return;
}
if(args.Length==2&&args[0]=="--project-checks"){await CodingProjectChecks.Run(Path.GetFullPath(args[1]),(pass,name)=>{Console.WriteLine((pass?"PASS ":"FAIL ")+name);if(!pass)throw new Exception(name);});return;}
if (args.Length == 4 && args[0] == "--evaluate-csv") { await CsvImportEvaluation.Run(args[1], args[2], args[3]); return; }
if (args.Length == 5 && args[0] == "--resume-csv") { await CsvImportEvaluation.Resume(args[1], args[2], args[3], File.ReadAllText(args[4])); return; }
if (args.Length is 4 or 5 && args[0] == "--evaluate") { await RealModelEvaluation.Run(args[1], args[2], args[3], args.Length == 5 ? args[4] : "off"); return; }
if (args.Length == 4 && args[0] == "--continue-fix") { await RealSmallTaskEvaluation.Continue(args[1], args[2], args[3], fixOnly:true); return; }
if (args.Length == 4 && args[0] == "--continue-small") { await RealSmallTaskEvaluation.Continue(args[1], args[2], args[3]); return; }
if (args.Length == 4 && args[0] == "--evaluate-create-auto") { await RealCreateTaskEvaluation.Run(args[1], args[2], args[3], auto:true); return; }
if (args.Length == 4 && args[0] == "--evaluate-create") { await RealCreateTaskEvaluation.Run(args[1], args[2], args[3]); return; }
if (args.Length == 4 && args[0] == "--evaluate-fix") { await RealSmallTaskEvaluation.Run(args[1], args[2], args[3], fixOnly:true); return; }
if (args.Length == 4 && args[0] == "--evaluate-small") { await RealSmallTaskEvaluation.Run(args[1], args[2], args[3]); return; }
if (args.Length == 4 && args[0] == "--probe-local") { await LocalPerformanceProbe.Run(args[1], args[2], args[3]); return; }
if(args.Length==4&&args[0]=="--evaluate-local-bash"){await RealModelEvaluation.Run(args[1],args[2],args[3],"default",autoSandbox:true,gitBash:true);return;}
if (args.Length == 4 && args[0] == "--evaluate-owned-flint-batch") { await SandboxedFlintEvaluation.Run(args[1],args[2],args[3],batch:true); return; }
if (args.Length == 4 && args[0] == "--evaluate-owned-native-flint") { await SandboxedFlintEvaluation.Run(args[1],args[2],args[3],batch:true,native:true); return; }
if (args.Length == 4 && args[0] == "--evaluate-owned-native-flint-python") { await SandboxedFlintEvaluation.Run(args[1],args[2],args[3],native:true); return; }
if (args.Length == 4 && args[0] == "--evaluate-owned-native-flint-bash") { await SandboxedFlintEvaluation.Run(args[1],args[2],args[3],native:true,gitBash:true); return; }
if (args.Length == 4 && args[0] == "--resume-owned-native-flint-python") { await SandboxedFlintEvaluation.Run(args[1],args[2],args[3],resume:true,native:true); return; }
if (args.Length == 4 && args[0] == "--resume-owned-native-flint-bash") { await SandboxedFlintEvaluation.Run(args[1],args[2],args[3],resume:true,native:true,gitBash:true); return; }
if (args.Length == 4 && args[0] == "--resume-owned-native-flint") { await SandboxedFlintEvaluation.Run(args[1],args[2],args[3],resume:true,batch:true,native:true); return; }
if(args.Length==3&&args[0]=="--replay-native-failure"){await CodeNativeChecks.Replay(args[1],args[2]);return;}
if(args.Length==2&&args[0]=="--runtime-checks"){await CodingRuntimeChecks.Run(Path.GetFullPath(args[1]),(pass,name)=>{Console.WriteLine((pass?"PASS ":"FAIL ")+name);if(!pass)throw new Exception(name);});return;}
if(args.Length==2&&args[0]=="--bash-cancellation-checks"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await BashCancellationChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--runtime-alias-checks"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await RuntimeAliasChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--native-pipe-checks"){await NativePipeChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--quiescence-probe"){await QuiescenceChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--quiescence-denied-probe"){await QuiescenceChecks.Run(args[1],denied:true);return;}
if(args.Length==2&&args[0]=="--quiescence-cancel-probe"){await QuiescenceChecks.Run(args[1],cancel:true);return;}
if(args.Length==2&&args[0]=="--memory-reservation-probe"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await MemoryReservationChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--memory-adoption-probe"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await MemoryReservationChecks.Run(args[1],adopt:true);return;}
if(args.Length==2&&args[0]=="--memory-multiple-probe"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await MemoryReservationChecks.Run(args[1],adopt:true,multipleReservations:true);return;}
if(args.Length==2&&args[0]=="--memory-userheap-probe"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await MemoryReservationChecks.Run(args[1],adopt:true,multipleReservations:true,secondReservationBytes:0x20000000);return;}
if(args.Length==2&&args[0]=="--memory-reservation-cancel"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await MemoryReservationChecks.Run(args[1],true);return;}
if(args.Length==1&&args[0]=="--runtime-image-checks"){RuntimeImageChecks.Run((pass,name)=>{Console.WriteLine((pass?"PASS ":"FAIL ")+name);if(!pass)throw new Exception(name);});return;}
if(args.Length==2&&args[0]=="--handoff-image-checks"){HandoffImageChecks.Run(args[1]);return;}
if(args.Length==1&&args[0]=="--handoff-request-checks"){HandoffRequestChecks.Run();return;}
if(args.Length==1&&args[0]=="--handoff-admission-checks"){HandoffAdmissionChecks.Run();return;}
if(args.Length==1&&args[0]=="--thread-census-checks"){ThreadCensusChecks.Run();return;}
if(args.Length==2&&args[0]=="--native-payload-checks"){NativePayloadChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--context-preservation-checks"){await ContextPreservationChecks.Run(args[1]);return;}
if(args.Length==5&&args[0]=="--handoff-stress"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await HandoffStressChecks.Run(args[1],args[2],int.Parse(args[3]),int.Parse(args[4]));return;}
if(args.Length==5&&args[0]=="--handoff-batch-stress"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await HandoffStressChecks.Run(args[1],args[2],int.Parse(args[3]),int.Parse(args[4]),4);return;}
if(args.Length==5&&args[0]=="--continuation-stress"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await HandoffStressChecks.Run(args[1],args[2],int.Parse(args[3]),int.Parse(args[4]),2,true);return;}
if(args.Length==2&&args[0]=="--compatibility-checks"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await CompatibilityChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--runtime-host-checks"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await RuntimeHostChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--runtime-host-profile-checks"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await RuntimeHostChecks.Run(args[1],true);return;}
if(args.Length==3&&args[0]=="--runtime-host-worker"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await RuntimeHostChecks.Worker(args[1],args[2]);return;}
if(args.Length==2&&args[0]=="--bash-workspace-image-checks"){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();await BashWorkspaceImageChecks.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--bash-section-probe")
{
    var area=Path.GetFullPath(args[1]);Directory.CreateDirectory(area);
    var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new Exception("Git Bash is required.");
    var command="set -e; printf 'bash-core-ok\\n' > inside-bash.txt; IFS= read -r marker < inside-bash.txt; printf '%s\\n' \"$marker\"; python -c \"print('python-child-ok')\"; node -e \"console.log('node-child-ok')\"";
    var result=await CodeCommand.Run(bash,["--noprofile","--norc","-c",command],area,default,area);Console.WriteLine(JsonSerializer.Serialize(result));
    if(result.ExitCode!=0||!result.Output.Contains("bash-core-ok")||!result.Output.Contains("python-child-ok")||!result.Output.Contains("node-child-ok"))Environment.ExitCode=1;return;
}
if(args.Length==2&&args[0]=="--bash-stress-probe")
{
    var area=Path.GetFullPath(args[1]);Directory.CreateDirectory(area);var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new Exception("Git Bash is required.");
    for(int attempt=0;attempt<40;attempt++)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var result=await CodeCommand.Run(bash,["--noprofile","--norc","-c","set -euo pipefail; value=$(printf 'repeat-ok\\n' | cat); [[ $value == repeat-ok ]]; printf 'repeat-ok\\n'"],area,deadline.Token,area);
        File.WriteAllText(Path.Combine(area,"attempt-"+attempt+".json"),JsonSerializer.Serialize(result));
        if(result.ExitCode!=0||result.Output.Split("\n[Owned runtime diagnostics]",StringSplitOptions.None)[0].Trim()!="repeat-ok")throw new Exception("Fresh fork failure at "+attempt+": "+result.Output);
        Console.WriteLine("PASS fresh fork "+attempt);
    }
    return;
}
if(args.Length==3&&args[0]=="--bash-command-probe")
{
    var area=Path.GetFullPath(args[1]);Directory.CreateDirectory(area);var bash=CodeShells.Find(CodeShellKind.GitBash)??throw new Exception("Git Bash is required.");
    using var diagnosticDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var result=await CodeCommand.Run(bash,["--noprofile","--norc","-c",File.ReadAllText(args[2])],area,diagnosticDeadline.Token,area);Console.WriteLine(JsonSerializer.Serialize(result));Environment.ExitCode=result.ExitCode;return;
}
if(args.Length==2&&args[0]=="--node-child-probe")
{
    var area=Path.GetFullPath(args[1]);Directory.CreateDirectory(area);
    var script="console.log('before-child');const cp=require('node:child_process');console.log('module-ready');const child=cp.spawnSync(process.execPath,['-e',\"console.log('child-ok')\"],{encoding:'utf8',timeout:5000});console.log('after-child',JSON.stringify({status:child.status,error:child.error?.code,output:child.stdout}));";
    Console.WriteLine(JsonSerializer.Serialize(await CodeCommand.Run(CodeCommand.ResolveExecutable("node.exe",area),["-e",script],area,default,area)));return;
}
if(args.Length==2&&args[0]=="--native-protocol-checks")
{
    var area=Path.GetFullPath(args[1]);Directory.CreateDirectory(Path.Combine(area,"workspace"));int count=0;
    await CodeNativeChecks.Run(area,(passed,name)=>{if(!passed)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);count++;});Console.WriteLine(count+" native protocol checks passed.");return;
}
if(args.Length==2&&args[0]=="--runtime-resource-probe")
{
    var area=Path.GetFullPath(args[1]);Directory.CreateDirectory(area);
    var script="""
        import ctypes,json
        k=ctypes.WinDLL('kernel32',use_last_error=True); n=ctypes.WinDLL('ntdll'); k.GetStdHandle.restype=ctypes.c_void_p
        n.NtQueryObject.argtypes=[ctypes.c_void_p,ctypes.c_ulong,ctypes.c_void_p,ctypes.c_ulong,ctypes.c_void_p]
        result={}
        for key in (-10,-11,-12):
            h=k.GetStdHandle(key); b=ctypes.create_string_buffer(8192); count=ctypes.c_ulong(); status=n.NtQueryObject(h,1,b,len(b),ctypes.byref(count))
            result[str(key)]={'status':hex(status&0xffffffff),'length':count.value}
            if status>=0: result[str(key)]['name']=ctypes.wstring_at(ctypes.c_void_p.from_buffer(b,8).value,ctypes.c_ushort.from_buffer(b).value//2)
        print(json.dumps(result))
        """;
    Console.WriteLine(JsonSerializer.Serialize(await CodeCommand.Run(CodeCommand.ResolveExecutable("python.exe",area),["-c",script],area,default,area)));return;
}
if (args.Length == 4 && args[0] == "--resume-owned-flint-batch") { await SandboxedFlintEvaluation.Run(args[1],args[2],args[3],resume:true,batch:true); return; }
if (args.Length == 4 && args[0] == "--resume-owned-flint") { await SandboxedFlintEvaluation.Run(args[1],args[2],args[3],resume:true); return; }
if (args.Length == 4 && args[0] == "--evaluate-owned-flint") { await SandboxedFlintEvaluation.Run(args[1],args[2],args[3]); return; }
if (args.Length == 2 && args[0] == "--sandbox-smoke")
{
    var sandboxRoot = Path.GetFullPath(args[1]); Directory.CreateDirectory(sandboxRoot);
    var cmd = CodeCommand.ResolveExecutable(Environment.GetEnvironmentVariable("MASON_SMOKE_EXE") ?? "cmd.exe", sandboxRoot);
    var commandArguments = Path.GetFileName(cmd).Equals("python.exe", StringComparison.OrdinalIgnoreCase) ? new[] { "-c", "import os,hashlib; print(hashlib.sha256(b'hello').hexdigest()); open('inside-python.txt','w').write('python-ok'); print('python-ok')" } : Path.GetFileName(cmd).Equals("node.exe",StringComparison.OrdinalIgnoreCase)?new[]{"-e","require('node:fs').writeFileSync('inside-node.txt','node-ok'); console.log(require('node:crypto').createHash('sha256').update('hello').digest('hex')); console.log('node-ok')"}: Path.GetFileName(cmd).Equals("bash.exe", StringComparison.OrdinalIgnoreCase) ? new[] { "--noprofile", "--norc", "-c", "printf 'bash-ok' > inside-bash.txt; cat inside-bash.txt" } : Path.GetFileName(cmd).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Write-Output 'ps7-ok'" }: new[] { "/d", "/c", "echo sandbox-ok>inside.txt & type inside.txt" };
    var result = await CodeCommand.Run(cmd, commandArguments, sandboxRoot, default, sandboxRoot);
    Console.WriteLine(JsonSerializer.Serialize(result)); return;
}
var testRoot = Path.Combine(AppContext.BaseDirectory, "check-runs");
var root = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root); string workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
Directory.CreateDirectory(Path.Combine(workspace, "src"));
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or JsonException) { Check(true, name); return; } throw new Exception("FAIL did not reject " + name); }
CodeAction Tool(string name, object args, string message = "") => new(message, name, JsonSerializer.SerializeToElement(args));
CodeAction Done(string message = "Finished.") => new(message, null, default);
try
{
    EnvironmentChecks.Run(Check);
    File.WriteAllText(Path.Combine(workspace, "README.md"), "# Catalog API\n\nOffset pagination.\n");
    File.WriteAllText(Path.Combine(workspace, "src", "route.ts"), "const limit = 12;\r\nreturn limit;\r\n", new UTF8Encoding(true));
    File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), "Preserve compatibility.");
    File.WriteAllText(Path.Combine(workspace, "src", "AGENTS.md"), "Use TypeScript.");
    File.WriteAllText(Path.Combine(workspace, ".env"), "secret");
    var scope = new CodeWorkspace(workspace); var initial = scope.Read("src/route.ts");
    Check(initial.Bom && initial.Content.Contains("\r\n") && initial.Sha256.Length == 64, "UTF8 BOM, line endings and byte hash");
    Reject(() => scope.Read("../outside.txt"), "path traversal");
    Reject(() => scope.Resolve("..\\outside.txt"), "Windows path traversal");
    Reject(() => scope.Resolve(Path.Combine(workspace, "README.md")), "absolute path");
    Reject(() => scope.Resolve("README.md:stream"), "alternate data stream");
    Reject(() => scope.Resolve("README.md."), "ambiguous trailing dot");
    Reject(() => scope.Resolve("NUL.txt"), "Windows device path");
    Reject(() => scope.Read(".env"), "secret file read");
    Reject(() => scope.Create(".git/config", "bad"), "repository metadata write");
    Check(!scope.Files().Files.Contains(".env"), "excluded content absent from inventory");
    foreach (var scratch in new[] { ".mason-sandbox", ".dbcowboy-sandbox" }) { Directory.CreateDirectory(Path.Combine(workspace, scratch, "stale")); File.WriteAllText(Path.Combine(workspace, scratch, "stale", "leftover.txt"), "staleprobe"); }
    bool scratchRead = false; try { scope.Read(".dbcowboy-sandbox/stale/leftover.txt"); scratchRead = true; } catch (UnauthorizedAccessException) { }
    Check(!scratchRead && !scope.Files().Files.Any(f => f.Contains("sandbox")) && !JsonSerializer.Serialize(scope.Search("staleprobe")).Contains("leftover"), "current and leftover legacy sandbox scratch folders stay hidden");
    Check(scope.Instructions("src/route.ts").Contains("Preserve compatibility") && scope.Instructions("src/route.ts").Contains("Use TypeScript"), "nested repository instructions");
    Check(JsonSerializer.Serialize(scope.Search("limit")).Contains("route.ts"), "literal repository search");
    Reject(() => scope.Edit("src/route.ts", "wrong", "12", "24", (_, _) => { }), "stale edit hash");
    Reject(() => scope.Edit("src/route.ts", initial.Sha256, "limit", "count", (_, _) => { }), "ambiguous edit context");
    Reject(() => scope.Create("README.md", "replace"), "create never overwrites");
    var edited = scope.Edit("src/route.ts", initial.Sha256, "= 12", "= 24", (_, _) => { });
    Check(edited.Bom && scope.Read("src/route.ts").Content == "const limit = 24;\r\nreturn limit;\r\n", "exact edit preserves unrelated text, BOM and CRLF");
    var restore = new CodeChange { Path = edited.Path, Before = initial.Content, BeforeBom = true, After = edited.Content, AfterHash = edited.Sha256 };
    scope.Restore(restore); Check(scope.Read("src/route.ts").Sha256 == initial.Sha256, "restore original bytes");
    var blanked = scope.Edit("src/route.ts", initial.Sha256, initial.Content, "", (_, _) => { });
    scope.Restore(new CodeChange { Path = initial.Path, Before = initial.Content, BeforeBom = true, After = "", AfterHash = blanked.Sha256 });
    Check(scope.Read("src/route.ts").Sha256 == initial.Sha256, "restore after an edit leaves an empty file");
    File.AppendAllText(Path.Combine(workspace, "src", "route.ts"), "// external\r\n");
    Reject(() => scope.Restore(withClone()), "restore protects later changes");
    CodeChange withClone() => new() { Path = edited.Path, Before = initial.Content, After = edited.Content, AfterHash = edited.Sha256 };
    Reject(() => CodeProtocol.Parse("{\"version\":1,\"message\":\"x\",\"tool\":null,\"tool\":null}"), "duplicate protocol field");
    Reject(() => CodeProtocol.Parse("{\"version\":1,\"message\":\"\",\"tool\":null}"), "empty final answer");
    Reject(() => CodeProtocol.PreviewUri("file:///C:/Windows/win.ini"), "file URL preview");
    Reject(() => CodeProtocol.PreviewUri("https://example.com"), "remote preview URL");
    Check(CodeProtocol.PreviewUri("http://localhost:5173/catalog").Host == "127.0.0.1", "localhost preview pinned to loopback");
    var webScope=new CodeWorkspace(Directory.CreateDirectory(Path.Combine(root,"website")).FullName);
    var site=new CodeWebsite(webScope);
    Check(site.StartPage()==null,"Website asks for an address when there is no project entry page");
    var previewSession = new CodeSession();
    var oldServer = new CodeTab { Kind = "web", Target = "http://127.0.0.1:5173/" };
    CodePanels.Open(previewSession, oldServer, true);
    CodePanels.Open(previewSession, new CodeTab { Kind = "changes" }, true);
    Check(site.PreviewTarget(previewSession)?.Key == oldServer.Key, "Preview from a diff retains an explicit server in a server-only project");
    webScope.Create("index.html","<title>Local shop</title>");webScope.Create("item details.htm","Item");
    Check(site.PreviewTarget(previewSession)?.Key == "site:index.html", "Completion preview opens project HTML even with a legacy localhost tab and a selected diff");
    var detailsTab = new CodeTab { Kind = "file", Target = "item details.htm" };
    CodePanels.Open(previewSession, detailsTab, true);
    Check(site.PreviewTarget(previewSession)?.Key == "site:item details.htm", "A selected HTML file is shared by the Website and completion preview actions");
    File.Delete(webScope.Resolve("index.html"));
    CodePanels.Close(previewSession, detailsTab.Key);
    previewSession.Changes.Add(new CodeChange { Path = "item details.htm" });
    Check(site.PreviewTarget(previewSession)?.Key == "site:item details.htm", "Completion preview finds a changed standalone page after its tab has closed");
    previewSession.Changes[0].Restored = true;
    Check(site.PreviewTarget(previewSession)?.Key == oldServer.Key, "Restored pages no longer determine the completion preview");
    CodePanels.Close(previewSession, oldServer.Key);
    Check(site.PreviewTarget(previewSession) == null, "Closing the final server tab removes its destination");
    webScope.Create("index.html","<title>Local shop</title>");
    Check(site.StartPage()=="index.html" && site.StartPage("item details.htm")=="item details.htm","Website prefers the selected HTML page and otherwise opens the project index");
    var itemUri=site.Page("item details.htm");
    Check(Encoding.UTF8.GetString(site.Read(new Uri(itemUri+"?v=2#price")).Bytes)=="Item" && site.Read(site.Origin).Type=="text/html","Website serves encoded HTML paths and the root document without a server");
    var css=webScope.Create("store.css","body{color:green}");
    Check(Encoding.UTF8.GetString(site.Read(new Uri(site.Origin,css.Path)).Bytes)==css.Content,"Website resolves local stylesheet assets");
    Reject(()=>site.Read(new Uri(new CodeWebsite(scope).Origin,"index.html")),"website request from another project origin");
    Reject(()=>site.Read(new Uri(site.Origin,".git/config")),"restricted workspace file in website preview");
    Reject(() => CodeProtocol.Parse(JsonSerializer.Serialize(new { version=1, message=new string('x',1601), tool=new { name="read_file", arguments=new { path="README.md" } } })), "long tool narration is rejected before an operation can execute");
    Check(CodeSchema.Action(true).GetProperty("properties").GetProperty("message").GetProperty("maxLength").GetInt32()==1600,"Build wire schema bounds public updates rather than streaming unbounded analysis");
    var diff = CodeProtocol.Diff("a\nb\nc", "a\nx\nc").ToArray(); Check(diff.Any(d => d.Kind == '-' && d.Text == "b") && diff.Any(d => d.Kind == '+' && d.Text == "x"), "native diff has explicit before/after lines");
    var many = string.Join('\n', Enumerable.Range(0, 35).Select(i => "line " + i));
    var separate = CodeProtocol.Diff(many, many.Replace("line 2\n", "early\n").Replace("line 30\n", "late\n")).ToArray();
    Check(separate.Count(l => l.Kind == '-') == 2 && separate.Count(l => l.Kind == '+') == 2 && separate.Any(l => l.Kind == '@'), "separate diff hunks retain unchanged middle without counting it as replaced");
    Check(CodeProtocol.Diff(null, "one line\n").Count(l => l.Kind == '+') == 1 && CodeProtocol.Diff("one line\n", "one line\n\n").Count(l => l.Kind == '+') == 1, "diff counts final newline and meaningful blank lines correctly");
    var multi = scope.Create("src/multi.txt", "first\nkeep\nlast\n");
    Reject(() => scope.ApplyEdits(multi.Path, multi.Sha256, [new("first", "FIRST"), new("missing", "bad")], (_, _) => { }), "invalid second replacement rejects entire edit");
    Check(scope.Read(multi.Path).Sha256 == multi.Sha256, "multi-edit failure has no partial file write");
    Reject(() => scope.ApplyEdits(multi.Path, multi.Sha256, [new("first", "A"), new("irst", "B")], (_, _) => { }), "overlapping replacements rejected");
    var replaced = scope.ApplyEdits(multi.Path, multi.Sha256, [new("first", "FIRST"), new("last", "LAST")], (_, _) => { });
    Check(replaced.Content == "FIRST\nkeep\nLAST\n", "multi-region edit preserves intervening text");
    Check(scope.CreateDirectory("new/nested") == "new/nested" && scope.Create("new/nested/file.txt", "created").Content == "created", "nested directory then new file");
    Reject(() => scope.CreateDirectory("../bad"), "directory creation cannot escape scope");
    var gitStatus = CodeGit.Parse("## feature/test\0 M file with spaces.py\0R  renamed.py\0original.py\0?? new.py\0", "worktree fixture");
    Check(gitStatus.Branch == "feature/test" && gitStatus.Files[0].Path == "file with spaces.py" && gitStatus.Files[1].PreviousPath == "original.py", "Git NUL status preserves spaces and rename origin");
    using (var store = new CodeStore(Path.Combine(root, "state")))
    {
        var session = store.New(workspace); session.Permission = CodePermission.WorkspaceEdits;
        var before = scope.Read("README.md"); int approvals = 0;
        var model = new ScriptedModel(
            Tool("read_file", new { path = "README.md" }, "I will inspect the current API notes."),
            Tool("edit_file", new { path = "README.md", expected_sha256 = before.Sha256, old_text = "Offset pagination.", new_text = "Cursor pagination is available; offset remains supported." }, "I found the compatibility contract."), Done("The notes are updated."));
        await new CodeEngine(store, model, (_, _) => { approvals++; return Task.FromResult(true); }, () => { }).Run(session, "Document cursor pagination.", default);
        Check(session.State == "Completed" && scope.Read("README.md").Content.Contains("Cursor pagination"), "agent reads and edits real workspace file");
        Check(approvals == 0 && session.Changes.Count == 1 && session.Activities.All(a => a.State == "Completed"), "workspace-edit policy and actual activity receipts");
        Check(session.Tabs.Any(t => t.Kind == "changes") && session.Messages.Count == 6, "tool results and artifacts retained for continuation");
        File.WriteAllText(Path.Combine(workspace, "whole.txt"), "old\r\nkeep\r\n", new UTF8Encoding(true));
        var wholeBefore = scope.Read("whole.txt"); var whole = store.New(workspace); whole.Permission = CodePermission.WorkspaceEdits;
        await new CodeEngine(store, new ScriptedModel(Tool("replace_file", new { path = "whole.txt", expected_sha256 = wholeBefore.Sha256, content = "new\nkeep\n" }), Done()), (_, _) => Task.FromResult(false), () => { }).Run(whole, "Rewrite the file.", default);
        Check(scope.Read("whole.txt").Content == "new\r\nkeep\r\n" && scope.Read("whole.txt").Bom && whole.Changes[0].Before == wholeBefore.Content, "checked whole-file replacement preserves BOM, CRLF and original snapshot");
        var staleWhole = store.New(workspace); staleWhole.Permission = CodePermission.WorkspaceEdits;
        await new CodeEngine(store, new ScriptedModel(Tool("replace_file", new { path = "whole.txt", expected_sha256 = wholeBefore.Sha256, content = "bad" }), Tool("ask_user", new { question = "The file changed; should I inspect it again?" })), (_, _) => Task.FromResult(true), () => { }).Run(staleWhole, "Reject stale full-file replacement.", default);
        Check(staleWhole.Activities[0].State == "Failed" && scope.Read("whole.txt").Content == "new\r\nkeep\r\n", "stale whole-file replacement cannot overwrite current bytes");
        var readOnly = store.New(workspace); int deniedApprovals = 0;
        await new CodeEngine(store, new ScriptedModel(Tool("create_file", new { path = "denied.txt", content = "bad" }), Done()), (_, _) => { deniedApprovals++; return Task.FromResult(true); }, () => { }).Run(readOnly, "Read only.", default);
        Check(!File.Exists(Path.Combine(workspace, "denied.txt")) && deniedApprovals == 0 && readOnly.Activities[0].State == "Denied", "read-only enforced inside engine");
        var review = store.New(workspace); review.Permission = CodePermission.ReviewEdits;
        await new CodeEngine(store, new ScriptedModel(Tool("create_file", new { path = "declined.txt", content = "bad" }), Done()), (_, _) => Task.FromResult(false), () => { }).Run(review, "Review each edit.", default);
        Check(!File.Exists(Path.Combine(workspace, "declined.txt")) && review.Activities[0].State == "Denied", "declined edit has no side effect");
        var race = store.New(workspace); race.Permission = CodePermission.ReviewEdits; var snap = scope.Read("README.md");
        await new CodeEngine(store, new ScriptedModel(Tool("edit_file", new { path = "README.md", expected_sha256 = snap.Sha256, old_text = "Cursor", new_text = "Changed" }), Done()), (_, _) => { File.AppendAllText(Path.Combine(workspace, "README.md"), "external edit"); return Task.FromResult(true); }, () => { }).Run(race, "Guard external changes.", default);
        Check(race.Activities[0].State == "Failed" && scope.Read("README.md").Content.EndsWith("external edit"), "file revalidated after approval");
        var pin = store.New(workspace); pin.ViewerPinned = true; pin.Tabs.Add(new() { Kind = "file", Target = "README.md" }); pin.SelectedTab = pin.Tabs[0].Key;
        await new CodeEngine(store, new ScriptedModel(Tool("open_preview", new { url = "http://127.0.0.1:5173" }), Done()), (_, _) => Task.FromResult(false), () => { }).Run(pin, "Open preview.", default);
        Check(pin.Tabs.Count == 2 && pin.SelectedTab == "file:README.md", "agent cannot replace pinned viewer selection");
        var repair = store.New(workspace); repair.Permission = CodePermission.WorkspaceEdits; repair.Mode = "ask";
        await new CodeEngine(store, new RepairModel(), (_, _) => Task.FromResult(false), () => { }).Run(repair, "Recover valid action syntax.", default);
        Check(repair.State == "Completed" && repair.Ending?.Code == CodeRunEnding.Done && repair.Activities.Count(a => a.Tool == "model_response") == 1 && !File.Exists(Path.Combine(workspace, "partial.txt")), "malformed response recorded and corrected without executing partial action");
        var failRepair = store.New(workspace);
        await new CodeEngine(store, new RepairModel(true), (_, _) => Task.FromResult(false), () => { }).Run(failRepair, "Bound correction attempts.", default);
        Check(failRepair.State == "Failed" && failRepair.Ending?.Code == CodeRunEnding.ModelStuck && failRepair.Activities.Count == 3 && failRepair.Messages.Count(m => m.Content.StartsWith(CodeProtocol.ResponseRepairPrefix, StringComparison.Ordinal)) == 2,
            "malformed response recovery stops after two corrections and retains all three failed responses");
        var premature = store.New(workspace);
        await new CodeEngine(store, new ScriptedModel(Done("I will inspect it."), Done("I will start."), Done("Working on it.")), (_, _) => Task.FromResult(false), () => { }).Run(premature, "Implement the change.", default);
        Check(premature.State == "Failed" && premature.Activities.Count == 2 && premature.Detail.Contains("No work is claimed complete"), "Build rejects repeated completion without any tool evidence");
        var plannedOnly = store.New(workspace);
        await new CodeEngine(store, new ScriptedModel(Tool("read_file", new { path = "README.md" }), Done("Here is my plan."), Done("I will implement it."), Done("Planning complete.")), (_, _) => Task.FromResult(false), () => { }).Run(plannedOnly, "Implement the change.", default);
        Check(plannedOnly.State == "Failed" && plannedOnly.Changes.Count == 0, "reading and planning cannot complete a Build task");
        var openedOnly = store.New(workspace); openedOnly.Permission = CodePermission.WorkspaceEdits;
        await new CodeEngine(store, new ScriptedModel(Tool("open_file", new { path = "README.md" }), Done("Please review the file."), Tool("create_file", new { path = "completion-proof.txt", content = "implemented" }), Done("Implemented.")), (_, _) => Task.FromResult(true), () => { }).Run(openedOnly, "Implement the change and open the README.", default);
        Check(openedOnly.State == "Needs verification" && openedOnly.Activities.Any(a => a.Tool == "completion_check" && a.State == "Failed") && File.ReadAllText(Path.Combine(workspace, "completion-proof.txt")) == "implemented", "opening a file cannot prematurely complete a writable Build task and an unverified write still requires a feature check");
        var ask = store.New(workspace); ask.Mode = "ask"; ask.Permission = CodePermission.WorkspaceEdits;
        await new CodeEngine(store, new ScriptedModel(Tool("create_file", new { path = "ask-must-not-write.txt", content = "bad" }), Done("Ask is read-only.")), (_, _) => Task.FromResult(true), () => { }).Run(ask, "Explain the code.", default);
        Check(!File.Exists(Path.Combine(workspace, "ask-must-not-write.txt")) && ask.Activities[0].State == "Denied", "Ask cannot write despite a stored workspace-edit permission");
        var question = store.New(workspace);
        await new CodeEngine(store, new ScriptedModel(Tool("ask_user", new { question = "Which API version should be preserved?" })), (_, _) => Task.FromResult(false), () => { }).Run(question, "Change the API.", default);
        Check(question.State == "Waiting for input" && question.Turns.Last().Text.Contains("Which API version"), "explicit user question pauses rather than claiming completion");
        var directoryDenied = store.New(workspace);
        await new CodeEngine(store, new ScriptedModel(Tool("create_directory", new { path = "denied-folder" }), Done()), (_, _) => Task.FromResult(true), () => { }).Run(directoryDenied, "Read only directory guard.", default);
        Check(!Directory.Exists(Path.Combine(workspace, "denied-folder")) && directoryDenied.Activities[0].State == "Denied", "directory creation honors read-only mode");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var interrupted = store.New(workspace); interrupted.State = "Running"; interrupted.Activities.Add(new() { Tool = "run_command", State = "Pending", Arguments = "{}" }); store.Save(interrupted);
    }
    using (var restored = new CodeStore(Path.Combine(root, "state")))
    {
        Check(restored.Sessions.Any(s => s.State == "Completed" && s.Changes.Count == 1), "sessions, changes and protocol history survive restart");
        Check(restored.Sessions.Any(s => s.State == "Interrupted" && s.Activities[0].State == "Outcome unknown"), "interrupted intent retained without replay");
        Reject(() => { using var second = new CodeStore(Path.Combine(root, "state")); }, "single writer lock");
    }
    var actionText = "{\"version\":1,\"message\":\"Transport verified.\",\"tool\":null}";
    var profile = new AiEndpointProfile { Model = "test-coder", Endpoint = "http://127.0.0.1:5179" };
    string budget = "", visible = ""; var generationProgress = new List<string>();
    var adapter = new CodeLocalModel(profile, "", "default", value => budget = value, () => new LocalAiClient(new ModelHandler(actionText)), generationProgress.Add);
    var reply = await adapter.Next([new("system", CodeProtocol.Instructions), new("user", "Reply.")], value => visible = value, default);
    Check(reply.Message == "Transport verified." && visible == reply.Message && budget.Contains("context"), "local adapter identity, exact budgeting, SSE and action parsing");
    RuntimeImageChecks.Run(Check);
    await CodeNativeChecks.Run(root,Check);
    Check(generationProgress.Any(p => p.StartsWith("Waiting for local model")) && generationProgress.Any(p => p.StartsWith("Generating") && p.Contains("characters")), "streaming reports waiting versus generation without claiming uncommitted file writes");
    var digestHandler = new DigestHandler();
    var digestAdapter = new CodeLocalModel(profile, "", "default", _ => { }, () => new LocalAiClient(digestHandler));
    var digest = await digestAdapter.Summarize(CodeContext.DigestRequest("Older notes", "Observed test failure"), default);
    Check(digest.Attempts == 2 && digest.RejectedResponseHash.Length == 64 && digest.Text == "Open work: fix the actual test failure." && digestHandler.Calls == 2, "incomplete coding digest is discarded, hashed and retried once with a complete replacement");
    var exhaustedHandler = new DigestHandler(true); bool digestRefused = false;
    try { _ = await new CodeLocalModel(profile, "", "default", _ => { }, () => new LocalAiClient(exhaustedHandler)).Summarize(CodeContext.DigestRequest("", "Source"), default); } catch (InvalidDataException) { digestRefused = true; }
    Check(digestRefused && exhaustedHandler.Calls == 2, "coding digest retry is bounded and never accepts a second incomplete response");
    var firstAdapter = new CodeLocalModel(profile, "", "default", _ => { }, () => new LocalAiClient(new ModelHandler(actionText, true)));
    _ = await firstAdapter.Next([new("system", CodeProtocol.Instructions + "\nAgent mode: build"), new("user", "{\"request\":\"Build\",\"references\":[]}")], _ => { }, default);
    Check(true, "new Build request requires a tool in the wire schema");
    var checkpointAdapter = new CodeLocalModel(profile, "", "default", _ => { }, () => new LocalAiClient(new ModelHandler(actionText, false)));
    _ = await checkpointAdapter.Next([new("system", CodeProtocol.Instructions + "\nAgent mode: build"), new("user", "{\"coding_checkpoint\":\"retained\",\"ledger\":{}}")], _ => { }, default);
    Check(true, "checkpoint-only projection permits a final answer while engine completion evidence remains authoritative");
    var schemaBody = new ChatGenerationOptions { ResponseSchema = CodeSchema.Action(true) }.Body(profile, [new("user", "Build.")], 500, true);
    using (var schemaJson = JsonDocument.Parse(JsonSerializer.Serialize(schemaBody)))
    {
        var toolOptions = schemaJson.RootElement.GetProperty("response_format").GetProperty("schema").GetProperty("properties").GetProperty("tool").GetProperty("anyOf");
        Check(CodeNativeChecks.PublicTools.SetEquals(toolOptions.EnumerateArray().Select(t=>t.GetProperty("properties").GetProperty("name").GetProperty("const").GetString()!)) && toolOptions.EnumerateArray().All(t => t.GetProperty("properties").GetProperty("arguments").GetProperty("additionalProperties").ValueKind == JsonValueKind.False), "first Build schema requires a complete known tool with exact argument fields");
    }
    Reject(() => new ChatGenerationOptions { ResponseSchema = CodeSchema.Action() }.Body(profile, [], 500, false), "response schema is not silently sent to an unverified provider");
    if (OperatingSystem.IsWindows())
    {
        var command = CodeCommand.ResolveExecutable("cmd.exe", workspace);
        var result = await CodeCommand.Run(command, ["/d", "/c", "echo code-command-ok"], workspace, default);
        Check(result.ExitCode == 0 && result.Output.Contains("code-command-ok"), "actual Windows owned command and stdout " + JsonSerializer.Serialize(result));
        using (var failureStore = new CodeStore(Path.Combine(root, "failed-command-state")))
        {
            var failure = failureStore.New(workspace); failure.Permission = CodePermission.WorkspaceEdits;
            await new CodeEngine(failureStore, new ScriptedModel(Tool("run_command", new { executable = command, arguments = new[] { "/d", "/c", "exit 7" }, directory = "." }), Done("The command failed.")), (_, _) => Task.FromResult(true), () => { }).Run(failure, "Report command failure.", default);
            Check(failure.Activities[0].State == "Failed" && failure.Activities[0].Result.Contains("\"ExitCode\":7"), "nonzero command exit is a failed tool receipt");
        }
        using var cancellation = new CancellationTokenSource();
        bool stopped = false;
        var python = CodeCommand.ResolveExecutable("python.exe", workspace);
        var highExit = await CodeCommand.Run(python, ["-c", "import sys;sys.exit(259)"], workspace, default);
        Check(highExit.ExitCode == 259, "exit code 259 is not mistaken for a live process");
        var childFile = Path.Combine(workspace, "child.pid");
        var running = CodeCommand.Run(python, ["-c", "import subprocess,sys,time; p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(30)']); open('child.pid','w').write(str(p.pid)); time.sleep(30)"], workspace, cancellation.Token);
        int childId=0;
        for(int i=0;i<100&&childId==0&&!running.IsCompleted;i++)
        {
            try { if(File.Exists(childFile))int.TryParse(File.ReadAllText(childFile),out childId); }catch(IOException){}
            if(childId==0)await Task.Delay(50);
        }
        cancellation.Cancel();
        Check(childId>0, "owned command launches a real descendant");
        try { await running; } catch (OperationCanceledException) { stopped = true; }
        bool childAlive = true;
        for (int i = 0; i < 20 && childAlive; i++)
        {
            try { using var child = System.Diagnostics.Process.GetProcessById(childId); childAlive = !child.HasExited; } catch (ArgumentException) { childAlive = false; }
            if (childAlive) await Task.Delay(50);
        }
        Check(stopped && !childAlive, "command cancellation terminates the owned descendant tree");
    }
    Reject(()=>scope.Create("control-character.txt","broken\u0012source"),"unexpected control characters are rejected before file creation");
    Check(!File.Exists(Path.Combine(workspace,"control-character.txt")),"rejected control-character content has no file side effect");
    var noChange=scope.Read("README.md");Reject(()=>scope.Edit("README.md",noChange.Sha256,noChange.Content,noChange.Content,(_,_)=>throw new Exception("No-op reached write callback")),"no-op edits fail before the write callback");
    using(var loopStore=new CodeStore(Path.Combine(root,"loop-guard")))
    {
        var loop=loopStore.New(workspace);loop.Mode="ask";
        var read=Tool("read_file",new{path="README.md"});
        await new CodeEngine(loopStore,new ScriptedModel(read,read,read,Done()),(_,_)=>throw new Exception("Loop requested review"),()=>{}).Run(loop,"Inspect the read loop.",default);
        Check(loop.State=="Stopped"&&loop.Ending==new CodeRunEnding(CodeRunEnding.ModelLoop,0)&&loop.Activities.Count==3&&loop.Activities[^1].State=="Skipped"&&loop.Activities[1].State=="Completed"&&
            loop.RepeatStop==new CodeRepeatStop("read_file","README.md",3,"")&&
            loop.Detail=="The model kept repeating read file README.md with the same result, so Mason stopped it. Nothing was changed.",
            "a read loop with nothing changed stops, not fails, and skips the third identical call");
        Check(loop.Messages.Any(m=>m.Content.Contains("\"Skipped\"")&&m.Content.Contains("got the same result both times")),"the skipped repeat is still answered in the model history");
        Check(CodeProtocol.ActivitySummary(loop.Activities).Contains("1 repeat skipped")&&!CodeProtocol.ActivitySummary(loop.Activities).Contains("failed"),"a skipped repeat is counted apart from failures");
        var loopScenes=CodeScenes.Group(loop.Activities);
        Check(loopScenes.Count==1&&loopScenes[0]==new CodeScene(CodeSceneKind.Read,0,3)&&CodeScenes.Label(loopScenes[0],loop.Activities)=="Read 1 file","repeated reads of one file form one read scene");
        {
            CodeActivity Call(string tool,object arguments,string result="",string state="Completed")=>new(){Tool=tool,Arguments=JsonSerializer.Serialize(arguments),Result=result,State=state};
            var testRun=JsonSerializer.Serialize(new{ExitCode=1,Output="PASS auth.test.ts\nFAIL tests/upload.test.ts\n  ✕ returns 429 after the 10th upload (12 ms)\nTests:       1 failed, 41 passed, 42 total\n",Truncated=false});
            var mixed=new[]{Call("read_file",new{path="src/a.ts"}),Call("search_text",new{query="limit"}),Call("read_file",new{path="tests\\a.test.ts"}),
                Call("edit_file",new{path="./src/a.ts"}),Call("create_file",new{path="src/b.ts"}),Call("run_command",new{command="npm test"},testRun,"Failed"),Call("browser_load",new{url="http://127.0.0.1:5173/"}),Call("suggest_note",new{note="x"})};
            var scenes=CodeScenes.Group(mixed);
            Check(scenes.Select(s=>s.Kind).SequenceEqual(new[]{CodeSceneKind.Read,CodeSceneKind.Edit,CodeSceneKind.Command,CodeSceneKind.Page,CodeSceneKind.Other})&&scenes.Sum(s=>s.Count)==mixed.Length,"each call lands in exactly one scene, grouped by kind in order");
            Check(CodeScenes.Label(scenes[0],mixed)=="Read 2 files, searched 1 time"&&CodeScenes.Label(scenes[1],mixed)=="Edited 2 files"&&CodeScenes.Label(scenes[2],mixed)=="Ran npm test","scene labels count distinct files and name the command");
            Check(CodeScenes.PathOf(mixed[3])=="src/a.ts"&&CodeScenes.PathOf(mixed[2])=="tests/a.test.ts","paths are normalised for matching against changes");
            Check(CodeScenes.Folders(mixed.Take(3)).SequenceEqual(new[]{("src/",1),("tests/",1)}),"folders count the reads under each top folder");
            var summary=CodeScenes.Command(mixed[5]);
            Check(summary.ExitCode==1&&summary.Passed==41&&summary.Failed==1&&summary.Failures.SequenceEqual(new[]{"FAIL tests/upload.test.ts","✕ returns 429 after the 10th upload (12 ms)"}),"a test run is read for exit code, counts and failing lines, not the summary line");
            var dotnet=CodeScenes.Command(Call("run_command",new{command="dotnet test"},JsonSerializer.Serialize(new{ExitCode=1,Output="  Failed Mason.Tests.Upload [12 ms]\nFailed!  - Failed:     2, Passed:    41, Skipped:     0, Total:    43\n",Truncated=false})));
            Check(dotnet.Passed==41&&dotnet.Failed==2,"dotnet test counts are read too");
            var unittest=Call("run_command",new{executable=@"C:\Python314\python.exe",arguments=new[]{"-m","unittest","-v"}},JsonSerializer.Serialize(new{ExitCode=1,Output="test_a ... ok\nFAIL: test_b (t.T.test_b)\n\nRan 6 tests in 0.002s\n\nFAILED (failures=1, errors=1)\n",Truncated=false}));
            var py=CodeScenes.Command(unittest);
            Check(py.Passed==4&&py.Failed==2&&py.Failures.Contains("FAIL: test_b (t.T.test_b)"),"Python unittest counts come from Ran and FAILED");
            Check(CodeScenes.Label(new CodeScene(CodeSceneKind.Command,0,1),[unittest])=="Ran python -m unittest -v","a command label names the program, not its folder");
            var long_=Enumerable.Range(0,CodeScenes.MaxSceneLength+5).Select(i=>Call("read_file",new{path=$"f{i}.txt"})).ToArray();
            Check(CodeScenes.Group(long_).Select(s=>s.Count).SequenceEqual(new[]{CodeScenes.MaxSceneLength,5}),"long runs split so no scene grows without bound");
        }

        var made=Path.Combine(root,"loop-made");Directory.CreateDirectory(made);
        var created=loopStore.New(made);created.Permission=CodePermission.ReviewEdits;
        var create=Tool("create_file",new{path="index.html",content="<h1>Dojo</h1>"});
        int approvals=0;
        await new CodeEngine(loopStore,new ScriptedModel(create,create,create,create,Done()),(_,_)=>Task.FromResult(++approvals>0),()=>{}){ModelName="Tiny-2B"}.Run(created,"Build a dojo site.",default);
        // The write and the first refusal differ, so the model gets one more try; the second identical refusal is the loop.
        Check(created.State=="Stopped"&&created.Ending==new CodeRunEnding(CodeRunEnding.ModelLoop,1)&&created.RepeatStop?.Model=="Tiny-2B"&&created.Activities.Select(a=>a.State).SequenceEqual(["Completed","Failed","Failed","Skipped"])&&
            File.ReadAllText(Path.Combine(made,"index.html"))=="<h1>Dojo</h1>"&&created.Detail.EndsWith("Your work so far is kept.")&&created.Detail.StartsWith("Tiny-2B kept repeating create file index.html")&&created.RepeatStop!.Times==4&&created.RepeatStop.Explanation(true).StartsWith("Tiny-2B asked for create file index.html 4 times in a row"),
            "a create loop after the file landed ends as a model loop, never Completed, keeps the file and names the model");
        var saved=Directory.GetFiles(loopStore.Root,created.Id.ToString("N")+".json",SearchOption.AllDirectories).Single();
        Check(System.Text.Json.JsonSerializer.Deserialize<CodeSession>(File.ReadAllText(saved))!.RepeatStop==created.RepeatStop,"the repeat stop survives a reload");

        var reset=loopStore.New(workspace);reset.Mode="ask";
        var list=Tool("list_files",new{path=".",limit=5});
        await new CodeEngine(loopStore,new ScriptedModel(read,read,list,read,read,Done()),(_,_)=>throw new Exception("Reset requested review"),()=>{}).Run(reset,"Inspect twice.",default);
        Check(reset.RepeatStop==null&&reset.Activities.Count==5&&reset.Activities.All(a=>a.State=="Completed"),"a different call in between resets the repeat count");
        Check(created.Messages.Count(m=>m.Content.Contains("will not run this exact call a third time"))==1,"the model is warned once, on the second identical result, before anything is skipped");

        // A file that changes between identical reads is progress (a growing log), never a loop.
        var growing=loopStore.New(workspace);growing.Mode="ask";
        var log=Path.Combine(workspace,"build.log");File.WriteAllText(log,"step 0");
        var readLog=Tool("read_file",new{path="build.log"});int step=0;
        await new CodeEngine(loopStore,new GrowingModel(()=>{File.WriteAllText(log,"step "+ ++step);return readLog;},5),(_,_)=>throw new Exception("Log requested review"),()=>{}).Run(growing,"Watch the log.",default);
        File.Delete(log);
        Check(growing.RepeatStop==null&&growing.Activities.Count==5&&growing.Activities.All(a=>a.State=="Completed"),"identical calls whose results keep changing are never stopped");

        Check(CodeEngine.Looping([("same",false),("same",false)])&&!CodeEngine.Looping([("same",false),("changed",false)])&&
            !CodeEngine.Looping([("running",true),("running",true)])&&!CodeEngine.Looping([("running",false),("running",true)])&&!CodeEngine.Looping([("same",false)]),
            "only two identical results in a row count, and a poll that waited for its job never does");

        // Every ending maps to one look, unknown codes fall back, and each way out of a request records its ending.
        Check(CodeRunEnding.Codes.All(c=>CodeRunEnding.Look(c) is {Headline.Length:>0})&&CodeRunEnding.Look("from_the_future")==null&&CodeRunEnding.Look(null)==null&&
            CodeRunEnding.Codes.Distinct().Count()==11&&CodeRunEnding.Codes.Where(c=>CodeRunEnding.Look(c)!.Tone==CodeEndingTone.Danger).SequenceEqual([CodeRunEnding.ModelUnavailable,CodeRunEnding.Failed]),
            "the ending table covers every code, keeps red for breakage only, and has no look for an unknown code");
        Check(new CodeRunEnding(CodeRunEnding.ModelLoop,2).KeptText=="2 files kept"&&new CodeRunEnding(CodeRunEnding.Done,2).KeptText==null&&new CodeRunEnding(CodeRunEnding.Stopped,0).KeptText==null&&
            CodeRunEnding.Hint(CodeRunEnding.ModelUnavailable).Contains("/models")&&!CodeRunEnding.Hint(CodeRunEnding.ModelUnavailable,terminal:false).Contains('/'),
            "kept files are counted for every ending but done, and app hints never name terminal commands");

        var asked=loopStore.New(workspace);asked.Mode="ask";
        await new CodeEngine(loopStore,new ScriptedModel(Tool("ask_user",new{question="Which colour?"})),(_,_)=>throw new Exception("Question requested review"),()=>{}).Run(asked,"Pick a colour.",default);
        Check(asked.Ending==new CodeRunEnding(CodeRunEnding.Question,0),"a question ends as needs your answer");

        var unreachable=loopStore.New(workspace);unreachable.Mode="ask";
        await new CodeEngine(loopStore,new ThrowingModel(new HttpRequestException("Connection refused (127.0.0.1:18310)")),(_,_)=>throw new Exception("Unreachable requested review"),()=>{}).Run(unreachable,"Hello?",default);
        Check(unreachable.State=="Failed"&&unreachable.Ending==new CodeRunEnding(CodeRunEnding.ModelUnavailable,0)&&unreachable.Detail.Contains("Connection refused"),"a model server that cannot be reached ends as model unavailable, not as Mason failing");

        var broken=loopStore.New(workspace);broken.Mode="ask";
        await new CodeEngine(loopStore,new ThrowingModel(new InvalidOperationException("Exact coding context measurement is unavailable.")),(_,_)=>throw new Exception("Broken requested review"),()=>{}).Run(broken,"Hello?",default);
        Check(broken.Ending==new CodeRunEnding(CodeRunEnding.Failed,0),"any other error ends as failed");

        var cancelled=loopStore.New(workspace);cancelled.Mode="ask";
        using(var stop=new CancellationTokenSource()){
            await new CodeEngine(loopStore,new ThrowingModel(null,stop),(_,_)=>throw new Exception("Cancel requested review"),()=>{}).Run(cancelled,"Wait.",stop.Token);
        }
        Check(cancelled.State=="Stopped"&&cancelled.Ending==new CodeRunEnding(CodeRunEnding.Stopped,0),"stopping a request ends as stopped by you");

        await new CodeEngine(loopStore,new ScriptedModel(Done()),(_,_)=>throw new Exception("Next requested review"),()=>{}).Run(loop,"Answer briefly.",default);
        Check(loop.RepeatStop==null&&loop.Ending?.Code!=CodeRunEnding.ModelLoop,"the next request clears the previous ending and repeat stop");
        var reloaded=System.Text.Json.JsonSerializer.Deserialize<CodeSession>(File.ReadAllText(Directory.GetFiles(loopStore.Root,unreachable.Id.ToString("N")+".json",SearchOption.AllDirectories).Single()))!;
        Check(reloaded.Ending==unreachable.Ending,"the ending survives a reload");
        var waits=CodeSchema.NativeTools().EnumerateArray().Select(t=>t.GetProperty("function")).Where(f=>f.GetProperty("name").GetString() is "poll_job" or "terminal_status")
            .Select(f=>f.GetProperty("parameters")).ToArray();
        Check(waits.Length==2&&waits.All(p=>p.GetProperty("properties").TryGetProperty("wait_ms",out var w)&&w.GetProperty("maximum").GetInt32()==10000&&
            p.GetProperty("required").EnumerateArray().Any(r=>r.GetString()=="wait_ms")),"terminal status can wait like poll_job, so watching a long build is patience, not a loop");
    }
    await HarnessRecoveryChecks.Run(root, Check);
    await HarnessContractChecks.Run(root, Check);
    await SkillsToolsWebChecks.Run(root, Check);
    await ModelCatalogChecks.Run(root, Check);
    if (OperatingSystem.IsWindows()) await HarnessRecoveryChecks.CheckNamedTests(root,Check);
    await CodingCompactionChecks.Run(root, workspace, Check);
    await CodingPanelChecks.Run(root, workspace, Check);
    await CodingShellChecks.Run(root, workspace, Check);
    await CodingSandboxChecks.Run(root, Check);
    await CodingDeputyChecks.Run(root, Check);
    if(OperatingSystem.IsWindows())
    {
        var quotedRoot=Path.Combine(root,"quoted-cmd");Directory.CreateDirectory(quotedRoot);
        var quotedCommand=await CodeCommand.Run(CodeCommand.ResolveExecutable("cmd.exe",quotedRoot),["/d","/s","/c","echo quoted-ok>\"quoted file.txt\" & type \"quoted file.txt\""],quotedRoot,default,quotedRoot);
        Check(quotedCommand.ExitCode==0&&File.Exists(Path.Combine(quotedRoot,"quoted file.txt"))&&quotedCommand.Output.Contains("quoted-ok"),"Auto CMD preserves literal quotes and file paths with spaces: "+JsonSerializer.Serialize(quotedCommand));
        File.WriteAllText(Path.Combine(quotedRoot,"tool with spaces.cmd"),"@echo off\r\nif \"%~1\"==\"arg with spaces\" (echo arg-ok&exit /b 0)\r\nexit /b 2\r\n");
        var quotedTool=await CodeCommand.Run(CodeCommand.ResolveExecutable("cmd.exe",quotedRoot),["/c","call \"tool with spaces.cmd\" \"arg with spaces\""],quotedRoot,default,quotedRoot);
        Check(quotedTool.ExitCode==0&&quotedTool.Output.Trim()=="arg-ok","CMD preserves a quoted script filename and argument while disabling AutoRun");
        Reject(()=>CodeCommand.CommandLine(CodeCommand.ResolveExecutable("cmd.exe",quotedRoot),["/c","echo safe\0ignored"]),"NUL command arguments are rejected rather than silently truncated");
    }
    CodingSessionDeletionChecks.Run(root, workspace, Check);
    await CodingProjectChecks.Run(root,Check);
    Console.WriteLine($"{passed} coding checks passed.");
}
// Retain disposable fixtures as evidence. Git creates read-only object files;
// recursive deletion here masked the original exception during acceptance.
// Production identity/job/ACL cleanup remains asserted by the sandbox checks.
finally { Console.WriteLine("Coding check artifacts: " + root); }

/// <summary>Throws an error from the model call, or cancels the request and waits to be stopped.</summary>
sealed class ThrowingModel(Exception? error, CancellationTokenSource? cancel = null) : ICodeModel
{
    public async Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation)
    {
        if (error != null) throw error;
        cancel!.Cancel();
        await Task.Delay(Timeout.Infinite, cancellation);
        throw new InvalidOperationException("Unreachable.");
    }
}
/// <summary>Runs a step before each of its first <c>times</c> answers, then finishes.</summary>
sealed class GrowingModel(Func<CodeAction> step, int times) : ICodeModel
{
    int calls;
    public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation) =>
        Task.FromResult(calls++ < times ? step() : new CodeAction("Watched.", null, default));
}
sealed class ScriptedModel(params CodeAction[] actions) : ICodeModel, ICodeAcceptancePlanner
{
    public Task<List<AcceptanceCheck>> PlanAcceptance(string request, string[] units, CodeFiles files, CancellationToken ct) =>
        Task.FromResult<List<AcceptanceCheck>>(request == "Document cursor pagination." ? [new(1, "Cursor pagination documented", "document_text", "README.md", "", "Cursor pagination")] : []);
    int index;
    public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); var action = actions[index++]; if (action.Message.Length > 0) onMessage(action.Message); return Task.FromResult(action);
    }
}
sealed class RepairModel(bool always = false) : ICodeModel
{
    int count;
    public Task<CodeAction> Next(IReadOnlyList<CodeMessage> messages, Action<string> onMessage, CancellationToken cancellation)
    {
        if (count++ == 0 || always) throw new CodeResponseException("Malformed action", "{\"tool\":{\"name\":\"create_file\",\"arguments\":{\"path\":\"partial.txt\"");
        if (!messages.Last().Content.Contains("before any tool ran")) throw new Exception("Correction did not reach model.");
        return Task.FromResult(new CodeAction("Recovered.", null, default));
    }
}
sealed class ModelHandler(string action, bool? requiresTool = null) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        if (request.Content != null)
        {
            string input = await request.Content.ReadAsStringAsync(cancellationToken);
            if (path == "/v1/chat/completions" && requiresTool.HasValue)
            {
                using var json = JsonDocument.Parse(input);
                bool nullable = json.RootElement.GetProperty("response_format").GetProperty("schema").GetProperty("properties").GetProperty("tool").GetProperty("anyOf").EnumerateArray().Any(e => e.TryGetProperty("type", out var type) && type.GetString() == "null");
                if (nullable == requiresTool.Value) throw new Exception("Incorrect tool-null policy for current request/checkpoint.");
            }
        }
        var body = path switch
        {
            "/v1/models" => "{\"data\":[{\"id\":\"test-coder\"}]}",
            "/props" => "{\"build_info\":\"fixture\",\"chat_template\":\"plain\",\"default_generation_settings\":{\"n_ctx\":32768}}",
            "/apply-template" => "{\"prompt\":\"fixture prompt\"}",
            "/tokenize" => "{\"tokens\":[1,2,3,4]}",
            "/v1/chat/completions" => "data: " + JsonSerializer.Serialize(new { model = "test-coder", choices = new[] { new { index = 0, delta = new { content = action }, finish_reason = (string?)null } } }) + "\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            _ => throw new Exception("Unexpected path: " + path)
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, path == "/v1/chat/completions" ? "text/event-stream" : "application/json") };
    }
}

sealed class DigestHandler(bool alwaysIncomplete = false) : HttpMessageHandler
{
    public int Calls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string path = request.RequestUri!.AbsolutePath;
        string input = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        string body;
        if (path == "/v1/chat/completions")
        {
            Calls++; if (Calls == 2 && !input.Contains("150 WORDS")) throw new Exception("Digest retry lacks the stricter summary bound.");
            bool incomplete = alwaysIncomplete || Calls == 1;
            string answer = incomplete ? "Discarded incomplete digest" : "Open work: fix the actual test failure.";
            body = "data: " + JsonSerializer.Serialize(new { model = "test-coder", choices = new[] { new { index = 0, delta = new { content = answer }, finish_reason = (string?)null } } }) + "\n\ndata: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { }, finish_reason = incomplete ? "length" : "stop" } } }) + "\n\ndata: [DONE]\n\n";
        }
        else body = path switch { "/v1/models" => "{\"data\":[{\"id\":\"test-coder\"}]}", "/props" => "{\"build_info\":\"fixture\",\"chat_template\":\"plain\",\"default_generation_settings\":{\"n_ctx\":16384}}", "/apply-template" => "{\"prompt\":\"fixture\"}", "/tokenize" => "{\"tokens\":[1,2,3,4]}", _ => throw new Exception("Unexpected digest path " + path) };
        return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, path == "/v1/chat/completions" ? "text/event-stream" : "application/json") };
    }
}
