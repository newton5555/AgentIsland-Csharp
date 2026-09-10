using System.Globalization;
using System.Windows.Media;
using AgentIsland.Core.Cost;

namespace AgentIsland.UI.Report;

/// One pie slice / legend row of the model breakdown. Carries its owning
/// provider so the card can price it ("$X") or show "—" (Cursor/DeepSeek/Antigravity), the
/// same per-row honesty split macOS ModelShare keeps.
public sealed record ModelShare(
    string Name, long Tokens, double Dollars, double Percent, Color Color,
    AgentIsland.UI.Providers.DisplayProvider Provider, bool IsOthers = false);

/// One provider's token roll-up for a report period — the atom the top-2
/// duel and the cross-provider totals are built from. Replaces the hardcoded
/// Claude/Codex share so a Grok-only, Cursor+Antigravity, or DeepSeek-only period still renders a
/// meaningful card.
public sealed record ProviderPeriodSlice(AgentIsland.UI.Providers.DisplayProvider Provider, long Tokens);

/// The shareable weekly report — assembled from LOCAL data only (CostStore's
/// log scan). Users copy or save it as a PNG and post it themselves; nothing
/// is ever uploaded, which is what lets this exist at all under the
/// no-telemetry promise.
public sealed record WeeklyReportData(
    string RangeText,
    long TotalTokens,
    double TotalDollars,
    IReadOnlyList<ProviderPeriodSlice> Providers,   // token desc, only providers that ran
    IReadOnlyList<long> DailyTokens,   // oldest → today, exactly 7, summed across all providers
    IReadOnlyList<string> DayLetters,
    IReadOnlyList<ModelShare> TopModels,
    int OmittedModelsCount = 0,
    double OmittedPercent = 0,
    bool IsAllTokens = true)
{
    public static WeeklyReportData Current(
        ICostStore? costStore = null,
        TokenCountModeStore? tokenModeStore = null,
        IProviderVisibilityStore? visibilityStore = null,
        ICostQueryService? costQueryService = null)
    {
        var cost = costStore ?? (App.Instance?.Services?.GetService(typeof(ICostStore)) as ICostStore);
        // Anchor the 7-day window to the freshest SCANNED day, not the wall
        // clock (macOS): right after launch the store can still hold
        // yesterday's snapshot, and a wall-clock window shears against the
        // scan-anchored model rows.
        var anchor = ReportPeriods.ScanAnchor(cost);
        var days = Enumerable.Range(0, 7).Reverse().Select(offset => anchor.AddDays(-offset)).ToArray();
        var mode = (tokenModeStore ?? (App.Instance?.Services?.GetService(typeof(TokenCountModeStore)) as TokenCountModeStore))?.Mode ?? AgentIsland.Backend.Settings.TokenCountMode.All;

        long BucketTotal(IReadOnlyList<DailyTokenBucket> buckets, DateTime day) =>
            buckets.FirstOrDefault(b => b.DayStart.Date == day) is { } bucket
                ? (mode == AgentIsland.Backend.Settings.TokenCountMode.All ? bucket.Tokens : bucket.BillableTokens)
                : 0;

        long WeekTokens(AgentIsland.UI.Providers.DisplayProvider provider) =>
            days.Sum(d => BucketTotal(cost?.Summary(provider).DailyHistory ?? Array.Empty<DailyTokenBucket>(), d));

        var targets = (visibilityStore ?? (App.Instance?.Services?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore))?.Enabled ?? [];

        // Every provider that ran from user's enabled targets
        var providers = targets
            .Select(provider => new ProviderPeriodSlice(provider, WeekTokens(provider)))
            .Where(slice => slice.Tokens > 0)
            .OrderByDescending(slice => slice.Tokens)
            .ToList();

        var daily = days
            .Select(d => targets.Sum(p => BucketTotal(cost?.Summary(p).DailyHistory ?? Array.Empty<DailyTokenBucket>(), d)))
            .ToArray();
        var total = providers.Sum(slice => slice.Tokens);
        // Tokens-but-no-dollars providers (Cursor) carry $0 rows, so they add
        // nothing to the dollar total on their own; the "≈" hero copy keeps it
        // an estimate.
        var dollars = targets.Sum(p => cost?.Summary(p).WeeklyModels.Sum(m => m.Dollars) ?? 0.0);

        // The card follows the app language — a card destined for WeChat
        // groups must read Chinese when the UI is Chinese.
        var range = FormatRange(days[0], anchor);
        var (topModels, omittedCount, omittedPercent) = ReportFormat.BuildTopModelsDetailed(
            cost != null ? ReportFormat.ProviderModels(cost, targets, s => s.WeeklyModels) : Array.Empty<(AgentIsland.UI.Providers.DisplayProvider, ModelSpend)>(), top: 3, tokenModeStore: tokenModeStore);

        return new WeeklyReportData(
            range,
            total,
            dollars,
            providers,
            daily,
            LettersFor(days),
            topModels,
            omittedCount,
            omittedPercent,
            mode == AgentIsland.Backend.Settings.TokenCountMode.All);
    }

    /// Assembles a PAST page of the report pager from interval slices
    /// (offset ≠ 0 — the current page keeps Current()). Mirrors Current()
    /// in shape; daily bars, totals, and per-model rows come from one
    /// full-scan slice instead of the live store windows, so the whole card
    /// sits on a single consistent window by construction.
    public static WeeklyReportData ForInterval(
        DateTime start, DateTime endExclusive,
        IReadOnlyDictionary<AgentIsland.UI.Providers.DisplayProvider, ReportSlice> slices,
        TokenCountModeStore? tokenModeStore = null,
        IProviderVisibilityStore? visibilityStore = null)
    {
        var mode = (tokenModeStore ?? (App.Instance?.Services?.GetService(typeof(TokenCountModeStore)) as TokenCountModeStore))?.Mode ?? AgentIsland.Backend.Settings.TokenCountMode.All;
        var firstDay = start.Date;
        var days = Enumerable.Range(0, 7).Select(offset => firstDay.AddDays(offset)).ToArray();

        long BucketValue(DailyTokenBucket bucket) =>
            mode == AgentIsland.Backend.Settings.TokenCountMode.All ? bucket.Tokens : bucket.BillableTokens;

        var targets = (visibilityStore ?? (App.Instance?.Services?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore))?.Enabled ?? [];
        ReportSlice SliceOf(AgentIsland.UI.Providers.DisplayProvider provider) =>
            slices.TryGetValue(provider, out var slice) ? slice : ReportSlice.Empty;

        var daily = Enumerable.Range(0, days.Length)
            .Select(i => targets.Sum(p =>
            {
                var buckets = SliceOf(p).DailyTokens;
                return i < buckets.Count ? BucketValue(buckets[i]) : 0;
            }))
            .ToArray();

        var providers = targets
            .Select(provider => new ProviderPeriodSlice(
                provider, SliceOf(provider).DailyTokens.Sum(BucketValue)))
            .Where(slice => slice.Tokens > 0)
            .OrderByDescending(slice => slice.Tokens)
            .ToList();
        var total = providers.Sum(slice => slice.Tokens);
        var dollars = targets.Sum(p => SliceOf(p).Dollars);

        var lastDay = days[^1];
        var (topModels, omittedCount, omittedPercent) = ReportFormat.BuildTopModelsDetailed(
            targets.SelectMany(p => SliceOf(p).ByModel.Select(spend => (p, spend))),
            top: 3, tokenModeStore: tokenModeStore);

        return new WeeklyReportData(
            FormatRange(firstDay, lastDay),
            total,
            dollars,
            providers,
            daily,
            LettersFor(days),
            topModels,
            omittedCount,
            omittedPercent,
            mode == AgentIsland.Backend.Settings.TokenCountMode.All);
    }

    internal static string FormatRange(DateTime first, DateTime last) => ReportFormat.IsChinese
        ? $"{first:M月d日} – {last:M月d日}"
        : $"{first.ToString("MMM d", CultureInfo.InvariantCulture)} – {last.ToString("MMM d", CultureInfo.InvariantCulture)}";

    internal static IReadOnlyList<string> LettersFor(IReadOnlyList<DateTime> days)
    {
        var zh = ReportFormat.IsChinese;
        var zhDays = new[] { "日", "一", "二", "三", "四", "五", "六" };
        return days
            .Select(d => zh
                ? zhDays[(int)d.DayOfWeek]
                : d.ToString("ddd", CultureInfo.InvariantCulture)[..1])
            .ToArray();
    }
}

