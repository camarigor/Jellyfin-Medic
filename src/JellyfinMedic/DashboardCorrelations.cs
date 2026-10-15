using System.Globalization;
using MediaBrowser.Model.Tasks;
using JellyfinMedic.Services;

namespace JellyfinMedic.Api;

/// <summary>
/// Puts side by side what Medic records in separate places, for the Dashboard. Pure functions on plain
/// inputs, so they're tested without a server; times are turned into server local time with the zone given.
/// </summary>
internal static class DashboardCorrelations
{
    // The rule DiagnosticsEngine uses for "Tasks run while people are usually watching".
    internal const double BusyAverageStreams = 1.0;

    // A planned run's status.
    internal const string Ran = "ran";
    internal const string Late = "late";
    internal const string NotRun = "not run";
    internal const string Waiting = "waiting";

    private const int MinutesInDay = 24 * 60;

    // A run that starts up to this much before its planned time still belongs to it.
    private const int EarlyMinutes = 15;

    // Started more than this after its planned time: late.
    private const int LateMinutes = 30;

    // No run this long after its planned time: not run.
    private const int NotRunMinutes = 60;

    // Each transcode reason the library can explain: the media report's count that causes it.
    private static readonly Dictionary<string, (string Cause, Func<CompatibilityCounts, int> Count)> Causes = new(StringComparer.Ordinal)
    {
        ["AudioCodecNotSupported"] = (nameof(CompatibilityCounts.DtsOrTrueHdOnly), c => c.DtsOrTrueHdOnly),
        ["SubtitleCodecNotSupported"] = (nameof(CompatibilityCounts.ImageSubtitles), c => c.ImageSubtitles),
        ["ContainerNotSupported"] = (nameof(CompatibilityCounts.OldContainers), c => c.OldContainers),
        ["VideoRangeTypeNotSupported"] = (nameof(CompatibilityCounts.Hdr), c => c.Hdr)
    };

    /// <summary>
    /// Viewing by hour of the week next to the task runs of the last 7 days. An hour is busy once the
    /// profile is ready and people watch at least one stream there on average.
    /// </summary>
    internal static BusyHoursSection BusyHours(UsageSummary usage, UsageProfile profile, IEnumerable<RunRecord> runs, DateTime nowUtc, TimeZoneInfo zone)
    {
        var hours = new BusyHour[7 * 24];
        for (int i = 0; i < hours.Length; i++)
        {
            double streams = usage.AverageStreams.ElementAtOrDefault(i);
            var bucket = profile.Buckets.ElementAtOrDefault(i);
            hours[i] = new BusyHour
            {
                AverageStreams = Math.Round(streams, 2),
                AverageTranscodes = bucket is { Samples: > 0 } ? Math.Round((double)bucket.TranscodeSum / bucket.Samples, 2) : 0,
                Busy = usage.Ready && streams >= BusyAverageStreams
            };
        }

        var section = new BusyHoursSection { Ready = usage.Ready, HoursRecorded = Math.Round(usage.HoursCollected, 1) };
        var now = ScheduleStorage.AsUtc(nowUtc);
        var since = now.AddDays(-7);
        foreach (var hour in runs
                     .Select(r => (r.TaskName, Start: ScheduleStorage.AsUtc(r.StartUtc)))
                     .Where(r => r.Start >= since && r.Start <= now)
                     .GroupBy(r => HourOfWeek(r.Start, zone)))
        {
            var cell = hours[hour.Key];
            cell.Runs = hour.GroupBy(r => r.TaskName, StringComparer.Ordinal)
                .Select(g => new HourRuns { TaskName = g.Key, Count = g.Count() })
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.TaskName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (cell.Busy)
            {
                section.RunsInBusyHours += hour.Count();
            }
        }

        section.Days = Enumerable.Range(0, 7).Select(d => hours.Skip(d * 24).Take(24).ToList()).ToList();
        return section;
    }

    // Monday 00:00 is 0, the order the usage profile uses.
    private static int HourOfWeek(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        return ScheduleStorage.MondayFirst(local.DayOfWeek) * 24 + local.Hour;
    }

