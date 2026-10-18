using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Jellyfin.Plugin.MedicProfiles.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

/// <summary>
/// Jellyfin's own plugin settings endpoint saves the addresses without going through Medic Profiles' Save, so the
/// guard that keeps a key or password with the server it was entered for has to hold there too.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class ArrKeyGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;
    private readonly Plugin _plugin;
    private readonly ArrKeyGuard _guard;

    public ArrKeyGuardTests()
    {
        var paths = new TestPaths(_root);
        _store = new ArrStore(paths, NullLogger<ArrStore>.Instance);
        _plugin = new Plugin(paths, new DefaultSettingsSerializer());
        _plugin.Configuration.SonarrUrl = "http://sonarr:8989";
        _plugin.Configuration.RadarrUrl = "http://radarr:7878";
        _plugin.Configuration.QbitUrl = "http://qbit:8080";
        _store.SetKey(ArrApp.Sonarr, "sonarr-key");
        _store.SetKey(ArrApp.Radarr, "radarr-key");
        _store.SetQbitPassword("qbit-password");
        _guard = new ArrKeyGuard(_store, NullLogger<ArrKeyGuard>.Instance);
        _guard.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _guard.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Each_address_moved_to_another_server_loses_only_its_own_secret()
    {
        Save(sonarr: "http://elsewhere:8989", qbit: "http://elsewhere:8080");

        Assert.Null(_store.GetKey(ArrApp.Sonarr));
        Assert.Equal("radarr-key", _store.GetKey(ArrApp.Radarr));
        Assert.Null(_store.GetQbitPassword());
    }

    [Fact]
    public void A_removed_address_counts_as_a_change()
    {
        // Or the next address saved would be taken for a first one and get the old server's key.
        Save(radarr: string.Empty);

        Assert.Null(_store.GetKey(ArrApp.Radarr));
    }

    [Fact]
    public void The_same_server_written_another_way_keeps_every_secret()
    {
        Save(sonarr: "sonarr:8989", radarr: "http://RADARR:7878/radarr/", qbit: "http://qbit:8080/");

        Assert.Equal("sonarr-key", _store.GetKey(ArrApp.Sonarr));
        Assert.Equal("radarr-key", _store.GetKey(ArrApp.Radarr));
        Assert.Equal("qbit-password", _store.GetQbitPassword());
    }

    [Fact]
    public void The_key_is_gone_before_the_new_address_takes_effect()
    {
        (string Address, string? Key)? seen = null;
        _plugin.ConfigurationChanged += (_, _) => seen ??= (_plugin.Configuration.SonarrUrl, _store.GetKey(ArrApp.Sonarr));

        Save(sonarr: "http://elsewhere:8989");

        Assert.Equal(("http://elsewhere:8989", (string?)null), seen);
    }

    private void Save(string? sonarr = null, string? radarr = null, string? qbit = null) =>
        _plugin.UpdateConfiguration(new PluginConfiguration
        {
            SonarrUrl = sonarr ?? _plugin.Configuration.SonarrUrl,
            RadarrUrl = radarr ?? _plugin.Configuration.RadarrUrl,
            QbitUrl = qbit ?? _plugin.Configuration.QbitUrl
        });
}
