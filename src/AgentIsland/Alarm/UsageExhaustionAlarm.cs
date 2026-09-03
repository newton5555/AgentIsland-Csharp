using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Alarm;

/// Fires a distinct full-screen alarm the moment a provider's 5-hour or
/// weekly window hits 100% — the "you're out of quota until &lt;time&gt;" popup,
/// separate from the thread-finished "it's your turn" alarm. Lets the alarm
/// mean something actionable instead of conflating a finished turn with a
/// hard rate-limit block.
///
/// Port of the macOS 1.5.7 UsageExhaustionAlarm. Warms up on the first real
/// usage sample (launching into an already-exhausted window doesn't alarm)
/// and fires once per reset cycle per (provider, window).
///
/// Re-arming is keyed on the reset boundary ADVANCING to a new cycle, not on
/// its exact value. Anthropic's rolling 5-hour reset_at drifts by seconds to
/// minutes on every poll while a window sits exhausted, so an exact-timestamp
/// dedup (what the earlier port shipped, matching mac 1.5.6) churned its key
/// every 5-minute refresh and re-fired the same "out of quota" alarm over and
/// over. We now track the latest boundary accounted for per window and only
/// re-alarm when a boundary jumps past it by more than ReArmMargin — real
/// resets leap hours (5-hour) or days (weekly); jitter never does.
///
/// When both of a provider's windows cross 100% in the same refresh they
/// collapse to one alarm for the binding window (the latest reset — the time
/// you're actually blocked until).
public sealed class UsageExhaustionAlarm
{
    public static UsageExhaustionAlarm Shared { get; } = new(
        (provider, window, resetAt) => TurnAlarmWindowController.Shared.Show(
            provider,
            null,
            QuotaAlarmKey(provider, window, resetAt),
            new TurnAlarmKind.QuotaExhausted(window, resetAt)));

    private readonly Action<TriggerTool, QuotaWindowKind, DateTimeOffset> _fire;

    /// windowId ("provider-window") → the most recent reset boundary we've
    /// accounted for. Presence means "already alarmed for this cycle".
    private readonly Dictionary<string, DateTimeOffset> _alarmedResetAt = new(StringComparer.Ordinal);

    /// A boundary must advance by more than this to count as a new cycle.
    /// Comfortably larger than any observed reset_at jitter, comfortably
    /// smaller than the smallest real reset span (the 5-hour window).
    private static readonly TimeSpan ReArmMargin = TimeSpan.FromMinutes(30);

    private bool _warmedUp;

    internal UsageExhaustionAlarm(Action<TriggerTool, QuotaWindowKind, DateTimeOffset> fire)
    {
        _fire = fire;
    }

    public void Start()
    {
        UsageStore.Shared.PropertyChanged += OnUsageChanged;
        Recompute();
    }

