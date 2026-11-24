using System.Text;

namespace Mason.Code;

internal sealed record CodeSearchMatch(string Path,int Line,string Text);
internal sealed record CodeSearchPage(List<CodeSearchMatch> Matches,bool Truncated,string? Cursor,int Skipped);

internal sealed class CodeRepositorySearch(CodeWorkspace scope) : IDisposable
{
    const int MaxBytes=8*1024*1024;
    IEnumerator<(CodeSearchMatch? Match,bool Skipped)>? scan;
    string? cursor;
    public CodeSearchPage Start(string query,string directory,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(query) || query.Length>200)throw new ArgumentException("Search for 1–200 literal characters.");
        Dispose();
        scan=Scan(scope.Resolve(directory,true),query,ct).GetEnumerator();
        cursor=Guid.NewGuid().ToString("N");
        return Next(cursor,ct);
    }
    public CodeSearchPage Next(string token,CancellationToken ct)
    {
        if(scan==null || cursor!=token)throw new ArgumentException("This search cursor expired. Start a new search.");
        var matches=new List<CodeSearchMatch>();
        int files=0,skipped=0;
        while(files<300 && matches.Count<60){
            ct.ThrowIfCancellationRequested();
            if(!scan.MoveNext()){Dispose();return new(matches,false,null,skipped);}
            var row=scan.Current;
            if(row.Match!=null)matches.Add(row.Match);
            else{files++;if(row.Skipped)skipped++;}
        }
        cursor=Guid.NewGuid().ToString("N");
        return new(matches,true,cursor,skipped);
    }
    static string Clip(string line)=>line.Length<=400?line:line[..400]+" [line clipped]";
    static string ReadText(string path)
    {
        using var input=File.OpenRead(path);
        if(input.Length>MaxBytes)throw new InvalidDataException("Text file exceeds the 8 MiB inspection limit.");
        var bytes=new byte[(int)input.Length];input.ReadExactly(bytes);
        if(bytes.Contains((byte)0))throw new InvalidDataException("Binary file.");
        return new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');
    }
    public static object Range(CodeWorkspace scope,string path,int start,int count)
    {
        if(start<1 || count is <1 or >200)throw new ArgumentException("Use a 1-based line number and 1–200 lines.");
        var lines=ReadText(scope.Resolve(path)).ReplaceLineEndings("\n").Split('\n');
        if(start>lines.Length)throw new ArgumentException("The starting line is past the end of the file.");
        var slice=lines.Skip(start-1).Take(count).Select((s,i)=>new{line=start+i,text=Clip(s)}).ToArray();
        return new{path,lines=slice,totalLines=lines.Length,nextLine=start-1+slice.Length<lines.Length?(int?)(start+slice.Length):null};
    }
    IEnumerable<(CodeSearchMatch?,bool)> Scan(string root,string query,CancellationToken ct)
    {
        var directories=new Queue<string>();directories.Enqueue(root);
        while(directories.TryDequeue(out var directory)){
            ct.ThrowIfCancellationRequested();
            CodeWorkspace.CheckLinks(directory);
            foreach(var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase)){
                ct.ThrowIfCancellationRequested();
                if(CodeWorkspace.Forbidden(Path.GetFileName(path)))continue;
                var attrs=File.GetAttributes(path);
                if(attrs.HasFlag(FileAttributes.ReparsePoint))continue;
                if(attrs.HasFlag(FileAttributes.Directory)){directories.Enqueue(path);continue;}
                string relative=scope.Relative(path);string? content=null;
                try{content=ReadText(scope.Resolve(relative));}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException){}
                if(content!=null){
                    using var reader=new StringReader(content);
                    int number=0;
                    while(reader.ReadLine() is {} line){
                        number++;
                        if(line.Contains(query,StringComparison.OrdinalIgnoreCase))yield return(new(relative,number,Clip(line)),false);
                    }
                }
                yield return(null,content==null);
            }
        }
    }
    public void Dispose(){scan?.Dispose();scan=null;cursor=null;}
}
