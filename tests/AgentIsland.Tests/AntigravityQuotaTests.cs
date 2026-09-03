using System;
using System.Text;
using AgentIsland.Usage;
using AgentIsland.UI.Charts;

namespace AgentIsland.Tests;

public static class AntigravityQuotaTests
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
        Console.WriteLine("--- AntigravityQuotaTests ---");
        TestDualQuotaParsing();
        TestWeeklyOnlyFallback();
        TestProcessNameMatching();
        Console.WriteLine("AntigravityQuotaTests GREEN");
    }

    private static void TestDualQuotaParsing()
    {
        var json = @"
        {
            ""response"": {
                ""groups"": [
                    {
                        ""displayName"": ""Gemini Models"",
                        ""buckets"": [
                            {
                                ""bucketId"": ""gemini-weekly"",
                                ""window"": ""weekly"",
                                ""remainingFraction"": 0.8
                            },
                            {
                                ""bucketId"": ""gemini-5h"",
                                ""window"": ""5h"",
                                ""remainingFraction"": 0.4
                            }
                        ]
                    }
                ]
            }
        }";

        var parsed = AntigravityQuotaParser.ParseQuotaSummary(Encoding.UTF8.GetBytes(json));
        Expect(parsed is not null, "quota summary must parse");
        var snapshot = new AntigravityQuotaSnapshot(parsed!.Value.Buckets);

        Expect(snapshot.FiveHour is not null, "FiveHour bucket must be detected");
        Expect(snapshot.Weekly is not null, "Weekly bucket must be detected");
        Expect(Math.Abs(snapshot.FiveHour!.UsedPercent - 0.6) < 0.001, "FiveHour UsedPercent = 1 - 0.4 = 0.6");
        Expect(Math.Abs(snapshot.Weekly!.UsedPercent - 0.2) < 0.001, "Weekly UsedPercent = 1 - 0.8 = 0.2");

        Expect(snapshot.Primary == snapshot.FiveHour, "Primary should point to FiveHour when 5h bucket exists");
        Expect(snapshot.FiveHour.PeriodSeconds == 18000, "FiveHour PeriodSeconds must be 5h (18000s)");
        Expect(snapshot.Weekly.PeriodSeconds == 7 * 86400, "Weekly PeriodSeconds must be weekly");

        var fiveLabel = ChartTile.PeriodLabel(new WindowUsage(snapshot.FiveHour.UsedPercent, null, null, snapshot.FiveHour.PeriodSeconds), "5h");
        var weekLabel = ChartTile.PeriodLabel(new WindowUsage(snapshot.Weekly.UsedPercent, null, null, snapshot.Weekly.PeriodSeconds), "week");
        Expect(fiveLabel == "5h", "FiveHour label should be 5h");
        Expect(weekLabel.Length > 0, "Weekly label should not be empty");
    }

    private static void TestWeeklyOnlyFallback()
    {
        var json = @"
        {
            ""response"": {
                ""groups"": [
                    {
                        ""displayName"": ""Gemini Models"",
                        ""buckets"": [
                            {
                                ""bucketId"": ""gemini-weekly"",
                                ""window"": ""weekly"",
                                ""remainingFraction"": 0.75
                            }
                        ]
                    }
                ]
            }
        }";

        var parsed = AntigravityQuotaParser.ParseQuotaSummary(Encoding.UTF8.GetBytes(json));
        Expect(parsed is not null, "weekly-only quota must parse");
        var snapshot = new AntigravityQuotaSnapshot(parsed!.Value.Buckets);

        Expect(snapshot.FiveHour is null, "FiveHour bucket should be null when only weekly is reported");
        Expect(snapshot.Weekly is not null, "Weekly bucket should be present");
        Expect(snapshot.Primary == snapshot.Weekly, "Primary should fall back to Weekly");
    }

    private static void TestProcessNameMatching()
    {
        Expect(AntigravityLanguageServer.IsAntigravityName("Antigravity IDE"), "Antigravity IDE should be matched");
        Expect(AntigravityLanguageServer.IsAntigravityName("agy"), "agy should be matched");
        Expect(AntigravityLanguageServer.IsAntigravityName("antigravity"), "antigravity should be matched");
        Expect(AntigravityLanguageServer.IsAntigravityName("language_server_windows_x64"), "language_server should be matched");
    }
}
