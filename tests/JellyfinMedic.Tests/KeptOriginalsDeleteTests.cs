using System.Reflection;
using JellyfinMedic.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Tests;

/// <summary>"Delete the kept originals" on real files in a library folder, as the Tracks tab runs it.</summary>
public sealed class KeptOriginalsDeleteTests : IDisposable
{
    private static readonly LibraryFacts Films = new() { Id = Guid.NewGuid().ToString("N"), Name = "Films" };

    private readonly string _root = Directory.CreateTempSubdirectory("medic-tests-").FullName;
    private readonly string _originals;
    private readonly string _inPlace;
    private readonly string _orphan;
    private readonly string _cutShort;
    private readonly ILibraryManager _library;

    public KeptOriginalsDeleteTests()
    {
        _originals = Directory.CreateDirectory(Path.Combine(_root, ".medic-originals")).FullName;

        // Cleaned, with the cleaned file in its place.
        _inPlace = Kept("In place.mkv");
        File.WriteAllText(Path.Combine(_root, "In place.mkv"), "cleaned");

        // Renamed or replaced by Sonarr or Radarr since: no file of its name, and no swap of it going on.
        _orphan = Kept("Renamed.mkv");

        // Jellyfin stopped between moving the original away and moving the cleaned copy in.
        _cutShort = Kept("Cut short.mkv");
        File.WriteAllText(Path.Combine(_root, "Cut short.mkv.medic-tmp.mkv"), "cleaned");

        // Jellyfin still lists the cut-short film under its own name, which is how its folder is found.
        _library = FakeLibrary.Listing(new BaseItem[]
        {
            new Movie { Path = Path.Combine(_root, "In place.mkv") },
            new Movie { Path = Path.Combine(_root, "Cut short.mkv") }
        });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task The_default_delete_keeps_a_cut_short_swaps_original_so_it_can_be_put_back()
    {
        TrackCleaner.DeleteOriginals(_library, new[] { Films });

        Assert.True(File.Exists(_cutShort), "The original of a swap that was cut short was deleted.");
        await AssertPutBack();
    }

    [Fact]
    public async Task Deleting_orphans_too_keeps_a_cut_short_swaps_original_so_it_can_be_put_back()
    {
        TrackCleaner.DeleteOriginals(_library, new[] { Films }, orphans: true);

        Assert.True(File.Exists(_cutShort), "The original of a swap that was cut short was deleted as an orphan.");
        await AssertPutBack();
    }

    [Fact]
    public void The_default_delete_takes_only_originals_whose_cleaned_file_is_in_place()
    {
        var (deleted, freed, _) = TrackCleaner.DeleteOriginals(_library, new[] { Films });

        Assert.False(File.Exists(_inPlace));
        Assert.True(File.Exists(_orphan), "An orphan was deleted without being asked for.");
        Assert.Equal(1, deleted);
        Assert.Equal("original".Length, freed);
    }

    [Fact]
    public void Deleting_orphans_too_also_takes_originals_with_no_file_of_their_name()
    {
        var (deleted, _, _) = TrackCleaner.DeleteOriginals(_library, new[] { Films }, orphans: true);

        Assert.False(File.Exists(_inPlace));
        Assert.False(File.Exists(_orphan));
        Assert.Equal(2, deleted);
    }

    [Fact]
    public void The_orphans_counted_are_the_ones_deleting_orphans_too_adds()
    {
        var (files, _, orphans, _) = TrackCleaner.KeptOriginals(_library, new[] { Films });
        int byDefault = TrackCleaner.DeleteOriginals(_library, new[] { Films }).Deleted;
        int withOrphans = TrackCleaner.DeleteOriginals(_library, new[] { Films }, orphans: true).Deleted;

        Assert.Equal(3, files);
        Assert.Equal(1, orphans);
        Assert.Equal(orphans, withOrphans);
        Assert.Equal(1, byDefault);
    }

    [Fact]
    public void Originals_left_in_a_folder_the_library_no_longer_uses_are_counted_and_deleted_as_orphans()
    {
        // Sonarr renamed "Season 1" to "Season 01" and moved the episode; Jellyfin has no item in the old folder.
        string left = Directory.CreateDirectory(Path.Combine(_root, "Show", "Season 1", ".medic-originals")).FullName;
        string episode = Path.Combine(left, "Show S01E01.mkv");
        File.WriteAllText(episode, "original");
        var shows = new LibraryFacts { Id = Films.Id, Name = "Shows", Locations = { _root } };

        TrackCleaner.KeptOriginalsWalk.Clear();
        var (files, _, orphans, _) = TrackCleaner.KeptOriginals(_library, new[] { shows });
        int byDefault = TrackCleaner.DeleteOriginals(_library, new[] { shows }).Deleted;
        bool keptByDefault = File.Exists(episode);
        int withOrphans = TrackCleaner.DeleteOriginals(_library, new[] { shows }, orphans: true).Deleted;

        Assert.Equal((4, 2), (files, orphans));
        Assert.True(keptByDefault, "An orphan in a folder Jellyfin no longer lists was deleted without being asked for.");
        Assert.Equal((1, 2), (byDefault, withOrphans));
        Assert.False(File.Exists(episode));
        Assert.False(Directory.Exists(left));
    }

    [Fact]
    public void Deleting_an_original_saves_only_what_cleaning_took_off_the_file_in_its_place()
    {
        // Cleaning wrote the 7 byte copy and kept the 8 byte original, so the drive comes out 1 byte ahead, not 8.
        var (_, freed, saved) = TrackCleaner.DeleteOriginals(_library, new[] { Films });

        Assert.Equal("original".Length, freed);
        Assert.Equal("original".Length - "cleaned".Length, saved);
    }

    [Fact]
    public async Task Deleting_an_original_medic_recorded_saves_what_its_first_cleaning_took_off()
    {
        string film = Path.Combine(_root, "Recorded.mkv");
        File.WriteAllText(film, "a recorded original");
        await Swap(film, "cleaned");

        // Cleaned again in place since: that run already counted what it took off the first copy.
        File.WriteAllText(film, "again");
        var (_, _, saved) = TrackCleaner.DeleteOriginals(_library, new[] { Films });

        Assert.Equal(("a recorded original".Length - "cleaned".Length) + ("original".Length - "cleaned".Length), saved);
    }

    [Fact]
    public async Task Cleaning_twice_and_deleting_the_original_saves_what_the_drive_gained()
    {
        string film = Path.Combine(_root, "Twice.mkv");
        File.WriteAllText(film, "a twice cleaned original");
        await Swap(film, "cleaned once");
        await Swap(film, "twice");
        File.Delete(_inPlace);

        var (_, _, saved) = TrackCleaner.DeleteOriginals(_library, new[] { Films });

        Assert.Equal("a twice cleaned original".Length - "twice".Length, saved);
    }

    [Fact]
    public void Deleting_an_orphan_saves_its_whole_size()
    {
        // Its copy is gone already, replaced by Sonarr or Radarr, so nothing Medic wrote for it is left on the drive.
        var (_, freed, saved) = TrackCleaner.DeleteOriginals(_library, new[] { Films }, orphans: true);

        Assert.Equal(2 * "original".Length, freed);
        Assert.Equal("original".Length + ("original".Length - "cleaned".Length), saved);
    }

    /// <summary>Cleans the film the way keep mode does, with a copy of this text.</summary>
    private static async Task Swap(string film, string copy)
    {
        string temp = film + ".medic-tmp.mkv";
        File.WriteAllText(temp, copy);
        await TrackCleaner.SwapKeepingOriginal(film, temp, Path.GetFileNameWithoutExtension(film), new FileInfo(film).Length, CancellationToken.None);
    }

    /// <summary>"Tidy copies" (like the next run) puts the original back under its own name and drops the temp file.</summary>
    private async Task AssertPutBack()
    {
        // No old-style copies here, so FFprobe is never asked.
        await TrackCleaner.TidyEarlierCopies(_library, new[] { Films }, ffprobe: string.Empty);

        string film = Path.Combine(_root, "Cut short.mkv");
        Assert.True(File.Exists(film), "The cut-short film wasn't put back.");
        Assert.Equal("original", File.ReadAllText(film));
        Assert.False(File.Exists(_cutShort));
        Assert.False(File.Exists(film + ".medic-tmp.mkv"));
    }

    private string Kept(string name)
    {
        string path = Path.Combine(_originals, name);
        File.WriteAllText(path, "original");
        return path;
    }

    /// <summary>A library manager that only lists items, which is all finding the library folders asks of it.</summary>
    private class FakeLibrary : DispatchProxy
    {
        private IReadOnlyList<BaseItem> _items = Array.Empty<BaseItem>();

        public static ILibraryManager Listing(IReadOnlyList<BaseItem> items)
        {
            var library = Create<ILibraryManager, FakeLibrary>();
            ((FakeLibrary)(object)library)._items = items;
            return library;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(ILibraryManager.GetItemList) ? _items : throw new NotSupportedException(targetMethod?.Name);
    }
}
