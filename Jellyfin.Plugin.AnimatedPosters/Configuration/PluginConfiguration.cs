using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AnimatedPosters.Configuration;

/// <summary>
/// Settings shown on the plugin's page in the dashboard.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets how long each image stays up, in seconds.</summary>
    public double IntervalSeconds { get; set; } = 7;

    /// <summary>Gets or sets the crossfade length, in seconds.</summary>
    public double FadeSeconds { get; set; } = 1.4;

    public int ItemsPerLibrary { get; set; } = 24;

    public int RefreshMinutes { get; set; } = 30;

    public bool KenBurns { get; set; } = true;

    public bool ShowLogos { get; set; } = true;

    /// <summary>Gets or sets what happens to the library's own artwork: slide, screen or none.</summary>
    public string OriginalArt { get; set; } = "slide";

    public int OriginalEvery { get; set; } = 4;

    public double ScreenOpacity { get; set; } = 0.55;

    /// <summary>Gets or sets library names, one per line, that stay static.</summary>
    public string SkipLibraries { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the library name under each card is hidden.</summary>
    public bool HideText { get; set; }

    /// <summary>Gets or sets a value indicating whether card borders, rounded corners and shadows are removed.</summary>
    public bool HideBorder { get; set; }
}
