using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentIsland.Backend.Cost.Adapters;
using AgentIsland.Backend.Monitoring.Sensors;
using AgentIsland.Backend.Usage;

namespace AgentIsland.Backend.Providers;

public sealed class ClaudeAgentProvider : IAgentProvider, ISessionSensor, IUsageFetcher, ICostLedgerReader, IReauthHandler
{
    private readonly ClaudeSessionSensor _sensor = new();
    private readonly ClaudeCostLedgerReader _costReader = new();

    private readonly IClaudeWebLogin _claudeWebLogin;

    public ClaudeAgentProvider(IClaudeWebLogin? claudeWebLogin = null)
    {
        _claudeWebLogin = claudeWebLogin ?? new ClaudeWebLogin();
    }

    public AgentDescriptor Descriptor { get; } = new(
        new AgentKey("claude"),
        "Claude",
        AgentCapabilities.Activity | AgentCapabilities.Usage | AgentCapabilities.Cost | AgentCapabilities.SessionNavigation | AgentCapabilities.Reauthentication,
        "claude");

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default) =>
        _sensor.ScanSessionsAsync(now, lastWorking, ct);

    public async ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default) =>
        await UsageFetcher.FetchClaude(ct).ConfigureAwait(false);

    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
        _costReader.ReadCostEventsAsync(lookbackDays, ct);

    public Task StartReauthAsync(CancellationToken ct = default)
    {
        _ = _claudeWebLogin.Start();
        return Task.CompletedTask;
    }
}
