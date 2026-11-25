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
        string outcome; Guid? task = null;
        try
        {
            var profile = (Store.Read<List<AiEndpointProfile>>("ai-connections.json") ?? []).SingleOrDefault(p => p.Id == schedule.Connection)
                ?? throw new InvalidOperationException("its model connection was removed");
            var live = modelServing.Running.FirstOrDefault(s => s.Profile.Id == profile.Id && s.Owner.IsRunning);
            if (live == null && profile.ManagedCapability != null) throw new InvalidOperationException("its managed model is not running");
            var key = aiSessionKeys.TryGetValue(profile.Id, out var sessionKey) ? (string.IsNullOrEmpty(sessionKey) ? "" : Store.Protect(sessionKey)) : profile.Secret;
            var main = new TaskModelConnection(LocalAiClient.ValidateEndpoint((live?.Profile ?? profile).Endpoint).AbsoluteUri, profile.Model, key, live?.Owner.Lifetime);
            var permission = schedule.Auto ? CodePermission.Auto : CodePermission.ReadOnly;
            task = new LocalTasks(Store.Root).Start(Path.Combine(AppContext.BaseDirectory, "mason.exe"), schedule.Workspace, schedule.Prompt, main.Endpoint, main.Model, permission, main.Lifetime,
                models: new TaskModelOptions(main, null, null, "default", 64, false));
            outcome = "started";
            status.Text = "Scheduled run started · " + schedule.Name;
        }
        catch (Exception e) { outcome = "skipped: " + e.Message; status.Text = "Scheduled run “" + schedule.Name + "” skipped · " + e.Message; }
        try
        {
            var all = CodeSchedules.Load(Store.Root); var i = all.FindIndex(s => s.Id == schedule.Id);
            if (i >= 0) { all[i] = all[i] with { LastRun = DateTimeOffset.Now, LastTask = task ?? all[i].LastTask, LastOutcome = outcome }; CodeSchedules.Save(Store.Root, all); }
        }
        catch (Exception) { /* The run already started; a failed bookkeeping write only risks one repeat. */ }
    }
}
