using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Runtime.Scanning;

namespace AgentIsland.Runtime.Snapshots;

/// <summary>
/// Platform-neutral snapshot source for local token log and cost rollups.
/// Bridges a host's log file discovery and parser with <see cref="TokenLogScanner"/>
/// and <see cref="CostSummarizer"/> without UI or platform-specific dependencies.
/// </summary>
public sealed class TokenCostSnapshotSource : IAgentSnapshotSource
{
    private readonly TokenLogScanner _scanner;
    private readonly Func<IEnumerable<string>> _fileProvider;
    private readonly int _lookbackDays;

    public AgentKey Agent { get; }
    public TriggerTool TriggerTool { get; }
    public int LookbackDays => _lookbackDays;

    public TokenCostSnapshotSource(
        AgentKey agent,
        TriggerTool triggerTool,
        string cacheFilePath,
        Func<string, List<TokenEvent>> parser,
        Func<IEnumerable<string>> fileProvider,
        int lookbackDays = 30)
        : this(agent, triggerTool, new TokenLogScanner(triggerTool, cacheFilePath, parser), fileProvider, lookbackDays)
    {
    }

    public TokenCostSnapshotSource(
        AgentKey agent,
        TriggerTool triggerTool,
        TokenLogScanner scanner,
        Func<IEnumerable<string>> fileProvider,
        int lookbackDays = 30)
    {
        if (string.IsNullOrWhiteSpace(agent.Value))
            throw new ArgumentException("Agent key cannot be empty.", nameof(agent));
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(fileProvider);
        if (lookbackDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(lookbackDays), "Lookback days must be greater than zero.");

        Agent = agent;
        TriggerTool = triggerTool;
        _scanner = scanner;
        _fileProvider = fileProvider;
        _lookbackDays = lookbackDays;
    }

    public async Task<AgentSnapshot> ReadAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cutoff = observedAt.AddDays(-_lookbackDays);
        var files = _fileProvider();

        cancellationToken.ThrowIfCancellationRequested();

        var events = await _scanner.ScanAsync(
            files ?? Enumerable.Empty<string>(),
            cutoff,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (events is null || events.Count == 0)
        {
            return AgentSnapshot.NoData(Agent, ActivityState.Idle, observedAt);
        }

        var latestTimestamp = events[0].Timestamp;
        for (var i = 1; i < events.Count; i++)
        {
            if (events[i].Timestamp > latestTimestamp)
            {
                latestTimestamp = events[i].Timestamp;
            }
        }

        var summary = CostSummarizer.Summarize(events, observedAt);

        return new AgentSnapshot(
            Agent,
            ActivityState.Idle,
            SnapshotAvailability.Ready,
            observedAt,
            DataAt: latestTimestamp,
            Cost: summary);
    }
}
