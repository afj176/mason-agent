using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason.Code;

public sealed record DeveloperEvidence(Guid Id, string Kind, string Label, string Details, string Source, string? DiskHash,
    bool Unsaved = false, int Line = 0, bool HasScreenshot = false)
{
    public object ForModel(CodeWorkspace workspace)
    {
        bool? current = null;
        if (DiskHash != null && Source.Length > 0)
        {
            try { current = workspace.Read(Source).Sha256 == DiskHash; }
            catch (Exception e) when (e is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException) { current = false; }
        }
        return new { kind = Kind, label = Label, observation = Details, source = Source, line = Line, unsaved_buffer = Unsaved,
            disk_sha_at_capture = DiskHash, disk_revision_current = current, screenshot_available_in_review = HasScreenshot };
    }

    public static DeveloperEvidence Editor(CodeWorkspace scope, string json)
    {
        if (json.Length > 30000) throw new InvalidDataException("The editor selection exceeds the attachment limit.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Read(string key, int max)
        {
            string value = root.GetProperty(key).GetString() ?? throw new InvalidDataException(key);
            return value.Length <= max ? value : throw new InvalidDataException("Editor field exceeds its limit: " + key);
        }
        if (root.GetProperty("version").GetInt32() != 1 || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Read("workspace", 4096))),
            Path.TrimEndingDirectorySeparator(scope.Root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Select the same workspace in Mason and the editor.");
        string path = scope.Relative(scope.Resolve(Read("path", 2048)));
        string? hash = File.Exists(scope.Resolve(path)) ? scope.Read(path).Sha256 : null;
        string bufferBase = Read("diskHash", 64);
        if (bufferBase.Length != 0 && !Regex.IsMatch(bufferBase, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Invalid editor disk revision.");
        int first = root.GetProperty("line").GetInt32(), last = root.GetProperty("endLine").GetInt32();
        if (first < 1 || last < first) throw new InvalidDataException("Invalid editor selection range.");
        bool dirty = root.GetProperty("dirty").GetBoolean();
        return new(Guid.NewGuid(), "editor selection", $"{path}:{first}–{last}", JsonSerializer.Serialize(new {
            first, last, bufferVersion = root.GetProperty("bufferVersion").GetInt32(), selection = Read("selection", 6000),
            diagnostics = Read("diagnostics", 2000), changedSinceEditorCapture = !string.Equals(hash ?? "", bufferBase, StringComparison.OrdinalIgnoreCase)
        }), path, bufferBase.Length == 0 ? null : bufferBase.ToLowerInvariant(), dirty, first);
    }

    public static string ImageFile(string hostRoot, Guid session, Guid id) =>
        Path.Combine(hostRoot, "developer-evidence", session.ToString("N"), id.ToString("N") + ".png");
}
