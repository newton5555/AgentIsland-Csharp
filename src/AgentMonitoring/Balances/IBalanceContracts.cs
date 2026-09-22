using AgentIsland.Core.Agents;
using AgentIsland.Core.Usage;

namespace AgentMonitoring.Balances;

public abstract record BalanceFetchResult
{
    public sealed record Success(AccountBalance Balance, bool IsAvailable) : BalanceFetchResult;
    public sealed record Failed(string Error) : BalanceFetchResult;
}

public interface IBalanceSource
{
    AgentKey Agent { get; }
    Task<BalanceFetchResult> FetchAsync(AccountRef account, CancellationToken cancellationToken = default);
}

public interface IBalanceStore
{
    RemoteBalanceSnapshot? Read(AccountRef account);
    IReadOnlyList<RemoteBalanceSnapshot> ReadAll();
    IReadOnlyList<RemoteBalanceSnapshot> ReadAll(AgentKey agent);
    void Commit(RemoteBalanceSnapshot snapshot);
}

public interface IBalanceRefresher
{
    Task<RemoteBalanceSnapshot> RefreshAsync(AccountRef account, CancellationToken cancellationToken = default);
    void Invalidate(AccountRef account);
}
