using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Backend.Cost.Adapters;
using AgentIsland.Backend.Monitoring.Sensors;

namespace AgentIsland.Backend.Providers;

public sealed class DeepSeekAgentProvider : IAgentProvider, ISessionSensor, ICostLedgerReader
{
    private readonly DeepSeekSessionSensor _sensor = new();
    private readonly DeepSeekCostLedgerReader _costReader = new();

    public AgentDescriptor Descriptor { get; } = new(
        new AgentKey("deepseek"),
        "DeepSeek",
        AgentCapabilities.Activity | AgentCapabilities.Cost);

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default) =>
        _sensor.ScanSessionsAsync(now, lastWorking, ct);

    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
        _costReader.ReadCostEventsAsync(lookbackDays, ct);
}
