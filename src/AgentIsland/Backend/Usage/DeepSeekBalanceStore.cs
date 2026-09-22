using System.ComponentModel;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.Providers.Usage.DeepSeek;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Providers;

namespace AgentIsland.Backend.Usage;

/// A short-lived cache envelope for the official DeepSeek account balance.
/// The API key itself is never persisted by Agent Island.
public sealed class DeepSeekCachedBalance
{
    public DeepSeekBalanceSnapshot? Snapshot { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// Publishes DeepSeek's account-level currency balance. It rides the existing
/// provider refresh cadence but stays separate from UsageStore because this is
/// a balance snapshot, not a percentage quota window.
public sealed class DeepSeekBalanceStore : IDeepSeekBalanceStore
{
    private const string CacheKey = "DeepSeekBalanceStore.lastSnapshot.v1";
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MinAttemptGap = TimeSpan.FromSeconds(120);

    private readonly IProviderVisibilityStore _visibilityStore;
    private readonly AgentIsland.Core.Threading.IUiDispatcher? _dispatcher;
    private readonly AgentMonitoring.Balances.IBalanceStore? _balanceStore;
    private readonly AgentMonitoring.Accounts.IAccountDirectory? _accounts;

    private DeepSeekBalanceSnapshot? _snapshot;
    private string? _errorCaption;
    private DateTimeOffset? _lastUpdated;
    private DateTimeOffset? _lastAttempt;
    private bool _loading;
    private long _refreshGeneration;
    private CancellationTokenSource? _refreshCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DeepSeekBalanceStore(
        IProviderVisibilityStore visibilityStore,
        AgentIsland.Core.Threading.IUiDispatcher? dispatcher = null,
        AgentMonitoring.Balances.IBalanceStore? balanceStore = null,
        AgentMonitoring.Accounts.IAccountDirectory? accounts = null)
    {
        _visibilityStore = visibilityStore ?? throw new ArgumentNullException(nameof(visibilityStore));
        _dispatcher = dispatcher;
        _balanceStore = balanceStore;
        _accounts = accounts;

        if (AppEnvironment.IsDemo)
        {
            if (_visibilityStore.DeepSeekPanelShown)
            {
                _snapshot = new DeepSeekBalanceSnapshot(
                    true,
                    new[] { new DeepSeekBalanceInfo("CNY", 12.34m, 10.00m, 2.34m) });
                _lastUpdated = DateTimeOffset.Now;
            }
            return;
        }

        if (Preferences.Get<DeepSeekCachedBalance?>(CacheKey) is not { } cached) return;
        if (cached.Snapshot is not { } restored) return;
        if (DateTimeOffset.Now - cached.UpdatedAt > CacheMaxAge) return;
        _snapshot = restored;
        _lastUpdated = cached.UpdatedAt;
    }

    public DeepSeekBalanceSnapshot? Snapshot
    {
        get => _snapshot;
        private set { _snapshot = value; Raise(nameof(Snapshot)); }
    }

    /// Non-null when the latest request failed. A previous good snapshot is
    /// intentionally preserved so an intermittent network error does not turn
    /// a known balance into a misleading zero.
    public string? ErrorCaption
    {
        get => _errorCaption;
        private set { _errorCaption = value; Raise(nameof(ErrorCaption)); }
    }

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        private set { _lastUpdated = value; Raise(nameof(LastUpdated)); }
    }

    public bool Loading
    {
        get => _loading;
        private set { _loading = value; Raise(nameof(Loading)); }
    }

    private void CompleteRefresh(long generation, CancellationTokenSource cts)
    {
        try
        {
            // Check ownership when the UI callback runs, after any disable/re-enable.
            if (generation != _refreshGeneration || !ReferenceEquals(_refreshCts, cts)) return;
            _refreshCts = null;
            Loading = false;
        }
        finally { cts.Dispose(); }
    }


    /// Release the in-process balance snapshot while retaining the persisted
    /// value for a later enable. Generation checks prevent a late response
    /// from reviving the disabled provider.
    public void ClearMemory()
    {
        _refreshGeneration++;
        _refreshCts?.Cancel();
        _refreshCts = null;
        Snapshot = null;
        ErrorCaption = null;
        LastUpdated = null;
        _lastAttempt = null;
        Loading = false;
    }

    /// Whether an official key is currently available. Reading this property
    /// never exposes the secret; it is only used to choose the empty-state
    /// caption in the UI.
    public bool Configured => !string.IsNullOrWhiteSpace(DeepSeekCredentials.ReadApiKey());

    /// Shared refresh cadence entry point. No request is made unless DeepSeek
    /// occupies a detected island slot, and bursts are coalesced with a small
    /// floor so the manual button plus a timer tick cannot double-poll.
    public void KickRefresh() => KickRefresh(force: false);

    public void KickRefresh(bool force = false)
    {
        if (AppEnvironment.IsDemo) return;
        if (!_visibilityStore.DeepSeekPanelShown) return;
        if (Loading) return;
        if (!force && _lastAttempt is { } last && DateTimeOffset.Now - last < MinAttemptGap) return;

        RestoreCachedSnapshot();
        _lastAttempt = DateTimeOffset.Now;
        Loading = true;
        var generation = ++_refreshGeneration;
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var dispatcher = _dispatcher != null
            ? (Action<Action>)(act => _dispatcher.BeginInvoke(act))
            : (Action<Action>)(act => (System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher).BeginInvoke(act));
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await DeepSeekBalanceFetcher.Fetch(cts.Token);
                if (cts.IsCancellationRequested || generation != _refreshGeneration) return;
                dispatcher(() =>
                {
                    if (cts.IsCancellationRequested || generation != _refreshGeneration
                        || !_visibilityStore.DeepSeekPanelShown) return;
                    Apply(outcome);
                });
            }
            catch (Exception error)
            {
                if (cts.IsCancellationRequested || generation != _refreshGeneration) return;
                try
                {
                    dispatcher(() =>
                    {
                        if (generation != _refreshGeneration
                            || !_visibilityStore.DeepSeekPanelShown) return;
                        Apply(new DeepSeekBalanceFetcher.Outcome.Failed(error.Message));
                    });
                }
                catch { }
            }
            finally
            {
                try { dispatcher(() => CompleteRefresh(generation, cts)); }
                catch { cts.Dispose(); }
            }
        });
    }

    public void Refresh() => KickRefresh(force: true);

    private void Apply(DeepSeekBalanceFetcher.Outcome outcome)
    {
        Loading = false;
        switch (outcome)
        {
            case DeepSeekBalanceFetcher.Outcome.Success success:
                Snapshot = success.Snapshot;
                ErrorCaption = null;
                LastUpdated = DateTimeOffset.Now;
                Persist(success.Snapshot);
                PublishBalance(Adapters.DeepSeekBalanceSource.ToResult(success.Snapshot));
                break;
            case DeepSeekBalanceFetcher.Outcome.NotConfigured:
                ErrorCaption = "no deepseek api key";
                PublishBalance(new AgentMonitoring.Balances.BalanceFetchResult.Failed("no deepseek api key"));
                break;
            case DeepSeekBalanceFetcher.Outcome.Unauthorized:
                ErrorCaption = "deepseek api key rejected";
                PublishBalance(new AgentMonitoring.Balances.BalanceFetchResult.Failed("deepseek api key rejected"));
                break;
            case DeepSeekBalanceFetcher.Outcome.Failed failed:
                ErrorCaption = failed.Message;
                PublishBalance(new AgentMonitoring.Balances.BalanceFetchResult.Failed(failed.Message));
                break;
        }
    }

    private void PublishBalance(AgentMonitoring.Balances.BalanceFetchResult result)
    {
        if (_balanceStore is null) return;
        var account = _accounts?.Current(AgentIsland.Core.Agents.AgentKeys.DeepSeek)
            ?? new AgentIsland.Core.Agents.AccountRef(AgentIsland.Core.Agents.AgentKeys.DeepSeek, null);
        var previous = _balanceStore.Read(account);
        var now = DateTimeOffset.Now;
        AgentIsland.Core.Usage.RemoteBalanceSnapshot snapshot = result switch
        {
            AgentMonitoring.Balances.BalanceFetchResult.Success success => new(
                account,
                success.Balance.Currency,
                success.Balance.Amount,
                now,
                now,
                null,
                success.IsAvailable),
            AgentMonitoring.Balances.BalanceFetchResult.Failed failed when previous is { SucceededAt: not null } keep =>
                keep with { FetchedAt = now, Error = failed.Error },
            AgentMonitoring.Balances.BalanceFetchResult.Failed failed => new(
                account, "", 0, now, null, failed.Error, false),
            _ => previous ?? new(account, "", 0, now, null, "unknown balance result", false),
        };
        _balanceStore.Commit(snapshot);
    }

    private static void Persist(DeepSeekBalanceSnapshot fresh) => Preferences.Set(
        CacheKey,
        new DeepSeekCachedBalance { Snapshot = fresh, UpdatedAt = DateTimeOffset.Now });

    private void RestoreCachedSnapshot()
    {
        if (Preferences.Get<DeepSeekCachedBalance?>(CacheKey) is not { } cached
            || cached.Snapshot is not { } restored
            || DateTimeOffset.Now - cached.UpdatedAt > CacheMaxAge) return;
        Snapshot = restored;
        LastUpdated = cached.UpdatedAt;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
