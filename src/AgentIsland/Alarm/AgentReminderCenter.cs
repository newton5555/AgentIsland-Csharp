using AgentIsland.Core;

namespace AgentIsland.Alarm;

/// Turn-alarm delivery pipeline: per-turn delivery keys, 12h acknowledged
/// memory (persisted), launch/pre-launch baselining, storm collapse, a 1s
/// confirmation buffer that a fresher scan can cancel, and auto-dismissal
/// when the turn leaves needsYou. Direct port of the macOS center with the
/// system notification delivered as a tray balloon.
public sealed class AgentReminderCenter
{
    public static AgentReminderCenter Shared { get; } = new();

    private readonly Dictionary<string, DateTimeOffset> _deliveredNeedsYouKeys = new();
    private readonly Dictionary<string, HashSet<string>> _activeNeedsYouKeys = new();
    private Dictionary<string, DateTimeOffset> _acknowledgedNeedsYouKeys;
    private readonly Dictionary<string, System.Threading.CancellationTokenSource> _pendingNeedsYouTasks = new();
    private readonly HashSet<string> _observedProviders = new();
    private static readonly TimeSpan RememberedKeyLifetime = TimeSpan.FromHours(12);
    /// Scans are event-driven: a reply appended to the transcript triggers a
    /// rescan within ~1.2s that cancels this pending confirm; anything that
    /// still slips through auto-dismisses. 1s keeps the popup inside the
    /// "it just finished" moment.
    private static readonly TimeSpan NeedsYouConfirmationDelay = TimeSpan.FromSeconds(1);
    private const string AcknowledgedPrefsKey = "AgentIsland.acknowledgedNeedsYouKeys";

    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    /// Alarms held back because the session's host app was frontmost — the
    /// user is LOOKING at the finished turn; a popup over it is noise. They
    /// fire the moment focus leaves with the turn still open (macOS
    /// frontmost-hold, ccb6e44).
    private readonly Dictionary<string, (TriggerTool Provider, ActivityMonitor.ActiveThread Thread)> _heldAlarms
        = new(StringComparer.Ordinal);

    private AgentReminderCenter()
    {
        _acknowledgedNeedsYouKeys = LoadAcknowledgedKeys();
        InstallForegroundWatch();
    }

    public void Handle(TriggerTool provider, IReadOnlyList<ActivityMonitor.ActiveThread> needsYouThreads)
    {
        if (!AgentReminderStore.Shared.Enabled) return;
        PruneRememberedKeys();
        var providerKey = provider.RawValue();
        var isFirstObservation = _observedProviders.Add(providerKey);
        var keyed = needsYouThreads
            .Select(thread => (Key: DeliveryKeyFor(provider, thread), Thread: thread))
            .ToList();
        var currentKeys = keyed.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);

        // Turns that left needsYou (the user replied, or they aged out): the
        // pending confirm is void and a visible panel for them is pure noise.
        if (_activeNeedsYouKeys.TryGetValue(providerKey, out var previous))
        {
            foreach (var staleKey in previous.Where(key => !currentKeys.Contains(key)))
            {
                CancelPending(staleKey);
                _heldAlarms.Remove(staleKey);
                TurnAlarmWindowController.Shared.AutoDismiss(provider, staleKey);
            }
        }
        _activeNeedsYouKeys[providerKey] = currentKeys;

        var fresh = new List<(string Key, ActivityMonitor.ActiveThread Thread)>();
        var baselined = false;
        foreach (var (key, thread) in keyed)
        {
            if (_acknowledgedNeedsYouKeys.ContainsKey(key)
                || _deliveredNeedsYouKeys.ContainsKey(key)
                || _pendingNeedsYouTasks.ContainsKey(key))
            {
                continue;
            }
            // First sighting at launch, and turns finished before this app
            // was running, are history rather than news — record, don't alarm.
            if (isFirstObservation || thread.Modified < _startedAt)
            {
                Baseline(key);
                baselined = true;
                continue;
            }
            fresh.Add((key, thread));
        }

