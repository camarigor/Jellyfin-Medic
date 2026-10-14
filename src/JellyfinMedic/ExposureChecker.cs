using System.Net;

namespace JellyfinMedic.Services;

public class ExposureDefaults
{
    public string? Domain { get; set; }

    public string? PublicIp { get; set; }

    public int Port { get; set; } = 443;

    public string Protocol { get; set; } = "https";
}

public class ExposureResult
{
    public bool Checked { get; set; }

    public bool Reachable { get; set; }

    public bool RespondedAsJellyfin { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string Protocol { get; set; } = "https";

    /// <summary>
    /// True when the answer came from connecting from this server itself, not from a check out on the internet.
    /// That can differ: a router may not reach its own public address from inside, or a domain may point home.
    /// </summary>
    public bool FromThisServer { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// Checks whether Jellyfin answers at a given address, port and protocol. With a domain (or plain http) it
/// connects from this server and sees whether the address responds as a Jellyfin server; for an IP over
/// https, or when that fails, it asks a public port-checker. Only the address and port are sent out; no
/// server data leaves your network. Nothing goes out until the admin asks: the check runs on "Check now",
/// and the public IP is looked up (at most once an hour) only for "Use my IP".
/// </summary>
public static class ExposureChecker
{
    // Redirects aren't followed: an answer of any kind already means something is listening there.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly TimeSpan PublicIpCacheTime = TimeSpan.FromHours(1);
    private static readonly TimeSpan NoPublicIpCacheTime = TimeSpan.FromMinutes(5);
    private static PublicIpLookup? _publicIp;

    private static readonly string[] IpServices =
    {
        "https://api.ipify.org",
        "https://ipv4.icanhazip.com",
        "https://checkip.amazonaws.com"
    };

    public static async Task<ExposureDefaults> DefaultsAsync(object? networkConfig, bool lookUpPublicIp, CancellationToken ct)
    {
        var defaults = new ExposureDefaults
        {
            // Only when asked for: finding it means asking a service out on the internet.
            PublicIp = lookUpPublicIp ? await PublicIpAsync(ct).ConfigureAwait(false) : null
        };

        // Try to suggest the domain from the published server URL, if one is set.
        string? published = SettingsReader.Text(networkConfig, "PublishedServerUriBySubnet")
                            ?? SettingsReader.Text(networkConfig, "CertificateHost")
                            ?? SettingsReader.Text(networkConfig, "BaseUrl");
        if (!string.IsNullOrWhiteSpace(published) && Uri.TryCreate(published.Contains("://") ? published : "https://" + published, UriKind.Absolute, out var uri))
        {
            defaults.Domain = uri.Host;
        }

        return defaults;
    }

    public static async Task<ExposureResult> CheckAsync(string host, int port, string protocol, CancellationToken ct)
    {
        protocol = protocol?.ToLowerInvariant() == "http" ? "http" : "https";
        var result = new ExposureResult { Host = host?.Trim() ?? string.Empty, Port = port, Protocol = protocol };

        if (string.IsNullOrWhiteSpace(result.Host))
        {
            result.Error = "Enter a domain or IP address to check.";
            return result;
        }

        if (port is < 1 or > 65535)
        {
            result.Error = "That doesn't look like a valid port.";
            return result;
        }

        // Only a host name or an address: anything else (a path, a query, a second address) is refused here,
        // instead of ending up in the URL.
        string bare = result.Host.Trim('[', ']');
        if (Uri.CheckHostName(bare) == UriHostNameType.Unknown
            || !Uri.TryCreate(new UriBuilder(protocol, bare, port, "/System/Info/Public").ToString(), UriKind.Absolute, out var url))
        {
            result.Error = "That doesn't look like a domain or IP address.";
            return result;
        }

        // A domain (or plain http): connect from this server, the way a viewer's app would.
        bool looksLikeIp = IPAddress.TryParse(bare, out _);
        if (!looksLikeIp || protocol == "http")
        {
            result.FromThisServer = true;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                result.Checked = true;
                result.Reachable = true;
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                result.RespondedAsJellyfin = body.Contains("\"ServerName\"", StringComparison.OrdinalIgnoreCase) || body.Contains("\"Version\"", StringComparison.OrdinalIgnoreCase) || body.Contains("\"Id\"", StringComparison.OrdinalIgnoreCase);
                return result;
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            {
                // A certificate this server doesn't trust (self-signed, say): something is listening there all the same.
                result.Checked = true;
                result.Reachable = true;
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Nothing answered from here. For a domain that is the result, though it only tells about this server's view.
                if (!looksLikeIp)
                {
                    result.Checked = true;
                    result.Reachable = false;
                    return result;
                }

                // For an IP, ask the port-checker from outside instead.
                result.FromThisServer = false;
            }
        }

        // A raw IP and port: ask a public port-checker whether it answers.
        try
        {
            string query = System.Text.Json.JsonSerializer.Serialize(new { host = bare, ports = new[] { port.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://portchecker.io/api/query")
            {
                Content = new StringContent(query, System.Text.Encoding.UTF8, "application/json")
            };
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).ToLowerInvariant();
                int pos = body.IndexOf($"\"{port}\"", StringComparison.Ordinal);
                if (pos < 0)
                {
                    pos = body.IndexOf($":{port},", StringComparison.Ordinal);
                }

                if (pos >= 0)
                {
                    string after = body[pos..Math.Min(body.Length, pos + 60)];
                    result.Checked = true;
                    result.Reachable = after.Contains("\"status\":true") || after.Contains("open");
                    return result;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Nothing worked.
        }

        result.Error = "Couldn't test that address, so this isn't conclusive. Your network may block the check, or the port-check service may be down. Try again later.";
        return result;
    }

    // Kept for a while so pressing the button again doesn't ask an outside service each time. A failed
    // lookup is kept for less, so a short network blip doesn't hide the address for an hour.
    private static async Task<string?> PublicIpAsync(CancellationToken ct)
    {
        var known = _publicIp;
        if (known is not null && DateTime.UtcNow - known.At < (known.Ip is null ? NoPublicIpCacheTime : PublicIpCacheTime))
        {
            return known.Ip;
        }

        string? ip = await LookUpPublicIpAsync(ct).ConfigureAwait(false);
        if (!ct.IsCancellationRequested)
        {
            _publicIp = new PublicIpLookup(ip, DateTime.UtcNow);
        }

        return ip;
    }

    private static async Task<string?> LookUpPublicIpAsync(CancellationToken ct)
    {
        foreach (var service in IpServices)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                string ip = (await Http.GetStringAsync(service, timeout.Token).ConfigureAwait(false)).Trim();
                if (IPAddress.TryParse(ip, out _))
                {
                    return ip;
                }
            }
            catch
            {
                // Try the next service.
            }
        }

        return null;
    }

    // A class, not a tuple: requests can read it while another one replaces it, and a reference swap is atomic.
    private sealed record PublicIpLookup(string? Ip, DateTime At);
}
