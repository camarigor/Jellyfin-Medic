using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AnimatedPosters.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AnimatedPosters;

/// <summary>
/// Animated, randomized library cards for the Jellyfin web client.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin GUID. Must match the guid in manifest.json and the release workflow.</summary>
    public const string PluginGuid = "892f168c-c44e-4abf-b72f-6ebce7b90fdc";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Animated Posters";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "Turns the My Media library cards into rotating, animated artwork pulled from each library.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "AnimatedPosters",
                DisplayName = "Animated Posters",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",

                // Adds "Animated Posters" to the Plugins section of the dashboard sidebar.
                EnableInMainMenu = true,
                MenuIcon = "animation"
            }
        };
    }
}