        // Storm collapse: an orchestration fanning out dozens of subagents
        // finishes them in bursts. To the human that is ONE event — alarm for
        // the newest turn only and record the rest.
        if (fresh.Count > 0)
        {
            ScheduleDelivery(provider, fresh[0].Thread, fresh[0].Key);
            foreach (var extra in fresh.Skip(1))
            {
                Baseline(extra.Key);
                baselined = true;
            }
        }

        // Persist ONCE, not once per key — at launch isFirstObservation can
        // baseline dozens of finished sessions, and a full settings-file write
        // per key was janking the UI thread.
        if (baselined) PersistAcknowledgedKeys();
    }

    public bool HasAcknowledged(TriggerTool provider, ActivityMonitor.ActiveThread? thread) =>
        _acknowledgedNeedsYouKeys.ContainsKey(DeliveryKeyFor(provider, thread));

    public void Acknowledge(TriggerTool provider, ActivityMonitor.ActiveThread? thread)
    {
        var deliveryKey = DeliveryKeyFor(provider, thread);
        CancelPending(deliveryKey);
        _heldAlarms.Remove(deliveryKey);
        _acknowledgedNeedsYouKeys[deliveryKey] = DateTimeOffset.Now;
        _deliveredNeedsYouKeys[deliveryKey] = DateTimeOffset.Now;
        PersistAcknowledgedKeys();
    }

    private void PruneRememberedKeys()
    {
        var cutoff = DateTimeOffset.Now - RememberedKeyLifetime;
        var acknowledgedBefore = _acknowledgedNeedsYouKeys.Count;
        foreach (var stale in _deliveredNeedsYouKeys.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
        {
            _deliveredNeedsYouKeys.Remove(stale);
        }
        _acknowledgedNeedsYouKeys = _acknowledgedNeedsYouKeys
            .Where(kv => kv.Value >= cutoff)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (acknowledgedBefore != _acknowledgedNeedsYouKeys.Count)
        {
            PersistAcknowledgedKeys();
        }
    }

    /// Records a key as already-seen WITHOUT persisting — the caller batches
    /// a single write after the loop.
    private void Baseline(string deliveryKey)
    {
        _acknowledgedNeedsYouKeys[deliveryKey] = DateTimeOffset.Now;
    }

    private static string DeliveryKeyFor(TriggerTool provider, ActivityMonitor.ActiveThread? thread) =>
        ReminderDeliveryKey.Make(
            provider.RawValue(),
            (int)ActivityState.NeedsYou,
            thread?.TranscriptPath,
            thread?.SessionId ?? "",
            thread?.Cwd ?? "",
            thread?.Label ?? "",
            thread?.TurnKey);

    private void ScheduleDelivery(TriggerTool provider, ActivityMonitor.ActiveThread thread, string deliveryKey)
    {
        var cts = new System.Threading.CancellationTokenSource();
        _pendingNeedsYouTasks[deliveryKey] = cts;
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(NeedsYouConfirmationDelay, cts.Token); }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested) return;
            await dispatcher.BeginInvoke(() => ConfirmAndDeliver(provider, thread, deliveryKey));
        });
    }

    private void ConfirmAndDeliver(TriggerTool provider, ActivityMonitor.ActiveThread thread, string deliveryKey)
    {
        _pendingNeedsYouTasks.Remove(deliveryKey);
        // Event-driven scans re-evaluate the active set within ~1.2s of any
        // transcript write, so by fire time a turn the user already answered
        // was removed (and this delivery cancelled) by that fresher scan.
        if (!AgentReminderStore.Shared.Enabled) return;
        if (!_activeNeedsYouKeys.TryGetValue(provider.RawValue(), out var active)
            || !active.Contains(deliveryKey))
        {
            return;
        }
        if (_acknowledgedNeedsYouKeys.ContainsKey(deliveryKey)
            || _deliveredNeedsYouKeys.ContainsKey(deliveryKey))
        {
            return;
        }
        // Frontmost hold: the alarm would land on top of the very session
        // the user is watching. Park it; the focus-change watch below fires
        // it the moment they switch away with the turn still open. Sessions
        // whose host can't be resolved (daemons, containers) fail open.
        if (!AgentReminderStore.Shared.AlarmWhenFrontmost
            && AgentHostAppResolver.IsHostAppFrontmost(provider, thread.Cwd))
        {
            _heldAlarms[deliveryKey] = (provider, thread);
            // Opt-in: one chime marks the moment instead of total silence —
            // "the app is frontmost" doesn't always mean "the user noticed".
            if (AgentReminderStore.Shared.FrontmostSoundOnly) PlayFrontmostChime();
            return;
        }
        _deliveredNeedsYouKeys[deliveryKey] = DateTimeOffset.Now;
        Deliver(provider, thread, deliveryKey);
    }

    /// EVENT_SYSTEM_FOREGROUND (the didActivateApplication analog): each
    /// focus change re-examines the held alarms. No polling — zero cost
    /// until the user actually switches windows.
    private void InstallForegroundWatch()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            // The delegate must outlive the hook — a collected callback is
            // a native callback into freed memory.
            _foregroundCallback = (_, _, _, _, _, _, _) => ReleaseHeldAlarms();
            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundCallback, 0, 0, WINEVENT_OUTOFCONTEXT);
        });
    }

    private void ReleaseHeldAlarms()
    {
        if (_heldAlarms.Count == 0) return;
        foreach (var (key, held) in _heldAlarms.ToList())
        {
            // Conditions may have moved on while parked.
            if (!AgentReminderStore.Shared.Enabled
                || !_activeNeedsYouKeys.TryGetValue(held.Provider.RawValue(), out var active)
                || !active.Contains(key)
                || _acknowledgedNeedsYouKeys.ContainsKey(key)
                || _deliveredNeedsYouKeys.ContainsKey(key))
            {
                _heldAlarms.Remove(key);
                continue;
            }
            if (AgentHostAppResolver.IsHostAppFrontmost(held.Provider, held.Thread.Cwd)) continue; // still watching it
            _heldAlarms.Remove(key);
            _deliveredNeedsYouKeys[key] = DateTimeOffset.Now;
            Deliver(held.Provider, held.Thread, key);
        }
    }

    private IntPtr _foregroundHook;
    private WinEventDelegate? _foregroundCallback;

    private delegate void WinEventDelegate(
        IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint timestamp);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
        uint processId, uint threadId, uint flags);

    private void CancelPending(string deliveryKey)
    {
        if (_pendingNeedsYouTasks.Remove(deliveryKey, out var cts))
        {
            cts.Cancel();
        }
    }

    private static Dictionary<string, DateTimeOffset> LoadAcknowledgedKeys()
    {
        var stored = Preferences.Get<Dictionary<string, double>?>(AcknowledgedPrefsKey);
        if (stored is null) return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        return stored.ToDictionary(
            kv => kv.Key,
            kv => DateTimeOffset.FromUnixTimeMilliseconds((long)kv.Value),
            StringComparer.Ordinal);
    }

    private void PersistAcknowledgedKeys()
    {
        var stored = _acknowledgedNeedsYouKeys.ToDictionary(
            kv => kv.Key,
            kv => (double)kv.Value.ToUnixTimeMilliseconds());
        Preferences.Set(AcknowledgedPrefsKey, stored);
    }

    /// Retained so the chime isn't garbage-collected mid-play.
    private System.Windows.Media.MediaPlayer? _frontmostChime;

    private void PlayFrontmostChime()
    {
        var store = AgentReminderStore.Shared;
        if (!store.SoundEnabled) return;
        if (store.ResolveSoundFile() is not { } file) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() =>
        {
            try
            {
                _frontmostChime ??= new System.Windows.Media.MediaPlayer();
                _frontmostChime.Stop();
                _frontmostChime.Open(new Uri(file));
                _frontmostChime.Volume = store.Volume;
                _frontmostChime.Play();
            }
            catch
            {
                // A missing codec only costs the chime.
            }
        });
    }

    private void Deliver(TriggerTool provider, ActivityMonitor.ActiveThread? thread, string deliveryKey)
    {
        // The foreground alarm window IS the notification — a system toast
        // in the corner would just repeat the same message next to it.
        TurnAlarmWindowController.Shared.Show(provider, thread, deliveryKey);
    }
}
