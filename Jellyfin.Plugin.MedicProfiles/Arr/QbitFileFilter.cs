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

        var (prefs, error) = await WithSessionAsync(client => GetPrefsAsync(client, ct), ct).ConfigureAwait(false);
        if (prefs is null)
        {
            state.Message = error.Length > 0 ? error : "qBittorrent answered, but didn't return its settings.";
            return state;
        }

        state.Reachable = true;
        Fill(state, prefs.Value);
        state.Message = !prefs.Value.TryGetProperty("excluded_file_names", out _)
            ? "This qBittorrent is too old to exclude file names. Update to 4.6 or later."
            : state.ListOn && state.BlocksPrograms ? "qBittorrent skips program files in new downloads." : "Program files aren't blocked yet.";
        return state;
    }

    /// <summary>
    /// Adds (or, with block false, removes) Medic Profiles' patterns. Only patterns it added itself are ever
    /// removed, so entries the owner had (even the same ones) stay, and unblocking switches the list back
    /// on or off as it was before.
    /// </summary>
    public async Task<(bool Ok, string Message)> ApplyAsync(bool block, bool archives, CancellationToken ct)
    {
        var (result, error) = await WithSessionAsync(client => ApplyWithAsync(client, block, archives, ct), ct).ConfigureAwait(false);
        return result ?? (false, error);
    }

    private async Task<(bool Ok, string Message)?> ApplyWithAsync(HttpClient client, bool block, bool archives, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        var prefs = await GetPrefsAsync(client, ct).ConfigureAwait(false);
        if (prefs is null)
        {
            return (false, "qBittorrent answered, but didn't return its settings.");
        }

        if (!prefs.Value.TryGetProperty("excluded_file_names", out var current))
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
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new SessionExpiredException();
        }

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

    /// <summary>The Web UI's preferences, or null when qBittorrent answers with something else.</summary>
    private static async Task<JsonElement?> GetPrefsAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync(BaseUrl() + "/api/v2/app/preferences", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new SessionExpiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- Signing in ----------

    // One signed-in session is kept and reused, so opening the page doesn't sign in each time: every failed
    // sign-in counts towards qBittorrent's ban on this server's address. A refused password isn't tried again
    // for a while, unless the address, user name or password change.
    private static readonly TimeSpan SessionLife = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan RefusedPause = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HttpClient? _session;
    private string _sessionBase = string.Empty;
    private int _signedInAs; // the address, user name and password the session, or the refusal, is for
    private DateTime _sessionUntil;
    private string? _refusal;
    private DateTime _refusedUntil;

    /// <summary>qBittorrent answered 403 to a call after signing in: the session has ended on its side.</summary>
    private sealed class SessionExpiredException : Exception
    {
    }

    /// <summary>
    /// Runs work with a signed-in session; if qBittorrent has dropped the session, signs in once more and runs it again.
    /// Returns the work's result, or default and the reason it couldn't run.
    /// </summary>
    private async Task<(T? Result, string Error)> WithSessionAsync<T>(Func<HttpClient, Task<T>> work, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var (client, error) = await SessionAsync(ct).ConfigureAwait(false);
            if (client is null)
            {
                return (default, error);
            }

            try
            {
                return (await work(client).ConfigureAwait(false), string.Empty);
            }
            catch (SessionExpiredException) when (attempt == 0)
            {
                await ForgetSessionAsync().ConfigureAwait(false);
            }
            catch (SessionExpiredException)
            {
                await ForgetSessionAsync().ConfigureAwait(false);
                return (default, "qBittorrent keeps ending Medic Profiles' session. Check the user name and password.");
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _logger.LogInformation("Medic Profiles: couldn't reach qBittorrent: {Message}", ex.Message);
                await ForgetSessionAsync().ConfigureAwait(false);
                return (default, "Couldn't reach qBittorrent at that address from the Jellyfin server.");
            }
        }
    }

    private async Task<(HttpClient? Client, string Error)> SessionAsync(CancellationToken ct)
    {
        string? baseUrl = BaseUrl();
        if (baseUrl is null)
        {
            return (null, "Add qBittorrent's address first.");
        }

        string user = Plugin.Instance?.Configuration.QbitUser ?? string.Empty;
        string password = _store.GetQbitPassword() ?? string.Empty;
        int who = HashCode.Combine(baseUrl, user, password);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            if (who == _signedInAs && _session is not null && now < _sessionUntil)
            {
                return (_session, string.Empty);
            }

            if (who == _signedInAs && _refusal is not null && now < _refusedUntil)
            {
                return (null, _refusal);
            }

            await SignOutLockedAsync().ConfigureAwait(false);
            _signedInAs = who;
            _refusal = null;
            var (client, error, refused) = await SignInAsync(baseUrl, user, password, ct).ConfigureAwait(false);
            if (client is not null)
            {
                _session = client;
                _sessionBase = baseUrl;
                _sessionUntil = now + SessionLife;
            }
            else if (refused)
            {
                _refusal = error;
                _refusedUntil = now + RefusedPause;
            }

            return (client, error);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ForgetSessionAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SignOutLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Signs the kept session out of qBittorrent (best effort) and drops it. Call with _gate held.
    private async Task SignOutLockedAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await _session.PostAsync(_sessionBase + "/api/v2/auth/logout", null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // It expires on qBittorrent's side anyway.
        }

        _session.Dispose();
        _session = null;
    }

    /// <summary>
    /// Signs in to qBittorrent's Web UI. Refused is true when qBittorrent turned the user name and password
    /// down (or is blocking sign-ins), as opposed to not being reachable.
    /// </summary>
    private async Task<(HttpClient? Client, string Error, bool Refused)> SignInAsync(string baseUrl, string user, string password, CancellationToken ct)
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", user),
                new KeyValuePair<string, string>("password", password)
            });
            using var response = await client.PostAsync(baseUrl + "/api/v2/auth/login", form, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // qBittorrent says "Ok." and sets its session cookie when signed in, "Fails." for a wrong password,
            // and 403 after too many tries.
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                client.Dispose();
                return (null, "qBittorrent has blocked sign-ins for now after too many wrong passwords. Wait a while, then try again.", true);
            }

            if (!response.IsSuccessStatusCode || !body.Contains("Ok", StringComparison.OrdinalIgnoreCase) || cookies.GetCookies(new Uri(baseUrl)).Count == 0)
            {
                client.Dispose();
                return (null, "qBittorrent didn't accept the user name and password. Medic Profiles won't try them again for 5 minutes, so qBittorrent doesn't block this server.", true);
            }

            return (client, string.Empty, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogInformation("Medic Profiles: couldn't reach qBittorrent at {Url}: {Message}", baseUrl, ex.Message);
            client.Dispose();
            return (null, "Couldn't reach qBittorrent at that address from the Jellyfin server.", false);
        }
    }
}
