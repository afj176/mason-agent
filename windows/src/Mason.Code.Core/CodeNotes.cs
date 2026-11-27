using System.Text;
using System.Text.Json;

namespace Mason.Code;

/// <summary>
/// Notes: short facts the user keeps about a project ("we use pnpm", "tests live in tests/"). Mason reads every
/// current note, up to <see cref="Limit"/>, with each request, as background rather than instructions. A note can be
/// about a file; once that file changes the note is out of date and is not sent until it is kept again.
/// Notes live in the data folder as project-notes/&lt;fingerprint of the project folder&gt;.json, so the app and the
/// mason command share them and read them fresh each time. Suggestions the user dismissed sit beside them.
/// </summary>
public static class CodeNotes
{
    public const int Limit = 12, TextLimit = 280, DismissedLimit = 50;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly object Gate = new();

    static string Key(string workspace)
    {
        // FNV-1a over the normalized folder: Windows paths compare without case.
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)).ToLowerInvariant();
        ulong hash = 0xcbf29ce484222325;
        foreach (var b in Encoding.UTF8.GetBytes(folder)) { hash ^= b; hash *= 0x100000001b3; }
        return hash.ToString("x");
    }
    static string NotesFile(string home, string workspace) => Path.Combine(home, "project-notes", Key(workspace) + ".json");
    static string DismissedFile(string home, string workspace) => Path.Combine(home, "project-notes", Key(workspace) + "-dismissed.json");

    static T[] Read<T>(string path)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The saved notes exceed 1 MiB.");
        return JsonSerializer.Deserialize<T[]>(File.ReadAllBytes(path), Json) ?? [];
    }
    static void Write<T>(string path, T[] items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(items, Json));
        File.Move(temporary, path, true);
    }

    /// <summary>The project's notes, newest first. Notes saved by earlier versions in the project list move here once.</summary>
    public static ProjectNote[] Load(string home, string workspace, IReadOnlyList<ProjectNote>? legacy = null)
    {
        lock (Gate)
        {
            var path = NotesFile(home, workspace);
            if (!File.Exists(path) && legacy is { Count: > 0 }) Write(path, legacy.ToArray());
            return Read<ProjectNote>(path).Where(n => n != null && n.Id != Guid.Empty && !string.IsNullOrWhiteSpace(n.Text))
                .Select(n => n with { Source = n.Source ?? "" }).OrderByDescending(n => n.Saved).ToArray();
        }
    }

    static string Clean(string text)
    {
        text = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length is 0 or > TextLimit) throw new ArgumentException($"A note is 1 to {TextLimit} characters.");
        return text;
    }
    static string Same(string text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    static ProjectNote Tie(CodeWorkspace scope, Guid id, string text, string source, DateTimeOffset saved)
    {
        source = (source ?? "").Trim();
        var file = source.Length == 0 ? null : scope.Read(source);
        return new ProjectNote(id, text, file?.Path ?? "", file?.Sha256, saved);
    }

    public static ProjectNote Add(string home, CodeWorkspace scope, string text, string source = "")
    {
        lock (Gate)
        {
            text = Clean(text);
            var notes = Load(home, scope.Root);
            if (notes.Length >= Limit) throw new InvalidOperationException($"{Limit} of {Limit}. Remove a note to add another.");
            if (notes.Any(n => Same(n.Text) == Same(text))) throw new InvalidOperationException("That note is already kept.");
            var note = Tie(scope, Guid.NewGuid(), text, source, DateTimeOffset.UtcNow);
            Write(NotesFile(home, scope.Root), notes.Prepend(note).ToArray());
            return note;
        }
    }

    /// <summary>Changes a note's words; the file it is about and when it was saved stay as they were.</summary>
    public static void Edit(string home, string workspace, Guid id, string text)
    {
        lock (Gate)
        {
            var notes = Load(home, workspace);
            var note = notes.SingleOrDefault(n => n.Id == id) ?? throw new ArgumentException("That note is no longer kept.");
            // An older note may be longer than the limit; it can be kept as it is, but new words must fit.
            text = note.Text == text ? text : Clean(text);
            Write(NotesFile(home, workspace), notes.Select(n => n.Id == id ? n with { Text = text } : n).ToArray());
        }
    }

    /// <summary>Keep anyway: ties an out-of-date note to its file as the file is now.</summary>
    public static void Refresh(string home, CodeWorkspace scope, Guid id)
    {
        lock (Gate)
        {
            var notes = Load(home, scope.Root);
            var note = notes.SingleOrDefault(n => n.Id == id) ?? throw new ArgumentException("That note is no longer kept.");
            var tied = Tie(scope, note.Id, note.Text, note.Source, DateTimeOffset.UtcNow);
            Write(NotesFile(home, scope.Root), notes.Select(n => n.Id == id ? tied : n).ToArray());
        }
    }

    public static ProjectNote? Remove(string home, string workspace, Guid id)
    {
        lock (Gate)
        {
            var notes = Load(home, workspace);
            var removed = notes.FirstOrDefault(n => n.Id == id);
            if (removed != null) Write(NotesFile(home, workspace), notes.Where(n => n.Id != id).ToArray());
            return removed;
        }
    }

    /// <summary>Removes every note and returns them, for Undo.</summary>
    public static ProjectNote[] Clear(string home, string workspace)
    {
        lock (Gate)
        {
            var notes = Load(home, workspace);
            Write(NotesFile(home, workspace), Array.Empty<ProjectNote>());
            return notes;
        }
    }

    /// <summary>Undo: puts back notes that were removed or cleared, keeping any added since.</summary>
    public static void Restore(string home, string workspace, IEnumerable<ProjectNote> notes)
    {
        lock (Gate)
        {
            var kept = Load(home, workspace).ToList();
            foreach (var note in notes) if (kept.All(n => n.Id != note.Id)) kept.Add(note);
            Write(NotesFile(home, workspace), kept.OrderByDescending(n => n.Saved).ToArray());
        }
    }

    public static bool Current(CodeWorkspace scope, ProjectNote note)
    {
        if (string.IsNullOrEmpty(note.Source)) return true;
        try { return scope.Read(note.Source).Sha256 == note.SourceHash; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { return false; }
    }

    /// <summary>What the model reads: every current note, newest first, up to the limit; empty when there are none.</summary>
    public static string Context(string home, CodeWorkspace scope, IReadOnlyList<ProjectNote>? legacy = null) =>
        string.Join("\n", Load(home, scope.Root, legacy).Where(n => Current(scope, n)).Take(Limit)
            .Select(n => JsonSerializer.Serialize(new { n.Text, n.Source })));

    public static bool Dismissed(string home, string workspace, string text)
    {
        lock (Gate) return Read<string>(DismissedFile(home, workspace)).Any(d => Same(d) == Same(text));
    }
    public static void Dismiss(string home, string workspace, string text)
    {
        lock (Gate)
        {
            var dismissed = Read<string>(DismissedFile(home, workspace)).Where(d => Same(d) != Same(text)).Prepend(text.Trim()).Take(DismissedLimit).ToArray();
            Write(DismissedFile(home, workspace), dismissed);
        }
    }

    /// <summary>
    /// Checks a note the agent suggests. Throws with a reason the model can act on when the suggestion is too long,
    /// already kept, or one the user dismissed before.
    /// </summary>
    public static string CheckSuggestion(string home, string workspace, string text)
    {
        text = Clean(text);
        if (Load(home, workspace).Any(n => Same(n.Text) == Same(text))) throw new InvalidOperationException("That note is already kept. Don't suggest it again.");
        if (Dismissed(home, workspace, text)) throw new InvalidOperationException("The user dismissed this note before. Don't suggest it again.");
        return text;
    }
}
