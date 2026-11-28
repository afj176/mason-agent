using System.Text.Json;

namespace Mason.Code;

public enum ScheduleCadence { Hourly, Daily, Weekdays, Weekly }

/// <summary>A request Mason starts on its own as a background task. Stored in the profile, never in the project.</summary>
public sealed record CodeSchedule(Guid Id, string Name, string Workspace, string Prompt, Guid Connection, bool Auto,
    ScheduleCadence Cadence, TimeOnly At, DayOfWeek Day = DayOfWeek.Monday, bool Enabled = true)
{
    public DateTimeOffset? LastRun { get; init; }
    public Guid? LastTask { get; init; }
    public string LastOutcome { get; init; } = "";

    /// <summary>The first run time strictly after <paramref name="after"/>, in local time.</summary>
    public DateTimeOffset Next(DateTimeOffset after)
    {
        var local = after.ToLocalTime();
        if (Cadence == ScheduleCadence.Hourly)
        {
            var hour = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, At.Minute, 0, local.Offset);
            return hour > local ? hour : hour.AddHours(1);
        }
        var candidate = new DateTimeOffset(local.Date + At.ToTimeSpan(), local.Offset);
        for (int i = 0; i < 8; i++, candidate = candidate.AddDays(1))
        {
            if (candidate <= local) continue;
            if (Cadence == ScheduleCadence.Weekdays && candidate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (Cadence == ScheduleCadence.Weekly && candidate.DayOfWeek != Day) continue;
            return candidate;
        }
        return candidate;
    }

    public string Describe() => Cadence switch
    {
        ScheduleCadence.Hourly => $"Every hour at :{At.Minute:00}",
        ScheduleCadence.Daily => $"Every day at {At:HH:mm}",
        ScheduleCadence.Weekdays => $"Weekdays at {At:HH:mm}",
        _ => $"Every {Day} at {At:HH:mm}",
    };
}

/// <summary>Schedules live in the profile's schedules.json. The desktop checks them while it runs; a run missed
/// while Mason was closed starts once at the next check, never repeatedly.</summary>
public static class CodeSchedules
{
    const int MaxSchedules = 32;
    static string Location(string root) => Path.Combine(root, "schedules.json");

    public static List<CodeSchedule> Load(string root)
    {
        var path = Location(root);
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<CodeSchedule>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { return []; }
    }

    public static void Save(string root, IReadOnlyList<CodeSchedule> schedules)
    {
        if (schedules.Count > MaxSchedules) throw new InvalidOperationException("Keep up to " + MaxSchedules + " schedules.");
        foreach (var s in schedules)
            if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 80 || string.IsNullOrWhiteSpace(s.Prompt) || s.Prompt.Length > 16_000 || !Directory.Exists(s.Workspace))
                throw new ArgumentException("Each schedule needs a name, a request of up to 16,000 characters and an existing project folder.");
        Directory.CreateDirectory(root);
        var path = Location(root); var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(schedules, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }

    /// <summary>Enabled schedules whose next run after their last run (or creation) is due.</summary>
    public static IEnumerable<CodeSchedule> Due(IEnumerable<CodeSchedule> schedules, DateTimeOffset now, DateTimeOffset firstSeen) =>
        schedules.Where(s => s.Enabled && s.Next(s.LastRun ?? firstSeen) <= now);

