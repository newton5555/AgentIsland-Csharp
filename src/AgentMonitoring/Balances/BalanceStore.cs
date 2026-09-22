using AgentIsland.Core.Agents;
using AgentIsland.Core.Usage;
using AgentMonitoring.Quotas;

namespace AgentMonitoring.Balances;

public sealed class BalanceStore : IBalanceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RemoteBalanceSnapshot> _snapshots = new(StringComparer.Ordinal);

    public RemoteBalanceSnapshot? Read(AccountRef account)
    {
        lock (_gate) return _snapshots.TryGetValue(QuotaStore.Key(account), out var snapshot) ? snapshot : null;
    }

    public IReadOnlyList<RemoteBalanceSnapshot> ReadAll()
    {
        lock (_gate) return _snapshots.Values.ToList();
    }

    public IReadOnlyList<RemoteBalanceSnapshot> ReadAll(AgentKey agent)
    {
        lock (_gate)
        {
            return _snapshots.Values
                .Where(snapshot => snapshot.Account.Agent.Value == agent.Value)
                .ToList();
        }
    }

    public void Commit(RemoteBalanceSnapshot snapshot)
    {
        lock (_gate) _snapshots[QuotaStore.Key(snapshot.Account)] = snapshot;
    }
}
