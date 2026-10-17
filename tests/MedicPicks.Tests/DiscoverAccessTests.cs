using Jellyfin.Plugin.MedicPicks.Api;
using Jellyfin.Plugin.MedicPicks.Configuration;
using Jellyfin.Plugin.MedicPicks.Picks;

namespace MedicPicks.Tests;

public sealed class DiscoverAccessTests
{
    private static readonly Guid Child = Guid.NewGuid();

    [Fact]
    public void An_account_with_parental_limits_gets_Discover_only_when_an_admin_ticked_it()
    {
        var config = new PluginConfiguration { EnableDiscover = true };
        Assert.False(DiscoverAccess.Allowed(config, Child, parentalLimits: true));
        Assert.True(DiscoverAccess.Allowed(config, Child, parentalLimits: false));

        config.DiscoverAllowedLimitedUserIds = new[] { Child.ToString("N") };
        Assert.True(DiscoverAccess.Allowed(config, Child, parentalLimits: true));

        config.EnableDiscover = false;
        Assert.False(DiscoverAccess.Allowed(config, Child, parentalLimits: true));
    }

    [Fact]
    public void An_unticked_account_gets_no_Discover_whatever_its_limits()
    {
        var config = new PluginConfiguration { EnableDiscover = true, DiscoverDisabledUserIds = new[] { Child.ToString() } };

        Assert.False(DiscoverAccess.Allowed(config, Child, parentalLimits: false));
    }

    private enum PreferenceKind { BlockedTags, AllowedTags, OrderedViews }

    private sealed class FakeUser
    {
        public int? MaxParentalRatingScore { get; set; }

        public Dictionary<PreferenceKind, string[]> Preferences { get; } = new();

        public string[] GetPreference(PreferenceKind kind) => Preferences.GetValueOrDefault(kind) ?? Array.Empty<string>();
    }

    [Fact]
    public void A_maximum_rating_or_tag_rules_count_as_parental_limits()
    {
        Assert.False(DiscoverAccess.HasParentalLimits(new FakeUser()));
        Assert.True(DiscoverAccess.HasParentalLimits(new FakeUser { MaxParentalRatingScore = 12 }));
        var tagged = new FakeUser();
        tagged.Preferences[PreferenceKind.BlockedTags] = new[] { "horror" };
        Assert.True(DiscoverAccess.HasParentalLimits(tagged));
        var empty = new FakeUser();
        empty.Preferences[PreferenceKind.AllowedTags] = new[] { string.Empty };
        Assert.False(DiscoverAccess.HasParentalLimits(empty));
    }

    [Fact]
    public void A_persons_rebuilds_start_at_least_the_gap_apart()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.Zero, PicksController.WaitBeforeRebuild(null, now));
        Assert.Equal(PicksController.MinRebuildGap - TimeSpan.FromSeconds(5), PicksController.WaitBeforeRebuild(now.AddSeconds(-5), now));
        Assert.Equal(TimeSpan.Zero, PicksController.WaitBeforeRebuild(now - PicksController.MinRebuildGap, now));
    }

    [Fact]
    public void A_names_leading_words_are_every_shorter_run_of_words_from_its_start()
    {
        Assert.Equal(new[] { "seven", "seven deadly" }, PicksEngine.LeadingWords("seven deadly sins"));
        Assert.Empty(PicksEngine.LeadingWords("avatar"));
    }
}
