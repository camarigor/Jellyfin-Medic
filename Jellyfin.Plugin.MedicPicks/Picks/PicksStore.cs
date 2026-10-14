using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>
/// Stores picks as one JSON file per user under the server's data folder
/// (not the plugin folder, so picks survive plugin upgrades).
/// Also holds the TMDb and Seerr keys in their own file so they never travel with the plugin's normal settings.
/// </summary>
public class PicksStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _root;
    private readonly ILogger<PicksStore> _logger;
    private readonly object _lock = new();
    private volatile bool _checkedPrivate;

    public PicksStore(IApplicationPaths applicationPaths, ILogger<PicksStore> logger)
    {
        _root = Path.Combine(applicationPaths.DataPath, "medic-picks");
        _logger = logger;
    }

    private string UsersDir => Path.Combine(_root, "users");

    private string SecretsPath => Path.Combine(_root, "secrets.json");

    public UserPicks? Load(Guid userId)
    {
        var path = Path.Combine(UsersDir, userId.ToString("N") + ".json");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lock (_lock)
            {
                return JsonSerializer.Deserialize<UserPicks>(File.ReadAllText(path), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Picks: could not read picks for user {UserId}", userId);
            return null;
        }
    }

    public void Save(Guid userId, UserPicks picks)
    {
        Directory.CreateDirectory(UsersDir);
        var path = Path.Combine(UsersDir, userId.ToString("N") + ".json");
        WriteAtomic(path, JsonSerializer.Serialize(picks, JsonOptions));
    }

    // Writes to a temporary file first, so a crash mid-write can't leave a half-written file behind.
    // The keys file holds the TMDb and Seerr keys, so only the account Jellyfin runs as can read it.
    private void WriteAtomic(string path, string json, bool secret = false)
    {
        var tmp = path + ".tmp";
        lock (_lock)
        {
            if (secret && !OperatingSystem.IsWindows())
            {
                File.Delete(tmp);
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                };
                using (var writer = new StreamWriter(tmp, options))
                {
                    writer.Write(json);
                }
            }
            else
            {
                File.WriteAllText(tmp, json);
            }

            File.Move(tmp, path, true);
        }
    }

    // Keys files saved by earlier versions could be read by any account on the server. Tighten them when read.
    private void KeepPrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            if (File.GetUnixFileMode(path) != Private)
            {
                File.SetUnixFileMode(path, Private);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Picks: could not make the keys file private");
        }
    }

    public int CountUsersWithPicks()
    {
        return Directory.Exists(UsersDir) ? Directory.GetFiles(UsersDir, "*.json").Length : 0;
    }

    public string? GetTmdbKey() => ReadSecrets().TmdbKey;

    public void SetTmdbKey(string? key) => UpdateSecrets(s => s.TmdbKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim());

    public string? GetSeerrKey() => ReadSecrets().SeerrKey;

    public void SetSeerrKey(string? key) => UpdateSecrets(s => s.SeerrKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim());

    // Read, change and write as one step, so saving both keys at once can't lose one of them.
    private void UpdateSecrets(Action<Secrets> change)
    {
        lock (_lock)
        {
            Secrets secrets;
            try
            {
                secrets = ParseSecrets() ?? new Secrets();
            }
            catch (JsonException ex)
            {
                // Don't overwrite a damaged keys file with only the new key: keep it aside for the admin.
                var damaged = SecretsPath + ".bad";
                File.Move(SecretsPath, damaged, true);
                _logger.LogWarning(ex, "Medic Picks: the keys file couldn't be read, so it was kept as {Path} and a new one started", damaged);
                secrets = new Secrets();
            }

            change(secrets);
            WriteSecrets(secrets);
        }
    }

    private Secrets ReadSecrets()
    {
        try
        {
            return ParseSecrets() ?? new Secrets();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Picks: could not read the keys file");
            return new Secrets();
        }
    }

    // The keys file's contents, or null when there isn't one yet. Throws when it can't be read.
    private Secrets? ParseSecrets()
    {
        if (!File.Exists(SecretsPath))
        {
            return null;
        }

        if (!_checkedPrivate)
        {
            KeepPrivate(SecretsPath); // once: files written since are created private
            _checkedPrivate = true;
        }

        var secrets = JsonSerializer.Deserialize<Secrets>(File.ReadAllText(SecretsPath), JsonOptions) ?? new Secrets();
        secrets.TmdbKey = string.IsNullOrWhiteSpace(secrets.TmdbKey) ? null : secrets.TmdbKey.Trim();
        secrets.SeerrKey = string.IsNullOrWhiteSpace(secrets.SeerrKey) ? null : secrets.SeerrKey.Trim();
        return secrets;
    }

    private void WriteSecrets(Secrets secrets)
    {
        Directory.CreateDirectory(_root);
        WriteAtomic(SecretsPath, JsonSerializer.Serialize(secrets, JsonOptions), secret: true);
    }

    // ---------- Each person's own choices on the My picks page ----------

    private string PrefsDir => Path.Combine(_root, "preferences");

    public UserPreferences LoadPreferences(Guid userId)
    {
        try
        {
            var path = Path.Combine(PrefsDir, userId.ToString("N") + ".json");
            if (File.Exists(path))
            {
                return (JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path), JsonOptions) ?? new UserPreferences()).Normalise();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Picks: could not read preferences for user {UserId}", userId);
        }

        return new UserPreferences();
    }

    public void SavePreferences(Guid userId, UserPreferences prefs)
    {
        Directory.CreateDirectory(PrefsDir);
        WriteAtomic(Path.Combine(PrefsDir, userId.ToString("N") + ".json"), JsonSerializer.Serialize(prefs, JsonOptions));
    }

    /// <summary>
    /// Reads, changes and saves one person's choices under the store's lock, so two changes at the same
    /// time (a Hide while a rebuild forgets what was seen, say) can't undo each other.
    /// </summary>
    public UserPreferences UpdatePreferences(Guid userId, Func<UserPreferences, UserPreferences> change)
    {
        lock (_lock)
        {
            var updated = change(LoadPreferences(userId)).Normalise();
            SavePreferences(userId, updated);
            return updated;
        }
    }

    /// <summary>
    /// Saves picks without anything the person has hidden, as their choices stand at the moment of saving:
    /// a title hidden while these picks were being built (by the nightly run, say) would otherwise come back.
    /// </summary>
    public void SaveWithoutHidden(Guid userId, UserPicks picks)
    {
        lock (_lock)
        {
            var prefs = LoadPreferences(userId);
            picks.InLibrary.RemoveAll(p => prefs.HiddenItems.Contains(p.ItemId));
            picks.Linked.RemoveAll(p => prefs.HiddenItems.Contains(p.ItemId));
            picks.Discover.RemoveAll(p => prefs.HiddenTmdb.Contains(TmdbKey(p.MediaType, p.TmdbId)));
            Save(userId, picks);
        }
    }

    /// <summary>Takes anything the person has hidden out of their saved picks.</summary>
    public void RemoveHidden(Guid userId)
    {
        lock (_lock)
        {
            if (Load(userId) is { } picks)
            {
                SaveWithoutHidden(userId, picks);
            }
        }
    }

    /// <summary>How a Discover title appears in the hidden and seen lists: "movie:123" or "tv:123".</summary>
    public static string TmdbKey(string? mediaType, int tmdbId) =>
        (mediaType == "tv" ? "tv:" : "movie:") + tmdbId.ToString(CultureInfo.InvariantCulture);

    private sealed class Secrets
    {
        public string? TmdbKey { get; set; }

        public string? SeerrKey { get; set; }
    }
}
