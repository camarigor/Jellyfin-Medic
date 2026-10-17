using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>One reading of what Jellyfin itself (plus the FFmpeg processes it starts) is using.</summary>
public sealed class ResourceSample
{
    public DateTime TimeUtc { get; set; }

    // Share of all CPU threads, Jellyfin and its FFmpeg processes together.
    public double CpuPercent { get; set; }

    public double MemoryGb { get; set; }

    public double MemoryPercent { get; set; }

    // The whole server's memory in use, other programs included: the figure LoadGuard's server check acts on. Null off Linux.
    public double? ServerMemoryPercent { get; set; }

    // Disk reads and writes by Jellyfin and FFmpeg, in MB a second. Null where it can't be read.
    public double? DiskMBps { get; set; }

    // The busiest GPU engine, video engines included. Null when there's no GPU Jellyfin was given, or its load
    // can't be read.
    public double? GpuPercent { get; set; }

    // True when GpuPercent is the whole GPU, every program using it, because Jellyfin's own share can't be read
    // (NVIDIA, or an older kernel). Such a figure never raises a heavy-use event, which would blame Jellyfin's tasks.
    public bool GpuWholeDevice { get; set; }

    public int FfmpegProcesses { get; set; }
}

/// <summary>A spell of heavy use, and what was running at the time.</summary>
public sealed class ResourceEvent
{
    public DateTime TimeUtc { get; set; }

    // "CPU", "Memory", "Disk" or "GPU".
    public string Resource { get; set; } = string.Empty;

    public string Level { get; set; } = string.Empty;

    public List<string> Causes { get; set; } = new();
}