/// The monthly share card — v3 drops the heatmap; the month's model mix
/// (TOP 5 pie) is the centerpiece under the faceoff bar.
public sealed record MonthlyReportData(
    string MonthText,
    long TotalTokens,
    double TotalDollars,
    IReadOnlyList<ProviderPeriodSlice> Providers,   // token desc, only providers that ran
    IReadOnlyList<ModelShare> TopModels,
    int OmittedModelsCount = 0,
    double OmittedPercent = 0,
    bool IsAllTokens = true)
{
    public static MonthlyReportData Current(
        ICostStore? costStore = null,
        TokenCountModeStore? tokenModeStore = null,
        IProviderVisibilityStore? visibilityStore = null)
    {
        var cost = costStore ?? (App.Instance?.Services?.GetService(typeof(ICostStore)) as ICostStore);
        var today = DateTime.Today;
        var zh = ReportFormat.IsChinese;

        var mode = (tokenModeStore ?? (App.Instance?.Services?.GetService(typeof(TokenCountModeStore)) as TokenCountModeStore))?.Mode ?? AgentIsland.Backend.Settings.TokenCountMode.All;
        var targets = (visibilityStore ?? (App.Instance?.Services?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore))?.Enabled ?? [];
        var providers = targets
            .Select(provider => new ProviderPeriodSlice(provider, mode == AgentIsland.Backend.Settings.TokenCountMode.All
                ? cost?.Summary(provider).MonthTokens ?? 0
                : cost?.Summary(provider).MonthBillableTokens ?? 0))
            .Where(slice => slice.Tokens > 0)
            .OrderByDescending(slice => slice.Tokens)
            .ToList();
        var totalTokens = providers.Sum(slice => slice.Tokens);
        var totalDollars = targets.Sum(p => cost?.Summary(p).MonthDollars ?? 0.0);

        var (topModels, omittedCount, omittedPercent) = ReportFormat.BuildTopModelsDetailed(
            cost != null ? ReportFormat.ProviderModels(cost, targets, s => s.MonthModels) : Array.Empty<(AgentIsland.UI.Providers.DisplayProvider, ModelSpend)>(), top: 5, tokenModeStore: tokenModeStore);

        return new MonthlyReportData(
            zh ? $"{today:yyyy年M月}" : today.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            totalTokens,
            totalDollars,
            providers,
            topModels,
            omittedCount,
            omittedPercent,
            mode == AgentIsland.Backend.Settings.TokenCountMode.All);
    }

    /// A PAST calendar month (or an anchored 30-day window) from interval
    /// slices — same accounting as the live month window, sourced from one
    /// full-scan slice.
    public static MonthlyReportData ForInterval(
        DateTime start,
        IReadOnlyDictionary<AgentIsland.UI.Providers.DisplayProvider, ReportSlice> slices,
        TokenCountModeStore? tokenModeStore = null,
        IProviderVisibilityStore? visibilityStore = null)
    {
        var zh = ReportFormat.IsChinese;
        var mode = (tokenModeStore ?? (App.Instance?.Services?.GetService(typeof(TokenCountModeStore)) as TokenCountModeStore))?.Mode ?? AgentIsland.Backend.Settings.TokenCountMode.All;

        long BucketValue(DailyTokenBucket bucket) =>
            mode == AgentIsland.Backend.Settings.TokenCountMode.All ? bucket.Tokens : bucket.BillableTokens;

        ReportSlice SliceOf(AgentIsland.UI.Providers.DisplayProvider provider) =>
            slices.TryGetValue(provider, out var slice) ? slice : ReportSlice.Empty;

        var targets = (visibilityStore ?? (App.Instance?.Services?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore))?.Enabled ?? [];
        var providers = targets
            .Select(provider => new ProviderPeriodSlice(
                provider, SliceOf(provider).DailyTokens.Sum(BucketValue)))
            .Where(slice => slice.Tokens > 0)
            .OrderByDescending(slice => slice.Tokens)
            .ToList();
        var totalTokens = providers.Sum(slice => slice.Tokens);
        var totalDollars = targets.Sum(p => SliceOf(p).Dollars);

        var (topModels, omittedCount, omittedPercent) = ReportFormat.BuildTopModelsDetailed(
            targets.SelectMany(p => SliceOf(p).ByModel.Select(spend => (p, spend))),
            top: 5, tokenModeStore: tokenModeStore);

        return new MonthlyReportData(
            zh ? $"{start:yyyy年M月}" : start.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            totalTokens,
            totalDollars,
            providers,
            topModels,
            omittedCount,
            omittedPercent,
            mode == AgentIsland.Backend.Settings.TokenCountMode.All);
    }
}

