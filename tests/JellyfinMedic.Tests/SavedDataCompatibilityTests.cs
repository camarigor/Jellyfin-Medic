using JellyfinMedic.Api;

namespace JellyfinMedic.Tests;

public sealed class SavedDataCompatibilityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("medic-tests").FullName;

    [Fact]
    public void A_learning_profile_saved_with_fields_since_removed_still_loads()
    {
        // Medic 1.0.18 and earlier also saved RequiresExclusiveExecution and BackedOffSinceUtc. A file that
        // can't be read is set aside as .bad and replaced, so these must still load.
        string path = Path.Combine(_dir, "task_learning_profile.json");
        File.WriteAllText(path, """
            [{ "TaskKey": "RefreshLibrary", "TaskName": "Scan Media Library", "TotalRuns": 4, "Failures": 1,
               "AverageDurationSeconds": 12.5, "RequiresExclusiveExecution": true, "BackedOffSinceUtc": "2026-10-01T03:00:00Z" }]
            """);

        var record = Assert.Single(ScheduleStorage.ReadList<ReliabilityRecord>(path));

        Assert.Equal("RefreshLibrary", record.TaskKey);
        Assert.Equal(4, record.TotalRuns);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".bad"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
