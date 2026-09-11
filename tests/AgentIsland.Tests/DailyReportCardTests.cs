using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        TestDynamicModelBudgetAndOmittedRowFolding();
        TestDailyReportTabSwitching();
        Console.WriteLine("PASS daily report card aggregation, delta, labels, tree shares, clipping, tabs");
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
        // Two enabled hosts + one disabled host whose day data auto-joins.
        long total = 1000;
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
        if (Math.Abs(claudeRow.SharePercent - 0.5) > 1e-9)
            throw new Exception("Claude's 500/1000 must be a 50% global share.");
        var opus = claudeRow.Models.Single(m => m.Name.Contains("Opus"));
        if (Math.Abs(opus.SharePercent - 0.3) > 1e-9)
            throw new Exception("Opus's 300/1000 must be a GLOBAL share (not of its parent).");
        if (data.HasCacheData) throw new Exception("No cache writes/reads means the cache cell reads —.");
        if (data.TotalModelsCount != 4) throw new Exception("Four model rows across three hosts.");
        if (data.PeakTokens <= 0 || data.HourlyTokens.Count != 24)
            throw new Exception("Pulse must exist even when assembled from model-level slices.");

        // Daily scope: the disabled DeepSeek host (100 tokens of day data)
        // auto-joins AFTER the enabled pair — guests follow enabled hosts.
        var deepseekRow = data.AgentTree.Single(r => r.Provider == DisplayProvider.DeepSeek);
        if (deepseekRow.Dollars != 0)
            throw new Exception("Unpriced providers carry a zero dollar, the card prints —.");
        if (Math.Abs(deepseekRow.SharePercent - 0.1) > 1e-9)
            throw new Exception("DeepSeek's 100/1000 must be a 10% global share.");
        if (data.AgentTree.ToList().IndexOf(deepseekRow) < data.AgentTree.ToList().IndexOf(claudeRow))
            throw new Exception("Enabled hosts anchor the tree before auto-joined guests.");
        if (data.ActiveAgentsCount != 3)
            throw new Exception("KPI counts all three hosts with day data.");

        // A guest with NO day data stays out of the tree entirely.
        var deepseekEmpty = new ReportSlice(
            new List<DailyTokenBucket>(), 0,
            new List<ModelSpend> { new("deepseek-chat", 0, 0, 0, 0) },
            new long[24], 0, 0);
        var emptyGuestSlices = new Dictionary<DisplayProvider, ReportSlice>(slices)
        {
            [DisplayProvider.DeepSeek] = deepseekEmpty,
        };
        var withoutGuest = DailyReportData.ForInterval(new DateTime(2026, 9, 9), emptyGuestSlices, visibilityStore: visibility);
        if (withoutGuest.AgentTree.Any(r => r.Provider == DisplayProvider.DeepSeek))
            throw new Exception("A zero-data guest must not render a row.");
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

    private static void TestDynamicModelBudgetAndOmittedRowFolding()
    {
        // 1. Verify DeepSeek with 4 models displays all 4 models when budget permits
        var models = new List<DailyModelRow>
        {
            new("glm-5.3-flash", 16_000_000, 0, 0.5),
            new("deepseek-v4-flash", 8_000_000, 0, 0.25),
            new("deepseek-flash", 4_000_000, 0, 0.125),
            new("deepseek-v4-pro", 200_000, 0, 0.01),
        };
        var deepseekAgent = new DailyAgentRow(DisplayProvider.DeepSeek, 28_200_000, 0, 0.885, models);
        var codexAgent = new DailyAgentRow(DisplayProvider.Codex, 2_000_000, 0, 0.065, new List<DailyModelRow>
        {
            new("gpt-5.6", 2_000_000, 0, 0.065)
        });
        var agyAgent = new DailyAgentRow(DisplayProvider.Antigravity, 1_000_000, 0, 0.05, new List<DailyModelRow>
        {
            new("gemini-flash", 1_000_000, 0, 0.05)
        });
        var testData = new DailyReportData(
            "9月10日", "今天", "—", false, false, 31_200_000, 0, false, false, 0, 0, false,
            3, 6, new long[24], 0, 0, new[] { codexAgent, deepseekAgent, agyAgent });

        var card = ReportCards.Daily(testData);
        var textBlocks = FindChildren<TextBlock>(card);
        var foundFlash = textBlocks.Any(t => t.Text == "deepseek-flash");
        if (!foundFlash) throw new Exception("deepseek-flash must be rendered in the daily card visual tree.");

        // 2. Verify overflow folding into omitted row when an agent has 7 models
        var sevenModels = Enumerable.Range(1, 7)
            .Select(i => new DailyModelRow($"model-{i}", 100_000, 0, 0.1))
            .ToList();
        var heavyAgent = new DailyAgentRow(DisplayProvider.DeepSeek, 700_000, 0, 0.7, sevenModels);
        var heavyData = new DailyReportData(
            "9月10日", "今天", "—", false, false, 1_000_000, 0, false, false, 0, 0, false,
            3, 9, new long[24], 0, 0, new[] { codexAgent, heavyAgent, agyAgent });

        var heavyCard = ReportCards.Daily(heavyData);
        var heavyTextBlocks = FindChildren<TextBlock>(heavyCard);
        var foundOmitted = heavyTextBlocks.Any(t => t.Text.Contains("其他模型") || t.Text.Contains("other models"));
        if (!foundOmitted) throw new Exception("Overflow models must be folded into an aggregated omitted row.");
    }

    private static void TestDailyReportTabSwitching()
    {
        var models = new List<DailyModelRow>
        {
            new("glm-5.3-flash", 16_000_000, 0, 0.5),
            new("deepseek-v4-flash", 8_000_000, 0, 0.25),
            new("deepseek-flash", 4_000_000, 0, 0.125),
            new("deepseek-v4-pro", 200_000, 0, 0.01),
        };
        var deepseekAgent = new DailyAgentRow(DisplayProvider.DeepSeek, 28_200_000, 0, 0.885, models);
        var codexAgent = new DailyAgentRow(DisplayProvider.Codex, 2_000_000, 0, 0.065, new List<DailyModelRow>
        {
            new("gpt-5.6", 2_000_000, 0, 0.065)
        });
        var testData = new DailyReportData(
            "9月10日", "今天", "—", false, false, 30_200_000, 0, false, false, 0, 0, false,
            2, 5, new long[24], 0, 0, new[] { deepseekAgent, codexAgent });

        // 1. Test Overview tab default
        var overviewCard = ReportCards.Daily(testData, selectedTab: "overview");
        var overviewTexts = FindChildren<TextBlock>(overviewCard);
        if (!overviewTexts.Any(t => t.Text.Contains("AGENTS 全局分布") || t.Text.Contains("AGENTS & MODELS")))
            throw new Exception("Overview view must render global distribution header.");

        // 2. Test DeepSeek dedicated tab
        var changedTab = "";
        var deepseekCard = ReportCards.Daily(testData, selectedTab: "DeepSeek", onTabChanged: tab => changedTab = tab);
        var deepseekTexts = FindChildren<TextBlock>(deepseekCard);

        if (!deepseekTexts.Any(t => t.Text.Contains("DeepSeek 消耗全貌") || t.Text.Contains("DeepSeek Overview")))
            throw new Exception("DeepSeek tab must render dedicated banner.");

        // Verify rank badges
        if (!deepseekTexts.Any(t => t.Text == "#1") || !deepseekTexts.Any(t => t.Text == "#4"))
            throw new Exception("DeepSeek tab must render rank badges for all models.");

        // Verify all 4 models are rendered
        foreach (var m in models)
        {
            if (!deepseekTexts.Any(t => t.Text == m.Name))
                throw new Exception($"DeepSeek tab must render model {m.Name}.");
        }

        // 3. Verify WPF Measure, Arrange, Layout and Bitmap Rendering for both views
        overviewCard.Measure(new Size(420, 560));
        overviewCard.Arrange(new Rect(0, 0, 420, 560));
        overviewCard.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(420 * 2, 560 * 2, 96 * 2, 96 * 2, PixelFormats.Pbgra32);
        bmp.Render(overviewCard);

        deepseekCard.Measure(new Size(420, 560));
        deepseekCard.Arrange(new Rect(0, 0, 420, 560));
        deepseekCard.UpdateLayout();
        var deepseekBmp = new System.Windows.Media.Imaging.RenderTargetBitmap(420 * 2, 560 * 2, 96 * 2, 96 * 2, PixelFormats.Pbgra32);
        deepseekBmp.Render(deepseekCard);
    }

    private static List<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var list = new List<T>();
        if (parent == null) return list;
        if (parent is T typed) list.Add(typed);
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            list.AddRange(FindChildren<T>(child));
        }
        if (parent is ContentControl cc && cc.Content is DependencyObject dcc) list.AddRange(FindChildren<T>(dcc));
        if (parent is Border b && b.Child is DependencyObject bc) list.AddRange(FindChildren<T>(bc));
        if (parent is Panel p)
        {
            foreach (UIElement pe in p.Children) list.AddRange(FindChildren<T>(pe));
        }
        return list;
    }
}
