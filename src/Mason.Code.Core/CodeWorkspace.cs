using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mason.Code;

public sealed record CodeFile(string Path, string Content, string Sha256, bool Bom);
public sealed record CodeFiles(string[] Files, bool Truncated)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool RootIsOnlyDirectory { get; init; }
}
public sealed record CodeReplacement(string OldText, string NewText);

internal sealed class CodeWorkspacePathException(string message) : UnauthorizedAccessException(message);
internal sealed class CodeWorkspaceDirectoryException(string message) : DirectoryNotFoundException(message);

/// <summary>Bounded, UTF-8-only repository access. These checks are not an operating-system sandbox.</summary>
public sealed class CodeWorkspace
{
    public const int FileLimit = 128 * 1024;
    static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>Per-command scratch folder created inside the project by the Auto sandbox.</summary>
    internal const string SandboxFolder = ".mason-sandbox";
    // Earlier builds named the scratch folder ".dbcowboy-sandbox". Nothing is created there any more, but a leftover one stays excluded so it is never treated as project content.
    internal const string LegacySandboxFolder = ".dbcowboy-sandbox";
    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { SandboxFolder, LegacySandboxFolder, ".git", ".svn", ".hg", ".aws", ".ssh", ".azure", "node_modules", "bin", "obj", "vendor", ".venv", "venv", "dist", "artifacts", ".cache", "__pycache__" };
    readonly StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public string Root { get; }
    public CodeWorkspace(string root)
    {
        Root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("Open an existing workspace folder.");
        CheckLinks(Root);
    }
    internal static bool Forbidden(string name) => DeviceName(name) || Excluded.Contains(name) || name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase) || name.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
        new[] { ".pem", ".key", ".pfx", ".p12", ".kdbx" }.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase));
    static bool DeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9';
    }
    internal static void CheckLinks(string path)
    {
        for (string? part = path; part != null; part = Directory.GetParent(part)?.FullName)
        {
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Workspace file tools do not traverse links or junctions: " + part);
            if (OperatingSystem.IsWindows() && File.Exists(part))
            {
                using var file = File.OpenHandle(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!GetFileInformationByHandle(file.DangerousGetHandle(), out var information)) throw new System.ComponentModel.Win32Exception();
                if (information.Links > 1) throw new UnauthorizedAccessException("Workspace file tools reject files with multiple hard links: " + part);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct FileInformation { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written; public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(IntPtr file,out FileInformation information);
    public string Resolve(string relative, bool directory = false)
    {
        string DisplayPath() => JsonSerializer.Serialize(relative is { Length: > 160 } ? relative[..160] + "…" : relative);
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1000 || System.IO.Path.IsPathRooted(relative) || relative.Contains(':') || relative.Any(char.IsControl))
            throw new CodeWorkspacePathException($"Rejected workspace path {DisplayPath()}. Use a relative workspace path without a drive, stream or device prefix.");
        var segments = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(p => p == ".." || p.TrimEnd(' ', '.') != p && p != "." || Forbidden(p))) throw new CodeWorkspacePathException($"Rejected workspace path {DisplayPath()}. The path is outside the workspace or addresses excluded content.");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative));
        if (!full.Equals(Root, comparison) && !full.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, comparison)) throw new CodeWorkspacePathException($"Rejected workspace path {DisplayPath()}. The path is outside the workspace.");
        CheckLinks(full);
        if (directory && !Directory.Exists(full))
        {
            if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("The selected workspace folder is no longer available. Reopen it before continuing.");
            throw new CodeWorkspaceDirectoryException($"Directory {DisplayPath()} was not found inside the selected workspace.");
        }
        return full;
    }
    public string Relative(string full) => System.IO.Path.GetRelativePath(Root, full).Replace('\\', '/');
    internal string ResolveAgentCreate(string relative, bool directory)
    {
        var full = Resolve(relative);
        var parts = relative.Replace('\\', '/').Split('/', 2);
        var first = parts[0];
        if (first == "." || File.Exists(Path.Combine(Root, first)) || Directory.Exists(Path.Combine(Root, first))) return full;
        var name = Path.GetFileName(Root);
        bool echo = first.Equals(name, comparison) && (directory || parts.Length > 1);
        if (!echo && !IsMalformedRoot(first)) return full;
        var suggested = parts.Length == 2 ? JsonSerializer.Serialize(parts[1]) : directory ? "\".\"" : "the intended filename";
        throw new CodeWorkspacePathException($"The selected project is already open as '.'. Use {suggested} here. To deliberately create the literal nested name, prefix the original path with './'.");
    }
    bool IsMalformedRoot(string relative)
    {
        // Some structured decoders spill closing JSON delimiters into a root argument.
        var stem = relative.EndsWith("}},", StringComparison.Ordinal) ? relative[..^3] :
            relative.EndsWith("}}", StringComparison.Ordinal) ? relative[..^2] : null;
        return stem != null && (stem == "." || stem.Equals(Path.GetFileName(Root), comparison));
    }
    public CodeFile Read(string relative)
    {
        var full = Resolve(relative);
        FileStream Open()
        {
            try { return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { throw new FileNotFoundException($"Workspace file {JsonSerializer.Serialize(Relative(full))} was not found. Consult the workspace inventory for its relative path.", relative, error); }
        }
        using var input = Open();
        if (input.Length > FileLimit) throw new InvalidDataException("File exceeds the 128 KiB text-file limit. Open it in your editor.");
        using var buffer = new MemoryStream(); input.CopyTo(buffer); var bytes = buffer.ToArray();
        if (bytes.Length > FileLimit || bytes.Contains((byte)0)) throw new InvalidDataException("File is too large or is binary.");
        var bom = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 });
        return new(Relative(full), Utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), CodeProtocol.Hash(bytes), bom);
    }
    public CodeFiles Files(string relative = ".", int limit = 200)
    {
        if (limit is < 1 or > 1000) throw new ArgumentException("List between 1 and 1,000 files.");
        var files = new List<string>(); var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((Resolve(relative, true), 0));
        int inspected = 0;
        bool hasDirectory = false, clippedDepth = false;
        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (++inspected > 8000) return new(files.ToArray(), true);
                var info = File.GetAttributes(path);
                if (Forbidden(System.IO.Path.GetFileName(path)) || (info & FileAttributes.ReparsePoint) != 0) continue;
                if ((info & FileAttributes.Directory) != 0)
                {
                    hasDirectory = true;
                    if (depth < 12) queue.Enqueue((path, depth + 1));
                    else clippedDepth = true;
                    continue;
                }
                if (files.Count == limit) return new(files.ToArray(), true);
                files.Add(Relative(path));
            }
        }
        return new(files.ToArray(), clippedDepth) { RootIsOnlyDirectory = relative == "." && !hasDirectory };
    }
    internal object ListForAgent(string relative, int limit)
    {
        try { return Files(relative, limit); }
        catch (CodeWorkspaceDirectoryException) when (IsMalformedRoot(relative) && !File.Exists(Resolve(relative)))
        {
            var root = Files(".", limit);
            return new { root.Files, root.Truncated, requested_path = relative, path = "." };
        }
    }
    public object Search(string query, string relative = ".")
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new ArgumentException("Search for 1–200 literal characters.");
        var listing = Files(relative, 1000); var matches = new List<object>(); int skipped = 0;
        foreach (var path in listing.Files)
        {
            CodeFile file;
            try { file = Read(path); } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException) { skipped++; continue; }
            var lines = file.Content.Split('\n');
            for (int i = 0; i < lines.Length; i++) if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new { path, line = i + 1, text = lines[i].Length > 400 ? lines[i][..400] + " [line clipped]" : lines[i] });
                if (matches.Count >= 60) return new { matches, truncated = true, skipped };
            }
        }
        return new { matches, truncated = listing.Truncated, skipped };
    }
    public string Instructions(string relative = "AGENTS.md")
    {
        var full = Resolve(relative); var current = Directory.Exists(full) ? full : System.IO.Path.GetDirectoryName(full)!;
        var folders = new List<string>();
        while (current.StartsWith(Root, comparison)) { folders.Add(current); if (current == Root) break; current = Directory.GetParent(current)!.FullName; }
        var text = new StringBuilder();
        foreach (var folder in folders.AsEnumerable().Reverse())
        {
            var path = System.IO.Path.Combine(folder, "AGENTS.md"); if (!File.Exists(path)) continue;
            var file = Read(Relative(path)); if (text.Length + file.Content.Length > 16_000) throw new InvalidDataException("Applicable AGENTS.md instructions exceed 16,000 characters; choose a smaller scope.");
            text.AppendLine("--- " + file.Path + " ---").AppendLine(file.Content);
        }
        return text.ToString();
    }
    static byte[] Bytes(string content, bool bom)
    {
        var bytes = Utf8.GetBytes(content); if (bytes.Length > FileLimit || content.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f'))) throw new InvalidDataException("Edits require UTF-8 text up to 128 KiB without unexpected control characters.");
        return bom ? new byte[] { 239, 187, 191 }.Concat(bytes).ToArray() : bytes;
    }
    public CodeFile Edit(string relative, string expected, string oldText, string newText, Action<CodeFile, string> beforeWrite)
        => ApplyEdits(relative, expected, [new(oldText, newText)], beforeWrite);
    public CodeFile ApplyEdits(string relative, string expected, IReadOnlyList<CodeReplacement> edits, Action<CodeFile, string> beforeWrite)
    {
        var file = Read(relative);
        if (!file.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("File changed since the last read. Read it again; nothing was overwritten.");
        if (edits.Count is < 1 or > 32) throw new ArgumentException("Apply 1–32 replacements per file.");
        var regions = new List<(int At, CodeReplacement Edit)>();
        foreach (var edit in edits)
        {
            int at = file.Content.IndexOf(edit.OldText, StringComparison.Ordinal);
            if (edit.OldText.Length == 0 && (file.Content.Length > 0 || edits.Count > 1) || at < 0 || edit.OldText.Length > 0 && file.Content.IndexOf(edit.OldText, at + edit.OldText.Length, StringComparison.Ordinal) >= 0)
                throw new ArgumentException("Each old_text must match exactly once in the original file; use larger unique context.");
            regions.Add((at, edit));
        }
        regions.Sort((a, b) => a.At.CompareTo(b.At));
        for (int i = 1; i < regions.Count; i++) if (regions[i].At < regions[i - 1].At + regions[i - 1].Edit.OldText.Length) throw new ArgumentException("Replacements overlap. Nothing was written.");
        var content = file.Content;
        foreach (var region in regions.AsEnumerable().Reverse()) content = content[..region.At] + region.Edit.NewText + content[(region.At + region.Edit.OldText.Length)..];
        if(content==file.Content)throw new InvalidDataException("The requested edit makes no change. Inspect the file and provide a different implementation or verify it; do not repeat this edit.");
        var bytes = Bytes(content, file.Bom);
        beforeWrite(file, content);
        var path = Resolve(relative);
        // Hold the file without write/delete sharing while verifying and replacing its bytes.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var current = new MemoryStream(); stream.CopyTo(current);
        if (CodeProtocol.Hash(current.ToArray()) != file.Sha256) throw new IOException("File changed while this edit was being reviewed.");
        stream.Position = 0; stream.Write(bytes); stream.SetLength(bytes.Length); stream.Flush(true);
        return new(file.Path, content, CodeProtocol.Hash(bytes), file.Bom);
    }
    public string CreateDirectory(string relative) => CreateDirectory(relative, out _);

    internal string CreateDirectory(string relative, out bool created)
    {
        var path = Resolve(relative);
        if (File.Exists(path)) throw new IOException("A file already occupies that path.");
        created = !Directory.Exists(path);
        if (created) Directory.CreateDirectory(path);
        CheckLinks(path);
        return Relative(path);
    }
    public CodeFile Create(string relative, string content)
    {
        var path = Resolve(relative); var parent = System.IO.Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Create files in an existing directory in this first release.");
        if (File.Exists(path))
            throw new IOException($"File {JsonSerializer.Serialize(Relative(path))} already exists. Its contents were preserved. If the file still needs changes, use read_file and edit the current version. Otherwise continue or finish the request.");
        var bytes = Bytes(content, false);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); output.Flush(true);
        return new(Relative(path), content, CodeProtocol.Hash(bytes), false);
    }
    public void Restore(CodeChange change)
    {
        var current = Read(change.Path);
        if (change.Restored || current.Sha256 != change.AfterHash) throw new IOException("The file no longer matches this session's last edit. Restore it manually to preserve later changes.");
        if (change.Before == null) throw new InvalidOperationException("New-file removal is manual in this release. Open the file in your editor.");
        Edit(change.Path, current.Sha256, current.Content, change.Before, (_, _) => { }); change.Restored = true;
    }
}
