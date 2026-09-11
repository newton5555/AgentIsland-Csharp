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
        Daily,
        Weekly,
        Monthly,
    }

    private static ReportWindow? _current;

    private readonly ICostStore? _costStore;
    private readonly IProviderVisibilityStore? _visibilityStore;
    private readonly TokenCountModeStore? _tokenModeStore;
    private readonly ICostQueryService? _costQueryService;

    private Kind _kind;
    private readonly PagerCircle _back;
    private readonly PagerCircle _forward;
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

    public static void Show(
        Kind kind,
        ICostStore? costStore = null,
        IProviderVisibilityStore? visibilityStore = null,
        TokenCountModeStore? tokenModeStore = null,
        ICostQueryService? costQueryService = null)
    {
        if (_current != null && (_current.IsLoaded || _current.IsVisible))
        {
            _current.SwitchKind(kind);
            WindowActivation.BringToFront(_current);
            return;
        }
        var window = new ReportWindow(kind, costStore, visibilityStore, tokenModeStore, costQueryService);
        _current = window;
        window.Closed += (_, _) =>
        {
            if (_current == window) _current = null;
        };
        window.Show();
        WindowActivation.BringToFront(window);
    }

    public ReportWindow() : this(Kind.Weekly) { }

    public ReportWindow(
        Kind kind,
        ICostStore? costStore = null,
        IProviderVisibilityStore? visibilityStore = null,
        TokenCountModeStore? tokenModeStore = null,
        ICostQueryService? costQueryService = null)
    {
        _costStore = costStore ?? (App.Instance?.Services?.GetService(typeof(ICostStore)) as ICostStore);
        _visibilityStore = visibilityStore ?? (App.Instance?.Services?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore);
        _tokenModeStore = tokenModeStore ?? (App.Instance?.Services?.GetService(typeof(TokenCountModeStore)) as TokenCountModeStore);
        _costQueryService = costQueryService ?? (App.Instance?.Services?.GetService(typeof(ICostQueryService)) as ICostQueryService);

        InitializeComponent();
        _kind = kind;
        _display = CurrentData();

        Title = kind switch
        {
            Kind.Daily => AgentIsland.UI.Localization.L10n.Tr("Daily report"),
            Kind.Weekly => AgentIsland.UI.Localization.L10n.Tr("Weekly report"),
            _ => AgentIsland.UI.Localization.L10n.Tr("Share monthly report"),
        };

        _back = new PagerCircle("\uE76B", AgentIsland.UI.Localization.L10n.Tr("Previous period (←)"));
        _back.Clicked += OnBackClicked;
        BackSlot.Child = _back;

        _forward = new PagerCircle("", AgentIsland.UI.Localization.L10n.Tr("Next period (→)"));
        _forward.Clicked += OnForwardClicked;
        ForwardSlot.Child = _forward;

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
        // The badge must opt out of the window drag or DragMove's modal loop
        // swallows the press and its MouseLeftButtonUp never fires — the same
        // guard the pager circles and the close disc carry.
        PeriodBadge.MouseLeftButtonDown += (_, args) => args.Handled = true;
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
            AlignCurrentDay();
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
        if (_costStore != null) _costStore.PropertyChanged += _costChanged;
        if (_visibilityStore != null) _visibilityStore.PropertyChanged += _providerSelectionChanged;
        Closed += (_, _) =>
        {
            CancelReportQuery();
            _pagerHideTimer.Stop();
            _coachTimer?.Stop();
            if (_costStore != null) _costStore.PropertyChanged -= _costChanged;
            if (_visibilityStore != null) _visibilityStore.PropertyChanged -= _providerSelectionChanged;
        };
        if (!Core.AppEnvironment.IsDemo) _costStore?.Refresh();
        AlignCurrentWeek();
        AlignCurrentDay();

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = ExportRender());
    }

    private object CurrentData() => _kind switch
    {
        Kind.Daily => DailyReportData.Current(_costStore, _tokenModeStore, _visibilityStore),
        Kind.Weekly => WeeklyReportData.Current(_costStore, _tokenModeStore, _visibilityStore, _costQueryService),
        _ => MonthlyReportData.Current(_costStore, _tokenModeStore, _visibilityStore),
    };

    private string _dailySelectedTab = "overview";

    private FrameworkElement CardFor(object data, bool rounded) => _kind switch
    {
        Kind.Daily => ReportCards.Daily((DailyReportData)data, rounded, _dailySelectedTab, OnDailyTabChanged),
        Kind.Weekly => ReportCards.Weekly((WeeklyReportData)data, rounded),
        _ => ReportCards.Monthly((MonthlyReportData)data, rounded),
    };

    private void OnDailyTabChanged(string tab)
    {
        _dailySelectedTab = tab;
        _rendered = null;
    }

    private string PeriodText => _kind switch
    {
        Kind.Daily => ((DailyReportData)_display).PagerLabel,
        Kind.Weekly => ((WeeklyReportData)_display).RangeText,
        _ => ((MonthlyReportData)_display).MonthText,
    };

    public void SwitchKind(Kind target)
    {
        if (_kind == target) return;
        CancelReportQuery();
        _kind = target;
        _pageOffset = 0;
        _anchorDate = null;
        _loading = false;
        Title = _kind switch
        {
            Kind.Daily => AgentIsland.UI.Localization.L10n.Tr("Daily report"),
            Kind.Weekly => AgentIsland.UI.Localization.L10n.Tr("Weekly report"),
            _ => AgentIsland.UI.Localization.L10n.Tr("Share monthly report"),
        };
        _display = CurrentData();
        RebuildCard();
        if (_kind == Kind.Weekly)
        {
            AlignCurrentWeek();
        }
        else if (_kind == Kind.Daily)
        {
            AlignCurrentDay();
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
            var earliest = ReportPeriods.EarliestDataDay(_costStore);
            canGoBack = !_loading && anchor > earliest;
            canGoForward = !_loading;
        }
        else
        {
            var interval = _kind switch
            {
                Kind.Daily => ReportPeriods.DayInterval(_pageOffset, _costStore),
                Kind.Weekly => ReportPeriods.WeekInterval(_pageOffset, _costStore),
                _ => ReportPeriods.MonthInterval(_pageOffset),
            };
            canGoBack = !_loading && ReportPeriods.HasData(interval.Start, ReportPeriods.EarliestDataDay(_costStore));
            canGoForward = _pageOffset > 0 && !_loading;
        }

        _back.Enabled = canGoBack;
        _forward.Enabled = canGoForward;

        // The badge itself is the calendar trigger now — one affordance for
        // date selection across all three cards. Demo keeps real logs off
        // recordings, so the calendar (which pages from real scans) stays
        // closed there.
        PeriodBadge.ToolTip = Core.AppEnvironment.IsDemo
            ? null
            : AgentIsland.UI.Localization.L10n.Tr("Select date...");
        PeriodBadge.Cursor = Core.AppEnvironment.IsDemo ? Cursors.Arrow : Cursors.Hand;

        ActionsPanel.IsEnabled = !_loading;
        ActionsPanel.Opacity = _loading ? 0.5 : 1;
    }

    private void OnBackClicked()
    {
        if (_anchorDate is { } anchor)
        {
            var prev = _kind switch
            {
                Kind.Daily => anchor.AddDays(-1),
                Kind.Weekly => anchor.AddDays(-7),
                _ => anchor.AddMonths(-1),
            };
            if (prev >= ReportPeriods.EarliestDataDay(_costStore))
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
            var next = _kind switch
            {
                Kind.Daily => anchor.AddDays(1),
                Kind.Weekly => anchor.AddDays(7),
                _ => anchor.AddMonths(1),
            };
            var overshoots = _kind switch
            {
                Kind.Daily => next >= DateTime.Today,
                Kind.Weekly => next >= DateTime.Today || next.AddDays(7) > DateTime.Today,
                _ => next >= DateTime.Today,
            };
            if (overshoots)
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
            if (_kind == Kind.Weekly) AlignCurrentWeek();
            else if (_kind == Kind.Daily) AlignCurrentDay();
            return;
        }
        LoadPage(target);
    }

    private async void AlignCurrentWeek()
    {
        if (_kind != Kind.Weekly || Core.AppEnvironment.IsDemo) return;
        var (start, end) = ReportPeriods.WeekInterval(0, _costStore);
        if (end > DateTime.Today) return;
        var queryId = BeginReportQuery(out var cts);
        try
        {
            var slices = await ReportPeriods.SlicesAsync(start, end, _visibilityStore, _costQueryService, cts.Token);
            if (queryId != _querySequence || !IsLoaded
                || _pageOffset != 0 || _anchorDate is not null || _loading) return;
            _display = WeeklyReportData.ForInterval(start, end, slices, _tokenModeStore, _visibilityStore);
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

    /// The live daily card is a skeleton (buckets only) until the
    /// event-level slice lands — same two-beat rebuild as the weekly card.
    private async void AlignCurrentDay()
    {
        if (_kind != Kind.Daily || Core.AppEnvironment.IsDemo) return;
        var (start, end) = ReportPeriods.DayInterval(0, _costStore);
        var queryId = BeginReportQuery(out var cts);
        try
        {
            var slicesTask = ReportPeriods.SlicesAsync(start, end, _visibilityStore, _costQueryService, cts.Token, includeAllDetected: true);
            var baselineTask = PreviousDayTokensAsync(start, cts.Token);
            await Task.WhenAll(slicesTask, baselineTask).ConfigureAwait(true);
            var slices = slicesTask.Result;
            if (queryId != _querySequence || !IsLoaded
                || _pageOffset != 0 || _anchorDate is not null || _loading) return;
            _display = DailyReportData.ForInterval(
                start, slices, _tokenModeStore, _visibilityStore, baselineTask.Result);
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

    /// Day-over-day baseline in the SAME scope as the card: a forced scan
    /// of yesterday across every host. CostStore only carries enabled
    /// providers, and the daily card now shows guests with data too — so
    /// the baseline must see them or the delta lies. LogParseCache
    /// memoizes per file, so this second walk is nearly free.
    private async Task<long> PreviousDayTokensAsync(DateTime day, CancellationToken token)
    {
        var yesterday = day.Date.AddDays(-1);
        var slices = await ReportPeriods.SlicesAsync(
            yesterday, yesterday.AddDays(1), _visibilityStore, _costQueryService, token,
            includeAllDetected: true).ConfigureAwait(true);
        return slices.Values
            .Sum(s => s.DailyTokens.Count > 0 ? s.DailyTokens[0].Tokens : 0);
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
        else if (_kind == Kind.Daily) AlignCurrentDay();
    }

    private async void LoadPage(int target)
    {
        var queryId = BeginReportQuery(out var cts);
        _loading = true;
        UpdatePagerChrome();
        var (start, end) = _kind switch
        {
            Kind.Daily => ReportPeriods.DayInterval(target, _costStore),
            Kind.Weekly => ReportPeriods.WeekInterval(target, _costStore),
            _ => ReportPeriods.MonthInterval(target),
        };
        var accepted = false;
        try
        {
            long baseline = 0;
            IReadOnlyDictionary<AgentIsland.UI.Providers.DisplayProvider, ReportSlice> slices;
            if (_kind == Kind.Daily)
            {
                var slicesTask = ReportPeriods.SlicesAsync(start, end, _visibilityStore, _costQueryService, cts.Token, includeAllDetected: true);
                var baselineTask = PreviousDayTokensAsync(start, cts.Token);
                await Task.WhenAll(slicesTask, baselineTask).ConfigureAwait(true);
                slices = slicesTask.Result;
                baseline = baselineTask.Result;
            }
            else
            {
                slices = await ReportPeriods.SlicesAsync(start, end, _visibilityStore, _costQueryService, cts.Token);
            }
            if (queryId != _querySequence || !IsLoaded
                || _pageOffset != target || _anchorDate is not null) return;
            _display = _kind switch
            {
                Kind.Daily => DailyReportData.ForInterval(start, slices, _tokenModeStore, _visibilityStore, baseline),
                Kind.Weekly => WeeklyReportData.ForInterval(start, end, slices, _tokenModeStore, _visibilityStore),
                _ => MonthlyReportData.ForInterval(start, slices, _tokenModeStore, _visibilityStore),
            };
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
        // Demo keeps real usage off recordings; the calendar pages assemble
        // from real scans, so it stays closed there.
        if (Core.AppEnvironment.IsDemo) return;
        var currentSelected = _anchorDate ?? (_kind switch
        {
            Kind.Daily => ReportPeriods.DayInterval(_pageOffset, _costStore).Start,
            Kind.Weekly => ReportPeriods.WeekInterval(_pageOffset, _costStore).Start,
            _ => ReportPeriods.MonthInterval(_pageOffset).Start,
        });
        _calendar = new ReportCalendarPopup(ReportPeriods.EarliestDataDay(_costStore), SetAnchor, currentSelected)
        {
            PlacementTarget = PeriodBadge,
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
        if (_kind == Kind.Daily && start >= ReportPeriods.DayInterval(0, _costStore).Start)
        {
            Flip(0);
            return;
        }
        if (_kind == Kind.Weekly && start >= ReportPeriods.WeekInterval(0, _costStore).Start)
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
        var end = _kind switch
        {
            Kind.Daily => start.AddDays(1),
            Kind.Weekly => start.AddDays(7),
            _ => start.AddMonths(1),
        };
        var queryId = BeginReportQuery(out var cts);
        var accepted = false;
        try
        {
            long baseline = 0;
            IReadOnlyDictionary<AgentIsland.UI.Providers.DisplayProvider, ReportSlice> slices;
            if (_kind == Kind.Daily)
            {
                var slicesTask = ReportPeriods.SlicesAsync(start, end, _visibilityStore, _costQueryService, cts.Token, includeAllDetected: true);
                var baselineTask = PreviousDayTokensAsync(start, cts.Token);
                await Task.WhenAll(slicesTask, baselineTask).ConfigureAwait(true);
                slices = slicesTask.Result;
                baseline = baselineTask.Result;
            }
            else
            {
                slices = await ReportPeriods.SlicesAsync(start, end, _visibilityStore, _costQueryService, cts.Token);
            }
            if (queryId != _querySequence || !IsLoaded || _anchorDate != start) return;
            _display = _kind switch
            {
                Kind.Daily => DailyReportData.ForInterval(start, slices, _tokenModeStore, _visibilityStore, baseline),
                Kind.Weekly => WeeklyReportData.ForInterval(start, end, slices, _tokenModeStore, _visibilityStore),
                _ => MonthlyReportData.ForInterval(start, slices, _tokenModeStore, _visibilityStore),
            };
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
        if (!Core.AppEnvironment.IsDemo)
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
        OpenCalendar();
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

    public static BitmapSource RenderCard(
        Kind kind,
        ICostStore? costStore = null,
        TokenCountModeStore? tokenModeStore = null,
        IProviderVisibilityStore? visibilityStore = null,
        ICostQueryService? costQueryService = null)
    {
        var card = kind switch
        {
            Kind.Daily => ReportCards.Daily(DailyReportData.Current(costStore, tokenModeStore, visibilityStore), rounded: false),
            Kind.Weekly => ReportCards.Weekly(WeeklyReportData.Current(costStore, tokenModeStore, visibilityStore, costQueryService), rounded: false),
            _ => ReportCards.Monthly(MonthlyReportData.Current(costStore, tokenModeStore, visibilityStore), rounded: false),
        };
        return Render(card);
    }

    private bool CopyImage()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetImage(ExportRender());
                return true;
            }
            catch
            {
                if (attempt < 2) System.Threading.Thread.Sleep(50);
            }
        }
        return false;
    }

    private void SavePng()
    {
        var tag = _kind switch
        {
            Kind.Daily => _dailySelectedTab.Equals("overview", StringComparison.OrdinalIgnoreCase) ? "daily" : $"daily-{_dailySelectedTab.ToLowerInvariant()}",
            Kind.Weekly => "weekly",
            _ => "monthly",
        };
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
            ShowCoach(AgentIsland.UI.Localization.L10n.Tr("Saved! Post it and bring a friend to the island 🏝️ Thanks for spreading the word"));
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
