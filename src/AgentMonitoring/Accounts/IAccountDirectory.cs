using AgentIsland.Core.Agents;

namespace AgentMonitoring.Accounts;

/// Live credential fetches must name the same account they will be stored
/// under. A parked AccountRef must not be filled from the current login.
public static class AccountCredentials
{
    public static bool CanUseLive(AccountRef requested, AccountRef live)
    {
        var requestedId = string.IsNullOrWhiteSpace(requested.AccountId) ? null : requested.AccountId;
        var liveId = string.IsNullOrWhiteSpace(live.AccountId) ? null : live.AccountId;
        return string.Equals(requestedId, liveId, StringComparison.Ordinal);
    }
}

public interface IAccountDirectory
{
    AccountRef Current(AgentKey agent);
    IReadOnlyList<AccountRef> List(AgentKey agent);
}

/// Test and default directory: one unknown-current account per agent unless
/// Remember is used.
public sealed class MemoryAccountDirectory : IAccountDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AccountRef> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AccountRef>> _all = new(StringComparer.Ordinal);

    public AccountRef Current(AgentKey agent)
    {
        lock (_gate)
        {
            if (_current.TryGetValue(agent.Value, out var account)) return account;
            return new AccountRef(agent, null);
        }
    }

    public IReadOnlyList<AccountRef> List(AgentKey agent)
    {
        lock (_gate)
        {
            return _all.TryGetValue(agent.Value, out var list)
                ? list.ToList()
                : new[] { Current(agent) };
        }
    }

    public void Remember(AccountRef account, bool makeCurrent = true)
    {
        lock (_gate)
        {
            if (!_all.TryGetValue(account.Agent.Value, out var list))
            {
                list = new List<AccountRef>();
                _all[account.Agent.Value] = list;
            }

            if (!list.Any(item => item.AccountId == account.AccountId))
                list.Add(account);
            if (makeCurrent) _current[account.Agent.Value] = account;
        }
    }
}
