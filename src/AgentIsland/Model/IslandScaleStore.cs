using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Model;

/// Interface scale for the island (macOS 1f97e4d): magnifies the whole
/// silhouette for big monitors where its points shrink well below laptop
/// size. macOS gates it to notchless screens; every Windows screen is
/// notchless, so it simply applies. 100–150%, default 1:1.
public sealed class IslandScaleStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.interfaceScale";

    public static IslandScaleStore Shared { get; } = new();

    private double _scale;

    public event PropertyChangedEventHandler? PropertyChanged;

    private IslandScaleStore()
    {
        _scale = Clamp(Preferences.Get<double?>(Key) ?? 1.0);
    }

    public double Scale
    {
        get => _scale;
        set
        {
            var clamped = Clamp(value);
            if (Math.Abs(_scale - clamped) < 0.001) return;
            _scale = clamped;
            Preferences.Set(Key, clamped);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
        }
    }

    private static double Clamp(double value) => Math.Min(1.5, Math.Max(1.0, value));
}
