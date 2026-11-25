using System.Diagnostics;
using System.Text.Json;
using Mason;
using Mason.Code;

/// <summary>
/// Models in the terminal session: find the ones on this computer, start one with the installed llama.cpp, search
/// Hugging Face, download a build and install the runtime. It uses the desktop app's library, download queue and
/// serving code, so files, settings and verdicts are the same in both. A model started here runs as a process owned
/// by this session and stops when the session ends.
/// </summary>
internal sealed partial class Interactive
{
    ModelServing? serving;
    ServedModel? served;
    LocalModel[] local = [];
    HubModel[] hubResults = [];
    sealed record LastModel(string Path, bool Gpu, int Context);
    string LastModelFile => Path.Combine(host, "cli", "last-model.json");
    static bool Startable(LocalModel model) => model.Format == "GGUF" && model.Task == "Chat";

    // ---- settings shared with the desktop app ------------------------------------------------------------------

    ModelLibrarySettings Library()
    {
        var saved = ModelLibrarySettings.ReadSaved(Path.Combine(host, "model-library.json"));
        if (saved == null) { var found = RuntimeDiscovery.Find(); saved = new([], found.LlamaServer, found.Python); }
        return saved with { Locations = saved.Locations ?? [], LlamaServer = saved.LlamaServer ?? "", Python = saved.Python ?? "", QuantizedPython = saved.QuantizedPython ?? "" };
    }
    DownloadSettings Downloads()
    {
        try { return Store.Read<DownloadSettings>("model-download-settings.json") ?? new(Path.Combine(host, "Models")); }
        catch (Exception e) when (e is JsonException or IOException) { return new(Path.Combine(host, "Models")); }
    }
    HubClient Hub()
    {
        var settings = Downloads();
        return new HubClient(() => { try { return Store.Unprotect(settings.ProtectedToken); } catch (System.Security.Cryptography.CryptographicException) { return ""; } });
    }
    static async Task<RuntimeFacts> Facts(ModelLibrarySettings settings, CancellationToken ct)
    {
        var build = await LlamaRuntime.Inspect(settings.LlamaServer, ct);
        return new(File.Exists(settings.LlamaServer), build?.Build, build?.Architectures, File.Exists(settings.Python), File.Exists(settings.QuantizedPython), ModelAdapters.TextFamilies);
    }

    /// <summary>Runs work behind a status line. Esc or Ctrl+C cancels it.</summary>
    async Task<T> Busy<T>(string label, Func<CancellationToken, Action<string>, Task<T>> work)
    {
        using var stop = new CancellationTokenSource(); run = stop;
        string detail = label; var clock = Stopwatch.StartNew(); int frame = 0;
        try
        {
            var task = Task.Run(() => work(stop.Token, text => detail = text));
            while (!task.IsCompleted)
            {
                if (Term.Interactive && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) stop.Cancel();
                Term.Status($"  {"⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[frame++ / 2 % 10]} {Term.Clip(detail.ReplaceLineEndings(" "), Math.Max(20, Term.Width - 34))}  ·  {clock.Elapsed.TotalSeconds:0}s  ·  Esc to cancel");
                await Task.Delay(80);
            }
            Term.Status("");
            return await task;
        }
        finally { run = null; Term.Status(""); }
    }
    bool Confirm(string question)
    {
        if (!Term.Interactive) { Term.Line("  " + question + " Not confirmed: there is no terminal to ask."); return false; }
        Term.Write($"  {question}  {Term.Paint(Term.Accent, "y")} yes   {Term.Paint(Term.Accent, "n")} no  › ");
        bool yes = Choose("yn\x1b", CancellationToken.None) == 'y';
        Term.Line(yes ? "yes" : "no");
        return yes;
    }

    // ---- command -----------------------------------------------------------------------------------------------

    async Task ModelsCommand(string rest)
    {
        int space = rest.IndexOf(' ');
        string verb = (space < 0 ? rest : rest[..space]).ToLowerInvariant(), argument = space < 0 ? "" : rest[(space + 1)..].Trim();
        try
        {
            switch (verb)
            {
                case "" or "list": await ListLocal(); break;
                case "start": await StartNamed(argument); break;
                case "stop": StopModel(say: true); break;
                case "search": await Search(argument); break;
                case "download": await Download(argument); break;
                case "setup": await QuickSetupFlow(); break;
                case "runtime": if (await InstallRuntime(force: true) is { } path) Term.Line(Term.Paint(Term.Green, "  ✓ llama.cpp is ready") + Term.Paint(Term.Dim, "  " + path)); break;
                default: Term.Line(Term.Paint(Term.Yellow, "  Use /models, /models start, stop, search, download, setup or runtime.")); break;
            }
        }
        catch (OperationCanceledException) { Term.Line(Term.Paint(Term.Yellow, "  Cancelled.")); }
    }

