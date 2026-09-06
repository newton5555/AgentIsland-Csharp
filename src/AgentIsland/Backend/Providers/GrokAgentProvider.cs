using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentIsland.Backend.Cost.Adapters;
using AgentIsland.Backend.Monitoring.Sensors;
using AgentIsland.Backend.Usage;

namespace AgentIsland.Backend.Providers;

public sealed class GrokAgentProvider : IAgentProvider, ISessionSensor, IUsageFetcher, ICostLedgerReader
{
    private readonly GrokSessionSensor _sensor = new();
    private readonly GrokCostLedgerReader _costReader = new();

    public AgentDescriptor Descriptor { get; } = new(
        new AgentKey("grok"),
        "Grok",
        AgentCapabilities.Activity | AgentCapabilities.Usage | AgentCapabilities.Cost | AgentCapabilities.SessionNavigation,
        "grok");

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default) =>
        _sensor.ScanSessionsAsync(now, lastWorking, ct);

    public async ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default)
    {
        var outcome = await GrokUsageFetcher.Fetch(ct).ConfigureAwait(false);
        if (outcome is GrokUsageFetcher.Outcome.Success s)
        {
            return new AppUsage(
                new WindowUsage(s.Snapshot.WeeklyUsedPercent, s.Snapshot.WeeklyPeriodEnd, null),
                WindowUsage.Unknown,
                "grok");
        }
        if (outcome is GrokUsageFetcher.Outcome.ReauthRequired)
        {
            return AppUsage.ErrorPair("auth expired — grok login");
        }
        if (outcome is GrokUsageFetcher.Outcome.Failed f)
        {
            return AppUsage.ErrorPair(f.Message);
        }
        return AppUsage.Empty;
    }

    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
        _costReader.ReadCostEventsAsync(lookbackDays, ct);
}
