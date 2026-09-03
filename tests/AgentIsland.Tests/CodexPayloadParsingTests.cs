using AgentIsland.Core;
using AgentIsland.Usage;
using AgentIsland.UI.Charts;
using System.Text.Json;
using System;

namespace AgentIsland.Tests;

public static class CodexPayloadParsingTests
{
    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            Console.WriteLine($"FAIL: {message}");
            Environment.Exit(1);
        }
    }

    public static void RunAll()
    {
        Console.WriteLine("--- CodexPayloadParsingTests ---");
        TestParseCodexWindow();
        TestBothWindows();
        TestPrimaryOnlyWindow();
        Console.WriteLine("CodexPayloadParsingTests GREEN");
    }

    private static void TestParseCodexWindow()
    {
        var json = @"{ ""window"": { ""used_percent"": 10, ""limit_window_seconds"": 18000 } }";
        using var doc = JsonDocument.Parse(json);
        var element = doc.RootElement.GetProperty("window");
        var result = UsageFetcher.ParseCodexWindow(element);
        
        Expect(Math.Abs(result.UsedPercent - 0.1) < 0.0001, "used_percent should be parsed correctly");
        Expect(result.PeriodSeconds == 18000, "period should be parsed correctly");
    }

    private static void TestBothWindows()
    {
        var json = @"
        {
            ""plan_type"": ""plus"",
            ""rate_limit"": {
                ""primary_window"": {
                    ""limit_window_seconds"": 18000,
                    ""used_percent"": 20
                },
                ""secondary_window"": {
                    ""limit_window_seconds"": 604800,
                    ""used_percent"": 30
                }
            }
        }";
        using var doc = JsonDocument.Parse(json);
        var rateLimit = Jsonl.GetObject(doc.RootElement, "rate_limit") ?? throw new Exception("missing rate_limit");
        
        var primary = UsageFetcher.ParseCodexWindow(Jsonl.GetObject(rateLimit, "primary_window"));
        var secondary = UsageFetcher.ParseCodexWindow(Jsonl.GetObject(rateLimit, "secondary_window"));
        
        var appUsage = new AppUsage(primary, secondary, "plus");
        
        Expect(!appUsage.SecondaryMissing, "Secondary should not be missing");
        Expect(appUsage.FiveHour.PeriodSeconds == 18000, "Primary period is 18000");
        Expect(appUsage.Weekly.PeriodSeconds == 604800, "Secondary period is 604800");
        Expect(Math.Abs(appUsage.FiveHour.UsedPercent - 0.2) < 0.0001, "Primary used_percent should be 0.2");
        Expect(Math.Abs(appUsage.Weekly.UsedPercent - 0.3) < 0.0001, "Secondary used_percent should be 0.3");
        
        var primaryLabel = ChartTile.PeriodLabel(appUsage.FiveHour, "5h");
        var secondaryLabel = ChartTile.PeriodLabel(appUsage.Weekly, "week");
        var weekTag = AgentIsland.Localization.L10n.Tr("week");
        Expect(primaryLabel == "5h", $"Primary label should be 5h, was {primaryLabel}");
        Expect(secondaryLabel == weekTag, $"Secondary label should be {weekTag}, was {secondaryLabel}");
    }

    private static void TestPrimaryOnlyWindow()
    {
        var json = @"
        {
            ""plan_type"": ""plus"",
            ""rate_limit"": {
                ""primary_window"": {
                    ""limit_window_seconds"": 604800,
                    ""used_percent"": 30
                }
            }
        }";
        using var doc = JsonDocument.Parse(json);
        var rateLimit = Jsonl.GetObject(doc.RootElement, "rate_limit") ?? throw new Exception("missing rate_limit");
        
        var primary = UsageFetcher.ParseCodexWindow(Jsonl.GetObject(rateLimit, "primary_window"));
        var secondary = UsageFetcher.ParseCodexWindow(Jsonl.GetObject(rateLimit, "secondary_window"));
        
        var appUsage = new AppUsage(primary, secondary, "plus");
        
        Expect(appUsage.SecondaryMissing, "Secondary should be missing when only primary is provided");
        Expect(appUsage.FiveHour.PeriodSeconds == 604800, "Primary period is 604800");
        Expect(appUsage.Weekly.Error == "no data", "Secondary error should be 'no data'");
        
        var primaryLabel = ChartTile.PeriodLabel(appUsage.FiveHour, "5h");
        var weekTag = AgentIsland.Localization.L10n.Tr("week");
        Expect(primaryLabel == weekTag, $"Primary label should be {weekTag}, was {primaryLabel}");
    }
}

