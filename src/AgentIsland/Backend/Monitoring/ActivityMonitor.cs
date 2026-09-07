using System.ComponentModel;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Backend.Alarms;
using AgentIsland.UI.Providers;
using AgentIsland.Core.Usage;
using AgentIsland.Windows.Monitoring;

namespace AgentIsland.Backend.Monitoring;

/// Aggregates scanned sessions into one visible state per provider, mixing in
/// usage-fetch attention states. Poll every 6 seconds plus file-event kicks
/// throttled to ~2/s with a guaranteed trailing scan: a streaming transcript
/// writes many times per second, but the LAST write of a turn (end_turn /
/// task_complete) must never wait for the fallback poll.
public sealed class ActivityMonitor : IActivityMonitor
{
    public sealed record ActiveThread(
        string SessionId,
        string Label,
        string Cwd,
        DateTimeOffset Modified,
        string? TranscriptPath,
        string? TurnKey,
        SessionLaunchTarget LaunchTarget);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Providers whose session status is actually scanned. Cursor is absent
    /// from the old batch-search path; all six ride the live scan. Cursor graduated once the real signal was
    /// found: each workspace's state.vscdb (and its -wal journal) is written
    /// continuously while a Cursor window is open — unlike the batch-written
    /// conversation-search.db that disqualified it earlier. Its turn
    /// detector is MtimeOnly, so it shows working/idle but can never raise a
    /// false "your turn".
    private TriggerTool[] _availableProviders =
    {
        TriggerTool.Claude,
        TriggerTool.Codex,
        TriggerTool.Grok,
        TriggerTool.Antigravity,
        TriggerTool.Cursor,
        TriggerTool.DeepSeek,
    };
    private TriggerTool[] _monitoredProviders =
    {
        TriggerTool.Claude,
        TriggerTool.Codex,
        TriggerTool.Grok,
        TriggerTool.Antigravity,
        TriggerTool.Cursor,
        TriggerTool.DeepSeek,
    };
    private readonly Dictionary<TriggerTool, AgentIsland.Core.Agents.ISessionSensor> _injectedSensors = new();
    private readonly AgentIsland.Backend.Settings.IProviderVisibilityStore _visibilityStore;
    private readonly AgentIsland.Backend.Alarms.IAgentReminderCenter? _reminderCenter;
    private readonly AgentIsland.Core.Threading.IUiDispatcher _uiDispatcher;

    /// <summary>
    /// When true, internal DispatcherTimer is suppressed because an external BackgroundService worker drives ticks.
    /// </summary>
    public bool DisableInternalTimer { get; set; }

    public ActivityMonitor(
        AgentIsland.Backend.Settings.IProviderVisibilityStore? visibilityStore = null,
        AgentIsland.Backend.Alarms.IAgentReminderCenter? reminderCenter = null,
        AgentIsland.Core.Threading.IUiDispatcher? uiDispatcher = null,
        IEnumerable<AgentIsland.Core.Agents.IAgentProvider>? providers = null)
    {
        _visibilityStore = visibilityStore ?? new AgentIsland.Backend.Settings.ProviderVisibilityStore();
        _reminderCenter = reminderCenter;
        _uiDispatcher = uiDispatcher ?? (System.Windows.Application.Current?.Dispatcher is not null
            ? new AgentIsland.UI.Threading.WpfUiDispatcher()
            : AgentIsland.Core.Threading.DirectUiDispatcher.Instance);
        if (providers is not null)
        {
            foreach (var p in providers)
            {
                if (p.SessionSensor is null) continue;
                var tool = TriggerToolExtensions.FromRawValue(p.Descriptor.Key.Value);
                if (tool is not null)
                {
                    _injectedSensors[tool.Value] = p.SessionSensor;
                }
            }
        }
    }

