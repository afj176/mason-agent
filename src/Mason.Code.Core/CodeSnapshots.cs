using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mason.Code;

public sealed record SnapshotFile(string Path, long Size, long Written, string Hash);
public sealed record WorkspaceSnapshot(Guid Id, DateTimeOffset At, SnapshotFile[] Files, bool Truncated);
public sealed record SnapshotChange(string Path, string? From, string? To, bool Conflict);
public sealed record SnapshotRestore(int Restored, int Created, int Deleted, string[] Conflicts);

/// <summary>
/// Content-addressed workspace snapshots kept in the Mason profile, never in the project. A turn records one before it
/// starts and one when it ends, so undo covers files changed by commands and workflows as well as edit tools. Restoring
/// only touches a file whose current content still matches the snapshot it is leaving; anything changed since is a
/// conflict and is left alone.
/// </summary>
public static class CodeSnapshots
{
    public const int MaxFiles = 20_000;
    public const long MaxFileBytes = 16L << 20;
    const int KeepPerWorkspace = 200;

    static string Key(string workspace) => CodeProtocol.Hash(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)).ToLowerInvariant()))[..24];
    public static string Folder(string settingsRoot, string workspace) => Path.Combine(settingsRoot, "snapshots", Key(workspace));
    static string Manifest(string folder, Guid id) => Path.Combine(folder, "manifests", id.ToString("N") + ".json");
    static string Blob(string folder, string hash) => Path.Combine(folder, "objects", hash[..2], hash + ".gz");

    /// <summary>Records the workspace. Unchanged files (same size and write time as the last capture) are not re-read.</summary>
    public static WorkspaceSnapshot Capture(string settingsRoot, string workspace, CancellationToken ct = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var folder = Folder(settingsRoot, root);
        Directory.CreateDirectory(Path.Combine(folder, "manifests"));
        var indexPath = Path.Combine(folder, "index.json");
        var index = ReadJson<Dictionary<string, SnapshotFile>>(indexPath) ?? new(StringComparer.OrdinalIgnoreCase);
        var files = new List<SnapshotFile>(); bool truncated = false;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }
            foreach (var path in entries)
            {
                var name = Path.GetFileName(path);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (CodeWorkspace.Forbidden(name) || (attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                if (files.Count >= MaxFiles) { truncated = true; continue; }
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes) { truncated = true; continue; }
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                long written = info.LastWriteTimeUtc.Ticks;
                if (index.TryGetValue(relative, out var known) && known.Size == info.Length && known.Written == written && File.Exists(Blob(folder, known.Hash)))
                { files.Add(known); continue; }
                byte[] bytes;
                try { bytes = File.ReadAllBytes(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                var hash = CodeProtocol.Hash(bytes);
                Store(folder, hash, bytes);
                files.Add(new(relative, bytes.Length, written, hash));
            }
        }
        var snapshot = new WorkspaceSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray(), truncated);
        WriteJson(Manifest(folder, snapshot.Id), snapshot);
        WriteJson(indexPath, snapshot.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase));
        Prune(folder);
        return snapshot;
    }

    public static WorkspaceSnapshot? Read(string settingsRoot, string workspace, Guid id) => ReadJson<WorkspaceSnapshot>(Manifest(Folder(settingsRoot, workspace), id));

    /// <summary>Files that differ between two snapshots, marked as conflicts when the file no longer matches <paramref name="from"/>.</summary>
    public static SnapshotChange[] Plan(string settingsRoot, string workspace, Guid from, Guid to)
    {
        var a = Read(settingsRoot, workspace, from) ?? throw new FileNotFoundException("The earlier workspace snapshot is no longer available.");
        var b = Read(settingsRoot, workspace, to) ?? throw new FileNotFoundException("The later workspace snapshot is no longer available.");
        var left = a.Files.ToDictionary(f => f.Path, f => f.Hash, StringComparer.OrdinalIgnoreCase);
        var right = b.Files.ToDictionary(f => f.Path, f => f.Hash, StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(workspace);
        var changes = new List<SnapshotChange>();
        foreach (var path in left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            left.TryGetValue(path, out var fromHash); right.TryGetValue(path, out var toHash);
            if (fromHash == toHash) continue;
            var full = Path.Combine(root, path);
            string? current = File.Exists(full) ? CurrentHash(full) : null;
            changes.Add(new(path, fromHash, toHash, current != fromHash && current != toHash));
        }
        return changes.ToArray();
    }

    /// <summary>Moves the workspace from snapshot <paramref name="from"/> to snapshot <paramref name="to"/>, skipping conflicts.</summary>
    public static SnapshotRestore Restore(string settingsRoot, string workspace, Guid from, Guid to)
    {
        var folder = Folder(settingsRoot, workspace);
        var scope = new CodeWorkspace(workspace);
        int restored = 0, created = 0, deleted = 0; var conflicts = new List<string>();
        foreach (var change in Plan(settingsRoot, workspace, from, to))
        {
            if (change.Conflict) { conflicts.Add(change.Path); continue; }
            var full = scope.Resolve(change.Path);
            if (change.To == null) { if (File.Exists(full)) { File.Delete(full); deleted++; } continue; }
            var target = Load(folder, change.To);
            bool existed = File.Exists(full);
            if (existed && CurrentHash(full) == change.To) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temp = full + ".mason-restore-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllBytes(temp, target);
            File.Move(temp, full, true);
            if (existed) restored++; else created++;
        }
        return new(restored, created, deleted, conflicts.ToArray());
    }

    static string CurrentHash(string path)
    {
        try { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    static void Store(string folder, string hash, byte[] bytes)
    {
        var path = Blob(folder, hash);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        using (var output = File.Create(temp))
        using (var zip = new GZipStream(output, CompressionLevel.Fastest)) zip.Write(bytes);
        try { File.Move(temp, path, false); } catch (IOException) when (File.Exists(path)) { File.Delete(temp); }
    }

    static byte[] Load(string folder, string hash)
    {
        var path = Blob(folder, hash);
        if (!File.Exists(path)) throw new FileNotFoundException("Snapshot content is missing for " + hash[..12] + ".");
        using var input = File.OpenRead(path); using var zip = new GZipStream(input, CompressionMode.Decompress); using var buffer = new MemoryStream();
        zip.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (CodeProtocol.Hash(bytes) != hash) throw new InvalidDataException("Snapshot content failed its hash check.");
        return bytes;
    }

    /// <summary>Keeps the newest manifests and removes content no remaining manifest refers to.</summary>
    static void Prune(string folder)
    {
        var manifests = new DirectoryInfo(Path.Combine(folder, "manifests")).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        if (manifests.Length <= KeepPerWorkspace) return;
        foreach (var old in manifests.Skip(KeepPerWorkspace)) old.Delete();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kept in manifests.Take(KeepPerWorkspace))
            foreach (var file in ReadJson<WorkspaceSnapshot>(kept.FullName)?.Files ?? []) live.Add(file.Hash);
        var objects = Path.Combine(folder, "objects");
        if (!Directory.Exists(objects)) return;
        foreach (var blob in Directory.EnumerateFiles(objects, "*.gz", SearchOption.AllDirectories))
            if (!live.Contains(Path.GetFileNameWithoutExtension(blob))) File.Delete(blob);
    }

    static T? ReadJson<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch (JsonException) { return default; }
    }
    static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value));
        File.Move(temp, path, true);
    }
}
