using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MedicProfiles.Api;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

/// <summary>Which saved addresses now point at another server.</summary>
public sealed class ServersChangingEventArgs : EventArgs
{
    public ServersChangingEventArgs(bool sonarr, bool radarr, bool qbit)
    {
        Sonarr = sonarr;
        Radarr = radarr;
        Qbit = qbit;
    }

    public bool Sonarr { get; }

    public bool Radarr { get; }

    public bool Qbit { get; }

    public bool Any => Sonarr || Radarr || Qbit;
}

/// <summary>
/// Removes a saved key or the qBittorrent password when settings saved through Jellyfin's own plugin settings
/// endpoint point its address at another server. Medic Profiles' Save does the same itself; this covers every
/// other way in, so a changed address can't be used to collect a secret (Medic Picks does the same with Seerr's key).
/// </summary>
public sealed class ArrKeyGuard : IHostedService
{
    private readonly ArrStore _store;
    private readonly ILogger<ArrKeyGuard> _logger;
    private Plugin? _plugin;

    public ArrKeyGuard(ArrStore store, ILogger<ArrKeyGuard> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Whether the address moved to another scheme, host or port, read as Medic Profiles' Save would store it.
    /// A first address isn't a change; removing the address is, or the next one saved would be taken for a first.
    /// </summary>
    public static bool HostChanged(string? before, string? after) =>
        !string.IsNullOrWhiteSpace(before)
        && !string.Equals(ProfilesController.Authority(ProfilesController.CleanUrl(before)), ProfilesController.Authority(ProfilesController.CleanUrl(after)), StringComparison.OrdinalIgnoreCase);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _plugin = Plugin.Instance;
        if (_plugin is not null)
        {
            _plugin.ServersChanging += OnServersChanging;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_plugin is not null)
        {
            _plugin.ServersChanging -= OnServersChanging;
        }

        return Task.CompletedTask;
    }

    private void OnServersChanging(object? sender, ServersChangingEventArgs e)
    {
        if (e.Sonarr && _store.GetKey(ArrApp.Sonarr) is not null)
        {
            _store.SetKey(ArrApp.Sonarr, null);
            _logger.LogInformation("Medic Profiles: the Sonarr address now points at another server, so the saved API key was removed; enter it again for the new server");
        }

        if (e.Radarr && _store.GetKey(ArrApp.Radarr) is not null)
        {
            _store.SetKey(ArrApp.Radarr, null);
            _logger.LogInformation("Medic Profiles: the Radarr address now points at another server, so the saved API key was removed; enter it again for the new server");
        }

        if (e.Qbit && _store.GetQbitPassword() is not null)
        {
            _store.SetQbitPassword(null);
            _logger.LogInformation("Medic Profiles: the qBittorrent address now points at another server, so the saved password was removed; enter it again for the new server");
        }
    }
}
