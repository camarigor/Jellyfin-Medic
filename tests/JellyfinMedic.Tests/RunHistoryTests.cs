using JellyfinMedic.Api;

namespace JellyfinMedic.Tests;

public sealed class RunHistoryTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly FakeApplicationPaths _paths = new();

    private static RunRecord Run(string task, int minutes) =>
        new() { TaskId = task, TaskName = task, StartUtc = Start.AddMinutes(minutes), EndUtc = Start.AddMinutes(minutes + 1), Status = "Completed" };

    [Fact]
    public void A_task_that_runs_every_few_minutes_cant_push_out_the_others()
    {
        var runs = Enumerable.Range(0, 2000).Select(i => Run("notifier", 10 + i * 5)).Prepend(Run("scan", 0)).ToList();

        var kept = ScheduleStorage.Trim(runs);

        Assert.Equal(ScheduleStorage.MaxRunsPerTask, kept.Count(r => r.TaskId == "notifier"));
        Assert.Single(kept, r => r.TaskId == "scan");
        // The newest of the frequent task's runs are the ones kept, oldest first.
        Assert.Equal(runs[^1].EndUtc, kept[^1].EndUtc);
        Assert.True(kept.Zip(kept.Skip(1)).All(p => p.First.EndUtc <= p.Second.EndUtc));
    }

    [Fact]
    public void A_run_not_written_yet_is_read_back_and_written_with_the_next_flush()
    {
        ScheduleStorage.AppendRun(_paths, Run("scan", 0));
        ScheduleStorage.AppendRun(_paths, Run("trickplay", 5));

        Assert.Equal(2, ScheduleStorage.ReadRuns(_paths).Count);
        ScheduleStorage.FlushRuns(_paths);
        Assert.Equal(new[] { "scan", "trickplay" }, ScheduleStorage.ReadList<RunRecord>(ScheduleStorage.RunHistoryPath(_paths)).Select(r => r.TaskId));
        Assert.Equal(2, ScheduleStorage.ReadRuns(_paths).Count);
    }

    public void Dispose()
    {
        ScheduleStorage.FlushRuns(_paths);
        Directory.Delete(_paths.Root, recursive: true);
    }
}

public sealed class DamagedProfileTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();

    [Fact]
    public void A_damaged_usage_profile_is_kept_as_bad_and_a_new_one_started()
    {
        string path = JellyfinMedic.Services.UsageStore.FilePath(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var profile = JellyfinMedic.Services.UsageStore.Load(_paths);

        Assert.Equal(0, profile.TotalSamples);
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
        Assert.False(File.Exists(path));
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);
}
