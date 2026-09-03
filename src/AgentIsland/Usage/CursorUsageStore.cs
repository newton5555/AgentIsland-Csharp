using System.ComponentModel;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;

namespace AgentIsland.Usage;

/// Cursor's slice of the panel: the last fetched billing-period snapshot plus
/// identity from the editor's own store. Timer-free — it rides
/// UsageStore.Refresh()'s cadence via KickRefresh() behind the slot
/// selection, with an attempt floor so kick bursts never add polling.
public sealed class CursorUsageStore : INotifyPropertyChanged
{
    public static CursorUsageStore Shared { get; } = new();

    private const string CacheKey = "CursorUsageStore.lastSnapshot.v1";
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

    /// De-dupes kick bursts (poll + reset boundary + manual refresh landing
    /// together); the real cadence stays whatever UsageStore runs.
    private static readonly TimeSpan MinAttemptGap = TimeSpan.FromMinutes(2);

    /// One included-usage pool per billing cycle (~30 days). Cursor reports
    /// the cycle END, never its length, so this is what the window label
    /// renders from — including before the first successful fetch.
    public const double CycleSeconds = 30 * 24 * 60 * 60;

    private CursorUsageSnapshot? _snapshot;
    private string? _errorCaption;
    private DateTimeOffset? _lastUpdated;
    private string? _accountEmail;
    private string? _localPlan;
    private bool _loading;
    private DateTimeOffset? _lastAttempt;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Persisted shape of the last good fetch. Public because the JSON
    /// serializer behind Preferences has to reach it.
    public sealed class Cached
    {
        public CursorUsageSnapshot? Snapshot { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    private CursorUsageStore()
    {
        if (AppEnvironment.IsDemo)
        {
            if (DemoGuestFixtures)
            {
                var now = DateTimeOffset.Now;
                _snapshot = new CursorUsageSnapshot(0.21, now.AddDays(12), "pro");
                _lastUpdated = now;
            }
            return;
        }
        // Identity is deliberately NOT read here: on Windows it is a raw scan
        // of Cursor's multi-megabyte state db, and this constructor runs on
        // the UI thread. The first kick loads it off-thread instead.
        if (Preferences.Get<Cached?>(CacheKey) is { Snapshot: { } cached } stored
            && DateTimeOffset.Now - stored.UpdatedAt <= CacheMaxAge)
        {
            _snapshot = cached;
            _lastUpdated = stored.UpdatedAt;
        }
    }

    public CursorUsageSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            _snapshot = value;
            Raise(nameof(Snapshot));
            Raise(nameof(Window));
            Raise(nameof(PlanBadge));
        }
    }

    /// Non-null while the latest fetch failed. Values in Snapshot are the
    /// preserved last-good numbers in that case, same policy as UsageStore.
    public string? ErrorCaption
    {
        get => _errorCaption;
        private set { _errorCaption = value; Raise(nameof(ErrorCaption)); Raise(nameof(Window)); }
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

    public bool Loading
    {
        get => _loading;
        private set { _loading = value; Raise(nameof(Loading)); }
    }

    /// "FREE" / "PRO" chip text for the Settings row. Reads two cached
    /// fields, never the state db — the badge is bound by the UI.
    public string? PlanBadge => (_snapshot?.PlanName ?? _localPlan)?.ToUpperInvariant();

    /// The island renders Cursor through the same WindowUsage the two fully
    /// monitored providers use: one pool, a ~30-day cycle, and the store's
    /// caption riding along so a stale strip admits it.
    public WindowUsage Window =>
        new(_snapshot?.UsedPercent ?? 0, _snapshot?.PeriodEnd, _errorCaption, CycleSeconds);

    /// No-ops when Cursor isn't on the island, when a fetch is already in
    /// flight, or when the last attempt is younger than the floor.
    public void KickRefresh()
    {
        if (AppEnvironment.IsDemo) return;
        if (!ProviderVisibilityStore.Shared.CursorPanelShown) return;
        if (Loading) return;
        if (_lastAttempt is { } last && DateTimeOffset.Now - last < MinAttemptGap) return;
        _lastAttempt = DateTimeOffset.Now;
        Loading = true;
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            try
            {
                // The identity scan rides the fetch thread and the same pass,
                // so the account line and the numbers can never disagree.
                var email = CursorCredentials.CachedEmail();
                var plan = CursorCredentials.CachedPlan();
                var outcome = await CursorUsageFetcher.Fetch();
                await dispatcher.BeginInvoke(() => Apply(outcome, email, plan));
            }
            catch
            {
                // A faulted attempt must never leave Loading latched — that
                // would wedge every later kick behind the in-flight guard.
                await dispatcher.BeginInvoke(() => { Loading = false; });
            }
        });
    }

    private void Apply(CursorUsageFetcher.Outcome outcome, string? email, string? localPlan)
    {
        Loading = false;
        AccountEmail = email;
        _localPlan = localPlan;
        Raise(nameof(PlanBadge));
        switch (outcome)
        {
            case CursorUsageFetcher.Outcome.Success success:
                Snapshot = success.Snapshot;
                ErrorCaption = null;
                LastUpdated = DateTimeOffset.Now;
                Persist(success.Snapshot);
                break;
            case CursorUsageFetcher.Outcome.ReauthRequired:
                ErrorCaption = L10n.Tr("sign in again — open Cursor");
                break;
            case CursorUsageFetcher.Outcome.Failed failed:
                // Keep the last good numbers; the caption admits staleness.
                ErrorCaption = failed.Message;
                break;
            case CursorUsageFetcher.Outcome.NotInstalled:
                Snapshot = null;
                ErrorCaption = null;
                break;
        }
    }

    private static void Persist(CursorUsageSnapshot fresh) =>
        Preferences.Set(CacheKey, new Cached { Snapshot = fresh, UpdatedAt = DateTimeOffset.Now });

    /// The recording rig's guest fixtures. AppEnvironment carries no flag for
    /// them yet, so the macOS variable is read directly here.
    private static bool DemoGuestFixtures =>
        AppEnvironment.IsDemo && Environment.GetEnvironmentVariable("AGENTISLAND_DEMO_GUESTS") == "1";

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
