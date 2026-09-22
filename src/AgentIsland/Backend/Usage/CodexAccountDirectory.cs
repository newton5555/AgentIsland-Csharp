using AgentIsland.Core.Agents;
using AgentMonitoring.Accounts;

namespace AgentIsland.Backend.Usage;

/// Maps parked Codex logins to AccountRef. Unknown live credentials stay
/// AccountId null instead of inheriting the previous parked label.
public sealed class CodexAccountDirectory : IAccountDirectory
{
    public AccountRef Current(AgentKey agent)
    {
        if (agent.Value != AgentKeys.Codex.Value)
            return new AccountRef(agent, null);
        return new AccountRef(agent, CodexAccountSwitcher.ActiveLabel());
    }

    public IReadOnlyList<AccountRef> List(AgentKey agent)
    {
        if (agent.Value != AgentKeys.Codex.Value)
            return new[] { Current(agent) };
        var listed = CodexAccountSwitcher.Accounts()
            .Select(account => new AccountRef(agent, account.Label, account.Label))
            .ToList();
        var current = Current(agent);
        if (listed.All(item => item.AccountId != current.AccountId))
            listed.Insert(0, current);
        return listed;
    }
}
