using System.Diagnostics;
using System.Net;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class PlaybackAndSpeedTests
{
    // Jellyfin 12 on an AMD GPU with VAAPI tone maps through Vulkan with libplacebo, as on a Radeon 780M.
    private const string AmdHdrCommand =
        "ffmpeg -analyzeduration 200M -probesize 1G -init_hw_device drm=dr:/dev/dri/renderD128 -init_hw_device vaapi=va@dr "
        + "-init_hw_device vulkan=vk@dr -filter_hw_device vk -hwaccel vaapi -hwaccel_output_format vaapi -noautorotate "
        + "-i file:\"/media/Film (2020)/Film.mkv\" -map 0:0 -map 0:1 -codec:v:0 h264_vaapi -rc_mode VBR -b:v 8000000 "
        + "-vf \"setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale_vaapi=format=p010:extra_hw_frames=24,"
        + "hwmap=derive_device=vulkan,format=vulkan,libplacebo=upscaler=none:downscaler=none:format=bgra:tonemapping=bt.2390:peak_detect=0:"
        + "color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=pc,format=vulkan,hwmap=derive_device=vaapi,format=vaapi,"
        + "scale_vaapi=format=nv12:out_range=tv\" -codec:a:0 libfdk_aac -f hls \"/cache/transcodes/abc.m3u8\"";

    // The same path for an SDR film only scales: libplacebo runs, but without tone mapping.
    private const string AmdSdrCommand =
        "ffmpeg -init_hw_device drm=dr:/dev/dri/renderD128 -init_hw_device vaapi=va@dr -init_hw_device vulkan=vk@dr "
        + "-hwaccel vaapi -hwaccel_output_format vaapi -i file:\"/media/Show/S01E01.mkv\" -codec:v:0 h264_vaapi "
        + "-vf \"hwmap=derive_device=vulkan,format=vulkan,libplacebo=upscaler=none:downscaler=none:w=1280:h=720:format=bgra:range=pc,"
        + "format=vulkan,hwmap=derive_device=vaapi,format=vaapi,scale_vaapi=format=nv12\" -f hls \"/cache/transcodes/def.m3u8\"";

    [Fact]
    public void Tone_mapping_through_libplacebo_counts_as_done_on_the_GPU()
    {
        var log = ReadLog(AmdHdrCommand);

        Assert.Equal("GPU (Vulkan)", log.ToneMapping);
    }

    [Fact]
    public void Libplacebo_used_only_to_scale_isnt_tone_mapping()
    {
        var log = ReadLog(AmdSdrCommand);

        Assert.Equal("Not used", log.ToneMapping);
    }

    [Fact]
    public async Task The_upload_speed_counts_what_the_far_end_received_not_what_waits_in_the_send_buffers()
    {
        // A 2 Mbps uplink behind send buffers that take every byte at once: the test may only
        // count what the far end has confirmed, or it reports the buffers' speed.
        const double linkMbps = 2;
        using var http = new HttpClient(new SlowUplink(linkMbps));

        double measured = await SpeedTester.UploadAsync(http, "http://speed.test/__up", TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.InRange(measured, linkMbps * 0.5, linkMbps * 1.25);
    }

    [Fact]
    public async Task An_uplink_too_slow_to_finish_a_large_piece_still_measures()
    {
        const double linkMbps = 0.5;
        using var http = new HttpClient(new SlowUplink(linkMbps));

        var sw = Stopwatch.StartNew();
        double measured = await SpeedTester.UploadAsync(http, "http://speed.test/__up", TimeSpan.FromSeconds(2), CancellationToken.None);
        sw.Stop();

        Assert.InRange(measured, linkMbps * 0.5, linkMbps * 1.25);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"took {sw.Elapsed}");
    }

    private static TranscodeLogResult ReadLog(string command)
    {
        var dir = Directory.CreateTempSubdirectory("medic-ffmpeg-log-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "FFmpeg.Transcode-2026-10-17_20-00-00_abc.log"), command + "\n\nframe= 2400 fps=96 q=-0.0 size=N/A time=00:01:40.00 bitrate=N/A speed=4.01x\n");
            return TranscodeLogReader.Read(dir.FullName, DateTime.UtcNow.AddMinutes(-1), null, "vaapi");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// One shared link of a fixed speed. The request body is taken in full at once, as the kernel's
    /// send buffers do, and the answer comes when the link would have carried it to the far end.
    /// </summary>
    private sealed class SlowUplink : HttpMessageHandler
    {
        private readonly double _bytesPerSecond;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private double _linkFreeAt;

        public SlowUplink(double mbps) => _bytesPerSecond = mbps * 1_000_000 / 8;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            double doneAt;
            lock (_gate)
            {
                doneAt = Math.Max(_clock.Elapsed.TotalSeconds, _linkFreeAt) + (body.Length / _bytesPerSecond);
                _linkFreeAt = doneAt;
            }

            var wait = TimeSpan.FromSeconds(Math.Max(0, doneAt - _clock.Elapsed.TotalSeconds));
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
