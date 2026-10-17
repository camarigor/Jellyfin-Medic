using System.Diagnostics;
using System.Globalization;

namespace JellyfinMedic.Services;

/// <summary>
/// A GPU reading: the busiest engine's share, in percent. WholeDevice is true when it counts every program using the
/// GPU, not only Jellyfin's FFmpeg processes.
/// </summary>
internal readonly record struct GpuReading(double Percent, bool WholeDevice);

/// <summary>
/// Jellyfin's own GPU load, from the DRM usage figures the kernel keeps per open GPU file
/// (/proc/&lt;pid&gt;/fdinfo, Documentation/gpu/drm-usage-stats.rst) of the processes Jellyfin started. This counts the
/// video engines (VCN, Quick Sync) that gpu_busy_percent leaves out, and only Jellyfin's work. Where the kernel gives no
/// such figures, it falls back to the whole GPU: NVIDIA through nvidia-smi, AMD through sysfs. Only GPUs Jellyfin was
/// given (in /dev/dri, or /dev/nvidiactl) are read, never another host GPU sysfs happens to list.
/// </summary>
internal sealed class GpuLoad
{
    private const string FullNvidiaQuery = "--query-gpu=utilization.gpu,utilization.encoder,utilization.decoder --format=csv,noheader,nounits";
    private const string PlainNvidiaQuery = "--query-gpu=utilization.gpu --format=csv,noheader,nounits";

    private readonly string _proc;
    private readonly string _dev;
    private readonly string _sys;
    private readonly Func<string, string?> _nvidiaSmi;
    private Dictionary<(string Device, string Client), Dictionary<string, Counter>> _previous = new();
    private DateTime? _previousUtc;
    private DateTime _nvidiaRetryUtc = DateTime.MinValue;

    // Drivers older than about 2023 don't know the encoder and decoder fields; then only the plain query is asked.
    private string _nvidiaQuery = FullNvidiaQuery;

    public GpuLoad(string proc = "/proc", string dev = "/dev", string sys = "/sys", Func<string, string?>? nvidiaSmi = null)
    {
        _proc = proc;
        _dev = dev;
        _sys = sys;
        _nvidiaSmi = nvidiaSmi ?? RunNvidiaSmi;
    }

