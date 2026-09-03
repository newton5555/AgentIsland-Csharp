using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Model;

public enum AlertSeverity
{
    None,
    Warning,
    Critical,
}

/// Approaching-limit alerts over the visible 5h windows. Warmup swallows the
/// first usage update (already-above-threshold at launch is history, not a
/// crossing); crossing memory is keyed per (provider, threshold, resetAt) so
/// value bounces don't re-pulse and a new reset window re-arms.
public sealed class AlertEngine : INotifyPropertyChanged
{
    public static AlertEngine Shared { get; } = new();

    public sealed record PulseLine(TriggerTool Provider, double Percent, DateTimeOffset? ResetAt, AlertSeverity Severity);

    private readonly HashSet<string> _crossings = new(StringComparer.Ordinal);
    private bool _warmedUp;
    private AlertSeverity _severity = AlertSeverity.None;
    private IReadOnlyList<PulseLine>? _pulse;

    public event PropertyChangedEventHandler? PropertyChanged;

    private AlertEngine() { }

    public AlertSeverity Severity
    {
        get => _severity;
        private set
        {
            if (_severity == value) return;
            _severity = value;
            Raise(nameof(Severity));
        }
    }

    /// One-shot pulse payload; the UI consumes it and calls ClearPulse.
    public IReadOnlyList<PulseLine>? Pulse
    {
        get => _pulse;
        private set { _pulse = value; Raise(nameof(Pulse)); }
    }

    public void ClearPulse() => _pulse = null;

    public AlertSeverity SeverityFor(TriggerTool tool)
    {
        if (!AlertThresholdStore.Shared.Enabled || !ProviderVisibilityStore.Shared.IsVisible(tool))
        {
            return AlertSeverity.None;
        }
        // Guests have no AppUsage in the main store; borrowing Codex's
        // numbers here once produced phantom threshold alerts for them.
        var usage = tool switch
        {
            TriggerTool.Claude => UsageStore.Shared.Claude,
            TriggerTool.Codex => UsageStore.Shared.Codex,
            _ => null,
        };
        if (usage is null) return AlertSeverity.None;
        return Grade(usage.FiveHour.UsedPercent * 100);
    }

    public void Start()
    {
        UsageStore.Shared.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(UsageStore.Claude) or nameof(UsageStore.Codex))
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(Evaluate);
            }
        };
        AlertThresholdStore.Shared.PropertyChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(Evaluate);
        ProviderVisibilityStore.Shared.PropertyChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(Evaluate);
    }

    private void Evaluate()
    {
        if (!AlertThresholdStore.Shared.Enabled)
        {
            Severity = AlertSeverity.None;
            return;
        }

        var lines = new List<PulseLine>();
        var worst = AlertSeverity.None;
        foreach (var tool in new[] { TriggerTool.Claude, TriggerTool.Codex })
        {
            if (!ProviderVisibilityStore.Shared.IsVisible(tool)) continue;
            var window = tool == TriggerTool.Claude
                ? UsageStore.Shared.Claude.FiveHour
                : UsageStore.Shared.Codex.FiveHour;
            var percent = window.UsedPercent * 100;
            var grade = Grade(percent);
            if (grade > worst) worst = grade;
            // No usable reset boundary -> skip crossing bookkeeping for this
            // update; the next update with a real resetAt is the one we react to.
            if (window.ResetAt is not { } resetAt) continue;
            PruneOtherWindows(tool, resetAt);
            foreach (var threshold in ActiveThresholds(percent))
            {
                var key = CrossingKey(tool, threshold, resetAt);
                if (_crossings.Add(key) && _warmedUp)
                {
                    lines.Add(new PulseLine(tool, percent, resetAt, threshold));
                }
            }
        }
        Severity = worst;
        if (!_warmedUp)
        {
            // Initial discovery is a warmup tick: crossings recorded, no pulse.
            _warmedUp = true;
            return;
        }
        if (lines.Count > 0 && !AppEnvironment.IsDemo && UI.IslandModel.Shared.State != UI.IslandState.Expanded)
        {
            Pulse = lines;
        }
    }

    private IEnumerable<AlertSeverity> ActiveThresholds(double percent)
    {
        if (percent >= AlertThresholdStore.Shared.WarningPercent) yield return AlertSeverity.Warning;
        if (percent >= AlertThresholdStore.Shared.CriticalPercent) yield return AlertSeverity.Critical;
    }

    private AlertSeverity Grade(double percent)
    {
        if (percent >= AlertThresholdStore.Shared.CriticalPercent) return AlertSeverity.Critical;
        if (percent >= AlertThresholdStore.Shared.WarningPercent) return AlertSeverity.Warning;
        return AlertSeverity.None;
    }

    private static string CrossingKey(TriggerTool tool, AlertSeverity threshold, DateTimeOffset resetAt) =>
        $"{tool.RawValue()}|{threshold}|{resetAt.ToUnixTimeSeconds()}";

    /// Window reset (new resetAt) clears the memory for that provider so the
    /// next cycle can pulse again.
    private void PruneOtherWindows(TriggerTool tool, DateTimeOffset currentResetAt)
    {
        var prefix = tool.RawValue() + "|";
        var current = "|" + currentResetAt.ToUnixTimeSeconds();
        _crossings.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal)
            && !key.EndsWith(current, StringComparison.Ordinal));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
