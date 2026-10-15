using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class LoadGuardServerMemoryTests
{
    private static readonly TimeSpan JustNow = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_heavy_task_that_just_started_waits_while_the_whole_server_is_short_of_memory()
    {
        Assert.True(LoadGuard.HoldBack(serverPercent: 90, highMark: 85, runningFor: JustNow, letThrough: false));
    }

    [Fact]
    public void Work_already_under_way_is_not_interrupted_for_the_server()
    {
        Assert.False(LoadGuard.HoldBack(90, 85, TimeSpan.FromMinutes(10), letThrough: false));
    }

    [Fact]
    public void A_task_the_guard_started_again_is_not_held_back_again()
    {
        Assert.False(LoadGuard.HoldBack(90, 85, JustNow, letThrough: true));
    }

    [Fact]
    public void Nothing_waits_when_the_server_has_room_or_its_memory_cannot_be_read()
    {
        Assert.False(LoadGuard.HoldBack(80, 85, JustNow, letThrough: false));
        Assert.False(LoadGuard.HoldBack(null, 85, JustNow, letThrough: false));
    }

    [Fact]
    public void A_waiting_task_starts_again_once_the_server_has_room_or_after_two_hours()
    {
        Assert.False(LoadGuard.CanRestart(jellyfinPercent: 40, serverPercent: 80, clearMark: 73, heavyRunning: 0, waited: TimeSpan.FromMinutes(30)));
        Assert.True(LoadGuard.CanRestart(40, 70, 73, 0, TimeSpan.FromMinutes(30)));
        Assert.True(LoadGuard.CanRestart(40, 80, 73, 0, TimeSpan.FromHours(2)));
        Assert.True(LoadGuard.CanRestart(40, null, 73, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Nothing_starts_again_while_a_heavy_task_runs_or_jellyfin_itself_is_short_of_memory()
    {
        Assert.False(LoadGuard.CanRestart(40, 70, 73, heavyRunning: 1, waited: TimeSpan.FromHours(3)));
        Assert.False(LoadGuard.CanRestart(80, 70, 73, heavyRunning: 0, waited: TimeSpan.FromHours(3)));
    }
}
