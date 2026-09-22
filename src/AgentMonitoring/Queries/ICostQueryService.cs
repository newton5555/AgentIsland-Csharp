using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;

namespace AgentMonitoring.Queries;

public sealed record CostScanResult(
    AgentKey Agent,
    long ProviderVersion,
    DateTimeOffset ScannedAt,
    IReadOnlyList<TokenEvent> Events,
    ProviderCostSummary Summary);

public interface ICostQueryService
{
    Task<CostScanResult> ScanAsync(
        AgentKey agent,
        int lookbackDays,
        DateTimeOffset now,
        CancellationToken consumerCancellation = default,
        bool force = false);

    Task<CostScanResult> ScanCurrentAsync(
        AgentKey agent,
        DateTimeOffset now,
        CancellationToken consumerCancellation = default);

    void Invalidate(AgentKey agent);
    bool IsCurrent(AgentKey agent, long providerVersion);
}
