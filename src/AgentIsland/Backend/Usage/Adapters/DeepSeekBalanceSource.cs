using AgentIsland.Core.Agents;
using AgentMonitoring.Accounts;
using AgentMonitoring.Balances;

namespace AgentIsland.Backend.Usage.Adapters;

public sealed class DeepSeekBalanceSource : IBalanceSource
{
    private readonly IAccountDirectory? _directory;

    public DeepSeekBalanceSource(IAccountDirectory? directory = null)
    {
        _directory = directory;
    }

    public AgentKey Agent => AgentKeys.DeepSeek;

    public async Task<BalanceFetchResult> FetchAsync(
        AccountRef account,
        CancellationToken cancellationToken = default)
    {
        var live = _directory?.Current(Agent) ?? new AccountRef(Agent, null);
        if (!AccountCredentials.CanUseLive(account, live))
            return new BalanceFetchResult.Failed("account credentials not available");

        var outcome = await DeepSeekBalanceFetcher.Fetch(cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            DeepSeekBalanceFetcher.Outcome.Success success => ToResult(success.Snapshot),
            DeepSeekBalanceFetcher.Outcome.NotConfigured => new BalanceFetchResult.Failed("no deepseek api key"),
            DeepSeekBalanceFetcher.Outcome.Unauthorized => new BalanceFetchResult.Failed("deepseek api key rejected"),
            DeepSeekBalanceFetcher.Outcome.Failed failed => new BalanceFetchResult.Failed(failed.Message),
            _ => new BalanceFetchResult.Failed("unknown balance result"),
        };
    }

    internal static BalanceFetchResult ToResult(AgentIsland.Providers.Usage.DeepSeek.DeepSeekBalanceSnapshot snapshot)
    {
        var row = snapshot.Balances.Count > 0 ? snapshot.Balances[0] : null;
        if (row is null)
            return new BalanceFetchResult.Failed("empty balance");
        return new BalanceFetchResult.Success(
            new AccountBalance(row.Currency, (double)row.TotalBalance, DateTimeOffset.Now),
            snapshot.IsAvailable);
    }
}
