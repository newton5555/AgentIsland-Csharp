using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentIsland.Backend.Cost.Adapters;
using AgentIsland.Backend.Monitoring.Sensors;
using AgentIsland.Backend.Usage;

namespace AgentIsland.Backend.Providers;

public sealed class CodexAgentProvider : IAgentProvider, ISessionSensor, IUsageFetcher, ICostLedgerReader, IReauthHandler
{
    private readonly CodexSessionSensor _sensor = new();
    private readonly CodexCostLedgerReader _costReader = new();

    public AgentDescriptor Descriptor { get; } = new(
        new AgentKey("codex"),
        "Codex",
        AgentCapabilities.Activity | AgentCapabilities.Usage | AgentCapabilities.Cost | AgentCapabilities.SessionNavigation | AgentCapabilities.Reauthentication,
        "codex");

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default) =>
        _sensor.ScanSessionsAsync(now, lastWorking, ct);

    public async ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default) =>
        await UsageFetcher.FetchCodex(ct).ConfigureAwait(false);

    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
        _costReader.ReadCostEventsAsync(lookbackDays, ct);

    public Task StartReauthAsync(CancellationToken ct = default) =>
        Task.CompletedTask;
}
