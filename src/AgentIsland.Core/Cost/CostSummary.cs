namespace AgentIsland.Cost;

public sealed record DailyTokenBucket(DateTimeOffset DayStart, long Tokens, long BillableTokens, double Dollars);

public sealed record ModelSpend(string Model, long Tokens, long BillableTokens, double Dollars);

/// One provider's rollups for the cost page and the overview grid.
public sealed record ProviderCostSummary(
    double TodayDollars,
    long TodayTokens,
    long TodayBillableTokens,
    double MonthDollars,
    long MonthTokens,
    long MonthBillableTokens,
    IReadOnlyList<double> TodayCumulativeDollars,     // 24 hourly points
    IReadOnlyList<double> MonthCumulativeDollars,     // one per day of month
    IReadOnlyList<ModelSpend> RecentModels,           // rolling 5h, billable desc
    IReadOnlyList<ModelSpend> WeeklyModels,           // rolling 7d, billable desc
    IReadOnlyList<ModelSpend> MonthModels,            // calendar month, billable desc
    IReadOnlyList<DailyTokenBucket> DailyHistory,     // Jan 1 -> today
    IReadOnlyList<string> UnknownModels)
{
    public static ProviderCostSummary Empty { get; } = new(
        0, 0, 0, 0, 0, 0,
        new double[24], Array.Empty<double>(),
        Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
        Array.Empty<DailyTokenBucket>(), Array.Empty<string>());
}

/// One report period's aggregation: per-day buckets, total dollars, and
/// per-model rows over an arbitrary half-open [start, end) interval. The
/// report pager feeds this from its own full-year reader scan (per-file
/// memoization makes the rescan cheap), fully independent of the fixed
/// windows Summarize computes for the live panel.
public sealed record ReportSlice(
    IReadOnlyList<DailyTokenBucket> DailyTokens,   // zero-filled, oldest first
    double Dollars,
    IReadOnlyList<ModelSpend> ByModel)
{
    public static ReportSlice Empty { get; } = new(
        Array.Empty<DailyTokenBucket>(), 0, Array.Empty<ModelSpend>());
}

/// Single-pass aggregation, calendar-local like the macOS CostSummary.
public static class CostSummarizer
{
    /// Aggregate events over [start, end) with the same accounting rules as
    /// Summarize: wire vs billable split, canonical model grouping,
    /// self-reported cost override.
    public static ReportSlice Slice(IReadOnlyList<TokenEvent> events, DateTimeOffset start, DateTimeOffset end)
    {
        var startDay = start.ToLocalTime().Date;
        var lastDay = end.ToLocalTime().AddSeconds(-1).Date;
        var dayCount = Math.Max(1, (int)(lastDay - startDay).TotalDays + 1);

        var tokenBuckets = new long[dayCount];
        var billableBuckets = new long[dayCount];
        double dollars = 0;
        var byModel = new Dictionary<string, (long Tokens, long Billable, double Dollars)>(StringComparer.OrdinalIgnoreCase);

        foreach (var tokenEvent in events)
        {
            var local = tokenEvent.Timestamp.ToLocalTime();
            if (local < start.ToLocalTime() || local >= end.ToLocalTime()) continue;
            var dayOffset = (int)(local.Date - startDay).TotalDays;
            if (dayOffset >= 0 && dayOffset < dayCount)
            {
                tokenBuckets[dayOffset] += tokenEvent.WireTokens;
                billableBuckets[dayOffset] += tokenEvent.BillableTokens;
            }
            dollars += tokenEvent.Dollars;
            var model = Pricing.CanonicalModelName(tokenEvent.Model);
            byModel.TryGetValue(model, out var entry);
            byModel[model] = (entry.Tokens + tokenEvent.WireTokens,
                entry.Billable + tokenEvent.BillableTokens,
                entry.Dollars + tokenEvent.Dollars);
        }

        var daily = new List<DailyTokenBucket>(dayCount);
        for (var i = 0; i < dayCount; i++)
        {
            var day = new DateTimeOffset(startDay.AddDays(i), DateTimeOffset.Now.Offset);
            daily.Add(new DailyTokenBucket(day, tokenBuckets[i], billableBuckets[i], 0));
        }

        return new ReportSlice(
            daily,
            dollars,
            byModel.Select(kv => new ModelSpend(kv.Key, kv.Value.Tokens, kv.Value.Billable, kv.Value.Dollars))
                .OrderByDescending(spend => spend.BillableTokens)
                .ToList());
    }

