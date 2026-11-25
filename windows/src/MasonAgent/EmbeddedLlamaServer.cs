using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Mason;

/// <summary>
/// Runs llama.cpp's server inside Mason's own process. Recent Windows builds ship each tool as a thin exe over an
/// "-impl.dll"; llama-server-impl.dll exports the same entry point the exe calls and the terminate hook llama-cli uses
/// to embed the server. Templates, tool calls, grammars and projectors therefore behave exactly as in llama-server.exe,
/// and clients keep the same loopback API.
/// </summary>
/// <remarks>
/// The server keeps its shutdown handler in a static, so one model can be embedded at a time. The handler is assigned
/// only after the model loads, and until then it may still refer to the previous run, so terminate is requested only
/// once the log reports that the server is listening. One llama.cpp build can be loaded per process: a second build's
/// DLLs would bind to the first build's llama.dll.
/// </remarks>
public sealed class EmbeddedLlamaServer : IModelHost
{
    public const string ServeExport = "?llama_server@@YAHHPEAPEAD@Z", TerminateExport = "?llama_server_terminate@@YAXXZ";
    const uint StillActive = 259;
    static readonly object gate = new();
    static string? loadedFolder, loadFailure;
    static ServeFn? serve; static TerminateFn? terminate;
    static IReadOnlyList<string> devices = [];
    static EmbeddedLlamaServer? active;
    static readonly Dictionary<string, (DateTime Stamp, bool Exports)> exportCache = new(StringComparer.OrdinalIgnoreCase);

    readonly Thread thread;
    readonly string log;
    readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool finished;
    int result, stopRequested;

    public bool IsRunning => !finished;
    public uint ExitCode => finished ? unchecked((uint)result) : StillActive;
    public int ProcessId => Environment.ProcessId;
    public Guid? Lifetime => null;
    public bool Embedded => true;
    public Task Exited => exited.Task;
    /// <summary>Backend devices the loaded build registered, for example "CUDA0" or "Vulkan0".</summary>
    public static IReadOnlyList<string> Devices { get { lock (gate) return devices; } }
    public static string? LoadedFolder { get { lock (gate) return loadedFolder; } }

    EmbeddedLlamaServer(string log) { this.log = log; thread = new Thread(Run, 16 * 1024 * 1024) { IsBackground = true, Name = "Mason embedded llama.cpp" }; }

    static string Folder(string server) => Path.GetDirectoryName(Path.GetFullPath(server))!;
    public static string ImplPath(string server) => Path.Combine(Folder(server), "llama-server-impl.dll");

