using System.IO;
using System.Windows;
using AgentIsland.Core.Cost;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Providers;
using AgentIsland.UI.Report;

namespace AgentIsland.Tests;

// Offline fixtures: no App startup, network calls, or preference writes.
public static class ReportPreview
{
    public static void Run(string output)
    {
        var app = new Application();
        var samples = new[]
        {
            (DisplayProvider.DeepSeek, new ModelSpend("main", 9960, 9960, 0)),
            (DisplayProvider.DeepSeek, new ModelSpend("tiny-a", 20, 20, 0)),
            (DisplayProvider.DeepSeek, new ModelSpend("tiny-b", 20, 20, 0)),
        };
        var small = ReportFormat.BuildTopModelsDetailed(samples, 3);
        if (small.TopModels.Count != 1 || small.OmittedCount != 2
            || Math.Abs(small.OmittedPercent - 0.004) > 0.000001)
            throw new Exception("Sub-threshold models must remain in the omitted share.");

        foreach (var zh in new[] { true, false })
        {
            L10n.Current = zh ? L10n.Language.SimplifiedChinese : L10n.Language.English;
            var lang = zh ? "zh" : "en";
            var providers = new[] { new ProviderPeriodSlice(DisplayProvider.Codex, 400_000_000), new ProviderPeriodSlice(DisplayProvider.DeepSeek, 600_000_000) };
            var models = Enumerable.Range(0, 5).Select(i => new ModelShare(
                i == 0 ? "deepseek-v4-flash-long-model-identifier" : $"model-{i}",
                200_000_000, i > 2 ? 125 : 0, 0.2,
                ReportFormat.ColorForModel(i > 2 ? DisplayProvider.Codex : DisplayProvider.DeepSeek, i % 3),
                i > 2 ? DisplayProvider.Codex : DisplayProvider.DeepSeek)).ToArray();
            var week = new WeeklyReportData(zh ? "8月13日 – 8月19日" : "Aug 13 – Aug 19", 1_000_000_000, 250, providers,
                new long[] { 100_000_000, 200_000_000, 300_000_000, 100_000_000, 100_000_000, 100_000_000, 100_000_000 },
                zh ? new[] { "四", "五", "六", "日", "一", "二", "三" } : new[] { "T", "F", "S", "S", "M", "T", "W" },
                models.Take(3).ToArray(), 2, 0.4);
            ReportCards.SavePng(ReportCards.Weekly(week), Path.Combine(output, $"weekly-{lang}.png"), 1.5);
            ReportCards.SavePng(ReportCards.Weekly(week, false), Path.Combine(output, $"weekly-export-{lang}.png"), 1.5);
            ReportCards.SavePng(ReportCards.Monthly(new MonthlyReportData(zh ? "2026年8月" : "August 2026", 1_000_000_000, 250, providers, models)), Path.Combine(output, $"monthly-{lang}.png"), 1.5);
            var solo = week with { TotalTokens = 568_000_000, TotalDollars = 0,
                Providers = new[] { new ProviderPeriodSlice(DisplayProvider.DeepSeek, 568_000_000) },
                DailyTokens = new long[] { 0, 381_000_000, 187_000_000, 0, 0, 0, 0 },
                TopModels = new[] { new ModelShare("deepseek-v4-pro", 568_000_000, 0, 1, ProviderIdentity.DeepSeekAccent, DisplayProvider.DeepSeek) }, OmittedModelsCount = 0, OmittedPercent = 0 };
            ReportCards.SavePng(ReportCards.Weekly(solo), Path.Combine(output, $"solo-{lang}.png"), 1.5);
            ReportCards.SavePng(ReportCards.Weekly(solo with { TotalTokens = 0, Providers = Array.Empty<ProviderPeriodSlice>(), DailyTokens = new long[7], TopModels = Array.Empty<ModelShare>() }), Path.Combine(output, $"empty-{lang}.png"), 1.5);
        }
        Console.WriteLine("PASS omitted-model accounting; rendered bilingual report fixtures to " + Path.GetFullPath(output));
        app.Shutdown();
    }
}
