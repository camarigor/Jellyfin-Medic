using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Medic.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

public enum ArrApp
{
    Sonarr,
    Radarr
}

/// <summary>The answer from Sonarr or Radarr: the JSON when it worked, or a message worded for the admin.</summary>
public sealed class ArrResult : IDisposable
{
    public bool Ok { get; init; }

    public int Status { get; init; }

    public JsonDocument? Json { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>Where the request ended up, when a redirect took it somewhere other than the address asked.</summary>
    public Uri? RedirectedTo { get; init; }

    public void Dispose() => Json?.Dispose();
}

/// <summary>
/// Talks to Sonarr and Radarr's v3 API from the Jellyfin server. The keys stay on the server.
/// </summary>
public class ArrClient
{
    // A long queue page can take Sonarr or Radarr a while; past this, they count as out of reach.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ArrStore _store;
    private readonly ILogger<ArrClient> _logger;

    public ArrClient(IHttpClientFactory httpClientFactory, ArrStore store, ILogger<ArrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _logger = logger;
    }

    public static string Name(ArrApp app) => app == ArrApp.Sonarr ? "Sonarr" : "Radarr";

    public static string? BaseUrl(ArrApp app)
    {
        var config = Plugin.Instance?.Configuration;
        string? url = app == ArrApp.Sonarr ? config?.SonarrUrl : config?.RadarrUrl;
        return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
    }

    /// <summary>Set up: an address and a key.</summary>
    public bool IsConfigured(ArrApp app) => BaseUrl(app) is not null && _store.GetKey(app) is not null;

    /// <summary>
    /// The settings' Test: passes only when the app itself answered at the saved address. A proxy's sign-in page
    /// answers 200 too, and a redirect may have carried the call to another app.
    /// </summary>
    public async Task<(bool Ok, string Message)> StatusAsync(ArrApp app, CancellationToken ct)
    {
        using var result = await GetAsync(app, "system/status", ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, result.Message);
        }

        if (result.RedirectedTo is { } to)
        {
            return (false, $"{BaseUrl(app)} sent the test on to {to}, so it may not have reached {Name(app)}. Enter the address {Name(app)} answers at directly, with its URL base (/{Name(app).ToLowerInvariant()}, say).");
        }

        // A status with a version but no appName still counts, so a build that leaves the name out isn't refused.
        var root = result.Json?.RootElement ?? default;
        string? appName = Json.Text(root, "appName");
        string? version = Json.Text(root, "version");
        bool isApp = appName is null ? version is not null : string.Equals(appName, Name(app), StringComparison.OrdinalIgnoreCase);
        if (!isApp)
        {
            return (false, (appName is null ? string.Empty : $"{appName} answered, not {Name(app)}. ") + NotTheApp(app, BaseUrl(app)));
        }

        return (true, $"{Name(app)} answered{(version is null ? string.Empty : " (version " + version + ")")} and accepted the key.");
    }

    public Task<ArrResult> GetAsync(ArrApp app, string pathAndQuery, CancellationToken ct) =>
        SendAsync(app, HttpMethod.Get, pathAndQuery, null, ct);

    public Task<ArrResult> PostRawAsync(ArrApp app, string path, string json, CancellationToken ct) =>
        SendAsync(app, HttpMethod.Post, path, json, ct);

    public Task<ArrResult> DeleteAsync(ArrApp app, string pathAndQuery, object? body, CancellationToken ct) =>
        SendAsync(app, HttpMethod.Delete, pathAndQuery, body is null ? null : JsonSerializer.Serialize(body, ArrStore.JsonOptions), ct);

    private async Task<ArrResult> SendAsync(ArrApp app, HttpMethod method, string pathAndQuery, string? json, CancellationToken ct)
    {
        string? baseUrl = BaseUrl(app);
        string? key = _store.GetKey(app);
        if (baseUrl is null || key is null)
        {
            return new ArrResult { Message = $"{Name(app)} isn't set up yet. Add its address and API key on the Settings tab." };
        }

        try
        {
            using var request = new HttpRequestMessage(method, baseUrl + "/api/v3/" + pathAndQuery.TrimStart('/'));
            request.Headers.Add("X-Api-Key", key);
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            // Following a redirect changes this request in place, so keep what was asked.
            Uri asked = request.RequestUri!;
            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await ServiceHttp.SendAsync(client, request, CallTimeout, Name(app), ct).ConfigureAwait(false);
            Uri? reached = response.RequestMessage?.RequestUri;
            Uri? redirectedTo = reached is not null && reached != asked ? reached : null;
            if (response.RequestMessage is { } sent && sent.Method != method)
            {
                // A 301 or 302 turns a POST or DELETE into a GET, which reads the list instead of changing it.
                return new ArrResult
                {
                    RedirectedTo = redirectedTo,
                    Message = $"{baseUrl} sent the request on to {reached}, which turned it into a different request, so {Name(app)} didn't act on it. Enter the address {Name(app)} answers at directly, with its URL base (/{Name(app).ToLowerInvariant()}, say), on the Settings tab."
                };
            }

            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new ArrResult { Ok = true, Status = status, RedirectedTo = redirectedTo, Json = text.Length == 0 ? null : TryParse(text) };
            }

            string message = status switch
            {
                401 => $"{Name(app)} didn't accept the API key. Copy it again from {Name(app)} → Settings → General.",
                // Sonarr and Radarr word their own 404s as JSON; an empty or HTML one came from somewhere else.
                404 when IsJson(text) => $"{Name(app)} says that wasn't found. It may have been removed already.",
                404 => NotTheApp(app, baseUrl),
                _ => ServiceHttp.ErrorMessage(text) is { } err ? $"{Name(app)} says: {err}" : $"{Name(app)} answered with {status}."
            };
            return new ArrResult { Status = status, RedirectedTo = redirectedTo, Message = message };
        }
        catch (HttpRequestException ex) when (ex.InnerException is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // ServiceHttp's time limit, which would otherwise read like an address that can't be reached.
            _logger.LogWarning(ex, "Medic Profiles: {App} didn't answer in time", Name(app));
            return new ArrResult { Message = string.Format(CultureInfo.InvariantCulture, "{0} at {1} didn't answer within {2:0} seconds.", Name(app), baseUrl, CallTimeout.TotalSeconds) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Medic Profiles: couldn't reach {App}", Name(app));
            return new ArrResult { Message = $"Couldn't reach {Name(app)} at {baseUrl} from the Jellyfin server." };
        }
    }

    private static string NotTheApp(ArrApp app, string? baseUrl) =>
        $"{Name(app)} wasn't found at {baseUrl}. Check its address and URL base (/{Name(app).ToLowerInvariant()}, say) on the Settings tab.";

    private static bool IsJson(string text)
    {
        using var doc = TryParse(text);
        return doc is not null;
    }

    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
