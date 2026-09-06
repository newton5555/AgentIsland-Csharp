using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentIsland.Backend.Cost.Adapters;
using AgentIsland.Backend.Monitoring.Sensors;
using AgentIsland.Backend.Usage;
using AgentIsland.Providers.Usage.Antigravity;

namespace AgentIsland.Backend.Providers;

public sealed class AntigravityAgentProvider : IAgentProvider, ISessionSensor, IUsageFetcher, ICostLedgerReader
{
    private readonly AntigravitySessionSensor _sensor = new();
    private readonly AntigravityCostLedgerReader _costReader = new();

    public AgentDescriptor Descriptor { get; } = new(
        new AgentKey("antigravity"),
        "Antigravity",
        AgentCapabilities.Activity | AgentCapabilities.Usage | AgentCapabilities.Cost | AgentCapabilities.SessionNavigation,
        "agy");

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default) =>
        _sensor.ScanSessionsAsync(now, lastWorking, ct);

    public async ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default)
    {
        var outcome = await AntigravityUsageFetcher.Fetch(ct).ConfigureAwait(false);
        if (outcome is AntigravityUsageFetcher.Outcome.Success s)
        {
            var snap = s.Snapshot;
            var five = snap.FiveHour;
            var week = snap.Weekly;
            if (five is not null && week is not null)
            {
                return new AppUsage(
                    new WindowUsage(five.UsedPercent, five.ResetAt, null, five.PeriodSeconds),
                    new WindowUsage(week.UsedPercent, week.ResetAt, null, week.PeriodSeconds),
                    snap.TierLabel ?? snap.TierId);
            }
            var single = five ?? week ?? snap.Primary;
            return new AppUsage(
                new WindowUsage(single?.UsedPercent ?? 0, single?.ResetAt, null, single?.PeriodSeconds),
                WindowUsage.Unknown,
                snap.TierLabel ?? snap.TierId);
        }
        if (outcome is AntigravityUsageFetcher.Outcome.Failed f)
        {
            return AppUsage.ErrorPair(f.Message);
        }
        return AppUsage.Empty;
    }

    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
        _costReader.ReadCostEventsAsync(lookbackDays, ct);
}
