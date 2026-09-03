using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.UI;

/// "Always show usage in the top bar" — when on, the compact silhouette
/// carries the 5h percentages instead of staying blank until hover.
public sealed class AlwaysShowUsageStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.alwaysShowUsage";

    public static AlwaysShowUsageStore Shared { get; } = new();

    private bool _enabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    private AlwaysShowUsageStore()
    {
        _enabled = Preferences.Get<bool?>(Key) ?? false;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Preferences.Set(Key, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }
}
