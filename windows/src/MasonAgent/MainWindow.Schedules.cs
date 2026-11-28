using Mason.Code;
using Microsoft.UI.Xaml;

namespace Mason;

/// <summary>G7: runs due schedules as background tasks while Mason is open. A missed run starts once, not repeatedly.</summary>
public sealed partial class MainWindow
{
    readonly DispatcherTimer scheduleTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly DateTimeOffset scheduleSeen = DateTimeOffset.Now;

    void StartScheduler()
    {
        scheduleTimer.Tick += (_, _) => RunDueSchedules();
        scheduleTimer.Start();
        Closed += (_, _) => scheduleTimer.Stop();
    }

    void RunDueSchedules()
    {
        List<CodeSchedule> all;
        try { all = CodeSchedules.Load(Store.Root); } catch (Exception) { return; }
        foreach (var due in CodeSchedules.Due(all, DateTimeOffset.Now, scheduleSeen).ToArray()) RunSchedule(due);
    }

    /// <summary>Starts one scheduled request as a background task with its saved connection and permission.</summary>
    void RunSchedule(CodeSchedule schedule)
    {
        var (outcome, _) = CodeSchedules.Start(Store.Root, schedule, Path.Combine(AppContext.BaseDirectory, "mason.exe"), due =>
        {
            var profile = (Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []).SingleOrDefault(p => p.Id == due.Connection)
                ?? throw new InvalidOperationException("its model connection was removed");
            var live = modelServing.Running.FirstOrDefault(s => s.Profile.Id == profile.Id && s.Owner.IsRunning);
            if (live == null && profile.ManagedCapability != null) throw new InvalidOperationException("its managed model is not running");
            var key = aiSessionKeys.TryGetValue(profile.Id, out var sessionKey) ? (string.IsNullOrEmpty(sessionKey) ? "" : Store.Protect(sessionKey)) : profile.Secret;
            return new TaskModelConnection(LocalAiClient.ValidateEndpoint((live?.Profile ?? profile).Endpoint).AbsoluteUri, profile.Model, key, live?.Owner.Lifetime);
        });
        status.Text = outcome == "started" ? "Scheduled run started · " + schedule.Name : "Scheduled run “" + schedule.Name + "” skipped · " + outcome["skipped: ".Length..];
    }
}
