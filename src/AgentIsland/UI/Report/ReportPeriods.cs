using AgentIsland.Core;
using AgentIsland.Core.Cost;
using AgentIsland.Backend.Cost;
using AgentIsland.Backend.Settings;
using AgentIsland.UI.Providers;

namespace AgentIsland.UI.Report;

/// Period math + slice loading shared by the weekly and monthly report
/// windows' ← → pagers. Offset 0 is the current period (the live card);
/// positive offsets step back in time. The right paging bound is the
/// current period; the left bound is the earliest day with any scanned
/// token activity.
public static class ReportPeriods
{
    /// Earliest day with any recorded token activity across ALL providers.
    /// Null when no history has been scanned yet (paging stays disabled).
    /// Also null in demo mode: past pages assemble from a REAL log scan, and
    /// demo exists precisely to keep real usage off screen recordings.
    public static DateTime? EarliestDataDay(ICostStore? costStore = null)
    {
        if (AppEnvironment.IsDemo) return null;
        var cost = costStore ?? (App.Instance?.Services?.GetService(typeof(ICostStore)) as ICostStore) ?? new CostStore();
        DateTime? earliest = null;
        foreach (var provider in DisplayProviders.All)
        {
            foreach (var bucket in cost.Summary(provider).DailyHistory)
            {
                if (bucket.Tokens <= 0) continue;
                var day = bucket.DayStart.Date;
                if (earliest is null || day < earliest) earliest = day;
            }
        }
        return earliest;
    }

    /// The 7-day block `offset` weeks behind the current card, half-open
    /// [start, end). Offset 0 reproduces the live card's window — anchored
    /// to the freshest SCANNED day, same as WeeklyReportData.Current() —
    /// so older pages tile exactly against what the current card shows.
    public static (DateTime Start, DateTime End) WeekInterval(int offset, ICostStore? costStore = null)
    {
        var anchor = ScanAnchor(costStore);
        var end = anchor.AddDays(1 - 7 * offset);
        var start = end.AddDays(-7);
        return (start, end);
    }

    /// The calendar month `offset` months behind today, half-open [start, end).
    /// Offset 0 is this month, 1 is last month, etc.
    public static (DateTime Start, DateTime End) MonthInterval(int offset)
    {
        var today = DateTime.Today;
        var targetMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-offset);
        var start = targetMonth;
        var end = targetMonth.AddMonths(1);
        return (start, end);
    }

    /// Freshest scanned day across all providers, clamped to today — the
    /// weekly window's right edge (macOS: min(scanAnchor, today)).
    public static DateTime ScanAnchor(ICostStore? costStore = null)
    {
        var today = DateTime.Today;
        var cost = costStore ?? (App.Instance?.Services?.GetService(typeof(ICostStore)) as ICostStore) ?? new CostStore();
        DateTime? scanned = null;
        foreach (var provider in DisplayProviders.All)
        {
            var history = cost.Summary(provider).DailyHistory;
            if (history.Count == 0) continue;
            var day = history[^1].DayStart.Date;
            if (scanned is null || day > scanned) scanned = day;
        }
        return scanned is { } anchor && anchor < today ? anchor : today;
    }

    /// Whether one page older than `intervalStart` still overlaps recorded
    /// history — the ← button's enablement.
    public static bool HasData(DateTime intervalStart, DateTime? earliestDataDay) =>
        earliestDataDay is { } earliest && intervalStart > earliest;

    public static DateTimeOffset AtLocalBoundary(DateTime date, TimeZoneInfo timeZone)
    {
        var wallTime = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        return new DateTimeOffset(wallTime, timeZone.GetUtcOffset(wallTime));
    }


    /// Full-year rescan → per-provider slices for the interval, off the UI
    /// thread. The readers memoize per file (LogParseCache), so the
    /// steady-state cost is a cache walk + dedup pass, not a re-parse —
    /// cheap enough to run per page flip. Never touches CostStore. It uses a
    /// snapshot of the enabled set, so a report cannot wake disabled-agent
    /// readers or make the zero-agent state perform a hidden full scan.
    public static Task<Dictionary<DisplayProvider, ReportSlice>> SlicesAsync(
        DateTime start,
        DateTime end,
        IProviderVisibilityStore? visibilityStore = null,
        ICostQueryService? costQueryService = null,
        CancellationToken cancellationToken = default)
    {
        var lookback = CostSummarizer.YearHistoryDays(DateTimeOffset.Now);
        var startOffset = AtLocalBoundary(start, TimeZoneInfo.Local);
        var endOffset = AtLocalBoundary(end, TimeZoneInfo.Local);
        var visibility = visibilityStore ?? (App.Instance?.Services?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore) ?? new ProviderVisibilityStore();
        var queryService = costQueryService ?? (App.Instance?.Services?.GetService(typeof(ICostQueryService)) as ICostQueryService) ?? new CostQueryService(visibility);
        var providers = visibility.Enabled.ToArray();
        return Task.Run(async () =>
        {
            ReportSlice Slice(IReadOnlyList<TokenEvent> events) =>
                CostSummarizer.Slice(events, startOffset, endOffset);

            cancellationToken.ThrowIfCancellationRequested();
            var scans = providers.ToDictionary(
                provider => provider,
                provider => queryService.ScanAsync(
                    provider, lookback, DateTimeOffset.Now, cancellationToken));

            var output = new Dictionary<DisplayProvider, ReportSlice>();
            foreach (var provider in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CostScanResult scan;
                try
                {
                    scan = await scans[provider].ConfigureAwait(false);
                }
                catch (CostQueryService.ProviderDisabledException)
                {
                    continue;
                }
                if (!queryService.IsCurrent(provider, scan.ProviderVersion)) continue;
                output[provider] = Slice(scan.Events);
            }
            return output;
        }, cancellationToken);
    }
}
