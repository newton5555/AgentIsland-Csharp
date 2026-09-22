using AgentIsland.Core.Cost;

namespace AgentMonitoring.Pricing;

public interface IPricer
{
    PricingQuote Price(ConsumptionFact fact);
}