/// The daily share card — one local day, hour-resolved. Same honesty
/// rules as the weekly/monthly cards: wire tokens everywhere, "—" where a
/// provider can't be priced, nothing invented. No rank line, no session
/// count (TokenEvent carries no session identity, and invented ones would
/// break the no-fake-data promise).
public sealed record DailyReportData(
    string DateText,                        // card corner, "9月9日" / "Sep 9"
    string PagerLabel,                      // "2026年9月9日 (今天)" / "Sep 9, 2026 (Today)"
    string DeltaText,                       // "↑ 18.4%" / "— " when no baseline
    bool DeltaUp,                           // arrow direction; also picks the tint
    bool HasDelta,
    long TotalTokens,
    double TotalDollars,
    bool HasActualDollars,
    bool IsPartialDollars,
    double CacheRate,                       // 0..1, cache reads over wire tokens
    double CacheSavingsDollars,
    bool HasCacheData,                      // false → cache cell reads "—"
    int ActiveAgentsCount,
    int TotalModelsCount,
    IReadOnlyList<long> HourlyTokens,       // exactly 24, oldest → latest hour
    int PeakHour,
    long PeakTokens,
    IReadOnlyList<DailyAgentRow> AgentTree,
    bool IsAllTokens = true)
{
    public static DailyReportData Current(
        ICostStore? costStore = null,
        TokenCountModeStore? tokenModeStore = null,
        IProviderVisibilityStore? visibilityStore = null)
    {
        var cost = costStore ?? (App.Instance?.Services?.GetService(typeof(ICostStore)) as ICostStore);
        var mode = (tokenModeStore ?? (App.Instance?.Services?.GetService(typeof(TokenCountModeStore)) as TokenCountModeStore))?.Mode ?? AgentIsland.Backend.Settings.TokenCountMode.All;
        var targets = (visibilityStore ?? (App.Instance?.Services?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore))?.Enabled ?? [];

        long BucketValue(DailyTokenBucket bucket) =>
            mode == AgentIsland.Backend.Settings.TokenCountMode.All ? bucket.Tokens : bucket.BillableTokens;

        long DayTokens(AgentIsland.UI.Providers.DisplayProvider provider, DateTime day) =>
            cost?.Summary(provider).DailyHistory.FirstOrDefault(b => b.DayStart.Date == day) is { } bucket
                ? BucketValue(bucket)
                : 0;

        var anchor = ReportPeriods.ScanAnchor(cost);
        var day = anchor;
        var previous = anchor.AddDays(-1);
        var total = targets.Sum(p => DayTokens(p, day));
        var previousTotal = targets.Sum(p => DayTokens(p, previous));

        var providers = targets
            .Select(p => new ProviderPeriodSlice(p, DayTokens(p, day)))
            .Where(slice => slice.Tokens > 0)
            .OrderByDescending(slice => slice.Tokens)
            .ToList();

        // Live-card skeleton: the fresh daily buckets carry totals, but the
        // hour pulse and model tree need the event-level slice, which only
        // the async query provides — SlicesAsync lands a beat later and the
        // ForInterval path rebuilds the card. Dollars and models ride the
        // summary's rolling windows here (approximate for hours 5..∞), so
        // money shows only once the precise slice arrives.
        var dollars = 0.0;
        var hourly = new long[24];
        var agentTree = Array.Empty<DailyAgentRow>();

        return new DailyReportData(
            FormatDate(day),
            FormatPager(day),
            FormatDelta(total, previousTotal),
            total > previousTotal,
            previousTotal > 0,
            total,
            dollars,
            HasActualDollars: false,
            IsPartialDollars: false,
            CacheRate: 0,
            CacheSavingsDollars: 0,
            HasCacheData: false,
            ActiveAgentsCount: providers.Count,
            TotalModelsCount: 0,
            hourly,
            PeakHour: 0,
            PeakTokens: 0,
            agentTree,
            mode == AgentIsland.Backend.Settings.TokenCountMode.All);
    }

    /// A past (or settled current) day assembled from event-level slices —
    /// the calendar anchor and every async rebuild lands here.
    public static DailyReportData ForInterval(
        DateTime day,
        IReadOnlyDictionary<AgentIsland.UI.Providers.DisplayProvider, ReportSlice> slices,
        TokenCountModeStore? tokenModeStore = null,
        IProviderVisibilityStore? visibilityStore = null,
        long previousDayTokens = 0)
    {
        var mode = (tokenModeStore ?? (App.Instance?.Services?.GetService(typeof(TokenCountModeStore)) as TokenCountModeStore))?.Mode ?? AgentIsland.Backend.Settings.TokenCountMode.All;
        long BucketValue(DailyTokenBucket bucket) =>
            mode == AgentIsland.Backend.Settings.TokenCountMode.All ? bucket.Tokens : bucket.BillableTokens;

        ReportSlice SliceOf(AgentIsland.UI.Providers.DisplayProvider provider) =>
            slices.TryGetValue(provider, out var slice) ? slice : ReportSlice.Empty;

        // Daily scope: the enabled hosts anchor the card (slot order), then
        // every OTHER host that carries day data joins — the tree shows what
        // the machine actually ran that day, not what the island slots hold.
        // Zero-data non-enabled hosts stay out; the rows themselves only
        // render tokens > 0 anyway.
        var enabledSet = (visibilityStore ?? (App.Instance?.Services?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore))?.Enabled ?? [];
        var enabled = AgentIsland.UI.Providers.DisplayProviders.All.Where(enabledSet.Contains);
        var guestsWithData = AgentIsland.UI.Providers.DisplayProviders.All
            .Where(p => !enabledSet.Contains(p))
            .Select(p => (Provider: p, Tokens: SliceOf(p).DailyTokens.Sum(BucketValue)))
            .Where(pair => pair.Tokens > 0)
            .OrderByDescending(pair => pair.Tokens)
            .Select(pair => pair.Provider);
        var targets = enabled.Concat(guestsWithData).ToList();

        var providers = targets
            .Select(provider => new ProviderPeriodSlice(
                provider, SliceOf(provider).DailyTokens.Sum(BucketValue)))
            .Where(slice => slice.Tokens > 0)
            .OrderByDescending(slice => slice.Tokens)
            .ToList();
        var total = providers.Sum(slice => slice.Tokens);
        var dollars = targets.Sum(p => SliceOf(p).Dollars);
        var hasPriced = targets.Any(p => SliceOf(p).ByModel.Any(m => m.Dollars > 0));
        var hasUnpriced = providers.Any(slice => !ReportFormat.ProvidesDollars(slice.Provider));

        var hourly = new long[24];
        foreach (var provider in targets)
        {
            var buckets = SliceOf(provider).HourlyTokens ?? Array.Empty<long>();
            for (var h = 0; h < 24; h++)
            {
                if (h < buckets.Count) hourly[h] += buckets[h];
            }
        }
        var peakHour = 0;
        var peakTokens = 0L;
        for (var h = 0; h < 24; h++)
        {
            if (hourly[h] > peakTokens)
            {
                peakTokens = hourly[h];
                peakHour = h;
            }
        }

        var cacheRead = targets.Sum(p => SliceOf(p).CacheReadTokens);
        var cacheWrite = targets.Sum(p => SliceOf(p).CacheWriteTokens);
        var wireTotal = Math.Max(1, total);
        // Cache savings ride the priced models only — same honesty split as
        // the dollar hero: where the price table is silent, so is the card.
        var savings = targets.Sum(p => ReportFormat.ProvidesDollars(p)
            ? SliceOf(p).ByModel.Sum(m => Pricing.CacheSavings(m.Model, m.CacheReadTokens))
            : 0.0);
        var hasCacheData = cacheRead + cacheWrite > 0;

        var agentTree = targets
            .Select(provider =>
            {
                var dayTokens = SliceOf(provider).DailyTokens.Sum(BucketValue);
                var providerModels = SliceOf(provider).ByModel
                    .Where(m => mode == AgentIsland.Backend.Settings.TokenCountMode.All
                        ? m.Tokens > 0
                        : m.BillableTokens > 0)
                    .OrderByDescending(m => mode == AgentIsland.Backend.Settings.TokenCountMode.All ? m.Tokens : m.BillableTokens)
                    .ToList();
                var modelRows = providerModels
                    .Select(m => new DailyModelRow(
                        ReportFormat.DisplayModelName(m.Model),
                        mode == AgentIsland.Backend.Settings.TokenCountMode.All ? m.Tokens : m.BillableTokens,
                        m.Dollars,
                        total > 0 ? (double)(mode == AgentIsland.Backend.Settings.TokenCountMode.All ? m.Tokens : m.BillableTokens) / total : 0))
                    .ToList();
                return new DailyAgentRow(
                    provider,
                    dayTokens,
                    provider is AgentIsland.UI.Providers.DisplayProvider.Cursor
                        or AgentIsland.UI.Providers.DisplayProvider.DeepSeek
                        or AgentIsland.UI.Providers.DisplayProvider.Antigravity ? 0 : SliceOf(provider).Dollars,
                    total > 0 ? (double)dayTokens / total : 0,
                    modelRows);
            })
            .Where(row => row.Tokens > 0)
            .ToList();

        var modelCount = agentTree.Sum(row => row.Models.Count);

        return new DailyReportData(
            FormatDate(day),
            FormatPager(day),
            FormatDelta(total, previousDayTokens),
            total > previousDayTokens,
            previousDayTokens > 0,
            total,
            dollars,
            HasActualDollars: dollars >= 1 && hasPriced,
            IsPartialDollars: dollars >= 1 && hasPriced && hasUnpriced,
            CacheRate: wireTotal > 0 ? (double)cacheRead / wireTotal : 0,
            CacheSavingsDollars: savings,
            HasCacheData: hasCacheData,
            ActiveAgentsCount: providers.Count,
            TotalModelsCount: modelCount,
            hourly,
            peakHour,
            peakTokens,
            agentTree,
            mode == AgentIsland.Backend.Settings.TokenCountMode.All);
    }

    internal static string FormatDate(DateTime day) => ReportFormat.IsChinese
        ? $"{day:M月d日}"
        : day.ToString("MMM d", CultureInfo.InvariantCulture);

    internal static string FormatPager(DateTime day)
    {
        var zh = ReportFormat.IsChinese;
        var today = DateTime.Today;
        var relative = day == today ? (zh ? "今天" : "Today")
            : day == today.AddDays(-1) ? (zh ? "昨天" : "Yesterday")
            : null;
        var date = zh ? $"{day:yyyy年M月d日}" : day.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        return relative is null ? date : $"{date} ({relative})";
    }

    /// Day-over-day. No invented percentages: the day before the first
    /// recorded one, or a zero baseline, reads "—".
    internal static string FormatDelta(long current, long previous)
    {
        if (previous <= 0) return "—";
        var percent = (current - previous) * 100.0 / previous;
        var arrow = percent >= 0 ? "↑" : "↓";
        return $"{arrow} {Math.Abs(percent):F1}%";
    }
}

