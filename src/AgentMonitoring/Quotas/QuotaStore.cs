using AgentIsland.Core.Agents;
using AgentIsland.Core.Usage;

namespace AgentMonitoring.Quotas;

public sealed class QuotaStore : IQuotaStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, QuotaSnapshot> _snapshots = new(StringComparer.Ordinal);

    public QuotaSnapshot? Read(AccountRef account)
    {
        lock (_gate) return _snapshots.TryGetValue(Key(account), out var snapshot) ? snapshot : null;
    }

    public IReadOnlyList<QuotaSnapshot> ReadAll()
    {
        lock (_gate) return _snapshots.Values.ToList();
    }

    public IReadOnlyList<QuotaSnapshot> ReadAll(AgentKey agent)
    {
        lock (_gate)
        {
            return _snapshots.Values
                .Where(snapshot => snapshot.Account.Agent.Value == agent.Value)
                .ToList();
        }
    }

    public void Commit(QuotaSnapshot snapshot)
    {
        lock (_gate) _snapshots[Key(snapshot.Account)] = snapshot;
    }

    public static string Key(AccountRef account) =>
        account.Agent.Value + "\n" + (account.AccountId ?? "");
}