    /// <summary>
    /// The load since the last call, from the GPU files the given processes hold. Null on the first call, and when
    /// there's no GPU Jellyfin was given or its load can't be read.
    /// </summary>
    public GpuReading? Read(IEnumerable<int> pids, DateTime nowUtc)
    {
        var clients = new Dictionary<(string Device, string Client), DrmClient>();
        bool holdsGpu = false;
        bool unreadable = false;
        foreach (int pid in pids)
        {
            string fds = Path.Combine(_proc, pid.ToString(CultureInfo.InvariantCulture), "fd");
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(fds);
            }
            catch (UnauthorizedAccessException)
            {
                unreadable = true; // another user's process, or /proc mounted with hidepid
                continue;
            }
            catch (IOException)
            {
                continue; // the process has exited
            }

            foreach (string fd in entries)
            {
                // The link target alone tells a GPU file apart, so the fdinfo of every other file is never read.
                string? node = DriNode(fd);
                if (node is null)
                {
                    continue;
                }

                holdsGpu = true;
                string text;
                try
                {
                    text = File.ReadAllText(Path.Combine(_proc, pid.ToString(CultureInfo.InvariantCulture), "fdinfo", Path.GetFileName(fd)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var client = ParseFdinfo(text);
                // Several files, in one process or inherited by another, can be the same client and repeat its counters.
                var key = (client.Pdev ?? node, client.Id ?? $"{pid}/{Path.GetFileName(fd)}");
                clients.TryAdd(key, client);
            }
        }

        var previous = _previous;
        var previousUtc = _previousUtc;
        _previous = clients.ToDictionary(c => c.Key, c => c.Value.Engines);
        _previousUtc = nowUtc;
        if (previousUtc is not { } then || nowUtc <= then)
        {
            return null; // the first reading only sets the starting point
        }

        if (clients.Values.Any(c => c.Measured))
        {
            return new GpuReading(JellyfinShare(clients, previous, (nowUtc - then).TotalMilliseconds * 1e6), false);
        }

        if (File.Exists(Path.Combine(_dev, "nvidiactl")) && Nvidia() is { } nvidia)
        {
            return new GpuReading(nvidia, true);
        }

        if (holdsGpu || unreadable)
        {
            // Jellyfin uses the GPU but the kernel doesn't say how much: the whole GPU it uses, or null rather than 0.
            return WholeAmdGpu(clients.Values.Select(c => c.Pdev).OfType<string>().ToList()) is { } whole ? new GpuReading(whole, true) : null;
        }

        // A GPU Jellyfin has and no FFmpeg is using: Jellyfin's load is none, whatever other programs do on it.
        return DriNodes().Count > 0 ? new GpuReading(0, false) : null;
    }

    /// <summary>The busiest engine's share over all of Jellyfin's clients, so a full encoder shows even with the 3D engine idle.</summary>
    private static double JellyfinShare(
        Dictionary<(string Device, string Client), DrmClient> clients,
        Dictionary<(string Device, string Client), Dictionary<string, Counter>> previous,
        double wallNs)
    {
        var busy = new Dictionary<(string Device, string Engine), double>();
        foreach (var (key, client) in clients)
        {
            var before = previous.GetValueOrDefault(key);
            foreach (var (engine, now) in client.Engines)
            {
                // A client that is new since the last reading counts from zero, as does one whose counters went back
                // (its id reused after the file was closed).
                var then = before is not null && before.TryGetValue(engine, out var b) && b.Busy <= now.Busy && b.Total <= now.Total ? b : default;
                double span = now.Total > 0 ? now.Total - then.Total : wallNs;
                if (span > 0)
                {
                    var slot = (key.Device, engine);
                    busy[slot] = busy.GetValueOrDefault(slot) + (now.Busy - then.Busy) / span / now.Capacity;
                }
            }
        }

        return busy.Count == 0 ? 0 : Math.Round(Math.Clamp(busy.Values.Max() * 100, 0, 100), 1);
    }

    /// <summary>The device node's name when an open file is a GPU node (/dev/dri/renderD128, /dev/dri/card0), else null.</summary>
    private static string? DriNode(string fd)
    {
        string? target;
        try
        {
            target = new FileInfo(fd).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // closed since the folder was listed
        }

        if (target is null || Path.GetFileName(Path.GetDirectoryName(target)) != "dri")
        {
            return null;
        }

        string name = Path.GetFileName(target);
        return name.StartsWith("renderD", StringComparison.Ordinal) || name.StartsWith("card", StringComparison.Ordinal) ? name : null;
    }

    private List<string> DriNodes()
    {
        string dri = Path.Combine(_dev, "dri");
        return Directory.Exists(dri)
            ? Directory.EnumerateFileSystemEntries(dri).Select(Path.GetFileName).OfType<string>()
                .Where(n => n.StartsWith("renderD", StringComparison.Ordinal) || n.StartsWith("card", StringComparison.Ordinal)).ToList()
            : new List<string>();
    }

    /// <summary>
    /// The busier of the 3D engine and the video engine of AMD GPUs: those Jellyfin's clients are on (by PCI address)
    /// or, without one, those whose node is in /dev/dri. vcn_busy_percent is a single sample and swings at a steady
    /// load, but it's the only device-wide figure for the video engine.
    /// </summary>
    private double? WholeAmdGpu(List<string> pdevs)
    {
        var devices = pdevs.Count > 0
            ? pdevs.Distinct().Select(p => Path.Combine(_sys, "bus", "pci", "devices", p))
            : DriNodes().Select(n => Path.Combine(_sys, "class", "drm", n, "device"));
        double? highest = null;
        foreach (string device in devices)
        {
            foreach (string file in new[] { "gpu_busy_percent", "vcn_busy_percent" })
            {
                try
                {
                    if (double.TryParse(File.ReadAllText(Path.Combine(device, file)).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        highest = Math.Max(highest ?? 0, Math.Clamp(value, 0, 100));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not there on this kernel or GPU.
                }
            }
        }

        return highest;
    }

    /// <summary>The fields Medic uses from one GPU file's fdinfo.</summary>
    internal static DrmClient ParseFdinfo(string text)
    {
        string? driver = null, pdev = null, id = null;
        var busy = new Dictionary<string, double>();
        var totals = new Dictionary<string, double>();
        var capacity = new Dictionary<string, double>();
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string key = line[..colon];
            string value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "drm-driver": driver = value; continue;
                case "drm-pdev": pdev = value; continue;
                case "drm-client-id": id = value; continue;
            }

            // "drm-engine-enc: 175009915 ns", "drm-engine-capacity-video: 2", "drm-cycles-vcs: 4000", "drm-total-cycles-vcs: 14000".
            if (!double.TryParse(value.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            if (key.StartsWith("drm-engine-capacity-", StringComparison.Ordinal))
            {
                capacity[key["drm-engine-capacity-".Length..]] = number;
            }
            else if (key.StartsWith("drm-engine-", StringComparison.Ordinal))
            {
                busy[key["drm-engine-".Length..]] = number;
            }
            else if (key.StartsWith("drm-total-cycles-", StringComparison.Ordinal))
            {
                totals[key["drm-total-cycles-".Length..]] = number;
            }
            else if (key.StartsWith("drm-cycles-", StringComparison.Ordinal))
            {
                busy[key["drm-cycles-".Length..]] = number;
            }
        }

        var engines = busy.ToDictionary(
            e => e.Key,
            e => new Counter(e.Value, totals.GetValueOrDefault(e.Key), Math.Max(1, capacity.GetValueOrDefault(e.Key, 1))));
        // amdgpu leaves out an engine that hasn't run yet, so a client of these drivers with no engine lines is idle,
        // not unmeasured. Other drivers (NVIDIA's among them) give no engine figures at all.
        bool measured = engines.Count > 0 || (id is not null && driver is "amdgpu" or "i915" or "xe");
        return new DrmClient(pdev, id, engines, measured);
    }

    /// <summary>The highest of GPU, encoder and decoder use over the NVIDIA GPUs this container sees.</summary>
    private double? Nvidia()
    {
        if (DateTime.UtcNow < _nvidiaRetryUtc)
        {
            return null;
        }

        try
        {
            string? output = _nvidiaSmi(_nvidiaQuery);
            if (output is null)
            {
                _nvidiaRetryUtc = DateTime.UtcNow.AddMinutes(5); // hung: don't start another one every 15 seconds
                return null;
            }

            if (ParseNvidia(output) is { } value)
            {
                return value;
            }

            if (_nvidiaQuery == FullNvidiaQuery)
            {
                _nvidiaQuery = PlainNvidiaQuery;
                return Nvidia();
            }

            _nvidiaRetryUtc = DateTime.UtcNow.AddMinutes(5); // an answer that isn't a number won't change by itself
            return null;
        }
        catch
        {
            _nvidiaRetryUtc = DateTime.UtcNow.AddMinutes(30); // no nvidia-smi in this container
            return null;
        }
    }

    /// <summary>The highest number in nvidia-smi's CSV, one line per GPU; "[N/A]" fields are skipped. Null without any.</summary>
    internal static double? ParseNvidia(string output)
    {
        double? highest = null;
        foreach (string field in output.Split('\n', ','))
        {
            if (double.TryParse(field.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                highest = Math.Max(highest ?? 0, Math.Clamp(value, 0, 100));
            }
        }

        return highest;
    }

    private static string? RunNvidiaSmi(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("nvidia-smi", arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (p is null || !p.WaitForExit(3000))
        {
            p?.Kill(entireProcessTree: true);
            return null;
        }

        return p.StandardOutput.ReadToEnd();
    }

    /// <summary>One engine's counter: busy time in ns (or cycles, with Total the elapsed cycles; 0 for ns) and its unit count.</summary>
    internal readonly record struct Counter(double Busy, double Total, double Capacity);

    /// <summary>One DRM client. Measured is false when its driver gives no per-engine figures.</summary>
    internal sealed record DrmClient(string? Pdev, string? Id, Dictionary<string, Counter> Engines, bool Measured);
}
