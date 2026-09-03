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
public sealed class ReportWindow : Window
{
    public enum Kind
    {
        Weekly,
        Monthly,
    }

    private static ReportWindow? _current;

    private Kind _kind;
    private readonly TextBlock _coach;
    private readonly Button _copy;
    private readonly StackPanel _actions;
    private readonly Grid _cardHost;
    private readonly Border _cardHeaderHotspot;
    private const double CardHeaderHotspotHeight = 72.0;
    private readonly StackPanel _pager;
    private readonly TextBlock _periodLabel;
    private readonly Border _periodBadge;
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
    private DispatcherTimer? _coachTimer;
    private readonly System.ComponentModel.PropertyChangedEventHandler _costChanged;

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
        _kind = kind;
        _display = CurrentData();
        Title = kind == Kind.Weekly
            ? Localization.L10n.Tr("Weekly report")
            : Localization.L10n.Tr("Share monthly report");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = false;
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var zh = Localization.L10n.IsChinese;

        // ← period label → row above the card (macOS pager): the right edge
        // is the current period, the left edge the earliest scanned day, and
        // the calendar anchors the window to any start date.
        _back = new PagerCircle("\uE76B", zh ? "上一周期 (←)" : "Previous period (←)");
        _back.Clicked += OnBackClicked;
        _forward = new PagerCircle("", zh ? "下一周期 (→)" : "Next period (→)");
        _forward.Clicked += OnForwardClicked;

