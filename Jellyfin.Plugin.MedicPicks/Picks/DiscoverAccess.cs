using System;
using System.Collections;
using System.Linq;
using Jellyfin.Plugin.MedicPicks.Configuration;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>
/// Who gets Discover picks. Discover suggests titles from TMDb, which parental limits can't filter, so an account
/// with a rating limit or tag rules only gets it when an admin has ticked it for Discover since.
/// </summary>
public static class DiscoverAccess
{
    /// <summary>Discover is on for the server, the person isn't unticked, and an account with parental limits was ticked.</summary>
    public static bool Allowed(PluginConfiguration config, Guid userId, bool parentalLimits) =>
        config.EnableDiscover
        && !Listed(config.DiscoverDisabledUserIds, userId)
        && (!parentalLimits || Listed(config.DiscoverAllowedLimitedUserIds, userId));

    private static bool Listed(string[]? ids, Guid userId) =>
        (ids ?? Array.Empty<string>()).Any(id => Guid.TryParse(id, out var g) && g == userId);

    /// <summary>
    /// Whether a Jellyfin user has a maximum rating, blocked tags or allowed tags. Read by name, as the user type and
    /// its rating property changed between Jellyfin versions; false when none can be read.
    /// </summary>
    public static bool HasParentalLimits(object? user)
    {
        if (user is null)
        {
            return false;
        }

        var type = user.GetType();
        foreach (string name in new[] { "MaxParentalRatingScore", "MaxParentalAgeRating" })
        {
            if (type.GetProperty(name)?.GetValue(user) is not null)
            {
                return true;
            }
        }

        var getPreference = type.GetMethods().FirstOrDefault(m => m.Name == "GetPreference" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
        if (getPreference is null)
        {
            return false;
        }

        var kind = getPreference.GetParameters()[0].ParameterType;
        foreach (string name in new[] { "BlockedTags", "AllowedTags" })
        {
            if (Enum.IsDefined(kind, name)
                && getPreference.Invoke(user, new[] { Enum.Parse(kind, name) }) is IEnumerable values
                && values.Cast<object?>().Any(v => v is string s ? s.Length > 0 : v is not null))
            {
                return true;
            }
        }

        return false;
    }
}
