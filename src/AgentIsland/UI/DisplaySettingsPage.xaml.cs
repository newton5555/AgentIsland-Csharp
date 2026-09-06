using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentIsland.Backend.Settings;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

public sealed partial class DisplaySettingsPage : UserControl
{
    public static string UsageDisplayLabel => L10n.Tr("Usage display").ToUpperInvariant();
    public static string CostDisplayLabel => L10n.Tr("Cost display").ToUpperInvariant();
    public static string TopBarLabel => L10n.Tr("Top bar").ToUpperInvariant();
    public static string ScreenLabel => L10n.Tr("Screen").ToUpperInvariant();
    public static string PositionLabel => L10n.Tr("Position").ToUpperInvariant();

    private readonly bool _initialized;
    private readonly StylePreferenceStore _stylePrefStore;
    private readonly QuotaDisplayModeStore _quotaDisplayModeStore;
    private readonly ScreenPref _screenPref;
    private readonly CostStylePreferenceStore _costStylePrefStore;
    private readonly LowPowerModeStore _lowPowerModeStore;
    private readonly IslandScaleStore _scaleStore;
    private readonly AlwaysShowUsageStore _alwaysShowUsageStore;
    private readonly IslandTargetDisplayStore _targetDisplayStore;
    private readonly IslandPositionStore _positionStore;
    private readonly GlowColorStore _glowColorStore;

    public DisplaySettingsPage() : this(null) { }

