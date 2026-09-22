using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;

namespace AgentMonitoring.Queries;

public sealed record LedgerSnapshot(
    AgentKey Agent,
    long Version,
    DateTimeOffset ScannedAt,
    IReadOnlyList<TokenEvent> Events,
    ProviderCostSummary Summary);

public interface ILedgerSnapshotStore
{
    LedgerSnapshot? Read(AgentKey agent);
    IReadOnlyList<LedgerSnapshot> ReadAll();
    void Replace(LedgerSnapshot snapshot);
}

public sealed class LedgerSnapshotStore : ILedgerSnapshotStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LedgerSnapshot> _snapshots = new(StringComparer.Ordinal);

    public LedgerSnapshot? Read(AgentKey agent)
    {
        lock (_gate) return _snapshots.TryGetValue(agent.Value, out var snapshot) ? snapshot : null;
    }

    public IReadOnlyList<LedgerSnapshot> ReadAll()
    {
        lock (_gate) return _snapshots.Values.ToList();
    }

    public void Replace(LedgerSnapshot snapshot)
    {
        lock (_gate) _snapshots[snapshot.Agent.Value] = snapshot;
    }
}
