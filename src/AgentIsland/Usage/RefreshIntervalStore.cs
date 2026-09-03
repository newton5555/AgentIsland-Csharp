using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// Poll cadence for the usage APIs. Anthropic rate-limits /api/oauth/usage
/// aggressively — anything below the 5-minute floor burns the daily quota,
/// so the presets are fixed at 5m / 15m / 30m exactly as shipped on macOS.
public sealed class RefreshIntervalStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.refreshInterval";

    /// Declared before Shared: static initializers run in declaration order,
    /// and Shared's constructor reads this array.
    public static readonly int[] Presets = { 300, 900, 1800 };

    public static RefreshIntervalStore Shared { get; } = new();

    private int _seconds;

    public event PropertyChangedEventHandler? PropertyChanged;

    private RefreshIntervalStore()
    {
        var saved = Preferences.Get<int?>(Key) ?? 300;
        _seconds = Presets.Contains(saved) ? saved : 300;
    }

    public int Seconds
    {
        get => _seconds;
        set
        {
            var clamped = Presets.Contains(value) ? value : 300;
            if (_seconds == clamped) return;
            _seconds = clamped;
            Preferences.Set(Key, clamped);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Seconds)));
        }
    }
}
