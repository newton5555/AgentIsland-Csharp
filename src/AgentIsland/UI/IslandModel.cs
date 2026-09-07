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

    /// Standard wide notch space (200pt) used during normal/hover/active state.
    public const double StandardNotchWidth = 200;
    /// Minimal gap (16pt) used when idle-collapsed after 30s of inactivity.
    public const double CompactCenterGap = 16;

    public double NotchWidth =>
        (_positionStore?.Placement == IslandPlacement.Floating)
            ? 64
            : (_spacingMode == IslandSpacingMode.CompactStyle ? 100 : StandardNotchWidth);

    public Size Size => _state switch
    {
        // 超过 30s 没操作收缩时：以中间黑区收拢为限制，单双 Agent 宽度一致 (92px)
        IslandState.Compact => new Size(CompactCenterGap + TabWidth * 2, SilhouetteHeight),

        // 默认 / 鼠标悬停 (Peek 或正常活跃状态)：恢复原来的完整展开宽度 (200 + (38+104)*2 = 484px)
        IslandState.Peek => new Size(NotchWidth + (TabWidth + PillSlotWidth) * 2, SilhouetteHeight),

        // 点击展开 (Expanded)：原来的完整大看板宽度 (800px)
        IslandState.Expanded => new Size(ExpandedWidth, SilhouetteHeight + _expandedContentHeight),

        _ => new Size(CompactCenterGap + TabWidth * 2, SilhouetteHeight),
    };

    /// Re-emit Size when "always show usage" flips so the compact bar
    /// widens/narrows immediately.
    public void NotifyAlwaysShowUsageChanged() => Raise(nameof(Size));

    public double CornerRadius => _state == IslandState.Expanded ? ExpandedCornerRadius : CompactCornerRadius;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
