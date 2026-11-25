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
}
