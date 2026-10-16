using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;

// Shared by the plugins' test projects, which each compile it in (see their .csproj).
namespace Medic.TestSupport;

/// <summary>A test of Unix file permissions; skipped on Windows, which keeps its own folder permissions.</summary>
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix file permissions only";
        }
    }
}

/// <summary>Jellyfin's folders, all under one temporary folder.</summary>
internal sealed class TestPaths(string root) : IApplicationPaths
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

/// <summary>Reads every settings file as the defaults and writes nothing, so a plugin made in a test needs no files.</summary>
internal sealed class DefaultSettingsSerializer : IXmlSerializer
{
    public object? DeserializeFromStream(Type type, Stream stream) => Activator.CreateInstance(type);

    public void SerializeToStream(object obj, Stream stream)
    {
    }

    public void SerializeToFile(object obj, string file)
    {
    }

    public object? DeserializeFromFile(Type type, string file) => Activator.CreateInstance(type);

    public object? DeserializeFromBytes(Type type, byte[] buffer) => Activator.CreateInstance(type);
}
