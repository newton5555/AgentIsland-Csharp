using AgentIsland.Core;
using AgentIsland.Core.Cost;
using AgentIsland.UI.Providers;
using AgentIsland.UI.Report;

namespace AgentIsland.Tests;

public class DailyReportCardTests
{
    [WpfFact]
    public void RunAll() => Run();

    internal static void Run()
    {
        WpfTestEnvironment.EnsureInitialized();
        TestHourlyBucketsSumPerLocalHour();
        TestCacheReadAggregatesPerModelAndSlice();
        TestCacheSavingsUsesPriceTableDeltaAndIgnoresUnknown();
        TestDeltaHandlesZeroBaselineAndDirection();
        TestPagerLabelRelativeDays();
        TestAgentTreeSharesAreGlobalAndClipped();
        TestForIntervalEmptySlicesYieldEmptyCard();
        Console.WriteLine("PASS daily report card aggregation, delta, labels, tree shares, clipping");
    }

    private static IReadOnlyList<TokenEvent> Events(params TokenEvent[] events) => events;

    private static TokenEvent Ev(int hour, int minute, string model, long input, long output, long cacheRead = 0, long cacheWrite = 0)
    {
        var local = new DateTime(2026, 9, 9, hour, minute, 0, DateTimeKind.Local);
        return new TokenEvent(
            TriggerTool.Claude, new DateTimeOffset(local), model, input, output, cacheWrite, cacheRead,
            SelfReportedCostUSD: null);
    }

    private static void TestHourlyBucketsSumPerLocalHour()
    {
        var events = Events(
            Ev(3, 15, "claude-sonnet-4-6", 1000, 100),
            Ev(14, 20, "claude-sonnet-4-6", 2000, 200),
            Ev(14, 50, "gpt-5.2", 3000, 300),
            Ev(9, 0, "claude-sonnet-4-6", 500, 50));
        var day = new DateTime(2026, 9, 9);
        var slice = CostSummarizer.Slice(events, ReportPeriodsAtLocal(day), ReportPeriodsAtLocal(day.AddDays(1)));

        if (slice.HourlyTokens is null || slice.HourlyTokens.Count != 24)
            throw new Exception("HourlyTokens must be exactly 24 buckets.");
        if (slice.HourlyTokens[3] != 1100) throw new Exception("Hour 3 must hold the 03:15 event's wire tokens.");
        if (slice.HourlyTokens[14] != 5500) throw new Exception("Hour 14 must sum both 14:xx events.");
        if (slice.HourlyTokens[9] != 550) throw new Exception("Hour 9 must hold the 09:00 event.");
        if (slice.HourlyTokens.Where((t, h) => h is not (3 or 14 or 9) && t != 0).Any())
            throw new Exception("Other hours must stay zero.");

        // Out-of-interval events must not leak into the slice.
        var nextDay = new List<TokenEvent> { Ev(3, 15, "claude-sonnet-4-6", 7000, 700) }
            .Select(e => e with { Timestamp = e.Timestamp.AddDays(1) }).ToList();
        var slice2 = CostSummarizer.Slice(nextDay, ReportPeriodsAtLocal(day), ReportPeriodsAtLocal(day.AddDays(1)));
        if (slice2.HourlyTokens[3] != 0) throw new Exception("Next-day events must not leak into today's pulse.");
    }

    private static void TestCacheReadAggregatesPerModelAndSlice()
    {
        var events = Events(
            Ev(3, 15, "claude-sonnet-4-6", 1000, 100, cacheRead: 8000),
            Ev(14, 20, "claude-sonnet-4-6", 2000, 200, cacheRead: 4000),
            Ev(14, 50, "gpt-5.2", 3000, 300));
        var day = new DateTime(2026, 9, 9);
        var slice = CostSummarizer.Slice(events, ReportPeriodsAtLocal(day), ReportPeriodsAtLocal(day.AddDays(1)));

        if (slice.CacheReadTokens != 12000) throw new Exception("Slice cache reads must sum across models.");
        var claude = slice.ByModel.Single(m => m.Model == "Sonnet 4.6" || m.Model.Contains("sonnet", StringComparison.OrdinalIgnoreCase));
        if (claude.CacheReadTokens != 12000) throw new Exception("Per-model cache reads must aggregate.");
        var gpt = slice.ByModel.Single(m => m.Model.Contains("gpt", StringComparison.OrdinalIgnoreCase));
        if (gpt.CacheReadTokens != 0) throw new Exception("Models without cache reads must stay at zero.");
    }

