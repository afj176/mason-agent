using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace Mason.Code;

internal static class CodeNavigation
{
    internal sealed record Hit(string Path,int Line,string Kind,string Description);
    static readonly HashSet<string> Extensions=new(StringComparer.OrdinalIgnoreCase){".cs",".py",".js",".jsx",".ts",".tsx",".go",".rb",".dart"};
    internal static readonly Lazy<MetadataReference[]> Framework=new(()=> ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")??"")
        .Split(Path.PathSeparator).Where(p=>Path.GetFileNameWithoutExtension(p) is "System.Private.CoreLib" or "System.Runtime" or "System.Collections" or "System.Linq" or "System.Console" or "netstandard")
        .Select(p=>(MetadataReference)MetadataReference.CreateFromFile(p)).ToArray());
    internal static object Query(CodeWorkspace scope,string dir,string symbol,string mode,IEnumerable<CodeActivity> history,CancellationToken ct)
    {
        if(mode is not ("definitions" or "references" or "tests" or "diagnostics") || symbol.Length>100 || mode!="diagnostics" && !Regex.IsMatch(symbol,@"\A[\p{L}_][\w]*\z"))
            throw new ArgumentException("Choose definitions, references, tests or diagnostics and a single identifier.");
        var queue=new Queue<string>();queue.Enqueue(scope.Resolve(dir,true));
        var trees=new List<SyntaxTree>();var hits=new List<Hit>();int entries=0,files=0,skipped=0;long total=0;bool partial=false;
        var word=new Regex(@"\b"+Regex.Escape(symbol)+@"\b");
        while(queue.Count>0 && !partial)
        {
            foreach(var path in Directory.EnumerateFileSystemEntries(queue.Dequeue()).Order(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if(++entries>60000 || total>24*1024*1024 || files>=4000){partial=true;break;}
                if(CodeWorkspace.Forbidden(Path.GetFileName(path)) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))continue;
                if(Directory.Exists(path)){queue.Enqueue(path);continue;}
                if(!Extensions.Contains(Path.GetExtension(path)))continue;
                var rel=scope.Relative(path);
                CodeFile f;
                try{f=scope.Read(rel);}catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException){skipped++;continue;}
                files++;total+=f.Content.Length*2L;
                if(rel.EndsWith(".cs",StringComparison.OrdinalIgnoreCase)){trees.Add(CSharpSyntaxTree.ParseText(f.Content,path:rel,cancellationToken:ct));continue;}
                if(mode=="diagnostics")continue;
                int line=0;
                foreach(var content in f.Content.Split('\n'))
                {
                    line++;if(hits.Count>=150 || !word.IsMatch(content))continue;
                    var trimmed=content.Trim();
                    if(trimmed.StartsWith("//") || trimmed.StartsWith('#'))continue;
                    bool decl=Regex.IsMatch(trimmed,@"\b(?:class|interface|enum|def|func|function|type|const|let|var|struct)\s+"+Regex.Escape(symbol)+@"\b");
                    if(mode=="definitions"?!decl:mode=="tests"?!TestName(rel):decl)continue;
                    hits.Add(new(rel,line,"text candidate",Clip(trimmed)));
                }
            }
        }
        var compilation=CSharpCompilation.Create("WorkspaceInspection",trees,Framework.Value,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var symbols=new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach(var tree in trees)
        {
            ct.ThrowIfCancellationRequested();
            var semantic=compilation.GetSemanticModel(tree);
            foreach(var token in tree.GetRoot(ct).DescendantTokens().Where(t=>t.ValueText==symbol && t.IsKind(SyntaxKind.IdentifierToken)))
            {
                var node=token.Parent!;
                if(node is SimpleNameSyntax)continue;
                var declared=semantic.GetDeclaredSymbol(node,ct);
                if(declared?.Name!=symbol)continue;
                symbols.Add(declared.OriginalDefinition);
                if(mode=="definitions" && hits.Count<150)hits.Add(new(tree.FilePath,token.GetLocation().GetLineSpan().StartLinePosition.Line+1,"definition",declared.ToDisplayString()));
            }
        }
        if(mode is "references" or "tests")
            foreach(var tree in trees)
            {
                var model=compilation.GetSemanticModel(tree);
                foreach(var name in tree.GetRoot(ct).DescendantNodes().OfType<SimpleNameSyntax>().Where(n=>n.Identifier.ValueText==symbol))
                {
                    var info=model.GetSymbolInfo(name,ct);
                    if(info.Symbol==null || !symbols.Contains(info.Symbol.OriginalDefinition))continue;
                    var method=name.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    bool test=TestName(tree.FilePath)||method?.AttributeLists.Any(a=>Regex.IsMatch(a.ToString(),@"\b(Fact|Theory|Test|TestMethod|TestCase)\b"))==true;
                    if(mode=="tests"&&!test || hits.Count>=150)continue;
                    hits.Add(new(tree.FilePath,name.GetLocation().GetLineSpan().StartLinePosition.Line+1,test?"test reference":"reference",method?.Identifier.Text??info.Symbol.ToDisplayString()));
                }
            }
        if(mode=="diagnostics")
            foreach(var d in compilation.GetDiagnostics(ct).Where(d=>d.Location.IsInSource && d.Severity>=DiagnosticSeverity.Warning).Take(150))
                hits.Add(new(d.Location.SourceTree!.FilePath,d.Location.GetLineSpan().StartLinePosition.Line+1,d.Id,Clip(d.GetMessage())));
        var receipts=mode=="diagnostics"?history.Where(a=>a.Tool is "run_command" or "run_shell" && a.State=="Failed")
            .TakeLast(3).Select(a=>new {a.Id,output=a.Result[..Math.Min(1400,a.Result.Length)]}).ToArray():null;
        return new { mode,symbol,files,skipped,partial=partial||hits.Count>=150,results=hits,command_diagnostics=receipts,
            basis="C# compiler symbols over workspace source with framework references. Other languages use identifier candidates. Project build receipts are authoritative for project-specific dependencies." };
    }
    static bool TestName(string path)=>Regex.IsMatch(path,@"(?:^|[/._-])tests?(?:[/._-]|$)|(?:Tests?|Checks)\.\w+$",RegexOptions.IgnoreCase);
    static string Clip(string s)=>s[..Math.Min(s.Length,320)];
}
