using JellyfinMedic.Services;
using MediaBrowser.Model.Tasks;

namespace JellyfinMedic.Tests;

public sealed class LoadGuardServerMemoryTests
{
    private static readonly TimeSpan JustNow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HalfAnHour = TimeSpan.FromMinutes(30);

    [Fact]
    public void A_heavy_task_that_just_started_waits_while_the_whole_server_is_short_of_memory()
    {
        Assert.True(LoadGuard.HoldBack(serverPercent: 90, usualServer: null, jellyfinPercent: 5, highMark: 85, runningFor: JustNow, letThrough: false));
    }

    [Fact]
    public void Work_already_under_way_is_not_interrupted_for_the_server()
    {
        Assert.False(LoadGuard.HoldBack(90, null, 5, 85, TimeSpan.FromMinutes(10), letThrough: false));
    }

    [Fact]
    public void A_task_the_guard_started_again_is_not_held_back_again()
    {
        Assert.False(LoadGuard.HoldBack(90, null, 5, 85, JustNow, letThrough: true));
    }

    [Fact]
    public void Nothing_waits_when_the_server_has_room_or_its_memory_cannot_be_read()
    {
        Assert.False(LoadGuard.HoldBack(80, null, 5, 85, JustNow, letThrough: false));
        Assert.False(LoadGuard.HoldBack(null, null, 5, 85, JustNow, letThrough: false));
    }

    [Fact]
    public void A_server_that_always_sits_above_the_ceiling_does_not_hold_up_every_task()
    {
        // Other programs keep this host at 85 to 89%: waiting would only move the work two hours later.
        Assert.False(LoadGuard.HoldBack(serverPercent: 89, usualServer: 87, jellyfinPercent: 5, highMark: 85, runningFor: JustNow, letThrough: false));
    }

    [Fact]
    public void A_rise_above_the_usual_level_or_Jellyfin_past_the_ceiling_still_holds_a_task_back()
    {
        Assert.True(LoadGuard.HoldBack(serverPercent: 93, usualServer: 87, jellyfinPercent: 5, highMark: 85, runningFor: JustNow, letThrough: false));
        Assert.True(LoadGuard.HoldBack(serverPercent: 88, usualServer: 87, jellyfinPercent: 90, highMark: 85, runningFor: JustNow, letThrough: false));
    }

    [Fact]
    public void A_waiting_task_starts_again_once_the_server_has_room_or_after_two_hours()
    {
        Assert.False(LoadGuard.CanRestart(jellyfinPercent: 40, serverPercent: 80, usualServer: null, highMark: 85, clearMark: 73, heavyRunning: 0, waited: HalfAnHour, avoidedHours: false, watching: false));
        Assert.True(LoadGuard.CanRestart(40, 70, null, 85, 73, 0, HalfAnHour, false, false));
        Assert.True(LoadGuard.CanRestart(40, 80, null, 85, 73, 0, TimeSpan.FromHours(2), false, false));
        Assert.True(LoadGuard.CanRestart(40, null, null, 85, 73, 0, TimeSpan.Zero, false, false));
    }

    [Fact]
    public void A_waiting_task_starts_again_once_the_server_is_back_to_its_usual_level()
    {
        Assert.False(LoadGuard.CanRestart(jellyfinPercent: 40, serverPercent: 93, usualServer: 87, highMark: 85, clearMark: 73, heavyRunning: 0, waited: HalfAnHour, avoidedHours: false, watching: false));
        Assert.True(LoadGuard.CanRestart(40, 88, 87, 85, 73, 0, HalfAnHour, false, false));
    }

    [Fact]
    public void After_two_hours_a_task_starts_again_while_jellyfin_is_below_the_ceiling()
    {
        Assert.False(LoadGuard.CanRestart(jellyfinPercent: 80, serverPercent: 70, usualServer: null, highMark: 85, clearMark: 73, heavyRunning: 0, waited: HalfAnHour, avoidedHours: false, watching: false));
        Assert.True(LoadGuard.CanRestart(80, 70, null, 85, 73, 0, TimeSpan.FromHours(2), false, false));
        Assert.True(LoadGuard.CanRestart(80, 90, null, 85, 73, 0, TimeSpan.FromHours(3), false, false));
    }

