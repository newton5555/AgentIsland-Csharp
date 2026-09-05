using System.ComponentModel;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Providers;
using AgentIsland.Providers.Usage.Grok;

namespace AgentIsland.Backend.Usage;

/// The 24h cache envelope for the last good Grok billing snapshot.
public sealed class GrokCachedSnapshot
{
    public GrokBillingSnapshot? Snapshot { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// Grok's slice of the panel: the last fetched billing snapshot plus the
/// account identity from auth.json. Deliberately timer-free — it rides
/// UsageStore.Refresh()'s existing cadence (5–30 min polls, wake, unlock,
/// network recovery, manual refresh) via KickRefresh(), with a small attempt
/// floor so bursty kick sources never turn into extra polling of a provider
/// the user may not even be looking at.
public sealed class GrokUsageStore : INotifyPropertyChanged
{
    public static GrokUsageStore Shared { get; } = new();

    private const string CacheKey = "GrokUsageStore.lastSnapshot.v1";
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

    /// De-dupes kick bursts (poll + reset boundary + manual refresh all
    /// landing close together); the real cadence stays whatever UsageStore runs.
    private static readonly TimeSpan MinAttemptGap = TimeSpan.FromSeconds(120);

    private GrokBillingSnapshot? _snapshot;
    private string? _errorCaption;
    private DateTimeOffset? _lastUpdated;
    private string? _accountEmail;
    private string? _authModeBadge;
    private bool _loading;
    private DateTimeOffset? _lastAttempt;
    private long _refreshGeneration;
    private CancellationTokenSource? _refreshCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    private GrokUsageStore()
    {
        if (AppEnvironment.IsDemo)
        {
            // The recording rig pins its own island via AGENTISLAND_DEMO_PROVIDERS;
            // only dress the Grok row when Grok is one of the pinned slots.
            if (!ProviderVisibilityStore.Shared.GrokPanelShown) return;
            var now = DateTimeOffset.Now;
            _snapshot = new GrokBillingSnapshot(
                0.37,
                now.AddSeconds(3 * 86400 + 9 * 3600),
                123,
                4000,
                now.AddSeconds(26 * 86400),
                new[]
                {
                    new GrokProductUsage("grok-code", 0.29),
                    new GrokProductUsage("grok-web", 0.08),
                });
            _authModeBadge = "SUPERGROK";
            _lastUpdated = now;
            return;
        }

        LoadIdentity();
        RestoreCachedSnapshot();
    }

    public GrokBillingSnapshot? Snapshot
    {
        get => _snapshot;
        private set { _snapshot = value; Raise(nameof(Snapshot)); }
    }

    /// Non-null while the latest fetch failed. Values in Snapshot are the
    /// preserved last-good numbers in that case, same policy as UsageStore.
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

    public string? AccountEmail
    {
        get => _accountEmail;
        private set { _accountEmail = value; Raise(nameof(AccountEmail)); }
    }

    /// "SUPERGROK" for OIDC logins, else the raw auth_mode uppercased. This is
    /// the badge the settings row and the panel header show.
    public string? AuthModeBadge
    {
        get => _authModeBadge;
        private set { _authModeBadge = value; Raise(nameof(AuthModeBadge)); }
    }

    public bool Loading
    {
        get => _loading;
        private set { _loading = value; Raise(nameof(Loading)); }
    }

    /// Releases the in-process snapshot and cancels a provider request while
    /// retaining the persisted last-good value. A request that was already in
    /// flight is generation-checked before it can publish or persist anything.
    public void ClearMemory()
    {
        _refreshGeneration++;
        _refreshCts?.Cancel();
        _refreshCts = null;
        Snapshot = null;
        ErrorCaption = null;
        LastUpdated = null;
        AccountEmail = null;
        AuthModeBadge = null;
        _lastAttempt = null;
        Loading = false;
    }

    public void KickRefresh()
    {
        if (AppEnvironment.IsDemo) return;
        if (!ProviderVisibilityStore.Shared.GrokPanelShown) return;
        if (Loading) return;
        if (_lastAttempt is { } last && DateTimeOffset.Now - last < MinAttemptGap) return;

        RestoreCachedSnapshot();
        LoadIdentity();
        _lastAttempt = DateTimeOffset.Now;
        Loading = true;
        var generation = ++_refreshGeneration;
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await GrokUsageFetcher.Fetch(cts.Token);
                if (cts.IsCancellationRequested || generation != _refreshGeneration) return;
                await dispatcher.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested || generation != _refreshGeneration
                        || !ProviderVisibilityStore.Shared.GrokPanelShown) return;
                    Apply(outcome);
                });
            }
            catch (Exception error)
            {
                if (cts.IsCancellationRequested || generation != _refreshGeneration) return;
                try
                {
                    await dispatcher.BeginInvoke(() =>
                    {
                        if (generation != _refreshGeneration
                            || !ProviderVisibilityStore.Shared.GrokPanelShown) return;
                        Apply(new GrokUsageFetcher.Outcome.Failed(error.Message));
                    });
                }
                catch { }
            }
            finally
            {
                if (ReferenceEquals(_refreshCts, cts))
                {
                    _refreshCts = null;
                    if (generation == _refreshGeneration)
                    {
                        try { _ = dispatcher.BeginInvoke(() => Loading = false); } catch { }
                    }
                }
                cts.Dispose();
            }
        });
    }

    private void Apply(GrokUsageFetcher.Outcome outcome)
    {
        Loading = false;
        switch (outcome)
        {
            case GrokUsageFetcher.Outcome.Success success:
                Snapshot = success.Snapshot;
                ErrorCaption = null;
                LastUpdated = DateTimeOffset.Now;
                // The fetch may have rotated tokens, or the user may have
                // re-logged in since launch — keep the identity row honest.
                LoadIdentity();
                Persist(success.Snapshot);
                break;
            case GrokUsageFetcher.Outcome.ReauthRequired:
                ErrorCaption = L10n.Tr("sign in again — run grok login");
                break;
            case GrokUsageFetcher.Outcome.Failed failed:
                // Keep the last good numbers; the caption admits staleness.
                ErrorCaption = failed.Message;
                break;
            case GrokUsageFetcher.Outcome.NotInstalled:
                Snapshot = null;
                ErrorCaption = null;
                break;
        }
    }

    private void LoadIdentity()
    {
        if (GrokAuthFile.LoadEntry() is not { } entry)
        {
            AccountEmail = null;
            AuthModeBadge = null;
            return;
        }
        AccountEmail = entry.Email;
        AuthModeBadge = entry.IsSuperGrok ? "SUPERGROK" : entry.AuthMode?.ToUpperInvariant();
    }

    private void RestoreCachedSnapshot()
    {
        if (Preferences.Get<GrokCachedSnapshot?>(CacheKey) is not { } cached
            || cached.Snapshot is not { } restored
            || DateTimeOffset.Now - cached.UpdatedAt > CacheMaxAge) return;
        Snapshot = restored;
        LastUpdated = cached.UpdatedAt;
    }

    private static void Persist(GrokBillingSnapshot fresh) => Preferences.Set(
        CacheKey, new GrokCachedSnapshot { Snapshot = fresh, UpdatedAt = DateTimeOffset.Now });

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