/// One first-level agent row of the daily tree: brand mark, share pill,
/// tokens, dollars where priceable, global share.
public sealed record DailyAgentRow(
    AgentIsland.UI.Providers.DisplayProvider Provider,
    long Tokens,
    double Dollars,
    double SharePercent,
    IReadOnlyList<DailyModelRow> Models);

/// One nested model row: display name, tokens, optional dollars, global
/// share (sums of children equal the parent's share, like the prototype).
public sealed record DailyModelRow(
    string Name,
    long Tokens,
    double Dollars,
    double SharePercent);

/// Shared number/caption formatting for both cards.
public static class ReportFormat
{
    public static bool IsChinese => AgentIsland.UI.Localization.L10n.IsChinese;

    /// Model list filtered by specified active providers.
    public static IEnumerable<(AgentIsland.UI.Providers.DisplayProvider Provider, ModelSpend Spend)> ProviderModels(
        ICostStore cost, IEnumerable<AgentIsland.UI.Providers.DisplayProvider> providers, Func<ProviderCostSummary, IReadOnlyList<ModelSpend>> select)
    {
        foreach (var provider in providers)
        {
            foreach (var spend in select(cost.Summary(provider)))
            {
                yield return (provider, spend);
            }
        }
    }

    public static IEnumerable<(AgentIsland.UI.Providers.DisplayProvider Provider, ModelSpend Spend)> ProviderModels(
        ICostStore cost, Func<ProviderCostSummary, IReadOnlyList<ModelSpend>> select) =>
        ProviderModels(cost, AgentIsland.UI.Providers.DisplayProviders.All, select);

