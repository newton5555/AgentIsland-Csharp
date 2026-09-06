using AgentIsland.UI.Providers;
using AgentIsland.UI.Report;

namespace AgentIsland.Tests;

public class ReportDuelTests
{
    [WpfFact]
    public void TestReportDuel() => RunAll();

    internal static void RunAll()
    {
        WpfTestEnvironment.EnsureInitialized();
        foreach (var opponent in new[] { DisplayProvider.Codex, DisplayProvider.Claude })
        foreach (var tokens in new long[] { 20, 50, 80 })
        {
            var first = new ProviderPeriodSlice(opponent, tokens);
            var second = new ProviderPeriodSlice(DisplayProvider.DeepSeek, 100 - tokens);
            var sides = ReportCards.ResolveDuelSides(first, second);
            if (sides.Left != second || sides.Right != first)
                throw new Exception($"DeepSeek/{opponent} must use the existing left-DeepSeek art for {tokens}%.");
            if (ReportCards.ResolveDuelSides(second, first) != (second, first))
                throw new Exception("An available direct pose must preserve its sides.");
        }

        var agy = new ProviderPeriodSlice(DisplayProvider.Antigravity, 20);
        var deepseek = new ProviderPeriodSlice(DisplayProvider.DeepSeek, 80);
        if (ReportCards.ResolveDuelSides(agy, deepseek) != (deepseek, agy))
            throw new Exception("New DeepSeek-win-Antigravity artwork must resolve in reverse slot order.");

        agy = agy with { Tokens = 80 };
        deepseek = deepseek with { Tokens = 20 };
        if (ReportCards.ResolveDuelSides(agy, deepseek) != (agy, deepseek))
            throw new Exception("A missing opposite outcome must not reuse the wrong winner's artwork.");

        var claude = new ProviderPeriodSlice(DisplayProvider.Claude, 70);
        var codex = new ProviderPeriodSlice(DisplayProvider.Codex, 30);
        if (ReportCards.ResolveDuelSides(claude, codex) != (claude, codex))
            throw new Exception("Existing direct Claude/Codex artwork must retain slot order.");
        Console.WriteLine("PASS report duel assets resolve both slot orders without reversing winners or logos");
    }
}
