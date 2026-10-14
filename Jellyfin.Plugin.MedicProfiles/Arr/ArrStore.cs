using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

/// <summary>One thing an admin did from the Downloads tab, kept so there's a record of it.</summary>
public class ActionRecord
{
    public DateTime Utc { get; set; }

    public string App { get; set; } = string.Empty;

    /// <summary>remove, block, import, unblock.</summary>
    public string Action { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public string? By { get; set; }
}

/// <summary>
/// Keeps the Sonarr and Radarr API keys in their own file under the server's data folder, readable
/// only through admin-only endpoints and never sent to a browser, plus a short history of actions.
/// </summary>
public class ArrStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private const int MaxHistory = 200;

    private readonly string _root;
    private readonly ILogger<ArrStore> _logger;
    private readonly object _lock = new();
    private volatile bool _checkedPrivate;

    public ArrStore(IApplicationPaths applicationPaths, ILogger<ArrStore> logger)
    {
        _root = Path.Combine(applicationPaths.DataPath, "medic-profiles");
        _logger = logger;
    }

    private string SecretsPath => Path.Combine(_root, "secrets.json");

    private string HistoryPath => Path.Combine(_root, "history.json");

    public string? GetKey(ArrApp app) => app == ArrApp.Sonarr ? ReadSecrets().SonarrKey : ReadSecrets().RadarrKey;

    public void SetKey(ArrApp app, string? key)
    {
        string? clean = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        UpdateSecrets(secrets =>
        {
            if (app == ArrApp.Sonarr)
            {
                secrets.SonarrKey = clean;
            }
            else
            {
                secrets.RadarrKey = clean;
            }
        });
    }

    public string? GetQbitPassword() => ReadSecrets().QbitPassword;

    public void SetQbitPassword(string? password) =>
        UpdateSecrets(secrets => secrets.QbitPassword = string.IsNullOrEmpty(password) ? null : password);

    // Read, change and write as one step. A keys file that can't be read is kept aside rather than replaced
    // by one holding only the new value, which would lose the other keys without a word.
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
                var damaged = SecretsPath + ".bad";
                File.Move(SecretsPath, damaged, true);
                _logger.LogWarning(ex, "Medic Profiles: the keys file couldn't be read, so it was kept as {Path} and a new one started", damaged);
                secrets = new Secrets();
            }

            change(secrets);
            Directory.CreateDirectory(_root);
            WriteAtomic(SecretsPath, JsonSerializer.Serialize(secrets, JsonOptions), secret: true);
        }
    }

    public List<ActionRecord> LoadHistory()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(HistoryPath))
                {
                    return JsonSerializer.Deserialize<List<ActionRecord>>(File.ReadAllText(HistoryPath), JsonOptions) ?? new List<ActionRecord>();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Profiles: could not read the action history");
        }

        return new List<ActionRecord>();
    }

    public void Record(ActionRecord record)
    {
        try
        {
            lock (_lock)
            {
                var history = LoadHistory();
                history.Insert(0, record);
                Directory.CreateDirectory(_root);
                WriteAtomic(HistoryPath, JsonSerializer.Serialize(history.Take(MaxHistory).ToList(), JsonOptions));
            }
        }
        catch (Exception ex)
        {
            // The history is a convenience; the action itself has already happened.
            _logger.LogWarning(ex, "Medic Profiles: could not save the action history");
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
            _logger.LogWarning(ex, "Medic Profiles: could not read the keys file");
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

        return JsonSerializer.Deserialize<Secrets>(File.ReadAllText(SecretsPath), JsonOptions) ?? new Secrets();
    }

    // Writes to a temporary file first, so a crash mid-write can't leave a half-written file behind.
    // The keys file holds API keys and a password, so only the account Jellyfin runs as can read it.
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

    // Keys files saved by 1.1.1 and earlier could be read by any account on the server. Tighten them when read.
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
            _logger.LogWarning(ex, "Medic Profiles: could not make the keys file private");
        }
    }

    private sealed class Secrets
    {
        public string? SonarrKey { get; set; }

        public string? RadarrKey { get; set; }

        public string? QbitPassword { get; set; }
    }
}