    private static void TestCacheSavingsUsesPriceTableDeltaAndIgnoresUnknown()
    {
        // claude-sonnet-4-6: input 3, cache read 0.3 → 2.7 per million saved.
        var savings = Pricing.CacheSavings("claude-sonnet-4-6", 1_000_000);
        if (Math.Abs(savings - 2.7) > 1e-9) throw new Exception("Savings must use the input-minus-cacheRead delta.");
        if (Pricing.CacheSavings("unknown-model-xyz", 5_000_000) != 0)
            throw new Exception("Unpriced models must not invent savings.");
        if (Pricing.CacheSavings("claude-sonnet-4-6", 0) != 0)
            throw new Exception("Zero reads save nothing.");
    }

    private static void TestDeltaHandlesZeroBaselineAndDirection()
    {
        if (DailyReportData.FormatDelta(120, 100) != "↑ 20.0%")
            throw new Exception("A rise must render the up arrow with one decimal.");
        if (DailyReportData.FormatDelta(80, 100) != "↓ 20.0%")
            throw new Exception("A fall must render the down arrow.");
        if (DailyReportData.FormatDelta(100, 0) != "—")
            throw new Exception("A zero baseline must read —, never +∞%.");
        if (DailyReportData.FormatDelta(0, 100) != "↓ 100.0%")
            throw new Exception("A silent day after an active one still measures the drop.");
    }

    private static void TestPagerLabelRelativeDays()
    {
        var today = DateTime.Today;
        var todayLabel = DailyReportData.FormatPager(today);
        var yesterdayLabel = DailyReportData.FormatPager(today.AddDays(-1));
        var olderLabel = DailyReportData.FormatPager(new DateTime(2025, 1, 2));
        if (ReportFormat.IsChinese)
        {
            if (!todayLabel.EndsWith("(今天)")) throw new Exception("Today must carry the (今天) suffix.");
            if (!yesterdayLabel.EndsWith("(昨天)")) throw new Exception("Yesterday must carry the (昨天) suffix.");
            if (!olderLabel.StartsWith("2025年1月2日")) throw new Exception("Older days render the plain date.");
        }
        else
        {
            if (!todayLabel.EndsWith("(Today)")) throw new Exception("Today must carry the (Today) suffix.");
            if (!olderLabel.StartsWith("Jan 2, 2025")) throw new Exception("Older days render the plain date.");
        }
        if (DailyReportData.FormatDate(today).Length == 0) throw new Exception("Header date must never be empty.");
    }

    private static void TestAgentTreeSharesAreGlobalAndClipped()
    {
        // Build slices directly: two enabled hosts (island cap) + one
        // disabled slice used by the unpriced-row pass below.
        long total = 900;
        var day = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.FromHours(8));
        var claudeHours = new long[24];
        claudeHours[15] = 500;
        var claudeSlice = new ReportSlice(
            new List<DailyTokenBucket> { new(day, 500, 500, 12.5) },
            12.5,
            new List<ModelSpend>
            {
                new("claude-opus-4-6", 300, 300, 10, 1000),
                new("claude-sonnet-4-6", 200, 200, 2.5, 500),
            },
            claudeHours, 0, 0);
        var codexSlice = new ReportSlice(
            new List<DailyTokenBucket> { new(day, 400, 400, 3) },
            3,
            new List<ModelSpend> { new("gpt-5.2", 400, 400, 3, 0) },
            new long[24], 0, 0);
        var deepseekSlice = new ReportSlice(
            new List<DailyTokenBucket> { new(day, 100, 100, 0) },
            0,
            new List<ModelSpend> { new("deepseek-chat", 100, 100, 0, 0) },
            new long[24], 0, 0);
        var slices = new Dictionary<DisplayProvider, ReportSlice>
        {
            [DisplayProvider.Claude] = claudeSlice,
            [DisplayProvider.Codex] = codexSlice,
            [DisplayProvider.DeepSeek] = deepseekSlice,
        };