    [Fact]
    public void Nothing_starts_again_while_a_heavy_task_runs_or_jellyfin_itself_is_short_of_memory()
    {
        Assert.False(LoadGuard.CanRestart(40, 70, null, 85, 73, heavyRunning: 1, waited: TimeSpan.FromHours(3), avoidedHours: false, watching: false));
        Assert.False(LoadGuard.CanRestart(85, 70, null, 85, 73, heavyRunning: 0, waited: TimeSpan.FromHours(3), avoidedHours: false, watching: false));
        Assert.False(LoadGuard.CanRestart(85.5, 70, null, 85, 73, heavyRunning: 0, waited: TimeSpan.FromHours(3), avoidedHours: false, watching: false));
    }

    [Fact]
    public void Nothing_starts_again_in_the_avoided_hours_or_while_someone_is_watching()
    {
        Assert.False(LoadGuard.CanRestart(40, 70, null, 85, 73, 0, TimeSpan.FromHours(3), avoidedHours: true, watching: false));
        Assert.False(LoadGuard.CanRestart(40, 70, null, 85, 73, 0, TimeSpan.FromHours(3), avoidedHours: false, watching: true));
    }

    [Fact]
    public void The_avoided_hours_can_run_past_midnight()
    {
        var evening = new PluginConfiguration { AvoidEnabled = true, AvoidStartMinute = 18 * 60, AvoidEndMinute = 23 * 60 };
        var night = new PluginConfiguration { AvoidEnabled = true, AvoidStartMinute = 22 * 60, AvoidEndMinute = 2 * 60 };

        Assert.True(LoadGuard.InAvoidedHours(new TimeSpan(18, 30, 0), evening));
        Assert.False(LoadGuard.InAvoidedHours(new TimeSpan(23, 0, 0), evening));
        Assert.True(LoadGuard.InAvoidedHours(new TimeSpan(1, 0, 0), night));
        Assert.False(LoadGuard.InAvoidedHours(new TimeSpan(3, 0, 0), night));
        Assert.False(LoadGuard.InAvoidedHours(new TimeSpan(18, 30, 0), new PluginConfiguration { AvoidStartMinute = 18 * 60, AvoidEndMinute = 23 * 60 }));
    }

    [Fact]
    public void The_usual_level_is_the_middle_reading_of_the_last_two_hours_once_there_are_twenty_minutes_of_them()
    {
        var usual = new LoadGuard.UsualMemory();
        var start = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

        usual.Add(start, 86);
        usual.Add(start.AddMinutes(10), 95);
        Assert.Null(usual.Level(start.AddMinutes(10)));

        usual.Add(start.AddMinutes(20), 88);
        Assert.Equal(88, usual.Level(start.AddMinutes(20)));

        // Readings older than two hours no longer count.
        usual.Add(start.AddMinutes(125), 70);
        usual.Add(start.AddMinutes(131), 71);
        Assert.Equal(71, usual.Level(start.AddMinutes(131)));
    }

    // Heavy task triggers as Jellyfin stores them, on the server's clock.
    private static TaskTriggerInfo Daily(int hour, long? limitHours = null) => new()
    {
        Type = TaskTriggerInfoType.DailyTrigger,
        TimeOfDayTicks = TimeSpan.FromHours(hour).Ticks,
        MaxRuntimeTicks = limitHours is { } h ? TimeSpan.FromHours(h).Ticks : null
    };

    private static readonly DateTime GuardStarted = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private static bool Scheduled(DateTime startedUtc, TaskResult? previous, DateTime? managedStartedLocal, params TaskTriggerInfo[] triggers) =>
        LoadGuard.StartedBySchedule(triggers, previous, startedUtc, TimeZoneInfo.Utc, GuardStarted, managedStartedLocal);

