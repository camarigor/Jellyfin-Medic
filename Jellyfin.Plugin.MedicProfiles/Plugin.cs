using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Jellyfin.Plugin.MedicProfiles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MedicProfiles;

/// <summary>
/// Medic Profiles: the Sonarr and Radarr side of the server. See what's downloading, sort out
/// stuck or wrong downloads, and get quality profile advice from what your server actually plays.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin GUID. Must match the guid in scripts/publish_dist.sh, which writes it to the manifest.</summary>
    public const string PluginGuid = "1eb65368-7b2e-4a55-8f2b-b56134f3fe68";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Raised when saved settings are about to point Sonarr, Radarr or qBittorrent at another server, before they take effect.
    /// </summary>
    public event EventHandler<ServersChangingEventArgs>? ServersChanging;

    public static Plugin? Instance { get; private set; }

    public override string Name => "Medic Profiles";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Sonarr and Radarr from your Jellyfin dashboard: what's downloading, fix or block stuck and wrong downloads, "
        + "import by hand, and quality profile advice from what your server actually plays.";

    // Jellyfin's own plugin settings endpoint lands here, not in Medic Profiles' Save. Raised before the new address is
    // in use, so no request can pair it with the old server's key or password.
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration next)
        {
            var moved = new ServersChangingEventArgs(
                ArrKeyGuard.HostChanged(Configuration.SonarrUrl, next.SonarrUrl),
                ArrKeyGuard.HostChanged(Configuration.RadarrUrl, next.RadarrUrl),
                ArrKeyGuard.HostChanged(Configuration.QbitUrl, next.QbitUrl));
            if (moved.Any)
            {
                ServersChanging?.Invoke(this, moved);
            }
        }

        base.UpdateConfiguration(configuration);
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "MedicProfiles",
                DisplayName = "Medic Profiles",
                EmbeddedResourcePath = GetType().Namespace + ".Web.profiles.html",

                // Adds "Medic Profiles" to the Plugins section of the dashboard sidebar.
                EnableInMainMenu = true,
                MenuIcon = "tune"
            }
        };
    }
}
