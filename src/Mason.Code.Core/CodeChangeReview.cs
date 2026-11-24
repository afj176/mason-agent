namespace Mason.Code;

public sealed record ReviewSource(string Path,string? Before,string Current,string Hash);
public sealed record ReviewFinding(string Path,int Line,string Evidence,string Problem);
public sealed record ReviewPrediction(string Assertion,string Reason);
public interface IReviewPrediction
{
    Task<ReviewPrediction> Predict(ReviewFinding finding,IReadOnlyDictionary<string,AcceptanceCheck> cases,CancellationToken ct);
}
public interface ICodeChangeReviewer
{
    Task<ReviewFinding[]> ReviewChanges(string request,IReadOnlyList<ReviewSource> files,CancellationToken ct);
}
public static class CodeChangeReview
{
    public static async Task<ReviewFinding[]> Review(ICodeChangeReviewer reviewer,string request,IReadOnlyList<ReviewSource> sources,CancellationToken ct)
    {
        var findings=await reviewer.ReviewChanges(request,sources,ct);
        foreach(var f in findings){
            var file=sources.SingleOrDefault(s=>s.Path==f.Path);
            if(file==null||string.IsNullOrWhiteSpace(f.Evidence)||string.IsNullOrWhiteSpace(f.Problem))throw new InvalidDataException("Reviewer reported an ungrounded finding.");
            var lines=file.Current.ReplaceLineEndings("\n").Split('\n');
            if(f.Line<1||f.Line>lines.Length||!lines[f.Line-1].Contains(f.Evidence,StringComparison.Ordinal))throw new InvalidDataException("Reviewer evidence did not match the current source line.");
        }
        return findings;
    }
    internal static bool Supported(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".go" or ".rb" or ".dart";
}
