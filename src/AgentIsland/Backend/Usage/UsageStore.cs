using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Providers;

namespace AgentIsland.Backend.Usage;

/// Live provider usage published to the UI and the activity monitor. Direct
/// port of the macOS UsageStore: parallel fetch, error-merge that never
/// clobbers good values, 24h provider-stamped cache, re-auth polling, and a
/// refresh-on-reconnect network monitor.
public sealed class UsageStore : IUsageStore
{
    private const string CacheKey = "UsageStore.lastSuccessfulUsage.v1";
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);
    private static readonly DisplayProvider[] CoreProviders =
    {
        DisplayProvider.Claude,
        DisplayProvider.Codex,
    };


    private sealed class RefreshSlot
    {
        public long Generation;
        public CancellationTokenSource? Cancellation;
        public Task<AppUsage>? Task;
        public TaskCompletionSource? CompletionTcs;
    }

    private AppUsage _claude = AppUsage.Empty;
    private AppUsage _codex = AppUsage.Empty;
    private DateTimeOffset? _lastUpdated;
    private string? _refreshWarning;
    private bool _loading;
    private bool _claudeReauthInProgress;
    private bool _codexReauthInProgress;
    private string? _claudeReauthFailureCaption;
    private string? _codexAutoSwitched;
    private readonly HashSet<DisplayProvider> _enabledProviders = new();
    private readonly Dictionary<DisplayProvider, DateTimeOffset> _providerUpdatedAt = new();
    private readonly Dictionary<DisplayProvider, RefreshSlot> _refreshSlots =
        CoreProviders.ToDictionary(provider => provider, _ => new RefreshSlot());

    private RefreshSlot GetSlot(DisplayProvider provider)
    {
        if (!_refreshSlots.TryGetValue(provider, out var slot))
        {
            slot = new RefreshSlot();
            _refreshSlots[provider] = slot;
        }
        return slot;
    }

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
    private CancellationTokenSource? _codexReauthCts;
    private PropertyChangedEventHandler? _visibilityChanged;
    private bool _autoRefreshStarted;
    private bool _networkMonitorArmed;
    private bool _powerMonitorArmed;
    private bool _lastNetworkAvailable = true;
    private readonly Dictionary<DisplayProvider, AgentIsland.Core.Agents.IUsageFetcher> _injectedFetchers = new();
    private readonly AgentIsland.Backend.Settings.IProviderVisibilityStore _visibilityStore;
    private readonly RefreshIntervalStore _refreshIntervalStore;
    private readonly IGrokUsageStore? _grokUsageStore;
    private readonly IAntigravityUsageStore? _antigravityUsageStore;
    private readonly ICursorUsageStore? _cursorUsageStore;
    private readonly IDeepSeekBalanceStore? _deepSeekBalanceStore;
    private readonly IClaudeWebLogin? _claudeWebLogin;
    private readonly AgentIsland.Core.Threading.IUiDispatcher _uiDispatcher;
    private readonly AgentIsland.Core.Storage.ISettingsStorage _settingsStorage;
    private readonly Dictionary<DisplayProvider, AppUsage> _usages = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public UsageStore() : this(
        (AgentIsland.Backend.Settings.IProviderVisibilityStore?)null,
        (RefreshIntervalStore?)null)
    {
    }

    public UsageStore(
        IEnumerable<AgentIsland.Core.Agents.IAgentProvider>? providers,
        AgentIsland.Backend.Settings.IProviderVisibilityStore? visibilityStore = null,
        AgentIsland.Core.Storage.ISettingsStorage? settingsStorage = null)
        : this(visibilityStore, null, providers, settingsStorage: settingsStorage)
    {
    }

    public UsageStore(
        AgentIsland.Backend.Settings.IProviderVisibilityStore? visibilityStore,
        RefreshIntervalStore? refreshIntervalStore = null,
        IEnumerable<AgentIsland.Core.Agents.IAgentProvider>? providers = null,
        IGrokUsageStore? grokUsageStore = null,
        IAntigravityUsageStore? antigravityUsageStore = null,
        ICursorUsageStore? cursorUsageStore = null,
        IDeepSeekBalanceStore? deepSeekBalanceStore = null,
        IClaudeWebLogin? claudeWebLogin = null,
        AgentIsland.Core.Threading.IUiDispatcher? uiDispatcher = null,
        AgentIsland.Core.Storage.ISettingsStorage? settingsStorage = null)
    {
        _visibilityStore = visibilityStore ?? new AgentIsland.Backend.Settings.ProviderVisibilityStore();
        _refreshIntervalStore = refreshIntervalStore ?? new RefreshIntervalStore();
        _grokUsageStore = grokUsageStore;
        _antigravityUsageStore = antigravityUsageStore;
        _cursorUsageStore = cursorUsageStore;
        _deepSeekBalanceStore = deepSeekBalanceStore;
        _claudeWebLogin = claudeWebLogin;
        _settingsStorage = settingsStorage ?? Preferences.Storage;
        _uiDispatcher = uiDispatcher ?? (System.Windows.Application.Current?.Dispatcher is not null
            ? new AgentIsland.UI.Threading.WpfUiDispatcher()
            : AgentIsland.Core.Threading.DirectUiDispatcher.Instance);

        if (providers is not null)
        {
            foreach (var p in providers)
            {
                if (p.UsageFetcher is null) continue;
                var dp = DisplayProviders.Parse(p.Descriptor.Key.Value);
                if (dp is not null)
                {
                    _injectedFetchers[dp.Value] = p.UsageFetcher;
                }
            }
        }

        if (AppEnvironment.IsDemo) return;
        if (LoadCachedSnapshot() is { } snapshot)
        {
            _claude = snapshot.Claude;
            _codex = snapshot.Codex;
            _usages[DisplayProvider.Claude] = _claude;
            _usages[DisplayProvider.Codex] = _codex;
            _lastUpdated = snapshot.UpdatedAt;
            if (snapshot.ClaudeUpdatedAt is { } claudeAt)
                _providerUpdatedAt[DisplayProvider.Claude] = claudeAt;
            if (snapshot.CodexUpdatedAt is { } codexAt)
                _providerUpdatedAt[DisplayProvider.Codex] = codexAt;
        }
    }

    public AppUsage Usage(DisplayProvider provider) =>
        _usages.TryGetValue(provider, out var u) ? u : (provider == DisplayProvider.Claude ? _claude : provider == DisplayProvider.Codex ? _codex : AppUsage.Empty);

    public AppUsage Claude { get => _claude; private set { _claude = value; _usages[DisplayProvider.Claude] = value; Raise(nameof(Claude)); Raise(nameof(Usage)); } }
    public AppUsage Codex { get => _codex; private set { _codex = value; _usages[DisplayProvider.Codex] = value; Raise(nameof(Codex)); Raise(nameof(Usage)); } }
    public DateTimeOffset? LastUpdated { get => _lastUpdated; private set { _lastUpdated = value; Raise(nameof(LastUpdated)); } }
    public string? RefreshWarning { get => _refreshWarning; private set { _refreshWarning = value; Raise(nameof(RefreshWarning)); } }
    public bool Loading { get => _loading; private set { _loading = value; Raise(nameof(Loading)); } }
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
    public void ClearClaudeReauthFailure()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(ClearClaudeReauthFailure);
            return;
        }
        ClaudeReauthFailureCaption = null;
    }

    /// Label the auto-switcher rotated to most recently, shown on the Codex
    /// card until the next manual action. Real state, not explanation.
    public string? CodexAutoSwitched { get => _codexAutoSwitched; set { _codexAutoSwitched = value; Raise(nameof(CodexAutoSwitched)); } }

    /// Refresh only when the last successful update is older than the poll
    /// interval — the panel-open freshness hook. Capped by the user's refresh
    /// interval so opening the island never polls the rate-limited endpoints
    /// any faster than the background schedule already would.
    public void RefreshIfStale()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(RefreshIfStale);
            return;
        }
        if (AppEnvironment.IsDemo) return;
        SyncProviderMode();
        var interval = TimeSpan.FromSeconds(_refreshIntervalStore.Seconds);
        var staleCore = CoreProviders.Any(provider =>
            _enabledProviders.Contains(provider)
            && (!_providerUpdatedAt.TryGetValue(provider, out var last)
                || DateTimeOffset.Now - last >= interval));
        var hasGuest = _enabledProviders.Any(provider => DisplayProviders.Guests.Contains(provider));
        if (!staleCore && !hasGuest) return;
        Refresh();
    }

    public void Refresh()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(Refresh);
            return;
        }

        SyncProviderMode();
        if (_enabledProviders.Count == 0)
        {
            Loading = false;
            return;
        }

        // Guests re-probe every refresh cycle (macOS redetectGuests):
        // signing into agy/grok/Cursor while the app runs claims the slot
        // without a relaunch.
        _visibilityStore.RedetectGuests();

        // Demo mode for screen recordings: skip the network entirely and
        // inject hand-tuned values. Reset times are recomputed each refresh
        // so the countdowns tick down naturally on camera.
        if (AppEnvironment.IsDemo)
        {
            var demoNow = DateTimeOffset.Now;
            Claude = new AppUsage(
                new WindowUsage(
                    DemoDouble("AGENTISLAND_DEMO_CLAUDE_5H", 0.73),
                    demoNow.AddMinutes(DemoMinutes("AGENTISLAND_DEMO_CLAUDE_RESET_MINUTES", 107)),
                    null),
                new WindowUsage(0.0 + DemoDouble("AGENTISLAND_DEMO_CLAUDE_WEEKLY", 0.81), demoNow.AddSeconds(4 * 86400 + 11 * 3600), null),
                "max");
            // AGENTISLAND_DEMO_CODEX_SINGLE=1 shows Codex's primary-only shape:
            // one weekly window, secondary gone, plus banked reset cards.
            var codexSingle = Environment.GetEnvironmentVariable("AGENTISLAND_DEMO_CODEX_SINGLE") == "1";
            Codex = codexSingle
                ? new AppUsage(
                    new WindowUsage(
                        DemoDouble("AGENTISLAND_DEMO_CODEX_5H", 0.67),
                        demoNow.AddSeconds(5 * 86400 + 4 * 3600),
                        null,
                        PeriodSeconds: 604800),
                    WindowUsage.Unknown,
                    "pro",
                    ResetCards: 2,
                    ResetCardDetails: new[]
                    {
                        new ResetCard("demo-1", "Full reset", demoNow.AddDays(9)),
                        new ResetCard("demo-2", "Full reset", demoNow.AddDays(23)),
                    })
                : new AppUsage(
                    new WindowUsage(
                        DemoDouble("AGENTISLAND_DEMO_CODEX_5H", 0.67),
                        demoNow.AddMinutes(DemoMinutes("AGENTISLAND_DEMO_CODEX_RESET_MINUTES", 143)),
                        null),
                    new WindowUsage(DemoDouble("AGENTISLAND_DEMO_CODEX_WEEKLY", 0.76), demoNow.AddSeconds(4 * 86400 + 18 * 3600), null),
                    "pro");
            LastUpdated = demoNow;
            RefreshWarning = null;
            return;
        }

        // Each core provider owns its request and generation. A slow or
        // disabled Codex request must not hold Claude's value hostage.
        // Grok, Gemini and Cursor ride this exact cadence (poll / wake /
        // unlock / network / manual) instead of owning timers; their stores
        // no-op when the provider is undetected or when kicked again inside
        // their own attempt floors.
        if (_enabledProviders.Contains(DisplayProvider.Grok)) _grokUsageStore?.KickRefresh();
        if (_enabledProviders.Contains(DisplayProvider.Antigravity)) _antigravityUsageStore?.KickRefresh();
        if (_enabledProviders.Contains(DisplayProvider.Cursor)) _cursorUsageStore?.KickRefresh();
        if (_enabledProviders.Contains(DisplayProvider.DeepSeek)) _deepSeekBalanceStore?.KickRefresh();
        foreach (var provider in _enabledProviders)
        {
            if (CoreProviders.Contains(provider))
            {
                StartCoreRefresh(provider);
            }
            else if (_injectedFetchers.TryGetValue(provider, out var fetcher))
            {
                StartGenericRefresh(provider, fetcher);
            }
        }
        UpdateLoading();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Task[] inFlight;
        if (_uiDispatcher.CheckAccess())
        {
            Refresh();
            inFlight = _refreshSlots.Values
                .Select(s => s.CompletionTcs?.Task)
                .Where(t => t != null)
                .Cast<Task>()
                .ToArray();
        }
        else
        {
            var tcs = new TaskCompletionSource<Task[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _uiDispatcher.BeginInvoke(() =>
            {
                try
                {
                    Refresh();
                    var tasks = _refreshSlots.Values
                        .Select(s => s.CompletionTcs?.Task)
                        .Where(t => t != null)
                        .Cast<Task>()
                        .ToArray();
                    tcs.SetResult(tasks);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            try
            {
                inFlight = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
        }

        if (inFlight.Length > 0)
        {
            try
            {
                await Task.WhenAll(inFlight).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private void StartGenericRefresh(
        DisplayProvider provider,
        AgentIsland.Core.Agents.IUsageFetcher fetcher)
    {
        var slot = GetSlot(provider);
        if (slot.Task is not null) return;

        var generation = slot.Generation;
        var cts = new CancellationTokenSource();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(
            async () => await fetcher.FetchUsageAsync(cts.Token).ConfigureAwait(false),
            CancellationToken.None);
        slot.Cancellation = cts;
        slot.Task = task;
        slot.CompletionTcs = tcs;
        _ = ObserveGenericRefresh(provider, generation, task, cts, tcs);
    }

    private async Task ObserveGenericRefresh(
        DisplayProvider provider,
        long generation,
        Task<AppUsage> task,
        CancellationTokenSource cts,
        TaskCompletionSource tcs)
    {
        AppUsage? result = null;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            _uiDispatcher.BeginInvoke(() =>
            {
                try
                {
                    var slot = GetSlot(provider);
                    if (!ReferenceEquals(slot.Task, task) || slot.Generation != generation)
                    {
                        return;
                    }

                    slot.Task = null;
                    slot.Cancellation = null;
                    slot.CompletionTcs = null;
                    if (!_enabledProviders.Contains(provider) || result is null)
                    {
                        UpdateLoading();
                        return;
                    }

                    _usages[provider] = result;
                    _providerUpdatedAt[provider] = DateTimeOffset.Now;
                    Raise(nameof(Usage));
                    UpdateLoading();
                }
                finally
                {
                    cts.Dispose();
                    tcs.TrySetResult();
                }
            });
        }
        catch
        {
            cts.Dispose();
            tcs.TrySetResult();
        }
    }

    private bool SyncProviderMode()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(() => SyncProviderMode());
            return false;
        }

        var visibility = _visibilityStore;
        var next = visibility.Enabled.ToHashSet();
        if (_enabledProviders.SetEquals(next)) return false;

        var removed = _enabledProviders.Except(next).ToArray();
        var added = next.Except(_enabledProviders).ToArray();
        _enabledProviders.Clear();
        foreach (var provider in next) _enabledProviders.Add(provider);

        foreach (var provider in removed)
        {
            if (_refreshSlots.TryGetValue(provider, out var slot))
            {
                slot.Generation++;
                slot.Cancellation?.Cancel();
                slot.Cancellation = null;
                slot.Task = null;
                slot.CompletionTcs?.TrySetResult();
                slot.CompletionTcs = null;
            }

            if (provider == DisplayProvider.Claude) Claude = AppUsage.Empty;
            if (provider == DisplayProvider.Codex)
            {
                Codex = AppUsage.Empty;
                _codexAutoSwitchTried.Clear();
                _codexAutoSwitchPool = null;
                CodexAutoSwitched = null;
            }
            _providerUpdatedAt.Remove(provider);
            _usages.Remove(provider);
            ClearGuestMemory(provider);
        }

        foreach (var provider in added)
        {
            RestoreCoreSnapshot(provider);
        }

        RefreshWarning = WarningFor(
            IsErrorOnly(Claude),
            IsErrorOnly(Codex));
        UpdateLastUpdated();

        if (_enabledProviders.Count == 0)
        {
            _pollTimer?.Stop();
            _pollTimer = null;
            _resetEdgeTimer?.Stop();
            _resetEdgeTimer = null;
            Loading = false;
        }
        else if (_autoRefreshStarted)
        {
            ArmTimer();
            ArmResetEdgeTimer();
        }
        return true;
    }

    private void RestoreCoreSnapshot(DisplayProvider provider)
    {
        if (!CoreProviders.Contains(provider)) return;
        var snapshot = LoadCachedSnapshot();
        if (snapshot is null) return;
        if (provider == DisplayProvider.Claude)
        {
            Claude = snapshot.Claude;
            if (snapshot.ClaudeUpdatedAt is { } at) _providerUpdatedAt[provider] = at;
        }
        else if (provider == DisplayProvider.Codex)
        {
            Codex = snapshot.Codex;
            if (snapshot.CodexUpdatedAt is { } at) _providerUpdatedAt[provider] = at;
        }
    }

    private void StartCoreRefresh(DisplayProvider provider)
    {
        var slot = GetSlot(provider);
        // Keep a completed task attached until its dispatcher completion has
        // released it. This prevents a timer tick from attaching a duplicate
        // observer in the small completion window.
        if (slot.Task is not null) return;

        var generation = slot.Generation;
        var cts = new CancellationTokenSource();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(
            () => FetchCore(provider, cts.Token),
            CancellationToken.None);
        slot.Cancellation = cts;
        slot.Task = task;
        slot.CompletionTcs = tcs;
        _ = ObserveCoreRefresh(provider, generation, task, cts, tcs);
    }

    private Task<AppUsage> FetchCore(
        DisplayProvider provider,
        CancellationToken cancellationToken)
    {
        if (_injectedFetchers.TryGetValue(provider, out var fetcher))
        {
            return FetchWithRetry(ct => fetcher.FetchUsageAsync(ct).AsTask(), cancellationToken);
        }
        return provider switch
        {
            DisplayProvider.Claude => FetchWithRetry(UsageFetcher.FetchClaude, cancellationToken),
            DisplayProvider.Codex => FetchWithRetry(UsageFetcher.FetchCodex, cancellationToken),
            _ => Task.FromResult(AppUsage.Empty),
        };
    }

    private async Task ObserveCoreRefresh(
        DisplayProvider provider,
        long generation,
        Task<AppUsage> task,
        CancellationTokenSource cts,
        TaskCompletionSource tcs)
    {
        AppUsage result;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            result = AppUsage.ErrorPair(error.Message);
        }

        try
        {
            _uiDispatcher.BeginInvoke(() =>
            {
                try
                {
                    var slot = GetSlot(provider);
                    if (!ReferenceEquals(slot.Task, task) || slot.Generation != generation)
                    {
                        return;
                    }

                    slot.Task = null;
                    slot.Cancellation = null;
                    slot.CompletionTcs = null;
                    if (!_enabledProviders.Contains(provider))
                    {
                        UpdateLoading();
                        return;
                    }

                    var failed = IsErrorOnly(result);
                    if (provider == DisplayProvider.Claude)
                    {
                        var merged = MergedUsage(Claude, result);
                        Claude = merged;
                        SaveCachedSnapshot(merged, Codex, fetchedClaude: true, fetchedCodex: false);
                    }
                    else
                    {
                        var merged = MergedUsage(Codex, result);
                        Codex = merged;
                        SaveCachedSnapshot(Claude, merged, fetchedClaude: false, fetchedCodex: true);
                        if (!failed) MaybeAutoSwitchCodex(merged);
                    }

                    if (!failed) _providerUpdatedAt[provider] = DateTimeOffset.Now;
                    RefreshWarning = WarningFor(
                        IsErrorOnly(Claude),
                        IsErrorOnly(Codex));
                    UpdateLastUpdated();
                    UpdateLoading();
                }
                finally
                {
                    cts.Dispose();
                    tcs.TrySetResult();
                }
            });
        }
        catch
        {
            cts.Dispose();
            tcs.TrySetResult();
        }
    }

    private void UpdateLastUpdated()
    {
        DateTimeOffset? latest = null;
        foreach (var provider in CoreProviders)
        {
            if (!_enabledProviders.Contains(provider)) continue;
            if (!_providerUpdatedAt.TryGetValue(provider, out var at)) continue;
            if (latest is null || at > latest) latest = at;
        }
        LastUpdated = latest;
    }

    private void UpdateLoading()
    {
        Loading = _enabledProviders.Any(provider =>
            _refreshSlots.TryGetValue(provider, out var slot)
            && slot.Task is not null);
    }

    private void CancelAllRefreshes()
    {
        foreach (var slot in _refreshSlots.Values)
        {
            slot.Generation++;
            slot.Cancellation?.Cancel();
            slot.Cancellation = null;
            slot.Task = null;
            slot.CompletionTcs?.TrySetResult();
            slot.CompletionTcs = null;
        }
        UpdateLoading();
    }

    private void ClearGuestMemory(DisplayProvider provider)
    {
        switch (provider)
        {
            case DisplayProvider.Grok:
                _grokUsageStore?.ClearMemory();
                break;
            case DisplayProvider.Antigravity:
                _antigravityUsageStore?.ClearMemory();
                break;
            case DisplayProvider.Cursor:
                _cursorUsageStore?.ClearMemory();
                break;
            case DisplayProvider.DeepSeek:
                _deepSeekBalanceStore?.ClearMemory();
                break;
        }
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

    private string? WarningFor(bool codexFailed, bool claudeFailed)
    {
        // A provider the user removed from the slots cannot nag from the
        // footer — "Claude stale" while only Grok + Cursor are selected reads
        // as a bug, because it was one (owner report, 2026-08-08).
        var visibility = _visibilityStore;
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

    private UsageCacheSnapshot? LoadCachedSnapshot()
    {
        var snapshot = _settingsStorage.Get<UsageCacheSnapshot?>(CacheKey);
        if (snapshot is null) return null;
        return UsageCachePolicy.RestoredSnapshot(snapshot, DateTimeOffset.Now, CacheMaxAge);
    }

    private void SaveCachedSnapshot(
        AppUsage claude,
        AppUsage codex,
        bool fetchedClaude = true,
        bool fetchedCodex = true)
    {
        var existing = LoadCachedSnapshot();
        var snapshot = UsageCachePolicy.SnapshotForSave(
            claude, codex, existing, DateTimeOffset.Now, fetchedClaude, fetchedCodex);
        if (snapshot is null) return;
        _settingsStorage.Set(CacheKey, snapshot);
    }

    // MARK: - Preview injection (status guide)

    /// Replace current values with hand-tuned percentages so the alert
    /// engine's pulse + tint behavior can be exercised without waiting for a
    /// real provider crossing. The next scheduled poll overwrites them.
    public void InjectPreviewUsage(double claudeFiveHour, double codexFiveHour)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(() => InjectPreviewUsage(claudeFiveHour, codexFiveHour));
            return;
        }
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
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(ReauthenticateClaude);
            return;
        }
        if (ClaudeReauthInProgress) return;
        ClaudeReauthFailureCaption = null;
        ClaudeReauthInProgress = true;
        _ = Task.Run(async () =>
        {
            // Nothing observes this task, so a throw anywhere below would
            // leave ClaudeReauthInProgress latched — and the in-progress guard
            // at the top then refuses every later attempt, killing the
            // Re-authenticate button until the app restarts.
            try
            {
                var outcome = await (_claudeWebLogin ?? new ClaudeWebLogin()).Start().ConfigureAwait(false);
                if (outcome is ClaudeWebLogin.Outcome.Failed failure)
                {
                    _uiDispatcher.BeginInvoke(() =>
                    {
                        ClaudeReauthInProgress = false;
                        ClaudeReauthFailureCaption = failure.Reason;
                    });
                    return;
                }
                _uiDispatcher.BeginInvoke(() => { ClaudeReauthFailureCaption = null; });
                await FinishClaudeReauth().ConfigureAwait(false);
            }
            catch
            {
                try { _uiDispatcher.BeginInvoke(() => { ClaudeReauthInProgress = false; }); }
                catch { }
            }
        });
    }

    public bool ReauthenticateCodex()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(() => ReauthenticateCodex());
            return true;
        }
        if (CodexReauthInProgress) return true;
        var initialStamp = CodexCredentials.AuthModificationStamp();
        if (!CodexCredentials.SpawnReauth()) return false;
        CodexReauthInProgress = true;
        _codexReauthCts?.Cancel();
        var cts = new CancellationTokenSource();
        _codexReauthCts = cts;
        _ = Task.Run(async () =>
        {
            // Same latch hazard as the Claude flow: nobody observes this task,
            // and a stuck CodexReauthInProgress makes every later attempt
            // return early without doing anything.
            try
            {
                for (var i = 0; i < 40; i++)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false); }
                    catch (TaskCanceledException) { return; }
                    var currentStamp = CodexCredentials.AuthModificationStamp();
                    if (currentStamp is null || currentStamp == initialStamp) continue;
                    await FinishCodexReauth().ConfigureAwait(false);
                    return;
                }
                await FinishCodexReauth().ConfigureAwait(false);
            }
            catch
            {
                try { _uiDispatcher.BeginInvoke(() => { CodexReauthInProgress = false; }); }
                catch { }
            }
        });
        return true;
    }

    private async Task FinishClaudeReauth()
    {
        var fetched = await UsageFetcher.FetchClaude().ConfigureAwait(false);
        _uiDispatcher.BeginInvoke(() =>
        {
            var merged = MergedUsage(Claude, fetched);
            Claude = merged;
            SaveCachedSnapshot(merged, Codex, fetchedClaude: true, fetchedCodex: false);
            RefreshWarning = IsErrorOnly(fetched) ? L10n.Tr("Claude stale") : null;
            if (!IsErrorOnly(fetched)) LastUpdated = DateTimeOffset.Now;
            ClaudeReauthInProgress = false;
        });
    }

    private async Task FinishCodexReauth()
    {
        var fetched = await UsageFetcher.FetchCodex().ConfigureAwait(false);
        _uiDispatcher.BeginInvoke(() =>
        {
            var merged = MergedUsage(Codex, fetched);
            Codex = merged;
            SaveCachedSnapshot(Claude, merged, fetchedClaude: false, fetchedCodex: true);
            RefreshWarning = IsErrorOnly(fetched) && _visibilityStore.CodexVisible
                ? L10n.Tr("Codex stale")
                : null;
            if (!IsErrorOnly(fetched)) LastUpdated = DateTimeOffset.Now;
            CodexReauthInProgress = false;
        });
    }

    // MARK: - Auto refresh

    public void StartAutoRefresh()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(StartAutoRefresh);
            return;
        }

        StopAutoRefresh();
        Refresh();
        _autoRefreshStarted = true;
        _visibilityChanged = (_, args) =>
        {
            if (args.PropertyName is not (
                nameof(AgentIsland.Backend.Settings.ProviderVisibilityStore.Enabled)
                or nameof(AgentIsland.Backend.Settings.ProviderVisibilityStore.SlotProviders))) return;
            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.BeginInvoke(() =>
                {
                    if (SyncProviderMode() && _enabledProviders.Count > 0) Refresh();
                });
                return;
            }
            if (SyncProviderMode() && _enabledProviders.Count > 0) Refresh();
        };
        _visibilityStore.PropertyChanged += _visibilityChanged;
        if (_enabledProviders.Count > 0)
        {
            ArmTimer();
            ArmResetEdgeTimer();
        }
        _refreshIntervalStore.PropertyChanged += OnIntervalChanged;
        StartNetworkMonitor();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        _powerMonitorArmed = true;
    }

    public void StopAutoRefresh()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(StopAutoRefresh);
            return;
        }

        _autoRefreshStarted = false;
        if (_visibilityChanged is not null)
        {
            _visibilityStore.PropertyChanged -= _visibilityChanged;
            _visibilityChanged = null;
        }
        CancelAllRefreshes();
        _pollTimer?.Stop();
        _pollTimer = null;
        _resetEdgeTimer?.Stop();
        _resetEdgeTimer = null;
        _refreshIntervalStore.PropertyChanged -= OnIntervalChanged;
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
        _uiDispatcher.BeginInvoke(RefreshIfStale);
    }

    /// Refresh right after waking from sleep — the poll timer's schedule
    /// slid while suspended, and any in-flight request died with the network
    /// stack. Without this, a machine that slept through a reset boundary
    /// shows the expired countdown until the next poll (up to 30 minutes).
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        _uiDispatcher.BeginInvoke(() =>
        {
            // The dead in-flight request would block Refresh's early-return
            // for up to 2 minutes — supersede it outright.
            CancelAllRefreshes();
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
        _resetEdgeTimer?.Stop();
        if (DisableInternalTimer) return;
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

    public bool DisableInternalTimer { get; set; }

    private void OnIntervalChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(ArmTimer);
            return;
        }
        ArmTimer();
    }

    private void ArmTimer()
    {
        _pollTimer?.Stop();
        if (DisableInternalTimer) return;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(_refreshIntervalStore.Seconds),
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
        _uiDispatcher.BeginInvoke(() =>
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
                CancelAllRefreshes();
                Refresh();
            }
            catch
            {
                // The poll timer is still the backstop.
            }
        });
    }

    private void Raise(string name)
    {
        if (_uiDispatcher.CheckAccess())
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        else
        {
            _uiDispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
        }
    }
}
