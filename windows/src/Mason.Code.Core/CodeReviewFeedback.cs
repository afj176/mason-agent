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

    public const int Limit = 40;
    public const string Help =
        "/comment PATH:LINE NOTE     Pin a note to a line; it is sent with your next request\n" +
        "/comment                    List the notes waiting to be sent\n" +
        "/comment remove NUMBER      Drop one    ·    /comment clear   drops them all\n" +
        "/comment send               Send the notes now, as a request of their own";

    /// <summary>The CLI's /comment: pin a note to a line of the file as it is now, list, remove or clear them.
    /// The notes wait in the session and the engine sends them with the next request.</summary>
    public static string Command(string rest, CodeWorkspace workspace, List<CodeReviewComment> comments)
    {
        rest = rest.Trim();
        int space = rest.IndexOf(' ');
        string word = space < 0 ? rest : rest[..space], after = space < 0 ? "" : rest[(space + 1)..].Trim(), verb = word.ToLowerInvariant();
        if (verb.Length == 0)
        {
            if (comments.Count == 0) return "No comments are waiting.\n" + Help;
            return string.Concat(comments.Select((c, i) => $"{i + 1}. {c.Location} — {c.Note}\n")) + "They are sent with your next request.";
        }
        if (verb == "help") return Help;
        if (verb == "clear")
        {
            int count = comments.Count; comments.Clear();
            return "Dropped " + count + (count == 1 ? " comment." : " comments.");
        }
        if (verb == "remove")
        {
            if (!int.TryParse(after, out var index) || index < 1 || index > comments.Count) throw new ArgumentException("Give the number of a comment. /comment lists them.");
            var where = comments[index - 1].Location; comments.RemoveAt(index - 1);
            return "Dropped the comment on " + where + ".";
        }
        int colon = word.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(word[(colon + 1)..], out var line) || after.Length == 0)
            throw new ArgumentException("Write it as /comment PATH:LINE NOTE, for example /comment src/app.py:12 Use the helper here.");
        if (after.Length > 2000) throw new ArgumentException("Write feedback of 1–2,000 characters.");
        if (comments.Count >= Limit) throw new ArgumentException("At most " + Limit + " comments can wait at once.");
        CodeFile file;
        try { file = workspace.Read(word[..colon]); }
        catch (FileNotFoundException) { throw new ArgumentException(word[..colon] + " does not exist."); }
        var made = new CodeReviewComment(Guid.NewGuid(), file.Path, file.Sha256, false, line, "", after).OnCurrentLine(file, line);
        comments.Add(made);
        var quote = made.Quote.Trim();
        return "Noted on " + made.Location + ": " + (quote.Length > 100 ? quote[..100] : quote);
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
