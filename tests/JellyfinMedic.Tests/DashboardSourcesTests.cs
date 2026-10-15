using System.Globalization;
using System.Text.Json;
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

public sealed class DiskReadingsTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();

    [Fact]
    public void A_reading_records_what_the_drive_holds_and_a_later_one_the_same_day_fills_it_in()
    {
        string dir = _paths.PluginConfigurationsPath;
        StorageWatch.Record(dir, new List<DriveStatus> { new() { Key = "/mnt/a", FreeGb = 100, TotalGb = 200, ExamplePath = _paths.Root } });
        StorageWatch.Record(dir, new List<DriveStatus> { new() { Key = "/mnt/a", Holds = "Films", FreeGb = 90, TotalGb = 200, ExamplePath = _paths.Root } });

        var reading = Assert.Single(StorageWatch.History(dir));
        Assert.Equal("Films", reading.Holds);
        Assert.Equal(100.0, reading.FreeGb);
    }

    [Fact]
    public void The_background_reading_carries_forward_what_the_drive_holds()
    {
        string dir = _paths.PluginConfigurationsPath;
        string yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.Combine(dir, "JellyfinMedic"));
        File.WriteAllText(
            Path.Combine(dir, "JellyfinMedic", "disk_history.json"),
            JsonSerializer.Serialize(new[] { new DiskSample { Key = "/mnt/a", Day = yesterday, FreeGb = 50, Holds = "Films", Path = _paths.Root } }));

        StorageWatch.RecordKnown(dir);

        Assert.Equal("Films", StorageWatch.History(dir).Single(h => h.Day != yesterday).Holds);
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);
}

public sealed class FreedDaysTests
{
    [Fact]
    public void Space_freed_the_same_day_adds_up_with_each_kind_named_once()
    {
        long before = WeeklyLedger.FreedDays(DateTime.Today).Sum(d => d.Bytes);

        WeeklyLedger.AddFreed("Clean-ups", 1000);
        WeeklyLedger.AddFreed("Track cleanup", 500);
        WeeklyLedger.AddFreed("Clean-ups", 250);

        var today = Assert.Single(WeeklyLedger.FreedDays(DateTime.Today));
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today.Day);
        Assert.Equal(before + 1750, today.Bytes);
        Assert.Equal(new[] { "Clean-ups", "Track cleanup" }, today.Kinds.Order(StringComparer.Ordinal));
    }
}

public sealed class SignInTests
{
    // An activity log entry as the auditor reads it, by property name.
    private sealed class Entry
    {
        public string Type { get; init; } = "AuthenticationSucceeded";

        public string Name { get; init; } = "roamer signed in";

        public string ShortOverview { get; init; } = string.Empty;

        public string UserId { get; init; } = string.Empty;
    }

    [Fact]
    public void An_account_is_counted_by_network_and_failed_sign_ins_apart()
    {
        string id = Guid.NewGuid().ToString("N");
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [id] = "roamer" };
        var entries = new List<object>
        {
            new Entry { UserId = id, ShortOverview = "IP address: 203.0.113.10" },
            new Entry { UserId = id, ShortOverview = "IP address: 203.0.113.11" },
            new Entry { UserId = id, ShortOverview = "IP address: 2001:db8:1:2::10" },
            new Entry { UserId = id, ShortOverview = "IP address: 2001:db8:1:2::99" },
            new Entry { Type = "AuthenticationFailed", Name = "Failed sign-in", ShortOverview = "IP address: 198.51.100.7" }
        };

        var (failures, networks) = SecurityAuditor.SignIns(entries, names);

        Assert.Equal(3, networks["roamer"].Count);
        Assert.Equal("198.51.100.7", Assert.Single(failures).Ip);
    }

    [Fact]
    public void With_no_activity_log_no_account_is_roaming()
    {
        Assert.Empty(SecurityAuditor.RoamingAccounts(null, new List<object>())!);
    }
}
