using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.UI;

/// macOS ChartStyle mirror — same four styles, same order (the line
/// style died there; a stored "Spark" falls back to Stepped on parse).
public enum ChartStyle
{
    Stepped,
    Bar,
    Ring,
    Numeric,
}

/// Usage chart visualization. Defaults to Stepped — the segmented style the
/// product's own screenshots and website ship with.
public sealed class StylePreferenceStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.chartStyle";

    public static StylePreferenceStore Shared { get; } = new();

    private ChartStyle _style;

    public event PropertyChangedEventHandler? PropertyChanged;

    private StylePreferenceStore()
    {
        var raw = Preferences.Get<string?>(Key);
        _style = Enum.TryParse<ChartStyle>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : ChartStyle.Stepped;
    }

    public ChartStyle Style
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

    public void Cycle()
    {
        var values = Enum.GetValues<ChartStyle>();
        Style = values[(Array.IndexOf(values, _style) + 1) % values.Length];
    }
}
