using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason.Code;

public sealed record DeveloperEvidence(Guid Id, string Kind, string Label, string Details, string Source, string? DiskHash,
    int Line = 0, bool HasScreenshot = false)
{
    public object ForModel(CodeWorkspace workspace)
    {
        bool? current = null;
        if (DiskHash != null && Source.Length > 0)
        {
            try { current = workspace.Read(Source).Sha256 == DiskHash; }
            catch (Exception e) when (e is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException) { current = false; }
        }
        return new { kind = Kind, label = Label, observation = Details, source = Source, line = Line,
            disk_sha_at_capture = DiskHash, disk_revision_current = current, screenshot_available_in_review = HasScreenshot };
    }

    public static string ImageFile(string hostRoot, Guid session, Guid id) =>
        Path.Combine(hostRoot, "developer-evidence", session.ToString("N"), id.ToString("N") + ".png");
}