        _periodLabel = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.95)),
            MinWidth = 128,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(_periodLabel, System.Windows.FontNumeralAlignment.Tabular);

        _periodBadge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Child = _periodLabel,
        };
        _periodBadge.MouseEnter += (_, _) =>
        {
            if (_pageOffset > 0 || _anchorDate is not null)
            {
                _periodBadge.Background = IslandColors.Brush(IslandColors.White(0.12));
            }
        };
        _periodBadge.MouseLeave += (_, _) =>
        {
            _periodBadge.Background = Brushes.Transparent;
        };
        _periodBadge.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            if (_pageOffset > 0 || _anchorDate is not null)
            {
                Flip(0);
            }
        };

        _calendarButton = new PagerCircle("", zh ? "选择指定日期 (日历)" : "Select date...");
        _calendarButton.Clicked += OpenCalendar;

        var capsuleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        capsuleStack.Children.Add(_back);
        capsuleStack.Children.Add(_periodBadge);
        capsuleStack.Children.Add(_forward);

        // Divider
        capsuleStack.Children.Add(new Border
        {
            Width = 1,
            Height = 14,
            Background = IslandColors.Brush(IslandColors.White(0.12)),
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        capsuleStack.Children.Add(_calendarButton);

        var capsule = new Border
        {
            Background = IslandColors.Brush(Color.FromRgb(0x13, 0x16, 0x1C)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.15)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Height = 34,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 3,
                Direction = 270,
                BlurRadius = 16,
                Color = Colors.Black,
                Opacity = 0.35,
            },
            Child = capsuleStack,
        };

        _pager = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
            Background = Brushes.Transparent,
        };
        _pager.MouseEnter += (_, _) => SetPagerVisible(true);
        _pager.MouseLeave += (_, _) => CheckHidePager();
        _pager.Children.Add(capsule);

        // Hover hotspot: only hovering the top designated header height of the card
        // or the pager itself reveals the navigation controls.
        _cardHeaderHotspot = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = CardHeaderHotspotHeight,
            Background = Brushes.Transparent,
        };
        _cardHeaderHotspot.MouseEnter += (_, _) => SetPagerVisible(true);
        _cardHeaderHotspot.MouseLeave += (_, _) => CheckHidePager();

        _cardHost = new Grid();
        _pagerHideTimer = new DispatcherTimer
        {
            // Leave enough time to cross the small transparent gap between
            // the card header and the pager without making the chrome feel sticky.
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _pagerHideTimer.Tick += (_, _) =>
        {
            _pagerHideTimer.Stop();
            if (_calendar is { IsOpen: true }) return;
            if (!_cardHeaderHotspot.IsMouseOver && !_pager.IsMouseOver)
            {
                SetPagerVisible(false);
            }
        };
        SetPagerVisible(false, animate: false);

        _copy = ActionButton(Localization.L10n.Tr("Copy image"), prominent: true);
        _copy.Click += (_, _) =>
        {
            if (!CopyImage()) return;
            _copy.Content = Localization.L10n.Tr("Copied");
            ShowCoach(Localization.L10n.Tr("Copied! Post it and bring a friend to the island 🏝️ Thanks for spreading the word"));
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                _copy.Content = Localization.L10n.Tr("Copy image");
            };
            reset.Start();
        };
        var save = ActionButton(Localization.L10n.Tr("Save PNG"), prominent: false);
        save.Click += (_, _) => SavePng();

        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        _actions.Children.Add(_copy);
        save.Margin = new Thickness(10, 0, 0, 0);
        _actions.Children.Add(save);

        // Fixed one-line slot so the window never reflows.
        _coach = new TextBlock
        {
            Text = " ",
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(Color.FromRgb(0x8C, 0xD9, 0x9E)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Opacity = 0,
        };

        // The close control rides ON the card (top-right, dark disc, hover
        // red) — parked on the window's transparent margin it was invisible
        // against a light desktop.
        RebuildCard();

        var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 12) };
        stack.Children.Add(_pager);
        stack.Children.Add(_cardHost);
        stack.Children.Add(_actions);
        stack.Children.Add(_coach);
        Content = stack;

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

        // A fresh scan self-heals a stale launch snapshot within seconds; the
        // store commit rebuilds the live page when it lands (macOS onAppear).
        _costChanged = (_, args) =>
        {
            if (args.PropertyName != nameof(Cost.CostStore.LastUpdated)) return;
            if (_pageOffset != 0 || _anchorDate is not null || _loading) return;
            _display = CurrentData();
            RebuildCard();
            AlignCurrentWeek();
        };
        Cost.CostStore.Shared.PropertyChanged += _costChanged;
        Closed += (_, _) => Cost.CostStore.Shared.PropertyChanged -= _costChanged;
        if (!Core.AppEnvironment.IsDemo) Cost.CostStore.Shared.Refresh();
        AlignCurrentWeek();

        // Warm the 3x export render off the click path — it costs a beat,
        // and doing it lazily made the first Copy feel broken.
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
        _kind = target;
        _pageOffset = 0;
        _anchorDate = null;
        _loading = false;
        Title = _kind == Kind.Weekly
            ? Localization.L10n.Tr("Weekly report")
            : Localization.L10n.Tr("Share monthly report");
        _display = CurrentData();
        RebuildCard();
        if (_kind == Kind.Weekly)
        {
            AlignCurrentWeek();
        }
    }

    /// Rebuild the on-screen card from the current display data and refresh
    /// every piece of pager chrome. Also resets the export cache — copy and
    /// save must ship exactly the page on screen.
    private void RebuildCard()
    {
        _rendered = null;
        var card = CardFor(_display, rounded: true);
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            ShadowDepth = 4,
            Direction = 270,
            BlurRadius = 30,
            Color = Colors.Black,
            Opacity = 0.30,
        };
        _cardHost.Children.Clear();
        _cardHost.Children.Add(card);
        _cardHost.Children.Add(_cardHeaderHotspot);
        _cardHost.Children.Add(CloseDisc());
        UpdatePagerChrome();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = ExportRender());
    }

    private void UpdatePagerChrome()
    {
        _periodLabel.Text = PeriodText;
        _periodLabel.Opacity = _loading ? 0.45 : 1;

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
        _periodBadge.ToolTip = isHistorical
            ? (Localization.L10n.IsChinese ? "点击回到最新周期" : "Click to return to current period")
            : null;
        _periodBadge.Cursor = isHistorical ? Cursors.Hand : Cursors.Arrow;

        // While a past page is still assembling, the card shows the previous
        // period — exporting would ship the wrong week.
        _actions.IsEnabled = !_loading;
        _actions.Opacity = _loading ? 0.5 : 1;
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
        // Arrow paging leaves anchored mode and resumes calendar tiling.
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

    /// The live weekly card's model table and dollars come from the store's
    /// WALL-CLOCK last-7-days window, while its bars anchor to the freshest
    /// SCANNED day — on a machine whose logs stopped days ago the hero says
    /// 2693万 while the donut sits empty. Rebuild the current page from one
    /// anchored interval slice so every series shares one window by
    /// construction. (Same latent shear exists in the macOS current() —
    /// invisible there only while the machine is used daily.)
    private async void AlignCurrentWeek()
    {
        if (_kind != Kind.Weekly || Core.AppEnvironment.IsDemo) return;
        var (start, end) = ReportPeriods.WeekInterval(0);
        // Nothing to align when the anchored week IS the wall-clock week.
        if (end > DateTime.Today) return;
        var slices = await ReportPeriods.SlicesAsync(start, end);
        if (_pageOffset != 0 || _anchorDate is not null || _loading) return;
        _display = WeeklyReportData.ForInterval(start, end, slices);
        RebuildCard();
    }

    private async void LoadPage(int target)
    {
        _loading = true;
        UpdatePagerChrome();
        var (start, end) = _kind == Kind.Weekly
            ? ReportPeriods.WeekInterval(target)
            : ReportPeriods.MonthInterval(target);
        var slices = await ReportPeriods.SlicesAsync(start, end);
        // The user may have flipped again while the scan ran.
        if (_pageOffset != target || _anchorDate is not null) return;
        _display = _kind == Kind.Weekly
            ? WeeklyReportData.ForInterval(start, end, slices)
            : MonthlyReportData.ForInterval(start, slices);
        _loading = false;
        RebuildCard();
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
            // Entering either the card or the pager cancels the pending hide
            // while the pointer crosses the gap between the two regions.
            _pagerHideTimer.Stop();
        }

        _pager.IsHitTestVisible = visible;
        var targetOpacity = visible ? 1.0 : 0.0;
        if (!animate)
        {
            _pager.BeginAnimation(UIElement.OpacityProperty, null);
            _pager.Opacity = targetOpacity;
            return;
        }

        var duration = new Duration(TimeSpan.FromSeconds(visible ? 0.15 : 0.20));
        var anim = new DoubleAnimation(targetOpacity, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Freeze();
        _pager.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// Any-date anchor: the window becomes [picked day, +7d) weekly /
    /// [picked day, +30d) monthly (macOS, owner ask 2026-08-08).
    private async void SetAnchor(DateTime day)
    {
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
        var slices = await ReportPeriods.SlicesAsync(start, end);
        if (_anchorDate != start) return;
        _display = _kind == Kind.Weekly
            ? WeeklyReportData.ForInterval(start, end, slices)
            : MonthlyReportData.ForInterval(start, slices);
        _loading = false;
        RebuildCard();
    }

    /// The card's own close control: a quiet dark disc with an ✕, top-right
    /// corner, red on hover — always visible against the card's ink.
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
        disc.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a drag
        disc.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        return disc;
    }

    private void ShowCoach(string text)
    {
        _coach.Text = text;
        _coach.Opacity = 1;
        _coachTimer?.Stop();
        _coachTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _coachTimer.Tick += (_, _) =>
        {
            _coachTimer?.Stop();
            _coach.Opacity = 0;
        };
        _coachTimer.Start();
    }

    // MARK: - Export

    /// The EXPORT version is the card itself, full-bleed with SQUARE outer
    /// corners on an opaque background, rendered at 3x for crispness — from
    /// exactly the page on screen.
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
            ShowCoach(Localization.L10n.Tr("Copied! Post it and bring a friend to the island 🏝️ Thanks for spreading the word"));
        }
        catch
        {
        }
    }

    /// Headless snapshot for tooling/screenshots:
    /// AGENTISLAND_REPORT_SNAPSHOT / AGENTISLAND_MONTHLY_SNAPSHOT = path.png.
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

    /// Weekly report moment: once per ISO week, surface the card shortly
    /// after launch (the cost scan needs a beat). Sharing needs a moment put
    /// in front of people, not a buried menu item.
    public static void ArmWeeklyMoment()
    {
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            var now = DateTime.Now;
            var weekKey = $"{ISOWeek.GetYear(now)}-W{ISOWeek.GetWeekOfYear(now)}";
            const string shownKey = "AgentIsland.weeklyReportShownForWeek";
            if (Core.Preferences.Get<string?>(shownKey) == weekKey) return;
            Core.Preferences.Set(shownKey, weekKey);
            Show(Kind.Weekly);
        };
        delay.Start();
    }

    private static Button ActionButton(string title, bool prominent)
    {
        var button = new Button
        {
            Content = title,
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = prominent
                ? Brushes.Black
                : IslandColors.Brush(Color.FromRgb(0x15, 0x17, 0x1C)),
            Height = 30,
            Padding = new Thickness(16, 0, 16, 0),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        var face = prominent
            ? Brushes.White
            : IslandColors.Brush(Color.FromRgb(0xEE, 0xF0, 0xF2));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        factory.SetValue(Border.BackgroundProperty, face);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(MarginProperty, new Thickness(16, 0, 16, 0));
        factory.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
        IslandMotion.AttachPressFeedback(button);
        return button;
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
        // Swallow the DOWN: the report window starts a DragMove on any
        // unclaimed press, which captures the mouse and eats the UP — the
        // pager read as dead (the close disc dodges this the same way).
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

    /// Optional glyph tint override while enabled (the calendar button goes
    /// brighter brand teal when a date anchor is active).
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
