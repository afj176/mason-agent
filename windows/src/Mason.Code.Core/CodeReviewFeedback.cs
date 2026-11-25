namespace Mason.Code;

public sealed record CodeReviewComment(Guid Id, string Path, string Revision, bool Original, int Line, string Quote, string Note)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Location => $"{Path} · {(Original ? "original" : "updated")} line {Line}";

    public static CodeReviewComment OnDiff(CodeChange change, CodeDiffLine line, string note)
    {
        bool original = line.After == null;
        int number = line.After ?? line.Before ?? throw new ArgumentException("Select a source line.");
        return new(Guid.NewGuid(), change.Path, change.AfterHash, original, number, line.Text, note.Trim());
    }

    public CodeReviewComment OnCurrentLine(CodeFile file, int number)
    {
        var lines = file.Content.Replace("\r\n", "\n").Split('\n');
        int count = lines.Length - (lines[^1].Length == 0 ? 1 : 0);
        if (number < 1 || number > count) throw new ArgumentException($"Select a line between 1 and {count}.");
        return this with { Revision = file.Sha256, Original = false, Line = number, Quote = lines[number - 1] };
    }

    public static string Request(string draft, IReadOnlyList<CodeReviewComment> comments)
    {
        if (comments.Count == 0) return draft;
        return string.Join("\n", new[] { draft.Trim() }.Where(s => s.Length > 0)
            .Concat(comments.Select(c => $"{c.Location} — {c.Note}")));
    }

    public static void Validate(CodeWorkspace workspace, IReadOnlyList<CodeReviewComment> comments)
    {
        foreach (var group in comments.GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            CodeFile file;
            try { file = workspace.Read(group.Key); }
            catch (FileNotFoundException error)
            {
                throw new InvalidOperationException($"{group.Key} is missing. Open Review comments to remove its feedback or restore the file.", error);
            }
            foreach (var comment in group)
            {
                if (file.Sha256 != comment.Revision)
                    throw new InvalidOperationException($"{comment.Location} changed. Open Review comments to choose its current line.");
                if (string.IsNullOrWhiteSpace(comment.Note)) throw new ArgumentException("Enter feedback for each review comment.");
            }
        }
    }
}