    /// Rank models by TOKEN share — the one metric every provider defines
    /// (macOS 2026-08-08 owner call). Dollar-ranking (the old two-provider
    /// behavior) filtered a tokens-only period — Cursor ships tokens with no
    /// price, Antigravity has no trusted price table — down to an empty donut, and left the donut's
    /// proportions on a different axis than the token hero. Wire tokens (cache
    /// included), same accounting as the hero total, so the ring matches the
    /// headline. TOP-N only, no "Others" row: the donut's uncovered arc reads
    /// as the long tail on its own (macOS v3). Each slice takes its provider's
    /// brand accent; the dollar figure still rides each row where the provider
    /// can be priced, and reads "—" where it cannot.
    public static IReadOnlyList<ModelShare> BuildTopModels(
        IEnumerable<(AgentIsland.UI.Providers.DisplayProvider Provider, ModelSpend Spend)> spend, int top,
        TokenCountModeStore? tokenModeStore = null) =>
        BuildTopModelsDetailed(spend, top, tokenModeStore).TopModels;

    public static (IReadOnlyList<ModelShare> TopModels, int OmittedCount, double OmittedPercent) BuildTopModelsDetailed(
        IEnumerable<(AgentIsland.UI.Providers.DisplayProvider Provider, ModelSpend Spend)> spend, int top,
        TokenCountModeStore? tokenModeStore = null)
    {
        // Token counting follows the user's mode, same as the hero total
        // (macOS rankedModels) — one accounting for the whole card.
        var mode = (tokenModeStore ?? (App.Instance?.Services?.GetService(typeof(TokenCountModeStore)) as TokenCountModeStore))?.Mode ?? AgentIsland.Backend.Settings.TokenCountMode.All;
        long TokenOf(ModelSpend s) => mode == AgentIsland.Backend.Settings.TokenCountMode.All ? s.Tokens : s.BillableTokens;
        var all = spend.ToList();
        var tokenUniverse = Math.Max(1, all.Sum(m => TokenOf(m.Spend)));
        var ranked = all
            .Select(m => (m.Provider, m.Spend, Tokens: TokenOf(m.Spend), Percent: TokenOf(m.Spend) / (double)tokenUniverse))
            .Where(m => m.Tokens > 0)
            .OrderByDescending(m => m.Percent)
            .ToList();

        var topItems = ranked.Where(m => m.Percent >= 0.005).Take(top).ToList();
        var omitted = ranked.Skip(topItems.Count).ToList();
        var omittedCount = omitted.Count;
        var omittedTokens = omitted.Sum(m => m.Tokens);
        var omittedPercent = tokenUniverse > 0 ? (double)omittedTokens / tokenUniverse : 0.0;

        var providerCounts = new Dictionary<AgentIsland.UI.Providers.DisplayProvider, int>();
        var models = new List<ModelShare>();
        foreach (var item in topItems)
        {
            var p = item.Provider;
            providerCounts.TryGetValue(p, out var index);
            providerCounts[p] = index + 1;
            var color = ColorForModel(p, index);
            models.Add(new ModelShare(item.Spend.Model, item.Tokens, item.Spend.Dollars, item.Percent, color, p));
        }

        return (models, omittedCount, omittedPercent);
    }

