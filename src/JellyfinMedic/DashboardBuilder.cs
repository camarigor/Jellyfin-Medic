using JellyfinMedic.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyfinMedic.Api;

/// <summary>
/// Builds the Dashboard's data from Medic's stores. Each section is built on its own: one that fails is left
/// out with a short message in Errors, and the rest still arrive. Never runs the checks behind /Report, which
/// write files.
/// </summary>
internal sealed class DashboardBuilder
{
    // Long enough for a second tab or a quick reload to share one build, short enough for "Today" to stay current.
    private static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim BuildLock = new(1, 1);
    private static Built? _built;

    private readonly IServerConfigurationManager _config;
    private readonly IApplicationPaths _paths;
    private readonly ITaskManager _tasks;
    private readonly ISessionManager _sessions;
    private readonly IUserManager _users;
    private readonly IInstallationManager _installs;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    internal DashboardBuilder(
        IServerConfigurationManager config,
        IApplicationPaths paths,
        ITaskManager tasks,
        ISessionManager sessions,
        IUserManager users,
        IInstallationManager installs,
        IServiceProvider services,
        ILogger logger)
    {
        _config = config;
        _paths = paths;
        _tasks = tasks;
        _sessions = sessions;
        _users = users;
        _installs = installs;
        _services = services;
        _logger = logger;
    }

    /// <summary>The slower data, built at most once a minute however many pages ask.</summary>
    internal async Task<DashboardData> DataAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _built) is { } fresh && DateTime.UtcNow - fresh.At < KeepFor)
        {
            return fresh.Data;
        }

        await BuildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _built) is { } built && DateTime.UtcNow - built.At < KeepFor)
            {
                return built.Data;
            }

            var data = await BuildAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _built, new Built(DateTime.UtcNow, data));
            return data;
        }
        finally
        {
            BuildLock.Release();
        }
    }

    /// <summary>What's happening now: cheap, built on every call.</summary>
    internal DashboardLive Live() => new()
    {
        Now = ServerNow.Snapshot(_sessions, _tasks, _paths),
        Resources = ResourceMonitor.RecentReadings(),
        Limits = new ResourceLimits
        {
            CpuPercent = ResourceMonitor.CpuAlertPercent,
            MemoryPercent = ResourceMonitor.MemoryAlertPercent,
            DiskMBps = ResourceMonitor.DiskAlertMBps,
            GpuPercent = ResourceMonitor.GpuAlertPercent
        },
        Paused = LoadGuard.Paused.ToList()
    };

    /// <summary>Builds one section; a failure leaves it null with a short message in Errors, and is logged.</summary>
    internal static void Section(DashboardData data, string name, ILogger logger, Action build)
    {
        try
        {
            build();
        }
        catch (Exception ex)
        {
            Failed(data, name, logger, ex);
        }
    }

    private static void Failed(DashboardData data, string name, ILogger logger, Exception ex)
    {
        data.Errors[name] = "Couldn't be read (" + ex.GetType().Name + "). The Jellyfin log has the details.";
        logger.LogWarning(ex, "Jellyfin Medic: the Dashboard's {Section} couldn't be built.", name);
    }

    private async Task<DashboardData> BuildAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var zone = TimeZoneInfo.Local;
        var now = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var data = new DashboardData { GeneratedUtc = nowUtc, UtcOffsetMinutes = (int)zone.GetUtcOffset(nowUtc).TotalMinutes };
        var runs = new Lazy<List<RunRecord>>(ReadRuns);

        Section(data, nameof(DashboardData.BusyHours), _logger, () =>
        {
            var profile = UsageStore.Load(_paths);
            data.BusyHours = DashboardCorrelations.BusyHours(UsageAnalyzer.Summarise(profile), profile, runs.Value, nowUtc, zone);
        });
        Section(data, nameof(DashboardData.Today), _logger, () =>
        {
            var planned = ScheduleCalendar.PlannedRuns(_tasks.ScheduledTasks.Where(ScheduleStorage.IsVisible), ScheduleStorage.LoadProfile(_paths), ScheduleStorage.LoadManaged(_paths), now.Date, now);
            data.Today = DashboardCorrelations.Today(nowUtc, zone, planned, runs.Value, ResourceLog.Load(_paths), LoadGuardLog.Load(_paths));
        });
        Section(data, nameof(DashboardData.Transcodes), _logger, () =>
        {
            var media = MediaReport.Status(_paths);
            data.Transcodes = DashboardCorrelations.Transcodes(media.Transcodes, media.Report);
        });
        Section(data, nameof(DashboardData.Disks), _logger, () =>
            data.Disks = DashboardCorrelations.Disks(StorageWatch.History(_paths.PluginConfigurationsPath), SystemProbe.Space, WeeklyLedger.FreedDays(now.Date.AddDays(-30))));
        Section(data, nameof(DashboardData.Network), _logger, () =>
            data.Network = DashboardCorrelations.Network(SpeedStore.Load(_paths), _config.Configuration.RemoteClientBitrateLimit, UsageAnalyzer.Summarise(UsageStore.Load(_paths)).MaxStreams));
        Section(data, nameof(DashboardData.RepeatedErrors), _logger, () =>
            data.RepeatedErrors = LogScanner.Scan(_paths.LogDirectoryPath, _paths.PluginConfigurationsPath).Take(5).ToList());
        Section(data, nameof(DashboardData.People), _logger, () => data.People = People());

        try
        {
            data.Updates = await UpdateChecker.AvailableAsync(_installs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed(data, nameof(DashboardData.Updates), _logger, ex);
        }

        return data;
    }

    private List<RunRecord> ReadRuns()
    {
        lock (ScheduleStorage.SyncRoot)
        {
            return ScheduleStorage.ReadList<RunRecord>(ScheduleStorage.RunHistoryPath(_paths));
        }
    }

    private PeopleSection People()
    {
        var settings = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var (summary, _) = UserAuditor.Audit(_users, settings.InactiveUserDays);
        return new PeopleSection
        {
            Total = summary.Total,
            AdminNames = summary.AdminNames,
            Inactive = summary.Inactive,
            NoPassword = summary.NoPassword,
            Roaming = SecurityAuditor.RoamingAccounts(SecurityAuditor.ResolveActivityManager(_services), UserList.All(_users))
        };
    }

    private sealed record Built(DateTime At, DashboardData Data);
}
