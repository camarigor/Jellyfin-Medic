using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

/// <summary>GPU readings from a pretend /proc, /dev and /sys in a temporary folder.</summary>
public sealed class GpuLoadTests : IDisposable
{
    private const string Pdev = "0000:c5:00.0";
    private static readonly DateTime Start = new(2026, 10, 17, 21, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Directory.CreateTempSubdirectory("medic-gpu-").FullName;

    private string Proc => Path.Combine(_root, "proc");

    private string Dev => Path.Combine(_root, "dev");

    private string Sys => Path.Combine(_root, "sys");

    public void Dispose()
    {
        // A folder made unreadable by a test has to be readable again before it can be deleted.
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_vaapi_transcode_on_the_vcn_reads_as_video_load_while_the_whole_gpu_looks_idle()
    {
        // The 780M in production: VCN 4 counts decode and encode as "enc", scaling as "compute", and gpu_busy_percent stays 0.
        Node("renderD128");
        Card("card0", Pdev, gpuBusy: "0", vcnBusy: "3");
        Fd(50, 3, "renderD128", Client(7, ("compute", 26_847_320), ("enc", 175_009_915)));
        var gpu = new GpuLoad(Proc, Dev, Sys);
        Assert.Null(gpu.Read(new[] { 50 }, Start));

        // In 2 seconds, enc ran 1.5 s and compute 0.2 s.
        Fd(50, 3, "renderD128", Client(7, ("compute", 226_847_320), ("enc", 1_675_009_915)));
        var reading = gpu.Read(new[] { 50 }, Start.AddSeconds(2));

        Assert.Equal(new GpuReading(75, false), reading);
    }

    [Fact]
    public void A_client_open_on_several_fds_or_processes_is_counted_once()
    {
        Node("renderD128");
        foreach (var (pid, fd) in new[] { (50, 3), (50, 4), (50, 5), (51, 3) })
        {
            Fd(pid, fd, "renderD128", Client(7, ("enc", 0)));
        }

        var gpu = new GpuLoad(Proc, Dev, Sys);
        gpu.Read(new[] { 50, 51 }, Start);
        foreach (var (pid, fd) in new[] { (50, 3), (50, 4), (50, 5), (51, 3) })
        {
            Fd(pid, fd, "renderD128", Client(7, ("enc", 500_000_000)));
        }

        Assert.Equal(new GpuReading(25, false), gpu.Read(new[] { 50, 51 }, Start.AddSeconds(2)));
    }

    [Fact]
    public void Two_transcodes_on_the_same_engine_add_up()
    {
        Node("renderD128");
        Fd(50, 3, "renderD128", Client(7, ("enc", 0)));
        Fd(60, 3, "renderD128", Client(8, ("enc", 0)));
        var gpu = new GpuLoad(Proc, Dev, Sys);
        gpu.Read(new[] { 50, 60 }, Start);
        Fd(50, 3, "renderD128", Client(7, ("enc", 600_000_000)));
        Fd(60, 3, "renderD128", Client(8, ("enc", 400_000_000)));

        Assert.Equal(new GpuReading(50, false), gpu.Read(new[] { 50, 60 }, Start.AddSeconds(2)));
    }

    [Fact]
    public void A_transcode_that_started_after_the_last_reading_counts_from_zero()
    {
        Node("renderD128");
        var gpu = new GpuLoad(Proc, Dev, Sys);
        gpu.Read(Array.Empty<int>(), Start);
        Fd(70, 3, "renderD128", Client(9, ("enc", 1_000_000_000)));

        Assert.Equal(new GpuReading(50, false), gpu.Read(new[] { 70 }, Start.AddSeconds(2)));
    }

    [Fact]
    public void A_client_id_reused_with_smaller_counters_is_a_new_client()
    {
        Node("renderD128");
        Fd(50, 3, "renderD128", Client(7, ("enc", 9_000_000_000)));
        var gpu = new GpuLoad(Proc, Dev, Sys);
        gpu.Read(new[] { 50 }, Start);
        Fd(50, 3, "renderD128", Client(7, ("enc", 400_000_000)));

        Assert.Equal(new GpuReading(20, false), gpu.Read(new[] { 50 }, Start.AddSeconds(2)));
    }

    [Fact]
    public void An_engine_with_two_units_is_shared_between_them()
    {
        // i915 can report a video class of two engines: 2 s of busy time in 2 s is half of it.
        Node("renderD128");
        Fd(50, 3, "renderD128", "drm-driver:\ti915\ndrm-pdev:\t0000:00:02.0\ndrm-client-id:\t3\ndrm-engine-video:\t0 ns\ndrm-engine-capacity-video:\t2\n");
        var gpu = new GpuLoad(Proc, Dev, Sys);
        gpu.Read(new[] { 50 }, Start);
        Fd(50, 3, "renderD128", "drm-driver:\ti915\ndrm-pdev:\t0000:00:02.0\ndrm-client-id:\t3\ndrm-engine-video:\t2000000000 ns\ndrm-engine-capacity-video:\t2\n");

        Assert.Equal(new GpuReading(50, false), gpu.Read(new[] { 50 }, Start.AddSeconds(2)));
    }

    [Fact]
    public void Engines_counted_in_cycles_use_their_own_total()
    {
        Node("renderD128");
        Fd(50, 3, "renderD128", "drm-driver:\txe\ndrm-pdev:\t0000:03:00.0\ndrm-client-id:\t4\ndrm-cycles-vcs:\t1000\ndrm-total-cycles-vcs:\t10000\n");
        var gpu = new GpuLoad(Proc, Dev, Sys);
        gpu.Read(new[] { 50 }, Start);
        Fd(50, 3, "renderD128", "drm-driver:\txe\ndrm-pdev:\t0000:03:00.0\ndrm-client-id:\t4\ndrm-cycles-vcs:\t4000\ndrm-total-cycles-vcs:\t14000\n");

        Assert.Equal(new GpuReading(75, false), gpu.Read(new[] { 50 }, Start.AddSeconds(2)));
    }

    [Fact]
    public void A_host_gpu_jellyfin_was_not_given_is_never_read()
    {
        // A container started without --device: /sys/class/drm is still the host's.
        Card("card1", Pdev, gpuBusy: "9", vcnBusy: "40");
        var gpu = new GpuLoad(Proc, Dev, Sys);
        gpu.Read(Array.Empty<int>(), Start);

        Assert.Null(gpu.Read(Array.Empty<int>(), Start.AddSeconds(15)));
    }

    [Fact]
    public void A_gpu_jellyfin_has_but_is_not_using_reads_zero_whatever_other_programs_do()
    {
        Node("renderD128");
        Card("renderD128", Pdev, gpuBusy: "60", vcnBusy: "80");
        Card("card0", Pdev, gpuBusy: "60", vcnBusy: "80");
        var gpu = new GpuLoad(Proc, Dev, Sys);
        gpu.Read(Array.Empty<int>(), Start);

        Assert.Equal(new GpuReading(0, false), gpu.Read(Array.Empty<int>(), Start.AddSeconds(15)));
    }

    [Fact]
    public void Without_per_client_figures_the_whole_gpu_is_read_and_marked_as_such()
    {
        // An older kernel: Jellyfin's FFmpeg holds the GPU, but its fdinfo has no client id or engine times.
        Node("renderD128");
        Card("card0", Pdev, gpuBusy: "12", vcnBusy: "70");
        Fd(50, 3, "renderD128", $"pos:\t0\nflags:\t02100002\ndrm-driver:\tamdgpu\ndrm-pdev:\t{Pdev}\n");
        var gpu = new GpuLoad(Proc, Dev, Sys);
        gpu.Read(new[] { 50 }, Start);

        Assert.Equal(new GpuReading(70, true), gpu.Read(new[] { 50 }, Start.AddSeconds(15)));
    }

    [Fact]
    public void When_the_ffmpeg_processes_cannot_be_read_the_gpu_is_not_reported_as_idle()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Node("renderD128");
        Fd(50, 3, "renderD128", Client(7, ("enc", 0)));
        string fds = Path.Combine(Proc, "50", "fd");
        File.SetUnixFileMode(fds, UnixFileMode.None);
        if (Directory.Exists(fds) && TryList(fds))
        {
            return; // root reads it anyway
        }
        var gpu = new GpuLoad(Proc, Dev, Sys);
        gpu.Read(new[] { 50 }, Start);

        Assert.Null(gpu.Read(new[] { 50 }, Start.AddSeconds(15)));
    }

    [Fact]
    public void Nvidia_counts_the_video_engines_and_is_marked_as_the_whole_gpu()
    {
        // An AMD iGPU on the host as well: its card must not hide the NVIDIA one Jellyfin has.
        Card("card0", Pdev, gpuBusy: "2", vcnBusy: "0");
        Directory.CreateDirectory(Dev);
        File.WriteAllText(Path.Combine(Dev, "nvidiactl"), string.Empty);
        var asked = new List<string>();
        var gpu = new GpuLoad(Proc, Dev, Sys, args => { asked.Add(args); return "12, 85, 40\n"; });
        gpu.Read(Array.Empty<int>(), Start);

        Assert.Equal(new GpuReading(85, true), gpu.Read(Array.Empty<int>(), Start.AddSeconds(15)));
        Assert.Contains("utilization.encoder", asked[^1], StringComparison.Ordinal);
        Assert.Contains("utilization.decoder", asked[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void An_nvidia_driver_without_the_video_fields_still_gives_the_gpu_figure()
    {
        Directory.CreateDirectory(Dev);
        File.WriteAllText(Path.Combine(Dev, "nvidiactl"), string.Empty);
        var gpu = new GpuLoad(Proc, Dev, Sys, args => args.Contains("encoder", StringComparison.Ordinal)
            ? "Field \"utilization.encoder\" is not a valid field to query.\n"
            : "33\n");
        gpu.Read(Array.Empty<int>(), Start);

        Assert.Equal(new GpuReading(33, true), gpu.Read(Array.Empty<int>(), Start.AddSeconds(15)));
    }

    [Fact]
    public void Only_jellyfins_own_gpu_use_raises_a_heavy_use_alert()
    {
        Assert.True(ResourceMonitor.GpuHigh(new ResourceSample { GpuPercent = 96 }));
        Assert.False(ResourceMonitor.GpuHigh(new ResourceSample { GpuPercent = 100, GpuWholeDevice = true }));
        Assert.False(ResourceMonitor.GpuHigh(new ResourceSample { GpuPercent = null }));
    }

    private static bool TryList(string folder)
    {
        try
        {
            Directory.EnumerateFileSystemEntries(folder).ToList();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A device node Jellyfin was given.
    private void Node(string name)
    {
        Directory.CreateDirectory(Path.Combine(Dev, "dri"));
        File.WriteAllText(Path.Combine(Dev, "dri", name), string.Empty);
    }

    // A DRM entry in the host's sysfs, its device being the PCI device with that address.
    private void Card(string name, string pdev, string gpuBusy, string vcnBusy)
    {
        string device = Path.Combine(Sys, "bus", "pci", "devices", pdev);
        Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(device, "gpu_busy_percent"), gpuBusy + "\n");
        File.WriteAllText(Path.Combine(device, "vcn_busy_percent"), vcnBusy + "\n");
        string entry = Path.Combine(Sys, "class", "drm", name);
        Directory.CreateDirectory(entry);
        if (!File.Exists(Path.Combine(entry, "device")) && !Directory.Exists(Path.Combine(entry, "device")))
        {
            Directory.CreateSymbolicLink(Path.Combine(entry, "device"), device);
        }
    }

    // An open file of a process, pointing at /dev/dri/<node>, with its fdinfo.
    private void Fd(int pid, int fd, string node, string fdinfo)
    {
        string fds = Path.Combine(Proc, pid.ToString(), "fd");
        string infos = Path.Combine(Proc, pid.ToString(), "fdinfo");
        Directory.CreateDirectory(fds);
        Directory.CreateDirectory(infos);
        string link = Path.Combine(fds, fd.ToString());
        if (!File.Exists(link) && new FileInfo(link).LinkTarget is null)
        {
            File.CreateSymbolicLink(link, Path.Combine(Dev, "dri", node));
        }

        File.WriteAllText(Path.Combine(infos, fd.ToString()), fdinfo);
    }

    // amdgpu's fdinfo for one client, engine times in nanoseconds.
    private static string Client(int id, params (string Engine, long Ns)[] engines) =>
        $"pos:\t0\nflags:\t02100002\nmnt_id:\t26\nino:\t1234\ndrm-driver:\tamdgpu\ndrm-client-id:\t{id}\ndrm-pdev:\t{Pdev}\npasid:\t32790\n" +
        "drm-memory-vram:\t20480 KiB\n" +
        string.Concat(engines.Select(e => $"drm-engine-{e.Engine}:\t{e.Ns} ns\n"));
}