    public static Color ColorForModel(AgentIsland.UI.Providers.DisplayProvider provider, int modelIndex)
    {
        var palette = AgentIsland.UI.Providers.ProviderIdentity.StreamPalette(provider);
        if (palette.Count > 0 && modelIndex < palette.Count)
        {
            return palette[modelIndex];
        }
        var baseAccent = AgentIsland.UI.Providers.ProviderIdentity.Accent(provider);
        if (modelIndex == 0) return baseAccent;
        var step = (modelIndex % 3) switch
        {
            1 => 0.25,
            2 => -0.20,
            _ => 0.40,
        };
        return step > 0 ? Lighten(baseAccent, step) : Darken(baseAccent, -step);
    }

    private static Color Lighten(Color c, double t) => Color.FromRgb(
        (byte)Math.Clamp(c.R + (255 - c.R) * t, 0, 255),
        (byte)Math.Clamp(c.G + (255 - c.G) * t, 0, 255),
        (byte)Math.Clamp(c.B + (255 - c.B) * t, 0, 255));

    private static Color Darken(Color c, double t) => Color.FromRgb(
        (byte)Math.Clamp(c.R * (1 - t), 0, 255),
        (byte)Math.Clamp(c.G * (1 - t), 0, 255),
        (byte)Math.Clamp(c.B * (1 - t), 0, 255));

