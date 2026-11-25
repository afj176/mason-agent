using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mason;

public sealed record LlamaBuildInfo(string Path, int? Build, string Version, IReadOnlySet<string> Architectures, string Backend);
public sealed record LlamaAsset(string Name, long Size, string Sha256, Uri Url);
public sealed record LlamaRelease(int Build, string Tag, DateTimeOffset Published, Uri Page, LlamaAsset[] Assets)
{
    /// <summary>Main archive plus, for CUDA, the matching CUDA runtime libraries.</summary>
    public LlamaAsset[]? For(string backend)
    {
        var main = Assets.FirstOrDefault(a => a.Name.Equals($"llama-{Tag}-bin-win-{backend}-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (main == null) return null;
        if (!backend.StartsWith("cuda-", StringComparison.Ordinal)) return [main];
        var cudart = Assets.FirstOrDefault(a => a.Name.Equals($"cudart-llama-bin-win-{backend}-x64.zip", StringComparison.OrdinalIgnoreCase));
        return cudart == null ? null : [main, cudart];
    }
}

/// <summary>Inspects and installs llama.cpp builds. Installs are pinned to an official ggml-org release and SHA-256 verified.</summary>
public static partial class LlamaRuntime
{
    public static readonly string[] Backends = ["cuda-13.5", "cuda-12.4", "vulkan", "cpu"];
    public static string BackendLabel(string backend) => backend switch { "cuda-13.5" => "NVIDIA CUDA 13 (driver 580+)", "cuda-12.4" => "NVIDIA CUDA 12 (older drivers)", "vulkan" => "Vulkan (AMD, Intel, NVIDIA)", _ => "CPU only" };
    public static string ManagedRoot => System.IO.Path.Combine(Store.Root, "runtimes", "llama.cpp");
    static readonly Dictionary<string, (DateTime Stamp, LlamaBuildInfo Info)> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Version from --version and supported architectures from the library's own name table.</summary>
    public static async Task<LlamaBuildInfo?> Inspect(string server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server) || !File.Exists(server)) return null;
        var folder = System.IO.Path.GetDirectoryName(server)!;
        var library = System.IO.Path.Combine(folder, "llama.dll");
        var source = File.Exists(library) ? library : server;
        var stamp = File.GetLastWriteTimeUtc(source);
        lock (cache) if (cache.TryGetValue(server, out var hit) && hit.Stamp == stamp) return hit.Info;
        var architectures = await Task.Run(() => Architectures(File.ReadAllBytes(source)), ct).ConfigureAwait(false);
        string version = ""; int? build = null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(server, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder })!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
            var text = await output.ConfigureAwait(false) + "\n" + await error.ConfigureAwait(false);
            var match = BuildPattern().Match(text); if (match.Success) build = int.Parse(match.Groups[1].Value);
            version = text.Split('\n').FirstOrDefault(l => l.StartsWith("version", StringComparison.OrdinalIgnoreCase))?.Trim() ?? "";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
        var backend = File.Exists(System.IO.Path.Combine(folder, "ggml-cuda.dll")) ? "CUDA" : File.Exists(System.IO.Path.Combine(folder, "ggml-vulkan.dll")) ? "Vulkan" : "CPU";
        var info = new LlamaBuildInfo(server, build, version, architectures, backend);
        lock (cache) cache[server] = (stamp, info);
        return info;
    }

    /// <summary>Architecture names the llama.cpp library contains. Short names may be compiled into code as
    /// 8-byte immediates, so a name longer than 8 characters also matches as its first 8 bytes followed closely by the rest.</summary>
    public static IReadOnlySet<string> Architectures(byte[] binary) => new LibraryNames(binary);

    sealed class LibraryNames(byte[] binary) : IReadOnlySet<string>
    {
        readonly Dictionary<string, bool> known = new(StringComparer.Ordinal);
        bool Valid => Contains("llama") && Contains("qwen2");
        public bool Contains(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || name.Any(c => c > 127)) return false;
            lock (known)
            {
                if (known.TryGetValue(name, out var hit)) return hit;
                var bytes = Encoding.ASCII.GetBytes(name); var span = binary.AsSpan();
                bool found = span.IndexOf(bytes) >= 0;
                if (!found && bytes.Length > 8)
                    for (int at = span.IndexOf(bytes.AsSpan(0, 8)), from = 0; at >= 0 && !found; from += at + 1, at = span[from..].IndexOf(bytes.AsSpan(0, 8)))
                    {
                        int start = from + at + 8; int length = Math.Min(24, binary.Length - start);
                        found = length > 0 && span.Slice(start, length).IndexOf(bytes.AsSpan(8)) >= 0;
                    }
                return known[name] = found;
            }
        }
        public int Count => Valid ? 1 : 0;
        public IEnumerator<string> GetEnumerator() => Enumerable.Empty<string>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<string> other) => other.All(Contains);
        public bool Overlaps(IEnumerable<string> other) => other.Any(Contains);
        public bool SetEquals(IEnumerable<string> other) => throw new NotSupportedException();
    }

    public static async Task<LlamaRelease?> Latest(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=10");
        request.Headers.UserAgent.ParseAdd("MasonAgent/1.1"); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IOException("GitHub release check failed (HTTP " + (int)response.StatusCode + ").");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseReleases(doc.RootElement).FirstOrDefault();
    }

    public static IEnumerable<LlamaRelease> ParseReleases(JsonElement releases)
    {
        foreach (var r in releases.EnumerateArray())
        {
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var m = TagPattern().Match(tag); if (!m.Success || r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var assets = r.GetProperty("assets").EnumerateArray().Select(a =>
            {
                var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() ?? "" : "";
                return new LlamaAsset(a.GetProperty("name").GetString() ?? "", a.GetProperty("size").GetInt64(), digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..].ToLowerInvariant() : "", new Uri(a.GetProperty("browser_download_url").GetString()!));
            }).Where(a => a.Sha256.Length == 64 && a.Url.Scheme == "https" && a.Url.Host == "github.com" && a.Url.AbsolutePath.StartsWith("/ggml-org/llama.cpp/releases/download/" + tag + "/", StringComparison.Ordinal)).ToArray();
            yield return new(int.Parse(m.Groups[1].Value), tag, r.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.MinValue, new Uri("https://github.com/ggml-org/llama.cpp/releases/tag/" + tag), assets);
        }
    }

    /// <summary>Downloads, verifies and unpacks a release into Mason's runtime folder. Returns the new llama-server.exe.</summary>
    public static async Task<string> Install(HttpClient http, LlamaRelease release, string backend, Action<string> progress, CancellationToken ct)
    {
        var assets = release.For(backend) ?? throw new InvalidOperationException("This release has no " + BackendLabel(backend) + " build for Windows x64.");
        var target = System.IO.Path.Combine(ManagedRoot, release.Tag + "-" + backend);
        var staging = target + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var asset in assets)
            {
                var zip = System.IO.Path.Combine(staging, asset.Name);
                await Download(http, asset, zip, progress, ct).ConfigureAwait(false);
                progress("Unpacking " + asset.Name + "…");
                Extract(zip, staging);
                File.Delete(zip);
            }
            var server = Directory.EnumerateFiles(staging, "llama-server.exe", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault()
                ?? throw new InvalidDataException("The release archive has no llama-server.exe.");
            var relative = System.IO.Path.GetRelativePath(staging, server);
            // CUDA runtime DLLs must sit beside the server; move them if the archives used different folders.
            var serverFolder = System.IO.Path.GetDirectoryName(server)!;
            foreach (var dll in Directory.EnumerateFiles(staging, "cu*.dll", SearchOption.AllDirectories).Where(f => System.IO.Path.GetDirectoryName(f) != serverFolder).ToArray())
                File.Move(dll, System.IO.Path.Combine(serverFolder, System.IO.Path.GetFileName(dll)), true);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            return System.IO.Path.Combine(target, relative);
        }
        catch { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
    }

    static async Task Download(HttpClient http, LlamaAsset asset, string file, Action<string> progress, CancellationToken ct)
    {
        var uri = asset.Url;
        for (int hops = 0; ; hops++)
        {
            if (hops > 5) throw new IOException("Too many download redirects.");
            if (uri.Scheme != "https" || !(uri.Host == "github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))) throw new IOException("GitHub redirected to an unexpected host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("MasonAgent/1.1");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next) { uri = next.IsAbsoluteUri ? next : new Uri(uri, next); continue; }
            if (!response.IsSuccessStatusCode) throw new IOException("Download failed (HTTP " + (int)response.StatusCode + ").");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20]; long total = 0; int read; var shown = DateTime.MinValue;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read; if (total > asset.Size) throw new IOException("Download is larger than the release declares.");
                hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (DateTime.UtcNow - shown > TimeSpan.FromMilliseconds(400)) { shown = DateTime.UtcNow; progress($"Downloading {asset.Name} · {HubFormat.Size(total)} of {HubFormat.Size(asset.Size)}"); }
            }
            if (total != asset.Size) throw new IOException("Download ended early.");
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 check failed for " + asset.Name + ". The file was discarded.");
            progress("Verified " + asset.Name + " · SHA-256 matches the release.");
            return;
        }
    }

    public static void Extract(string zip, string folder)
    {
        var root = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 2000) throw new InvalidDataException("The archive has too many entries.");
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, name.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The archive contains an unsafe path.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
        }
    }

    [GeneratedRegex(@"build[:\s]+(\d+)")] private static partial Regex BuildPattern();
    [GeneratedRegex(@"^b(\d+)$")] private static partial Regex TagPattern();
}
