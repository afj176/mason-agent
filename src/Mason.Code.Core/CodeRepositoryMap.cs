using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mason.Code;

public sealed record MapHit(string Path, int Line, string Kind, string Name, string Detail);
public sealed record MapSlice(int Files, int Reindexed, bool Partial, IReadOnlyList<MapHit> Matches);

/// <summary>Each consumer owns an index. Repeated queries reuse unchanged syntax trees and compiler state.</summary>
public sealed class CodeRepositoryMap(CodeWorkspace workspace)
{
    sealed record Entry(string Hash, SyntaxTree? Tree, IReadOnlyList<MapHit> Candidates);
    readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".rb", ".dart", ".go" };
    static readonly Regex Identifiers = new(@"[A-Za-z_$][\w$]*", RegexOptions.CultureInvariant);
    static readonly Regex Declaration = new(@"\b(?:class|struct|interface|enum|type|func|def|function|const|let|var)\s+([A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant);
    sealed record Binding(MapHit[] Hits,bool Partial);
    readonly Dictionary<string,Binding> bindings=new(StringComparer.Ordinal);
    public async Task<MapSlice> Query(string query,int limit=24,CancellationToken ct=default)
    {
        var slice=await Task.Run(()=>Search(query,limit,ct),ct);
        var target=slice.Matches.FirstOrDefault(h=>h.Kind=="declaration candidate" && h.Name.Length>0 &&
            query.Contains(h.Name,StringComparison.OrdinalIgnoreCase) && Path.GetExtension(h.Path).ToLowerInvariant() is ".ts" or ".tsx" or ".js" or ".jsx" or ".py");
        if(target==null)return slice;
        string key;
        lock(entries)key=target.Path+":"+target.Line+"|"+target.Name+"|"+CodeProtocol.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join("|",entries.Select(p=>p.Key+p.Value.Hash))));
        Binding? bound;
        lock(bindings)bindings.TryGetValue(key,out bound);
        if(bound==null){
            try{
                var lines=workspace.Read(target.Path).Content.ReplaceLineEndings("\n").Split('\n');
                if(target.Line<1 || target.Line>lines.Length)return slice with {Partial=true};
                var line=lines[target.Line-1];
                int col=line.IndexOf(target.Name,StringComparison.Ordinal)+1;
                if(col<1)return slice;
                using var doc=System.Text.Json.JsonDocument.Parse(await CodeSemantic.Query(workspace,target.Path,target.Line,col,"references",ct));
                var root=doc.RootElement;
                var engine=root.GetProperty("engine").GetString();
                var hits=root.GetProperty("results").EnumerateArray().Select(hit=>{
                    var p=hit.GetProperty("file").GetString()!;int at=hit.GetProperty("line").GetInt32();workspace.Resolve(p);
                    return new MapHit(p,at,p==target.Path && at==target.Line?"definition":"bound reference",target.Name,engine+" · "+target.Path+":"+target.Line+" · "+target.Name);
                }).ToArray();
                bound=new(hits,root.TryGetProperty("partial",out var partial)&&partial.GetBoolean());
                lock(bindings){if(bindings.Count>=32)bindings.Clear();bindings[key]=bound;}
            }catch(Exception e) when(e is IOException or InvalidDataException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException) {return slice with{Partial=true};}
        }
        return slice with{Partial=slice.Partial||bound.Partial,Matches=bound.Hits.Concat(slice.Matches).Distinct().Take(limit).ToArray()};
    }
    CSharpCompilation compilation = CSharpCompilation.Create("MasonRepositoryMap", references: CodeNavigation.Framework.Value,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    public string PromptContext(string request,int contextTokens,CancellationToken ct)
    {
        var anchors=Regex.Matches(request,@"\b(?:[A-Za-z_$][\w$]*\.[\w$.]+|[a-z]+[A-Z][a-zA-Z0-9]*|[A-Za-z]+_[A-Za-z_]+)\b")
            .Select(m=>m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
        if(anchors.Length==0)return "";
        var limit=Math.Clamp(contextTokens/4096,2,8);
        var hits=Search(string.Join(" ",anchors),limit,ct).Matches;
        return hits.Count==0?"":string.Join("\n",hits.Select(hit=>$"{hit.Path}:{hit.Line} [{hit.Kind}] {hit.Detail}"));
    }

    public MapSlice Search(string query, int limit = 24, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) throw new ArgumentException("Search with at most 2,000 characters.");
        lock (entries)
        {
            var inventory = workspace.Files(".", 1000);
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool partial = inventory.Truncated;
            int changed = 0;
            long bytes = 0;
            foreach (var path in inventory.Files.Where(p => Extensions.Contains(Path.GetExtension(p))))
            {
                ct.ThrowIfCancellationRequested();
                CodeFile file;
                try { file = workspace.Read(path); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
                { partial = true; continue; }
                if ((bytes += file.Content.Length * 2L) > 24 * 1024 * 1024) { partial = true; break; }
                live.Add(path);
                if (entries.TryGetValue(path, out var prior) && prior.Hash == file.Sha256) continue;
                var tree = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? CSharpSyntaxTree.ParseText(file.Content, path: path, cancellationToken: ct) : null;
                var next = new Entry(file.Sha256, tree, tree == null ? CandidateLines(file) : []);
                if (prior?.Tree != null) compilation = compilation.RemoveSyntaxTrees(prior.Tree);
                if (tree != null) compilation = compilation.AddSyntaxTrees(tree);
                entries[path] = next;
                changed++;
            }
            foreach (var path in entries.Keys.Where(p => !live.Contains(p)).ToArray())
            {
                if (entries[path].Tree is {} tree) compilation = compilation.RemoveSyntaxTrees(tree);
                entries.Remove(path);
                changed++;
            }
            var terms = Identifiers.Matches(query).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
            if (terms.Length == 0) throw new ArgumentException("Enter a symbol or source path.");
            var hits = new SortedSet<(int Score, MapHit Hit)>(Comparer<(int Score, MapHit Hit)>.Create((a, b) =>
            {
                int rank = b.Score.CompareTo(a.Score);
                if (rank == 0) rank = (b.Hit.Kind == "definition").CompareTo(a.Hit.Kind == "definition");
                if (rank == 0) rank = string.CompareOrdinal(a.Hit.Path, b.Hit.Path);
                if (rank == 0) rank = a.Hit.Line.CompareTo(b.Hit.Line);
                if (rank == 0) rank = string.CompareOrdinal(a.Hit.Detail, b.Hit.Detail);
                return rank == 0 ? string.CompareOrdinal(a.Hit.Kind, b.Hit.Kind) : rank;
            }));
            void Rank(MapHit hit)
            {
                int score = terms.Sum(t => (hit.Name.Equals(t, StringComparison.OrdinalIgnoreCase) ? 20 : 0)
                    + (hit.Detail.Contains(t, StringComparison.OrdinalIgnoreCase) ? 4 : 0)
                    + (hit.Path.Contains(t, StringComparison.OrdinalIgnoreCase) ? 2 : 0));
                if (terms.Length != 0 && score == 0) return;
                if (query.Length > 0 && hit.Detail.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)) score += 80;
                hits.Add((score, hit));
                if (hits.Count > limit) hits.Remove(hits.Max);
            }
            foreach (var (path, entry) in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Tree == null)
                {
                    foreach (var hit in entry.Candidates) { ct.ThrowIfCancellationRequested(); Rank(hit); }
                    continue;
                }
                var root = entry.Tree.GetRoot(ct);
                // Look up exact candidate tokens before asking Roslyn to bind them.
                var identifiers = root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken));
                var model = compilation.GetSemanticModel(entry.Tree);
                foreach (var token in identifiers)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!terms.Contains(token.ValueText, StringComparer.OrdinalIgnoreCase)
                        && !terms.Any(t => path.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                    var node = token.Parent!;
                    bool use = node is SimpleNameSyntax;
                    var symbol = use ? model.GetSymbolInfo(node, ct).Symbol : model.GetDeclaredSymbol(node, ct);
                    if (symbol == null || symbol.Name != token.ValueText || symbol.Kind is SymbolKind.Local or SymbolKind.Parameter
                        || symbol.Kind != SymbolKind.Namespace && !symbol.Locations.Any(l => l.IsInSource)) continue;
                    var caller = use ? model.GetEnclosingSymbol(token.SpanStart, ct)?.ToDisplayString() : null;
                    var target = symbol.OriginalDefinition.ToDisplayString();
                    bool test = path.Contains("test", StringComparison.OrdinalIgnoreCase) || path.Contains("checks", StringComparison.OrdinalIgnoreCase);
                    var kind = !use ? "definition" : node.Ancestors().Any(n => n is UsingDirectiveSyntax) ? "import" : test ? "test reference" : "reference";
                    Rank(new(path, token.GetLocation().GetLineSpan().StartLinePosition.Line + 1, kind, symbol.Name,
                        caller == null ? target : target + " ← " + caller));
                }
            }
            var ranked = hits.Select(h => h.Hit).ToArray();
            return new(entries.Count, changed, partial, ranked);
        }
    }

    static List<MapHit> CandidateLines(CodeFile file)
    {
        var result = new List<MapHit>();
        using var reader = new StringReader(file.Content);
        for (int line = 1; reader.ReadLine() is {} text; line++)
        {
            var value = text.Trim();
            if (value.Length == 0 || value.StartsWith("//") || value.StartsWith('#')) continue;
            var declaration = Declaration.Match(value);
            var detail = value[..Math.Min(200, value.Length)];
            bool import = value.StartsWith("import ") || value.StartsWith("from ") || value.StartsWith("require ");
            string name = declaration.Success ? declaration.Groups[1].Value : "";
            string kind = import ? "import candidate" : declaration.Success ? "declaration candidate" : "reference candidate";
            result.Add(new(file.Path, line, kind, name, detail));
        }
        return result;
    }
}
