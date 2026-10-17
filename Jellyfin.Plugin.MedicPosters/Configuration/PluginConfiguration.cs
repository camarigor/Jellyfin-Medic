using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MedicPosters.Configuration;

/// <summary>Medic Posters' settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Film libraries to make posters for. Empty: every film library.</summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Poster width in pixels; the height is 1.5 times this. Jellyfin sends GIF posters at full size, so smaller loads faster.</summary>
    public int Width { get; set; } = 240;

    /// <summary>How many moments of the film each poster cuts between.</summary>
    public int Moments { get; set; } = 3;

    /// <summary>How long each moment plays, in tenths of a second.</summary>
    public int MomentTenths { get; set; } = 10;

    /// <summary>Frames per second. Fewer frames make a smaller file.</summary>
    public int FramesPerSecond { get; set; } = 8;

    /// <summary>Colours in the GIF's palette (16 to 256). Fewer colours make a smaller file.</summary>
    public int Colours { get; set; } = 96;

    /// <summary>The film's title on the poster.</summary>
    public bool ShowTitle { get; set; } = true;

    /// <summary>The rounded white frame.</summary>
    public bool ShowFrame { get; set; } = true;
}