    // ---- models on this computer -------------------------------------------------------------------------------

    async Task Scan()
    {
        var settings = Library();
        var roots = (settings.IncludeCommon ? ModelDiscovery.CommonLocations() : []).Concat(settings.Locations)
            .Append(new ModelLocation("Mason downloads", Downloads().Folder)).Concat(ReadyDownloads())
            .Where(l => Directory.Exists(l.Path)).DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var inventory = await Busy("Looking for models on this computer", (ct, _) => Task.Run(() => ModelDiscovery.Scan(roots, ct), ct));
        // Models this session can start come first; everything else is still listed with what it is for.
        local = inventory.Models.OrderBy(m => Startable(m) ? 0 : 1).ThenBy(m => m.Role).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    IEnumerable<ModelLocation> ReadyDownloads()
    {
        try
        {
            var path = Path.Combine(host, "model-downloads.json");
            if (!File.Exists(path)) return [];
            return (JsonSerializer.Deserialize<List<ModelDownload>>(File.ReadAllText(path)) ?? []).Where(j => j.State == "Ready in library").Select(j => new ModelLocation("Downloaded · " + j.Repository, j.Folder)).ToArray();
        }
        catch (Exception e) when (e is JsonException or IOException or ArgumentException) { return []; }
    }

    async Task ListLocal()
    {
        await Scan();
        if (local.Length == 0)
        {
            Term.Line(Term.Paint(Term.Dim, "  No model files were found on this computer."));
            Term.Line(Term.Paint(Term.Dim, "  /models setup suggests one that fits and downloads it · /models search TEXT looks on Hugging Face."));
            return;
        }
        var facts = await Facts(Library(), CancellationToken.None);
        Term.Line();
        for (int i = 0; i < local.Length && i < 40; i++)
        {
            var model = local[i]; var verdict = ModelCompatibility.Assess(model, facts);
            bool running = served is { Owner.IsRunning: true } && served.Model.Path.Equals(model.Path, StringComparison.OrdinalIgnoreCase);
            string note = running ? Term.Paint(Term.Green, "  ● running here") : Startable(model) ? "" : model.Launchable ? Term.Paint(Term.Dim, "  start it in the desktop app") : "";
            Term.Line($"  {Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(2))}  {(Startable(model) ? Term.Paint(Term.Bold, Term.Clip(model.Name, 52)) : Term.Clip(model.Name, 52))}" +
                Term.Paint(Term.Dim, $"  {model.Format} · {model.Size}") + note);
            Term.Line(Term.Paint(Term.Dim, "      " + Term.Clip(verdict.RoleLabel + " · " + verdict.Summary, Term.Width - 8)));
        }
        if (local.Length > 40) Term.Line(Term.Paint(Term.Dim, $"      … and {local.Length - 40} more files"));
        Term.Line(Term.Paint(Term.Dim, "  Start a coding model with /models start NUMBER (add cpu, or a context size such as 16384)."));
    }

    async Task StartNamed(string argument)
    {
        var words = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool? gpu = null; int? context = null;
        foreach (var word in words.Skip(1).ToArray())
        {
            if (word.Equals("cpu", StringComparison.OrdinalIgnoreCase)) { gpu = false; words.Remove(word); }
            else if (word.Equals("gpu", StringComparison.OrdinalIgnoreCase)) { gpu = true; words.Remove(word); }
            else if (int.TryParse(word, out var tokens) && tokens >= 512) { context = tokens; words.Remove(word); }
        }
        string wanted = string.Join(' ', words);
        if (wanted.Length == 0) throw new ArgumentException("Use /models start NUMBER or NAME. /models lists them.");
        if (local.Length == 0) await Scan();
        var model = int.TryParse(wanted, out var number) && number >= 1 && number <= local.Length ? local[number - 1]
            : local.FirstOrDefault(m => Startable(m) && m.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("No model on this computer matches \"" + wanted + "\". /models lists them.");
        if (!Startable(model)) throw new ArgumentException(model.Name + " is " + ModelVerdict.Label(model.Role).ToLowerInvariant() + " (" + model.Format + "). This session starts GGUF coding models; start others in the desktop app.");
        await StartModel(model, gpu, context);
    }

    /// <summary>Starts a GGUF model with llama.cpp and makes it this session's model. Returns false when it did not start.</summary>
    async Task<bool> StartModel(LocalModel model, bool? gpu = null, int? context = null)
    {
        var settings = Library() with { EmbedGguf = false };
        if (!ModelServing.HasRuntime(model, settings))
        {
            Term.Line(Term.Paint(Term.Yellow, "  llama.cpp is not installed yet; it runs GGUF models."));
            if (await InstallRuntime(force: false) == null) return false;
            settings = Library() with { EmbedGguf = false };
        }
        var (memory, sample) = await Busy("Checking memory for " + model.Name, async (ct, _) => (await Task.Run(() => ModelMemory.Read(model, ct), ct), await GpuTelemetry.Read(ct)));
        var suggested = ModelMemory.Recommend(memory, sample);
        bool onGpu = gpu ?? suggested.Gpu; int tokens = context ?? suggested.Context;
        StopModel(say: true);
        serving ??= new ModelServing();
        string scripts = Path.Combine(AppContext.BaseDirectory, "model-runtime");
        Task<ServedModel> Launch(bool device) => Busy("Starting " + model.Name, (ct, progress) => serving.Start(model, settings, device, scripts, ct, progress, new ModelLaunchOptions(tokens)));
        try
        {
            try { served = await Launch(onGpu); }
            catch (ModelCudaUnavailableException error) when (onGpu)
            {
                Term.Line(Term.Paint(Term.Yellow, "  " + error.Message));
                if (!Confirm("Start it on the CPU instead? It will be slower.")) return false;
                onGpu = false; served = await Launch(false);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or TimeoutException or IOException)
        {
            Term.Line(Term.Paint(Term.Red, "  ✗ " + model.Name + " did not start. ") + error.Message);
            return false;
        }
        profile = served.Profile; key = ""; session.Model = profile.Id; store.Save(session);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastModelFile)!);
            File.WriteAllText(LastModelFile, JsonSerializer.Serialize(new LastModel(model.Path, onGpu, tokens)));
        }
        catch (IOException) { /* Remembering the choice is a convenience; the model is already running. */ }
        var check = served.Profile.Checks.LastOrDefault();
        Term.Line(Term.Paint(Term.Green, "  ✓ " + model.Name + " is running") + Term.Paint(Term.Dim, $"  ·  {(onGpu ? "GPU" : "CPU")} · {tokens:N0} context · stops when you leave mason"));
        if (check is { State: not "Passed" }) Term.Line(Term.Paint(Term.Yellow, "  Its first reply check did not pass: " + check.Detail));
        return true;
    }
    void StopModel(bool say)
    {
        if (served == null) { if (say && serving == null) { } return; }
        var name = served.Model.Name; bool current = profile?.Id == served.Profile.Id;
        try { serving!.Stop(served); } catch (InvalidOperationException) { }
        served = null; if (current) profile = null;
        if (say) Term.Line(Term.Paint(Term.Dim, "  Stopped " + name + "."));
    }
    void StopAllModels() { try { serving?.Dispose(); } catch (Exception e) when (e is InvalidOperationException or IOException) { } serving = null; served = null; }

