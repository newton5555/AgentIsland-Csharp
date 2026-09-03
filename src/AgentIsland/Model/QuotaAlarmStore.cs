using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Model;

/// Whether hitting 100% on a 5-hour or weekly window raises the full-screen
/// "out of quota until &lt;time&gt;" alarm. Some people only want auto-resume and
/// treat the quota popup as low-priority — turning this off silences the
/// exhaustion alarm while leaving turn alarms untouched. Default ON, so the
/// pre-setting behavior is preserved for everyone who never opens Settings.
public sealed class QuotaAlarmStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.quotaAlarmEnabled";

    public static QuotaAlarmStore Shared { get; } = new();

    private bool _enabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    private QuotaAlarmStore()
    {
        // Missing key → ON. Preferences.Get<bool?> returns null when unset,
        // so the ?? keeps existing users who never touched the setting on the
        // prior behavior (a plain bool default would silence it for them).
        _enabled = Preferences.Get<bool?>(Key) ?? true;
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
