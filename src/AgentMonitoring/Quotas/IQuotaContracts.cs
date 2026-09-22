using AgentIsland.Core.Agents;
using AgentIsland.Core.Usage;

namespace AgentMonitoring.Quotas;

public interface IQuotaSource
{
    AgentKey Agent { get; }
    Task<AppUsage> FetchAsync(AccountRef account, CancellationToken cancellationToken = default);
}

public interface IQuotaStore
{
    QuotaSnapshot? Read(AccountRef account);
    IReadOnlyList<QuotaSnapshot> ReadAll();
    IReadOnlyList<QuotaSnapshot> ReadAll(AgentKey agent);
    void Commit(QuotaSnapshot snapshot);
}

public interface IQuotaRefresher
{
    Task<QuotaSnapshot> RefreshAsync(AccountRef account, CancellationToken cancellationToken = default);
    void Invalidate(AccountRef account);
}
