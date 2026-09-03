using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;
using AgentIsland.Core.Usage;

namespace AgentIsland.UI;

/// Fixed footer under the swipeable pages: style chip on the left, page
/// dots centered, live sync status on the right.
public sealed partial class PanelFooter : Grid
{
    private readonly DispatcherTimer _agoTimer;

    public PanelFooter()
    {
        InitializeComponent();

        WeeklyLabel.Text = L10n.Tr("Weekly");
        WeeklyPill.ToolTip = L10n.Tr("Share weekly report");
        MonthlyLabel.Text = L10n.Tr("Monthly");
        MonthlyPill.ToolTip = L10n.Tr("Share monthly report");

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

    private void Update()
    {
        // Page-specific corner chip, macOS rules: usage none (the gear owns
        // that corner), cost the cost style, overview the year, triggers AUTO.
        var pref = ScreenPref.Shared;
        ChipLabel.Text = pref.Screen switch
        {
            IslandScreen.Usage => "",
            IslandScreen.Cost => CostStylePreferenceStore.Shared.ChipLabel,
            IslandScreen.Overview => DateTime.Now.Year.ToString(),
            _ => StylePreferenceStore.Shared.Style.ToString().ToUpperInvariant(),
        };
        ChipHost.Visibility = ChipLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        DotsPanel.Children.Clear();
        foreach (var screen in pref.VisibleScreens)
        {
            var isActive = screen == pref.Screen;
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Margin = new Thickness(2.5, 0, 2.5, 0),
                Fill = IslandColors.Brush(IslandColors.White(isActive ? 0.78 : 0.22)),
                Cursor = Cursors.Hand,
            };
            var target = screen;
            dot.MouseLeftButtonUp += (_, args) =>
            {
                pref.HasSwiped = true;
                pref.Screen = target;
                args.Handled = true;
            };
            DotsPanel.Children.Add(dot);
        }

        var store = UsageStore.Shared;
        if (store.Loading)
        {
            SyncLabel.Text = L10n.Tr("Syncing…");
        }
        else if (store.RefreshWarning is { } warning)
        {
            SyncLabel.Text = warning;
        }
        else if (store.LastUpdated is { } updated)
        {
            SyncLabel.Text = L10n.Tr("Synced") + " " +
                Core.Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese);
        }
        else
        {
            SyncLabel.Text = "";
        }
        DotIndicator.SetActive(!store.Loading && store.RefreshWarning is null && store.LastUpdated is not null);
    }

    private void OnPillMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border) border.Background = Brushes.White;
    }

    private void OnPillMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border) border.Background = IslandColors.Brush(Colors.White, 0.92);
    }

    private void OnWeeklyPillClick(object sender, MouseButtonEventArgs e)
    {
        Report.ReportWindow.Show(Report.ReportWindow.Kind.Weekly);
        e.Handled = true;
    }

    private void OnMonthlyPillClick(object sender, MouseButtonEventArgs e)
    {
        Report.ReportWindow.Show(Report.ReportWindow.Kind.Monthly);
        e.Handled = true;
    }

    private void OnSyncMouseEnter(object sender, MouseEventArgs e)
    {
        SyncButton.Background = IslandColors.Brush(IslandColors.White(0.05));
    }

    private void OnSyncMouseLeave(object sender, MouseEventArgs e)
    {
        SyncButton.Background = Brushes.Transparent;
    }

    private void OnSyncClick(object sender, MouseButtonEventArgs e)
    {
        UsageStore.Shared.Refresh();
        e.Handled = true;
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
        System.ComponentModel.PropertyChangedEventHandler onSync =
            (_, _) => Dispatcher.BeginInvoke(MaybeBump);
        AgentIsland.Backend.Usage.UsageStore.Shared.PropertyChanged += onSync;
        Unloaded += (_, _) => AgentIsland.Backend.Usage.UsageStore.Shared.PropertyChanged -= onSync;
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

    private void ApplyBreath()
    {
        var scale = (ScaleTransform)_halo.RenderTransform;
        if (_active && IsVisible)
        {
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
        var updated = AgentIsland.Backend.Usage.UsageStore.Shared.LastUpdated;
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
