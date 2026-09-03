using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentIsland.Localization;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;
using AgentIsland.Usage;

namespace AgentIsland.UI;

/// Fixed footer under the swipeable pages: style chip on the left, page
/// dots centered, live sync status on the right.
public sealed class PanelFooter : Grid
{
    private readonly TextBlock _chip;
    private readonly Border _chipHost;
    private readonly StackPanel _dots;
    private readonly LiveDot _liveDot = new();
    private readonly TextBlock _syncLabel;
    private readonly DispatcherTimer _agoTimer;

    public PanelFooter()
    {
        Height = 40;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var hairline = new Border
        {
            Height = 1,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(IslandColors.White(0.06), 0.5),
                    new GradientStop(Colors.Transparent, 1),
                },
                new Point(0, 0),
                new Point(1, 0)),
            Margin = new Thickness(22, 0, 22, 0),
        };
        SetRow(hairline, 0);
        Children.Add(hairline);

        var row = new Grid { Margin = new Thickness(22, 6, 22, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetRow(row, 1);
        Children.Add(row);

        // Rounded mono chip, the macOS Typography.chip pill.
        _chip = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.78)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _chipHost = new Border
        {
            Child = _chip,
            CornerRadius = new CornerRadius(4),
            Background = IslandColors.Brush(IslandColors.White(0.08)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.10)),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(5, 2, 5, 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            // Clear the settings gear that sits in the panel's corner.
            Margin = new Thickness(18, 0, 0, 0),
        };
        SetColumn(_chipHost, 0);
        row.Children.Add(_chipHost);

        _dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetColumn(_dots, 1);
        row.Children.Add(_dots);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _syncLabel = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _liveDot.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(_liveDot);
        right.Children.Add(_syncLabel);
        // The macOS footer status is a quiet refresh button: hover wash,
        // click re-syncs immediately.
        var syncButton = new Border
        {
            Child = right,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Padding = new Thickness(7, 3, 7, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        syncButton.MouseEnter += (_, _) =>
            syncButton.Background = IslandColors.Brush(IslandColors.White(0.05));
        syncButton.MouseLeave += (_, _) => syncButton.Background = Brushes.Transparent;
        syncButton.MouseLeftButtonUp += (_, args) =>
        {
            UsageStore.Shared.Refresh();
            args.Handled = true;
        };

        // Report entries — labeled, because a bare glyph is invisible and
        // nobody shares what they can't find. Weekly + monthly, same bright
        // pill, every page (the panel's call-to-action).
        var rightCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rightCluster.Children.Add(ReportPill(
            Localization.L10n.Tr("Weekly"),
            Localization.L10n.Tr("Share weekly report"),
            () => Report.ReportWindow.Show(Report.ReportWindow.Kind.Weekly)));
        rightCluster.Children.Add(ReportPill(
            Localization.L10n.Tr("Monthly"),
            Localization.L10n.Tr("Share monthly report"),
            () => Report.ReportWindow.Show(Report.ReportWindow.Kind.Monthly)));
        rightCluster.Children.Add(syncButton);
        SetColumn(rightCluster, 2);
        row.Children.Add(rightCluster);

        // A single named handler so every subscription and the timer tear
        // down on Unloaded — a rebuilt island (e.g. language switch) would
        // otherwise leave the old footer's 30s timer waking the UI thread and
        // its store subscriptions pinning the dead instance alive forever.
        System.ComponentModel.PropertyChangedEventHandler onChanged =
            (_, _) => Dispatcher.BeginInvoke(Update);
        ScreenPref.Shared.PropertyChanged += onChanged;
        StylePreferenceStore.Shared.PropertyChanged += onChanged;
        UsageStore.Shared.PropertyChanged += onChanged;

        // Keep the "2m ago" caption honest while the panel sits open.
        _agoTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _agoTimer.Tick += (_, _) => Update();
        _agoTimer.Start();

        // The footer is discarded (not reparented) when the island rebuilds,
        // so a one-way teardown is correct.
        Unloaded += (_, _) =>
        {
            _agoTimer.Stop();
            ScreenPref.Shared.PropertyChanged -= onChanged;
            StylePreferenceStore.Shared.PropertyChanged -= onChanged;
            UsageStore.Shared.PropertyChanged -= onChanged;
        };

        Update();
    }

    /// Bright white pill with the share glyph — the report entry.
    private static UIElement ReportPill(string label, string help, Action open)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = "", // Segoe Fluent share glyph
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 9,
            Foreground = Brushes.Black,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = IslandFonts.Ui,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var pill = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(9),
            Background = IslandColors.Brush(Colors.White, 0.92),
            Padding = new Thickness(8, 2.5, 8, 2.5),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = help,
        };
        pill.MouseEnter += (_, _) => pill.Background = Brushes.White;
        pill.MouseLeave += (_, _) => pill.Background = IslandColors.Brush(Colors.White, 0.92);
        pill.MouseLeftButtonUp += (_, args) =>
        {
            open();
            args.Handled = true;
        };
        return pill;
    }

    private void Update()
    {
        // Page-specific corner chip, macOS rules: usage none (the gear owns
        // that corner), cost the cost style, overview the year, triggers AUTO.
        var pref = ScreenPref.Shared;
        _chip.Text = pref.Screen switch
        {
            IslandScreen.Usage => "",
            IslandScreen.Cost => CostStylePreferenceStore.Shared.ChipLabel,
            IslandScreen.Overview => DateTime.Now.Year.ToString(),
            _ => StylePreferenceStore.Shared.Style.ToString().ToUpperInvariant(),
        };
        _chipHost.Visibility = _chip.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        _dots.Children.Clear();
        foreach (var screen in pref.VisibleScreens)
        {
            var isActive = screen == pref.Screen;
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Margin = new Thickness(2.5, 0, 2.5, 0),
                Fill = IslandColors.Brush(IslandColors.White(isActive ? 0.78 : 0.22)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var target = screen;
            dot.MouseLeftButtonUp += (_, args) =>
            {
                pref.HasSwiped = true;
                pref.Screen = target;
                args.Handled = true;
            };
            _dots.Children.Add(dot);
        }

        var store = UsageStore.Shared;
        if (store.Loading)
        {
            _syncLabel.Text = L10n.Tr("Syncing…");
        }
        else if (store.RefreshWarning is { } warning)
        {
            _syncLabel.Text = warning;
        }
        else if (store.LastUpdated is { } updated)
        {
            _syncLabel.Text = L10n.Tr("Synced") + " " +
                Core.Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese);
        }
        else
        {
            _syncLabel.Text = "";
        }
        _liveDot.SetActive(!store.Loading && store.RefreshWarning is null && store.LastUpdated is not null);
    }
}