    [Fact]
    public void A_run_that_starts_at_its_daily_or_weekly_time_counts_as_scheduled()
    {
        var at3 = new DateTime(2026, 10, 17, 3, 0, 20, DateTimeKind.Utc); // a Saturday
        Assert.True(Scheduled(at3, null, null, Daily(3)));
        Assert.True(Scheduled(at3, null, null, new TaskTriggerInfo { Type = TaskTriggerInfoType.WeeklyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks, DayOfWeek = DayOfWeek.Saturday }));
        Assert.False(Scheduled(at3, null, null, new TaskTriggerInfo { Type = TaskTriggerInfoType.WeeklyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks, DayOfWeek = DayOfWeek.Sunday }));

        // Just after midnight, for a trigger at 23:59.
        var late = new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = new TimeSpan(23, 59, 0).Ticks };
        Assert.True(Scheduled(new DateTime(2026, 10, 18, 0, 0, 30, DateTimeKind.Utc), null, null, late));
    }

    [Fact]
    public void A_run_started_by_hand_or_queued_by_Jellyfin_does_not_count_as_scheduled()
    {
        // The Run button at 15:00 on a task that runs at 03:00, and a library scan queued for a task with no clock time.
        Assert.False(Scheduled(new DateTime(2026, 10, 17, 15, 0, 0, DateTimeKind.Utc), null, null, Daily(3)));
        Assert.False(Scheduled(new DateTime(2026, 10, 17, 15, 0, 0, DateTimeKind.Utc), null, null));
    }

    [Fact]
    public void An_interval_run_is_scheduled_once_the_interval_has_passed_since_the_last_run()
    {
        var interval = new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(12).Ticks };
        var ended = new DateTime(2026, 10, 17, 2, 0, 0, DateTimeKind.Utc);
        var last = new TaskResult { EndTimeUtc = ended, StartTimeUtc = ended.AddMinutes(-20) };

        Assert.True(Scheduled(ended.AddHours(12).AddMinutes(1), last, null, interval));
        Assert.False(Scheduled(ended.AddHours(4), last, null, interval));
    }

    [Fact]
    public void A_run_at_Jellyfin_start_or_one_Medic_starts_monthly_counts_as_scheduled()
    {
        var startup = new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        Assert.True(Scheduled(GuardStarted.AddMinutes(1), null, null, startup));
        Assert.False(Scheduled(GuardStarted.AddHours(3), null, null, startup));

        var now = new DateTime(2026, 10, 17, 4, 0, 5, DateTimeKind.Utc);
        Assert.True(Scheduled(now, null, new DateTime(2026, 10, 17, 4, 0, 0, DateTimeKind.Unspecified)));
        Assert.False(Scheduled(now, null, new DateTime(2026, 9, 3, 4, 0, 0, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void A_restart_keeps_the_time_limit_set_on_the_task_trigger()
    {
        Assert.Equal(TimeSpan.FromHours(3).Ticks, LoadGuard.RestartOptions(new[] { Daily(3, limitHours: 3) }).MaxRuntimeTicks);
        Assert.Equal(TimeSpan.FromHours(2).Ticks, LoadGuard.RestartOptions(new[] { Daily(3, limitHours: 4), Daily(15, limitHours: 2) }).MaxRuntimeTicks);
        Assert.Null(LoadGuard.RestartOptions(new[] { Daily(3) }).MaxRuntimeTicks);
    }

    [Fact]
    public void A_restart_is_recorded_on_the_same_memory_as_the_hold()
    {
        var log = new List<LoadGuardEvent>
        {
            new() { Action = "stopped", TaskName = "Trickplay" },
            new() { Action = "restarted", TaskName = "Scan" },
            new() { Action = "postponed", TaskName = "Scan" },
            new() { Action = "stopped", TaskName = "Scan" }
        };

        // Newest first, as the log keeps them.
        Assert.Equal(LoadGuard.ServerMemoryBasis, LoadGuard.HeldOn(log, "Scan"));
        Assert.Equal(LoadGuard.JellyfinMemory, LoadGuard.HeldOn(log, "Trickplay"));
        Assert.Equal(LoadGuard.JellyfinMemory, LoadGuard.HeldOn(log, "Segments"));
    }
}
