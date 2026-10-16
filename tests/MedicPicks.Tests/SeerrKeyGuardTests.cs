using Jellyfin.Plugin.MedicPicks;
using Jellyfin.Plugin.MedicPicks.Configuration;
using Jellyfin.Plugin.MedicPicks.Picks;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicPicks.Tests;

public sealed class SeerrKeyGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-picks-tests-").FullName;
    private readonly PicksStore _store;
    private readonly Plugin _plugin;
    private readonly SeerrKeyGuard _guard;

    public SeerrKeyGuardTests()
    {
        var paths = new TestPaths(_root);
        _store = new PicksStore(paths, NullLogger<PicksStore>.Instance);
        _plugin = new Plugin(paths, new DefaultSettingsSerializer());
        _guard = new SeerrKeyGuard(_store, NullLogger<SeerrKeyGuard>.Instance);
        _guard.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(null, "http://seerr:5055")]
    [InlineData("", "http://seerr:5055")]
    [InlineData("http://seerr:5055", "http://seerr:5055/")]
    [InlineData("http://seerr:5055", "http://SEERR:5055/seerr")]
    [InlineData("http://seerr", "http://seerr:80")]
    [InlineData(" http://seerr:5055 ", "http://seerr:5055")]
    public void A_first_address_or_the_same_server_is_not_a_change(string? before, string after) =>
        Assert.False(SeerrKeyGuard.HostChanged(before, after));

    [Theory]
    [InlineData("http://seerr:5055", "http://elsewhere:5055")]
    [InlineData("http://seerr:5055", "http://seerr:5056")]
    [InlineData("http://seerr:5055", "https://seerr:5055")]
    // Removing the address counts too, or the next address saved would be taken for a first one.
    [InlineData("http://seerr:5055", "")]
    [InlineData("http://seerr:5055", null)]
    public void Another_scheme_host_or_port_or_no_address_is_a_change(string before, string? after) =>
        Assert.True(SeerrKeyGuard.HostChanged(before, after));

    [Fact]
    public void Saving_an_address_on_another_server_removes_the_saved_key()
    {
        Save("http://seerr:5055");
        _store.SetSeerrKey("seerr-key");

        Save("http://elsewhere:5055");

        Assert.Null(_store.GetSeerrKey());
    }

    [Fact]
    public void A_first_address_and_a_new_path_keep_the_saved_key()
    {
        _store.SetSeerrKey("seerr-key");

        Save("http://seerr:5055");
        Save("http://seerr:5055/seerr/");

        Assert.Equal("seerr-key", _store.GetSeerrKey());
    }

    [Fact]
    public void The_key_is_gone_before_the_new_address_takes_effect()
    {
        // Otherwise a request made in between could pair the new address with the old server's key.
        Save("http://seerr:5055");
        _store.SetSeerrKey("seerr-key");
        (string Address, string? Key)? seen = null;
        _plugin.SeerrServerChanging += (_, _) => seen = (_plugin.Configuration.JellyseerrUrl, _store.GetSeerrKey());

        Save("http://elsewhere:5055");

        Assert.Equal(("http://seerr:5055", (string?)null), seen);
    }

    public void Dispose()
    {
        _guard.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        Directory.Delete(_root, recursive: true);
    }

    private void Save(string address) => _plugin.UpdateConfiguration(new PluginConfiguration { JellyseerrUrl = address });
}
