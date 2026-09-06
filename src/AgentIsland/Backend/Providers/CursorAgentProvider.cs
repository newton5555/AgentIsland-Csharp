using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentIsland.Backend.Cost.Adapters;
using AgentIsland.Backend.Monitoring.Sensors;
using AgentIsland.Backend.Usage;

namespace AgentIsland.Backend.Providers;

public sealed class CursorAgentProvider : IAgentProvider, ISessionSensor, IUsageFetcher, ICostLedgerReader
{
    private readonly CursorSessionSensor _sensor = new();
    private readonly CursorCostLedgerReader _costReader = new();

    public AgentDescriptor Descriptor { get; } = new(
        new AgentKey("cursor"),
        "Cursor",
        AgentCapabilities.Activity | AgentCapabilities.Usage | AgentCapabilities.Cost | AgentCapabilities.SessionNavigation);

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default) =>
        _sensor.ScanSessionsAsync(now, lastWorking, ct);

    public async ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default)
    {
        var outcome = await CursorUsageFetcher.Fetch(ct).ConfigureAwait(false);
        if (outcome is CursorUsageFetcher.Outcome.Success s)
        {
            return new AppUsage(
                new WindowUsage(s.Snapshot.UsedPercent, s.Snapshot.PeriodEnd, null),
                WindowUsage.Unknown,
                s.Snapshot.PlanName ?? "cursor");
        }
        if (outcome is CursorUsageFetcher.Outcome.ReauthRequired)
        {
            return AppUsage.ErrorPair("auth expired — sign into cursor");
        }
        if (outcome is CursorUsageFetcher.Outcome.Failed f)
        {
            return AppUsage.ErrorPair(f.Message);
        }
        return AppUsage.Empty;
    }

    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
        _costReader.ReadCostEventsAsync(lookbackDays, ct);
}
