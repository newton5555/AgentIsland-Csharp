using System.ComponentModel;
using System.Windows;
using AgentIsland.Core;
using AgentIsland.Model;

namespace AgentIsland.UI;

public enum IslandState
{
    Compact,
    Peek,
    Expanded,
}

/// Island bar width preset. macOS offered a wide notch-style bar and a
/// compact one; Windows has no camera notch, so the wide style (matching the
/// product's signature look) is the default and compact remains for users
/// who want a smaller pill.
public enum IslandSpacingMode
{
    NotchStyle,
    Compact,
}

/// State machine + geometry for the island silhouette. Sizes are the shipped
/// macOS constants: tab 38, peek pill slot 104 per side, expanded panel 800
/// wide with 188/244pt content pages.
public sealed class IslandModel : INotifyPropertyChanged
{
    public static IslandModel Shared { get; } = new();

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

    private IslandState _state = IslandState.Compact;
    private IslandSpacingMode _spacingMode;
    private double _expandedContentHeight = UsageContentHeight;

    private IslandModel()
    {
        // Windows displays have no notch, so the macOS Compact/Notched-Mac
        // bar-style choice is gone: the bar is always the wide layout
        // (any previously persisted choice is ignored).
        _spacingMode = IslandSpacingMode.NotchStyle;
        // The center gap depends on placement (see NotchWidth); re-emit Size
        // so the silhouette re-measures the moment the mode flips.
        Model.IslandPositionStore.Shared.PropertyChanged += (_, _) => Raise(nameof(Size));
        // A provider flip can change the solo split, so the bar reflows live.
        Model.ProviderVisibilityStore.Shared.PropertyChanged += (_, _) => Raise(nameof(Size));
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
        }
    }

    public IslandSpacingMode SpacingMode
    {
        get => _spacingMode;
        set
        {
            if (_spacingMode == value) return;
            _spacingMode = value;
            Core.Preferences.Set("AgentIsland.spacingMode", value.ToString());
            Raise(nameof(SpacingMode));
            Raise(nameof(Size));
        }
    }

    /// Height of the expanded content area below the silhouette strip;
    /// depends on the visible page (usage/cost 188, overview 244 + detail).
    public double ExpandedContentHeight
    {
        get => _expandedContentHeight;
        set
        {
            if (Math.Abs(_expandedContentHeight - value) < 0.5) return;
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
            var slots = Model.ProviderVisibilityStore.Shared.Slots;
            return slots.Count == 1 ? slots[0].ToTriggerTool() : null;
        }
    }

    /// The black center region between the logo tabs. The 200 gap is a notch
    /// lookalike and only makes sense when the bar hugs the top edge like a
    /// Mac menu bar; a floating island has no camera housing to mimic, so it
    /// tightens to a compact spacer.
    public double NotchWidth =>
        Model.IslandPositionStore.Shared.Placement == Model.IslandPlacement.Floating
            ? 64
            : (_spacingMode == IslandSpacingMode.NotchStyle ? 200 : 100);

    public Size Size => _state switch
    {
        // "Always show usage" keeps the compact bar at peek width so the
        // percentages have their outboard slots even without a hover.
        IslandState.Compact when AlwaysShowUsageStore.Shared.Enabled =>
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
