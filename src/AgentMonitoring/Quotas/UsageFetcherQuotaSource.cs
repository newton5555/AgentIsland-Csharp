using AgentIsland.Core.Agents;
using AgentIsland.Core.Usage;
using AgentMonitoring.Accounts;

namespace AgentMonitoring.Quotas;

public sealed class UsageFetcherQuotaSource : IQuotaSource
{
    private readonly IUsageFetcher _fetcher;
    private readonly IAccountDirectory? _directory;

    public UsageFetcherQuotaSource(
        AgentKey agent,
        IUsageFetcher fetcher,
        IAccountDirectory? directory = null)
    {
        Agent = agent;
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _directory = directory;
    }

    public AgentKey Agent { get; }

    public Task<AppUsage> FetchAsync(AccountRef account, CancellationToken cancellationToken = default)
    {
        var live = _directory?.Current(Agent) ?? new AccountRef(Agent, null);
        if (!AccountCredentials.CanUseLive(account, live))
            return Task.FromResult(AppUsage.ErrorPair("account credentials not available"));
        return _fetcher.FetchUsageAsync(cancellationToken).AsTask();
    }
}