    /// Whether a provider can state a dollar figure: Claude/Codex are
    /// table-priced and Grok self-reports; Cursor logs tokens with no model
    /// (no price), while DeepSeek and Antigravity ship tokens without a price. Mirrors
    /// CostPage.FaceOf so the overview shows "—" for token-only
    /// providers, never a coined $0.
    public static bool ProvidesDollars(AgentIsland.UI.Providers.DisplayProvider provider) =>
        provider is not (AgentIsland.UI.Providers.DisplayProvider.Cursor
            or AgentIsland.UI.Providers.DisplayProvider.DeepSeek
            or AgentIsland.UI.Providers.DisplayProvider.Antigravity);

    public static string CompactString(long n, bool zh)
    {
        var (value, unit) = CompactParts(n, zh);
        return value + unit;
    }

    /// Share-card display names: "claude-fable-5" reads like log spam next
    /// to a gold rank line — the cards print "Fable 5", "Opus 4.8",
    /// "GPT-5.6-sol" (macOS v3 lock). Unknown shapes pass through.
    public static string DisplayModelName(string raw)
    {
        if (raw.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            return "GPT-" + raw[4..];
        }
        if (raw.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw[7..].Split('-');
            if (parts.Length >= 2 && parts.Skip(1).All(p => p.All(char.IsDigit)))
            {
                var family = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
                return family + " " + string.Join('.', parts.Skip(1));
            }
        }
        return raw;
    }

