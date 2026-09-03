using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Model;

/// How and where the island sits on screen. Windows has no notch reserving
/// the top-center, so placement is a user choice, unlike macOS which always
/// pins top-center over the notch.
public enum IslandPlacement
{
    /// Horizontal bar hugging the top edge (the Mac look).
    TopBar,
    /// A free-floating widget the user drags anywhere; its position sticks.
    Floating,
}

/// Persisted island placement. Windows-only concept, hence the AgentIsland.
/// key prefix rather than the ported MacIsland. namespace.
public sealed class IslandPositionStore : INotifyPropertyChanged
{
    private const string PlacementKey = "AgentIsland.islandPlacement";
    private const string FloatXKey = "AgentIsland.floatX";
    private const string FloatYKey = "AgentIsland.floatY";
    // Legacy key from the first edge iteration.
    private const string LegacyEdgeKey = "AgentIsland.islandEdge";

    public static IslandPositionStore Shared { get; } = new();

    private IslandPlacement _placement;
    private double? _floatX;
    private double? _floatY;

    public event PropertyChangedEventHandler? PropertyChanged;

    private IslandPositionStore()
    {
        var raw = Preferences.Get<string?>(PlacementKey);
        if (Enum.TryParse<IslandPlacement>(raw, out var placement))
        {
            _placement = placement;
        }
        else if (raw is "BottomBar" or "Tray"
            || Preferences.Get<string?>(LegacyEdgeKey) == "Bottom")
        {
            // Retired modes (bottom bar, tray dock, bottom edge) fold into
            // the surviving out-of-the-way choice.
            _placement = IslandPlacement.Floating;
        }
        else
        {
            _placement = IslandPlacement.TopBar;
        }
        _floatX = Preferences.Get<double?>(FloatXKey);
        _floatY = Preferences.Get<double?>(FloatYKey);
    }

    public IslandPlacement Placement
    {
        get => _placement;
        set
        {
            if (_placement == value) return;
            _placement = value;
            Preferences.Set(PlacementKey, value.ToString());
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Placement)));
        }
    }

    /// The persisted floating top-left in DIP, or null until first dragged.
    public (double X, double Y)? FloatingPoint =>
        _floatX is { } x && _floatY is { } y ? (x, y) : null;

    /// Persist a dragged floating position; silently ignored unless the
    /// island is actually in Floating mode (a drag in another mode is noise).
    public void SetFloatingPoint(double x, double y)
    {
        _floatX = x;
        _floatY = y;
        Preferences.Set(FloatXKey, x);
        Preferences.Set(FloatYKey, y);
    }
}
