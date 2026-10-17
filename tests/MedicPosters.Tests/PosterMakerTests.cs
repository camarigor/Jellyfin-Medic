using Jellyfin.Plugin.MedicPosters.Configuration;
using Jellyfin.Plugin.MedicPosters.Posters;

namespace MedicPosters.Tests;

public class PosterMakerTests
{
    private static readonly PosterSpec Spec = PosterSpec.From(new PluginConfiguration());

    [Fact]
    public void Moments_are_spread_between_the_opening_and_the_credits()
    {
        var starts = PosterMaker.MomentStarts(6000, 3, 1);
        Assert.Equal(new[] { 900.0, 2700, 4500 }, starts);
    }

    [Fact]
    public void Short_films_still_fit_every_moment_and_never_play_past_the_end()
    {
        Assert.Equal(new[] { 0.6, 1.8, 3 }, PosterMaker.MomentStarts(4, 3, 1));
        Assert.Equal(new[] { 0.0, 1, 2 }, PosterMaker.MomentStarts(3, 3, 1));
        Assert.Equal(new[] { 0.0, 1, 1 }, PosterMaker.MomentStarts(2, 3, 1));
    }

    [Fact]
    public void A_film_of_unknown_length_plays_from_the_start()
    {
        Assert.Equal(new[] { 0.0, 1.5 }, PosterMaker.MomentStarts(null, 2, 1.5));
    }

    [Fact]
    public void Titles_cannot_inject_drawing_commands()
    {
        Assert.Equal("ALIEN (/P1) /N", PosterMaker.AssText("Alien {\\p1} \\N"));
        Assert.Equal("LINE BREAK", PosterMaker.AssText("Line\n  Break\t"));
    }

    [Fact]
    public void Long_titles_get_a_smaller_font()
    {
        Assert.True(PosterMaker.TitleSize(240, 4) > PosterMaker.TitleSize(240, 25));
        Assert.True(PosterMaker.TitleSize(240, 25) > PosterMaker.TitleSize(240, 80));
    }

    [Fact]
    public void The_drawing_has_the_title_frame_and_shade_only_when_asked_for()
    {
        var full = PosterMaker.BuildAss(Spec, "Big Buck Bunny");
        Assert.Contains("PlayResX: 240", full);
        Assert.Contains("PlayResY: 360", full);
        Assert.Contains(",BIG BUCK BUNNY", full);
        Assert.Contains("Style: Title," + PosterMaker.FontName, full);
        Assert.Equal(4, full.Split("Dialogue:").Length);

        var bare = PosterMaker.BuildAss(Spec with { ShowTitle = false, ShowFrame = false }, "Big Buck Bunny");
        Assert.DoesNotContain("Dialogue:", bare);
    }

    [Fact]
    public void FFmpeg_seeks_once_per_moment_and_writes_a_looping_gif()
    {
        var args = PosterMaker.BuildArgs("/films/a b.mkv", new[] { 10.0, 20.5 }, Spec, hdr: false, "/tmp/out.gif");
        Assert.Equal(2, args.Count(a => a == "-ss"));
        Assert.Equal(new[] { "-ss", "20.5", "-t", "1", "-i", "/films/a b.mkv" }, args.Skip(args.ToList().LastIndexOf("-ss")).Take(6));
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("concat=n=2:v=1:a=0", graph);
        Assert.Contains("ass=filename=poster.ass:fontsdir=fonts", graph);
        Assert.Contains("palettegen=max_colors=96", graph);
        Assert.DoesNotContain("zscale", graph);
        Assert.Equal("/tmp/out.gif", args[^1]);
        Assert.Contains("gif", args);

        var hdr = PosterMaker.BuildArgs("/films/a.mkv", new[] { 10.0 }, Spec, hdr: true, "/tmp/out.gif");
        Assert.Contains("tonemap", hdr[hdr.ToList().IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void Settings_out_of_range_are_kept_to_sensible_values()
    {
        var spec = PosterSpec.From(new PluginConfiguration { Width = 5001, Moments = 0, MomentTenths = 1, FramesPerSecond = 100, Colours = 2 });
        Assert.Equal((600, 900, 1, 0.5, 15, 16), (spec.Width, spec.Height, spec.Moments, spec.MomentSeconds, spec.FramesPerSecond, spec.Colours));
    }
}