    /// (value, unit). Chinese counts in 亿/万 — the way the number is
    /// actually said — English in B/M/K.
    public static (string Value, string Unit) CompactParts(long n, bool zh)
    {
        double v = n;
        if (zh)
        {
            if (v >= 100_000_000) return (Trim(v / 100_000_000), "亿");
            if (v >= 10_000) return (Trim(v / 10_000), "万");
            return (n.ToString(CultureInfo.InvariantCulture), "");
        }
        return v switch
        {
            >= 1_000_000_000 => (Trim(v / 1_000_000_000), "B"),
            >= 1_000_000 => (Trim(v / 1_000_000), "M"),
            >= 1_000 => (Trim(v / 1_000), "K"),
            _ => (n.ToString(CultureInfo.InvariantCulture), ""),
        };
    }

    private static string Trim(double v)
    {
        // No trailing zeros — "99.5亿", never "99.50亿".
        var s = v >= 100
            ? v.ToString("F0", CultureInfo.InvariantCulture)
            : v.ToString("F2", CultureInfo.InvariantCulture);
        if (s.Contains('.'))
        {
            s = s.TrimEnd('0').TrimEnd('.');
        }
        return s;
    }

    public static string Money(double v) => Math.Round(v).ToString("N0", CultureInfo.InvariantCulture);
}
