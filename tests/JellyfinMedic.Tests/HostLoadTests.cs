using JellyfinMedic.Api;
using JellyfinMedic.Services;
using static JellyfinMedic.Tests.FakeTaskWorker;

namespace JellyfinMedic.Tests;

public class HostLoadTests
{
    [Fact]
    public void Cpu_busy_time_leaves_out_idle_and_iowait()
    {
        var cpu = HostCounters.ParseCpu(new[] { "cpu  100 0 50 800 50 0 0 0 0 0", "cpu0 1 2 3 4" });

        Assert.Equal((150L, 1000L), cpu);
    }

    [Fact]
    public void Memory_in_use_does_not_count_what_can_be_freed()
    {
        var memory = HostCounters.ParseMemory(new[] { "MemTotal:       16000000 kB", "MemFree:  1000000 kB", "MemAvailable:   12000000 kB" });

        Assert.Equal(25, memory);
    }

    [Fact]
    public void Only_whole_drives_are_counted()
    {
        var lines = new[]
        {
            "   8       0 sda 100 0 0 0 0 0 0 0 0 4000 0",
            "   8       1 sda1 100 0 0 0 0 0 0 0 0 3900 0",
            "   7       0 loop0 1 0 0 0 0 0 0 0 0 9999 0",
            " 259       0 nvme0n1 1 0 0 0 0 0 0 0 0 1000 0"
        };

        var disks = HostCounters.ParseDisks(lines, name => name is "sda" or "nvme0n1" or "loop0");

        Assert.Equal(new[] { "nvme0n1", "sda" }, disks.Keys.Order());
        Assert.Equal(4000, disks["sda"]);
    }

    [Fact]
    public void A_reading_is_the_share_of_the_spell_between_two_sets_of_counters()
    {
        var start = new DateTime(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc);
        var before = new HostCounters { TimeUtc = start, CpuBusy = 1000, CpuTotal = 10000, DiskIoMs = new() { ["sda"] = 0, ["sdb"] = 0 } };
        var after = new HostCounters
        {
            TimeUtc = start.AddSeconds(10),
            CpuBusy = 1250,
            CpuTotal = 11000,
            MemoryPercent = 40,
            DiskIoMs = new() { ["sda"] = 2000, ["sdb"] = 7000 }
        };

        var reading = HostCounters.Between(before, after);

        Assert.Equal(new HostLoadReading(25, 40, 70), reading);
    }

    [Fact]
    public void Readings_are_kept_as_averages_by_hour_of_the_week()
    {
        var profile = new HostLoadProfile();
        var tuesday3am = new DateTime(2026, 10, 13, 3, 10, 0);
        HostLoadStore.Add(profile, tuesday3am, new HostLoadReading(20, 50, null));
        HostLoadStore.Add(profile, tuesday3am.AddMinutes(5), new HostLoadReading(40, 70, 10));

        var summary = HostLoadAnalyzer.Summarise(profile);

        int hour = 24 + 3;
        Assert.Equal(30, summary.AverageCpu[hour]);
        Assert.Equal(60, summary.AverageMemory[hour]);
        Assert.Equal(10, summary.AverageDisk[hour]);
        Assert.Null(summary.AverageCpu[hour + 1]);
        Assert.False(summary.Ready);
    }

    [Fact]
    public void Memory_only_counts_towards_busyness_once_it_is_fairly_full()
    {
        var summary = new HostLoadSummary
        {
            AverageCpu = Enumerable.Repeat<double?>(10, 168).ToList(),
            AverageDisk = Enumerable.Repeat<double?>(null, 168).ToList(),
            AverageMemory = Enumerable.Repeat<double?>(60, 167).Append(85).ToList()
        };

        var busy = HostLoadAnalyzer.Busyness(summary);

        Assert.Equal(0.1, busy[0]!.Value, 3);
        Assert.Equal(0.5, busy[167]!.Value, 3);
    }

    [Fact]
    public void Tasks_move_away_from_hours_when_the_server_is_busy()
    {
        var task = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(12, 0));
        var managed = new Dictionary<string, ManagedRun>();
        var history = new Dictionary<string, ReliabilityRecord>();

        // Without readings of the server, the typical household puts it in the early hours.
        var before = SchedulePlanner.Build(new[] { task }, history, managed, BusyProfile.Create(new UsageSummary(), null)).Single();
        Assert.InRange(before.StartMinutes, 0, 6 * 60);

        // The server is flat out from midnight to 07:00 every day, for something Medic knows nothing about.
        var server = new HostLoadSummary
        {
            HoursCollected = 24 * 7,
            Ready = true,
            AverageCpu = Enumerable.Range(0, 168).Select(i => (double?)(i % 24 < 7 ? 100 : 5)).ToList(),
            AverageMemory = Enumerable.Repeat<double?>(40, 168).ToList(),
            AverageDisk = Enumerable.Repeat<double?>(null, 168).ToList()
        };
        var busy = BusyProfile.Create(new UsageSummary(), null, server);
        var after = SchedulePlanner.Build(new[] { task }, history, managed, busy).Single();

        Assert.True(busy.FromServerLoad);
        Assert.NotInRange(after.StartMinutes, 0, 7 * 60);
        Assert.Contains("server", after.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_load_is_ignored_until_there_are_a_few_days_of_it()
    {
        var server = new HostLoadSummary { HoursCollected = 5, AverageCpu = Enumerable.Repeat<double?>(100, 168).ToList() };

        var busy = BusyProfile.Create(new UsageSummary(), null, server);

        Assert.False(busy.FromServerLoad);
        Assert.Equal(0, busy.ServerAt(DayOfWeek.Monday, 120));
        Assert.Contains("Still learning how busy the server is (5 hours", busy.Summary, StringComparison.Ordinal);
    }
}
