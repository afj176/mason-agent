using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason.Code;

internal static class CodeJavaScriptRepair
{
    // Only offer this editor quick fix on a page created by this run, at an observed
    // reassignment, with one simple declaration. All other cases go back to the model.
    internal static CodeAction? ForGeneratedFile(CodeFile file,IEnumerable<AcceptanceResult> results)
    {
        var failure=results.FirstOrDefault(r=>!r.Passed && r.ScriptPath==file.Path &&
            r.ScriptLine>0 && r.ScriptError=="TypeError: Assignment to constant variable.");
        if(failure?.ScriptLine is not int line)return null;
        string source=file.Content;
        var lines=source.Split('\n');
        if(line>lines.Length)return null;
        var assignment=Regex.Match(lines[line-1],@"^\s*([A-Za-z_$][\w$]*)\s*=(?!=|>)");
        if(!assignment.Success)return null;
        var identifier=Regex.Escape(assignment.Groups[1].Value);
        var declarations=Regex.Matches(source,@"(?m)^[ \t]*(const|let|var)[ \t]+"+identifier+@"(?![\w$])[ \t]*=");
        if(declarations.Count!=1 || declarations[0].Groups[1].Value!="const")return null;
        int assigned=0;
        for(int i=0;i<line-1;i++)assigned+=lines[i].Length+1;
        var keyword=declarations[0].Groups[1];
        if(keyword.Index>=assigned)return null;
        if(Path.GetExtension(file.Path).ToLowerInvariant() is ".html" or ".htm"){
            var scripts=Regex.Matches(source,@"<script\b[^>]*>([\s\S]*?)</script\s*>",RegexOptions.IgnoreCase);
            if(!scripts.Any(m=>m.Groups[1].Index<=keyword.Index && assigned<m.Groups[1].Index+m.Groups[1].Length))return null;
        }
        var content=source.Remove(keyword.Index,keyword.Length).Insert(keyword.Index,"let");
        return new($"Edge observed a reassignment of {assignment.Groups[1].Value} at {file.Path}:{line}. Applying the mutable-binding fix and rerunning acceptance.",
            "replace_file",JsonSerializer.SerializeToElement(new{path=file.Path,expected_sha256=file.Sha256,content}));
    }
}
