using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.Localization;

namespace AgentIsland.Usage;

/// Live provider usage published to the UI and the activity monitor. Direct
/// port of the macOS UsageStore: parallel fetch, error-merge that never
/// clobbers good values, 24h provider-stamped cache, re-auth polling, and a
/// refresh-on-reconnect network monitor.
public sealed class UsageStore : INotifyPropertyChanged
{
    public static UsageStore Shared { get; } = new();

    private const string CacheKey = "UsageStore.lastSuccessfulUsage.v1";
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

    private AppUsage _claude = AppUsage.Empty;
    private AppUsage _codex = AppUsage.Empty;
    private DateTimeOffset? _lastUpdated;
    private string? _refreshWarning;
    private bool _loading;
    private bool _claudeReauthInProgress;
    private bool _codexReauthInProgress;
    private string? _claudeReauthFailureCaption;
    private string? _codexAutoSwitched;

    /// Accounts tried since the current exhaustion episode began; cleared the
    /// moment a reading comes back under 100%, so each episode walks the pool
    /// at most once and a fully-exhausted pool goes quiet instead of thrashing
    /// auth.json forever.
    private readonly HashSet<string> _codexAutoSwitchTried = new(StringComparer.Ordinal);

    /// The parked labels as they stood when the episode began. Activate parks
    /// the OUTGOING login under a fresh `previous-<stamp>` name whenever it
    /// matches nothing on disk — and a Codex CLI that rewrites auth.json
    /// between polls (token refresh) makes that happen on every rotation. The
    /// pool would then grow exactly as fast as the tried set, so a candidate
    /// would always exist and the rotation would rewrite auth.json forever.
    /// Anything that appears after the snapshot is one of those copies and is
    /// excluded for the rest of the episode.
    private List<string>? _codexAutoSwitchPool;

    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _resetEdgeTimer;
    private DateTimeOffset _lastResetEdgeCheck = DateTimeOffset.Now;
    private DateTimeOffset _refreshStartedAt;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;
    private CancellationTokenSource? _codexReauthCts;
    private bool _networkMonitorArmed;
    private bool _powerMonitorArmed;
    private bool _lastNetworkAvailable = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    private UsageStore()
    {
        if (AppEnvironment.IsDemo) return;
        if (LoadCachedSnapshot() is { } snapshot)
        {
            _claude = snapshot.Claude;
            _codex = snapshot.Codex;
            _lastUpdated = snapshot.UpdatedAt;
        }
    }

    public AppUsage Claude { get => _claude; private set { _claude = value; Raise(nameof(Claude)); } }
    public AppUsage Codex { get => _codex; private set { _codex = value; Raise(nameof(Codex)); } }
    public DateTimeOffset? LastUpdated { get => _lastUpdated; private set { _lastUpdated = value; Raise(nameof(LastUpdated)); } }
    public string? RefreshWarning { get => _refreshWarning; private set { _refreshWarning = value; Raise(nameof(RefreshWarning)); } }
    public bool Loading { get => _loading; private set { _loading = value; Raise(nameof(Loading)); } }

    /// Set while a `claude auth login` flow is in progress; the UI hides the
    /// re-auth button during this window so users don't double-tap.
    public bool ClaudeReauthInProgress { get => _claudeReauthInProgress; private set { _claudeReauthInProgress = value; Raise(nameof(ClaudeReauthInProgress)); } }
    public bool CodexReauthInProgress { get => _codexReauthInProgress; private set { _codexReauthInProgress = value; Raise(nameof(CodexReauthInProgress)); } }

    /// Why the last browser sign-in round failed, or null. This is the whole
    /// recovery surface for a failed web login — the Settings row shows it and
    /// only then offers the paste-code fallback (progressive disclosure, macOS
    /// claudeReauthFailureCaption).
    public string? ClaudeReauthFailureCaption
    {
        get => _claudeReauthFailureCaption;
        private set { _claudeReauthFailureCaption = value; Raise(nameof(ClaudeReauthFailureCaption)); }
    }

    /// Clears the failure caption after the paste-code fallback succeeds —
    /// the row must drop back to its healthy state, not keep explaining a
    /// round that has since been recovered.
    public void ClearClaudeReauthFailure() => ClaudeReauthFailureCaption = null;

