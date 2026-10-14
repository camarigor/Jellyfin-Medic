using Jellyfin.Plugin.MedicPicks.Picks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicPicks.Tests;

public sealed class PicksStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-picks-tests-").FullName;
    private readonly PicksStore _store;

    public PicksStoreTests()
    {
        _store = new PicksStore(new Paths(_root), NullLogger<PicksStore>.Instance);
    }

    [Fact]
    public void A_title_hidden_while_picks_were_being_built_is_not_saved_with_them()
    {
        var user = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var built = new UserPicks
        {
            InLibrary = { new LibraryPick { ItemId = hidden }, new LibraryPick { ItemId = kept } },
            Discover = { new DiscoverPick { TmdbId = 603, MediaType = "movie" }, new DiscoverPick { TmdbId = 1399, MediaType = "tv" } }
        };

        // Hidden after the rebuild read the choices, before it saves.
        _store.UpdatePreferences(user, p =>
        {
            p.HiddenItems.Add(hidden);
            p.HiddenTmdb.Add(PicksStore.TmdbKey("tv", 1399));
            return p;
        });
        _store.SaveWithoutHidden(user, built);

        var saved = _store.Load(user)!;
        Assert.Equal(new[] { kept }, saved.InLibrary.Select(p => p.ItemId));
        Assert.Equal(new[] { 603 }, saved.Discover.Select(p => p.TmdbId));
    }

    [Fact]
    public void Changes_to_the_choices_made_at_the_same_time_are_all_kept()
    {
        var user = Guid.NewGuid();
        var ids = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();

        Parallel.ForEach(ids, id => _store.UpdatePreferences(user, p =>
        {
            p.HiddenItems.Add(id);
            return p;
        }));

        Assert.Equal(ids.OrderBy(i => i), _store.LoadPreferences(user).HiddenItems.OrderBy(i => i));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Paths(string root) : IApplicationPaths
    {
        public string ProgramDataPath => root;

        public string WebPath => Path.Combine(root, "web");

        public string ProgramSystemPath => Path.Combine(root, "system");

        public string DataPath => Path.Combine(root, "data");

        public string ImageCachePath => Path.Combine(root, "cache", "images");

        public string PluginsPath => Path.Combine(root, "plugins");

        public string PluginConfigurationsPath => Path.Combine(root, "plugins", "configurations");

        public string LogDirectoryPath => Path.Combine(root, "log");

        public string ConfigurationDirectoryPath => Path.Combine(root, "config");

        public string SystemConfigurationFilePath => Path.Combine(root, "config", "system.xml");

        public string CachePath => Path.Combine(root, "cache");

        public string TempDirectory => Path.Combine(root, "temp");

        public string VirtualDataPath => Path.Combine(root, "data");

        public string TrickplayPath => Path.Combine(root, "data", "trickplay");

        public string BackupPath => Path.Combine(root, "backups");

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }
}
