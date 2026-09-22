using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentMonitoring.Consumption;
using AgentMonitoring.Enablement;
using AgentMonitoring.Queries;

namespace AgentMonitoring.Runtime;

/// Collects cost for any AgentKey that registered a ledger reader. Codex
/// still runs its incremental collector first. DisplayProvider is not used.
/// Consumer cancellation only drops the wait; the shared collect uses an
/// internal token.
public sealed class AgentRuntime
{
    private readonly ILedgerSnapshotStore _snapshots;
    private readonly IAgentEnablement _enablement;
    private readonly IReadOnlyDictionary<AgentKey, ICostLedgerReader> _readers;
    private readonly IConsumptionCollector? _codexCollector;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    private sealed class Slot
    {
        public Task<CostScanResult>? Task;
        public CancellationTokenSource? Cts;
    }

    public AgentRuntime(
        ILedgerSnapshotStore snapshots,
        IAgentEnablement enablement,
        IEnumerable<IAgentProvider> providers,
        IConsumptionCollector? codexCollector = null)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _readers = (providers ?? Array.Empty<IAgentProvider>())
            .Where(provider => provider.CostLedgerReader is not null)
            .ToDictionary(provider => provider.Descriptor.Key, provider => provider.CostLedgerReader!);
        _codexCollector = codexCollector;
    }

    public IReadOnlyList<AgentKey> CostAgents => _readers.Keys.ToList();

    public Task<CostScanResult> CollectCostAsync(
        AgentKey agent,
        int lookbackDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!_enablement.IsEnabled(agent))
            return Task.FromException<CostScanResult>(new CostQueryService.AgentDisabledException(agent));
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<CostScanResult>(cancellationToken);

        Task<CostScanResult> shared;
        lock (_gate)
        {
            var slot = SlotFor(agent);
            if (slot.Task is { IsCompleted: false } existing)
            {
                shared = existing;
            }
            else
            {
                slot.Cts?.Dispose();
                var cts = new CancellationTokenSource();
                shared = Task.Run(() => CollectCore(agent, lookbackDays, now, cts.Token), CancellationToken.None);
                slot.Cts = cts;
                slot.Task = shared;
                _ = Observe(slot, shared, cts);
            }
        }

        return cancellationToken.CanBeCanceled ? shared.WaitAsync(cancellationToken) : shared;
    }

    private Slot SlotFor(AgentKey agent)
    {
        if (_slots.TryGetValue(agent.Value, out var slot)) return slot;
        slot = new Slot();
        _slots[agent.Value] = slot;
        return slot;
    }

    private async Task Observe(Slot slot, Task<CostScanResult> task, CancellationTokenSource cts)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(slot.Task, task))
                {
                    slot.Task = null;
                    slot.Cts = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task<CostScanResult> CollectCore(
        AgentKey agent,
        int lookbackDays,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (agent.Value == AgentKeys.Codex.Value && _codexCollector is not null)
            await _codexCollector.CollectAsync(agent, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TokenEvent> events = _readers.TryGetValue(agent, out var reader)
            ? await reader.ReadCostEventsAsync(lookbackDays, cancellationToken).ConfigureAwait(false)
            : Array.Empty<TokenEvent>();
        var summary = CostSummarizer.Summarize(events, now);
        var result = new CostScanResult(agent, 0, now, events, summary);
        _snapshots.Replace(new LedgerSnapshot(agent, 0, now, events, summary));
        return result;
    }
}