    /// <summary>Starts one scheduled request as a background task and records when it ran and how that went.
    /// <paramref name="bind"/> resolves the saved connection; an exception from it skips the run with its message.</summary>
    public static (string Outcome, Guid? Task) Start(string root, CodeSchedule schedule, string program, Func<CodeSchedule, TaskModelConnection> bind, DateTimeOffset? at = null)
    {
        string outcome; Guid? task = null;
        try
        {
            var main = bind(schedule);
            var permission = schedule.Auto ? CodePermission.Auto : CodePermission.ReadOnly;
            task = new LocalTasks(root).Start(program, schedule.Workspace, schedule.Prompt, main.Endpoint, main.Model, permission, main.Lifetime,
                models: new TaskModelOptions(main, null, null, "default", 64, false));
            outcome = "started";
        }
        catch (Exception e) when (e is not OutOfMemoryException) { outcome = "skipped: " + e.Message; }
        try
        {
            var all = Load(root); var i = all.FindIndex(s => s.Id == schedule.Id);
            if (i >= 0) { all[i] = all[i] with { LastRun = at ?? DateTimeOffset.Now, LastTask = task ?? all[i].LastTask, LastOutcome = outcome }; Save(root, all); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { /* The run already started; a failed bookkeeping write only risks one repeat. */ }
        return (outcome, task);
    }

    /// <summary>Starts every schedule that is due now ("mason --run-due"). A missed run starts once, not repeatedly.</summary>
    public static IReadOnlyList<(string Name, string Outcome)> RunDue(string root, string program, Func<CodeSchedule, TaskModelConnection> bind, DateTimeOffset now) =>
        Due(Load(root), now, now).ToArray().Select(s => (s.Name, Start(root, s, program, bind, now).Outcome)).ToArray();

    public const string Usage =
        "/schedule                                       list schedules\n" +
        "/schedule add NAME hourly :MM [auto] -- REQUEST\n" +
        "/schedule add NAME daily|weekdays HH:MM [auto] -- REQUEST\n" +
        "/schedule add NAME weekly DAY HH:MM [auto] -- REQUEST\n" +
        "/schedule on|off|remove NAME";

    /// <summary>The CLI's /schedule: list, add, on, off and remove, with the same wording on every platform.
    /// <paramref name="connection"/> is the saved connection the session uses; a new schedule runs with it.</summary>
    public static string Command(string arguments, string root, string workspace, (Guid Id, string Model)? connection, DateTimeOffset now)
    {
        var all = Load(root);
        int separator = arguments.IndexOf(" -- ", StringComparison.Ordinal);
        string head = separator < 0 ? arguments : arguments[..separator], prompt = separator < 0 ? "" : arguments[(separator + 4)..].Trim();
        var words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int Index(int word)
        {
            var wanted = word < words.Length ? words[word] : "";
            var i = all.FindIndex(s => s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i : throw new ArgumentException("No schedule named " + wanted + ". /schedule lists them.");
        }
        var verb = words.Length == 0 ? "list" : words[0].ToLowerInvariant();
        if (verb == "list")
        {
            if (all.Count == 0) return "No schedules. Add one with:\n" + Usage;
            return string.Join("\n", all.Select(s =>
            {
                var next = s.Enabled ? "next " + s.Next(s.LastRun is { } last && last > now ? last : now).ToLocalTime().ToString("g") : "off";
                var ran = s.LastOutcome.Length > 0 && s.LastRun is { } at ? "last " + at.ToLocalTime().ToString("g") + " · " + s.LastOutcome : "not run yet";
                return s.Name + "  ·  " + s.Describe() + "  ·  " + (s.Auto ? "Auto" : "read-only") + "  ·  " + next + "\n    " +
                    Path.GetFileName(s.Workspace.TrimEnd('\\', '/')) + ": " + (s.Prompt.Length > 80 ? s.Prompt[..80] : s.Prompt).ReplaceLineEndings(" ") + "\n    " + ran;
            }));
        }
        if (verb == "add")
        {
            if (separator < 0 || words.Length < 4 || prompt.Length == 0) throw new ArgumentException("Use:\n" + Usage);
            if (!Enum.TryParse<ScheduleCadence>(words[2], true, out var cadence) || !Enum.IsDefined(cadence)) throw new ArgumentException("Use:\n" + Usage);
            if (connection is not { } used) throw new ArgumentException("A schedule uses the model this session uses. Choose one with /model first.");
            int at = 3; var day = DayOfWeek.Monday;
            if (cadence == ScheduleCadence.Weekly)
            {
                var given = words[at].ToLowerInvariant();
                var found = Enum.GetValues<DayOfWeek>().Where(d => given.Length >= 3 && d.ToString().StartsWith(given, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (found.Length != 1) throw new ArgumentException("Name the day for a weekly schedule, such as Monday.");
                day = found[0]; at++;
            }
            var time = at < words.Length ? words[at] : "";
            int colon = time.IndexOf(':');
            static int Number(string digits) => digits.Length is >= 1 and <= 2 && digits.All(char.IsAsciiDigit) ? int.Parse(digits) : -1;
            int hour = colon < 0 ? -1 : cadence == ScheduleCadence.Hourly ? 0 : Number(time[..colon]), minute = colon < 0 ? -1 : Number(time[(colon + 1)..]);
            if (hour is < 0 or > 23 || minute is < 0 or > 59) throw new ArgumentException("Give the time as HH:MM, or :MM for an hourly schedule.");
            bool auto = words.Skip(at + 1).Any(w => w.Equals("auto", StringComparison.OrdinalIgnoreCase));
            if (all.Any(s => s.Name.Equals(words[1], StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("A schedule named " + words[1] + " already exists.");
            // Counting from now, as the app does, so a new schedule never fires at once for a time already past.
            var schedule = new CodeSchedule(Guid.NewGuid(), words[1], workspace, prompt, used.Id, auto, cadence, new TimeOnly(hour, minute), day) { LastRun = now };
            Save(root, [.. all, schedule]);
            return "Scheduled " + schedule.Name + ": " + schedule.Describe().ToLowerInvariant() + ", " + (auto ? "in Auto (edits and sandboxed commands)" : "read-only") + ", with " + used.Model +
                ". It runs while the Mason app is open, or when mason --run-due is run.";
        }
        if (verb is "on" or "off")
        {
            int i = Index(1);
            // Switching on counts from now, so a long-paused schedule does not fire at once for a time long past.
            all[i] = verb == "on" ? all[i] with { Enabled = true, LastRun = now } : all[i] with { Enabled = false };
            Save(root, all);
            return all[i].Name + " is " + verb + ".";
        }
        if (verb == "remove")
        {
            int i = Index(1); var removed = all[i].Name;
            all.RemoveAt(i); Save(root, all);
            return "Removed " + removed + ".";
        }
        throw new ArgumentException("Use:\n" + Usage);
    }
}