    public DisplaySettingsPage(
        StylePreferenceStore? stylePrefStore = null,
        QuotaDisplayModeStore? quotaDisplayModeStore = null,
        ScreenPref? screenPref = null,
        CostStylePreferenceStore? costStylePrefStore = null,
        LowPowerModeStore? lowPowerModeStore = null,
        IslandScaleStore? scaleStore = null,
        AlwaysShowUsageStore? alwaysShowUsageStore = null,
        IslandTargetDisplayStore? targetDisplayStore = null,
        IslandPositionStore? positionStore = null,
        GlowColorStore? glowColorStore = null)
    {
        var sp = App.Instance?.Services;
        _stylePrefStore = stylePrefStore ?? (sp?.GetService(typeof(StylePreferenceStore)) as StylePreferenceStore) ?? new StylePreferenceStore();
        _quotaDisplayModeStore = quotaDisplayModeStore ?? (sp?.GetService(typeof(QuotaDisplayModeStore)) as QuotaDisplayModeStore) ?? new QuotaDisplayModeStore();
        _screenPref = screenPref ?? (sp?.GetService(typeof(ScreenPref)) as ScreenPref) ?? new ScreenPref();
        _costStylePrefStore = costStylePrefStore ?? (sp?.GetService(typeof(CostStylePreferenceStore)) as CostStylePreferenceStore) ?? new CostStylePreferenceStore();
        _lowPowerModeStore = lowPowerModeStore ?? (sp?.GetService(typeof(LowPowerModeStore)) as LowPowerModeStore) ?? new LowPowerModeStore();
        _scaleStore = scaleStore ?? (sp?.GetService(typeof(IslandScaleStore)) as IslandScaleStore) ?? new IslandScaleStore();
        _alwaysShowUsageStore = alwaysShowUsageStore ?? (sp?.GetService(typeof(AlwaysShowUsageStore)) as AlwaysShowUsageStore) ?? new AlwaysShowUsageStore();
        _targetDisplayStore = targetDisplayStore ?? (sp?.GetService(typeof(IslandTargetDisplayStore)) as IslandTargetDisplayStore) ?? new IslandTargetDisplayStore();
        _positionStore = positionStore ?? (sp?.GetService(typeof(IslandPositionStore)) as IslandPositionStore) ?? new IslandPositionStore();
        _glowColorStore = glowColorStore ?? (sp?.GetService(typeof(GlowColorStore)) as GlowColorStore) ?? new GlowColorStore();

        InitializeComponent();

        // 1. Usage display
        StylePicker.Select(_stylePrefStore.Style);
        StylePicker.StyleSelected += style =>
        {
            if (!_initialized) return;
            _stylePrefStore.Style = style;
        };

        // 2. Quota shows
        var quotaMode = _quotaDisplayModeStore;
        QuotaSeg.SetLabels(
            new[] { L10n.Tr("Used"), L10n.Tr("Remaining") },
            quotaMode.ShowsRemaining ? 1 : 0);
        QuotaSeg.SelectionChanged += index =>
        {
            if (!_initialized) return;
            quotaMode.ShowsRemaining = index == 1;
        };

        // 3. Cost display
        void RefreshCostPicker()
        {
            if (_screenPref.ShowCostPage)
            {
                var picker = new CostStylePickerControl(_costStylePrefStore.Style);
                picker.StyleSelected += style => _costStylePrefStore.Style = style;
                CostPickerHost.Content = picker;
            }
            else
            {
                CostPickerHost.Content = null;
            }
        }

        CostToggle.IsOn = _screenPref.ShowCostPage;
        RefreshCostPicker();
        CostToggle.Toggled += enabled =>
        {
            if (!_initialized) return;
            _screenPref.ShowCostPage = enabled;
            RefreshCostPicker();
        };

        // 4. Top bar: Visual mode
        VisualModeCombo.Items.Add(L10n.Tr("Calm"));
        VisualModeCombo.Items.Add(L10n.Tr("Vivid"));
        VisualModeCombo.Items.Add(L10n.Tr("Follow model"));
        VisualModeCombo.SelectedIndex = _lowPowerModeStore.Mode switch
        {
            VisualMode.Calm => 0,
            VisualMode.Vivid => 1,
            VisualMode.FollowModel => 2,
            _ => 2,
        };

        GlowColorRow.Trailing = GlowSwatches();
        GlowColorRow.Visibility = _lowPowerModeStore.Mode == VisualMode.Vivid
            ? Visibility.Visible
            : Visibility.Collapsed;

        VisualModeCombo.SelectionChanged += (_, _) =>
        {
            if (!_initialized) return;
            var selectedMode = VisualModeCombo.SelectedIndex switch
            {
                0 => VisualMode.Calm,
                1 => VisualMode.Vivid,
                2 => VisualMode.FollowModel,
                _ => VisualMode.FollowModel,
            };
            _lowPowerModeStore.Mode = selectedMode;
            GlowColorRow.Visibility = selectedMode == VisualMode.Vivid
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        // Interface scale
        var scaleSteps = new[] { 1.0, 1.15, 1.3, 1.5 };
        foreach (var step in scaleSteps) ScaleBox.Items.Add($"{Math.Round(step * 100)}%");
        var currentScale = _scaleStore.Scale;
        var scaleIndex = Array.FindIndex(scaleSteps, s => Math.Abs(s - currentScale) < 0.01);
        ScaleBox.SelectedIndex = scaleIndex < 0 ? 0 : scaleIndex;
        ScaleBox.SelectionChanged += (_, _) =>
        {
            if (!_initialized) return;
            if (ScaleBox.SelectedIndex >= 0)
            {
                _scaleStore.Scale = scaleSteps[ScaleBox.SelectedIndex];
            }
        };

        // Always show usage
        AlwaysShowToggle.IsOn = _alwaysShowUsageStore.Enabled;
        AlwaysShowToggle.Toggled += enabled =>
        {
            if (!_initialized) return;
            _alwaysShowUsageStore.Enabled = enabled;
        };

        // 5. Screen
        var screens = System.Windows.Forms.Screen.AllScreens;
        DisplayCombo.Items.Add(L10n.Tr("Auto"));
        foreach (var screen in screens)
        {
            DisplayCombo.Items.Add(screen.DeviceName.TrimStart('\\', '.') + (screen.Primary ? " ★" : ""));
        }
        var choice = _targetDisplayStore.Choice;
        DisplayCombo.SelectedIndex = choice == "auto"
            ? 0
            : Math.Max(0, Array.FindIndex(screens, s => s.DeviceName == choice) + 1);

        UpdateShowOnSubtitle(choice);

        DisplayCombo.SelectionChanged += (_, _) =>
        {
            if (!_initialized) return;
            var selected = DisplayCombo.SelectedIndex <= 0
                ? "auto"
                : screens[DisplayCombo.SelectedIndex - 1].DeviceName;
            _targetDisplayStore.Choice = selected;
            UpdateShowOnSubtitle(selected);
        };

        // 6. Position
        var position = _positionStore;
        var placements = new[] { IslandPlacement.TopBar, IslandPlacement.Floating };
        foreach (var mode in placements) PlacementBox.Items.Add(PlacementLabel(mode));
        PlacementBox.SelectedIndex = Math.Max(0, Array.IndexOf(placements, position.Placement));
        PlacementBox.SelectionChanged += (_, _) =>
        {
            if (!_initialized) return;
            if (PlacementBox.SelectedIndex >= 0)
            {
                position.Placement = placements[PlacementBox.SelectedIndex];
            }
        };

        _initialized = true;
    }

    private void UpdateShowOnSubtitle(string choice)
    {
        ShowOnRow.Subtitle = choice == "auto"
            ? L10n.Tr("Auto — picks the best available screen.")
            : L10n.Tr("Pinned to a specific display. Falls back to Auto if unplugged.");
    }

    private static string PlacementLabel(IslandPlacement mode) => mode switch
    {
        IslandPlacement.TopBar => L10n.Tr("Top bar"),
        IslandPlacement.Floating => L10n.Tr("Floating window"),
        _ => mode.ToString(),
    };

    private UIElement GlowSwatches()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dots = new List<(GlowColorStore.Choice Choice, Ellipse Dot)>();
        void Restyle()
        {
            foreach (var (choice, dot) in dots)
            {
                var selected = _glowColorStore.Value == choice;
                dot.Stroke = IslandColors.Brush(IslandColors.White(selected ? 0.92 : 0.16));
                dot.StrokeThickness = selected ? 1.5 : 0.5;
                dot.Effect = selected
                    ? new System.Windows.Media.Effects.DropShadowEffect
                    {
                        ShadowDepth = 0,
                        BlurRadius = 8,
                        Color = GlowColorStore.ColorOf(choice),
                        Opacity = 0.55,
                    }
                    : null;
            }
        }
        foreach (var choice in GlowColorStore.All)
        {
            var dot = new Ellipse
            {
                Width = 13,
                Height = 13,
                Fill = IslandColors.Brush(GlowColorStore.ColorOf(choice)),
            };
            var puck = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(3),
                Margin = new Thickness(0, 0, 7, 0),
                Child = dot,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = L10n.Tr(GlowColorStore.LabelKey(choice)),
            };
            var captured = choice;
            puck.MouseLeftButtonDown += (_, e) =>
            {
                _glowColorStore.Value = captured;
                Restyle();
                e.Handled = true;
            };
            dots.Add((choice, dot));
            row.Children.Add(puck);
        }
        Restyle();
        return row;
    }
}
