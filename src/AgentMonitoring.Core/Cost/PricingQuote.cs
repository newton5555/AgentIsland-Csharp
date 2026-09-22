namespace AgentIsland.Core.Cost;

/// How an amount was obtained. Zero is a valid Official or Estimated value;
/// Unpriced must keep AmountUsd null so UI cannot render it as $0.
public enum CostKind
{
    Official = 0,
    Estimated = 1,
    Unpriced = 2,
}

public sealed record PriceSource(
    string Name,
    string Version,
    DateOnly? SnapshotDate,
    DateTimeOffset EffectiveAt);

/// Result of pricing one <see cref="ConsumptionFact"/>. Facts stay unchanged
/// when the price table moves; a later pass emits a new quote.
public sealed record PricingQuote(
    CostKind Kind,
    double? AmountUsd,
    PriceSource Source,
    string? UnpricedReason)
{
    public static PricingQuote Official(double amountUsd, PriceSource source) =>
        new(CostKind.Official, amountUsd, source, null);

    public static PricingQuote Estimated(double amountUsd, PriceSource source) =>
        new(CostKind.Estimated, amountUsd, source, null);

    public static PricingQuote Unpriced(string reason, PriceSource source) =>
        new(CostKind.Unpriced, null, source, reason);

    public bool HasAmount => Kind != CostKind.Unpriced && AmountUsd is not null;
}
