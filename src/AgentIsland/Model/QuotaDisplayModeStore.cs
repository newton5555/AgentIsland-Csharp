using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Model;

/// How usage tiles and peek pills present a quota window: percent consumed
/// ("73%") or percent still available ("27%"). Some people plan around
/// what's left, not what's spent — this flips every percent readout in one
/// place while the underlying data stays UsedPercent. (Parity with the
/// macOS QuotaDisplayModeStore.)
public sealed class QuotaDisplayModeStore : INotifyPropertyChanged
{
    public static QuotaDisplayModeStore Shared { get; } = new();

    private const string Key = "AgentIsland.quotaShowsRemaining";

    private bool _showsRemaining;

    public event PropertyChangedEventHandler? PropertyChanged;

    private QuotaDisplayModeStore()
    {
        _showsRemaining = Preferences.Get<bool?>(Key) ?? false;
    }

    public bool ShowsRemaining
    {
        get => _showsRemaining;
        set
        {
            if (_showsRemaining == value) return;
            _showsRemaining = value;
            Preferences.Set(Key, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsRemaining)));
        }
    }

    /// 0-100 display value for a window, honoring the mode.
    public double DisplayValue(double usedPercent)
    {
        var used = usedPercent * 100;
        return ShowsRemaining ? System.Math.Max(0, 100 - used) : used;
    }
}
