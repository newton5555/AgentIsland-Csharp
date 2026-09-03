using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Model;

public enum TokenCountMode
{
    All,
    Billable,
}

/// Which token total drives the TOKENS hero on the cost screen. `All`
/// mirrors ccusage (everything that crossed the wire); `Billable` matches
/// Anthropic's claude.ai stats (input + output only). Both totals are
/// computed every scan, so flipping is instant.
public sealed class TokenCountModeStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.tokenCountMode";

    public static TokenCountModeStore Shared { get; } = new();

    private TokenCountMode _mode;

    public event PropertyChangedEventHandler? PropertyChanged;

    private TokenCountModeStore()
    {
        var raw = Preferences.Get<string?>(Key);
        _mode = raw == nameof(TokenCountMode.Billable) ? TokenCountMode.Billable : TokenCountMode.All;
    }

    public TokenCountMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            Preferences.Set(Key, value.ToString());
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode)));
        }
    }
}