        var visibility = MakeStore(DisplayProvider.Claude, DisplayProvider.Codex);
        var data = DailyReportData.ForInterval(new DateTime(2026, 9, 9), slices, visibilityStore: visibility);
        if (data.TotalTokens != total) throw new Exception("Tree totals must sum every provider's day bucket.");
        var claudeRow = data.AgentTree.Single(r => r.Provider == DisplayProvider.Claude);
        if (Math.Abs(claudeRow.SharePercent - 500.0 / 900) > 1e-9)
            throw new Exception("Claude's 500/900 must be its global share.");
        var opus = claudeRow.Models.Single(m => m.Name.Contains("Opus"));
        if (Math.Abs(opus.SharePercent - 300.0 / 900) > 1e-9)
            throw new Exception("Opus's 300/900 must be a GLOBAL share (not of its parent).");
        if (data.HasCacheData) throw new Exception("No cache writes/reads means the cache cell reads —.");
        if (data.TotalModelsCount != 3) throw new Exception("Three model rows across two hosts.");
        if (data.PeakTokens <= 0 || data.HourlyTokens.Count != 24)
            throw new Exception("Pulse must exist even when assembled from model-level slices.");

        // Unpriced host: Claude + DeepSeek — the DeepSeek row prints —.
        var deepseekStore = MakeStore(DisplayProvider.Claude, DisplayProvider.DeepSeek);
        var unpriced = DailyReportData.ForInterval(new DateTime(2026, 9, 9), slices, visibilityStore: deepseekStore);
        var deepseekRow = unpriced.AgentTree.Single(r => r.Provider == DisplayProvider.DeepSeek);
        if (deepseekRow.Dollars != 0)
            throw new Exception("Unpriced providers carry a zero dollar, the card prints —.");
        if (Math.Abs(deepseekRow.SharePercent - 100.0 / 600) > 1e-9)
            throw new Exception("DeepSeek's 100/600 must be its global share.");
    }

    private static void TestForIntervalEmptySlicesYieldEmptyCard()
    {
        var slices = new Dictionary<DisplayProvider, ReportSlice>();
        var visibility = new AgentIsland.Backend.Settings.ProviderVisibilityStore();
        foreach (var provider in new[] { DisplayProvider.Claude, DisplayProvider.Codex, DisplayProvider.DeepSeek })
        {
            visibility.SetEnabled(provider, true);
        }
        var data = DailyReportData.ForInterval(new DateTime(2026, 9, 9), slices, visibilityStore: visibility);
        if (data.TotalTokens != 0 || data.AgentTree.Count != 0 || data.ActiveAgentsCount != 0)
            throw new Exception("A day with no records must render the empty state, not crash.");
        if (data.DeltaText != "—") throw new Exception("No baseline, no invented delta.");
    }

    private static DateTimeOffset ReportPeriodsAtLocal(DateTime date) =>
        new(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(date));

    /// The island caps the enabled set at two flank slots; tests pin their
    /// pair explicitly so assertions never depend on machine state.
    private static AgentIsland.Backend.Settings.ProviderVisibilityStore MakeStore(params DisplayProvider[] providers)
    {
        var store = new AgentIsland.Backend.Settings.ProviderVisibilityStore();
        foreach (var provider in store.Enabled.ToList())
        {
            store.SetEnabled(provider, false);
        }
        foreach (var provider in providers)
        {
            if (!store.SetEnabled(provider, true))
                throw new Exception($"MakeStore could not enable {provider}.");
        }
        return store;
    }
}
