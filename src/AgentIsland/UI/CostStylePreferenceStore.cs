using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.UI;

public enum CostStyle
{
    Dollar,
    Multi,
    Tokens,
    Trend,
}

/// Cost page hero style — USD / VALUE / TOKENS / TREND, mirroring the macOS
/// CostStylePref.
public sealed class CostStylePreferenceStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.costStyle";

    public static CostStylePreferenceStore Shared { get; } = new();

    private CostStyle _style;

    public event PropertyChangedEventHandler? PropertyChanged;

    private CostStylePreferenceStore()
    {
        var raw = Preferences.Get<string?>(Key);
        _style = Enum.TryParse<CostStyle>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : CostStyle.Dollar;
    }

    public CostStyle Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            Preferences.Set(Key, value.ToString());
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Style)));
        }
    }

    public string ChipLabel => _style switch
    {
        CostStyle.Dollar => "USD",
        CostStyle.Multi => "VALUE",
        CostStyle.Tokens => "TOKENS",
        CostStyle.Trend => "TREND",
        _ => "USD",
    };
}