    /// <summary>Why this build cannot be embedded now, or null when it can.</summary>
    public static string? Unavailable(string server)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return "Embedding llama.cpp requires 64-bit Windows.";
        if (string.IsNullOrWhiteSpace(server) || !File.Exists(server)) return "Choose an existing llama-server.exe in Setup.";
        var impl = ImplPath(server);
        if (!File.Exists(impl)) return "This llama.cpp build has no llama-server-impl.dll. Update llama.cpp in Runtimes to run models inside Mason.";
        if (!HasExports(impl)) return "This llama.cpp build does not export the embedding entry points. Update llama.cpp in Runtimes to run models inside Mason.";
        lock (gate)
        {
            if (loadFailure != null) return "llama.cpp could not be loaded into Mason earlier: " + loadFailure;
            if (loadedFolder != null && !loadedFolder.Equals(Folder(server), StringComparison.OrdinalIgnoreCase))
                return "Mason already loaded llama.cpp from " + loadedFolder + ". Restart Mason to run models inside it from this build.";
            if (active is { IsRunning: true }) return "Another model is already running inside Mason.";
        }
        return null;
    }

    /// <summary>Waits for an embedded model that is shutting down, so a following start can reuse the slot and its GPU memory.</summary>
    public static async Task WaitForStopping(TimeSpan limit, CancellationToken ct)
    {
        EmbeddedLlamaServer? previous;
        lock (gate) previous = active;
        if (previous is not { IsRunning: true } || Volatile.Read(ref previous.stopRequested) == 0) return;
        await previous.Exited.WaitAsync(limit, ct).ConfigureAwait(false);
    }

    static bool HasExports(string impl)
    {
        var stamp = File.GetLastWriteTimeUtc(impl);
        lock (exportCache) if (exportCache.TryGetValue(impl, out var hit) && hit.Stamp == stamp) return hit.Exports;
        var bytes = File.ReadAllBytes(impl).AsSpan();
        bool found = bytes.IndexOf(Encoding.ASCII.GetBytes(ServeExport + "\0")) >= 0 && bytes.IndexOf(Encoding.ASCII.GetBytes(TerminateExport + "\0")) >= 0;
        lock (exportCache) exportCache[impl] = (stamp, found);
        return found;
    }

    /// <summary>Loads the build once per process and returns its backend devices. CUDA runtime libraries are taken from
    /// the build folder, else from <paramref name="cudaLibraries"/>.</summary>
    public static IReadOnlyList<string> Load(string server, string? cudaLibraries)
    {
        var folder = Folder(server);
        lock (gate)
        {
            if (loadedFolder != null)
            {
                if (!loadedFolder.Equals(folder, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Mason already loaded llama.cpp from " + loadedFolder + ".");
                if (loadFailure != null) throw new InvalidOperationException(loadFailure);
                return devices;
            }
            // Pin the folder before loading anything: a partial load must not be followed by a different build.
            loadedFolder = folder;
            try
            {
                // Dependents resolve by module name once loaded, so preload them from their own folders. ggml loads its
                // backends with plain LoadLibrary, which would otherwise search Mason's folder rather than the build's.
                foreach (var name in new[] { "cudart64_*.dll", "cublasLt64_*.dll", "cublas64_*.dll" })
                {
                    var library = Directory.EnumerateFiles(folder, name).FirstOrDefault()
                        ?? (cudaLibraries != null && Directory.Exists(cudaLibraries) ? Directory.EnumerateFiles(cudaLibraries, name).FirstOrDefault() : null);
                    if (library != null) LoadFrom(library);
                }
                foreach (var name in new[] { "libomp.dll", "ggml-base.dll", "ggml.dll", "llama.dll", "mtmd.dll", "llama-common.dll" })
                    if (File.Exists(Path.Combine(folder, name))) LoadFrom(Path.Combine(folder, name));
                // The registry lives in ggml.dll; device accessors live in ggml-base.dll.
                var ggml = NativeLibrary.Load(Path.Combine(folder, "ggml.dll"));
                var ggmlBase = NativeLibrary.Load(Path.Combine(folder, "ggml-base.dll"));
                var impl = LoadFrom(ImplPath(server));
                serve = Marshal.GetDelegateForFunctionPointer<ServeFn>(NativeLibrary.GetExport(impl, ServeExport));
                terminate = Marshal.GetDelegateForFunctionPointer<TerminateFn>(NativeLibrary.GetExport(impl, TerminateExport));
                var loadAll = Marshal.GetDelegateForFunctionPointer<LoadAllFn>(NativeLibrary.GetExport(ggml, "ggml_backend_load_all_from_path"));
                var count = Marshal.GetDelegateForFunctionPointer<CountFn>(NativeLibrary.GetExport(ggml, "ggml_backend_dev_count"));
                var get = Marshal.GetDelegateForFunctionPointer<GetFn>(NativeLibrary.GetExport(ggml, "ggml_backend_dev_get"));
                var name2 = Marshal.GetDelegateForFunctionPointer<NameFn>(NativeLibrary.GetExport(ggmlBase, "ggml_backend_dev_name"));
                loadAll(folder);
                var found = new List<string>();
                for (nuint i = 0, n = count(); i < n; i++) found.Add(Marshal.PtrToStringUTF8(name2(get(i))) ?? "");
                return devices = found;
            }
            catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or IOException)
            {
                loadFailure = e.Message;
                throw new InvalidOperationException("llama.cpp could not be loaded into Mason: " + e.Message, e);
            }
        }
    }

    static IntPtr LoadFrom(string path)
    {
        // LOAD_WITH_ALTERED_SEARCH_PATH: this library's own dependencies resolve from its folder.
        var handle = LoadLibraryEx(path, IntPtr.Zero, 0x00000008);
        if (handle != IntPtr.Zero) return handle;
        int error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, "Could not load " + Path.GetFileName(path) + " (" + new Win32Exception(error).Message + ")");
    }

    /// <summary>Starts the server on a thread of this process with the same arguments llama-server.exe would receive.</summary>
    public static EmbeddedLlamaServer Start(string server, IReadOnlyList<string> arguments, string logPath, string? cudaLibraries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        Load(server, cudaLibraries);
        lock (gate)
        {
            if (Unavailable(server) is { } reason) throw new InvalidOperationException(reason);
            var host = new EmbeddedLlamaServer(logPath);
            host.argv = ["llama-server", .. arguments, "--log-file", logPath];
            active = host;
            host.thread.Start();
            return host;
        }
    }

    string[] argv = [];

    void Run()
    {
        var pointers = argv.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var vector = Marshal.AllocHGlobal(IntPtr.Size * (pointers.Length + 1));
        try
        {
            for (int i = 0; i < pointers.Length; i++) Marshal.WriteIntPtr(vector, i * IntPtr.Size, pointers[i]);
            Marshal.WriteIntPtr(vector, pointers.Length * IntPtr.Size, IntPtr.Zero);
            result = serve!(pointers.Length, vector);
        }
        catch (Exception e)
        {
            result = -1;
            try { File.AppendAllText(log, "\nE embedded server failed: " + e.Message + "\n"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally
        {
            // The server has returned, so it no longer reads its arguments.
            foreach (var pointer in pointers) Marshal.FreeCoTaskMem(pointer);
            Marshal.FreeHGlobal(vector);
            finished = true;
            exited.TrySetResult();
        }
    }

    /// <summary>True once the log shows the server listening. Its shutdown handler is assigned just before that line.</summary>
    bool Listening()
    {
        try
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains("listening on ", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Requests shutdown and returns at once; <see cref="Exited"/> completes when the model is unloaded.</summary>
    public void Stop()
    {
        if (finished || Interlocked.Exchange(ref stopRequested, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            // A model still loading has not installed its handler yet. Wait for it rather than call a stale one.
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            while (!finished && !Listening() && DateTime.UtcNow < deadline) await Task.Delay(200).ConfigureAwait(false);
            if (!finished && Listening()) terminate!();
        });
    }

    public void Dispose() => Stop();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ServeFn(int argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void TerminateFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void LoadAllFn([MarshalAs(UnmanagedType.LPUTF8Str)] string folder);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nuint CountFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetFn(nuint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NameFn(IntPtr device);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
}
