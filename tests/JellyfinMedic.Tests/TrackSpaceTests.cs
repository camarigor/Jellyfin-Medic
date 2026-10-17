using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class TrackSpaceTests : IDisposable
{
    private const long GiB = 1L << 30;

    private readonly string _root = Directory.CreateTempSubdirectory("medic-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_floor_is_10_GiB_or_5_percent_of_the_drive()
    {
        Assert.Equal(10 * GiB, TrackCleaner.SpaceFloor(100 * GiB));
        Assert.Equal(50 * GiB, TrackCleaner.SpaceFloor(1000 * GiB));
        Assert.Equal(10 * GiB, TrackCleaner.SpaceFloor(-1));
    }

    [Fact]
    public void A_run_stops_short_of_the_floor_rather_than_filling_the_drive()
    {
        long writing = 0;

        // 4 GiB file: the copy, a 1 GiB margin and the 10 GiB floor need 15 GiB.
        Assert.True(TrackCleaner.Reserve(15 * GiB, 100 * GiB, 4 * GiB, ref writing, out _));
        Assert.Equal(4 * GiB, writing);

        // A second file now counts the first one's copy still being written.
        Assert.False(TrackCleaner.Reserve(15 * GiB, 100 * GiB, 4 * GiB, ref writing, out long needed));
        Assert.Equal(9 * GiB, needed);
        Assert.Equal(4 * GiB, writing);
    }

    [Fact]
    public void Unknown_free_space_doesnt_hold_a_file_back()
    {
        long writing = 0;
        Assert.True(TrackCleaner.Reserve(-1, -1, 4 * GiB, ref writing, out _));
    }

    [Fact]
    public void An_earlier_releases_original_is_moved_aside_under_a_dated_name()
    {
        string kept = Path.Combine("films", ".medic-originals", "Film (2020).mkv");

        Assert.Equal(Path.Combine("films", ".medic-originals", "Film (2020).replaced-20261017-093005.mkv"),
            TrackCleaner.ReplacedPath(kept, new DateTime(2026, 10, 17, 9, 30, 5)));
    }

    [Fact]
    public void A_kept_original_without_its_file_is_an_orphan_unless_its_swap_was_cut_short()
    {
        string originals = Directory.CreateDirectory(Path.Combine(_root, ".medic-originals")).FullName;
        string Kept(string name)
        {
            string path = Path.Combine(originals, name);
            File.WriteAllText(path, "x");
            return path;
        }

        string inPlace = Kept("In place.mkv");
        File.WriteAllText(Path.Combine(_root, "In place.mkv"), "x");
        string renamed = Kept("Renamed.mkv");
        string cutShort = Kept("Cut short.mkv");
        File.WriteAllText(Path.Combine(_root, "Cut short.mkv.medic-tmp.mkv"), "x");

        Assert.False(TrackCleaner.IsOrphan(_root, inPlace));
        Assert.True(TrackCleaner.IsOrphan(_root, renamed));
        Assert.False(TrackCleaner.IsOrphan(_root, cutShort));
    }
}
