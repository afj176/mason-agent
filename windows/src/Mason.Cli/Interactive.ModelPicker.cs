using Mason;

/// <summary>
/// The terminal version of the Library's context ladder: /models lists each model with how much of the ladder fits
/// on the GPU, the picker chooses a context size and device with the memory each needs, start shows its stages, and a
/// typed size that is tight or too large asks first. The numbers come from the same ModelMemory code as the app.
/// </summary>
internal sealed partial class Interactive
{
    sealed record Reading(GpuSample? Sample, Dictionary<string, ModelMemoryProfile> Profiles);
    sealed record Pick(int Tokens, bool Gpu, LocalModel? Draft);
    int servedTokens; bool servedGpu; double? servedGiB;

    async Task<Reading> ReadMemory(IEnumerable<LocalModel> models) =>
        await Busy("Reading model sizes and GPU memory", async (ct, _) =>
        {
            var sample = await GpuTelemetry.Read(ct);
            var profiles = await Task.Run(() => models.Where(m => m.Launchable).DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(m => m.Path, m => ModelMemory.Read(m, ct), StringComparer.OrdinalIgnoreCase), ct);
            return new Reading(sample, profiles);
        });

    static string ToneColor(FitTone tone) => tone switch { FitTone.Fits => Term.Green, FitTone.Tight => Term.Yellow, FitTone.TooLarge => Term.Red, _ => Term.Dim };
    static string Symbol(ModelFit fit) => ModelLadder.Tone(fit) switch { FitTone.Fits => "●", FitTone.Tight => "◐", FitTone.TooLarge => "○", _ => "?" };
    static string Paint(ModelFit fit, string text) => Term.Paint(ToneColor(ModelLadder.Tone(fit)), text);

    /// <summary>"RTX 4070 · 8.2 of 12.0 GiB free", or why there is no reading.</summary>
    static string MemoryLine(GpuSample? sample)
    {
        if (sample is { Error: null, Adapters.Length: 1 } && sample.Adapters[0] is { FreeMiB: { } free, TotalMiB: { } total } gpu)
            return $"{ModelLadder.Short(gpu.Name)} · {free / 1024:0.0} of {total / 1024:0.0} GiB free";
        return sample is { Adapters.Length: > 1 } ? "several GPUs · fit is not estimated" : "no GPU memory reading";
    }

    static string AgentUse(LocalModel model) => model.Role switch
    {
        AgentRole.Implementer => "coding", AgentRole.Vision => "coding · vision", AgentRole.CodeSearch => "code search", AgentRole.Reranker => "reranker",
        AgentRole.Draft => "draft model", AgentRole.Adapter => "adapter", AgentRole.Projector => "vision file", _ => "not for the agent"
    };

    /// <summary>The GPU fit column: one symbol per ladder step up to 32K, and a short note.</summary>
    (string Symbols, int Width, string Note) FitColumn(LocalModel model, Reading reading)
    {
        switch (model.Role)
        {
            case AgentRole.Draft:
                var targets = local.Select((m, i) => (m, i)).Where(x => Startable(x.m) && x.m.Architecture.Length > 0 && x.m.Architecture == model.Architecture && model.Bytes * 4 <= x.m.Bytes).Select(x => (x.i + 1).ToString()).ToArray();
                return ("─", 1, targets.Length > 0 ? "attach while starting " + string.Join(" or ", targets) : "attach to a larger model of the same family");
            case AgentRole.Projector: return ("─", 1, "vision companion");
            case AgentRole.Adapter: return ("─", 1, "attach to its base model");
            case AgentRole.NotUsed or AgentRole.Unknown: return ("─", 1, "not for the agent");
        }
        if (!model.Launchable) return ("─", 1, "can't start here");
        var profile = reading.Profiles.GetValueOrDefault(model.Path);
        if (model.Task != "Chat")
        {
            var single = ModelMemory.Assess(profile, reading.Sample, 512);
            return (Paint(single, Symbol(single)), 1, single.State == ModelFit.Fits ? "fits" : single.State.ToLowerInvariant());
        }
        if (!Startable(model)) return ("─", 1, "start it in the desktop app");
        var steps = ModelLadder.Steps(profile).Where(t => t <= 32768).TakeLast(5).ToArray();
        var fits = steps.Select(t => ModelMemory.Assess(profile, reading.Sample, t)).ToArray();
        if (fits.All(f => ModelLadder.Tone(f) == FitTone.Muted))
            return fits[0].State == ModelFit.Unverified ? (Term.Paint(Term.Dim, "?"), 1, "fit unverified") : (Term.Paint(Term.Dim, "—"), 1, fits[0].State.ToLowerInvariant());
        if (fits.All(f => f.State == ModelFit.TooLarge))
            return (Term.Paint(Term.Red, "✕"), 1, fits[0].Estimated ? $"needs {fits[0].LowGiB:0.0}+ GiB · try CPU" : "weights alone exceed free memory");
        string symbols = string.Concat(fits.Select(f => Paint(f, Symbol(f))));
        int best = Array.FindLastIndex(fits, f => f.State == ModelFit.Fits);
        string note = best >= 0 ? "up to " + ModelLadder.Label(steps[best]) : "tight at " + ModelLadder.Label(steps[Array.FindIndex(fits, f => f.State == ModelFit.Tight)]);
        return (symbols, fits.Length, note);
    }

