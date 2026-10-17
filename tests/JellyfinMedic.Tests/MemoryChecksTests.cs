using JellyfinMedic.Api;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class MemoryChecksTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static LoadGuardEvent Event(string action, double hoursAgo, double usedGb = 23.5, double totalGb = 27, string task = "Scan Media Library") =>
        new() { Action = action, TaskName = task, TimestampUtc = Now.AddHours(-hoursAgo), MemoryUsedGb = usedGb, MemoryLimitGb = totalGb };

    // Newest first, as LoadGuardLog keeps them: one postponement a night, the server at 85 to 89%.
    private static List<LoadGuardEvent> NightlyPostponements(int nights) =>
        Enumerable.Range(0, nights)
            .SelectMany(n => new[] { Event("restarted", 6 + (24 * n), 2.1), Event("postponed", 8 + (24 * n), n % 2 == 0 ? 24.0 : 23.0) })
            .ToList();

    [Fact]
    public void Postponements_on_most_nights_say_the_server_sits_above_the_ceiling_and_what_to_do()
    {
        var finding = Assert.Single(DiagnosticsEngine.LoadGuardFindings(NightlyPostponements(5), Now, ceilingPercent: 85, isContainer: true));

        Assert.Equal(Sev.Improve, finding.Severity);
        Assert.Equal("The server's memory sits above Medic's memory ceiling", finding.Title);
        Assert.Contains("postponed 5 heavy task run(s) on 5 of the last 7 days", finding.Current, StringComparison.Ordinal);
        Assert.Contains("ceiling is 85%", finding.Current, StringComparison.Ordinal);
        Assert.Contains("up to 89%", finding.Current, StringComparison.Ordinal); // 24 of 27 GB, the highest reading
        Assert.Contains("Raise the memory ceiling to 91%", finding.Recommended, StringComparison.Ordinal);
        Assert.Contains("free memory", finding.Recommended, StringComparison.Ordinal);
        Assert.Contains("other programs", finding.Why, StringComparison.Ordinal);
        Assert.Contains("73%", finding.Why, StringComparison.Ordinal); // the restart mark, ceiling minus 12
        Assert.StartsWith("Medic → Settings", finding.Where, StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_too_full_to_raise_the_ceiling_past_it_is_told_to_free_memory()
    {
        var events = NightlyPostponements(3).Select(e => { e.MemoryUsedGb = e.Action == "postponed" ? 25.5 : e.MemoryUsedGb; return e; }).ToList();

        var finding = Assert.Single(DiagnosticsEngine.LoadGuardFindings(events, Now, ceilingPercent: 85, isContainer: false));

        Assert.DoesNotContain("Raise the memory ceiling to", finding.Recommended, StringComparison.Ordinal);
        Assert.Contains("Free memory", finding.Recommended, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)] // a couple of busy days are spikes, not the server's usual level
    [InlineData(0)]
    public void Postponements_on_fewer_than_three_days_raise_nothing(int nights)
    {
        Assert.Empty(DiagnosticsEngine.LoadGuardFindings(NightlyPostponements(nights), Now, ceilingPercent: 85, isContainer: true));
    }

    [Fact]
    public void Many_postponements_on_one_day_count_as_one_day()
    {
        var events = Enumerable.Range(0, 6).Select(i => Event("postponed", 2 + i)).ToList();

        Assert.Empty(DiagnosticsEngine.LoadGuardFindings(events, Now, ceilingPercent: 85, isContainer: true));
    }

    [Fact]
    public void Postponements_older_than_a_week_and_a_guard_turned_off_raise_nothing()
    {
        var old = NightlyPostponements(5).Select(e => { e.TimestampUtc = e.TimestampUtc.AddDays(-8); return e; }).ToList();

        Assert.Empty(DiagnosticsEngine.LoadGuardFindings(old, Now, ceilingPercent: 85, isContainer: true));
        Assert.Empty(DiagnosticsEngine.LoadGuardFindings(NightlyPostponements(5), Now, ceilingPercent: null, isContainer: true));
    }

    [Fact]
    public void Stopped_runs_keep_their_own_finding_beside_the_postponed_one()
    {
        var events = NightlyPostponements(4);
        events.Insert(0, Event("stopped", 1, 3.9, 4, "Generate Trickplay Images"));

        var titles = DiagnosticsEngine.LoadGuardFindings(events, Now, ceilingPercent: 85, isContainer: true).Select(f => f.Title).ToList();

        Assert.Equal(new[] { "Tasks have been overloading memory", "The server's memory sits above Medic's memory ceiling" }, titles);
    }

    [Theory]
    [InlineData(27, 3.5, false)] // this host: 27 GB that .NET may address, 3 to 4 GB actually free
    [InlineData(27, 11.9, false)]
    [InlineData(27, 12, true)] // room for the largest suggested folder (8 GB) and a margin
    [InlineData(12, 11, false)]
    public void Transcoding_to_RAM_is_offered_only_with_enough_free_memory(double memoryGb, double freeGb, bool offered)
    {
        var tip = DiagnosticsEngine.RamTranscodeTip("/config/transcodes", "Map a RAM folder", HostKind.Docker, memoryGb, freeGb);

        Assert.Equal(offered, tip is not null);
        if (tip is not null)
        {
            Assert.Equal("You could transcode to RAM", tip.Title);
            Assert.Contains("12 GB of memory free", tip.Why, StringComparison.Ordinal);
            Assert.DoesNotContain("can use", tip.Why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Transcoding_to_RAM_is_not_offered_when_free_memory_is_unknown()
    {
        Assert.Null(DiagnosticsEngine.RamTranscodeTip("/config/transcodes", "Map a RAM folder", HostKind.Docker, 64, null));
    }
}
