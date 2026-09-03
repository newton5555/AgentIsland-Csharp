using System.ComponentModel;
using System.Windows.Threading;
using AgentIsland.Alarm;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.Core;

/// Aggregates scanned sessions into one visible state per provider, mixing in
/// usage-fetch attention states. Poll every 6 seconds plus file-event kicks
/// throttled to ~2/s with a guaranteed trailing scan: a streaming transcript
/// writes many times per second, but the LAST write of a turn (end_turn /
/// task_complete) must never wait for the fallback poll.
public sealed class ActivityMonitor : INotifyPropertyChanged
{
    public static ActivityMonitor Shared { get; } = new();
    private ActivityMonitor() { }

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
    /// All five ride the scan. Cursor graduated once the real signal was
    /// found: each workspace's state.vscdb (and its -wal journal) is written
    /// continuously while a Cursor window is open — unlike the batch-written
    /// conversation-search.db that disqualified it earlier. Its turn
    /// detector is MtimeOnly, so it shows working/idle but can never raise a
    /// false "your turn".
    private static readonly TriggerTool[] MonitoredProviders =
    {
        TriggerTool.Claude,
        TriggerTool.Codex,
        TriggerTool.Grok,
        TriggerTool.Antigravity,
        TriggerTool.Cursor,
    };

    // Per-provider maps rather than per-provider fields: with fields, every
    // provider past Codex silently read CODEX's state (the macOS 2.1.1 bug
    // this port fixes). Replaced wholesale on each scan, never mutated in
    // place, so a binding reading `States` can never see a half-built map.
    private Dictionary<TriggerTool, ActivityState> _states = new();
    private Dictionary<TriggerTool, ActivityState> _rawStates = new();
    private Dictionary<TriggerTool, ActiveThread> _threads = new();
    private Dictionary<TriggerTool, ActivityState> _demoStates = new();
    private Dictionary<string, DateTimeOffset> _lastWorking = new();

    /// Visible state for every monitored provider. Raised as one change so
    /// five-wide surfaces refresh together.
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
    public ActiveThread? ClaudeThread => ThreadFor(TriggerTool.Claude);
    public ActiveThread? CodexThread => ThreadFor(TriggerTool.Codex);

    /// Recording rig / settings preview: pins every provider's published
    /// state, or clears the pin when passed null.
    public void Demo(ActivityState? state)
    {
        var next = new Dictionary<TriggerTool, ActivityState>();
        if (state is { } forced)
        {
            foreach (var tool in TriggerToolExtensions.All) next[tool] = forced;
        }
        _demoStates = next;
        RaiseAll();
    }

    private DispatcherTimer? _timer;
    private TranscriptEventStream? _eventStream;
    private Dispatcher? _dispatcher;
    private DateTimeOffset _lastEventKick = DateTimeOffset.MinValue;
    private bool _kickPending;
    private bool _scanInFlight;
    private bool _rescanQueued;

    public void Start()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Tick();
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(6),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        var stream = new TranscriptEventStream(() =>
        {
            // File events arrive on watcher threads; hop to the UI thread.
            _dispatcher?.BeginInvoke(EventKick);
        });
        stream.Start();
        _eventStream = stream;
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
        var trailing = new DispatcherTimer(DispatcherPriority.Background, _dispatcher!)
        {
            Interval = delay,
        };
        trailing.Tick += (_, _) =>
        {
            trailing.Stop();
            _kickPending = false;
            _lastEventKick = DateTimeOffset.UtcNow;
            Tick();
        };
        trailing.Start();
    }

    private void Tick()
    {
        // One scan at a time; a kick that lands mid-scan queues exactly one
        // follow-up so the trailing write of a turn is never dropped.
        if (_scanInFlight)
        {
            _rescanQueued = true;
            return;
        }
        _scanInFlight = true;
        var now = DateTimeOffset.UtcNow;
        // The comparer has to be restated: the copy constructor takes the
        // entries but NOT the source's comparer, and the scanner looks these
        // paths up again — Windows hands back whatever casing the directory
        // entry carries, so an ordinal snapshot would lose a session's
        // last-working stamp and downgrade a stall to idle.
        var lastWorkingSnapshot = new Dictionary<string, DateTimeOffset>(
            _lastWorking, StringComparer.OrdinalIgnoreCase);
        Task.Run(() => SessionScanner.MonitoringScan(now, lastWorkingSnapshot))
            .ContinueWith(task =>
            {
                var sessions = task.IsCompletedSuccessfully ? task.Result : new List<ScannedSession>();
                // Apply reaches the alarm center and every bound surface. A
                // throw there is swallowed by the continuation's own task, so
                // without the finally the in-flight flag would latch and the
                // monitor would silently never scan again.
                try
                {
                    Apply(sessions, now);
                }
                finally
                {
                    _scanInFlight = false;
                }
                if (_rescanQueued)
                {
                    _rescanQueued = false;
                    Tick();
                }
            }, _dispatcher is not null
                ? TaskScheduler.FromCurrentSynchronizationContext()
                : TaskScheduler.Default);
    }

    internal void Apply(List<ScannedSession> sessions, DateTimeOffset now)
    {
        // Usage-level attention (rate-limited / auth-required red) only
        // applies to providers switched ON in Settings. Someone who only
        // runs Claude keeps Codex hidden - its missing login must not
        // pulse the island red forever.
        var visibility = Model.ProviderVisibilityStore.Shared;
        UpdateLastWorking(sessions, now);
        var nextStates = new Dictionary<TriggerTool, ActivityState>();
        var nextRaw = new Dictionary<TriggerTool, ActivityState>();
        var nextThreads = new Dictionary<TriggerTool, ActiveThread>();
        foreach (var tool in MonitoredProviders)
        {
            var result = BestSession(sessions, tool,
                thread => AgentReminderCenter.Shared.HasAcknowledged(tool, thread));
            nextRaw[tool] = result.State;
            if (result.Thread is { } thread) nextThreads[tool] = thread;
            nextStates[tool] = visibility.IsVisible(tool)
                ? OverlayUsageAttention(result.State, UsageFor(tool))
                : result.State;
            AgentReminderCenter.Shared.Handle(tool, NeedsYouThreads(sessions, tool));
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
                _lastWorking[path] = now;
        }
        var livePaths = sessions
            .Where(s => s.TranscriptPath is not null)
            .Select(s => s.TranscriptPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _lastWorking = _lastWorking
            .Where(kv => livePaths.Contains(kv.Key))
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
    /// and tray listen for "Claude"/"Codex", the five-wide panel reads the
    /// maps, and a provider whose notification is missing simply stops
    /// updating on screen.
    private void RaiseAll()
    {
        Raise(nameof(States));
        Raise(nameof(Threads));
        Raise(nameof(Claude));
        Raise(nameof(Codex));
        Raise(nameof(ClaudeThread));
        Raise(nameof(CodexThread));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