    // ---- runtime -----------------------------------------------------------------------------------------------

    /// <summary>Installs the llama.cpp build that matches this computer's GPU after confirmation. Returns llama-server.exe, or null.</summary>
    async Task<string?> InstallRuntime(bool force)
    {
        var settings = Library();
        if (!force && File.Exists(settings.LlamaServer)) return settings.LlamaServer;
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        var (sample, release) = await Busy("Finding the current llama.cpp release", async (ct, _) => (await GpuTelemetry.Read(ct), await LlamaRuntime.Latest(http, ct)));
        if (release == null) { Term.Line(Term.Paint(Term.Red, "  No llama.cpp release for Windows was found.")); return null; }
        string backend = QuickSetup.Backend(sample);
        if (release.For(backend) == null) backend = "cpu";
        var assets = release.For(backend);
        if (assets == null) { Term.Line(Term.Paint(Term.Red, "  Release " + release.Tag + " has no Windows x64 build.")); return null; }
        Term.Line($"  llama.cpp {release.Tag} · {LlamaRuntime.BackendLabel(backend)} · {HubFormat.Size(assets.Sum(a => a.Size))}");
        Term.Line(Term.Paint(Term.Dim, "  From the official ggml-org release on GitHub, SHA-256 verified, installed under " + LlamaRuntime.ManagedRoot));
        if (!Confirm("Download and install it?")) return null;
        string installed = await Busy("Installing llama.cpp", (ct, progress) => LlamaRuntime.Install(http, release, backend, progress, ct));
        string server = Path.GetFileName(installed).Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase) ? installed : Path.Combine(installed, "llama-server.exe");
        if (!File.Exists(server)) { Term.Line(Term.Paint(Term.Red, "  The release was installed but llama-server.exe is missing in " + installed)); return null; }
        Store.Write("model-library.json", Library() with { LlamaServer = server });
        return server;
    }

    // ---- Hugging Face ------------------------------------------------------------------------------------------

    async Task Search(string query)
    {
        if (query.Length == 0) throw new ArgumentException("Use /models search TEXT, for example /models search qwen coder.");
        using var hub = Hub();
        var results = await Busy("Searching Hugging Face", (ct, _) => hub.Search(query, "", "", ct));
        // GGUF builds first because this session can run them; nothing is left out, each result says what it is for.
        hubResults = results.OrderBy(m => m.Format == "GGUF" ? 0 : 1).Take(20).ToArray();
        if (hubResults.Length == 0) { Term.Line(Term.Paint(Term.Dim, "  Nothing on Hugging Face matches \"" + query + "\".")); return; }
        var facts = await Facts(Library(), CancellationToken.None);
        Term.Line();
        for (int i = 0; i < hubResults.Length; i++)
        {
            var model = hubResults[i]; var verdict = ModelCompatibility.Assess(model, facts);
            Term.Line($"  {Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(2))}  {Term.Paint(Term.Bold, Term.Clip(model.Id, 60))}" + Term.Paint(Term.Dim, $"  {model.Format}{(model.Size.Length > 0 ? " · " + model.Size : "")} · {model.Signals}{(model.Gated ? " · access terms" : "")}"));
            Term.Line(Term.Paint(Term.Dim, "      " + Term.Clip(verdict.RoleLabel + " · " + verdict.Summary, Term.Width - 8)));
        }
        if (results.Length > hubResults.Length) Term.Line(Term.Paint(Term.Dim, $"      … {results.Length - hubResults.Length} more results. Narrow the search to see them."));
        Term.Line(Term.Paint(Term.Dim, "  See the builds of one with /models download NUMBER."));
    }

    /// <summary>"/models download REPOSITORY" lists its builds; adding a build number or name downloads it.</summary>
    async Task Download(string argument)
    {
        var words = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) throw new ArgumentException("Use /models download NUMBER (from a search) or OWNER/NAME, then choose a build.");
        string id = int.TryParse(words[0], out var number) && number >= 1 && number <= hubResults.Length ? hubResults[number - 1].Id : HubFormat.Repo(words[0]);
        using var hub = Hub();
        var (repository, sample) = await Busy("Reading " + id, async (ct, _) => (await hub.Inspect(id, ct), await GpuTelemetry.Read(ct)));
        double budget = QuickSetup.BudgetGiB(sample);
        var variants = repository.Variants;
        if (variants.Length == 0) { Term.Line(Term.Paint(Term.Yellow, "  " + id + " has no downloadable model files.")); return; }
        var best = QuickSetup.Fit(repository, budget)?.Variant;
        if (words.Length < 2)
        {
            Term.Line();
            Term.Line("  " + Term.Paint(Term.Bold, repository.Id) + Term.Paint(Term.Dim, $"  license {(repository.License.Length > 0 ? repository.License : "not stated")}{(repository.Gated ? " · access terms apply" : "")} · {budget:0.0} GiB of memory available for a model"));
            for (int i = 0; i < variants.Length && i < 40; i++)
            {
                var v = variants[i]; double needed = QuickSetup.NeededGiB(v.Bytes);
                string fit = v.Format != "GGUF" ? Term.Paint(Term.Dim, "runs in the desktop app") : needed <= budget ? Term.Paint(Term.Green, $"fits · about {needed:0.0} GiB") : Term.Paint(Term.Yellow, $"needs about {needed:0.0} GiB");
                Term.Line($"  {Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(2))}  {Term.Clip(v.Name, 46).PadRight(46)} {Term.Paint(Term.Dim, (HubFormat.Size(v.Bytes) + (v.Quality.Length > 0 ? " · " + v.Quality : "")).PadRight(40))} {fit}{(v == best ? Term.Paint(Term.Accent, "  ← suggested") : "")}");
            }
            Term.Line(Term.Paint(Term.Dim, $"  Download one with /models download {repository.Id} NUMBER."));
            return;
        }
        var variant = int.TryParse(words[1], out var pick) && pick >= 1 && pick <= variants.Length ? variants[pick - 1]
            : variants.FirstOrDefault(v => v.Name.Contains(words[1], StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("No build of " + id + " matches \"" + words[1] + "\".");
        await DownloadVariant(hub, repository, variant);
    }

    /// <summary>Downloads one build through the shared download queue, then offers to start it.</summary>
    async Task<bool> DownloadVariant(HubClient hub, HubRepository repository, HubVariant variant)
    {
        string folder = Downloads().Folder;
        Term.Line($"  {Term.Paint(Term.Bold, repository.Id)} · {variant.Name} · {HubFormat.Size(variant.Bytes)} · license {(repository.License.Length > 0 ? repository.License : "not stated")}");
        Term.Line(Term.Paint(Term.Dim, "  From huggingface.co, each file verified against its published hash, saved under " + folder));
        if (repository.Gated) Term.Line(Term.Paint(Term.Yellow, "  This repository has access terms. Accept them on its model page and add a read token in the desktop app if the download is refused."));
        if (!Confirm("Download it?")) return false;
        ModelDownloads queue;
        try { Directory.CreateDirectory(folder); queue = new ModelDownloads(host, hub, ModelDiscovery.CommonLocations().Where(x => x.Name.StartsWith("Hugging Face", StringComparison.Ordinal)).Select(x => x.Path)); }
        catch (IOException)
        {
            Term.Line(Term.Paint(Term.Yellow, "  Mason Agent is open and owns the download queue. Download it there (Models), or close the app and try again."));
            return false;
        }
        ModelDownload job;
        await using (queue)
        {
            job = queue.Enqueue(repository, variant, folder);
            if (job.State is "Paused" or "Needs attention") queue.Resume(job.Id);
            using var stop = new CancellationTokenSource(); run = stop; bool pausing = false;
            try
            {
                while (true)
                {
                    job = queue.Snapshot().First(j => j.Id == job.Id);
                    if (job.State is "Ready in library" or "Cancelled" or "Needs attention" || job.State == "Paused" && pausing) break;
                    if (Term.Interactive && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) stop.Cancel();
                    if (stop.IsCancellationRequested && !pausing) { pausing = true; queue.Pause(job.Id); }
                    Term.Status($"  {job.State} · {job.Percent:0}% · {job.ProgressText}{(job.RateText.Length > 0 ? " · " + job.RateText : "")}  ·  Esc pauses");
                    await Task.Delay(200);
                }
            }
            finally { run = null; Term.Status(""); }
        }
        if (job.State != "Ready in library")
        {
            Term.Line(job.State == "Paused" ? Term.Paint(Term.Yellow, $"  Paused at {job.Percent:0}%. Run the same /models download again to continue.") : Term.Paint(Term.Red, "  ✗ " + job.State + ". ") + job.Detail);
            return false;
        }
        Term.Line(Term.Paint(Term.Green, "  ✓ Downloaded and verified") + Term.Paint(Term.Dim, "  " + job.Folder));
        var inventory = await Task.Run(() => ModelDiscovery.Scan([new ModelLocation("Downloaded · " + job.Repository, job.Folder)], CancellationToken.None));
        local = [];
        var ready = inventory.Models.FirstOrDefault(Startable);
        if (ready == null) { Term.Line(Term.Paint(Term.Dim, "  It is in the library. /models shows what it is for.")); return true; }
        return !Confirm("Start it now?") || await StartModel(ready);
    }

    /// <summary>Suggests coding models that fit this computer's memory, then downloads and starts the chosen one.</summary>
    async Task QuickSetupFlow()
    {
        using var hub = Hub();
        var (sample, picks) = await Busy("Finding coding models that fit this computer", async (ct, progress) =>
        {
            var gpu = await GpuTelemetry.Read(ct); double room = QuickSetup.BudgetGiB(gpu);
            var found = await hub.Search("coder", "", "GGUF", ct);
            var fits = new List<QuickSetup.Choice>();
            foreach (var candidate in found.Where(m => m.Role == AgentRole.Implementer && QuickSetup.Suggestible(m.Id)).OrderByDescending(m => QuickSetup.Trusted(m.Id)).Take(10))
            {
                progress("Checking " + candidate.Id);
                try { var repo = await hub.Inspect(candidate.Id, ct); if (!repo.Gated && QuickSetup.Fit(repo, room) is { } fit) fits.Add(fit); }
                catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException) { }
                if (fits.Count >= 4) break;
            }
            return (gpu, QuickSetup.Rank(fits).ToList());
        });
        double budget = QuickSetup.BudgetGiB(sample);
        Term.Line($"  {sample?.Adapters.FirstOrDefault()?.Name ?? "No GPU detected"}" + Term.Paint(Term.Dim, $"  ·  {budget:0.0} GiB available for a model"));
        if (picks.Count == 0) { Term.Line(Term.Paint(Term.Yellow, $"  No coding model on Hugging Face fits in {budget:0.0} GiB. Free some GPU memory, or pick one with /models search.")); return; }
        for (int i = 0; i < picks.Count; i++)
        {
            Term.Line($"  {Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(2))}  {Term.Paint(Term.Bold, picks[i].Repository.Id)} · {picks[i].Variant.Name} · {HubFormat.Size(picks[i].Variant.Bytes)}");
            Term.Line(Term.Paint(Term.Dim, "      " + picks[i].Reason));
        }
        if (!Term.Interactive) return;
        Term.Write("  Download which? Type a number, or press Enter to skip: ");
        var answer = Console.ReadLine()?.Trim();
        if (!int.TryParse(answer, out var number) || number < 1 || number > picks.Count) return;
        await DownloadVariant(hub, picks[number - 1].Repository, picks[number - 1].Variant);
    }

    // ---- first run ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs when nothing is answering at startup, so plain "mason" needs no options: restart the model used last time,
    /// otherwise offer the coding models on this computer, otherwise offer to find and download one.
    /// </summary>
    async Task FirstRun()
    {
        if (!Term.Interactive) return; // the banner already said how to get a model
        try
        {
            LastModel? last = null;
            try { if (File.Exists(LastModelFile)) last = JsonSerializer.Deserialize<LastModel>(File.ReadAllText(LastModelFile)); }
            catch (Exception e) when (e is JsonException or IOException) { }
            if (last != null && File.Exists(last.Path))
            {
                var again = (await Task.Run(() => ModelDiscovery.Scan([new ModelLocation("Last used", Path.GetDirectoryName(last.Path)!)], CancellationToken.None))).Models
                    .FirstOrDefault(m => m.Path.Equals(last.Path, StringComparison.OrdinalIgnoreCase));
                if (again != null && Startable(again))
                {
                    Term.Line(Term.Paint(Term.Dim, "  No model is running, so Mason is starting the one you used last."));
                    if (await StartModel(again, last.Gpu, last.Context)) return;
                }
            }
            await Scan();
            var choices = local.Where(Startable).Take(12).ToArray();
            Term.Line();
            if (choices.Length == 0)
            {
                Term.Line("  No model is running and no coding model was found on this computer.");
                if (Confirm("Find one that fits this computer and download it?")) await QuickSetupFlow();
                else Term.Line(Term.Paint(Term.Dim, "  Later: /models setup, /models search TEXT, or start your own server and type /model."));
                return;
            }
            Term.Line("  No model is running. Coding models on this computer:");
            for (int i = 0; i < choices.Length; i++) Term.Line($"  {Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(2))}  {Term.Paint(Term.Bold, Term.Clip(choices[i].Name, 60))}" + Term.Paint(Term.Dim, $"  {choices[i].Size}"));
            Term.Write("  Start which? Type a number, or press Enter to skip: ");
            var answer = Console.ReadLine()?.Trim();
            if (int.TryParse(answer, out var number) && number >= 1 && number <= choices.Length) await StartModel(choices[number - 1]);
            else Term.Line(Term.Paint(Term.Dim, "  Later: /models lists them · /models setup suggests a download · /model switches to a running server."));
        }
        catch (OperationCanceledException) { Term.Line(Term.Paint(Term.Yellow, "  Cancelled.") + Term.Paint(Term.Dim, " /models continues when you are ready.")); }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            Term.Line(Term.Paint(Term.Red, "  ✗ " + error.Message));
        }
    }
}
