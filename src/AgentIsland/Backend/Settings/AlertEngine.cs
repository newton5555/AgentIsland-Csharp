using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.UI;

namespace AgentIsland.Backend.Settings;

/// Threshold alert engine: evaluates whether Claude / Codex 5-hour usage
/// has crossed the configured warning (default 80%) or critical (default 95%)
/// threshold. Fires property changes on Severity and one-shot pulse events
/// on Pulse so the island can briefly glow amber or red.
public sealed class AlertEngine : IAlertEngine, INotifyPropertyChanged
{
    public sealed record PulseLine(TriggerTool Provider, double Percent, DateTimeOffset? ResetAt, AlertSeverity Severity);

    private readonly AlertThresholdStore _thresholdStore;
    private readonly IProviderVisibilityStore _visibilityStore;
    private readonly Usage.IUsageStore _usageStore;
    private readonly IIslandModel _islandModel;
    private readonly AgentIsland.Core.Threading.IUiDispatcher? _uiDispatcher;

    private readonly HashSet<string> _crossings = new(StringComparer.Ordinal);
    private bool _warmedUp;
    private AlertSeverity _severity = AlertSeverity.None;
    private IReadOnlyList<PulseLine>? _pulse;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AlertEngine()
        : this(new AlertThresholdStore(), new ProviderVisibilityStore(), new Usage.UsageStore(), new AgentIsland.UI.IslandModel())
    {
    }

    public AlertEngine(
        AlertThresholdStore thresholdStore,
        IProviderVisibilityStore visibilityStore,
        Usage.IUsageStore usageStore,
        IIslandModel islandModel,
        AgentIsland.Core.Threading.IUiDispatcher? uiDispatcher = null)
    {
        _thresholdStore = thresholdStore ?? throw new ArgumentNullException(nameof(thresholdStore));
        _visibilityStore = visibilityStore ?? throw new ArgumentNullException(nameof(visibilityStore));
        _usageStore = usageStore ?? throw new ArgumentNullException(nameof(usageStore));
        _islandModel = islandModel ?? throw new ArgumentNullException(nameof(islandModel));
        _uiDispatcher = uiDispatcher;
    }

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
        if (!_thresholdStore.Enabled || !_visibilityStore.IsVisible(tool))
        {
            return AlertSeverity.None;
        }

        var usage = tool switch
        {
            TriggerTool.Claude => _usageStore.Claude,
            TriggerTool.Codex => _usageStore.Codex,
            _ => null,
        };
        if (usage is null) return AlertSeverity.None;
        return Grade(usage.FiveHour.UsedPercent * 100);
    }

    public void Start()
    {
        _usageStore.PropertyChanged += OnUsageChanged;
        _thresholdStore.PropertyChanged += OnSettingsChanged;
        _visibilityStore.PropertyChanged += OnSettingsChanged;
    }

    public void Stop()
    {
        _usageStore.PropertyChanged -= OnUsageChanged;
        _thresholdStore.PropertyChanged -= OnSettingsChanged;
        _visibilityStore.PropertyChanged -= OnSettingsChanged;
    }

    private void OnUsageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Usage.IUsageStore.Claude) or nameof(Usage.IUsageStore.Codex))
        {
            DispatchEvaluate();
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        DispatchEvaluate();
    }

    private void DispatchEvaluate()
    {
        if (_uiDispatcher != null)
        {
            _uiDispatcher.BeginInvoke(Evaluate);
        }
        else if (System.Windows.Application.Current?.Dispatcher is { } d)
        {
            d.BeginInvoke(Evaluate);
        }
        else
        {
            Evaluate();
        }
    }

    private void Evaluate()
    {
        if (!_thresholdStore.Enabled)
        {
            Severity = AlertSeverity.None;
            return;
        }

        var lines = new List<PulseLine>();
        var worst = AlertSeverity.None;
        foreach (var tool in new[] { TriggerTool.Claude, TriggerTool.Codex })
        {
            if (!_visibilityStore.IsVisible(tool)) continue;
            var window = tool == TriggerTool.Claude
                ? _usageStore.Claude.FiveHour
                : _usageStore.Codex.FiveHour;
            var percent = window.UsedPercent * 100;
            var grade = Grade(percent);
            if (grade > worst) worst = grade;
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
            _warmedUp = true;
            return;
        }
        if (lines.Count > 0 && !AppEnvironment.IsDemo && _islandModel.State != IslandState.Expanded)
        {
            Pulse = lines;
        }
    }

    private IEnumerable<AlertSeverity> ActiveThresholds(double percent)
    {
        if (percent >= _thresholdStore.WarningPercent) yield return AlertSeverity.Warning;
        if (percent >= _thresholdStore.CriticalPercent) yield return AlertSeverity.Critical;
    }

    private AlertSeverity Grade(double percent)
    {
        if (percent >= _thresholdStore.CriticalPercent) return AlertSeverity.Critical;
        if (percent >= _thresholdStore.WarningPercent) return AlertSeverity.Warning;
        return AlertSeverity.None;
    }

    private static string CrossingKey(TriggerTool tool, AlertSeverity threshold, DateTimeOffset resetAt) =>
        $"{tool.RawValue()}|{threshold}|{resetAt.ToUnixTimeSeconds()}";

    private void PruneOtherWindows(TriggerTool tool, DateTimeOffset currentResetAt)
    {
        var prefix = tool.RawValue() + "|";
        var current = "|" + currentResetAt.ToUnixTimeSeconds();
        _crossings.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal)
            && !key.EndsWith(current, StringComparison.Ordinal));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
