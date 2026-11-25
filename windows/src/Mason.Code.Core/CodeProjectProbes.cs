using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason.Code;

public sealed record ExampleProbe(string Function,JsonElement[] Arguments,JsonElement Expected);
public interface IProjectProbeReviewer
{
    Task<bool> ArgumentsInterchangeable(string request,ExampleProbe example,bool sourceSymmetric,CancellationToken ct);
}

/// <summary>Derive a follow-up to a supplied assertion only when the reviewer identifies an order-independent operation.</summary>
public static class CodeProjectProbes
{
    public static ExampleProbe? DirectExample(CodeWorkspace scope,ProjectTest test)
    {
        if(test.Format!="tap" || test.Inputs.Length!=1 || Path.GetExtension(test.Inputs[0])!=".cjs")return null;
        var module=scope.Read(test.Inputs[0]).Content;
        var names=Regex.Matches(module,@"\bexports\.([a-zA-Z_$][\w$]*)\s*=").Select(m=>m.Groups[1].Value).Distinct().Take(2).ToArray();
        if(names.Length!=1)return null;
        string name=names[0];
        foreach(var path in test.TestFiles){
            string source=scope.Read(path).Content;
            string? identity=null;
            foreach(Match import in Regex.Matches(source,@"\b(?:const|let|var)\s+([\w$]+|\{[^}\r\n]{1,150}\})\s*=\s*require\(\s*['""](\.[^'""\\\r\n]+)['""]\s*\)")){
                var directory=Path.GetDirectoryName(scope.Resolve(path))!;
                var imported=Path.GetFullPath(Path.Combine(directory,import.Groups[2].Value));
                if(!string.Equals(imported,scope.Resolve(test.Inputs[0]),StringComparison.OrdinalIgnoreCase))continue;
                var binding=import.Groups[1].Value;
                if(!binding.StartsWith('{'))identity=binding+"."+name;
                else {
                    var alias=Regex.Match(binding,@"(?:\{|,)\s*"+Regex.Escape(name)+@"\s*(?::\s*([\w$]+))?\s*(?:,|\})");
                    if(alias.Success)identity=alias.Groups[1].Success?alias.Groups[1].Value:name;
                }
            }
            if(identity==null)continue;
            foreach(Match match in Regex.Matches(source,@"\bassert\.(?:equal|strictEqual|deepEqual|deepStrictEqual)\(\s*"+Regex.Escape(identity)+@"\s*\(([^()\r\n]{1,400})\)\s*,\s*([^();\r\n]{1,200})\s*\)")){
                try{
                    var args=JsonSerializer.Deserialize<JsonElement[]>("["+match.Groups[1].Value+"]");
                    var expected=JsonSerializer.Deserialize<JsonElement>(match.Groups[2].Value);
                    if(args is {Length:2} && args[0].ValueKind==args[1].ValueKind && !JsonElement.DeepEquals(args[0],args[1]))
                        return new(name,args,expected);
                }catch(JsonException){/* This assertion uses JavaScript expressions rather than JSON literals. */}
            }
        }
        return null;
    }

    public static bool HasSymmetricImplementation(string source,string export)
    {
        // Prove only simple, side-effect-free projections over the two JSON inputs. An unknown
        // syntax falls back to review. No project code executes during this analysis.
        string parameter=@"[A-Za-z_$][\w$]*";
        var lambda=Regex.Match(source,@"\bexports\."+Regex.Escape(export)+@"\s*=\s*\(\s*("+parameter+@")\s*,\s*("+parameter+@")\s*\)\s*=>\s*([^;{}\r\n]+)\s*;");
        if(!lambda.Success)return false;
        var left=lambda.Groups[1].Value;
        var right=lambda.Groups[2].Value;
        string operand=@"(?:"+Regex.Escape(left)+"|"+Regex.Escape(right)+@")(?:\[\d+\]|\.[A-Za-z_$][\w$]*)*";
        string comparison="("+operand+@")(===|!==|==|!=|<=|>=|<|>)("+operand+")";
        var body=Regex.Replace(lambda.Groups[3].Value,@"\s+","");
        var clauses=Regex.Match(body,@"\A"+comparison+@"(&&|\|\|)"+comparison+@"\z");
        string Swap(string text)=>Regex.Replace(text,@"\A"+parameter,m=>m.Value==left?right:left);
        if(clauses.Success)return Swap(clauses.Groups[1].Value)==clauses.Groups[5].Value &&
            clauses.Groups[2].Value==clauses.Groups[6].Value && Swap(clauses.Groups[3].Value)==clauses.Groups[7].Value;
        var single=Regex.Match(body,@"\A"+comparison+@"\z");
        return single.Success && single.Groups[2].Value is "===" or "!==" or "==" or "!=" &&
            Swap(single.Groups[1].Value)==single.Groups[3].Value;
    }

    public static async Task<string?> Check(CodeWorkspace scope,string module,ExampleProbe probe,CancellationToken ct)
    {
        var marker="probe-"+Guid.NewGuid().ToString("N");
        const string program="""
            const [file,test,marker]=process.argv.slice(1);
            const {Function:name,Arguments:args,Expected:expected}=JSON.parse(test);
            const fn=require(require('node:path').resolve(file))[name];
            const result=fn(...args);
            require('node:assert/strict').deepStrictEqual(result,expected);
            console.log(marker);
            """;
        var result=await CodeCommand.Run(CodeCommand.ResolveExecutable("node",scope.Root),
            ["-e",program,"--",module,JsonSerializer.Serialize(probe),marker],scope.Root,ct,scope.Root);
        if(result.ExitCode==0 && !result.Truncated && result.Output.Split('\n').Any(line=>line.TrimEnd('\r')==marker))return null;
        return "Independent argument-order check failed: "+probe.Function+"("+string.Join(", ",probe.Arguments.Select(a=>a.GetRawText()))+") expected "+probe.Expected.GetRawText()+". "+result.Output[..Math.Min(650,result.Output.Length)];
    }
}
