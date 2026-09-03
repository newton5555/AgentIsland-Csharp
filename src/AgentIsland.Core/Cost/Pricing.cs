namespace AgentIsland.Cost;

/// Hardcoded price table (USD per million tokens), snapshot 2026-06-10,
/// mirroring LiteLLM's model_prices_and_context_window.json — identical to
/// the macOS app's table. Unknown models price at $0 (ccusage parity) and
/// are surfaced so the UI can warn about unpriced spend.
public static class Pricing
{
    public static readonly DateOnly SnapshotDate = new(2026, 6, 10);

    private sealed record Price(double Input, double Output, double CacheCreation, double CacheRead);

    private static readonly Dictionary<string, Price> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // Anthropic
        ["claude-fable-5"] = new(10, 50, 12.5, 1.0),
        ["claude-mythos-5"] = new(10, 50, 12.5, 1.0),
        ["claude-opus-5"] = new(5, 25, 6.25, 0.5),
        ["claude-opus-4-8"] = new(5, 25, 6.25, 0.5),
        ["claude-opus-4-7"] = new(5, 25, 6.25, 0.5),
        ["claude-opus-4-6"] = new(5, 25, 6.25, 0.5),
        ["claude-opus-4-5"] = new(5, 25, 6.25, 0.5),
        ["claude-opus-4-1"] = new(15, 75, 18.75, 1.5),
        ["claude-sonnet-5"] = new(3, 15, 3.75, 0.3),
        ["claude-sonnet-4-6"] = new(3, 15, 3.75, 0.3),
        ["claude-sonnet-4-5"] = new(3, 15, 3.75, 0.3),
        ["claude-haiku-4-5"] = new(1, 5, 1.25, 0.1),
        // OpenAI
        ["gpt-5.6-sol"] = new(5, 30, 5, 0.5),
        ["gpt-5.6-terra"] = new(2.5, 15, 2.5, 0.25),
        ["gpt-5.6-luna"] = new(1, 6, 1, 0.1),
        ["gpt-5.5"] = new(5, 30, 5, 0.5),
        ["gpt-5.4"] = new(2.5, 15, 2.5, 0.25),
        ["gpt-5.2"] = new(1.75, 14, 1.75, 0.175),
        ["gpt-5.1"] = new(1.25, 10, 1.25, 0.125),
        ["gpt-5"] = new(1.25, 10, 1.25, 0.125),
        ["gpt-5.3-codex"] = new(1.75, 14, 1.75, 0.175),
        ["gpt-5.2-codex"] = new(1.75, 14, 1.75, 0.175),
        ["gpt-5.1-codex"] = new(1.25, 10, 1.25, 0.125),
        ["gpt-5.1-codex-max"] = new(1.25, 10, 1.25, 0.125),
        ["gpt-5.1-codex-mini"] = new(0.25, 2, 0.25, 0.025),
        ["gpt-5-codex"] = new(1.25, 10, 1.25, 0.125),
        ["gpt-5.4-mini"] = new(0.75, 4.5, 0.75, 0.075),
        ["gpt-5.4-nano"] = new(0.2, 1.25, 0.2, 0.02),
        ["gpt-5-mini"] = new(0.25, 2, 0.25, 0.025),
        ["gpt-5-nano"] = new(0.05, 0.4, 0.05, 0.005),
        ["gpt-5.5-pro"] = new(30, 180, 30, 3),
        ["gpt-5.4-pro"] = new(30, 180, 30, 3),
        ["gpt-5.2-pro"] = new(21, 168, 21, 0),
        ["gpt-5-pro"] = new(15, 120, 15, 0),
    };

    public static double Cost(TokenEvent tokenEvent)
    {
        if (!Table.TryGetValue(CanonicalModelName(tokenEvent.Model), out var price)) return 0;
        return (tokenEvent.InputTokens * price.Input
                + tokenEvent.OutputTokens * price.Output
                + tokenEvent.CacheCreationTokens * price.CacheCreation
                + tokenEvent.CacheReadTokens * price.CacheRead) / 1_000_000.0;
    }

    public static bool IsKnown(string model) => Table.ContainsKey(CanonicalModelName(model));

    /// Date-suffixed model ids ("claude-haiku-4-5-20251001") strip to their
    /// base form: the suffix is a dash followed by exactly 8 digits.
    public static string CanonicalModelName(string model)
    {
        if (model.Length > 9)
        {
            var suffix = model[^9..];
            if (suffix[0] == '-' && suffix[1..].All(char.IsAsciiDigit))
            {
                return model[..^9];
            }
        }
        return model;
    }
}
