using JellyfinMedic.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using static JellyfinMedic.Tests.FakeTaskWorker;

namespace JellyfinMedic.Tests;

public sealed class BackoffTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();
    private readonly FakeTaskManager _tasks = new();

    public BackoffTests()
    {
        new TaskLifecycleListener(_tasks, _paths, NullLogger<TaskLifecycleListener>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void A_failed_task_moves_three_hours_later_until_it_succeeds()
    {
        var task = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));

        _tasks.Complete(task, TaskCompletionStatus.Failed);
        Assert.Equal(At(5, 0), task.Triggers.Single().TimeOfDayTicks);

        _tasks.Complete(task, TaskCompletionStatus.Completed);
        Assert.Equal(At(2, 0), task.Triggers.Single().TimeOfDayTicks);
    }

    [Fact]
    public void A_schedule_changed_during_a_back_off_is_kept()
    {
        var task = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));

        _tasks.Complete(task, TaskCompletionStatus.Failed);
        task.Triggers = new[] { Daily(4, 0) }; // changed by hand in Jellyfin's dashboard
        _tasks.Complete(task, TaskCompletionStatus.Completed);
        Assert.Equal(At(4, 0), task.Triggers.Single().TimeOfDayTicks);

        // The back-off is over, so the next failure moves the new time.
        _tasks.Complete(task, TaskCompletionStatus.Failed);
        Assert.Equal(At(7, 0), task.Triggers.Single().TimeOfDayTicks);
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private static long At(int hour, int minute) => new TimeSpan(hour, minute, 0).Ticks;
}