    public static int YearHistoryDays(DateTimeOffset now)
    {
        var jan1 = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset);
        return Math.Max(1, (int)(now.Date - jan1.Date).TotalDays + 1);
    }

    public static ProviderCostSummary Summarize(IReadOnlyList<TokenEvent> events, DateTimeOffset now)
    {
        var todayStart = new DateTimeOffset(now.Date, now.Offset);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var yearStart = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset);
        var fiveHoursAgo = now.AddHours(-5);
        var sevenDaysAgo = now.AddDays(-7);

        double todayDollars = 0, monthDollars = 0;
        long todayTokens = 0, todayBillable = 0, monthTokens = 0, monthBillable = 0;
        var hourly = new double[24];
        var daily = new double[DateTime.DaysInMonth(now.Year, now.Month)];
        var recent = new Dictionary<string, (long Tokens, long Billable, double Dollars)>(StringComparer.OrdinalIgnoreCase);
        var weekly = new Dictionary<string, (long Tokens, long Billable, double Dollars)>(StringComparer.OrdinalIgnoreCase);
        var month = new Dictionary<string, (long Tokens, long Billable, double Dollars)>(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<DateTime, (long Tokens, long Billable, double Dollars)>();
        var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tokenEvent in events)
        {
            var local = tokenEvent.Timestamp.ToLocalTime();
            if (local > now || local < yearStart) continue;
            var dollars = tokenEvent.Dollars;
            var model = Pricing.CanonicalModelName(tokenEvent.Model);
            if (!Pricing.IsKnown(model)) unknown.Add(model);

            if (local >= todayStart)
            {
                todayDollars += dollars;
                todayTokens += tokenEvent.WireTokens;
                todayBillable += tokenEvent.BillableTokens;
                hourly[Math.Clamp(local.Hour, 0, 23)] += dollars;
            }
            if (local >= monthStart)
            {
                monthDollars += dollars;
                monthTokens += tokenEvent.WireTokens;
                monthBillable += tokenEvent.BillableTokens;
                daily[Math.Clamp(local.Day - 1, 0, daily.Length - 1)] += dollars;
                Accumulate(month, model, tokenEvent, dollars);
            }
            if (local >= fiveHoursAgo) Accumulate(recent, model, tokenEvent, dollars);
            if (local >= sevenDaysAgo) Accumulate(weekly, model, tokenEvent, dollars);

            var day = local.Date;
            history.TryGetValue(day, out var bucket);
            history[day] = (bucket.Tokens + tokenEvent.WireTokens,
                bucket.Billable + tokenEvent.BillableTokens,
                bucket.Dollars + dollars);
        }

        // Cumulative series for the sparklines.
        var todaySeries = new double[24];
        double running = 0;
        var currentHour = now.Hour;
        for (var h = 0; h < 24; h++)
        {
            running += hourly[h];
            todaySeries[h] = h <= currentHour ? running : running;
        }
        var monthSeries = new double[now.Day];
        running = 0;
        for (var d = 0; d < now.Day; d++)
        {
            running += daily[d];
            monthSeries[d] = running;
        }

        var dailyHistory = history
            .OrderBy(kv => kv.Key)
            .Select(kv => new DailyTokenBucket(
                new DateTimeOffset(kv.Key, now.Offset), kv.Value.Tokens, kv.Value.Billable, kv.Value.Dollars))
            .ToList();

        return new ProviderCostSummary(
            todayDollars, todayTokens, todayBillable,
            monthDollars, monthTokens, monthBillable,
            todaySeries, monthSeries,
            ToSpendList(recent), ToSpendList(weekly), ToSpendList(month),
            dailyHistory, unknown.ToList());
    }

    private static void Accumulate(
        Dictionary<string, (long Tokens, long Billable, double Dollars)> sink,
        string model,
        TokenEvent tokenEvent,
        double dollars)
    {
        sink.TryGetValue(model, out var entry);
        sink[model] = (entry.Tokens + tokenEvent.WireTokens,
            entry.Billable + tokenEvent.BillableTokens,
            entry.Dollars + dollars);
    }

    private static List<ModelSpend> ToSpendList(
        Dictionary<string, (long Tokens, long Billable, double Dollars)> sink) =>
        sink.Select(kv => new ModelSpend(kv.Key, kv.Value.Tokens, kv.Value.Billable, kv.Value.Dollars))
            .OrderByDescending(spend => spend.BillableTokens)
            .ToList();
}
