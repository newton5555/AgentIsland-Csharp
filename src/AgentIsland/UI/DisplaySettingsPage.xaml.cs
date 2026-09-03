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

    public DisplaySettingsPage()
    {
        InitializeComponent();

        // 1. Usage display
        StylePicker.Select(StylePreferenceStore.Shared.Style);
        StylePicker.StyleSelected += style =>
        {
            if (!_initialized) return;
            StylePreferenceStore.Shared.Style = style;
        };

        // 2. Quota shows
        var quotaMode = QuotaDisplayModeStore.Shared;
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
            if (ScreenPref.Shared.ShowCostPage)
            {
                var picker = new CostStylePickerControl(CostStylePreferenceStore.Shared.Style);
                picker.StyleSelected += style => CostStylePreferenceStore.Shared.Style = style;
                CostPickerHost.Content = picker;
            }
            else
            {
                CostPickerHost.Content = null;
            }
        }

        CostToggle.IsOn = ScreenPref.Shared.ShowCostPage;
        RefreshCostPicker();
        CostToggle.Toggled += enabled =>
        {
            if (!_initialized) return;
            ScreenPref.Shared.ShowCostPage = enabled;
            RefreshCostPicker();
        };

        // 4. Top bar: Visual mode
        VisualModeCombo.Items.Add(L10n.Tr("Calm"));
        VisualModeCombo.Items.Add(L10n.Tr("Vivid"));
        VisualModeCombo.Items.Add(L10n.Tr("Follow model"));
        VisualModeCombo.SelectedIndex = LowPowerModeStore.Shared.Mode switch
        {
            VisualMode.Calm => 0,
            VisualMode.Vivid => 1,
            VisualMode.FollowModel => 2,
            _ => 1,
        };

        GlowColorRow.Trailing = GlowSwatches();
        GlowColorRow.Visibility = LowPowerModeStore.Shared.Mode == VisualMode.Vivid
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
                _ => VisualMode.Vivid,
            };
            LowPowerModeStore.Shared.Mode = selectedMode;
            GlowColorRow.Visibility = selectedMode == VisualMode.Vivid
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        // Interface scale
        var scaleSteps = new[] { 1.0, 1.15, 1.3, 1.5 };
        foreach (var step in scaleSteps) ScaleBox.Items.Add($"{Math.Round(step * 100)}%");
        var currentScale = IslandScaleStore.Shared.Scale;
        var scaleIndex = Array.FindIndex(scaleSteps, s => Math.Abs(s - currentScale) < 0.01);
        ScaleBox.SelectedIndex = scaleIndex < 0 ? 0 : scaleIndex;
        ScaleBox.SelectionChanged += (_, _) =>
        {
            if (!_initialized) return;
            if (ScaleBox.SelectedIndex >= 0)
            {
                IslandScaleStore.Shared.Scale = scaleSteps[ScaleBox.SelectedIndex];
            }
        };

        // Always show usage
        AlwaysShowToggle.IsOn = AlwaysShowUsageStore.Shared.Enabled;
        AlwaysShowToggle.Toggled += enabled =>
        {
            if (!_initialized) return;
            AlwaysShowUsageStore.Shared.Enabled = enabled;
        };

        // 5. Screen
        var screens = System.Windows.Forms.Screen.AllScreens;
        DisplayCombo.Items.Add(L10n.Tr("Auto"));
        foreach (var screen in screens)
        {
            DisplayCombo.Items.Add(screen.DeviceName.TrimStart('\\', '.') + (screen.Primary ? " ★" : ""));
        }
        var choice = IslandTargetDisplayStore.Shared.Choice;
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
            IslandTargetDisplayStore.Shared.Choice = selected;
            UpdateShowOnSubtitle(selected);
        };

        // 6. Position
        var position = IslandPositionStore.Shared;
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

    private static UIElement GlowSwatches()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dots = new List<(GlowColorStore.Choice Choice, Ellipse Dot)>();
        void Restyle()
        {
            foreach (var (choice, dot) in dots)
            {
                var selected = GlowColorStore.Shared.Value == choice;
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
                GlowColorStore.Shared.Value = captured;
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