    /// Label the auto-switcher rotated to most recently, shown on the Codex
    /// card until the next manual action. Real state, not explanation.
    public string? CodexAutoSwitched { get => _codexAutoSwitched; set { _codexAutoSwitched = value; Raise(nameof(CodexAutoSwitched)); } }

    /// Refresh only when the last successful update is older than the poll
    /// interval — the panel-open freshness hook. Capped by the user's refresh
    /// interval so opening the island never polls the rate-limited endpoints
    /// any faster than the background schedule already would.
    public void RefreshIfStale()
    {
        if (AppEnvironment.IsDemo) return;
        var interval = TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds);
        if (LastUpdated is { } last && DateTimeOffset.Now - last < interval) return;
        Refresh();
    }

    public void Refresh()
    {
        // Self-heal instead of early-return when a refresh has been "in
        // flight" for minutes: a wedged one (faulted task, socket dead after
        // sleep) used to latch Loading forever, freezing usage — and with it
        // reset detection and auto-resume — until the app was relaunched.
        if (Loading && DateTimeOffset.Now - _refreshStartedAt < TimeSpan.FromMinutes(2)) return;

        // Guests re-probe every refresh cycle (macOS redetectGuests):
        // signing into agy/grok/Cursor while the app runs claims the slot
        // without a relaunch.
        Model.ProviderVisibilityStore.Shared.RedetectGuests();

        // Demo mode for screen recordings: skip the network entirely and
        // inject hand-tuned values. Reset times are recomputed each refresh
        // so the countdowns tick down naturally on camera.
        if (AppEnvironment.IsDemo)
        {
            var now = DateTimeOffset.Now;
            Claude = new AppUsage(
                new WindowUsage(
                    DemoDouble("AGENTISLAND_DEMO_CLAUDE_5H", 0.73),
                    now.AddMinutes(DemoMinutes("AGENTISLAND_DEMO_CLAUDE_RESET_MINUTES", 107)),
                    null),
                new WindowUsage(0.0 + DemoDouble("AGENTISLAND_DEMO_CLAUDE_WEEKLY", 0.81), now.AddSeconds(4 * 86400 + 11 * 3600), null),
                "max");
            // AGENTISLAND_DEMO_CODEX_SINGLE=1 shows Codex's primary-only shape:
            // one weekly window, secondary gone, plus banked reset cards.
            var codexSingle = Environment.GetEnvironmentVariable("AGENTISLAND_DEMO_CODEX_SINGLE") == "1";
            Codex = codexSingle
                ? new AppUsage(
                    new WindowUsage(
                        DemoDouble("AGENTISLAND_DEMO_CODEX_5H", 0.67),
                        now.AddSeconds(5 * 86400 + 4 * 3600),
                        null,
                        PeriodSeconds: 604800),
                    WindowUsage.Unknown,
                    "pro",
                    ResetCards: 2,
                    ResetCardDetails: new[]
                    {
                        new ResetCard("demo-1", "Full reset", now.AddDays(9)),
                        new ResetCard("demo-2", "Full reset", now.AddDays(23)),
                    })
                : new AppUsage(
                    new WindowUsage(
                        DemoDouble("AGENTISLAND_DEMO_CODEX_5H", 0.67),
                        now.AddMinutes(DemoMinutes("AGENTISLAND_DEMO_CODEX_RESET_MINUTES", 143)),
                        null),
                    new WindowUsage(DemoDouble("AGENTISLAND_DEMO_CODEX_WEEKLY", 0.76), now.AddSeconds(4 * 86400 + 18 * 3600), null),
                    "pro");
            LastUpdated = now;
            RefreshWarning = null;
            return;
        }

        Loading = true;
        _refreshStartedAt = DateTimeOffset.Now;
        // Grok, Gemini and Cursor ride this exact cadence (poll / wake /
        // unlock / network / manual) instead of owning timers; their stores
        // no-op when the provider is undetected or when kicked again inside
        // their own attempt floors.
        GrokUsageStore.Shared.KickRefresh();
        AntigravityUsageStore.Shared.KickRefresh();
        CursorUsageStore.Shared.KickRefresh();
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _refreshTask = Task.Run(async () =>
        {
            try
            {
                // Thread the token into the HTTP calls so a superseding refresh
                // (network-up mid-flight on a dead path) actually aborts the dead
                // request instead of letting it run to its own timeout.
                var codexTask = FetchWithRetry(UsageFetcher.FetchCodex, cts.Token);
                var claudeTask = FetchWithRetry(UsageFetcher.FetchClaude, cts.Token);
                var codexResult = await codexTask;
                var claudeResult = await claudeTask;

                await dispatcher.BeginInvoke(() =>
                {
                    // Cancellation = a superseding refresh took over (network
                    // came up, or the watchdog replaced a wedged one). Only
                    // the CURRENT refresh may touch Loading — a late loser
                    // clearing it would let a third refresh start mid-flight.
                    if (cts.IsCancellationRequested)
                    {
                        if (ReferenceEquals(_refreshCts, cts)) Loading = false;
                        return;
                    }

                    var codexFailed = IsErrorOnly(codexResult);
                    var claudeFailed = IsErrorOnly(claudeResult);
                    var mergedCodex = MergedUsage(Codex, codexResult);
                    var mergedClaude = MergedUsage(Claude, claudeResult);
                    Codex = mergedCodex;
                    Claude = mergedClaude;
                    SaveCachedSnapshot(mergedClaude, mergedCodex);
                    RefreshWarning = WarningFor(codexFailed, claudeFailed);
                    LastUpdated = DateTimeOffset.Now;
                    Loading = false;
                    MaybeAutoSwitchCodex(mergedCodex);
                });
            }
            catch
            {
                // A faulted refresh must never leave Loading latched.
                await dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_refreshCts, cts)) Loading = false;
                });
            }
        }, CancellationToken.None);
    }

    /// AUTO mode of `CodexAccountSwitcher` (the codex-auto borrow, driven by
    /// the real usage numbers instead of scraped terminal text). Runs on every
    /// fresh Codex reading: exhausted + enabled + a candidate exists → swap and
    /// immediately re-poll so the island shows the incoming account's numbers,
    /// not a stale 100%.
    ///
    /// The tried set is what stops an infinite credential-rewrite loop: the
    /// outgoing account is recorded before each swap, so once every parked
    /// login has been walked the rotation stops and auth.json is left alone
    /// until a reading drops back under 100%.
    private void MaybeAutoSwitchCodex(AppUsage usage)
    {
        if (!CodexAccountSwitcher.AutoSwitchEnabled) return;
        var primary = usage.FiveHour.Error is null ? usage.FiveHour : usage.Weekly;
        if (primary.Error is not null) return;
        if (primary.UsedPercent < 0.999)
        {
            _codexAutoSwitchTried.Clear();
            _codexAutoSwitchPool = null;
            return;
        }
        var pool = _codexAutoSwitchPool ??= CodexAccountSwitcher.Accounts()
            .Select(account => account.Label)
            .ToList();
        // Retire every label the episode did not start with, so the rotation
        // can only ever walk the snapshot and always runs out of candidates.
        foreach (var account in CodexAccountSwitcher.Accounts())
        {
            if (!pool.Contains(account.Label, StringComparer.Ordinal))
            {
                _codexAutoSwitchTried.Add(account.Label);
            }
        }
        if (CodexAccountSwitcher.ActiveLabel() is { } active) _codexAutoSwitchTried.Add(active);
        if (CodexAccountSwitcher.RotationCandidate(_codexAutoSwitchTried) is not { } next) return;
        if (!CodexAccountSwitcher.Activate(next)) return;
        _codexAutoSwitchTried.Add(next.Label);
        CodexAutoSwitched = next.Label;
        Refresh();
    }

    private static double DemoDouble(string key, double fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (raw is null || !double.TryParse(raw, out var value)) return fallback;
        return Math.Min(1, Math.Max(0, value));
    }

    private static int DemoMinutes(string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (raw is null || !int.TryParse(raw, out var value)) return fallback;
        return Math.Max(1, value);
    }

    /// True when both windows have errors and zero values — nothing useful
    /// to show, so we keep whatever we had before.
    private static bool IsErrorOnly(AppUsage usage) =>
        usage.FiveHour.Error is not null && usage.Weekly.Error is not null
        && usage.FiveHour.UsedPercent == 0 && usage.Weekly.UsedPercent == 0;

    /// Transient-network retry (macOS ae5bafc): an SSL hiccup or timeout
    /// gets two more tries with a short backoff before anything is shown.
    /// A superseding refresh cancels the wait, and a genuine outage still
    /// resolves within seconds — the merge path then keeps the last data.
    private static async Task<AppUsage> FetchWithRetry(
        Func<CancellationToken, Task<AppUsage>> fetch, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await fetch(token);
            if (!IsErrorOnly(result) || attempt >= 2 || token.IsCancellationRequested) return result;
            try { await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 3), token); }
            catch (TaskCanceledException) { return result; }
        }
    }

    /// Don't clobber existing good values when a fetch returns an all-error
    /// result: preserve the last useful percentages but carry the new error
    /// forward so the UI admits the values are stale. If the existing value
    /// is itself error-only (cold start, series of failures), let the new
    /// error through.
    private static AppUsage MergedUsage(AppUsage existing, AppUsage fetched)
    {
        if (!IsErrorOnly(fetched) || IsErrorOnly(existing)) return fetched;
        var error = fetched.FiveHour.Error ?? fetched.Weekly.Error;
        return new AppUsage(
            new WindowUsage(
                existing.FiveHour.UsedPercent, existing.FiveHour.ResetAt, error,
                existing.FiveHour.PeriodSeconds),
            new WindowUsage(
                existing.Weekly.UsedPercent, existing.Weekly.ResetAt, error,
                existing.Weekly.PeriodSeconds),
            existing.Plan,
            existing.ResetCards,
            existing.ResetCardDetails);
    }

    private static string? WarningFor(bool codexFailed, bool claudeFailed)
    {
        // A provider the user removed from the slots cannot nag from the
        // footer — "Claude stale" while only Grok + Cursor are selected reads
        // as a bug, because it was one (owner report, 2026-08-08).
        var visibility = Model.ProviderVisibilityStore.Shared;
        var claude = claudeFailed && visibility.ClaudeVisible;
        var codex = codexFailed && visibility.CodexVisible;
        return (claude, codex) switch
        {
            // Both down says nothing about WHY — two expired logins look
            // exactly like a dead uplink from here. "network drop" is the
            // transport-layer caption UsageFetcher hands back when it really
            // saw one; naming it from this summary would invent a cause.
            (true, true) => L10n.Tr("Usage refresh failed"),
            (true, false) => L10n.Tr("Claude stale"),
            (false, true) => L10n.Tr("Codex stale"),
            _ => null,
        };
    }

    // MARK: - Cache

    private static UsageCacheSnapshot? LoadCachedSnapshot()
    {
        var snapshot = Preferences.Get<UsageCacheSnapshot?>(CacheKey);
        if (snapshot is null) return null;
        return UsageCachePolicy.RestoredSnapshot(snapshot, DateTimeOffset.Now, CacheMaxAge);
    }

    private static void SaveCachedSnapshot(
        AppUsage claude,
        AppUsage codex,
        bool fetchedClaude = true,
        bool fetchedCodex = true)
    {
        var existing = LoadCachedSnapshot();
        var snapshot = UsageCachePolicy.SnapshotForSave(
            claude, codex, existing, DateTimeOffset.Now, fetchedClaude, fetchedCodex);
        if (snapshot is null) return;
        Preferences.Set(CacheKey, snapshot);
    }

    // MARK: - Preview injection (status guide)

    /// Replace current values with hand-tuned percentages so the alert
    /// engine's pulse + tint behavior can be exercised without waiting for a
    /// real provider crossing. The next scheduled poll overwrites them.
    public void InjectPreviewUsage(double claudeFiveHour, double codexFiveHour)
    {
        var now = DateTimeOffset.Now;
        var fiveHourReset = now.AddSeconds(2 * 3600 + 14 * 60);
        var weeklyReset = now.AddSeconds(4 * 86400 + 6 * 3600);
        Claude = new AppUsage(
            new WindowUsage(claudeFiveHour, fiveHourReset, null),
            new WindowUsage(0.45, weeklyReset, null),
            Claude.Plan ?? "max");
        Codex = new AppUsage(
            new WindowUsage(codexFiveHour, fiveHourReset, null),
            new WindowUsage(0.30, weeklyReset, null),
            Codex.Plan ?? "pro");
        LastUpdated = now;
        RefreshWarning = null;
    }

    // MARK: - Re-auth

    /// The in-app browser login — PKCE + a loopback callback caught by our own
    /// listener, writing the fresh, fully-scoped token pair straight to the
    /// credentials file. No terminal, no manual code paste.
    ///
    /// A failure ends here with its reason on `ClaudeReauthFailureCaption`.
    /// There is no terminal fallback: the old path silently spawned
    /// `claude auth login`, a retired command on the 2.x CLI, which read as a
    /// mystery "authentication failed" from nowhere (owner repro, 2026-08-08).
    /// The recovery the caption unlocks is `ClaudeCredentials.BeginPasteLogin`,
    /// which the Settings row offers only after a failed round.
    public void ReauthenticateClaude()
    {
        if (ClaudeReauthInProgress) return;
        ClaudeReauthFailureCaption = null;
        ClaudeReauthInProgress = true;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            // Nothing observes this task, so a throw anywhere below would
            // leave ClaudeReauthInProgress latched — and the in-progress guard
            // at the top then refuses every later attempt, killing the
            // Re-authenticate button until the app restarts.
            try
            {
                var outcome = await ClaudeWebLogin.Shared.Start();
                if (outcome is ClaudeWebLogin.Outcome.Failed failure)
                {
                    await dispatcher.BeginInvoke(() =>
                    {
                        ClaudeReauthInProgress = false;
                        ClaudeReauthFailureCaption = failure.Reason;
                    });
                    return;
                }
                await dispatcher.BeginInvoke(() => { ClaudeReauthFailureCaption = null; });
                await FinishClaudeReauth(dispatcher);
            }
            catch
            {
                try { await dispatcher.BeginInvoke(() => { ClaudeReauthInProgress = false; }); }
                catch { }
            }
        });
    }

    public bool ReauthenticateCodex()
    {
        if (CodexReauthInProgress) return true;
        var initialStamp = CodexCredentials.AuthModificationStamp();
        if (!CodexCredentials.SpawnReauth()) return false;
        CodexReauthInProgress = true;
        _codexReauthCts?.Cancel();
        var cts = new CancellationTokenSource();
        _codexReauthCts = cts;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            // Same latch hazard as the Claude flow: nobody observes this task,
            // and a stuck CodexReauthInProgress makes every later attempt
            // return early without doing anything.
            try
            {
                for (var i = 0; i < 40; i++)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(3), cts.Token); }
                    catch (TaskCanceledException) { return; }
                    var currentStamp = CodexCredentials.AuthModificationStamp();
                    if (currentStamp is null || currentStamp == initialStamp) continue;
                    await FinishCodexReauth(dispatcher);
                    return;
                }
                await FinishCodexReauth(dispatcher);
            }
            catch
            {
                try { await dispatcher.BeginInvoke(() => { CodexReauthInProgress = false; }); }
                catch { }
            }
        });
        return true;
    }

    private async Task FinishClaudeReauth(Dispatcher dispatcher)
    {
        var fetched = await UsageFetcher.FetchClaude();
        await dispatcher.BeginInvoke(() =>
        {
            var merged = MergedUsage(Claude, fetched);
            Claude = merged;
            SaveCachedSnapshot(merged, Codex, fetchedClaude: true, fetchedCodex: false);
            RefreshWarning = IsErrorOnly(fetched) ? L10n.Tr("Claude stale") : null;
            if (!IsErrorOnly(fetched)) LastUpdated = DateTimeOffset.Now;
            ClaudeReauthInProgress = false;
        });
    }

    private async Task FinishCodexReauth(Dispatcher dispatcher)
    {
        var fetched = await UsageFetcher.FetchCodex();
        await dispatcher.BeginInvoke(() =>
        {
            var merged = MergedUsage(Codex, fetched);
            Codex = merged;
            SaveCachedSnapshot(Claude, merged, fetchedClaude: false, fetchedCodex: true);
            RefreshWarning = IsErrorOnly(fetched) && Model.ProviderVisibilityStore.Shared.CodexVisible
                ? L10n.Tr("Codex stale")
                : null;
            if (!IsErrorOnly(fetched)) LastUpdated = DateTimeOffset.Now;
            CodexReauthInProgress = false;
        });
    }

    // MARK: - Auto refresh

    public void StartAutoRefresh()
    {
        StopAutoRefresh();
        Refresh();
        ArmTimer();
        ArmResetEdgeTimer();
        RefreshIntervalStore.Shared.PropertyChanged += OnIntervalChanged;
        StartNetworkMonitor();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        _powerMonitorArmed = true;
    }

    public void StopAutoRefresh()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        _resetEdgeTimer?.Stop();
        _resetEdgeTimer = null;
        RefreshIntervalStore.Shared.PropertyChanged -= OnIntervalChanged;
        if (_networkMonitorArmed)
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _networkMonitorArmed = false;
        }
        if (_powerMonitorArmed)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            _powerMonitorArmed = false;
        }
    }

    /// Refresh on unlock, the macOS screenIsUnlocked mirror: the timers keep
    /// running while locked on Windows, but modern-standby machines may still
    /// have dozed midway — a stale-gated refresh at unlock is cheap insurance
    /// that a reset which landed during the lock is caught the moment you're
    /// back, not up to a poll interval later.
    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason != Microsoft.Win32.SessionSwitchReason.SessionUnlock) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshIfStale);
    }

    /// Refresh right after waking from sleep — the poll timer's schedule
    /// slid while suspended, and any in-flight request died with the network
    /// stack. Without this, a machine that slept through a reset boundary
    /// shows the expired countdown until the next poll (up to 30 minutes).
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // The dead in-flight request would block Refresh's early-return
            // for up to 2 minutes — supersede it outright.
            _refreshCts?.Cancel();
            Loading = false;
            Refresh();
        });
    }

    /// The moment any known reset boundary passes, fetch fresh windows —
    /// don't sit on 100%-with-expired-countdown until the next scheduled
    /// poll. This is also what lets AfterReset auto-resume triggers fire
    /// within a minute of the reset instead of up to a poll interval late.
    private void ArmResetEdgeTimer()
    {
        _lastResetEdgeCheck = DateTimeOffset.Now;
        _resetEdgeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _resetEdgeTimer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.Now;
            var previous = _lastResetEdgeCheck;
            _lastResetEdgeCheck = now;
            var boundaries = new[]
            {
                Claude.FiveHour.ResetAt, Claude.Weekly.ResetAt,
                Codex.FiveHour.ResetAt, Codex.Weekly.ResetAt,
            };
            if (boundaries.Any(reset => reset is { } at && previous < at && at <= now))
            {
                Refresh();
            }
        };
        _resetEdgeTimer.Start();
    }

    private void OnIntervalChanged(object? sender, PropertyChangedEventArgs e) => ArmTimer();

    private void ArmTimer()
    {
        _pollTimer?.Stop();
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds),
        };
        _pollTimer.Tick += (_, _) => Refresh();
        _pollTimer.Start();
    }

    /// Trigger an immediate refresh when connectivity returns — closes the
    /// launch-at-login race where Wi-Fi is still associating when the first
    /// refresh fires. Without this, the panel sits at the cold-start state
    /// until the next scheduled poll (5–30 minutes away).
    private void StartNetworkMonitor()
    {
        _lastNetworkAvailable = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _networkMonitorArmed = true;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        var was = _lastNetworkAvailable;
        _lastNetworkAvailable = e.IsAvailable;
        if (!e.IsAvailable || was) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            // This lambda is async void on the dispatcher: anything it throws
            // past the first await lands on the dispatcher as an unhandled
            // exception, and the app's handler logs without marking it
            // handled — so a hiccup on a network transition would take the
            // whole app down. Reconnect recovery is best-effort by nature.
            try
            {
                // Cancel any in-flight refresh — it was started on the dead
                // path and will return an error. Wait for it to finalize so
                // its loading=false lands before the replacement starts.
                _refreshCts?.Cancel();
                if (_refreshTask is { } task)
                {
                    try { await task; } catch { }
                }
                Refresh();
            }
            catch
            {
                // The poll timer is still the backstop.
            }
        });
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
