using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Backend.Cost.Adapters;
using AgentIsland.Backend.Monitoring.Sensors;
using AgentIsland.Backend.Usage;
using AgentIsland.Backend.Usage.Adapters;
using AgentMonitoring.Balances;

namespace AgentIsland.Backend.Providers;

public sealed class DeepSeekAgentProvider : IAgentProvider, ISessionSensor, ICostLedgerReader, IBalanceFetcher
{
    private readonly DeepSeekSessionSensor _sensor = new();
    private readonly DeepSeekCostLedgerReader _costReader = new();

    public AgentDescriptor Descriptor { get; } = new(
        new AgentKey("deepseek"),
        "DeepSeek",
        AgentCapabilities.Activity | AgentCapabilities.Cost | AgentCapabilities.Balance);

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default) =>
        _sensor.ScanSessionsAsync(now, lastWorking, ct);

    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
        _costReader.ReadCostEventsAsync(lookbackDays, ct);

    public async ValueTask<AccountBalance?> FetchBalanceAsync(CancellationToken ct = default)
    {
        var result = await new DeepSeekBalanceSource().FetchAsync(new AccountRef(AgentKeys.DeepSeek, null), ct).ConfigureAwait(false);
        return result is BalanceFetchResult.Success success ? success.Balance : null;
    }
}
