using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI.Report;

/// Hosts a share card (weekly or monthly) in a borderless window: the card
/// paints its own shadow, Esc closes, drag anywhere moves. A ← period →
/// pager plus an any-date calendar anchor ride above the card (macOS
/// report sheets); Copy image and Save PNG below, both served from a warm
/// 3x render of exactly the page on screen. Sharing is always the USER
/// posting an image; nothing leaves the machine on its own.
public sealed partial class ReportWindow : Window
{
    public enum Kind
    {
        Weekly,
        Monthly,
    }

    private static ReportWindow? _current;

    private Kind _kind;
    private readonly PagerCircle _back;
    private readonly PagerCircle _forward;
    private readonly PagerCircle _calendarButton;
    private readonly DispatcherTimer _pagerHideTimer;
    private ReportCalendarPopup? _calendar;
    private object _display;
    private int _pageOffset;
    private DateTime? _anchorDate;
    private bool _loading;
    private BitmapSource? _rendered;
    private CancellationTokenSource? _queryCts;
    private long _querySequence;
    private bool _selectionRefreshQueued;
    private DispatcherTimer? _coachTimer;
    private readonly System.ComponentModel.PropertyChangedEventHandler _costChanged;
    private readonly System.ComponentModel.PropertyChangedEventHandler _providerSelectionChanged;

    public static void Show(Kind kind)
    {
        if (_current != null && _current.IsLoaded)
        {
            _current.SwitchKind(kind);
            WindowActivation.BringToFront(_current);
            return;
        }
        var window = new ReportWindow(kind);
        _current = window;
        window.Closed += (_, _) =>
        {
            if (_current == window) _current = null;
        };
        window.Show();
        WindowActivation.BringToFront(window);
    }