    /// <summary>
    /// Today's planned runs next to the runs that happened (see <see cref="Match"/>), with the day's resource
    /// spikes and LoadGuard actions, each placed by server local minute. Tasks that run many times a day have
    /// no planned time and stay out of the plan.
    /// </summary>
    internal static TodaySection Today(DateTime nowUtc, TimeZoneInfo zone, IEnumerable<CalendarRun> planned, IEnumerable<RunRecord> runs, IEnumerable<ResourceEvent> spikes, IEnumerable<LoadGuardEvent> guard)
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(ScheduleStorage.AsUtc(nowUtc), zone);
        var today = now.Date;
        int MinuteOf(DateTime utc) => (int)Math.Floor((TimeZoneInfo.ConvertTimeFromUtc(ScheduleStorage.AsUtc(utc), zone) - today).TotalMinutes);
        static bool IsToday(int minute) => minute is >= 0 and < MinutesInDay;

        var section = new TodaySection
        {
            Date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            NowMinutes = (int)now.TimeOfDay.TotalMinutes,
            Planned = planned
                .Where(p => p.StartMinutes >= 0)
                .DistinctBy(p => (p.TaskId, p.StartMinutes))
                .OrderBy(p => p.StartMinutes)
                .Select(p => new PlannedRun { TaskId = p.TaskId, TaskName = p.TaskName, StartMinutes = p.StartMinutes, DurationMinutes = p.DurationMinutes, Category = p.Category, Kind = p.Kind })
                .ToList(),
            Actual = runs
                .Select(r => (Run: r, Start: MinuteOf(r.StartUtc)))
                .Where(r => IsToday(r.Start))
                .OrderBy(r => r.Start)
                .Select(r => new ActualRun
                {
                    TaskId = r.Run.TaskId,
                    TaskName = r.Run.TaskName,
                    StartMinutes = r.Start,
                    EndMinutes = Math.Clamp(MinuteOf(r.Run.EndUtc), r.Start, MinutesInDay),
                    Status = r.Run.Status,
                    ErrorMessage = r.Run.ErrorMessage
                })
                .ToList(),
            Spikes = spikes
                .Select(e => (Event: e, Minute: MinuteOf(e.TimeUtc)))
                .Where(e => IsToday(e.Minute))
                .OrderBy(e => e.Minute)
                .Select(e => new SpikeMark { Minute = e.Minute, Resource = e.Event.Resource, Level = e.Event.Level })
                .ToList(),
            Guard = guard
                .Select(e => (Event: e, Minute: MinuteOf(e.TimestampUtc)))
                .Where(e => IsToday(e.Minute))
                .OrderBy(e => e.Minute)
                .Select(e => new GuardMark { Minute = e.Minute, Action = e.Event.Action, TaskName = e.Event.TaskName })
                .ToList()
        };

