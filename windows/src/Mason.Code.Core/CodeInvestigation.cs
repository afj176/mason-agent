namespace Mason.Code;

public sealed record WorkerFinding(string Answer,string Path,int Line,string Quote);
public interface ICodeInvestigator
{
    Task<WorkerFinding[]> Investigate(string question,CodeFile[] files,CancellationToken ct);
}
public static class CodeInvestigation
{
    public static async Task<WorkerFinding[]> Run(CodeWorkspace scope,ICodeInvestigator worker,string question,string[] paths,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(question) || question.Length>2000 || paths.Length is <1 or >8)throw new ArgumentException("Supply a question and 1–8 workspace files.");
        var files=paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(scope.Read).ToArray();
        if(files.Sum(f=>f.Content.Length)>24000)throw new ArgumentException("Narrow the investigation to 24,000 source characters.");
        var result=await worker.Investigate(question,files,ct);
        if(result.Length>8)throw new InvalidDataException("Worker finding budget exceeded.");
        foreach(var fact in result){
            var file=files.SingleOrDefault(f=>f.Path==fact.Path)??throw new InvalidDataException("Worker cited a file outside its assignment.");
            var lines=file.Content.ReplaceLineEndings("\n").Split('\n');
            if(fact.Line<1 || fact.Line>lines.Length || string.IsNullOrWhiteSpace(fact.Answer) || string.IsNullOrWhiteSpace(fact.Quote) ||
                !lines[fact.Line-1].Contains(fact.Quote,StringComparison.Ordinal))throw new InvalidDataException("Worker citation did not match the source.");
        }
        if(files.Any(f=>scope.Read(f.Path).Sha256!=f.Sha256))throw new IOException("Assigned source changed during the investigation. Request a fresh review.");
        return result;
    }
}
