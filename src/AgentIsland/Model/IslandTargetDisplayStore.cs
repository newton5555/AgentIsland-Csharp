using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Model;

/// Which screen hosts the island: Auto (primary) or pinned to a specific
/// display by device name, falling back to Auto when it's unplugged.
public sealed class IslandTargetDisplayStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.targetDisplay";

    public static IslandTargetDisplayStore Shared { get; } = new();

    private string _choice;

    public event PropertyChangedEventHandler? PropertyChanged;

    private IslandTargetDisplayStore()
    {
        _choice = Preferences.Get<string?>(Key) ?? "auto";
    }

    /// "auto" or a Screen.DeviceName.
    public string Choice
    {
        get => _choice;
        set
        {
            if (_choice == value) return;
            _choice = value;
            Preferences.Set(Key, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Choice)));
        }
    }

    public System.Windows.Forms.Screen Resolve()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (_choice != "auto")
        {
            var pinned = screens.FirstOrDefault(s => s.DeviceName == _choice);
            if (pinned is not null) return pinned;
        }
        return System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
    }
}
