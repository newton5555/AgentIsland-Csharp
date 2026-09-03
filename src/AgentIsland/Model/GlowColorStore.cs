using System.ComponentModel;
using System.Windows.Media;
using AgentIsland.Core;

namespace AgentIsland.Model;

/// The Vivid mode's glow color — styles the ambient halo and the orbit
/// sweep only; warning amber and critical red always override, and Calm
/// shows no ambient light for it to color. Port of the macOS GlowColorStore.
public sealed class GlowColorStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.glowColor";

    public static GlowColorStore Shared { get; } = new();

    public enum Choice
    {
        Teal,
        Cobalt,
        Violet,
        Silver,
    }

    /// Declaration order is display order in the Settings swatch row.
    public static readonly Choice[] All = { Choice.Teal, Choice.Cobalt, Choice.Violet, Choice.Silver };

    private Choice _value;

    public event PropertyChangedEventHandler? PropertyChanged;

    private GlowColorStore()
    {
        _value = Parse(Preferences.Get<string?>(Key)) ?? Choice.Teal;
    }

    public Choice Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            Preferences.Set(Key, value.ToString().ToLowerInvariant());
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public Color Color => ColorOf(_value);

    public static Color ColorOf(Choice choice) => choice switch
    {
        Choice.Cobalt => Color.FromRgb(0x00, 0x47, 0xAB),
        Choice.Violet => Color.FromRgb(0x8A, 0x63, 0xFF),
        Choice.Silver => Color.FromRgb(0xC7, 0xD3, 0xDF),
        _ => Color.FromRgb(0x20, 0xC0, 0xB0), // brand teal
    };

    /// L10n key for the swatch's accessibility name.
    public static string LabelKey(Choice choice) => choice switch
    {
        Choice.Cobalt => "Cobalt",
        Choice.Violet => "Violet",
        Choice.Silver => "Silver",
        _ => "Teal",
    };

    private static Choice? Parse(string? raw) => raw switch
    {
        "teal" => Choice.Teal,
        "cobalt" => Choice.Cobalt,
        "violet" => Choice.Violet,
        "silver" => Choice.Silver,
        _ => null,
    };
}
