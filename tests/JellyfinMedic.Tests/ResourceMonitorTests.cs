using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class ResourceMonitorTests
{
    private static readonly DateTime Noon = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_cpu_time_of_an_ffmpeg_that_has_finished_stays_in_the_sum()
    {
        var jellyfin = ResourceMonitor.ParseStat(Stat(1, "jellyfin", 0, utime: 1000))!.Value;
        var ffmpeg = ResourceMonitor.ParseStat(Stat(50, "ffmpeg", 1, utime: 3000))!.Value;
        // FFmpeg used one more second and exited; Jellyfin reaped it, which moves all of its time to Jellyfin's cutime.
        var jellyfinLater = ResourceMonitor.ParseStat(Stat(1, "jellyfin", 0, utime: 1100, cutime: 3100))!.Value;

        double before = ResourceMonitor.TreeCpuSeconds(jellyfin, new[] { ffmpeg });
        double after = ResourceMonitor.TreeCpuSeconds(jellyfinLater, Array.Empty<ResourceMonitor.ProcessStat>());

        Assert.Equal(40, before, 3);
        Assert.Equal(42, after, 3);
    }

    [Fact]
    public void Each_process_counts_its_own_time_and_what_its_finished_children_used()
    {
        var jellyfin = ResourceMonitor.ParseStat(Stat(1, "jellyfin", 0, utime: 300, stime: 100, cutime: 200, cstime: 100))!.Value;
        var ffmpeg = ResourceMonitor.ParseStat(Stat(50, "ffmpeg", 1, utime: 40, stime: 10, cutime: 30, cstime: 20))!.Value;

        Assert.Equal(8, ResourceMonitor.TreeCpuSeconds(jellyfin, new[] { ffmpeg }), 3);
    }

    [Fact]
    public void A_spike_still_going_when_the_cooldown_ends_is_recorded_then()
    {
        Assert.False(ResourceMonitor.ShouldRecord(4, Noon.AddMinutes(-5), Noon));
        Assert.True(ResourceMonitor.ShouldRecord(4, Noon.AddMinutes(-16), Noon));
    }

    [Fact]
    public void A_spike_is_recorded_once_it_has_lasted_three_readings()
    {
        Assert.False(ResourceMonitor.ShouldRecord(2, null, Noon));
        Assert.True(ResourceMonitor.ShouldRecord(3, null, Noon));
    }

    // Jellyfin 12.2's trickplay arguments ("-loglevel error {0} -an -sn {1} -threads {2} -c:v {3} {4}{5}{6}-f {7} \"{8}\""),
    // with numbered images in a temporary folder; the word "trickplay" is in none of them.
    [Theory]
    [InlineData("/usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -threads 0 -i file:/media/Movies/Film (2020)/Film.mkv -an -sn -vf fps=1/10,scale=w=320:h=-2 -threads 1 -c:v mjpeg -qscale:v 4 -f image2 /cache/temp/5f0c1d2e9b8a4c7d8e6f1a2b3c4d5e6f/%08d.jpg")]
    [InlineData("/usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -skip_frame nokey -threads 0 -i file:/media/TV/Show/S01E01.mkv -an -sn -vf fps=1/10,setpts=N/1/TB,scale=w=320:h=-2 -threads 1 -c:v mjpeg -qscale:v 4 -f image2 /cache/temp/0a1b2c3d4e5f60718293a4b5c6d7e8f9/%08d.jpg")]
    public void Trickplay_is_told_by_its_numbered_images(string commandLine)
    {
        Assert.Equal("trickplay images", ResourceMonitor.FfmpegRole("ffmpeg", () => commandLine));
    }

    [Fact]
    public void A_chapter_image_is_one_frame_not_trickplay()
    {
        const string ChapterImage = "/usr/lib/jellyfin-ffmpeg/ffmpeg -ss 00:05:00 -i file:/media/Movies/Film.mkv -threads 0 -v quiet -vframes 1 -vf scale=trunc(iw*sar):ih -f image2 /config/data/chapters/0a1b/300000000.jpg";

        Assert.Equal("chapter or thumbnail images", ResourceMonitor.FfmpegRole("ffmpeg", () => ChapterImage));
    }

    [Fact]
    public void Ffprobe_is_counted_as_media_probing()
    {
        const string Probe = "/usr/lib/jellyfin-ffmpeg/ffprobe -analyzeduration 200M -probesize 1G -i file:/media/Movies/Film.mkv -threads 0 -v warning -print_format json -show_streams -show_format";

        Assert.Equal("media probing", ResourceMonitor.FfmpegRole("ffprobe", () => Probe));
    }

    [Fact]
    public void Other_processes_have_no_ffmpeg_role()
    {
        Assert.Null(ResourceMonitor.FfmpegRole("jellyfin", () => "/jellyfin/jellyfin --datadir /config"));
    }

    // A /proc/<pid>/stat line as Linux writes it, up to rsslim. Times are in ticks, 100 a second.
    private static string Stat(int pid, string name, int parent, int utime, int stime = 0, int cutime = 0, int cstime = 0) =>
        $"{pid} ({name}) S {parent} {pid} {pid} 0 -1 4194560 12000 300 0 0 {utime} {stime} {cutime} {cstime} 20 0 12 0 130221903 3221225472 51200 18446744073709551615";
}
