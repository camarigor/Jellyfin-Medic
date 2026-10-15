using System.Text.Json;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class WeeklyLedgerTests
{
    private static WeeklyLedger.DaySnapshot Day(int daysAgo, Dictionary<string, string> issues, params string[] ignored) =>
        new() { Day = DateTime.Today.AddDays(-daysAgo), Issues = issues, Ignored = ignored.ToList() };

    [Fact]
    public void An_issue_ignored_since_last_week_is_not_counted_as_fixed()
    {
        var then = Day(7, new() { ["a"] = "Issue A", ["b"] = "Issue B" });
        var now = Day(0, new() { ["a"] = "Issue A" }, "b");
        var summary = new WeeklySummary();

        WeeklyLedger.Compare(then, now, summary);

        Assert.Empty(summary.FixedIssues);
        Assert.Equal(new[] { "Issue B" }, summary.IgnoredIssues);
    }

    [Fact]
    public void An_issue_that_went_away_is_fixed()
    {
        var then = Day(7, new() { ["a"] = "Issue A", ["b"] = "Issue B" });
        var now = Day(0, new() { ["a"] = "Issue A" });
        var summary = new WeeklySummary();

        WeeklyLedger.Compare(then, now, summary);

        Assert.Equal(new[] { "Issue B" }, summary.FixedIssues);
        Assert.Empty(summary.IgnoredIssues);
    }

    [Fact]
    public void A_snapshot_saved_before_ignored_keys_were_recorded_reads_as_none_ignored()
    {
        var day = JsonSerializer.Deserialize<WeeklyLedger.DaySnapshot>(
            "{\"Day\":\"2026-10-07T00:00:00\",\"Issues\":{\"a\":\"Issue A\"},\"Problems\":0,\"Others\":1}");

        Assert.NotNull(day);
        Assert.Empty(day!.Ignored);
    }
}
