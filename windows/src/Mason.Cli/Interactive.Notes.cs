using Mason;
using Mason.Code;

// Notes in mason: the same notes the app keeps for the project, read by Mason with every request.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class Interactive
{
    ProjectNote[] LoadNotes() => CodeNotes.Load(host, scope.Root, store.ProjectFor(session)?.Notes ?? []);

    static string Age(DateTimeOffset saved)
    {
        var days = (DateTimeOffset.Now.Date - saved.ToLocalTime().Date).Days;
        return days <= 0 ? "today" : days == 1 ? "yesterday" : days < 7 ? $"{days} days ago" : saved.ToLocalTime().ToString("d MMM");
    }

    /// <summary>/notes [add TEXT | about PATH TEXT | remove N | clear] and /note TEXT.</summary>
    void Notes(string rest)
    {
        var parts = rest.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var notes = LoadNotes();
        try
        {
            switch (parts.FirstOrDefault() ?? "")
            {
                case "":
                    Term.Line();
                    Term.Line($"  {Term.Paint(Term.Accent, "Notes")} {Term.Paint(Term.Dim, "· Mason reads these with every request")}");
                    if (notes.Length == 0) Term.Line(Term.Paint(Term.Dim, "  None yet. Notes are facts Mason should know every time: how to run things, where things live."));
                    int width = Math.Min(48, notes.Select(n => n.Text.Length).DefaultIfEmpty(0).Max());
                    for (int i = 0; i < notes.Length; i++)
                    {
                        var note = notes[i];
                        bool current = CodeNotes.Current(scope, note);
                        var detail = !current ? $"{note.Source} changed · not sent" : note.Source.Length > 0 ? $"{note.Source} · {Age(note.Saved)}" : Age(note.Saved);
                        Term.Line($"  {(i + 1).ToString().PadLeft(2)}  {Term.Clip(note.Text, 60).PadRight(Math.Min(60, width))}  {Term.Paint(Term.Dim, detail)}");
                    }
                    Term.Line(Term.Paint(Term.Dim, "  /note TEXT adds one · /notes remove N · /notes clear"));
                    return;
                case "add":
                    if (parts.Length < 2) throw new ArgumentException("Use /note TEXT.");
                    CodeNotes.Add(host, scope, parts[1]);
                    Term.Line(Term.Paint(Term.Dim, "  Kept. Mason reads it with every request in this project."));
                    return;
                case "about":
                    var words = parts.Length < 2 ? [] : parts[1].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (words.Length < 2) throw new ArgumentException("Use /notes about PATH TEXT.");
                    CodeNotes.Add(host, scope, words[1], words[0]);
                    Term.Line(Term.Paint(Term.Dim, $"  Kept, about {words[0]}. It is sent until that file changes."));
                    return;
                case "remove":
                    if (parts.Length < 2 || !int.TryParse(parts[1], out int number) || number < 1 || number > notes.Length)
                        throw new ArgumentException("Use /notes remove NUMBER, with a number from /notes.");
                    CodeNotes.Remove(host, scope.Root, notes[number - 1].Id);
                    Term.Line(Term.Paint(Term.Dim, "  Removed."));
                    return;
                case "clear":
                    if (notes.Length == 0) { Term.Line(Term.Paint(Term.Dim, "  There are no notes to clear.")); return; }
                    Term.Write($"  Clear all {notes.Length} notes? Mason stops reading them. [y/N] ");
                    var answer = Choose("yn\r\x1b", default);
                    Term.Line(answer == 'y' ? "y" : "n");
                    if (answer != 'y') return;
                    CodeNotes.Clear(host, scope.Root);
                    Term.Line(Term.Paint(Term.Dim, "  This project has no notes now."));
                    return;
                case "help":
                    Term.Line(Term.Paint(Term.Dim, "  /notes lists them · /note TEXT adds one · /notes about PATH TEXT ties one to a file · /notes remove N · /notes clear"));
                    Term.Line(Term.Paint(Term.Dim, $"  Mason reads every note, up to {CodeNotes.Limit}, with each request in this project."));
                    return;
                default:
                    // "/notes We use pnpm" reads as adding a note.
                    CodeNotes.Add(host, scope, rest.Trim());
                    Term.Line(Term.Paint(Term.Dim, "  Kept. Mason reads it with every request in this project."));
                    return;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Term.Line(Term.Paint(Term.Dim, "  " + e.Message));
        }
    }

    /// <summary>After an answer: a note Mason suggested, kept with y, dismissed with n, left undecided with Enter.</summary>
    void OfferSuggestedNote()
    {
        if (session.SuggestedNote is not { Length: > 0 } text || !Term.Interactive) return;
        Term.Line();
        Term.Line($"  {Term.Paint(Term.Accent, "Remember this for next time?")} {text}");
        Term.Write(Term.Paint(Term.Dim, "  y remember · n dismiss · Enter skip "));
        var answer = Choose("yn\r\x1b", default);
        Term.Line();
        try
        {
            if (answer == 'y') { CodeNotes.Add(host, scope, text); Term.Line(Term.Paint(Term.Dim, "  Kept. Mason reads it with every request in this project.")); }
            else if (answer == 'n') CodeNotes.Dismiss(host, scope.Root, text);
            else return;
            session.SuggestedNote = null; store.Save(session);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Term.Line(Term.Paint(Term.Dim, "  " + e.Message));
        }
    }
}
