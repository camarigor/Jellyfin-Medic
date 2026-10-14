using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

/// <summary>What qBittorrent's "Excluded file names" list holds, as far as Medic Profiles is concerned.</summary>
public sealed class FileFilterState
{
    public bool Configured { get; set; }

    public bool Reachable { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool ListOn { get; set; }

    // Which of Medic Profiles' patterns are in the list.
    public bool BlocksPrograms { get; set; }

    public bool BlocksArchives { get; set; }

    public List<string> Patterns { get; set; } = new();
}

/// <summary>
/// Stops qBittorrent downloading program files (.exe and friends) inside films and series, using its
/// "Excluded file names" list (qBittorrent 4.6 and later). The files are skipped as the torrent starts,
/// so they never land on disk. Sonarr and Radarr can't do this: they only see release names, not the
/// files inside. Archives (.rar, .zip, .7z) are optional, because many genuine releases come packed and
/// Sonarr and Radarr unpack them.
/// </summary>
public class QbitFileFilter
{
    public static readonly string[] Programs =
    {
        "*.exe", "*.msi", "*.bat", "*.cmd", "*.com", "*.scr", "*.pif", "*.lnk", "*.vbs", "*.vbe", "*.ps1", "*.jar", "*.apk", "*.dll"
    };

    public static readonly string[] Archives = { "*.rar", "*.zip", "*.7z" };

    private readonly ArrStore _store;
    private readonly ILogger<QbitFileFilter> _logger;

    // qBittorrent needs its own client to keep the sign-in cookie, so the shared HttpClient factory isn't used.
    public QbitFileFilter(ArrStore store, ILogger<QbitFileFilter> logger)
    {
        _store = store;
        _logger = logger;
    }

    private static string? BaseUrl()
    {
        string? url = Plugin.Instance?.Configuration.QbitUrl;
        return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
    }

    public async Task<FileFilterState> StateAsync(CancellationToken ct)
    {
        var state = new FileFilterState { Configured = BaseUrl() is not null };
        if (!state.Configured)
        {
            state.Message = "Add qBittorrent's address to use this.";
            return state;
        }

        var (client, error) = await SignInAsync(ct).ConfigureAwait(false);
        using (client)
        {
            if (client is null)
            {
                state.Message = error;
                return state;
            }

            var prefs = await GetPrefsAsync(client, ct).ConfigureAwait(false);
            if (prefs is null)
            {
                state.Message = "qBittorrent answered, but didn't return its settings.";
                return state;
            }

            state.Reachable = true;
            Fill(state, prefs.Value);
            state.Message = !prefs.Value.TryGetProperty("excluded_file_names", out _)
                ? "This qBittorrent is too old to exclude file names. Update to 4.6 or later."
                : state.ListOn && state.BlocksPrograms ? "qBittorrent skips program files in new downloads." : "Program files aren't blocked yet.";
            return state;
        }
    }

