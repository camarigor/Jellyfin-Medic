using JellyfinMedic.Api;

namespace JellyfinMedic.Tests;

public sealed class RunHistoryWriteTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly FakeApplicationPaths _paths = new();

    private string HistoryPath => ScheduleStorage.RunHistoryPath(_paths);

    private static RunRecord Run(string task, int minutes) =>
        new() { TaskId = task, TaskName = task, StartUtc = Start.AddMinutes(minutes), EndUtc = Start.AddMinutes(minutes + 1), Status = "Completed" };

    // A folder where the write's temporary file goes makes the real write fail, as a full disk or a locked file would.
    private void BlockWrites() => Directory.CreateDirectory(HistoryPath + ".tmp");

    private void UnblockWrites() => Directory.Delete(HistoryPath + ".tmp");

    private List<string> Written() => ScheduleStorage.ReadList<RunRecord>(HistoryPath).Select(r => r.TaskId).ToList();

    // Polls instead of sleeping a fixed time: on a loaded host the delayed write can be seconds late.
    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition() && DateTime.UtcNow < limit)
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public void Runs_whose_write_failed_are_kept_and_written_by_the_next_flush()
    {
        BlockWrites();
        ScheduleStorage.AppendRun(_paths, Run("scan", 0));
        ScheduleStorage.AppendRun(_paths, Run("trickplay", 5));

        ScheduleStorage.FlushRuns(_paths);

        Assert.False(File.Exists(HistoryPath));
        Assert.Equal(new[] { "scan", "trickplay" }, ScheduleStorage.ReadRuns(_paths).Select(r => r.TaskId));

        UnblockWrites();
        ScheduleStorage.AppendRun(_paths, Run("backup", 10));
        ScheduleStorage.FlushRuns(_paths);

        Assert.Equal(new[] { "scan", "trickplay", "backup" }, ScheduleStorage.ReadList<RunRecord>(HistoryPath).Select(r => r.TaskId));
        Assert.Equal(3, ScheduleStorage.ReadRuns(_paths).Count);
    }

    [Fact]
    public async Task The_next_run_after_a_failed_write_brings_its_own_delayed_write()
    {
        BlockWrites();
        ScheduleStorage.AppendRun(_paths, Run("scan", 0));
        await Until(() => ScheduleStorage.RunWriteFailing(_paths)); // its delayed write fails
        Assert.True(ScheduleStorage.RunWriteFailing(_paths), "The delayed write never came.");
        UnblockWrites();

        ScheduleStorage.AppendRun(_paths, Run("trickplay", 5));

        // No flush by hand: the write this run schedules, 5 s later, has to come and take the failed one along.
        string[] expected = { "scan", "trickplay" };
        await Until(() => Written().SequenceEqual(expected));

        Assert.Equal(expected, Written());
    }

    [Fact]
    public void Runs_whose_write_failed_are_written_when_jellyfin_stops()
    {
        BlockWrites();
        ScheduleStorage.AppendRun(_paths, Run("scan", 0));
        ScheduleStorage.FlushRuns(_paths);
        UnblockWrites();

        // The flush TaskLifecycleListener.StopAsync makes, with no new run since the failure.
        ScheduleStorage.FlushRuns(_paths);

        Assert.Equal(new[] { "scan" }, ScheduleStorage.ReadList<RunRecord>(HistoryPath).Select(r => r.TaskId));
    }

    [Fact]
    public void Runs_waiting_on_a_file_that_cant_be_written_are_bounded_newest_kept()
    {
        BlockWrites();
        int count = ScheduleStorage.MaxPendingRuns + 10;
        for (int i = 0; i < count; i++)
        {
            ScheduleStorage.AppendRun(_paths, Run("task" + i, i));
            if (i % 1000 == 0)
            {
                ScheduleStorage.FlushRuns(_paths);
            }
        }

        ScheduleStorage.FlushRuns(_paths);

        var waiting = ScheduleStorage.ReadRuns(_paths);
        Assert.Equal(ScheduleStorage.MaxPendingRuns, waiting.Count);
        Assert.Equal("task10", waiting[0].TaskId);
        Assert.Equal("task" + (count - 1), waiting[^1].TaskId);
    }

    public void Dispose()
    {
        if (Directory.Exists(HistoryPath + ".tmp"))
        {
            UnblockWrites();
        }

        ScheduleStorage.FlushRuns(_paths);
        Directory.Delete(_paths.Root, recursive: true);
    }
}
