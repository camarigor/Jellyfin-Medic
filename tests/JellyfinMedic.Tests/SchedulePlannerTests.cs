using JellyfinMedic.Api;
using JellyfinMedic.Services;
using static JellyfinMedic.Tests.FakeTaskWorker;

namespace JellyfinMedic.Tests;

public class SchedulePlannerTests
{
    private static readonly Dictionary<string, ReliabilityRecord> NoHistory = new();

    [Fact]
    public void A_kept_task_that_runs_past_midnight_is_not_planned_over()
    {
        // Free from 23:00 to 03:30. A kept task takes 23:45 to about 03:30 every night,
        // so 23:00 to 23:45 is the only room left.
        var kept = new FakeTaskWorker("Someone else's task", "SomeoneElsesTask", 180, Daily(23, 45));
        var planned = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));

        var plan = SchedulePlanner.Build(new[] { kept, planned }, NoHistory, new Dictionary<string, ManagedRun>(), FreeBetween(23, 0, 3, 30));

        Assert.InRange(plan.Single(p => p.Worker == planned).StartMinutes, (23 * 60) + 0, (23 * 60) + 30);
    }

    [Fact]
    public void A_long_task_can_start_before_midnight_and_run_on()
    {
        // Free from 22:00 to 02:00. Three hours plus the buffer needs 3 h 45 min, which only fits across midnight.
        var scan = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 180, Daily(2, 0));

        var task = SchedulePlanner.Build(new[] { scan }, NoHistory, new Dictionary<string, ManagedRun>(), FreeBetween(22, 0, 2, 0)).Single();

        Assert.Equal(Cadence.Daily, task.Cadence);
        Assert.InRange(task.StartMinutes, 22 * 60, (22 * 60) + 15);
    }

    [Fact]
    public void A_kept_task_between_quarter_hours_blocks_all_of_its_run()
    {
        // Free from 23:45 to 00:15. The kept task runs 23:50 to 00:05 (with its buffer), so there's no room.
        var kept = new FakeTaskWorker("Someone else's task", "SomeoneElsesTask", 10, Daily(23, 50));
        var planned = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));

        var plan = SchedulePlanner.Build(new[] { kept, planned }, NoHistory, new Dictionary<string, ManagedRun>(), FreeBetween(23, 45, 0, 15));

        Assert.Equal(Cadence.LeaveAlone, plan.Single(p => p.Worker == planned).Cadence);
    }

    [Fact]
    public void A_kept_task_that_Medic_runs_monthly_keeps_its_time()
    {
        // Free from 01:00 to 01:15. Another kept task takes that quarter hour on every day but Wednesday,
        // and on Wednesday it's taken by a task Medic runs once a month, so there's no room.
        var weekly = new FakeTaskWorker("Someone else's task", "SomeoneElsesTask", 1,
            Enum.GetValues<DayOfWeek>().Where(d => d != DayOfWeek.Wednesday).Select(d => Weekly(d, 1, 0)).ToArray());
        var monthly = new FakeTaskWorker("A monthly task", "AMonthlyTask", 1);
        var managed = new Dictionary<string, ManagedRun>
        {
            [monthly.Id] = new ManagedRun { TaskId = monthly.Id, TaskName = monthly.Name, Weekday = DayOfWeek.Wednesday, TimeOfDayTicks = TimeSpan.FromHours(1).Ticks }
        };
        var planned = new FakeTaskWorker("Optimize database", "OptimizeDatabaseTask", 1, Daily(3, 0));

        var plan = SchedulePlanner.Build(new[] { weekly, monthly, planned }, NoHistory, managed, FreeBetween(1, 0, 1, 15));

        Assert.Equal(Cadence.LeaveAlone, plan.Single(p => p.Worker == planned).Cadence);
    }

    // Everything outside the given window is set as "avoid", so the planner can only use the window.
    private static BusyProfile FreeBetween(int fromHour, int fromMinute, int toHour, int toMinute) =>
        BusyProfile.Create(new UsageSummary(), new PluginConfiguration
        {
            AvoidEnabled = true,
            AvoidStartMinute = (toHour * 60) + toMinute,
            AvoidEndMinute = (fromHour * 60) + fromMinute
        });
}
