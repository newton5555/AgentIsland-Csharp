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