        Match(section.Planned, section.Actual, section.NowMinutes);
        section.Summary = new TodaySummary
        {
            Planned = section.Planned.Count,
            Ran = section.Actual.Count,
            Completed = section.Actual.Count(r => r.Status == nameof(TaskCompletionStatus.Completed)),
            Failed = section.Actual.Count(r => r.Status == nameof(TaskCompletionStatus.Failed)),
            Stopped = section.Actual.Count(r => r.Status == nameof(TaskCompletionStatus.Aborted)),
            Cancelled = section.Actual.Count(r => r.Status == nameof(TaskCompletionStatus.Cancelled)),
            NotRun = section.Planned.Count(p => p.Status == NotRun),
            Late = section.Planned.Count(p => p.Status == Late)
        };
        return section;
    }

    /// <summary>
    /// Gives each planned run its status. A planned run takes the first unclaimed run of the same task that
    /// started from 15 minutes before it until 15 minutes before that task's next planned run (or the end of
    /// the day): late when that run started more than 30 minutes after the planned time, ran otherwise.
    /// Without a run it's not run once an hour has passed, and waiting until then.
    /// </summary>
    private static void Match(List<PlannedRun> planned, List<ActualRun> actual, int nowMinutes)
    {
        var claimed = new HashSet<ActualRun>();
        foreach (var task in planned.GroupBy(p => p.TaskId, StringComparer.Ordinal))
        {
            var times = task.OrderBy(p => p.StartMinutes).ToList();
            for (int i = 0; i < times.Count; i++)
            {
                var plan = times[i];
                int from = plan.StartMinutes - EarlyMinutes;
                int until = i + 1 < times.Count ? times[i + 1].StartMinutes - EarlyMinutes : MinutesInDay;
                var run = actual.FirstOrDefault(a => a.TaskId == plan.TaskId && !claimed.Contains(a) && a.StartMinutes >= from && a.StartMinutes < until);
                if (run is not null)
                {
                    claimed.Add(run);
                    plan.Status = run.StartMinutes - plan.StartMinutes > LateMinutes ? Late : Ran;
                }
                else
                {
                    plan.Status = nowMinutes >= plan.StartMinutes + NotRunMinutes ? NotRun : Waiting;
                }
            }
        }
    }

    /// <summary>
    /// Why files are transcoded, as shares of all samples, each reason paired with what in the library causes
    /// it and how many files the latest media report counted with it (for the whole server).
    /// </summary>
    internal static TranscodesSection Transcodes(TranscodeLogView log, MediaReportResult? report) => new()
    {
        Since = log.SinceUtc,
        Samples = log.Samples,
        Reasons = log.ReasonTotals
            .OrderByDescending(r => r.Value)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r =>
            {
                bool known = Causes.TryGetValue(r.Key, out var cause);
                return new TranscodeReason
                {
                    Reason = r.Key,
                    Share = log.Samples > 0 ? Math.Round(r.Value * 100.0 / log.Samples, 1) : 0,
                    LibraryCause = known ? cause.Cause : null,
                    LibraryCount = known && report is not null ? cause.Count(report.Compatibility) : null
                };
            })
            .ToList(),
        Top = log.Top.Take(5).Select(e => new TranscodeTitle
        {
            Title = e.Title,
            Samples = e.Samples,
            Reasons = e.Reasons.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Key).ToList()
        }).ToList(),
        MediaReportUtc = report?.GeneratedUtc
    };

    /// <summary>
    /// Each drive in the disk history: free and total space now (<paramref name="space"/> reads a folder's
    /// drive; the last reading stands in when it can't), how fast it's filling and when it will be full (the
    /// rules StorageWatch and DriveStatus use), what it holds from its latest reading that recorded it, and its
    /// daily readings. Soonest full first, with the days Medic freed space.
    /// </summary>
    internal static DisksSection Disks(IEnumerable<DiskSample> history, Func<string, (double FreeGb, double TotalGb)?> space, IEnumerable<FreedDay> freed)
    {
        var drives = new List<DiskOutlook>();
        foreach (var drive in history.GroupBy(h => h.Key, StringComparer.Ordinal))
        {
            var days = drive.OrderBy(h => h.Day, StringComparer.Ordinal).ToList();
            var latest = days[^1];
            var now = space(latest.Path);
            var status = new DriveStatus { FreeGb = now?.FreeGb ?? latest.FreeGb, TotalGb = now?.TotalGb ?? 0, GbPerDay = StorageWatch.FillRate(days) };
            drives.Add(new DiskOutlook
            {
                Key = drive.Key,
                Holds = days.LastOrDefault(h => h.Holds.Length > 0)?.Holds ?? string.Empty,
                FreeGb = status.FreeGb,
                TotalGb = status.TotalGb,
                GbPerDay = status.GbPerDay,
                DaysToFull = status.DaysToFull,
                Series = days.Select(h => new DiskPoint { Day = h.Day, FreeGb = h.FreeGb }).ToList()
            });
        }

        return new DisksSection
        {
            Drives = drives.OrderBy(d => d.DaysToFull ?? double.MaxValue).ThenBy(d => d.Holds, StringComparer.OrdinalIgnoreCase).ToList(),
            Freed = freed.OrderBy(f => f.Day, StringComparer.Ordinal).ToList()
        };
    }

    /// <summary>
    /// Remote streaming: the latest speed test, the limit set now, the most streams seen at once, and the speed
    /// test's own option for that many viewers. Its options cover 1 to 6 viewers, so more than 6 uses the option
    /// for 6 and none seen yet the option for 1.
    /// </summary>
    internal static NetworkSection Network(SpeedTestResult? test, long currentLimitBps, int mostStreams)
    {
        var section = new NetworkSection { CurrentLimitMbps = Math.Round(currentLimitBps / 1_000_000d, 1), MostStreams = mostStreams };
        if (test is null)
        {
            return section;
        }

        section.Test = new SpeedTestSummary
        {
            TestedUtc = test.TestedUtc,
            DownloadMbps = test.DownloadMbps,
            UploadMbps = test.UploadMbps,
            LatencyMs = test.LatencyMs,
            IptvHdStreams = test.IptvHdStreams
        };
        if (test.UploadMbps > 0)
        {
            var options = SpeedAdvice.Options(test.UploadMbps);
            int viewers = Math.Clamp(mostStreams, 1, options.Max(o => o.Viewers));
            section.Suggested = options.First(o => o.Viewers == viewers);
        }

        return section;
    }
}
