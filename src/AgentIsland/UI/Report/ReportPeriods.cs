using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Model;

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
    public static DateTime? EarliestDataDay()
    {
        if (AppEnvironment.IsDemo) return null;
        var cost = CostStore.Shared;
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
    public static (DateTime Start, DateTime End) WeekInterval(int offset)
    {
        var endDay = ScanAnchor().AddDays(-7 * offset);
        var startDay = endDay.AddDays(-6);
        return (startDay, endDay.AddDays(1));
    }

    /// The calendar month `offset` months behind the current one, half-open
    /// [monthStart, nextMonthStart). The current month (offset 0) naturally
    /// reads month-to-date — future days simply hold no events yet.
    public static (DateTime Start, DateTime End) MonthInterval(int offset)
    {
        var now = DateTime.Today;
        var thisMonthStart = new DateTime(now.Year, now.Month, 1);
        var start = thisMonthStart.AddMonths(-offset);
        return (start, start.AddMonths(1));
    }

    /// Freshest scanned day across all providers, clamped to today — the
    /// weekly window's right edge (macOS: min(scanAnchor, today)).
    public static DateTime ScanAnchor()
    {
        var today = DateTime.Today;
        var cost = CostStore.Shared;
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

    /// Full-year rescan → per-provider slices for the interval, off the UI
    /// thread. The readers memoize per file (LogParseCache), so the
    /// steady-state cost is a cache walk + dedup pass, not a re-parse —
    /// cheap enough to run per page flip. Never touches CostStore. Scans
    /// ALL five providers so a Grok-or-Cursor-only past period still fills
    /// the report card.
    public static Task<Dictionary<DisplayProvider, ReportSlice>> SlicesAsync(DateTime start, DateTime end)
    {
        var lookback = CostSummarizer.YearHistoryDays(DateTimeOffset.Now);
        var startOffset = new DateTimeOffset(start, DateTimeOffset.Now.Offset);
        var endOffset = new DateTimeOffset(end, DateTimeOffset.Now.Offset);
        return Task.Run(() =>
        {
            ReportSlice Slice(IReadOnlyList<TokenEvent> events) =>
                CostSummarizer.Slice(events, startOffset, endOffset);
            return new Dictionary<DisplayProvider, ReportSlice>
            {
                [DisplayProvider.Claude] = Slice(ClaudeLogReader.Scan(lookback)),
                [DisplayProvider.Codex] = Slice(CodexLogReader.Scan(lookback)),
                [DisplayProvider.Antigravity] = Slice(AntigravityLogReader.Scan(lookback)),
                [DisplayProvider.Grok] = Slice(GrokLogReader.Scan(lookback)),
                [DisplayProvider.Cursor] = Slice(CursorLogReader.Scan(lookback)),
            };
        });
    }
}
