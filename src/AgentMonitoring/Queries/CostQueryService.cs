using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentMonitoring.Enablement;

namespace AgentMonitoring.Queries;

/// Shared per-agent cost scan. A cancelled consumer only drops its wait;
/// invalidation cancels the shared work. Readers come from IAgentProvider;
/// this type does not reference desktop identity or start UI work.
public sealed class CostQueryService : ICostQueryService
{
    private readonly IAgentEnablement _enablement;
    private readonly IReadOnlyDictionary<AgentKey, ICostLedgerReader> _readers;
    private readonly ILedgerSnapshotStore? _snapshots;

    private sealed class AgentState
    {
        public long Version;
        public Task<CostScanResult>? ScanTask;
        public CancellationTokenSource? ScanCts;
    }

    private readonly object _gate = new();
    private readonly Dictionary<AgentKey, AgentState> _states = new();

    public CostQueryService(
        IAgentEnablement enablement,
        IEnumerable<IAgentProvider>? providers = null,
        ILedgerSnapshotStore? snapshots = null)
    {
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _readers = (providers ?? Array.Empty<IAgentProvider>())
            .Where(provider => provider.CostLedgerReader is not null)
            .ToDictionary(provider => provider.Descriptor.Key, provider => provider.CostLedgerReader!);
        _snapshots = snapshots;
    }

    public Task<CostScanResult> ScanAsync(
        AgentKey agent,
        int lookbackDays,
        DateTimeOffset now,
        CancellationToken consumerCancellation = default,
        bool force = false)
    {
        if (consumerCancellation.IsCancellationRequested)
        {
            return Task.FromCanceled<CostScanResult>(consumerCancellation);
        }

        Task<CostScanResult> shared;
        lock (_gate)
        {
            if (!force && !_enablement.IsEnabled(agent))
            {
                return Task.FromException<CostScanResult>(
                    new AgentDisabledException(agent));
            }
            if (consumerCancellation.IsCancellationRequested)
            {
                return Task.FromCanceled<CostScanResult>(consumerCancellation);
            }
            var state = StateFor(agent);
            if (state.ScanTask is { IsCompleted: false } existing)
            {
                shared = existing;
            }
            else
            {
                state.ScanCts?.Dispose();
                var cts = new CancellationTokenSource();
                var version = state.Version;
                shared = Task.Run(
                    () => ScanCoreAsync(agent, version, lookbackDays, now, cts.Token),
                    CancellationToken.None);
                state.ScanCts = cts;
                state.ScanTask = shared;
                _ = ObserveCompletion(agent, state, shared, cts);
            }
        }

        return consumerCancellation.CanBeCanceled
            ? shared.WaitAsync(consumerCancellation)
            : shared;
    }

    public sealed class AgentDisabledException : InvalidOperationException
    {
        public AgentDisabledException(AgentKey agent)
            : base($"Cost scan skipped because {agent.Value} is disabled.")
        {
            Agent = agent;
        }

        public AgentKey Agent { get; }
    }

    public Task<CostScanResult> ScanCurrentAsync(
        AgentKey agent,
        CancellationToken consumerCancellation = default) =>
        ScanAsync(
            agent,
            CostSummarizer.YearHistoryDays(DateTimeOffset.Now),
            DateTimeOffset.Now,
            consumerCancellation);

    public Task<CostScanResult> ScanCurrentAsync(
        AgentKey agent,
        DateTimeOffset now,
        CancellationToken consumerCancellation = default) =>
        ScanAsync(
            agent,
            CostSummarizer.YearHistoryDays(now),
            now,
            consumerCancellation);

    public void Invalidate(AgentKey agent)
    {
        lock (_gate)
        {
            var state = StateFor(agent);
            state.Version++;
            state.ScanCts?.Cancel();
            state.ScanCts = null;
            state.ScanTask = null;
        }
    }

    public long Version(AgentKey agent)
    {
        lock (_gate) return StateFor(agent).Version;
    }

    public bool IsCurrent(AgentKey agent, long version)
    {
        lock (_gate) return StateFor(agent).Version == version;
    }

    private AgentState StateFor(AgentKey agent)
    {
        if (_states.TryGetValue(agent, out var state)) return state;
        state = new AgentState();
        _states[agent] = state;
        return state;
    }

    private async Task ObserveCompletion(
        AgentKey agent,
        AgentState state,
        Task<CostScanResult> task,
        CancellationTokenSource cts)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(state.ScanTask, task))
                {
                    state.ScanTask = null;
                    state.ScanCts = null;
                }
            }
            cts.Dispose();
        }
    }

    private async Task<CostScanResult> ScanCoreAsync(
        AgentKey agent,
        long providerVersion,
        int lookbackDays,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TokenEvent> events = _readers.TryGetValue(agent, out var reader)
            ? await reader.ReadCostEventsAsync(lookbackDays, cancellationToken).ConfigureAwait(false)
            : Array.Empty<TokenEvent>();
        cancellationToken.ThrowIfCancellationRequested();
        var summary = CostSummarizer.Summarize(events, now);
        _snapshots?.Replace(new LedgerSnapshot(agent, providerVersion, now, events, summary));
        return new CostScanResult(
            agent,
            providerVersion,
            now,
            events,
            summary);
    }
}
