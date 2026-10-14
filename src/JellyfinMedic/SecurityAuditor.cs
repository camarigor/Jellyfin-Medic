using System.Collections;
using System.Net;
using JellyfinMedic.Api;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Services;

/// <summary>
/// Security findings from data already on the server: failed logins, logins from new or far-off
/// places, and accounts that can reach libraries they probably shouldn't. Read-only. IP addresses
/// are shown to the admin on the page but never saved into Medic's files or sent anywhere.
/// </summary>
public static class SecurityAuditor
{
    private const string Area = "Security";
    private const string WhereUsers = "Dashboard → Users";

    public static List<Finding> Audit(object? activityManager, IUserManager userManager, List<object> users)
    {
        var findings = new List<Finding>();
        Guard(() => LoginAttempts(activityManager, UserNames(users), findings));
        Guard(() => LibraryAccess(userManager, users, findings));
        return findings;
    }

    // ---------- Failed logins and unusual places ----------

    /// <summary>Each account's name by its id, written both with and without dashes as the activity log may use either.</summary>
    private static Dictionary<string, string> UserNames(List<object> users)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            string? name = SettingsReader.Text(user, "Username") ?? SettingsReader.Text(user, "Name");
            if (name is not null && Guid.TryParse(SettingsReader.Text(user, "Id"), out var id))
            {
                names[id.ToString("N")] = name;
                names[id.ToString("D")] = name;
            }
        }

        return names;
    }

    private static void LoginAttempts(object? activityManager, Dictionary<string, string> userNames, List<Finding> findings)
    {
        var entries = ActivityEntries(activityManager, DateTime.UtcNow.AddDays(-7));
        if (entries is null)
        {
            findings.Add(new Finding
            {
                Area = Area,
                Severity = Sev.Tip,
                Title = "Sign-ins couldn't be checked this time",
                Current = "The activity log didn't answer within 15 seconds",
                Recommended = "Open Medic again later; if this keeps happening, the database may be busy or need optimising",
                Why = "Failed sign-ins and sign-ins from new places are found in the activity log. Without it, this check can't say whether there were any.",
                Where = "Dashboard → Activity"
            });
            return;
        }

        if (entries.Count == 0)
        {
            return;
        }

        var failures = new List<(string User, string Ip)>();
        var success = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            string type = (SettingsReader.Text(entry, "Type") ?? string.Empty).ToLowerInvariant();
            string name = SettingsReader.Text(entry, "Name") ?? string.Empty;
            string overview = SettingsReader.Text(entry, "ShortOverview") ?? SettingsReader.Text(entry, "Overview") ?? string.Empty;
            string ip = ExtractIp(overview) ?? ExtractIp(name) ?? string.Empty;
            string user = SettingsReader.Text(entry, "UserId") ?? string.Empty;

            bool failed = type.Contains("authenticationfailed") || type.Contains("failedlogin") ||
                          (type.Contains("login") && (name + overview).Contains("failed", StringComparison.OrdinalIgnoreCase));
            bool loggedIn = !failed && (type.Contains("authenticationsucceeded") || type.Contains("sessionstarted") || type.Contains("userauthenticated"));

            if (failed)
            {
                failures.Add((NameOrUser(name, user), ip));
            }
            else if (loggedIn && ip.Length > 0)
            {
                // By account: the entry's text includes the device and is translated, and long ones used to all
                // become "a user", adding up different people's addresses. Phones on IPv6 change address often,
                // so an IPv6 address counts by its /64 network.
                string key = user.Length > 0 ? (userNames.TryGetValue(user, out var known) ? known : "an account (" + user[..Math.Min(8, user.Length)] + ")") : NameOrUser(name, user);
                (success.TryGetValue(key, out var set) ? set : success[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(Network(ip));
            }
        }

        if (failures.Count >= 10)
        {
            var byIp = failures.Where(f => f.Ip.Length > 0).GroupBy(f => f.Ip).OrderByDescending(g => g.Count()).ToList();
            var worst = byIp.FirstOrDefault();
            string detail = worst is not null
                ? $"{failures.Count} failed sign-ins in the last 7 days; most from {worst.Key} ({worst.Count()})"
                : $"{failures.Count} failed sign-ins in the last 7 days";

            // Most failures from a private address usually means a reverse proxy Jellyfin doesn't know about,
            // so every failure looks like it came from the proxy and the real address is lost.
            bool fromProxy = worst is not null && IPAddress.TryParse(worst.Key, out var source) && IsPrivate(source);
            findings.Add(new Finding
            {
                Area = Area,
                Severity = failures.Count >= 50 ? Sev.Problem : Sev.Improve,
                Title = "A lot of failed sign-ins",
                Current = detail,
                Recommended = (fromProxy
                        ? $"If {worst!.Key} is your reverse proxy, add it under Known proxies so Jellyfin sees the real addresses. "
                        : string.Empty)
                    + "If your server is reachable from the internet, put it behind a reverse proxy with fail2ban, or only allow access over a VPN. Jellyfin can also lock an account after a few wrong passwords (each user's \"Maximum failed login attempts\")",
                Why = "Many failed sign-ins usually mean someone is guessing passwords. A lockout after a few tries, or not exposing Jellyfin directly, stops this.",
                Where = fromProxy ? "Dashboard → Networking → Known proxies, and Dashboard → Users" : "Dashboard → Networking, and your reverse proxy"
            });
        }

        var roaming = success.Where(s => s.Value.Count >= 3).ToList();
        if (roaming.Count > 0)
        {
            findings.Add(new Finding
            {
                Area = Area,
                Severity = Sev.Tip,
                Title = roaming.Count == 1 ? "An account signs in from several places" : $"{roaming.Count} accounts sign in from several places",
                Id = "security.roaming-accounts",
                Current = string.Join("; ", roaming.Select(r => $"{r.Key}: {r.Value.Count} addresses")),
                Recommended = "Check these are all the right person. If not, change the password and review who has it",
                Why = "Signing in from several different addresses in a week can be normal (phone, work, travel), but it can also mean a password is being shared or has been stolen.",
                Where = WhereUsers
            });
        }
    }

    // ---------- Library access ----------

    private static void LibraryAccess(IUserManager userManager, List<object> users, List<Finding> findings)
    {
        var getDto = userManager.GetType().GetMethods().FirstOrDefault(m => m.Name == "GetUserDto" && m.GetParameters().Length >= 1);
        var flagged = new List<string>();

        foreach (var user in users)
        {
            object? dto = TryDto(getDto, userManager, user);
            string name = SettingsReader.Text(dto, "Name") ?? SettingsReader.Text(user, "Username") ?? "Unknown";
            if (SettingsReader.Bool(dto, "Policy.IsAdministrator") ?? false)
            {
                continue; // Admins see everything by design.
            }

            bool allLibraries = SettingsReader.Bool(dto, "Policy.EnableAllFolders") ?? false;
            bool parentalLimited = (SettingsReader.Number(dto, "Policy.MaxParentalRating") ?? 0) > 0 ||
                                   (SettingsReader.List(dto, "Policy.BlockedTags")?.Count ?? 0) > 0;
            if (allLibraries && !parentalLimited)
            {
                flagged.Add(name);
            }
        }

        if (flagged.Count > 0)
        {
            findings.Add(new Finding
            {
                Area = Area,
                Severity = Sev.Tip,
                Title = flagged.Count == 1 ? "An account can see every library" : $"{flagged.Count} accounts can see every library",
                Id = "security.all-libraries",
                Current = string.Join(", ", flagged),
                Recommended = "Give each account only the libraries it should see, and set parental limits for children",
                Why = "These accounts can open every library, including any adult or IPTV ones, and have no content rating limit. That may not be what you want for children's or guests' accounts.",
                Where = WhereUsers + " → (user) → Access and Parental Control"
            });
        }
    }

    // ---------- Reading the activity log ----------

    /// <summary>The activity log since a date; empty when there's no log to read, null when it didn't answer in time.</summary>
    private static List<object>? ActivityEntries(object? activityManager, DateTime sinceUtc)
    {
        if (activityManager is null)
        {
            return new List<object>();
        }

        try
        {
            // Jellyfin 10.9 and later: GetPagedResultAsync(ActivityLogQuery), newest first.
            var asyncMethod = activityManager.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "GetPagedResultAsync" && m.GetParameters().Length == 1);
            if (asyncMethod is not null)
            {
                object? query = Activator.CreateInstance(asyncMethod.GetParameters()[0].ParameterType);
                SetIfPresent(query, "MinDate", sinceUtc);
                SetIfPresent(query, "Limit", 500);
                if (asyncMethod.Invoke(activityManager, new[] { query }) is System.Threading.Tasks.Task task)
                {
                    if (!task.Wait(TimeSpan.FromSeconds(15)))
                    {
                        return null; // too slow to answer: say so, rather than report a week with no failed sign-ins
                    }

                    object? result = task.GetType().GetProperty("Result")?.GetValue(task);
                    if (SettingsReader.Get(result, "Items") is IEnumerable found && found is not string)
                    {
                        return found.Cast<object>().Where(e => e is not null).ToList();
                    }
                }
            }

            // Older versions.
            foreach (var method in activityManager.GetType().GetMethods().Where(m => m.Name is "GetPagedResult" or "GetActivityLogEntries"))
            {
                var parameters = method.GetParameters();
                var args = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    args[i] = parameters[i].ParameterType switch
                    {
                        { } t when t == typeof(int) => i == parameters.Length - 1 ? 500 : 0,
                        { } t when t == typeof(DateTime) => sinceUtc,
                        { } t when t == typeof(DateTime?) => sinceUtc,
                        _ => parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null
                    };
                }

                object? result = method.Invoke(activityManager, args);
                var items = SettingsReader.Get(result, "Items") ?? result;
                if (items is IEnumerable list && items is not string)
                {
                    return list.Cast<object>().Where(e => e is not null).ToList();
                }
            }
        }
        catch
        {
            // Activity log unavailable in this version: skip these checks.
        }

        return new List<object>();
    }

    private static void SetIfPresent(object? target, string name, object value)
    {
        var property = target?.GetType().GetProperty(name);
        if (property is { CanWrite: true })
        {
            property.SetValue(target, value);
        }
    }

    private static string? ExtractIp(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var token in text.Split(new[] { ' ', '\t', ',', ';', '(', ')', '[', ']', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = token.Trim();
            int slash = candidate.IndexOf('/');
            if (slash > 0)
            {
                candidate = candidate[..slash];
            }

            if (IPAddress.TryParse(candidate, out var ip) && !IPAddress.IsLoopback(ip) && candidate.Any(char.IsDigit))
            {
                return ip.ToString();
            }
        }

        return null;
    }

    // An IPv6 address counts by its /64 network (one home or phone connection); IPv4 as it is.
    private static string Network(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6)
        {
            return ip;
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    // Addresses only seen inside a home or a Docker network: 10/8, 172.16/12, 192.168/16, IPv6 unique local and link-local.
    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var b = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
            : (b[0] & 0xfe) == 0xfc || ip.IsIPv6LinkLocal;
    }

    private static string NameOrUser(string name, string userId) =>
        !string.IsNullOrWhiteSpace(name) && name.Length < 40 ? name : (userId.Length > 0 ? "a user" : "unknown");

    private static object? TryDto(System.Reflection.MethodInfo? getDto, IUserManager manager, object user)
    {
        if (getDto is null)
        {
            return null;
        }

        try
        {
            var args = new object?[getDto.GetParameters().Length];
            args[0] = user;
            return getDto.Invoke(manager, args);
        }
        catch
        {
            return null;
        }
    }

    private static void Guard(Action check)
    {
        try
        {
            check();
        }
        catch
        {
            // One failing security check never stops the rest of the report.
        }
    }
}