    private ReportWindow(Kind kind)
    {
        InitializeComponent();
        _kind = kind;
        _display = CurrentData();

        Title = kind == Kind.Weekly
            ? AgentIsland.UI.Localization.L10n.Tr("Weekly report")
            : AgentIsland.UI.Localization.L10n.Tr("Share monthly report");

        var zh = AgentIsland.UI.Localization.L10n.IsChinese;

        _back = new PagerCircle("\uE76B", zh ? "上一周期 (←)" : "Previous period (←)");
        _back.Clicked += OnBackClicked;
        BackSlot.Child = _back;

        _forward = new PagerCircle("", zh ? "下一周期 (→)" : "Next period (→)");
        _forward.Clicked += OnForwardClicked;
        ForwardSlot.Child = _forward;

        _calendarButton = new PagerCircle("", zh ? "选择指定日期 (日历)" : "Select date...");
        _calendarButton.Clicked += OpenCalendar;
        CalendarSlot.Child = _calendarButton;

        CopyText.Text = AgentIsland.UI.Localization.L10n.Tr("Copy image");
        SaveText.Text = AgentIsland.UI.Localization.L10n.Tr("Save PNG");

        _pagerHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _pagerHideTimer.Tick += (_, _) =>
        {
            _pagerHideTimer.Stop();
            if (_calendar is { IsOpen: true }) return;
            if (!CardHeaderHotspot.IsMouseOver && !PagerRow.IsMouseOver)
            {
                SetPagerVisible(false);
            }
        };
        SetPagerVisible(false, animate: false);

        RebuildCard();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Left && _back.Enabled) OnBackClicked();
            else if (e.Key == Key.Right && _forward.Enabled) OnForwardClicked();
        };
        MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { }
        };

        _costChanged = (_, args) =>
        {
            if (args.PropertyName != nameof(AgentIsland.Backend.Cost.CostStore.LastUpdated)) return;
            if (_pageOffset != 0 || _anchorDate is not null || _loading) return;
            _display = CurrentData();
            RebuildCard();
            AlignCurrentWeek();
        };
        _providerSelectionChanged = (_, args) =>
        {
            // ProviderVisibilityStore raises a batch of compatibility
            // notifications for one toggle. Rebuild once from the Enabled
            // change; the overview grid listens to the same source.
            if (args.PropertyName is not (nameof(AgentIsland.Backend.Settings.ProviderVisibilityStore.Enabled)
                or nameof(AgentIsland.Backend.Settings.ProviderVisibilityStore.SlotProviders))) return;
            if (_selectionRefreshQueued) return;
            _selectionRefreshQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _selectionRefreshQueued = false;
                RefreshForProviderSelection();
            }));
        };
        AgentIsland.Backend.Cost.CostStore.Shared.PropertyChanged += _costChanged;
        AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.PropertyChanged += _providerSelectionChanged;
        Closed += (_, _) =>
        {
            CancelReportQuery();
            _pagerHideTimer.Stop();
            _coachTimer?.Stop();
            AgentIsland.Backend.Cost.CostStore.Shared.PropertyChanged -= _costChanged;
            AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.PropertyChanged -= _providerSelectionChanged;
        };
        if (!Core.AppEnvironment.IsDemo) AgentIsland.Backend.Cost.CostStore.Shared.Refresh();
        AlignCurrentWeek();

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = ExportRender());
    }

    private object CurrentData() => _kind == Kind.Weekly
        ? WeeklyReportData.Current()
        : MonthlyReportData.Current();

    private FrameworkElement CardFor(object data, bool rounded) => _kind == Kind.Weekly
        ? ReportCards.Weekly((WeeklyReportData)data, rounded)
        : ReportCards.Monthly((MonthlyReportData)data, rounded);

    private string PeriodText => _kind == Kind.Weekly
        ? ((WeeklyReportData)_display).RangeText
        : ((MonthlyReportData)_display).MonthText;

    public void SwitchKind(Kind target)
    {
        if (_kind == target) return;
        CancelReportQuery();
        _kind = target;
        _pageOffset = 0;
        _anchorDate = null;
        _loading = false;
        Title = _kind == Kind.Weekly
            ? AgentIsland.UI.Localization.L10n.Tr("Weekly report")
            : AgentIsland.UI.Localization.L10n.Tr("Share monthly report");
        _display = CurrentData();
        RebuildCard();
        if (_kind == Kind.Weekly)
        {
            AlignCurrentWeek();
        }
    }

    private void RebuildCard()
    {
        _rendered = null;
        var card = CardFor(_display, rounded: true);
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            ShadowDepth = 2,
            Direction = 270,
            BlurRadius = 22,
            Color = Colors.Black,
            Opacity = 0.22,
        };
        CardHost.Children.Clear();
        CardHost.Children.Add(card);
        CardHost.Children.Add(CardHeaderHotspot);
        CardHost.Children.Add(CloseDisc());
        UpdatePagerChrome();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = ExportRender());
    }

    private void UpdatePagerChrome()
    {
        PeriodLabel.Text = PeriodText;
        PeriodLabel.Opacity = _loading ? 0.45 : 1;

        bool canGoBack;
        bool canGoForward;
        if (_anchorDate is { } anchor)
        {
            var earliest = ReportPeriods.EarliestDataDay();
            canGoBack = !_loading && anchor > earliest;
            canGoForward = !_loading;
        }
        else
        {
            var interval = _kind == Kind.Weekly
                ? ReportPeriods.WeekInterval(_pageOffset)
                : ReportPeriods.MonthInterval(_pageOffset);
            canGoBack = !_loading && ReportPeriods.HasData(interval.Start, ReportPeriods.EarliestDataDay());
            canGoForward = _pageOffset > 0 && !_loading;
        }

        _back.Enabled = canGoBack;
        _forward.Enabled = canGoForward;
        _calendarButton.Enabled = !_loading && !Core.AppEnvironment.IsDemo;
        _calendarButton.Tint = _anchorDate is null ? IslandColors.White(0.90) : Color.FromRgb(0x3D, 0xD6, 0x8C);

        var isHistorical = _pageOffset > 0 || _anchorDate is not null;
        PeriodBadge.ToolTip = isHistorical
            ? (AgentIsland.UI.Localization.L10n.IsChinese ? "点击回到最新周期" : "Click to return to current period")
            : null;
        PeriodBadge.Cursor = isHistorical ? Cursors.Hand : Cursors.Arrow;

        ActionsPanel.IsEnabled = !_loading;
        ActionsPanel.Opacity = _loading ? 0.5 : 1;
    }

    private void OnBackClicked()
    {
        if (_anchorDate is { } anchor)
        {
            var prev = _kind == Kind.Weekly ? anchor.AddDays(-7) : anchor.AddMonths(-1);
            if (prev >= ReportPeriods.EarliestDataDay())
            {
                SetAnchor(prev);
            }
        }
        else
        {
            Flip(_pageOffset + 1);
        }
    }

    private void OnForwardClicked()
    {
        if (_anchorDate is { } anchor)
        {
            var next = _kind == Kind.Weekly ? anchor.AddDays(7) : anchor.AddMonths(1);
            if (next >= DateTime.Today || (_kind == Kind.Weekly && next.AddDays(7) > DateTime.Today))
            {
                Flip(0);
            }
            else
            {
                SetAnchor(next);
            }
        }
        else
        {
            if (_pageOffset > 0)
            {
                Flip(_pageOffset - 1);
            }
        }
    }

    private void Flip(int target)
    {
        CancelReportQuery();
        _anchorDate = null;
        if (target < 0) return;
        _pageOffset = target;
        if (target == 0)
        {
            _loading = false;
            _display = CurrentData();
            RebuildCard();
            AlignCurrentWeek();
            return;
        }
        LoadPage(target);
    }

    private async void AlignCurrentWeek()
    {
        if (_kind != Kind.Weekly || Core.AppEnvironment.IsDemo) return;
        var (start, end) = ReportPeriods.WeekInterval(0);
        if (end > DateTime.Today) return;
        var queryId = BeginReportQuery(out var cts);
        try
        {
            var slices = await ReportPeriods.SlicesAsync(start, end, cts.Token);
            if (queryId != _querySequence || !IsLoaded
                || _pageOffset != 0 || _anchorDate is not null || _loading) return;
            _display = WeeklyReportData.ForInterval(start, end, slices);
            RebuildCard();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndReportQuery(queryId, cts);
        }
    }

    private void RefreshForProviderSelection()
    {
        if (!IsLoaded) return;
        CancelReportQuery();
        _loading = false;

        // Historical pages were assembled with the previous target set, so
        // reload the same page. The current page can rebuild immediately from
        // CostStore and then follow the normal completed-week alignment path.
        if (_anchorDate is not null)
        {
            SetAnchor(_anchorDate.Value);
            return;
        }
        if (_pageOffset > 0)
        {
            LoadPage(_pageOffset);
            return;
        }

        _display = CurrentData();
        RebuildCard();
        if (_kind == Kind.Weekly) AlignCurrentWeek();
    }

    private async void LoadPage(int target)
    {
        var queryId = BeginReportQuery(out var cts);
        _loading = true;
        UpdatePagerChrome();
        var (start, end) = _kind == Kind.Weekly
            ? ReportPeriods.WeekInterval(target)
            : ReportPeriods.MonthInterval(target);
        var accepted = false;
        try
        {
            var slices = await ReportPeriods.SlicesAsync(start, end, cts.Token);
            if (queryId != _querySequence || !IsLoaded
                || _pageOffset != target || _anchorDate is not null) return;
            _display = _kind == Kind.Weekly
                ? WeeklyReportData.ForInterval(start, end, slices)
                : MonthlyReportData.ForInterval(start, slices);
            accepted = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (queryId == _querySequence)
            {
                _loading = false;
                EndReportQuery(queryId, cts);
                UpdatePagerChrome();
            }
        }
        if (accepted) RebuildCard();
    }

    private long BeginReportQuery(out CancellationTokenSource cts)
    {
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        cts = new CancellationTokenSource();
        _queryCts = cts;
        return ++_querySequence;
    }

    private void EndReportQuery(long queryId, CancellationTokenSource cts)
    {
        if (queryId != _querySequence) return;
        if (ReferenceEquals(_queryCts, cts)) _queryCts = null;
        cts.Dispose();
    }

    private void CancelReportQuery()
    {
        _querySequence++;
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = null;
    }

    private void OpenCalendar()
    {
        var currentSelected = _anchorDate ?? (_kind == Kind.Weekly
            ? ReportPeriods.WeekInterval(_pageOffset).Start
            : ReportPeriods.MonthInterval(_pageOffset).Start);
        _calendar = new ReportCalendarPopup(ReportPeriods.EarliestDataDay(), SetAnchor, currentSelected)
        {
            PlacementTarget = _calendarButton,
        };
        _calendar.Closed += (_, _) => CheckHidePager();
        _calendar.IsOpen = true;
    }

    private void CheckHidePager()
    {
        _pagerHideTimer.Stop();
        _pagerHideTimer.Start();
    }

    private void SetPagerVisible(bool visible, bool animate = true)
    {
        if (!visible && _calendar is { IsOpen: true }) return;

        if (visible)
        {
            _pagerHideTimer.Stop();
        }

        PagerRow.IsHitTestVisible = visible;
        var targetOpacity = visible ? 1.0 : 0.0;
        if (!animate)
        {
            PagerRow.BeginAnimation(UIElement.OpacityProperty, null);
            PagerRow.Opacity = targetOpacity;
            return;
        }

        var duration = new Duration(TimeSpan.FromSeconds(visible ? 0.15 : 0.20));
        var anim = new DoubleAnimation(targetOpacity, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Freeze();
        PagerRow.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private async void SetAnchor(DateTime day)
    {
        CancelReportQuery();
        var start = day.Date;
        if (_kind == Kind.Weekly && start >= ReportPeriods.WeekInterval(0).Start)
        {
            Flip(0);
            return;
        }
        if (_kind == Kind.Monthly && start.Year == DateTime.Today.Year && start.Month == DateTime.Today.Month)
        {
            Flip(0);
            return;
        }

        _anchorDate = start;
        _loading = true;
        UpdatePagerChrome();
        SetPagerVisible(false);
        var end = start.AddDays(_kind == Kind.Weekly ? 7 : 30);
        var queryId = BeginReportQuery(out var cts);
        var accepted = false;
        try
        {
            var slices = await ReportPeriods.SlicesAsync(start, end, cts.Token);
            if (queryId != _querySequence || !IsLoaded || _anchorDate != start) return;
            _display = _kind == Kind.Weekly
                ? WeeklyReportData.ForInterval(start, end, slices)
                : MonthlyReportData.ForInterval(start, slices);
            accepted = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (queryId == _querySequence)
            {
                _loading = false;
                EndReportQuery(queryId, cts);
                UpdatePagerChrome();
            }
        }
        if (accepted) RebuildCard();
    }

    private UIElement CloseDisc()
    {
        var glyph = new TextBlock
        {
            Text = "", // Segoe Fluent ChromeClose
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 8.5,
            Foreground = IslandColors.Brush(IslandColors.White(0.65)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var disc = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = IslandColors.Brush(IslandColors.White(0.10)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.12)),
            BorderThickness = new Thickness(0.5),
            Child = glyph,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 12, 0),
            Cursor = Cursors.Hand,
        };
        disc.MouseEnter += (_, _) =>
        {
            SetPagerVisible(true);
            disc.Background = IslandColors.Brush(Color.FromRgb(0xC4, 0x2B, 0x1C));
            glyph.Foreground = Brushes.White;
        };
        disc.MouseLeave += (_, _) =>
        {
            disc.Background = IslandColors.Brush(IslandColors.White(0.10));
            glyph.Foreground = IslandColors.Brush(IslandColors.White(0.65));
            CheckHidePager();
        };
        disc.MouseLeftButtonDown += (_, e) => e.Handled = true;
        disc.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        return disc;
    }

    private void ShowCoach(string text)
    {
        CoachBlock.Text = text;
        CoachBlock.Opacity = 1;
        _coachTimer?.Stop();
        _coachTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _coachTimer.Tick += (_, _) =>
        {
            _coachTimer?.Stop();
            CoachBlock.Opacity = 0;
        };
        _coachTimer.Start();
    }

    private void OnPagerMouseEnter(object sender, MouseEventArgs e) => SetPagerVisible(true);
    private void OnPagerMouseLeave(object sender, MouseEventArgs e) => CheckHidePager();
    private void OnHotspotMouseEnter(object sender, MouseEventArgs e) => SetPagerVisible(true);
    private void OnHotspotMouseLeave(object sender, MouseEventArgs e) => CheckHidePager();

    private void OnPeriodBadgeMouseEnter(object sender, MouseEventArgs e)
    {
        if (_pageOffset > 0 || _anchorDate is not null)
        {
            PeriodBadge.Background = IslandColors.Brush(IslandColors.White(0.12));
        }
    }

    private void OnPeriodBadgeMouseLeave(object sender, MouseEventArgs e)
    {
        PeriodBadge.Background = Brushes.Transparent;
    }

    private void OnPeriodBadgeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_pageOffset > 0 || _anchorDate is not null)
        {
            Flip(0);
        }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (!CopyImage()) return;
        CopyText.Text = AgentIsland.UI.Localization.L10n.Tr("Copied");
        ShowCoach(AgentIsland.UI.Localization.L10n.Tr("Copied! Post it and bring a friend to the island 🏝️ Thanks for spreading the word"));
        var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        reset.Tick += (_, _) =>
        {
            reset.Stop();
            CopyText.Text = AgentIsland.UI.Localization.L10n.Tr("Copy image");
        };
        reset.Start();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        SavePng();
    }

    private BitmapSource ExportRender()
    {
        if (_rendered is not null) return _rendered;
        _rendered = Render(CardFor(_display, rounded: false));
        return _rendered;
    }

    private static BitmapSource Render(FrameworkElement card)
    {
        const double scale = 3;
        card.Measure(new Size(ReportCards.CardWidth, ReportCards.CardHeight));
        card.Arrange(new Rect(0, 0, ReportCards.CardWidth, ReportCards.CardHeight));
        card.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)(ReportCards.CardWidth * scale), (int)(ReportCards.CardHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(card);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource RenderCard(Kind kind)
    {
        var card = kind == Kind.Weekly
            ? ReportCards.Weekly(WeeklyReportData.Current(), rounded: false)
            : ReportCards.Monthly(MonthlyReportData.Current(), rounded: false);
        return Render(card);
    }

    private bool CopyImage()
    {
        try
        {
            Clipboard.SetImage(ExportRender());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SavePng()
    {
        var tag = _kind == Kind.Weekly ? "weekly" : "monthly";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"agent-island-{tag}-{DateTime.Today:yyyy-MM-dd}.png",
            Filter = "PNG|*.png",
            DefaultExt = ".png",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var stream = File.Create(dialog.FileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(ExportRender()));
            encoder.Save(stream);
            ShowCoach(AgentIsland.UI.Localization.L10n.Tr("Copied! Post it and bring a friend to the island 🏝️ Thanks for spreading the word"));
        }
        catch
        {
        }
    }

    public static void WritePng(Kind kind, string path)
    {
        try
        {
            using var stream = File.Create(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(RenderCard(kind)));
            encoder.Save(stream);
        }
        catch
        {
        }
    }

    public static void ArmWeeklyMoment()
    {
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            var now = DateTime.Now;
            var weekKey = $"{ISOWeek.GetYear(now)}-W{ISOWeek.GetWeekOfYear(now)}";
            const string shownKey = "AgentIsland.weeklyReportShownForWeek";
            if (AgentIsland.Windows.Preferences.Get<string?>(shownKey) == weekKey) return;
            AgentIsland.Windows.Preferences.Set(shownKey, weekKey);
            Show(Kind.Weekly);
        };
        delay.Start();
    }
}