    /// Binds the legacy runtime enum to the stable Agent catalog at the
    /// composition root. Unknown future keys are ignored until their runtime
    /// adapter is migrated, rather than being forced into a fake enum value.
    public void Configure(IAgentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.Invoke(() => Configure(catalog));
            return;
        }
        var configured = catalog.Modules
            .Where(module => module.Descriptor.Supports(AgentCapabilities.Activity))
            .Select(module => TriggerToolExtensions.FromRawValue(module.Descriptor.Key.Value))
            .OfType<TriggerTool>()
            .Distinct()
            .ToArray();
        if (configured.Length > 0) _availableProviders = configured;
        if (_started) ApplyProviderMode();
    }

    // Per-provider maps rather than per-provider fields: with fields, every
    // provider past Codex silently read CODEX's state (the macOS 2.1.1 bug
    // this port fixes). Replaced wholesale on each scan, never mutated in
    // place, so a binding reading `States` can never see a half-built map.
    private Dictionary<TriggerTool, ActivityState> _states = new();
    private Dictionary<TriggerTool, ActivityState> _rawStates = new();
    private Dictionary<TriggerTool, ActiveThread> _threads = new();
    private Dictionary<TriggerTool, ActivityState> _demoStates = new();
    private Dictionary<string, DateTimeOffset> _lastWorking = new();
    private Dictionary<string, TriggerTool> _lastWorkingProviders = new(StringComparer.OrdinalIgnoreCase);

    /// Visible state for every monitored provider. Raised as one change so
    /// provider surfaces refresh together.
    public IReadOnlyDictionary<TriggerTool, ActivityState> States => _states;

    public IReadOnlyDictionary<TriggerTool, ActiveThread> Threads => _threads;

    public ActivityState StateFor(TriggerTool provider)
    {
        if (_demoStates.TryGetValue(provider, out var demo)) return demo;
        return _states.TryGetValue(provider, out var state) ? state : ActivityState.Idle;
    }

    /// Pre-overlay scan state. The turn-alarm confirm gate must read this:
    /// the usage overlay (rateLimited/authRequired outrank needsYou) would
    /// otherwise swallow alarms exactly when the quota is exhausted or the
    /// network is down — the moments a finished turn most needs surfacing.
    public ActivityState RawStateFor(TriggerTool provider) =>
        _rawStates.TryGetValue(provider, out var state) ? state : ActivityState.Idle;

    public ActiveThread? ThreadFor(TriggerTool provider) =>
        _threads.TryGetValue(provider, out var thread) ? thread : null;

    // The island and tray bind to these two by name; they stay as thin views
    // over the maps so those bindings keep working unchanged.
    public ActivityState Claude => StateFor(TriggerTool.Claude);
    public ActivityState Codex => StateFor(TriggerTool.Codex);
    public ActivityState DeepSeek => StateFor(TriggerTool.DeepSeek);
    public ActiveThread? ClaudeThread => ThreadFor(TriggerTool.Claude);
    public ActiveThread? CodexThread => ThreadFor(TriggerTool.Codex);

    /// Recording rig / settings preview: pins every provider's published
    /// state, or clears the pin when passed null.
    public void Demo(ActivityState? state)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            try { _uiDispatcher.BeginInvoke(() => Demo(state)); }
            catch { }
            return;
        }
        var next = new Dictionary<TriggerTool, ActivityState>();
        if (state is { } forced)
        {
            foreach (var tool in _monitoredProviders) next[tool] = forced;
        }
        _demoStates = next;
        RaiseAll();
    }

    private DispatcherTimer? _timer;
    private DispatcherTimer? _trailingKickTimer;
    private CancellationTokenSource? _trailingKickCts;
    private TranscriptEventStream? _eventStream;
    private Dispatcher? _dispatcher;
    private PropertyChangedEventHandler? _visibilityChanged;
    private readonly HashSet<TriggerTool> _activeProviders = new();
    private readonly HashSet<TriggerTool> _pendingCacheClears = new();
    private DateTimeOffset _lastEventKick = DateTimeOffset.MinValue;
    private bool _kickPending;
    private bool _scanInFlight;
    private bool _rescanQueued;
    private long _scanGeneration;
    private bool _started;
    private CancellationTokenSource? _scanCts;
    private TaskCompletionSource? _scanTcs;
    private TaskCompletionSource? _trailingTcs;

    public void Start()
    {
        if (_uiDispatcher.CheckAccess())
        {
            StartCore();
        }
        else
        {
            try
            {
                _uiDispatcher.Invoke(StartCore);
            }
            catch
            {
                try { _uiDispatcher.BeginInvoke(StartCore); }
                catch { }
            }
        }
    }

    private void StartCore()
    {
        if (_started) return;
        _started = true;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        _visibilityChanged = OnProviderVisibilityChanged;
        _visibilityStore.PropertyChanged += _visibilityChanged;
        ApplyProviderMode();
    }

    public void Stop()
    {
        if (_uiDispatcher.CheckAccess())
        {
            StopCore();
            return;
        }

        try
        {
            _uiDispatcher.Invoke(StopCore);
        }
        catch
        {
            try
            {
                _uiDispatcher.BeginInvoke(StopCore);
            }
            catch
            {
                // The dispatcher is unavailable or shutting down. Finalize the
                // background coordination state here so callers are not left
                // waiting on a UI commit that can no longer be delivered.
                StopBackgroundResources();
            }
        }
    }

    private void StopCore()
    {
        if (!_started) return;
        _started = false;
        if (_visibilityChanged is not null)
        {
            _visibilityStore.PropertyChanged -= _visibilityChanged;
            _visibilityChanged = null;
        }
        _scanGeneration++;
        _rescanQueued = false;
        if (_scanCts is { } inFlightCts)
        {
            _scanCts = null;
            try
            {
                inFlightCts.Cancel();
            }
            catch { }
        }
        StopMonitoringResources();
        foreach (var provider in _activeProviders) _pendingCacheClears.Add(provider);
        if (!_scanInFlight) FlushPendingCacheClears();
        _activeProviders.Clear();
        _monitoredProviders = Array.Empty<TriggerTool>();
        _lastWorking = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        _lastWorkingProviders = new Dictionary<string, TriggerTool>(StringComparer.OrdinalIgnoreCase);
        _states = new();
        _rawStates = new();
        _threads = new();
        _demoStates = new();
        _scanTcs?.TrySetResult();
        _trailingTcs?.TrySetResult();
        _scanTcs = null;
        _trailingTcs = null;
        RaiseAll();
    }

    private void StopBackgroundResources()
    {
        _started = false;
        if (_visibilityChanged is not null)
        {
            _visibilityStore.PropertyChanged -= _visibilityChanged;
            _visibilityChanged = null;
        }
        if (_scanCts is { } inFlightCts)
        {
            _scanCts = null;
            try
            {
                inFlightCts.Cancel();
            }
            catch { }
        }

        if (_trailingKickCts is { } kickCts)
        {
            _trailingKickCts = null;
            try
            {
                kickCts.Cancel();
            }
            catch { }
        }

        try
        {
            _eventStream?.Dispose();
        }
        catch { }
        _eventStream = null;

        _scanInFlight = false;
        _rescanQueued = false;
        _scanTcs?.TrySetResult();
        _trailingTcs?.TrySetResult();
        _scanTcs = null;
        _trailingTcs = null;
    }

    private void OnProviderVisibilityChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AgentIsland.Backend.Settings.ProviderVisibilityStore.Enabled)) return;
        if (!_uiDispatcher.CheckAccess())
        {
            try { _uiDispatcher.BeginInvoke(ApplyProviderMode); }
            catch { }
            return;
        }
        ApplyProviderMode();
    }

    private void ApplyProviderMode()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            try { _uiDispatcher.BeginInvoke(ApplyProviderMode); }
            catch { }
            return;
        }

        if (!_started) return;

        var selected = _visibilityStore.Enabled
            .Select(provider => provider.ToTriggerTool())
            .ToHashSet();
        var next = _availableProviders
            .Where(selected.Contains)
            .Distinct()
            .ToArray();
        var changed = !_monitoredProviders.SequenceEqual(next);
        var removed = _activeProviders.Except(next).ToArray();

        _activeProviders.Clear();
        foreach (var provider in next) _activeProviders.Add(provider);
        _monitoredProviders = next;

        if (changed)
        {
            _scanGeneration++;
            _rescanQueued = false;
            if (_scanCts is { } inFlightCts)
            {
                _scanCts = null;
                try
                {
                    inFlightCts.Cancel();
                }
                catch { }
            }
            // A last-working stamp is only meaningful for the provider that
            // produced it. Drop only disabled providers so an active sibling
            // keeps its stall/turn baseline across a slot change.
            ClearLastWorkingFor(removed);
            _states = _states.Where(pair => _activeProviders.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            _rawStates = _rawStates.Where(pair => _activeProviders.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            _threads = _threads.Where(pair => _activeProviders.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            _demoStates = _demoStates.Where(pair => _activeProviders.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var provider in removed)
            {
                _pendingCacheClears.Add(provider);
                _reminderCenter?.ClearProvider(provider);
            }
            if (!_scanInFlight) FlushPendingCacheClears();
            RaiseAll();
        }

        var wasRunning = _timer is not null;
        if (_monitoredProviders.Length == 0)
        {
            StopMonitoringResources();
            return;
        }

        if (changed)
        {
            _eventStream?.Dispose();
            _eventStream = null;
        }
        EnsureMonitoringResources();
        if (changed || !wasRunning) Tick();
    }

    private void EnsureMonitoringResources()
    {
        if (!DisableInternalTimer && _dispatcher is not null)
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(6),
                };
                _timer.Tick += (_, _) => Tick();
            }
            if (!_timer.IsEnabled) _timer.Start();
        }

        if (_eventStream is null)
        {
            var stream = new TranscriptEventStream(() =>
            {
                // File events arrive on watcher threads; hop to the UI thread.
                try { _uiDispatcher.BeginInvoke(EventKick); }
                catch { }
            });
            stream.Start(_activeProviders);
            _eventStream = stream;
        }
    }

    private void StopMonitoringResources()
    {
        _timer?.Stop();
        _timer = null;
        _trailingKickTimer?.Stop();
        _trailingKickTimer = null;
        if (_trailingKickCts is { } kickCts)
        {
            _trailingKickCts = null;
            try
            {
                kickCts.Cancel();
            }
            catch { }
        }
        _kickPending = false;
        _eventStream?.Dispose();
        _eventStream = null;
    }

    /// Minimum spacing between event-driven full scans. A marathon session
    /// (this repo's own development sessions hit 70MB+) writes its
    /// transcript several times a second, and at the old 0.5s spacing the
    /// monitor burned ~45% of a core purely on directory enumeration + a
    /// thousand stats per sweep. 2s keeps the your-turn alarm inside the
    /// "it just finished" moment (macOS lands at ~1.2s) at a quarter of
    /// the scan bill; the 6s timer still backstops missed events.
    private static readonly TimeSpan EventKickSpacing = TimeSpan.FromSeconds(2);

    private void EventKick()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(EventKick);
            return;
        }

        if (!_started || _monitoredProviders.Length == 0) return;
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastEventKick;
        if (elapsed >= EventKickSpacing)
        {
            _lastEventKick = now;
            Tick();
            return;
        }
        if (_kickPending) return;
        _kickPending = true;
        var delay = TimeSpan.FromSeconds(
            Math.Max(EventKickSpacing.TotalSeconds - elapsed.TotalSeconds, 0.05));
        if (_dispatcher is not null)
        {
            var trailing = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = delay,
            };
            _trailingKickTimer = trailing;
            trailing.Tick += (_, _) =>
            {
                trailing.Stop();
                if (ReferenceEquals(_trailingKickTimer, trailing)) _trailingKickTimer = null;
                _kickPending = false;
                _lastEventKick = DateTimeOffset.UtcNow;
                Tick();
            };
            trailing.Start();
        }
        else
        {
            var kickCts = new CancellationTokenSource();
            _trailingKickCts = kickCts;
            _ = Task.Delay(delay, kickCts.Token).ContinueWith(t =>
            {
                try { kickCts.Dispose(); } catch { }
                if (t.IsCompletedSuccessfully)
                {
                    try
                    {
                        _uiDispatcher.BeginInvoke(() =>
                        {
                            if (ReferenceEquals(_trailingKickCts, kickCts)) _trailingKickCts = null;
                            _kickPending = false;
                            _lastEventKick = DateTimeOffset.UtcNow;
                            Tick();
                        });
                    }
                    catch { }
                }
            }, TaskScheduler.Default);
        }
    }

    public void ScanNow()
    {
        if (_uiDispatcher.CheckAccess())
        {
            Tick();
        }
        else
        {
            try { _uiDispatcher.BeginInvoke(Tick); }
            catch { }
        }
    }

    public async Task ScanNowAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        if (_uiDispatcher.CheckAccess())
        {
            task = StartOrJoinScan();
        }
        else
        {
            var tcs = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _uiDispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        tcs.SetResult(StartOrJoinScan());
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
            }
            catch
            {
                return;
            }
            task = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (task != Task.CompletedTask)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Task StartOrJoinScan()
    {
        if (!_started || _monitoredProviders.Length == 0)
        {
            return Task.CompletedTask;
        }

        if (_scanInFlight)
        {
            _rescanQueued = true;
            _trailingTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _trailingTcs.Task;
        }

        Tick();
        return _scanTcs?.Task ?? Task.CompletedTask;
    }

    internal void Tick()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(Tick);
            return;
        }

        if (!_started || _monitoredProviders.Length == 0) return;
        // One scan at a time; a kick that lands mid-scan queues exactly one
        // follow-up so the trailing write of a turn is never dropped.
        if (_scanInFlight)
        {
            _rescanQueued = true;
            return;
        }
        _scanInFlight = true;
        _scanTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentScanTcs = _scanTcs;
        var scanGeneration = _scanGeneration;
        var providersSnapshot = _monitoredProviders.ToHashSet();
        var now = DateTimeOffset.UtcNow;
        // The comparer has to be restated: the copy constructor takes the
        // entries but NOT the source's comparer, and the scanner looks these
        // paths up again — Windows hands back whatever casing the directory
        // entry carries, so an ordinal snapshot would lose a session's
        // last-working stamp and downgrade a stall to idle.
        var lastWorkingSnapshot = new Dictionary<string, DateTimeOffset>(
            _lastWorking, StringComparer.OrdinalIgnoreCase);

        var cts = new CancellationTokenSource();
        _scanCts = cts;

        Task.Run(async () => await ScanSensorsAsync(now, lastWorkingSnapshot, providersSnapshot, cts.Token).ConfigureAwait(false))
            .ContinueWith(task =>
            {
                var sessions = task.IsCompletedSuccessfully ? task.Result : new List<ScannedSession>();
                Action commit = () =>
                {
                    try
                    {
                        if (!_started)
                        {
                            return;
                        }

                        if (scanGeneration == _scanGeneration
                            && providersSnapshot.SetEquals(_activeProviders))
                        {
                            try
                            {
                                Apply(sessions, now);
                            }
                            catch
                            {
                                // A failed provider read must not latch the monitor off.
                            }
                        }
                    }
                    finally
                    {
                        try { cts.Dispose(); } catch { }
                        if (ReferenceEquals(_scanCts, cts))
                        {
                            _scanCts = null;
                        }
                        _scanInFlight = false;
                        FlushPendingCacheClears();
                        currentScanTcs.TrySetResult();
                    }

                    if (!_started)
                    {
                        _rescanQueued = false;
                        _trailingTcs?.TrySetResult();
                        _trailingTcs = null;
                        _scanTcs = null;
                        return;
                    }

                    var isCurrent = scanGeneration == _scanGeneration
                        && providersSnapshot.SetEquals(_activeProviders);

                    var needFollowUp = _monitoredProviders.Length > 0
                        && (!isCurrent || _rescanQueued);

                    if (needFollowUp)
                    {
                        _rescanQueued = false;
                        if (_trailingTcs is not null)
                        {
                            _scanTcs = _trailingTcs;
                            _trailingTcs = null;
                        }
                        else
                        {
                            _scanTcs = null;
                        }
                        Tick();
                    }
                    else
                    {
                        _rescanQueued = false;
                        _trailingTcs?.TrySetResult();
                        _trailingTcs = null;
                        _scanTcs = null;
                    }
                };

                if (_uiDispatcher.CheckAccess())
                {
                    commit();
                }
                else
                {
                    try
                    {
                        _uiDispatcher.BeginInvoke(commit);
                    }
                    catch
                    {
                        AbandonScanAfterDispatcherFailure(cts, currentScanTcs);
                    }
                }
            }, TaskScheduler.Default);
    }

    private void AbandonScanAfterDispatcherFailure(
        CancellationTokenSource cts,
        TaskCompletionSource currentScanTcs)
    {
        if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
        _scanInFlight = false;
        _rescanQueued = false;
        currentScanTcs.TrySetResult();
        _trailingTcs?.TrySetResult();
        _trailingTcs = null;
        _scanTcs = null;
        try { cts.Dispose(); } catch { }
    }

    private async Task<List<ScannedSession>> ScanSensorsAsync(
        DateTimeOffset now,
        Dictionary<string, DateTimeOffset> lastWorkingSnapshot,
        HashSet<TriggerTool> providersSnapshot,
        CancellationToken ct = default)
    {
        if (_injectedSensors.Count > 0)
        {
            var results = new List<ScannedSession>();
            foreach (var tool in providersSnapshot)
            {
                if (ct.IsCancellationRequested) break;
                if (_injectedSensors.TryGetValue(tool, out var sensor))
                {
                    try
                    {
                        var sessions = await sensor.ScanSessionsAsync(now, lastWorkingSnapshot, ct).ConfigureAwait(false);
                        results.AddRange(sessions);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch { }
                }
            }
            results.Sort((a, b) => b.Modified.CompareTo(a.Modified));
            return results;
        }
        return SessionScanner.MonitoringScan(now, lastWorkingSnapshot, providersSnapshot);
    }

    internal void Apply(List<ScannedSession> sessions, DateTimeOffset now)
    {
        // Usage-level attention (rate-limited / auth-required red) only
        // applies to providers switched ON in Settings. Someone who only
        // runs Claude keeps Codex hidden - its missing login must not
        // pulse the island red forever.
        UpdateLastWorking(sessions, now);
        var nextStates = new Dictionary<TriggerTool, ActivityState>();
        var nextRaw = new Dictionary<TriggerTool, ActivityState>();
        var nextThreads = new Dictionary<TriggerTool, ActiveThread>();
        foreach (var tool in _monitoredProviders)
        {
            var result = BestSession(sessions, tool,
                thread => _reminderCenter?.HasAcknowledged(tool, thread) ?? false);
            nextRaw[tool] = result.State;
            if (result.Thread is { } thread) nextThreads[tool] = thread;
            nextStates[tool] = _visibilityStore.IsShown(tool.ToDisplayProvider())
                ? OverlayUsageAttention(result.State, UsageFor(tool))
                : result.State;
            _reminderCenter?.Handle(tool, NeedsYouThreads(sessions, tool));
        }
        _rawStates = nextRaw;
        _threads = nextThreads;
        _states = nextStates;
        RaiseAll();
    }

    private static AppUsage UsageFor(TriggerTool tool) =>
        UI.UsagePage.UsageFor(tool.ToDisplayProvider());

    private void UpdateLastWorking(List<ScannedSession> sessions, DateTimeOffset now)
    {
        foreach (var session in sessions)
        {
            if (session.Status == ActivityState.Working && session.TranscriptPath is { } path)
            {
                _lastWorking[path] = now;
                _lastWorkingProviders[path] = session.Tool;
            }
        }
        var livePaths = sessions
            .Where(s => s.TranscriptPath is not null)
            .Select(s => s.TranscriptPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _lastWorking = _lastWorking
            .Where(kv => livePaths.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        _lastWorkingProviders = _lastWorkingProviders
            .Where(kv => _lastWorking.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static ActivityState OverlayUsageAttention(ActivityState state, AppUsage usage)
    {
        if (UsageAttentionState(usage) is not { } attention) return state;
        return (int)attention > (int)state ? attention : state;
    }

    private static ActivityState? UsageAttentionState(AppUsage usage)
    {
        if (usage.FiveHour.UsedPercent >= 1 || usage.Weekly.UsedPercent >= 1)
            return ActivityState.RateLimited;
        var messages = new[] { usage.FiveHour.Error, usage.Weekly.Error }
            .Where(m => !string.IsNullOrEmpty(m))
            .Select(m => m!.ToLowerInvariant())
            .ToList();
        if (messages.Any(m => m.Contains("rate limited") || m.Contains("rate_limit")))
            return ActivityState.RateLimited;
        if (messages.Any(m =>
                ClaudeCredentials.IsAuthRecoverableError(m)
                || m.Contains("auth")
                || m.Contains("login")
                || m.Contains("no codex")))
        {
            return ActivityState.AuthRequired;
        }
        if (messages.Any(IsProviderOrNetworkError))
            return ActivityState.RateLimited;
        return null;
    }

    private static bool IsProviderOrNetworkError(string message) =>
        message.StartsWith("http ", StringComparison.Ordinal)
        || message.Contains("bad response")
        || message.Contains("parse error")
        || message.Contains("timed out")
        || message.Contains("timeout")
        || message.Contains("offline")
        || message.Contains("network")
        || message.Contains("internet")
        || message.Contains("connection")
        || message.Contains("cannot connect")
        || message.Contains("could not connect")
        || message.Contains("not connected")
        || message.Contains("dns")
        || message.Contains("ssl")
        || message.Contains("tls");

    /// A genuinely running turn must never be masked by an older or
    /// unacknowledged needsYou turn: Working outranks both unacknowledged
    /// and acknowledged needsYou. When no working session exists, unacknowledged
    /// needsYou outranks acknowledged needsYou, and both outrank idle so finished
    /// turns still surface. Stalled stays above working so anomalies surface.
    private static int SelectionPriority(ScannedSession session, Func<ActiveThread, bool> isAcknowledged) =>
        session.Status switch
        {
            ActivityState.Stalled => 4,
            ActivityState.Working => 3,
            ActivityState.NeedsYou => isAcknowledged(MakeThread(session)) ? 1 : 2,
            _ => 0,
        };

    private static (ActivityState State, ActiveThread? Thread) BestSession(
        List<ScannedSession> sessions,
        TriggerTool tool,
        Func<ActiveThread, bool> isAcknowledged)
    {
        var ranked = sessions
            .Where(s => s.Tool == tool)
            .Select(s => (Session: s, Priority: SelectionPriority(s, isAcknowledged)))
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.Session.Modified)
            .ToList();
        if (ranked.Count == 0) return (ActivityState.Idle, null);
        var top = ranked[0].Session;
        var thread = top.Status == ActivityState.Idle ? null : MakeThread(top);
        return (top.Status, thread);
    }

    /// Every needsYou session, newest first. The reminder center tracks each
    /// finished turn separately, so one thread's alarm can never cancel or
    /// mask another's.
    private static List<ActiveThread> NeedsYouThreads(List<ScannedSession> sessions, TriggerTool tool) =>
        sessions
            .Where(s => s.Tool == tool && s.Status == ActivityState.NeedsYou)
            .OrderByDescending(s => s.Modified)
            .Select(MakeThread)
            .ToList();

    private static ActiveThread MakeThread(ScannedSession session) => new(
        session.SessionId,
        session.Label,
        session.Cwd,
        session.Modified,
        session.TranscriptPath,
        session.TurnKey,
        session.LaunchTarget);

    /// Both the maps and the two named views have to be announced: the island
    /// and tray listen for "Claude"/"Codex", the provider panel reads the
    /// maps, and a provider whose notification is missing simply stops
    /// updating on screen.
    private void RaiseAll()
    {
        Raise(nameof(States));
        Raise(nameof(Threads));
        Raise(nameof(Claude));
        Raise(nameof(Codex));
        Raise(nameof(DeepSeek));
        Raise(nameof(ClaudeThread));
        Raise(nameof(CodexThread));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void ClearLastWorkingFor(IEnumerable<TriggerTool> providers)
    {
        var removed = providers.ToHashSet();
        if (removed.Count == 0 || _lastWorkingProviders.Count == 0) return;

        var removedPaths = _lastWorkingProviders
            .Where(pair => removed.Contains(pair.Value))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removedPaths.Count == 0) return;

        _lastWorking = _lastWorking
            .Where(pair => !removedPaths.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        _lastWorkingProviders = _lastWorkingProviders
            .Where(pair => !removedPaths.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private void FlushPendingCacheClears()
    {
        if (_pendingCacheClears.Count == 0) return;
        foreach (var provider in _pendingCacheClears) ClearActivityCache(provider);
        _pendingCacheClears.Clear();
        AgentIsland.Windows.Memory.MemoryReclaimer.ScheduleReclaim();
    }

    private static void ClearActivityCache(TriggerTool provider)
    {
        switch (provider)
        {
            case TriggerTool.Claude:
            case TriggerTool.Codex:
            case TriggerTool.Grok:
            case TriggerTool.Antigravity:
                // These providers share SessionScanner's bounded turn cache;
                // clear only the disabled provider's path entries.
                SessionScanner.ClearTurnCache(provider);
                break;
            case TriggerTool.Cursor:
                SessionScanner.ClearCursorCache();
                break;
            case TriggerTool.DeepSeek:
                DeepSeekActivityReader.ClearCache();
                break;
        }
    }
}