    /// <summary>
    /// Adds (or, with block false, removes) Medic Profiles' patterns. Only patterns it added itself are ever
    /// removed, so entries the owner had (even the same ones) stay, and unblocking switches the list back
    /// on or off as it was before.
    /// </summary>
    public async Task<(bool Ok, string Message)> ApplyAsync(bool block, bool archives, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        var (client, error) = await SignInAsync(ct).ConfigureAwait(false);
        using (client)
        {
            if (client is null)
            {
                return (false, error);
            }

            var prefs = await GetPrefsAsync(client, ct).ConfigureAwait(false);
            if (prefs is null || !prefs.Value.TryGetProperty("excluded_file_names", out var current))
            {
                return (false, "This qBittorrent can't exclude file names. Update it to 4.6 or later.");
            }

            var list = Split(current.GetString());
            bool wasOn = prefs.Value.TryGetProperty("excluded_file_names_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
            var added = new HashSet<string>(config.QbitPatternsAdded ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (config.QbitListWasOn is null && added.Count == 0)
            {
                // Medic Profiles 1.1.0 and 1.1.1 didn't record what they added: their full set counts as theirs.
                var present = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                if (Programs.All(present.Contains))
                {
                    added.UnionWith(Programs);
                    if (Archives.All(present.Contains))
                    {
                        added.UnionWith(Archives);
                    }
                }
            }

            var wanted = new HashSet<string>(block ? (archives ? Programs.Concat(Archives) : Programs) : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            list = list.Where(p => !added.Contains(p) || wanted.Contains(p)).ToList();
            added.IntersectWith(wanted);
            foreach (var pattern in wanted.Where(p => !list.Contains(p, StringComparer.OrdinalIgnoreCase)))
            {
                list.Add(pattern);
                added.Add(pattern);
            }

            bool listWasOn = config.QbitListWasOn ?? wasOn;
            bool on = block || (listWasOn && list.Count > 0);
            string json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["excluded_file_names_enabled"] = on,
                ["excluded_file_names"] = string.Join('\n', list)
            });
            using var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("json", json) });
            using var response = await client.PostAsync(BaseUrl() + "/api/v2/app/setPreferences", form, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"qBittorrent refused the change (HTTP {(int)response.StatusCode}).");
            }

            config.QbitPatternsAdded = block ? added.ToArray() : Array.Empty<string>();
            config.QbitListWasOn = block ? listWasOn : null;
            Plugin.Instance.SaveConfiguration();

            return (true, block
                ? "Done. qBittorrent now skips " + (archives ? "program files and archives" : "program files") + " in every new download, not only films and series. Anything that was already in the list is kept."
                : "Done. Only the entries Medic Profiles added are gone, and the list is switched " + (on ? "on" : "off") + " as it was before.");
        }
    }

    private static void Fill(FileFilterState state, JsonElement prefs)
    {
        state.ListOn = prefs.TryGetProperty("excluded_file_names_enabled", out var on) && on.ValueKind == JsonValueKind.True;
        state.Patterns = prefs.TryGetProperty("excluded_file_names", out var list) ? Split(list.GetString()) : new List<string>();
        var set = new HashSet<string>(state.Patterns, StringComparer.OrdinalIgnoreCase);
        state.BlocksPrograms = Programs.All(set.Contains);
        state.BlocksArchives = Archives.All(set.Contains);
    }

    private static List<string> Split(string? text) =>
        (text ?? string.Empty).Split('\n', '\r').Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private async Task<JsonElement?> GetPrefsAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync(BaseUrl() + "/api/v2/app/preferences", ct).ConfigureAwait(false));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Signs in to qBittorrent's Web UI. The caller disposes the client.</summary>
    private async Task<(HttpClient? Client, string Error)> SignInAsync(CancellationToken ct)
    {
        string? baseUrl = BaseUrl();
        if (baseUrl is null)
        {
            return (null, "Add qBittorrent's address first.");
        }

        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", Plugin.Instance?.Configuration.QbitUser ?? string.Empty),
                new KeyValuePair<string, string>("password", _store.GetQbitPassword() ?? string.Empty)
            });
            using var response = await client.PostAsync(baseUrl + "/api/v2/auth/login", form, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // qBittorrent says "Ok." when signed in, "Fails." for a wrong password, and 403 after too many tries.
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                client.Dispose();
                return (null, "qBittorrent has blocked sign-ins for now after too many wrong passwords. Wait a while, then try again.");
            }

            if (!response.IsSuccessStatusCode || !body.Contains("Ok", StringComparison.OrdinalIgnoreCase))
            {
                client.Dispose();
                return (null, "qBittorrent didn't accept the user name and password.");
            }

            return (client, string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogInformation("Medic Profiles: couldn't reach qBittorrent at {Url}: {Message}", baseUrl, ex.Message);
            client.Dispose();
            return (null, "Couldn't reach qBittorrent at that address from the Jellyfin server.");
        }
    }
}
