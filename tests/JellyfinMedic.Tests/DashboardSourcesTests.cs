using JellyfinMedic.Api;
using JellyfinMedic.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyfinMedic.Tests;

public sealed class ScheduleCalendarTests
{
    [Fact]
    public void A_day_has_each_daily_run_and_that_weekdays_weekly_runs_in_time_order()
    {
        var scan = new FakeTaskWorker("Scan media library", "RefreshLibrary", 30, FakeTaskWorker.Daily(3, 0));
        var people = new FakeTaskWorker("Refresh people", "RefreshPeople", 10, FakeTaskWorker.Weekly(DayOfWeek.Tuesday, 2, 0));
        var chapters = new FakeTaskWorker("Extract chapter images", "RefreshChapterImages", 10, FakeTaskWorker.Weekly(DayOfWeek.Monday, 1, 0));
        var tuesday = new DateTime(2026, 10, 13);

        var runs = ScheduleCalendar.PlannedRuns(new[] { scan, people, chapters }, new Dictionary<string, ReliabilityRecord>(), new Dictionary<string, ManagedRun>(), tuesday, tuesday.AddHours(12));

        Assert.Equal(new[] { "Refresh people", "Scan media library" }, runs.Select(r => r.TaskName));
        Assert.Equal(new[] { 120, 180 }, runs.Select(r => r.StartMinutes));
    }
}

public sealed class ResourceReadingsTests
{
    [Fact]
    public void The_buffer_keeps_the_last_40_readings_oldest_first_and_hands_out_a_copy()
    {
        var start = Dash.Utc(2026, 10, 14, 12);
        for (int i = 0; i < 41; i++)
        {
            ResourceMonitor.Remember(new ResourceSample { TimeUtc = start.AddSeconds(15 * i), CpuPercent = i });
        }

        ResourceMonitor.RecentReadings().Clear();
        var readings = ResourceMonitor.RecentReadings();

        Assert.Equal(40, readings.Count);
        Assert.Equal(1.0, readings[0].CpuPercent);
        Assert.Equal(40.0, readings[^1].CpuPercent);
    }
}

public sealed class LoadGuardPausedTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();

    [Fact]
    public void A_stopped_task_is_listed_once_with_its_name_until_it_is_taken_off()
    {
        using var guard = new LoadGuard(new FakeTaskManager(), _paths, NullLogger<LoadGuard>.Instance);
        var scan = new FakeTaskWorker("Scan media library", "RefreshLibrary", 30);

        guard.Defer(scan);
        guard.Defer(scan);

        var paused = Assert.Single(LoadGuard.Paused);
        Assert.Equal("Scan media library", paused.TaskName);
        Assert.Equal(scan.Id, paused.TaskId);
        guard.DropFirst();
        Assert.Empty(LoadGuard.Paused);
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);
}
