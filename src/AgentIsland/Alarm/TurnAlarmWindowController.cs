using AgentIsland.Core;

namespace AgentIsland.Alarm;

/// Owns the single visible alarm panel. Additional finished turns queue in
/// FIFO order; dismissing one recalls the next, so nothing gets swallowed
/// and nothing stacks.
public sealed class TurnAlarmWindowController
{
    public static TurnAlarmWindowController Shared { get; } = new();
    private TurnAlarmWindowController() { }

    private TurnAlarmWindow? _current;
    private readonly List<(TriggerTool Provider, ActivityMonitor.ActiveThread? Thread, string DeliveryKey, TurnAlarmKind? Kind)> _queue = new();

    public void Show(TriggerTool provider, ActivityMonitor.ActiveThread? thread, string deliveryKey, TurnAlarmKind? kind = null)
    {
        if (_current is { } current)
        {
            if (current.DeliveryKey == deliveryKey) return;
            if (_queue.Any(item => item.DeliveryKey == deliveryKey)) return;
            Banner(provider, thread, kind);
            _queue.Add((provider, thread, deliveryKey, kind));
            return;
        }
        Banner(provider, thread, kind);
        Present(provider, thread, deliveryKey, kind);
    }

    /// The tray banner rides along with every alarm — queued ones included,
    /// so an event isn't silent just because another panel holds the stage.
    /// Fires once per delivery key (both callers sit past the dedup guards).
    private static void Banner(TriggerTool provider, ActivityMonitor.ActiveThread? thread, TurnAlarmKind? kind)
    {
        string title, body;
        if (kind is TurnAlarmKind.QuotaExhausted quota)
        {
            var windowName = quota.Window == QuotaWindowKind.FiveHour
                ? Localization.L10n.Tr("5-hour limit")
                : Localization.L10n.Tr("Weekly limit");
            title = Localization.L10n.TrFormat("{0} {1} reached", provider.Display(), windowName);
            body = quota.ResetAt is { } reset
                ? Localization.L10n.TrFormat(
                    "You're out until it resets at {0}.",
                    reset.ToLocalTime().ToString("HH:mm"))
                : Localization.L10n.Tr("Out of quota");
        }
        else
        {
            title = Localization.L10n.Tr("It's your turn");
            body = thread is { } t && !string.IsNullOrWhiteSpace(t.Label)
                ? t.Label + " — " + Localization.L10n.Tr("The thread finished. Come back and reply.")
                : Localization.L10n.Tr("The thread finished. Come back and reply.");
        }
        UI.TrayIcon.Current?.ShowBanner(title, body);
    }

    /// The turn left needsYou (user replied, or it aged out): a visible
    /// panel for it is pure noise. Closes without acknowledging.
    public void AutoDismiss(TriggerTool provider, string deliveryKey)
    {
        _queue.RemoveAll(item => item.DeliveryKey == deliveryKey);
        if (_current is { } current && current.DeliveryKey == deliveryKey)
        {
            current.DismissSilently();
        }
    }

    private void Present(TriggerTool provider, ActivityMonitor.ActiveThread? thread, string deliveryKey, TurnAlarmKind? kind)
    {
        var window = new TurnAlarmWindow(provider, thread, deliveryKey, kind);
        window.Dismissed += OnDismissed;
        _current = window;
        window.Show();
        window.Activate();
    }

    private void OnDismissed(TurnAlarmWindow window)
    {
        window.Dismissed -= OnDismissed;
        if (ReferenceEquals(_current, window)) _current = null;
        if (_queue.Count == 0) return;
        var next = _queue[0];
        _queue.RemoveAt(0);
        // Let the close unwind before the next panel takes the stage. A
        // Show() can race in during that gap (a fresh turn finishing), so
        // re-check _current at fire time — if one is already up, put this
        // back at the head instead of stacking a second window (the macOS
        // controller's guard).
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_current is not null)
            {
                _queue.Insert(0, next);
                return;
            }
            Present(next.Provider, next.Thread, next.DeliveryKey, next.Kind);
        });
    }
}