    private void OnUsageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageStore.Claude) or nameof(UsageStore.Codex))
        {
            Recompute();
        }
    }

    private void Recompute()
    {
        // Never act in demo/preview — synthetic usage jumps around every tick.
        if (AppEnvironment.Current != AppMode.Normal) return;
        // Only once real data has flowed (matches AlertEngine's gate), so a
        // cached/zeroed launch snapshot can't fire anything.
        if (UsageStore.Shared.LastUpdated is null) return;
        Recompute(
            UsageStore.Shared.Claude,
            UsageStore.Shared.Codex,
            AgentReminderStore.Shared.Enabled,
            Model.ProviderVisibilityStore.Shared.ClaudeShown,
            Model.ProviderVisibilityStore.Shared.CodexShown,
            Model.QuotaAlarmStore.Shared.Enabled);
    }

    /// Testable core — the macOS 1.5.7 recompute() body with the store reads
    /// lifted out. Note there is no wall-clock input: the new-cycle test is
    /// purely boundary-vs-boundary, so nothing here depends on "now".
    internal void Recompute(
        AppUsage claude,
        AppUsage codex,
        bool remindersEnabled,
        bool claudeVisible = true,
        bool codexVisible = true,
        bool quotaAlarmEnabled = true)
    {
        var all = new (TriggerTool Provider, QuotaWindowKind Window, WindowUsage Usage)[]
        {
            (TriggerTool.Claude, QuotaWindowKind.FiveHour, claude.FiveHour),
            (TriggerTool.Claude, QuotaWindowKind.Weekly, claude.Weekly),
            (TriggerTool.Codex, QuotaWindowKind.FiveHour, codex.FiveHour),
            (TriggerTool.Codex, QuotaWindowKind.Weekly, codex.Weekly),
        };
        // Providers switched off in Settings never alarm — same contract as
        // the island's red attention glow.
        var windows = System.Array.FindAll(all, w =>
            (w.Provider == TriggerTool.Claude && claudeVisible) ||
            (w.Provider == TriggerTool.Codex && codexVisible));

        // Warmup: on the first real sample, record anything already exhausted
        // as already-alarmed so we don't pop for a state that predates launch.
        if (!_warmedUp)
        {
            foreach (var (provider, window, usage) in windows)
            {
                if (!IsExhausted(usage)) continue;
                if (usage.ResetAt is { } reset) AccountFor(provider, window, reset);
            }
            _warmedUp = true;
            return;
        }

        // Master alarm switch, then the dedicated quota-alarm opt-out (some
        // people only want auto-resume). Both gate BEFORE any window is
        // accounted for, so flipping either back on still delivers the next
        // real cycle rather than swallowing it silently.
        if (!remindersEnabled) return;
        if (!quotaAlarmEnabled) return;

        // One alarm per provider per pass. Claude exposes both a 5-hour and a
        // weekly window; when both cross 100% in the same refresh, collapse to
        // a single alarm for the binding window — the one with the latest
        // reset, i.e. the time you're actually blocked until.
        var exhausted = windows
            .Where(w => IsExhausted(w.Usage) && w.Usage.ResetAt is not null)
            .ToList();
        foreach (var provider in new[] { TriggerTool.Claude, TriggerTool.Codex })
        {
            var group = exhausted.Where(w => w.Provider == provider).ToList();
            if (group.Count == 0) continue;

            // Fire only if some window in this provider's group has entered a
            // new reset cycle. Check BEFORE recording, then record every group
            // boundary so a jittering reset_at (or a window flapping just
            // under/over 100% within one cycle) can never re-fire.
            var hasNewCycle = group.Any(w => IsNewCycle(w.Provider, w.Window, w.Usage.ResetAt!.Value));
            foreach (var (p, window, usage) in group) AccountFor(p, window, usage.ResetAt!.Value);
            if (!hasNewCycle) continue;

            var binding = group.MaxBy(w => w.Usage.ResetAt!.Value);
            // Name the window by its REAL period, not its slot: an alarm
            // that says "5-hour limit reached" for a week-long block would
            // be a lie if a provider re-shapes its windows.
            var namedWindow = binding.Usage.IsLongPeriod ? QuotaWindowKind.Weekly : binding.Window;
            _fire(binding.Provider, namedWindow, binding.Usage.ResetAt!.Value);
        }
    }

    private static string WindowId(TriggerTool provider, QuotaWindowKind window) =>
        $"{provider.RawValue()}-{TurnAlarmKind.RawValue(window)}";

    /// A window is fire-worthy when we've never alarmed it, or its reset
    /// boundary has jumped to a new cycle (past the last one by > margin).
    private bool IsNewCycle(TriggerTool provider, QuotaWindowKind window, DateTimeOffset resetAt)
    {
        if (!_alarmedResetAt.TryGetValue(WindowId(provider, window), out var prev)) return true;
        return resetAt > prev + ReArmMargin;
    }

    /// Record the boundary accounted for, monotonically — a reset_at that
    /// drifts EARLIER can't lower the bar and let jitter re-fire.
    private void AccountFor(TriggerTool provider, QuotaWindowKind window, DateTimeOffset resetAt)
    {
        var id = WindowId(provider, window);
        _alarmedResetAt[id] = _alarmedResetAt.TryGetValue(id, out var existing) && existing > resetAt
            ? existing
            : resetAt;
    }

    internal static bool IsExhausted(WindowUsage usage) =>
        usage.Error is null && usage.UsedPercent >= 0.999;

    /// Keyed on the reset boundary so it dedups against the currently-showing
    /// or queued exhaustion alarm in TurnAlarmWindowController — the macOS
    /// alarmKey shape.
    internal static string QuotaAlarmKey(TriggerTool provider, QuotaWindowKind window, DateTimeOffset? resetAt)
    {
        var stamp = resetAt is { } reset ? reset.ToUnixTimeSeconds().ToString() : "none";
        return $"exhausted-{provider.RawValue()}-{TurnAlarmKind.RawValue(window)}-{stamp}";
    }
}
