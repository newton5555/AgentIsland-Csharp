using AgentIsland.Core;
using AgentIsland.Core.Cost;
using AgentMonitoring.Pricing;

namespace AgentMonitoring.Consumption;

public static class FactMapping
{
    public static TokenEvent ToTokenEvent(ConsumptionFact fact, IPricer pricer)
    {
        var quote = pricer.Price(fact);
        double? amount = quote.Kind is CostKind.Official or CostKind.Estimated
            ? quote.AmountUsd
            : null;
        return new TokenEvent(
            TriggerTool.Codex,
            fact.Timestamp,
            fact.Model.Canonical,
            fact.Tokens.Input,
            fact.Tokens.Output,
            fact.Tokens.CacheCreation,
            fact.Tokens.CacheRead,
            amount,
            fact.Tokens.Reasoning,
            fact.Tokens.ReasoningAccounting,
            fact.Pricing.ServiceTier,
            fact.Pricing.LongContext,
            fact.Source.RecordId,
            fact.Source.SessionId,
            fact.Source.ProjectId,
            fact.Source.AccountId,
            fact.Source.SourcePath);
    }
}
