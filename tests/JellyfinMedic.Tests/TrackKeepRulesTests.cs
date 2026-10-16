using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class TrackKeepRulesTests
{
    private static readonly PluginConfiguration Settings = new() { TracksKeepLanguages = "eng" };

    private static TrackInfo Audio(int index, string language, string title = "", bool isDefault = false) =>
        new() { Index = index, Kind = "audio", Language = language, FileLanguage = language, Title = title, Default = isDefault };

    private static TrackInfo Subtitle(int index, string language) =>
        new() { Index = index, Kind = "subtitle", Language = language, FileLanguage = language };

    private static int[] Kept(params TrackInfo[] tracks)
    {
        var plan = new FilePlan { Tracks = tracks.ToList() };
        TrackCleaner.ApplyKeepRules(plan, TrackCleaner.KeepLanguages(Settings), Settings);
        return plan.Tracks.Where(t => t.Keep).Select(t => t.Index).ToArray();
    }

    [Fact]
    public void A_commentary_in_your_language_does_not_replace_the_films_own_audio()
    {
        var kept = Kept(Audio(1, "jpn", isDefault: true), Audio(2, "eng", "Commentary"), Subtitle(3, "eng"));

        Assert.Equal(new[] { 1, 2, 3 }, kept);
    }

    [Fact]
    public void A_foreign_film_keeps_its_subtitles_when_your_language_only_has_a_commentary()
    {
        var kept = Kept(Audio(1, "jpn", isDefault: true), Audio(2, "eng", "Commentary"), Subtitle(3, "jpn"), Subtitle(4, "chi"));

        Assert.Equal(new[] { 1, 2, 3, 4 }, kept);
    }

    [Theory]
    [InlineData("Director's Commentary")]
    [InlineData("AUDIO DESCRIPTION")]
    [InlineData("Descriptive Video Service")]
    [InlineData("English (Audio Described)")]
    [InlineData("Isolated Score")]
    [InlineData("Karaoke")]
    public void Extra_titles_are_recognised_in_any_case(string title)
    {
        var kept = Kept(Audio(1, "jpn", isDefault: true), Audio(2, "eng", title));

        Assert.Contains(1, kept);
    }

    [Fact]
    public void A_main_track_in_your_language_still_lets_other_languages_go()
    {
        var kept = Kept(Audio(1, "eng", "English 5.1", isDefault: true), Audio(2, "eng", "Commentary"), Audio(3, "fre"));

        Assert.Equal(new[] { 1, 2 }, kept);
    }

    [Fact]
    public void Every_main_track_stays_when_your_language_only_has_an_extra()
    {
        var kept = Kept(Audio(1, "jpn", isDefault: true), Audio(2, "fre"), Audio(3, "eng", "Commentary"));

        Assert.Equal(new[] { 1, 2, 3 }, kept);
    }
}
