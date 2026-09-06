using System.ComponentModel;
using System.Windows;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Backend.Settings;

namespace AgentIsland.UI;

public enum IslandState
{
    Compact,
    Peek,
    Expanded,
}

public enum IslandSpacingMode
{
    NotchStyle,
    CompactStyle,
}

/// State machine + geometry for the island silhouette. Sizes are the shipped
/// macOS constants: tab 38, peek pill slot 104 per side, expanded panel 800
/// wide with 188/244pt content pages.
public sealed class IslandModel : IIslandModel
{
    public const double TabWidth = 38;
    public const double PillSlotWidth = 104;
    public const double ExpandedWidth = 800;
    /// Windows counterpart of the menu-bar-height silhouette (37pt notched
    /// Mac, 24pt compact): one value tuned for taskbar-less screen tops.
    public const double SilhouetteHeight = 36;
    public const double UsageContentHeight = 188;
    public const double OverviewContentHeight = 244;
    public const double OverviewDetailHeight = 52;
    /// Corner radius on the side away from the screen edge. macOS uses a
    /// fixed 14 for both states (IslandShape.swift).
    public const double CompactCornerRadius = 14;
    public const double ExpandedCornerRadius = 14;

    private readonly IProviderVisibilityStore? _visibilityStore;
    private readonly IslandPositionStore? _positionStore;
    private readonly AlwaysShowUsageStore? _alwaysShowUsageStore;

    private IslandState _state = IslandState.Compact;
    private IslandSpacingMode _spacingMode;
    private double _expandedContentHeight = UsageContentHeight;

    public IslandModel(
        IProviderVisibilityStore? visibilityStore = null,
        IslandPositionStore? positionStore = null,
        AlwaysShowUsageStore? alwaysShowUsageStore = null)
    {
        _visibilityStore = visibilityStore;
        _positionStore = positionStore;
        _alwaysShowUsageStore = alwaysShowUsageStore;

        // Windows displays have no notch, so the macOS Compact/Notched-Mac
        // bar-style choice is gone: the bar is always the wide layout
        // (any previously persisted choice is ignored).
        _spacingMode = IslandSpacingMode.NotchStyle;

        // The center gap depends on placement (see NotchWidth); re-emit Size
        // so the silhouette re-measures the moment the mode flips.
        if (_positionStore != null)
        {
            _positionStore.PropertyChanged += (_, _) => Raise(nameof(Size));
        }
        // A provider flip can change the solo split, so the bar reflows live.
        if (_visibilityStore != null)
        {
            _visibilityStore.PropertyChanged += (_, _) => Raise(nameof(Size));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IslandState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Raise(nameof(State));
            Raise(nameof(Size));
            Raise(nameof(CornerRadius));
        }
    }

    public IslandSpacingMode SpacingMode
    {
        get => _spacingMode;
        set
        {
            if (_spacingMode == value) return;
            _spacingMode = value;
            Raise(nameof(SpacingMode));
            Raise(nameof(Size));
        }
    }

    public double ExpandedContentHeight
    {
        get => _expandedContentHeight;
        set
        {
            if (Math.Abs(_expandedContentHeight - value) < 0.001) return;
            _expandedContentHeight = value;
            Raise(nameof(ExpandedContentHeight));
            Raise(nameof(Size));
        }
    }

    /// The lone visible provider, or null with both (or neither) shown.
    /// A solo bar keeps the full symmetric width and SPLITS its flanks —
    /// logo on the provider's side, usage number on the other (macOS
    /// 9ee4219) — instead of folding the empty half away.
    public TriggerTool? SoloProvider
    {
        get
        {
            if (_visibilityStore == null) return null;
            var slots = _visibilityStore.Slots;
            return slots.Count == 1 ? slots[0].ToTriggerTool() : null;
        }
    }

    /// The black center region between the logo tabs. The 200 gap is a notch
    /// lookalike and only makes sense when the bar hugs the top edge like a
    /// Mac menu bar; a floating island has no camera housing to mimic, so it
    /// tightens to a compact spacer.
    public double NotchWidth =>
        (_positionStore?.Placement == IslandPlacement.Floating)
            ? 64
            : (_spacingMode == IslandSpacingMode.NotchStyle ? 200 : 100);

    public Size Size => _state switch
    {
        // "Always show usage" keeps the compact bar at peek width so the
        // percentages have their outboard slots even without a hover.
        IslandState.Compact when (_alwaysShowUsageStore?.Enabled ?? false) =>
            new Size(NotchWidth + (TabWidth + PillSlotWidth) * 2, SilhouetteHeight),
        IslandState.Compact => new Size(NotchWidth + TabWidth * 2, SilhouetteHeight),
        IslandState.Peek => new Size(NotchWidth + (TabWidth + PillSlotWidth) * 2, SilhouetteHeight),
        IslandState.Expanded => new Size(ExpandedWidth, SilhouetteHeight + _expandedContentHeight),
        _ => new Size(NotchWidth + TabWidth * 2, SilhouetteHeight),
    };

    /// Re-emit Size when "always show usage" flips so the compact bar
    /// widens/narrows immediately.
    public void NotifyAlwaysShowUsageChanged() => Raise(nameof(Size));

    public double CornerRadius => _state == IslandState.Expanded ? ExpandedCornerRadius : CompactCornerRadius;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