/// <summary>
/// Every 15 seconds, measures Jellyfin's own use of the CPU, memory, disk and GPU, counting the FFmpeg
/// processes it starts but nothing else on the machine. When a resource stays high for 45 seconds it
/// records what was running: scheduled tasks, transcodes and other FFmpeg work (such as trickplay or
/// chapter images). Work a plugin does outside a scheduled task can't be told apart from the rest of
/// Jellyfin, as all plugins run in Jellyfin's own process.
/// </summary>
public sealed class ResourceMonitor : IHostedService, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(15);
    private const int SustainedSamples = 3;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);
    private static readonly object Sync = new();
    private static readonly Queue<ResourceSample> Recent = new();

    // About 10 minutes of readings, one every 15 seconds.
    private const int KeptReadings = 40;

    // Heavy use, once it lasts 45 seconds. The Dashboard draws these as its alert lines.
    internal const double CpuAlertPercent = 90;
    internal const double MemoryAlertPercent = 90;
    internal const double DiskAlertMBps = 150;
    internal const double GpuAlertPercent = 95;

    private readonly ITaskManager _tasks;
    private readonly ISessionManager _sessions;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<ResourceMonitor> _logger;
    private readonly Dictionary<string, int> _highFor = new();
    private readonly Dictionary<string, DateTime> _lastEvent = new();
    private readonly GpuLoad _gpu = new();
    private Timer? _timer;
    private (DateTime Wall, double CpuSeconds, double IoBytes)? _previous;
    private int _busy;

    // Whether the last measure failed, so a failure that repeats every 15 seconds is a warning once.
    private bool _failing;

    public ResourceMonitor(ITaskManager tasks, ISessionManager sessions, IApplicationPaths paths, ILogger<ResourceMonitor> logger)
    {
        _tasks = tasks;
        _sessions = sessions;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>The latest reading, or null before the first one.</summary>
    public static ResourceSample? Latest
    {
        get
        {
            lock (Sync)
            {
                return Recent.LastOrDefault();
            }
        }
    }

    /// <summary>Adds a reading, keeping the last 40.</summary>
    internal static void Remember(ResourceSample sample)
    {
        lock (Sync)
        {
            Recent.Enqueue(sample);
            while (Recent.Count > KeptReadings)
            {
                Recent.Dequeue();
            }
        }
    }

    /// <summary>The readings of about the last 10 minutes, oldest first. A reading isn't changed once taken.</summary>
    internal static List<ResourceSample> RecentReadings()
    {
        lock (Sync)
        {
            return Recent.ToList();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Tick(), null, Every, Every);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var sample = Measure(out var ffmpeg);
            if (sample is not null)
            {
                Remember(sample);

                Watch("CPU", sample.CpuPercent >= CpuAlertPercent, $"{sample.CpuPercent:0}% of all CPU threads", ffmpeg);
                Watch("Memory", sample.MemoryPercent >= MemoryAlertPercent, $"{sample.MemoryGb:0.#} GB ({sample.MemoryPercent:0}% of what Jellyfin may use)", ffmpeg);
                Watch("Disk", sample.DiskMBps >= DiskAlertMBps, $"{sample.DiskMBps:0} MB/s of reads and writes", ffmpeg);
                Watch("GPU", GpuHigh(sample), $"{sample.GpuPercent:0}% GPU", ffmpeg);
            }

            if (_failing)
            {
                _failing = false;
                _logger.LogInformation("Jellyfin Medic: measuring resource use works again.");
            }
        }
        catch (Exception ex)
        {
            if (!_failing)
            {
                _failing = true;
                _logger.LogWarning(ex, "Jellyfin Medic: couldn't measure resource use; Medic keeps trying every 15 seconds.");
            }
            else
            {
                _logger.LogDebug(ex, "Jellyfin Medic: still couldn't measure resource use.");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void Watch(string resource, bool high, string level, List<(int Pid, string Role, double CpuSeconds)> ffmpeg)
    {
        _highFor[resource] = high ? _highFor.GetValueOrDefault(resource) + 1 : 0;
        var now = DateTime.UtcNow;
        if (!ShouldRecord(_highFor[resource], _lastEvent.TryGetValue(resource, out var last) ? last : null, now))
        {
            return;
        }

        _lastEvent[resource] = now;
        var entry = new ResourceEvent { TimeUtc = now, Resource = resource, Level = level, Causes = Causes(ffmpeg) };
        ResourceLog.Add(_paths, entry);
        _logger.LogInformation("Jellyfin Medic: high {Resource} use ({Level}). Running: {Causes}", resource, level, string.Join("; ", entry.Causes));
    }

    /// <summary>Whether a reading is heavy GPU use by Jellyfin. The whole GPU's figure may be another program's, so it never is.</summary>
    internal static bool GpuHigh(ResourceSample sample) => !sample.GpuWholeDevice && sample.GpuPercent >= GpuAlertPercent;

    /// <summary>
    /// Whether a spell of heavy use that has lasted <paramref name="highFor"/> readings becomes an event: once it
    /// reaches 3 readings, and at least 15 minutes after the last event for the same resource. A spell that reaches 3
    /// readings during those 15 minutes is recorded when they're over, if it's still going, rather than never.
    /// </summary>
    internal static bool ShouldRecord(int highFor, DateTime? lastEventUtc, DateTime nowUtc) =>
        highFor >= SustainedSamples && (lastEventUtc is not { } last || nowUtc - last >= Cooldown);

    private List<string> Causes(List<(int Pid, string Role, double CpuSeconds)> ffmpeg)
    {
        var causes = new List<string>();
        foreach (var t in _tasks.ScheduledTasks.Where(t => t.State == TaskState.Running))
        {
            causes.Add("Scheduled task: " + t.Name + (t.CurrentProgress is double p ? $" ({p:0}%)" : string.Empty));
        }

        try
        {
            var playing = _sessions.Sessions.Where(s => s.NowPlayingItem is not null).ToList();
            int transcoding = playing.Count(s => s.PlayState?.PlayMethod?.ToString() == "Transcode");
            if (transcoding > 0)
            {
                causes.Add($"{transcoding} transcode{(transcoding == 1 ? string.Empty : "s")}: " +
                    string.Join(", ", playing.Where(s => s.PlayState?.PlayMethod?.ToString() == "Transcode").Take(4).Select(s => s.NowPlayingItem!.Name)));
            }
            else if (playing.Count > 0)
            {
                causes.Add($"{playing.Count} playing directly (no transcode)");
            }
        }
        catch
        {
            // Sessions unavailable.
        }

        foreach (var group in ffmpeg.Where(f => f.Role != "transcode").GroupBy(f => f.Role))
        {
            causes.Add($"FFmpeg {group.Key}: {group.Count()} process{(group.Count() == 1 ? string.Empty : "es")}");
        }

        if (causes.Count == 0)
        {
            causes.Add("No task, transcode or FFmpeg job was running: most likely a plugin or a library change being processed");
        }

        return causes;
    }

    // ---------- Measuring ----------

    private ResourceSample? Measure(out List<(int Pid, string Role, double CpuSeconds)> ffmpeg)
    {
        ffmpeg = new List<(int, string, double)>();
        var self = Process.GetCurrentProcess();
        double cpuSeconds;
        double memoryBytes = self.WorkingSet64;
        double? ioBytes = ReadIo(self.Id);
        var started = new List<int>();

        // Jellyfin's own line rather than TotalProcessorTime, which leaves out the FFmpeg processes it has reaped.
        if ((OperatingSystem.IsLinux() ? ReadStat(self.Id) : null) is { } own)
        {
            var descendants = new List<ProcessStat>();
            foreach (int pid in Children(self.Id))
            {
                var stat = ReadStat(pid);
                if (stat is null)
                {
                    continue;
                }

                started.Add(pid);

                descendants.Add(stat.Value);
                memoryBytes += stat.Value.RssBytes;
                if (ioBytes is not null && ReadIo(pid) is { } io)
                {
                    ioBytes += io;
                }

                if (FfmpegRole(stat.Value.Name, () => CommandLine(pid)) is { } role)
                {
                    ffmpeg.Add((pid, role, stat.Value.CpuSeconds));
                }
            }

            cpuSeconds = TreeCpuSeconds(own, descendants);
        }
        else
        {
            // Without Jellyfin's line in /proc, as on Windows and macOS, its own time is all that can be read.
            cpuSeconds = self.TotalProcessorTime.TotalSeconds;
        }

        var now = DateTime.UtcNow;
        // Read every time, the first included, as its load is the change since the last reading. Only the processes
        // Jellyfin started: Jellyfin itself opens no GPU file, and listing its hundreds of files every 15 s is waste.
        var gpu = _gpu.Read(started, now);
        var previous = _previous;
        _previous = (now, cpuSeconds, ioBytes ?? -1);
        if (previous is not { } prev)
        {
            return null; // the first reading only sets the starting point
        }

        double wall = (now - prev.Wall).TotalSeconds;
        double limit = MemoryLimitBytes();
        return new ResourceSample
        {
            TimeUtc = now,
            // A process handed to a parent outside Jellyfin, or reaped during this reading, can still make this dip; never below 0.
            CpuPercent = Math.Round(Math.Clamp((cpuSeconds - prev.CpuSeconds) / wall / Environment.ProcessorCount * 100, 0, 100), 1),
            MemoryGb = Math.Round(memoryBytes / 1073741824d, 2),
            MemoryPercent = limit > 0 ? Math.Round(Math.Clamp(memoryBytes / limit * 100, 0, 100), 1) : 0,
            ServerMemoryPercent = ServerMemory.UsedPercent(out _, out _),
            DiskMBps = ioBytes is { } io2 && prev.IoBytes >= 0 && io2 >= prev.IoBytes ? Math.Round((io2 - prev.IoBytes) / wall / 1048576d, 1) : null,
            GpuPercent = gpu?.Percent,
            GpuWholeDevice = gpu?.WholeDevice ?? false,
            FfmpegProcesses = ffmpeg.Count
        };
    }

    /// <summary>Every process started by Jellyfin, and theirs in turn, from /proc.</summary>
    private static List<int> Children(int root)
    {
        // Each process's children, gathered in one pass, so each descendant is a lookup rather than
        // another pass over every process.
        var children = new Dictionary<int, List<int>>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(dir), out int pid) && ReadStat(pid) is { } s)
            {
                (children.TryGetValue(s.ParentPid, out var list) ? list : children[s.ParentPid] = new List<int>()).Add(pid);
            }
        }

        var found = new List<int>();
        var queue = new Queue<int>(new[] { root });
        while (queue.Count > 0 && found.Count < 500)
        {
            int parent = queue.Dequeue();
            foreach (var child in children.TryGetValue(parent, out var list) ? list : (IEnumerable<int>)Array.Empty<int>())
            {
                found.Add(child);
                queue.Enqueue(child);
            }
        }

        return found;
    }

    /// <summary>
    /// CPU seconds used so far by Jellyfin and the processes it started. A process that exits leaves the tree, but once
    /// its parent reaps it Linux adds its time to the parent's own line, so the sum keeps it instead of dropping.
    /// </summary>
    internal static double TreeCpuSeconds(ProcessStat self, IEnumerable<ProcessStat> descendants) =>
        self.CpuSeconds + self.FinishedChildrenCpuSeconds + descendants.Sum(d => d.CpuSeconds + d.FinishedChildrenCpuSeconds);

    private static ProcessStat? ReadStat(int pid)
    {
        try
        {
            return ParseStat(File.ReadAllText($"/proc/{pid}/stat"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The fields Medic uses from a /proc/&lt;pid&gt;/stat line.</summary>
    internal static ProcessStat? ParseStat(string text)
    {
        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return null;
        }

        string name = text[(open + 1)..close];
        var f = text[(close + 2)..].Split(' ');
        // After the name: state(0) ppid(1) ... utime(11) stime(12) cutime(13) cstime(14) ... rss(21), in pages.
        const double Ticks = 100; // USER_HZ on every Linux Jellyfin runs on
        return new ProcessStat(name, int.Parse(f[1], CultureInfo.InvariantCulture),
            (double.Parse(f[11], CultureInfo.InvariantCulture) + double.Parse(f[12], CultureInfo.InvariantCulture)) / Ticks,
            (double.Parse(f[13], CultureInfo.InvariantCulture) + double.Parse(f[14], CultureInfo.InvariantCulture)) / Ticks,
            double.Parse(f[21], CultureInfo.InvariantCulture) * Environment.SystemPageSize);
    }

    private static double? ReadIo(int pid)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            double total = 0;
            foreach (var line in File.ReadLines($"/proc/{pid}/io"))
            {
                if (line.StartsWith("read_bytes:", StringComparison.Ordinal) || line.StartsWith("write_bytes:", StringComparison.Ordinal))
                {
                    total += double.Parse(line[(line.IndexOf(':') + 1)..].Trim(), CultureInfo.InvariantCulture);
                }
            }

            return total;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>What an FFmpeg process is doing, from its name and command line; null for any other process.</summary>
    internal static string? FfmpegRole(string name, Func<string> commandLine)
    {
        // Jellyfin probes media with FFprobe, never with FFmpeg.
        if (name.Contains("ffprobe", StringComparison.OrdinalIgnoreCase))
        {
            return "media probing";
        }

        if (!name.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Jellyfin's trickplay writes numbered images (-f image2 ".../%08d.jpg") into a temporary folder, and the word
        // "trickplay" is in none of its arguments.
        string cmd = commandLine();
        if ((cmd.Contains("-f image2", StringComparison.Ordinal) && cmd.Contains("%08d.jpg", StringComparison.Ordinal))
            || cmd.Contains("trickplay", StringComparison.OrdinalIgnoreCase))
        {
            return "trickplay images";
        }

        if (cmd.Contains("-f hls", StringComparison.Ordinal) || cmd.Contains("transcodes", StringComparison.OrdinalIgnoreCase))
        {
            return "transcode";
        }

        if (cmd.Contains("-vframes 1", StringComparison.Ordinal) || cmd.Contains("-frames:v 1", StringComparison.Ordinal))
        {
            return "chapter or thumbnail images";
        }

        return "other work";
    }

    // A process that has already exited reads as an empty command line.
    private static string CommandLine(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>What Jellyfin's memory is measured against on the Dashboard and in Checks, in bytes.</summary>
    internal static double MemoryLimitBytes() =>
        MemoryBase(LoadGuard.CgroupLimit(), HostCounters.MemoryKb(HostPlatform.Lines("/proc/meminfo"))?.Total * 1024d, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>
    /// The container's memory limit when it has one, else the server's memory. .NET's own available figure is the last
    /// resort only: under a container limit it is the heap limit, 75% of the container's, which nothing holds FFmpeg to.
    /// </summary>
    internal static double MemoryBase(double? cgroupLimit, double? hostTotal, double dotnetAvailable) =>
        cgroupLimit ?? (hostTotal is > 0 ? hostTotal.Value : dotnetAvailable);

    /// <summary>
    /// From a process's line in /proc/&lt;pid&gt;/stat: its own CPU time and that of the children it has reaped (cutime and
    /// cstime), in seconds, and its memory in bytes.
    /// </summary>
    internal readonly record struct ProcessStat(string Name, int ParentPid, double CpuSeconds, double FinishedChildrenCpuSeconds, double RssBytes);
}

/// <summary>Spells of heavy use, kept for 14 days (up to 200).</summary>
public static class ResourceLog
{
    private static readonly object Sync = new();

    private static string FilePath(IApplicationPaths paths) => Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic", "resource_events.json");

    public static void Add(IApplicationPaths paths, ResourceEvent entry)
    {
        lock (Sync)
        {
            var list = Load(paths);
            list.Insert(0, entry);
            list = list.Where(e => e.TimeUtc > DateTime.UtcNow.AddDays(-14)).Take(200).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath(paths))!);
            ScheduleStorage.WriteJson(FilePath(paths), list);
        }
    }

    public static List<ResourceEvent> Load(IApplicationPaths paths)
    {
        lock (Sync)
        {
            return ScheduleStorage.ReadList<ResourceEvent>(FilePath(paths));
        }
    }
}