/// The pager's circular icon button inside the unified floating capsule.
internal sealed class PagerCircle : Border
{
    private readonly TextBlock _glyph;
    private bool _enabled = true;
    private bool _hovered;
    private Color? _tintOverride;

    public event Action? Clicked;

    public PagerCircle(string glyph, string? toolTip = null)
    {
        Width = 28;
        Height = 28;
        CornerRadius = new CornerRadius(14);
        VerticalAlignment = VerticalAlignment.Center;
        if (!string.IsNullOrEmpty(toolTip))
        {
            ToolTip = toolTip;
        }
        _glyph = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _glyph;
        MouseLeftButtonDown += (_, args) => args.Handled = true;
        MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            if (_enabled) Clicked?.Invoke();
        };
        MouseEnter += (_, _) =>
        {
            _hovered = true;
            Render();
        };
        MouseLeave += (_, _) =>
        {
            _hovered = false;
            Render();
        };
        Render();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Render();
        }
    }

    public Color Tint
    {
        set
        {
            _tintOverride = value;
            Render();
        }
    }

    private void Render()
    {
        Background = _enabled
            ? (_hovered ? IslandColors.Brush(IslandColors.White(0.15)) : Brushes.Transparent)
            : Brushes.Transparent;
        _glyph.Foreground = IslandColors.Brush(
            _enabled ? (_tintOverride ?? IslandColors.White(0.90)) : IslandColors.White(0.25));
        Cursor = _enabled ? Cursors.Hand : Cursors.Arrow;
        Opacity = _enabled ? 1.0 : 0.35;
    }
}