/// Breathing live-status dot: teal with a pulsing outer halo when healthy,
/// dim white otherwise. Bumps briefly when a fresh sync lands.
public sealed class LiveDot : Grid
{
    private readonly Ellipse _core;
    private readonly Ellipse _halo;
    private readonly ScaleTransform _bump = new(1, 1);
    private bool _active;
    private DateTimeOffset? _seenUpdate;

    public LiveDot()
    {
        Width = 10;
        Height = 10;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _bump;
        _halo = new Ellipse
        {
            Width = 6,
            Height = 6,
            Stroke = IslandColors.Brush(IslandColors.LiveTeal),
            StrokeThickness = 1,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Opacity = 0,
        };
        _core = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = IslandColors.Brush(IslandColors.LiveTeal, 0.9),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 3,
                Color = IslandColors.LiveTeal,
                Opacity = 0.55,
            },
        };
        Children.Add(_halo);
        Children.Add(_core);
        // Detach on Unloaded — otherwise this dot stays pinned by UsageStore
        // and defeats PanelFooter's own teardown, keeping the dead footer alive.
        System.ComponentModel.PropertyChangedEventHandler onSync =
            (_, _) => Dispatcher.BeginInvoke(MaybeBump);
        Usage.UsageStore.Shared.PropertyChanged += onSync;
        Unloaded += (_, _) => Usage.UsageStore.Shared.PropertyChanged -= onSync;
        // Pause/resume the breath as the dot enters/leaves the visual tree —
        // a compact island collapses the footer, and a forever animation on
        // the hidden dot would keep repainting the whole transparent window.
        IsVisibleChanged += (_, _) => ApplyBreath();
        SetActive(false);
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        _core.Fill = active
            ? IslandColors.Brush(IslandColors.LiveTeal, 0.9)
            : IslandColors.Brush(IslandColors.White(0.25));
        ApplyBreath();
    }

    /// The ~2.4s breath runs only while the dot is BOTH active AND actually
    /// visible. On the collapsed compact footer a forever animation would
    /// otherwise repaint the transparent window every frame for a dot nobody
    /// can see — the dominant idle-CPU cost on Windows (a layered window
    /// re-composites in full on each animation tick, however small the tick).
    private void ApplyBreath()
    {
        var scale = (ScaleTransform)_halo.RenderTransform;
        if (_active && IsVisible)
        {
            // halo swells 1 -> 1.6 while fading out.
            var grow = new DoubleAnimation(1.0, 1.6, new Duration(TimeSpan.FromSeconds(1.2)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
            var fade = new DoubleAnimation(0.55, 0.0, new Duration(TimeSpan.FromSeconds(1.2)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _halo.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _halo.BeginAnimation(OpacityProperty, null);
            _halo.Opacity = 0;
        }
    }

    private void MaybeBump()
    {
        var updated = Usage.UsageStore.Shared.LastUpdated;
        if (updated == _seenUpdate) return;
        _seenUpdate = updated;
        var up = new DoubleAnimation(1.18, IslandAnimations.StrongEaseOutDuration)
        {
            EasingFunction = IslandAnimations.StrongEaseOut(),
        };
        up.Completed += (_, _) =>
        {
            var down = new DoubleAnimation(1.0, IslandAnimations.StrongEaseOutDuration)
            {
                EasingFunction = IslandAnimations.StrongEaseOut(),
            };
            _bump.BeginAnimation(ScaleTransform.ScaleXProperty, down);
            _bump.BeginAnimation(ScaleTransform.ScaleYProperty, down.Clone());
        };
        _bump.BeginAnimation(ScaleTransform.ScaleXProperty, up);
        _bump.BeginAnimation(ScaleTransform.ScaleYProperty, up.Clone());
    }
}