    // ---- step 1: the list --------------------------------------------------------------------------------------

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
        var shown = local.Take(40).ToArray();
        var reading = await ReadMemory(shown);
        var columns = shown.Select(m => FitColumn(m, reading)).ToArray();
        Term.Line();
        Term.Line("  " + Term.Paint(Term.Bold, "On this computer") + Term.Paint(Term.Dim, $" · llama.cpp {facts.BuildLabel} · ") + MemoryLine(reading.Sample));
        Term.Line();
        List<string> Rows(int selected)
        {
            var rows = new List<string> { Term.Paint(Term.Dim, "   #  " + "Model".PadRight(28) + "  " + "Size".PadLeft(9) + "  " + "Agent use".PadRight(16) + "  GPU fit") };
            for (int i = 0; i < shown.Length; i++)
            {
                var model = shown[i]; var (symbols, width, note) = columns[i];
                bool running = served is { Owner.IsRunning: true } && served.Model.Path.Equals(model.Path, StringComparison.OrdinalIgnoreCase);
                string name = Term.Clip(model.Name, 28).PadRight(28);
                string marker = i == selected ? Term.Paint(Term.Accent + Term.Bold, "›") : " ";
                rows.Add($"{marker}{Term.Paint(Term.Dim, (i + 1).ToString().PadLeft(3))}  {(Startable(model) ? Term.Paint(Term.Bold, name) : name)}  {model.Size.PadLeft(9)}  {AgentUse(model).PadRight(16)}  {symbols}{new string(' ', Math.Max(1, 7 - width))}" +
                         (running ? Term.Paint(Term.Green, "● running here") : Term.Paint(Term.Dim, note)));
            }
            if (local.Length > shown.Length) rows.Add(Term.Paint(Term.Dim, $"       … and {local.Length - shown.Length} more files"));
            rows.Add("");
            rows.Add(Term.Paint(Term.Dim, "  ↑↓ choose  ⏎ open  · or type ") + "/models start 1 16k" + Term.Paint(Term.Dim, " to skip the picker"));
            return rows;
        }
        bool canChoose = Term.Interactive && Term.Ansi && shown.Length + 8 < SafeHeight();
        if (!canChoose) { foreach (var row in Rows(-1)) Term.Line(row); return; }
        int selected = Math.Max(0, Array.FindIndex(shown, Startable));
        int height = Term.Block(Rows(selected), 0);
        DrainKeys();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
            {
                selected = Math.Clamp(selected + (key.Key == ConsoleKey.UpArrow ? -1 : 1), 0, shown.Length - 1);
                height = Term.Block(Rows(selected), height); continue;
            }
            Term.Block(Rows(-1), height);
            if (key.Key != ConsoleKey.Enter) return;
            var model = shown[selected];
            if (!Startable(model)) { Term.Line(Term.Paint(Term.Yellow, "  " + model.Name + ": " + columns[selected].Note + ".")); return; }
            if (Picker(model, reading) is { } pick) await StartModel(model, pick.Gpu, pick.Tokens, pick.Draft, reading);
            return;
        }
    }
    static int SafeHeight() { try { return Console.WindowHeight; } catch (IOException) { return 0; } }

    // ---- step 2: the picker ------------------------------------------------------------------------------------

    /// <summary>Chooses a context size, device and draft model. Without a styled terminal it returns the suggestion.</summary>
    Pick? Picker(LocalModel model, Reading reading)
    {
        var profile = reading.Profiles.GetValueOrDefault(model.Path);
        var steps = ModelLadder.Steps(profile);
        var suggested = ModelMemory.Recommend(profile, reading.Sample);
        int at = Array.IndexOf(steps, suggested.Context); if (at < 0) at = Math.Max(0, Array.FindLastIndex(steps, t => t <= suggested.Context));
        bool gpu = suggested.Gpu, explain = false; int draft = 0;
        var drafts = new LocalModel?[] { null }.Concat(local.Where(x => x.Format == "GGUF" && x.Path != model.Path && (x.Role == AgentRole.Draft || x.Role == AgentRole.Implementer && x.Architecture.Length > 0 && x.Architecture == model.Architecture && x.Bytes * 4 <= model.Bytes))).ToArray();
        if (!Term.Interactive || !Term.Ansi) return new(steps[at], gpu, null);
        var fits = steps.Select(t => ModelMemory.Assess(profile, reading.Sample, t)).ToArray();
        List<string> Lines()
        {
            string Cell(string text, int i, string color) => i == at ? Term.Paint(Term.Selected, (Term.Styled ? " " + text + " " : "[" + text + "]").PadLeft(8)) : Term.Paint(color, text.PadLeft(8));
            var fit = fits[at];
            var facts = new[] { ModelLadder.Quant(model), model.Format, model.Architecture, profile is { ContextDeclared: true } ? "max " + ModelLadder.Label(profile.MaxContext) : "", model.Size + " on disk" }.Where(x => !string.IsNullOrEmpty(x));
            var lines = new List<string>
            {
                "", "  " + Term.Paint(Term.Bold, model.Name), Term.Paint(Term.Dim, "  " + string.Join(" · ", facts)), "",
                Term.Paint(Term.Dim, "  Context ") + string.Concat(steps.Select((t, i) => Cell(ModelLadder.Label(t), i, ""))),
                Term.Paint(Term.Dim, "  GiB     ") + string.Concat(fits.Select((f, i) => Cell(f.LowGiB is { } low ? (f.Estimated ? $"{low:0.0}" : $"{low:0.0}+") : "—", i, gpu ? ToneColor(ModelLadder.Tone(f)) : Term.Dim))),
                Term.Paint(Term.Dim, "  Fit     ") + string.Concat(fits.Select((f, i) => i == at ? Term.Paint(Term.Selected, (Term.Styled ? " " + Symbol(f) + " " : "[" + Symbol(f) + "]").PadLeft(8)) : (gpu ? Paint(f, Symbol(f).PadLeft(8)) : Term.Paint(Term.Dim, Symbol(f).PadLeft(8))))),
                "", BarLine(fit, reading.Sample, gpu)
            };
            if (gpu && fit.LowGiB.HasValue && reading.Sample is { Error: null, Adapters.Length: 1 }) lines.Add(Term.Paint(Term.Dim, "           other apps  model + cache   margin  spare"));
            if (drafts.Length > 1) lines.Add(Term.Paint(Term.Dim, "  Draft   ") + (drafts[draft] is { } d ? d.Name + Term.Paint(Term.Dim, " · " + d.Size) : "none"));
            if (explain)
            {
                lines.Add("");
                foreach (var part in Wrap("Read from the GGUF header and a fresh driver reading: the weights, the context cache and runtime headroom, with " +
                                          $"{fit.SpareGiB ?? .5:0.0} GiB kept spare. The low end is shown under each size; the margin covers the high end. Other apps can change free memory before Start. " +
                                          (profile?.Unknown ?? profile?.Basis ?? ""), Math.Max(40, Term.Width - 6)))
                    lines.Add(Term.Paint(Term.Dim, "  " + part));
            }
            lines.Add("");
            string Key(string key, string what) => Term.Paint(Term.Accent, key) + Term.Paint(Term.Dim, " " + what);
            lines.Add("  " + string.Join("   ", new[] { Key("←→", "context"), Key("g", "GPU") + "  " + Key("c", "CPU"), drafts.Length > 1 ? Key("d", "draft model") : "", Key("?", "how this is estimated"), Key("⏎", "start"), Key("esc", "back") }.Where(x => x.Length > 0)));
            return lines;
        }
        int height = Term.Block(Lines(), 0);
        DrainKeys();
        while (true)
        {
            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.LeftArrow: at = Math.Max(0, at - 1); break;
                case ConsoleKey.RightArrow: at = Math.Min(steps.Length - 1, at + 1); break;
                case ConsoleKey.Enter: return new(steps[at], gpu, drafts[draft]);
                case ConsoleKey.Escape: Term.Line(Term.Paint(Term.Dim, "  Not started.")); return null;
                default:
                    switch (char.ToLowerInvariant(key.KeyChar))
                    {
                        case 'g': gpu = true; break;
                        case 'c': gpu = false; break;
                        case 'd' when drafts.Length > 1: draft = (draft + 1) % drafts.Length; break;
                        case '?': explain = !explain; break;
                        default: continue;
                    }
                    break;
            }
            height = Term.Block(Lines(), height);
        }
    }

    /// <summary>A 30-cell bar of the GPU's memory: other apps, the model and its cache (low end), the margin to the high end, what stays free and the spare.</summary>
    static string BarLine(ModelFit fit, GpuSample? sample, bool gpu)
    {
        if (!gpu) return Term.Paint(Term.Dim, "  CPU      ") + "runs from system RAM" + Term.Paint(Term.Dim, " · not checked here");
        if (sample is not { Error: null, Adapters.Length: 1 } || sample.Adapters[0] is not { FreeMiB: { } freeMiB, TotalMiB: { } totalMiB } || fit.LowGiB is not { } low)
            return Term.Paint(Term.Dim, "  GPU      ") + Paint(fit, fit.State.ToLowerInvariant());
        double total = totalMiB / 1024, free = freeMiB / 1024, high = fit.HighGiB ?? low, spare = fit.SpareGiB ?? .5;
        int Cell(double gib) => (int)Math.Round(Math.Clamp(gib / total, 0, 1) * 29);
        int other = Cell(total - free), model = Math.Max(other, Cell(total - free + low)), margin = Math.Max(model, Cell(total - free + high));
        var bar = Term.Paint(Term.Dim, new string('█', other)) + Term.Paint(Term.Accent, new string('█', model - other)) + Term.Paint(Term.Yellow, new string('▒', margin - model)) + Term.Paint(Term.Dim, new string('░', 29 - margin) + "│");
        string range = fit.Estimated ? $"{low:0.0}–{high:0.0} of {free:0.0} GiB" : $"{low:0.0}+ of {free:0.0} GiB";
        return Term.Paint(Term.Dim, "  GPU      ") + bar + "  " + Paint(fit, fit.State.ToLowerInvariant()) + Term.Paint(Term.Dim, " · " + range);
    }

    static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width) { yield return line.ToString(); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    // ---- step 4: a typed size that is tight or too large ------------------------------------------------------

    /// <summary>Asks before starting at a typed size that is tight or too large. Returns the size and device to use, or null to cancel.</summary>
    static (int Tokens, bool Gpu)? ConfirmTyped(LocalModel model, int tokens, Reading reading)
    {
        var profile = reading.Profiles.GetValueOrDefault(model.Path);
        var fit = ModelMemory.Assess(profile, reading.Sample, tokens);
        if (fit.State is not (ModelFit.Tight or ModelFit.TooLarge)) return (tokens, true);
        string label = ModelLadder.Label(tokens);
        string needed = fit.LowGiB is { } low ? (fit.Estimated ? $"{low:0.0}–{fit.HighGiB:0.0} GiB needed" : $"{low:0.0}+ GiB needed") : "";
        Term.Line("  " + Paint(fit, (fit.State == ModelFit.Tight ? "◐ Tight fit at " : "✕ Too large at ") + label) + Term.Paint(Term.Dim, $" · {needed}, {fit.FreeGiB:0.0} GiB free, {fit.SpareGiB:0.0} kept spare"));
        var suggested = ModelMemory.Recommend(profile, reading.Sample);
        if (!Term.Interactive) { Term.Line(Term.Paint(Term.Dim, "    Starting anyway: there is no terminal to ask.")); return (tokens, true); }
        string Key(string key, string what) => Term.Paint(Term.Accent, key) + " " + what;
        Term.Write("    " + Key("y", "start anyway") + "   " + (suggested.Gpu ? Key("s", $"use {ModelLadder.Label(suggested.Context)} instead") : Key("c", "use CPU")) + "   " + Key("n", "cancel") + "  " + Term.Paint(Term.Accent, "› "));
        char choice = Choose(suggested.Gpu ? "ysn\x1b" : "ycn\x1b", CancellationToken.None);
        Term.Line(choice switch { 'y' => "start anyway", 's' => "use " + ModelLadder.Label(suggested.Context), 'c' => "use CPU", _ => "cancel" });
        return choice switch { 'y' => (tokens, true), 's' => (suggested.Context, true), 'c' => (tokens, false), _ => null };
    }

    // ---- status ------------------------------------------------------------------------------------------------

    /// <summary>"● Qwen2.5-Coder-7B  16K · GPU · ~7.0 GiB  127.0.0.1:8099  /models stop", while a model this session started runs.</summary>
    string? ModelStatusLine()
    {
        if (served is not { Owner.IsRunning: true } running) return null;
        string endpoint = running.Profile.Endpoint.Replace("http://", "").TrimEnd('/');
        string device = servedGpu ? "GPU" + (servedGiB is { } gib ? $" · ~{gib:0.0} GiB" : "") : "CPU";
        return "  " + Term.Paint(Term.Green, "●") + " " + running.Model.Name + Term.Paint(Term.Dim, $"  {ModelLadder.Label(servedTokens)} · {device}  {endpoint}  /models stop");
    }
}
