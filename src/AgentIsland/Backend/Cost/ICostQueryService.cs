using AgentIsland.Core.Cost;
using AgentIsland.UI.Providers;

namespace AgentIsland.Backend.Cost;

public interface ICostQueryService
{
    Task<CostScanResult> ScanAsync(
        DisplayProvider provider,
        int lookbackDays,
        DateTimeOffset now,
        CancellationToken consumerCancellation = default);

    Task<CostScanResult> ScanCurrentAsync(
        DisplayProvider provider,
        DateTimeOffset now,
        CancellationToken consumerCancellation = default);

    void Invalidate(DisplayProvider provider);
    bool IsCurrent(DisplayProvider provider, long providerVersion);
}
