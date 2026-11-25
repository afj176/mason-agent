using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mason.Code;

public sealed record RoundTripProperty(string Module,string Encode,string Decode,string Alphabet);
public sealed record ProjectTest(string Requirement,string Workflow,string Format,string Report,string Test,string[] TestFiles,string[] Inputs)
{
    public RoundTripProperty? RoundTrip { get; init; }
    public string? ArgumentOrder {get;init;}
}
public sealed record TestSuiteEvidence(Dictionary<string,string> Outcomes,int ExitCode,string Diagnostics="");
public sealed record PinnedProjectTest(ProjectTest Definition,string WorkflowRevision,Dictionary<string,string> TestHashes,TestSuiteEvidence? Baseline)
{
    public ExampleProbe? IndependentProbe {get;init;}
}

public static class CodeProjectTests
{
    const string Manifest=".mason/tests.json";
    public static void Bind(CodeWorkspace workspace,CodeAcceptancePlan plan,List<AcceptanceCheck> checks)
    {
        if(!File.Exists(workspace.Resolve(Manifest)))return;
        var definitions=JsonSerializer.Deserialize<ProjectTest[]>(workspace.Read(Manifest).Content,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??[];
        if(definitions.Length>64)throw new InvalidDataException("Declare at most 64 named project checks.");
        var exact=definitions.Any(d=>d?.Requirement!=null && string.Equals(d.Requirement.Trim().TrimEnd('.'),plan.Request.Trim().TrimEnd('.'),StringComparison.OrdinalIgnoreCase));
        bool granular=plan.Units.Length>1 && plan.Units.Any(unit=>definitions.Any(d=>
            string.Equals(d?.Requirement?.Trim().TrimEnd('.'),unit.Trim().TrimEnd('.'),StringComparison.OrdinalIgnoreCase)));
        if(exact && !granular)plan.Units=[plan.Request.Trim().TrimEnd('.')];
        var workflows=ProjectWorkflows.Load(workspace);
        foreach(var d in definitions)
        {
            if(d is null || string.IsNullOrWhiteSpace(d.Requirement) || string.IsNullOrWhiteSpace(d.Test) || d.Test.Length>400 ||
                d.ArgumentOrder is not (null or "ordered" or "interchangeable") || d.Format is not ("junit" or "trx" or "tap" or "go-json" or "flutter-json") || d.TestFiles is not {Length:>0 and <=16} ||
                d.Inputs is not {Length:>0 and <=32} || !workflows.Workflows.TryGetValue(d.Workflow,out var workflow))
                throw new InvalidDataException("Invalid named project test. Include its workflow, format, source inputs and existing test files.");
            for(int index=0;index<plan.Units.Length;index++)
            {
                if(!string.Equals(plan.Units[index].Trim().TrimEnd('.'),d.Requirement.Trim().TrimEnd('.'),StringComparison.OrdinalIgnoreCase) &&
                    !plan.Units[index].Equals("verify test "+d.Test,StringComparison.OrdinalIgnoreCase))continue;
                if(d.Format is "junit" or "trx")workspace.Resolve(d.Report);
                var hashes=d.TestFiles.Append(Manifest).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p=>p,p=>workspace.Read(p).Sha256);
                foreach(var input in d.Inputs)workspace.Resolve(input);
                if(d.RoundTrip is {} property && (property.Module==null || !d.Inputs.Contains(property.Module,StringComparer.OrdinalIgnoreCase) ||
                    !Path.GetExtension(property.Module).Equals(".cjs",StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(property.Encode??"", @"\A[A-Za-z_$][A-Za-z0-9_$]{0,63}\z") ||
                    !Regex.IsMatch(property.Decode??"", @"\A[A-Za-z_$][A-Za-z0-9_$]{0,63}\z") || property.Alphabet is not {Length:>0 and <=24}))
                    throw new InvalidDataException("A round-trip property needs a pinned CommonJS input, two exported functions and an alphabet of 1 to 24 characters.");
                string id=CodeProtocol.Hash(JsonSerializer.SerializeToUtf8Bytes(d));
                plan.ProjectTests[id]=new(d,JsonSerializer.Serialize(workflow),hashes,null);
                checks.Add(new(index+1,d.Requirement,"project_test",Manifest,id,d.Test));
            }
        }
    }
    static async Task<TestSuiteEvidence> Run(CodeWorkspace scope,PinnedProjectTest pinned,CancellationToken ct)
    {
        var d=pinned.Definition;
        void Validate()
        {
            if(pinned.TestHashes.Any(p=>scope.Read(p.Key).Sha256!=p.Value) || !ProjectWorkflows.Load(scope).Workflows.TryGetValue(d.Workflow,out var flow) ||
                JsonSerializer.Serialize(flow)!=pinned.WorkflowRevision)throw new InvalidDataException("The pinned test or workflow changed.");
        }
        Validate();
        string path=d.Format is "junit" or "trx"?scope.Resolve(d.Report):"";
        var before=path.Length>0 && File.Exists(path)?File.GetLastWriteTimeUtc(path):DateTime.MinValue;
        await using var services=new CodeRunServices(scope,ct,[]);
        var result=await services.Workflow(d.Workflow,ct);
        Validate();
        if(result.Truncated)throw new InvalidDataException("The test run was truncated.");
        string data=result.Output;
        if(path.Length>0)
        {
            if(!File.Exists(path) || File.GetLastWriteTimeUtc(path)==before)throw new InvalidDataException("Workflow did not publish a fresh test report.");
            data=scope.Read(d.Report).Content;
        }
        var observed=Parse(d.Format,data);
        if(!observed.ContainsKey(d.Test))throw new InvalidDataException("Named test absent from the report: "+d.Test);
        if(result.ExitCode!=0 && !observed.ContainsValue("failed"))throw new InvalidDataException("Test command failed outside a named test.");
        string propertyFailure="";
        if(d.RoundTrip is {} property && observed[d.Test]=="passed"){
            // Fixed host program. The module runs with the same workspace sandbox as the named suite.
            const string program="""
                const [file,encode,decode,alphabet,marker]=process.argv.slice(1);
                const api=require(require('node:path').resolve(file));
                const chars=[...new Set([...alphabet])];
                function check(value,depth){
                    const actual=api[decode](api[encode](value));
                    if(actual!==value) throw new Error('Round trip failed for '+JSON.stringify(value)+'; decoded '+JSON.stringify(actual));
                    if(depth<3) for(const ch of chars)check(value+ch,depth+1);
                }
                check('',0);console.log(marker);
                """;
            string marker="round-trip-"+Guid.NewGuid().ToString("N");
            var probe=await CodeCommand.Run(CodeCommand.ResolveExecutable("node",scope.Root),
                ["-e",program,"--",property.Module,property.Encode,property.Decode,property.Alphabet,marker],scope.Root,ct,scope.Root);
            Validate();
            if(probe.ExitCode!=0 || probe.Truncated || !probe.Output.Split('\n').Any(line=>line.TrimEnd('\r')==marker)){
                observed[d.Test]="failed";
                propertyFailure=probe.Output.Length>0?probe.Output[..Math.Min(2200,probe.Output.Length)]:"Round-trip property did not finish.";
            }
        }
        var diagnostics=result.Output.Length<=2800?result.Output:result.Output[..1400]+"\n…\n"+result.Output[^1400..];
        return new(observed,result.ExitCode,propertyFailure.Length>0?propertyFailure:result.ExitCode==0?"":diagnostics);
    }
    public static async Task Baseline(CodeWorkspace workspace,CodeAcceptancePlan plan,CancellationToken ct,IProjectProbeReviewer? reviewer=null)
    {
        foreach(var (id,pinned) in plan.ProjectTests.ToArray())
        {
            if(pinned.Baseline!=null)continue;
            var baseline=await Run(workspace,pinned,ct);
            ExampleProbe? followUp=null;
            if(pinned.Definition.ArgumentOrder!="ordered" && CodeProjectProbes.DirectExample(workspace,pinned.Definition) is {} example){
                bool symmetric=CodeProjectProbes.HasSymmetricImplementation(workspace.Read(pinned.Definition.Inputs[0]).Content,example.Function);
                if(pinned.Definition.ArgumentOrder=="interchangeable" || reviewer!=null &&
                    await reviewer.ArgumentsInterchangeable(plan.Request,example,symmetric,ct))
                    followUp=example with{Arguments=[example.Arguments[1],example.Arguments[0]]};
            }
            plan.ProjectTests[id]=pinned with {Baseline=baseline,IndependentProbe=followUp};
        }
    }
    public static async Task<AcceptanceResult> Verify(CodeWorkspace workspace,AcceptanceCheck check,CodeAcceptancePlan plan,CodePermission permission,CancellationToken ct)
    {
        try {
            if(permission!=CodePermission.Auto || !plan.ProjectTests.TryGetValue(check.Target,out var pinned) || pinned.Baseline==null)
                throw new InvalidDataException("A pre-edit baseline in Auto is required.");
            var original=pinned.Baseline.Outcomes;
            var now=await Run(workspace,pinned,ct);
            var target=check.Expected;
            string independent="";
            if(now.Outcomes[target]=="passed" && pinned.IndependentProbe is {} probe){
                if(await CodeProjectProbes.Check(workspace,pinned.Definition.Inputs.Single(),probe,ct) is {} finding)
                    return new(check,false,finding);
                independent=" Independent argument-order check passed for "+probe.Function+JsonSerializer.Serialize(probe.Arguments)+".";
            }
            var newFailures=now.Outcomes.Where(p=>p.Value=="failed" && original.GetValueOrDefault(p.Key)!="failed").Select(p=>p.Key);
            var missing=original.Keys.Except(now.Outcomes.Keys,StringComparer.Ordinal);
            var regressed=original.Where(p=>p.Value=="passed" && now.Outcomes.GetValueOrDefault(p.Key)!="passed").Select(p=>p.Key);
            var blockers=newFailures.Concat(missing).Concat(regressed).Distinct().ToArray();
            var existing=now.Outcomes.Where(p=>p.Key!=target && p.Value=="failed" && original.GetValueOrDefault(p.Key)=="failed").Select(p=>p.Key);
            return new(check,now.Outcomes[target]=="passed" && blockers.Length==0,$"{target}: {original[target]} → {now.Outcomes[target]}. Pre-existing failures: {string.Join(", ",existing)}. New failures, skips or missing tests: {string.Join(", ",blockers)}."+independent+ (now.Outcomes[target]=="failed"?"\n"+now.Diagnostics:""));
        } catch(Exception e) when(e is not OperationCanceledException) {return new(check,false,e.Message);}
    }
    public static Dictionary<string,string> Parse(string format,string content)
    {
        var state=new Dictionary<string,string>(StringComparer.Ordinal);
        void Add(string? name,string outcome)
        {
            if(string.IsNullOrWhiteSpace(name)||name.Length>450 || state.Count>=2000)throw new InvalidDataException("Missing or excessive test identities.");
            if(!state.TryAdd(name,outcome))throw new InvalidDataException("The test suite reported the same name twice: "+name+". Keep pinned test declarations separate from implementation files.");
        }
        if(format is "junit" or "trx")
        {
            using var xml=XmlReader.Create(new StringReader(content),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=2*1024*1024});
            var document=XDocument.Load(xml);
            foreach(var e in document.Descendants().Where(x=>x.Name.LocalName==(format=="junit"?"testcase":"UnitTestResult")))
            {
                if(format=="trx") Add((string?)e.Attribute("testName"),(string?)e.Attribute("outcome") switch {"Passed"=>"passed","Failed" or "Error"=>"failed",_=>"skipped"});
                else {
                    string key=string.Join(".",new[]{(string?)e.Attribute("classname"),(string?)e.Attribute("name")}.Where(x=>!string.IsNullOrEmpty(x)));
                    Add(key,e.Elements().Any(x=>x.Name.LocalName is "failure" or "error")?"failed":e.Elements().Any(x=>x.Name.LocalName=="skipped")?"skipped":"passed");
                }
            }
        }
        else if(format=="tap")
        {
            foreach(var line in content.Split('\n'))
            {
                var m=Regex.Match(line.TrimEnd(),@"^(not ok|ok)\s+\d+\s+-\s+(.+?)(?:\s+#\s*(SKIP|TODO)\b.*)?$",RegexOptions.IgnoreCase);
                if(m.Success)Add(m.Groups[2].Value,m.Groups[3].Success?"skipped":m.Groups[1].Value=="ok"?"passed":"failed");
            }
        }
        else if(format is "go-json" or "flutter-json")
        {
            var flutter=new Dictionary<int,string>();
            foreach(var line in content.Split('\n').Where(x=>x.TrimStart().StartsWith('{')))
            {
                using var json=JsonDocument.Parse(line);var e=json.RootElement;
                if(format=="go-json" && e.TryGetProperty("Test",out var test) && e.TryGetProperty("Action",out var a) && a.GetString() is "pass" or "fail" or "skip")
                    Add(e.GetProperty("Package").GetString()+"/"+test.GetString(),a.GetString() switch {"pass"=>"passed","fail"=>"failed",_=>"skipped"});
                if(format=="flutter-json" && e.TryGetProperty("type",out var t)){
                    if(t.GetString()=="testStart"){var f=e.GetProperty("test");flutter[f.GetProperty("id").GetInt32()]=f.GetProperty("name").GetString()!;}
                    if(t.GetString()=="testDone" && (!e.TryGetProperty("hidden",out var h)||!h.GetBoolean()))
                        Add(flutter[e.GetProperty("testID").GetInt32()],e.TryGetProperty("skipped",out var s)&&s.GetBoolean()?"skipped":e.GetProperty("result").GetString()=="success"?"passed":"failed");
                }
            }
        } else throw new InvalidDataException("Unsupported test reporter.");
        if(state.Count==0)throw new InvalidDataException("No named test results.");
        return state;
    }
}
