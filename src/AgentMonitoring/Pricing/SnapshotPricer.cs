using AgentIsland.Core.Cost;

namespace AgentMonitoring.Pricing;

/// Prices stored facts from the embedded snapshot. Official self-reported
/// amounts win. Unknown models are Unpriced (AmountUsd stays null).
public sealed class SnapshotPricer : IPricer
{
    public const string SourceName = "embedded-snapshot";
    public const string SourceVersion = "p2";

    public PricingQuote Price(ConsumptionFact fact)
    {
        var source = new PriceSource(
            SourceName,
            SourceVersion,
            AgentIsland.Core.Cost.Pricing.SnapshotDate,
            fact.Pricing.EventTime);

        if (fact.Pricing.OfficialCostUsd is { } official)
            return PricingQuote.Official(official, source);

        var tokens = fact.Tokens;
        if (AgentIsland.Core.Cost.Pricing.TryCost(
                fact.Model.Canonical,
                tokens.Input,
                tokens.Output,
                tokens.CacheCreation,
                tokens.CacheRead,
                out var dollars))
        {
            return PricingQuote.Estimated(dollars, source);
        }

        return PricingQuote.Unpriced("unknown model", source);
    }
}
