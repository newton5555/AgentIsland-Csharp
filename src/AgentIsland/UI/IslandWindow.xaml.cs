using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.UI.Theme;
using AgentIsland.Core.Usage;
using AgentIsland.UI.Providers;

namespace AgentIsland.UI;

/// The island itself: a borderless, topmost, per-pixel-transparent window
/// docked to an edge of the chosen screen (top-center by default). Fully
/// transparent pixels pass clicks through to whatever is behind, so only the
/// black silhouette is interactive — the WPF equivalent of the macOS hitTest
/// override. The native window style is switched with the global cursor so
/// this remains true when the underlying app belongs to another process.
public partial class IslandWindow : Window
{
    private readonly IslandModel _model = IslandModel.Shared;
    private System.Windows.Interop.HwndSource? _windowSource;
    private DispatcherTimer? _mouseHitTestTimer;
    private bool _mouseClickThrough;

    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    // Unsubscribe actions for the singleton-store handlers, run on Closed —
    // the island is discarded and recreated on a language switch, and without
    // this the dead window stays pinned by the stores and keeps handling
    // events (placement/usage/alert) forever.
    private readonly List<Action> _teardown = new();
    private bool _hovering;
    private System.Windows.Controls.StackPanel? _leftTitle;
    private System.Windows.Controls.StackPanel? _rightTitle;
    private ResetCardChip? _leftResetCards;
    private ResetCardChip? _rightResetCards;
    private System.Windows.Controls.TextBlock? _leftChip;
    private System.Windows.Controls.TextBlock? _rightChip;

    /// What the two physical flanks currently carry. Any two of the five
    /// providers can hold the slots (任选两家) — the elements keep their
    /// historical Claude*/Codex* names but are retargeted per selection.
    private TriggerTool? _leftTool = TriggerTool.Claude;
    private TriggerTool? _rightTool = TriggerTool.Codex;

    public IslandWindow()
    {
        InitializeComponent();
        LeftLogo.Tool = TriggerTool.Claude;
        RightLogo.Tool = TriggerTool.Codex;
        LeftPill.Tool = TriggerTool.Claude;
        LeftPill.Mirrored = false;
        RightPill.Tool = TriggerTool.Codex;
        RightPill.Mirrored = true;
        Loaded += OnLoaded;
    }

    /// A transparent layered WPF window can keep the mouse over the visual
    /// bounds of an effect (the halo) even when the effect itself is marked
    /// IsHitTestVisible=false. Keep the glow, but let Windows pass points
    /// outside the real silhouette through to the app underneath.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        _mouseHitTestTimer?.Stop();
        _mouseHitTestTimer = null;
        SetMouseClickThrough(false);
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        base.OnClosed(e);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmNcHitTest || IsPointInsideSilhouette(lParam))
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HtTransparent);
    }

    /// WM_NCHITTEST carries screen coordinates in physical pixels. Derive the
    /// current silhouette's screen rectangle with PointToScreen instead of
    /// converting the message point back through the visual tree: the former
    /// keeps the comparison in one coordinate space across per-monitor DPI,
    /// LayoutTransform scaling, and open/close morphs.
    private bool IsPointInsideSilhouette(IntPtr lParam)
    {
        var raw = lParam.ToInt64();
        var screenX = unchecked((short)(raw & 0xFFFF));
        var screenY = unchecked((short)((raw >> 16) & 0xFFFF));
        return IsPointInsideSilhouette(new Point(screenX, screenY));
    }

    private bool IsPointInsideSilhouette(Point screenPoint)
    {
        try
        {
            var width = Silhouette.ActualWidth;
            var height = Silhouette.ActualHeight;
            if (width <= 0 || height <= 0) return false;

            var topLeft = Silhouette.PointToScreen(new Point(0, 0));
            var bottomRight = Silhouette.PointToScreen(new Point(width, height));
            var screenWidth = bottomRight.X - topLeft.X;
            var screenHeight = bottomRight.Y - topLeft.Y;
            if (screenWidth <= 0 || screenHeight <= 0) return false;

            // Normalize the native screen point into the silhouette's local
            // coordinates. This avoids a second screen-to-DIP conversion and
            // keeps the corner test aligned with Border's CornerRadius.
            var point = new Point(
                (screenPoint.X - topLeft.X) / screenWidth * width,
                (screenPoint.Y - topLeft.Y) / screenHeight * height);
            if (point.X < 0 || point.Y < 0 || point.X > width || point.Y > height)
            {
                return false;
            }

            var radius = Math.Min(_model.CornerRadius, Math.Min(width, height) / 2);
            if (radius <= 0) return true;

            // Top-bar mode has square top corners and rounded bottom corners;
            // floating mode rounds all four corners just like the Border.
            if (IsFloating)
            {
                if (point.X < radius && point.Y < radius
                    && !InsideCorner(point, radius, radius, radius)) return false;
                if (point.X > width - radius && point.Y < radius
                    && !InsideCorner(point, width - radius, radius, radius)) return false;
            }
            if (point.X < radius && point.Y > height - radius
                && !InsideCorner(point, radius, height - radius, radius)) return false;
            if (point.X > width - radius && point.Y > height - radius
                && !InsideCorner(point, width - radius, height - radius, radius)) return false;
            return true;
        }
        catch (InvalidOperationException)
        {
            // The handle can receive a message while WPF is tearing down or
            // before the visual has joined a presentation source. Let the
            // underlying window receive that point.
            return false;
        }
    }

    /// HTTRANSPARENT is only forwarded to windows on the same GUI thread.
    /// Agent Island normally sits above another process, so use the layered
    /// window's WS_EX_TRANSPARENT style while the cursor is outside the real
    /// silhouette. The timer still sees the global cursor and removes the
    /// style as soon as the cursor enters the island, preserving clicks and
    /// floating-mode drag behavior.
    private void StartMouseHitTestWatchdog()
    {
        if (_mouseHitTestTimer is not null) return;

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        timer.Tick += (_, _) => UpdateMouseHitTestMode();
        _mouseHitTestTimer = timer;
        _teardown.Add(timer.Stop);
        timer.Start();
        UpdateMouseHitTestMode();
    }

    private void UpdateMouseHitTestMode()
    {
        if (!IsLoaded || !IsVisible || !GetCursorPos(out var cursor)) return;

        var inside = IsPointInsideSilhouette(new Point(cursor.X, cursor.Y));
        SetMouseClickThrough(!inside);
    }

    private void SetMouseClickThrough(bool clickThrough)
    {
        if (_mouseClickThrough == clickThrough) return;

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var extendedStyle = IntPtr.Size == 8
            ? GetWindowLongPtr64(handle, GwlExStyle).ToInt64()
            : GetWindowLong32(handle, GwlExStyle);
        var nextStyle = clickThrough
            ? extendedStyle | WsExTransparent
            : extendedStyle & ~WsExTransparent;

        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(handle, GwlExStyle, new IntPtr(nextStyle));
        }
        else
        {
            SetWindowLong32(handle, GwlExStyle, unchecked((int)nextStyle));
        }

        // Refresh the non-client hit-test cache without moving, resizing,
        // reordering, or activating the island.
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        _mouseClickThrough = clickThrough;
    }

    private static bool InsideCorner(Point point, double centerX, double centerY, double radius)
    {
        var dx = point.X - centerX;
        var dy = point.Y - centerY;
        return dx * dx + dy * dy <= radius * radius;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyEdgeLayout();
        PositionOnScreen();
        // Floating mode: drag the silhouette to move (and remember) the
        // window; a non-drag press still expands.
        Silhouette.MouseLeftButtonDown += OnSilhouetteMouseDown;

        // The sweep ring tracks the silhouette through every spring morph
        // (+4 so half its stroke rides outside the edge).
        Silhouette.SizeChanged += (_, args) =>
        {
            Sweep.Width = args.NewSize.Width + 4;
            Sweep.Height = args.NewSize.Height + 4;
        };
        System.ComponentModel.PropertyChangedEventHandler onLowPower =
            (_, _) => Dispatcher.BeginInvoke(UpdateHalo);
        AgentIsland.Backend.Settings.LowPowerModeStore.Shared.PropertyChanged += onLowPower;
        _teardown.Add(() => AgentIsland.Backend.Settings.LowPowerModeStore.Shared.PropertyChanged -= onLowPower);

        System.ComponentModel.PropertyChangedEventHandler onGlowColor =
            (_, _) => Dispatcher.BeginInvoke(UpdateHalo);
        AgentIsland.Backend.Settings.GlowColorStore.Shared.PropertyChanged += onGlowColor;
        _teardown.Add(() => AgentIsland.Backend.Settings.GlowColorStore.Shared.PropertyChanged -= onGlowColor);

        ApplyInterfaceScale();
        System.ComponentModel.PropertyChangedEventHandler onScale =
            (_, _) => Dispatcher.BeginInvoke(() =>
            {
                ApplyInterfaceScale();
                PositionOnScreen();
            });
        AgentIsland.Backend.Settings.IslandScaleStore.Shared.PropertyChanged += onScale;
        _teardown.Add(() => AgentIsland.Backend.Settings.IslandScaleStore.Shared.PropertyChanged -= onScale);

        System.ComponentModel.PropertyChangedEventHandler onSysParams = (_, args) =>
        {
            if (args.PropertyName is nameof(SystemParameters.WorkArea)
                or nameof(SystemParameters.PrimaryScreenWidth))
            {
                Dispatcher.BeginInvoke(PositionOnScreen);
            }
        };
        SystemParameters.StaticPropertyChanged += onSysParams;
        _teardown.Add(() => SystemParameters.StaticPropertyChanged -= onSysParams);

        // WorkArea/PrimaryScreenWidth only cover the primary display;
        // plug/unplug or resolution changes on a pinned secondary arrive via
        // SystemEvents (the didChangeScreenParameters analog).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _teardown.Add(() =>
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged);

        StartVisibilityWatchdog();

        System.ComponentModel.PropertyChangedEventHandler onTargetDisplay =
            (_, _) => Dispatcher.BeginInvoke(PositionOnScreen);
        AgentIsland.Backend.Settings.IslandTargetDisplayStore.Shared.PropertyChanged += onTargetDisplay;
        _teardown.Add(() => AgentIsland.Backend.Settings.IslandTargetDisplayStore.Shared.PropertyChanged -= onTargetDisplay);

        System.ComponentModel.PropertyChangedEventHandler onPlacement = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ApplyEdgeLayout();
            PositionOnScreen();
        });
        AgentIsland.Backend.Settings.IslandPositionStore.Shared.PropertyChanged += onPlacement;
        _teardown.Add(() => AgentIsland.Backend.Settings.IslandPositionStore.Shared.PropertyChanged -= onPlacement);
        Closed += (_, _) => { foreach (var teardown in _teardown) teardown(); };

        ApplySizeInstant();
        StartMouseHitTestWatchdog();
        BuildExpandedChrome();

        System.ComponentModel.PropertyChangedEventHandler onActivity =
            (_, _) => Dispatcher.BeginInvoke(UpdateActivityVisuals);
        ActivityMonitor.Shared.PropertyChanged += onActivity;
        _teardown.Add(() => ActivityMonitor.Shared.PropertyChanged -= onActivity);

        System.ComponentModel.PropertyChangedEventHandler onUsage = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            UpdatePlanChips();
            UpdatePills();
            UpdateHalo();
        });
        UsageStore.Shared.PropertyChanged += onUsage;
        _teardown.Add(() => UsageStore.Shared.PropertyChanged -= onUsage);
        AntigravityUsageStore.Shared.PropertyChanged += onUsage;
        _teardown.Add(() => AntigravityUsageStore.Shared.PropertyChanged -= onUsage);
        GrokUsageStore.Shared.PropertyChanged += onUsage;
        _teardown.Add(() => GrokUsageStore.Shared.PropertyChanged -= onUsage);
        CursorUsageStore.Shared.PropertyChanged += onUsage;
        _teardown.Add(() => CursorUsageStore.Shared.PropertyChanged -= onUsage);
        AgentIsland.Backend.Settings.QuotaDisplayModeStore.Shared.PropertyChanged += onUsage;
        _teardown.Add(() => AgentIsland.Backend.Settings.QuotaDisplayModeStore.Shared.PropertyChanged -= onUsage);

        System.ComponentModel.PropertyChangedEventHandler onAlert = (_, args) => Dispatcher.BeginInvoke(() =>
        {
            if (args.PropertyName == nameof(AgentIsland.Backend.Settings.AlertEngine.Pulse)) HandleAlertPulse();
            UpdateHalo();
            UpdatePills();
        });
        AgentIsland.Backend.Settings.AlertEngine.Shared.PropertyChanged += onAlert;
        _teardown.Add(() => AgentIsland.Backend.Settings.AlertEngine.Shared.PropertyChanged -= onAlert);

        // Live layout changes (Settings bar width, provider visibility, solo
        // centering, placement) reflow the collapsed bar with the open
        // spring — everything but state transitions, which SetState drives
        // itself. _model is the IslandModel singleton, so this too must be
        // torn down.
        System.ComponentModel.PropertyChangedEventHandler onModel = (_, args) =>
        {
            if (args.PropertyName != nameof(IslandModel.Size) || _stateDrivenResize) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_model.State != IslandState.Expanded) AnimateBarMetrics();
            });
        };
        _model.PropertyChanged += onModel;
        _teardown.Add(() => _model.PropertyChanged -= onModel);

        System.ComponentModel.PropertyChangedEventHandler onVisibility =
            (_, _) => Dispatcher.BeginInvoke(ApplyProviderVisibility);
        AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.PropertyChanged += onVisibility;
        _teardown.Add(() => AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.PropertyChanged -= onVisibility);

        System.ComponentModel.PropertyChangedEventHandler onAlwaysShow = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // The Size re-emit lands in onModel, which animates the reflow.
            _model.NotifyAlwaysShowUsageChanged();
            UpdatePills();
        });
        AlwaysShowUsageStore.Shared.PropertyChanged += onAlwaysShow;
        _teardown.Add(() => AlwaysShowUsageStore.Shared.PropertyChanged -= onAlwaysShow);

        ApplyProviderVisibility();
        UpdateActivityVisuals();
        UpdatePills();

        // Scripted layout diagnosis: dump geometry once a second.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_LAYOUTLOG") == "1")
        {
            var log = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            log.Tick += (_, _) =>
            {
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AgentIsland.Windows.IslandPaths.AppSupportDir, "layout.log"),
                        $"{DateTime.Now:HH:mm:ss.f} state={_model.State} winLeft={Left:F0} winW={Width:F0} " +
                        $"silW={Silhouette.Width:F0} silActualW={Silhouette.ActualWidth:F0} " +
                        $"leftSlot={LeftPillColumn.Width} rightSlot={RightPillColumn.Width} " +
                        $"modelSize={_model.Size.Width:F0}x{_model.Size.Height:F0}\n");
                }
                catch
                {
                }
            };
            log.Start();
        }
    }

    /// Hidden providers drop their logo, peek pill, and expanded title —
    /// the balanced peek width is preserved by the model's fixed slots.
    private void ApplyProviderVisibility()
    {
        var slots = AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.Slots;
        _leftTool = slots.Count > 0 ? slots[0].ToTriggerTool() : null;
        _rightTool = slots.Count > 1 ? slots[1].ToTriggerTool() : null;

        if (_leftTool is { } left)
        {
            LeftLogo.Tool = left;
            LeftPill.Tool = left;
            LeftPill.Visibility = Visibility.Visible;
        }
        else
        {
            LeftPill.Visibility = Visibility.Collapsed;
            LeftPill.Inlines.Clear();
            LeftPill.BeginAnimation(OpacityProperty, null);
            LeftPill.Opacity = 0;
        }

        if (_rightTool is { } right)
        {
            RightLogo.Tool = right;
            RightPill.Tool = right;
            RightPill.Visibility = Visibility.Visible;
        }
        else
        {
            RightPill.Visibility = Visibility.Collapsed;
            RightPill.Inlines.Clear();
            RightPill.BeginAnimation(OpacityProperty, null);
            RightPill.Opacity = 0;
        }

        // The logo's fixed grid column reserves its slot either way, so we
        // fade opacity (the macOS openMorph spring) rather than hard-toggle
        // Visibility — toggling a provider springs the mark in/out.
        FadeLogo(LeftLogo, _leftTool is not null);
        FadeLogo(RightLogo, _rightTool is not null);
        RetitleFlank(_leftTitle, _leftTool);
        RetitleFlank(_rightTitle, _rightTool);
        ApplySoloSplit();
        UpdatePlanChips();
        UpdatePills();
    }

    /// Expanded-panel flank title follows its slot's provider.
    private static void RetitleFlank(System.Windows.Controls.StackPanel? title, TriggerTool? tool)
    {
        if (title is null) return;
        title.Visibility = tool is not null ? Visibility.Visible : Visibility.Collapsed;
        if (tool is { } t
            && title.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault() is { } label)
        {
            label.Text = AgentIsland.UI.Providers.ProviderIdentity.DisplayName(t);
        }
    }

    private static void FadeLogo(UIElement logo, bool visible)
    {
        var fade = new DoubleAnimation(visible ? 1 : 0, IslandAnimations.OpenMorphDuration)
        {
            EasingFunction = IslandAnimations.OpenMorph(),
        };
        logo.BeginAnimation(OpacityProperty, fade);
        logo.IsHitTestVisible = visible;
    }

    /// Pages + footer inside the expanded area; provider titles + plan chips
    /// in the top strip (visible only when expanded, exactly like the macOS
    /// PanelHeader living beside the notch).
    private void BuildExpandedChrome()
    {
        var pages = new PagedContent();
        System.Windows.Controls.Grid.SetRow(pages, 0);
        ExpandedContent.Children.Add(pages);

        var footer = new PanelFooter();
        System.Windows.Controls.Grid.SetRow(footer, 1);
        ExpandedContent.Children.Add(footer);

        // Titles live in the center column, hugging the logo tabs on each
        // side — the macOS PanelHeader arrangement.
        (_leftTitle, _leftChip) = MakeProviderTitle("Claude");
        _leftTitle.HorizontalAlignment = HorizontalAlignment.Left;
        _leftTitle.Margin = new Thickness(8, 0, 0, 0);
        System.Windows.Controls.Grid.SetColumn(_leftTitle, 2);
        _leftResetCards = new ResetCardChip { Margin = new Thickness(0, 0, 10, 0) };
        _leftResetCards.PopupClosed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!Silhouette.IsMouseOver && !_hovering && _model.State != IslandState.Compact)
            {
                SetState(IslandState.Compact);
            }
        });
        _leftTitle.Children.Insert(0, _leftResetCards);
        TopStrip.Children.Add(_leftTitle);

        (_rightTitle, _rightChip) = MakeProviderTitle("Codex");
        _rightTitle.HorizontalAlignment = HorizontalAlignment.Right;
        _rightTitle.Margin = new Thickness(0, 0, 8, 0);
        System.Windows.Controls.Grid.SetColumn(_rightTitle, 2);
        // Banked-reset count ("reset cards") — the escape hatches of the
        // weekly-only quota era. Always shown, ×0 included, in the dead
        // space left of the title; click for per-card expiry.
        _rightResetCards = new ResetCardChip { Margin = new Thickness(0, 0, 10, 0) };
        // The popup suppressed the hover-out collapse while it was up; when
        // it closes, run the deferred check — if the mouse has genuinely
        // left the island, fold now instead of hanging open forever.
        _rightResetCards.PopupClosed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!Silhouette.IsMouseOver && !_hovering && _model.State != IslandState.Compact)
            {
                SetState(IslandState.Compact);
            }
        });
        _rightTitle.Children.Insert(0, _rightResetCards);
        TopStrip.Children.Add(_rightTitle);

        System.ComponentModel.PropertyChangedEventHandler onPlanChips =
            (_, _) => Dispatcher.BeginInvoke(UpdatePlanChips);
        UsageStore.Shared.PropertyChanged += onPlanChips;
        _teardown.Add(() => UsageStore.Shared.PropertyChanged -= onPlanChips);
        AntigravityUsageStore.Shared.PropertyChanged += onPlanChips;
        _teardown.Add(() => AntigravityUsageStore.Shared.PropertyChanged -= onPlanChips);
        GrokUsageStore.Shared.PropertyChanged += onPlanChips;
        _teardown.Add(() => GrokUsageStore.Shared.PropertyChanged -= onPlanChips);
        CursorUsageStore.Shared.PropertyChanged += onPlanChips;
        _teardown.Add(() => CursorUsageStore.Shared.PropertyChanged -= onPlanChips);
        UpdatePlanChips();

        // Overview needs the taller panel (contribution grid); the size
        // morphs live when paging while expanded — and the persisted page
        // must seed the height at startup, or reopening on Overview squashes
        // the grid.
        ApplyPanelHeightForScreen();
        System.ComponentModel.PropertyChangedEventHandler onScreen = (_, args) =>
        {
            if (args.PropertyName != nameof(ScreenPref.Screen)) return;
            Dispatcher.BeginInvoke(() =>
            {
                ApplyPanelHeightForScreen();
                if (_model.State == IslandState.Expanded)
                {
                    AnimateSize(_model.Size, open: true);
                }
            });
        };
        ScreenPref.Shared.PropertyChanged += onScreen;
        _teardown.Add(() => ScreenPref.Shared.PropertyChanged -= onScreen);
    }

    private void ApplyPanelHeightForScreen() =>
        _model.ExpandedContentHeight = ScreenPref.Shared.Screen == IslandScreen.Overview
            ? IslandModel.OverviewContentHeight
            : IslandModel.UsageContentHeight;

    private static (System.Windows.Controls.StackPanel Panel, System.Windows.Controls.TextBlock Chip) MakeProviderTitle(string name)
    {
        var panel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = name,
            FontFamily = Charts.IslandFonts.Ui,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var chip = new System.Windows.Controls.TextBlock
        {
            FontFamily = Charts.IslandFonts.Mono,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.78)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chipHost = new System.Windows.Controls.Border
        {
            Child = chip,
            CornerRadius = new CornerRadius(3),
            Background = IslandColors.Brush(IslandColors.White(0.06)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.08)),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(chipHost);
        return (panel, chip);
    }

    private void UpdatePlanChips()
    {
        UpdateChip(_leftChip, _leftTool is { } l ? UsagePage.UsageFor(l.ToDisplayProvider()).Plan : null);
        UpdateChip(_rightChip, _rightTool is { } r ? UsagePage.UsageFor(r.ToDisplayProvider()).Plan : null);

        var store = UsageStore.Shared;
        var banked = (store.Codex.ResetCards ?? 0) > 0;

        if (_leftResetCards is not null)
        {
            var codexLeft = _leftTool == TriggerTool.Codex;
            _leftResetCards.Visibility = codexLeft && banked ? Visibility.Visible : Visibility.Collapsed;
            if (codexLeft && banked)
            {
                _leftResetCards.Update(store.Codex.ResetCards, store.Codex.ResetCardDetails);
            }
        }

        if (_rightResetCards is not null)
        {
            var codexRight = _rightTool == TriggerTool.Codex;
            _rightResetCards.Visibility = codexRight && banked ? Visibility.Visible : Visibility.Collapsed;
            if (codexRight && banked)
            {
                _rightResetCards.Update(store.Codex.ResetCards, store.Codex.ResetCardDetails);
            }
        }
    }

    private static void UpdateChip(System.Windows.Controls.TextBlock? chip, string? plan)
    {
        if (chip is null) return;
        var host = (System.Windows.Controls.Border)chip.Parent;
        if (string.IsNullOrEmpty(plan))
        {
            host.Visibility = Visibility.Collapsed;
        }
        else
        {
            host.Visibility = Visibility.Visible;
            chip.Text = plan.ToUpperInvariant();
        }
    }

    private bool IsFloating =>
        AgentIsland.Backend.Settings.IslandPositionStore.Shared.Placement == AgentIsland.Backend.Settings.IslandPlacement.Floating;

    private void PositionOnScreen()
    {
        var area = WorkAreaDip(AgentIsland.Backend.Settings.IslandTargetDisplayStore.Shared.Resolve());
        var store = AgentIsland.Backend.Settings.IslandPositionStore.Shared;
        if (store.Placement == AgentIsland.Backend.Settings.IslandPlacement.Floating)
        {
            var pt = store.FloatingPoint;
            if (pt is { } p)
            {
                // Clamp the VISIBLE silhouette (not the oversized
                // transparent canvas) so the island can be parked right
                // at a screen edge; the canvas simply overhangs off-screen.
                var silW = Silhouette.ActualWidth > 0 ? Silhouette.ActualWidth : 280;
                var silH = Silhouette.ActualHeight > 0 ? Silhouette.ActualHeight : IslandModel.SilhouetteHeight;
                var insetX = (Width - silW) / 2; // silhouette is centered in the canvas
                var minLeft = area.Left - insetX;
                var maxLeft = area.Right - silW - insetX;
                var maxTop = area.Bottom - silH;
                Left = Math.Clamp(p.X, minLeft, Math.Max(minLeft, maxLeft));
                Top = Math.Clamp(p.Y, area.Top, Math.Max(area.Top, maxTop));
            }
            else
            {
                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + 72;
            }
        }
        else
        {
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top;
        }
    }

    /// The chosen monitor's work area (taskbar excluded, so a top-docked
    /// taskbar pushes the island below it) in WPF units. WinForms screens
    /// report physical pixels; TransformFromDevice maps them into this
    /// window's DIP space.
    private Rect WorkAreaDip(System.Windows.Forms.Screen screen)
    {
        var area = screen.WorkingArea;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            var device = target.TransformFromDevice;
            return new Rect(
                device.Transform(new Point(area.Left, area.Top)),
                device.Transform(new Point(area.Right, area.Bottom)));
        }
        return SystemParameters.WorkArea;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(PositionOnScreen);

    /// The island must never stay gone (macOS 2.1.2 parity — the same
    /// belt-and-suspenders rule that fixed "the island randomly
    /// disappears" there). On Windows the vanish paths are z-order theft
    /// (a fullscreen or topmost app parks itself above; explorer restarts
    /// drop the band) and a stray Hide. A slow sweep re-asserts both.
    /// SetWindowPos with NOACTIVATE never steals focus, so the sweep is
    /// invisible when nothing was wrong.
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.DllImport(
        "user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport(
        "user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport(
        "user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(
        IntPtr hWnd, int nIndex, IntPtr value);

    [System.Runtime.InteropServices.DllImport(
        "user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private const uint SwpFrameChanged = 0x20;

    /// Set when the user hid the island through the tray toggle — the
    /// watchdog must never fight a deliberate hide.
    public bool DeliberatelyHidden;

    private void StartVisibilityWatchdog()
    {
        var sweep = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20),
        };
        sweep.Tick += (_, _) =>
        {
            if (!IsVisible && !DeliberatelyHidden)
            {
                Show();
                PositionOnScreen();
            }
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoActivate);
            }
        };
        sweep.Start();
        _teardown.Add(sweep.Stop);
    }

    /// Top bar sits flush against the screen edge, so only its bottom
    /// corners round; a floating island rounds all four.
    private CornerRadius ShapeRadius(double radius) => IsFloating
        ? new CornerRadius(radius)
        : new CornerRadius(0, 0, radius, radius);

    /// Hidden panel content parks 8px toward the bar strip so the expand
    /// reveal always slides down and away from it.
    private const double PanelRestY = -8;

    /// Re-shapes the silhouette corners for the current placement.
    private void ApplyEdgeLayout()
    {
        Silhouette.CornerRadius = ShapeRadius(_model.CornerRadius);
        Sweep.CornerRadius = ShapeRadius(_model.CornerRadius + 2);
        if (_model.State != IslandState.Expanded)
        {
            ContentSlide.BeginAnimation(TranslateTransform.YProperty, null);
            ContentSlide.Y = PanelRestY;
        }
    }

    // MARK: - State transitions

    private DispatcherTimer? _hoverIntent;

    private void OnSilhouetteMouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        UpdateHalo();
        if (_model.State != IslandState.Compact) return;
        // Hover intent: unlike the macOS notch, this island sits where the
        // cursor routinely passes straight THROUGH it (especially floating
        // placement mid-screen). Peeking on raw enter made every pass-over
        // pop the bar open and snap it shut. The halo still lights up
        // instantly above; the size morph waits until the cursor has
        // actually settled on the island.
        _hoverIntent?.Stop();
        var intent = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hoverIntent = intent;
        intent.Tick += (_, _) =>
        {
            intent.Stop();
            if (_hovering && _model.State == IslandState.Compact)
            {
                SetState(IslandState.Peek);
            }
        };
        intent.Start();
    }

    private void OnSilhouetteMouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        _hoverIntent?.Stop();
        UpdateHalo();
        // Pills fade first (~80ms), then the silhouette springs back.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            // The reset-card popup steals the mouse the instant it opens,
            // which reads as a MouseLeave here — folding the panel would
            // yank the popup shut mid-look. Hold the panel while it's up;
            // its Closed handler runs this collapse check again.
            if (_leftResetCards?.IsPopupOpen == true || _rightResetCards?.IsPopupOpen == true) return;
            if (!_hovering && _model.State != IslandState.Compact)
            {
                SetState(IslandState.Compact);
            }
        };
        if (_model.State == IslandState.Peek)
        {
            // With "always show usage" the percentages stay painted on the
            // compact bar, so nothing fades on the way out.
            FadePills(visible: AlwaysShowUsageStore.Shared.Enabled, delayMs: 0, seconds: 0.08);
        }
        delay.Start();
    }

    /// In Floating mode a left-press either drags the window (and persists
    /// the new spot) or, if it barely moved, counts as the click that
    /// expands. DragMove swallows the mouse-up, so we drive expand here and
    /// let OnSilhouetteClick bail for floating.
    private void OnSilhouetteMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (AgentIsland.Backend.Settings.IslandPositionStore.Shared.Placement != AgentIsland.Backend.Settings.IslandPlacement.Floating) return;
        if (_model.State != IslandState.Compact && _model.State != IslandState.Peek) return;
        var startLeft = Left;
        var startTop = Top;
        try { DragMove(); } catch { }
        var moved = Math.Abs(Left - startLeft) > 3 || Math.Abs(Top - startTop) > 3;
        if (moved)
        {
            AgentIsland.Backend.Settings.IslandPositionStore.Shared.SetFloatingPoint(Left, Top);
            // Settle into the clamped resting spot now, so it matches where a
            // later reposition (display change / relaunch) would place it.
            PositionOnScreen();
        }
        else if (_model.State is IslandState.Peek or IslandState.Compact)
        {
            SetState(IslandState.Expanded);
            Activate();
            Focus();
        }
        e.Handled = true;
    }

    /// Scripted glow/sweep verification: render the live visual tree (sweep +
    /// halo effects included) to a PNG, composited over a desktop-like grey so
    /// the aura reads the way it does on screen. Immune to window occlusion —
    /// a plain screen grab loses to whatever sits on top of the topmost bar.
    public void SaveVisualSnapshot(string path)
    {
        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            try
            {
                var w = (int)Math.Ceiling(RootHost.ActualWidth);
                var h = (int)Math.Ceiling(RootHost.ActualHeight);
                if (w <= 0 || h <= 0) return;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    // The light desktop the island actually floats over, so the
                    // glow spread shows against a real backdrop, not transparency.
                    dc.DrawRectangle(
                        new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
                        null, new Rect(0, 0, w, h));
                    dc.DrawRectangle(new VisualBrush(RootHost), null, new Rect(0, 0, w, h));
                }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = System.IO.File.Create(path);
                encoder.Save(stream);
            }
            catch
            {
            }
        };
        settle.Start();
    }

    /// Bring the island up and open it — the tray-icon launcher.
    public void PopUp()
    {
        Show();
        ApplyEdgeLayout();
        PositionOnScreen();
        if (_model.State != IslandState.Expanded) SetState(IslandState.Expanded);
        Activate();
        Focus();
    }

    private void OnSilhouetteClick(object sender, MouseButtonEventArgs e)
    {
        // Floating handles expand in the mouse-down path (DragMove consumes
        // the up), so ignore the click there to avoid a double expand.
        if (AgentIsland.Backend.Settings.IslandPositionStore.Shared.Placement == AgentIsland.Backend.Settings.IslandPlacement.Floating) return;
        if (_model.State is IslandState.Peek or IslandState.Compact)
        {
            SetState(IslandState.Expanded);
            // Take focus so the wheel and arrow keys page the carousel even
            // when Windows' hover-scroll setting is off.
            Activate();
            Focus();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_model.State != IslandState.Expanded) return;
        switch (e.Key)
        {
            case Key.Right:
                ScreenPref.Shared.ShowNext(1);
                e.Handled = true;
                break;
            case Key.Left:
                ScreenPref.Shared.ShowNext(-1);
                e.Handled = true;
                break;
        }
    }

    /// True while SetState mutates the model — its Size re-emit must not
    /// ALSO trigger the live-reflow path (SetState animates everything
    /// itself with the state-appropriate spring).
    private bool _stateDrivenResize;

    private void SetState(IslandState state)
    {
        var previous = _model.State;
        if (previous == state) return;
        _stateDrivenResize = true;
        _model.State = state;
        _stateDrivenResize = false;

        var open = state != IslandState.Compact
            && (previous == IslandState.Compact || state == IslandState.Expanded);
        ApplySoloSplit();
        AnimateSize(_model.Size, open);
        AnimatePillSlots(open);
        AnimateTabColumns(open);
        Silhouette.CornerRadius = ShapeRadius(_model.CornerRadius);
        Sweep.CornerRadius = ShapeRadius(_model.CornerRadius + 2);

        // Expanded panel gains the hairline stroke and grounding shadow of
        // the macOS GlowLayer; both drop on collapse.
        var expanded = state == IslandState.Expanded;
        Silhouette.BorderBrush = expanded ? IslandColors.Brush(IslandColors.White(0.12)) : null;
        Silhouette.BorderThickness = new Thickness(expanded ? 0.5 : 0);
        RootHost.Effect = expanded
            ? new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.5,
                // macOS radius 20 is a sigma; WPF's kernel-extent BlurRadius
                // needs ~3x for the same soft grounding falloff.
                BlurRadius = 60,
                ShadowDepth = 10,
                // Grounding shadow falls downward, away from the bar strip.
                Direction = 270,
            }
            : null;

        switch (state)
        {
            case IslandState.Peek:
                // Shape commits first, pills follow.
                FadePills(visible: true, delayMs: 60, seconds: 0.18);
                break;
            case IslandState.Expanded:
                ShowExpandedContent();
                // Opening the island refreshes usage when it has gone stale,
                // so the numbers are current the moment you look — gated by
                // the poll interval, so it never out-polls the schedule.
                UsageStore.Shared.RefreshIfStale();
                // Pills travel with the growing shape, then cross-fade out
                // after the expanded content has settled.
                FadePills(visible: false, delayMs: 250, seconds: 0.18);
                StartPanelHeartbeat();
                break;
            case IslandState.Compact:
            default:
                FadePills(visible: AlwaysShowUsageStore.Shared.Enabled, delayMs: 0, seconds: 0.08);
                HideExpandedContent();
                break;
        }
        if (state != IslandState.Expanded) StopPanelHeartbeat();
    }

    // Heartbeat failsafe for the black expanded panel (macOS IslandRootView):
    // expanded with invisible content is never a legal steady state. The
    // WPF choreography triggers the content fade in the same call as the
    // state flip, so the macOS timer races shouldn't exist here — but any
    // path that strands the panel dark (an animation clobbered elsewhere, a
    // visual rebuilt under an expanded model) gets healed within 0.6s
    // instead of reading as a crash. Runs only while expanded, so the idle
    // island keeps zero timers.
    private DispatcherTimer? _panelHeartbeat;
    private long _lastShowContentMs;

    private void StartPanelHeartbeat()
    {
        if (_panelHeartbeat is not null) return;
        _panelHeartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.6) };
        _panelHeartbeat.Tick += (_, _) =>
        {
            if (_model.State != IslandState.Expanded) return;
            // Give the entrance choreography (180ms delay + fade) room; only
            // an opacity still pinned near zero well after it ran is stuck.
            var settled = Environment.TickCount64 - _lastShowContentMs > 700;
            if (ExpandedContent.Visibility != Visibility.Visible
                || (ExpandedContent.Opacity < 0.01 && settled))
            {
                ShowExpandedContent();
            }
        };
        _panelHeartbeat.Start();
    }

    private void StopPanelHeartbeat()
    {
        _panelHeartbeat?.Stop();
        _panelHeartbeat = null;
    }

    private void AnimateSize(Size target, bool open)
    {
        var duration = open ? IslandAnimations.OpenMorphDuration : IslandAnimations.CloseMorphDuration;
        SpringEase ease = open ? IslandAnimations.OpenMorph() : IslandAnimations.CloseMorph();
        var width = new DoubleAnimation(target.Width, duration) { EasingFunction = ease };
        var height = new DoubleAnimation(target.Height, duration) { EasingFunction = ease };
        Silhouette.BeginAnimation(WidthProperty, width);
        Silhouette.BeginAnimation(HeightProperty, height);
    }

    private void ApplySizeInstant()
    {
        Silhouette.BeginAnimation(WidthProperty, null);
        Silhouette.BeginAnimation(HeightProperty, null);
        var size = _model.Size;
        Silhouette.Width = size.Width;
        Silhouette.Height = size.Height;
        Silhouette.CornerRadius = ShapeRadius(_model.CornerRadius);
        Sweep.CornerRadius = ShapeRadius(_model.CornerRadius + 2);
        SetColumnInstant(LeftPillColumn, PillSlotTarget());
        SetColumnInstant(RightPillColumn, PillSlotTarget());
        SetColumnInstant(LeftTabColumn, IslandModel.TabWidth);
        SetColumnInstant(RightTabColumn, IslandModel.TabWidth);
        ApplySoloSplit();
    }

    private static void SetColumnInstant(System.Windows.Controls.ColumnDefinition column, double width)
    {
        column.BeginAnimation(System.Windows.Controls.ColumnDefinition.WidthProperty, null);
        column.Width = new GridLength(width);
    }

    /// Morph the collapsed bar to the model's current metrics — silhouette,
    /// pill slots, and tab columns — with the open spring. Drives live
    /// layout changes (provider visibility, solo centering, placement, bar
    /// width) so the bar reflows instead of snapping.
    private void AnimateBarMetrics()
    {
        ApplySoloSplit();
        AnimateSize(_model.Size, open: true);
        AnimatePillSlots(open: true);
        AnimateTabColumns(open: true);
    }

    /// Pill slots exist in peek — and in compact when "always show usage"
    /// keeps the percentages painted; they collapse in expanded so the logo
    /// tabs glide out to the panel corners. In the solo split both slots
    /// stay open (one carries the logo, the other the number), keeping the
    /// bar symmetric around the notch.
    private double PillSlotTarget()
    {
        return _model.State switch
        {
            IslandState.Peek => IslandModel.PillSlotWidth,
            IslandState.Compact when AlwaysShowUsageStore.Shared.Enabled => IslandModel.PillSlotWidth,
            _ => 0,
        };
    }

    private void AnimatePillSlots(bool open)
    {
        AnimateColumn(LeftPillColumn, PillSlotTarget(), open);
        AnimateColumn(RightPillColumn, PillSlotTarget(), open);
    }

    private void AnimateTabColumns(bool open)
    {
        AnimateColumn(LeftTabColumn, IslandModel.TabWidth, open);
        AnimateColumn(RightTabColumn, IslandModel.TabWidth, open);
    }

    /// Solo split (macOS 9ee4219): with one subscription the collapsed bar
    /// keeps its full symmetric width and splits the flanks — the lone logo
    /// rides the OUTER slot on its provider's side (14pt off the edge, where
    /// a pill would sit) and its usage pill crosses to the opposite flank.
    /// With both providers (or in expanded, where logos glide to the panel
    /// corners) everything returns to its home column.
    private void ApplySoloSplit()
    {
        var leftSolo = _leftTool is not null && _rightTool is null;
        var rightSolo = _rightTool is not null && _leftTool is null;
        var slotted = _model.State == IslandState.Peek
            || (_model.State == IslandState.Compact && AlwaysShowUsageStore.Shared.Enabled);

        // Claude logo: home is column 1 (centered tab); solo puts it in the
        // left slot, tucked to the edge.
        if (leftSolo && slotted)
        {
            System.Windows.Controls.Grid.SetColumn(LeftLogo, 0);
            LeftLogo.HorizontalAlignment = HorizontalAlignment.Left;
            LeftLogo.Margin = new Thickness(14, 0, 0, 0);
        }
        else
        {
            System.Windows.Controls.Grid.SetColumn(LeftLogo, 1);
            LeftLogo.HorizontalAlignment = HorizontalAlignment.Center;
            LeftLogo.Margin = new Thickness(0);
        }

        if (rightSolo && slotted)
        {
            System.Windows.Controls.Grid.SetColumn(RightLogo, 4);
            RightLogo.HorizontalAlignment = HorizontalAlignment.Right;
            RightLogo.Margin = new Thickness(0, 0, 14, 0);
        }
        else
        {
            System.Windows.Controls.Grid.SetColumn(RightLogo, 3);
            RightLogo.HorizontalAlignment = HorizontalAlignment.Center;
            RightLogo.Margin = new Thickness(0);
        }

        // Pills: the solo provider's number crosses to the opposite flank;
        // duo keeps each pill outboard of its own logo.
        if (leftSolo)
        {
            System.Windows.Controls.Grid.SetColumn(LeftPill, 4);
            LeftPill.HorizontalAlignment = HorizontalAlignment.Right;
            LeftPill.Margin = new Thickness(6, 0, 14, 0);
            LeftPill.Mirrored = true;

            RightPill.Visibility = Visibility.Collapsed;
            RightPill.BeginAnimation(OpacityProperty, null);
            RightPill.Opacity = 0;
        }
        else
        {
            System.Windows.Controls.Grid.SetColumn(LeftPill, 0);
            LeftPill.HorizontalAlignment = HorizontalAlignment.Left;
            LeftPill.Margin = new Thickness(14, 0, 6, 0);
            LeftPill.Mirrored = false;
        }

        if (rightSolo)
        {
            System.Windows.Controls.Grid.SetColumn(RightPill, 0);
            RightPill.HorizontalAlignment = HorizontalAlignment.Left;
            RightPill.Margin = new Thickness(14, 0, 6, 0);
            RightPill.Mirrored = false;

            LeftPill.Visibility = Visibility.Collapsed;
            LeftPill.BeginAnimation(OpacityProperty, null);
            LeftPill.Opacity = 0;
        }
        else
        {
            System.Windows.Controls.Grid.SetColumn(RightPill, 4);
            RightPill.HorizontalAlignment = HorizontalAlignment.Right;
            RightPill.Margin = new Thickness(6, 0, 14, 0);
            RightPill.Mirrored = true;
        }
    }

    private static void AnimateColumn(System.Windows.Controls.ColumnDefinition column, double target, bool open)
    {
        var animation = new GridLengthAnimation
        {
            From = column.Width,
            To = new GridLength(target),
            Duration = open ? IslandAnimations.OpenMorphDuration : IslandAnimations.CloseMorphDuration,
            EasingFunction = open ? IslandAnimations.OpenMorph() : IslandAnimations.CloseMorph(),
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) => column.Width = new GridLength(target);
        column.BeginAnimation(System.Windows.Controls.ColumnDefinition.WidthProperty, animation);
    }

    private void FadePills(bool visible, int delayMs, double seconds)
    {
        var leftTarget = visible && _leftTool is not null ? 1.0 : 0.0;
        var rightTarget = visible && _rightTool is not null ? 1.0 : 0.0;

        if (_leftTool is not null)
        {
            LeftPill.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(leftTarget, new Duration(TimeSpan.FromSeconds(seconds)))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new QuadraticEase
                {
                    EasingMode = leftTarget > 0 ? EasingMode.EaseOut : EasingMode.EaseIn,
                },
            };
            LeftPill.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            LeftPill.BeginAnimation(OpacityProperty, null);
            LeftPill.Opacity = 0;
            LeftPill.Visibility = Visibility.Collapsed;
        }

        if (_rightTool is not null)
        {
            RightPill.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(rightTarget, new Duration(TimeSpan.FromSeconds(seconds)))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new QuadraticEase
                {
                    EasingMode = rightTarget > 0 ? EasingMode.EaseOut : EasingMode.EaseIn,
                },
            };
            RightPill.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            RightPill.BeginAnimation(OpacityProperty, null);
            RightPill.Opacity = 0;
            RightPill.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowExpandedContent()
    {
        _lastShowContentMs = Environment.TickCount64;
        ExpandedContent.Visibility = Visibility.Visible;
        SettingsGear.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(1, IslandAnimations.StrongEaseOutDuration)
        {
            BeginTime = TimeSpan.FromMilliseconds(180),
            EasingFunction = IslandAnimations.StrongEaseOut(),
        };
        var slide = new DoubleAnimation(0, IslandAnimations.StrongEaseOutDuration)
        {
            BeginTime = TimeSpan.FromMilliseconds(180),
            EasingFunction = IslandAnimations.StrongEaseOut(),
        };
        ExpandedContent.BeginAnimation(OpacityProperty, fade);
        ContentSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        _leftTitle?.BeginAnimation(OpacityProperty, fade.Clone());
        _rightTitle?.BeginAnimation(OpacityProperty, fade.Clone());
        // The titles are built hit-test-off so the invisible strip never eats
        // bar clicks; expanded they host a real control (the reset-card chip).
        if (_leftTitle is not null) _leftTitle.IsHitTestVisible = true;
        if (_rightTitle is not null) _rightTitle.IsHitTestVisible = true;
        SettingsGear.BeginAnimation(OpacityProperty, fade.Clone());
    }

    private void HideExpandedContent()
    {
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromSeconds(0.12)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            if (_model.State == IslandState.Compact)
            {
                ExpandedContent.Visibility = Visibility.Collapsed;
                SettingsGear.Visibility = Visibility.Collapsed;
                ContentSlide.BeginAnimation(TranslateTransform.YProperty, null);
                ContentSlide.Y = PanelRestY;
            }
        };
        ExpandedContent.BeginAnimation(OpacityProperty, fade);
        _leftTitle?.BeginAnimation(OpacityProperty, fade.Clone());
        _rightTitle?.BeginAnimation(OpacityProperty, fade.Clone());
        if (_leftTitle is not null) _leftTitle.IsHitTestVisible = false;
        if (_rightTitle is not null) _rightTitle.IsHitTestVisible = false;
        SettingsGear.BeginAnimation(OpacityProperty, fade.Clone());
    }

    private void OnSettingsGearClick(object sender, MouseButtonEventArgs e)
    {
        SettingsWindow.Open();
        e.Handled = true;
    }

    private void OnSettingsGearEnter(object sender, MouseEventArgs e) =>
        SettingsGear.Foreground = IslandColors.Brush(IslandColors.White(0.9));

    private void OnSettingsGearLeave(object sender, MouseEventArgs e) =>
        SettingsGear.Foreground = IslandColors.Brush(IslandColors.White(0.45));

    // MARK: - Live state visuals

    private void UpdateActivityVisuals()
    {
        var monitor = ActivityMonitor.Shared;
        if (_leftTool is { } l) LeftLogo.SetState(monitor.StateFor(l));
        if (_rightTool is { } r) RightLogo.SetState(monitor.StateFor(r));
        UpdateHalo();
    }

    private enum HaloMode
    {
        Rest,
        WarningTint,
        CriticalTint,
        AttentionSteady,
        AttentionPulse,
    }

    private HaloMode _haloMode = HaloMode.Rest;
    private readonly RotateTransform _sweepRotate = new() { CenterX = 0.5, CenterY = 0.5 };
    private IReadOnlyList<Color> _sweepPalette = Array.Empty<Color>();
    private IReadOnlyList<Color> _sweepRightPalette = Array.Empty<Color>();
    private bool _isDualSweep;
    private bool _sweepActive;
    private bool _sweepSpinning;

    /// Stalled/rate-limited pulse the halo red (opacity and radius breathe
    /// together, macOS GlowLayer numbers); auth-required holds a static red
    /// — a login can pend for hours and endless blinking reads as a crash;
    /// threshold alerts hold a sustained amber/red tint; at rest, Vivid keeps
    /// the ambient aura in the chosen glow color, FollowModel dynamically follows
    /// the active AI, and Calm keeps nothing at all.
    /// Interface scale (macOS 1f97e4d): a LayoutTransform on the canvas
    /// magnifies every layout constant at once, and the window grows with
    /// it so nothing clips. The positioning math already keys on the
    /// window's Width, so centering holds at any scale.
    private void ApplyInterfaceScale()
    {
        var scale = AgentIsland.Backend.Settings.IslandScaleStore.Shared.Scale;
        RootHost.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
            ? null
            : new ScaleTransform(scale, scale);
        Width = 900 * scale;
        Height = 360 * scale;
    }

    private static bool AttentionShown()
    {
        var monitor = ActivityMonitor.Shared;
        return AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.Slots
            .Any(provider => monitor.StateFor(provider.ToTriggerTool()).IsAttentionState());
    }

    private void UpdateHalo()
    {
        var monitor = ActivityMonitor.Shared;
        var pulsing = AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.Slots
            .Any(provider => monitor.StateFor(provider.ToTriggerTool()).PulsesAttention());
        var attention = AttentionShown();
        var severity = AgentIsland.Backend.Settings.AlertEngine.Shared.Severity;
        var mode = pulsing
            ? HaloMode.AttentionPulse
            : attention
                ? HaloMode.AttentionSteady
                : severity switch
                {
                    AgentIsland.Backend.Settings.AlertSeverity.Critical => HaloMode.CriticalTint,
                    AgentIsland.Backend.Settings.AlertSeverity.Warning => HaloMode.WarningTint,
                    _ => HaloMode.Rest,
                };
        if (mode != _haloMode)
        {
            _haloMode = mode;
            Halo.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
            Halo.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
            switch (mode)
            {
                case HaloMode.AttentionPulse:
                    Halo.Color = IslandColors.AlertRed;
                    var half = IslandAnimations.AttentionPulseDuration.TimeSpan;
                    // Opacity only, frame-capped: animating BlurRadius
                    // re-runs the gaussian per frame on the CPU, and every
                    // frame recomposites the whole layered window. The
                    // brightness swing carries the pulse.
                    var strength = new DoubleAnimation(0.3, 0.9, new Duration(half))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    };
                    Timeline.SetDesiredFrameRate(strength, 24);
                    Halo.BlurRadius = 54;
                    Halo.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, strength);
                    break;
                case HaloMode.AttentionSteady:
                    // authRequired: red, steady 0.55 — attention without the
                    // pulse (macOS GlowLayer).
                    Halo.Color = IslandColors.AlertRed;
                    Halo.Opacity = 0.55;
                    Halo.BlurRadius = 42;
                    break;
                case HaloMode.CriticalTint:
                    Halo.Color = IslandColors.AlertRed;
                    Halo.Opacity = 0.35;
                    Halo.BlurRadius = 42;
                    break;
                case HaloMode.WarningTint:
                    Halo.Color = IslandColors.AlertAmber;
                    Halo.Opacity = 0.35;
                    Halo.BlurRadius = 42;
                    break;
                case HaloMode.Rest:
                default:
                    Halo.Opacity = 0.35;
                    Halo.BlurRadius = 42;
                    break;
            }
        }
        // At rest the halo IS the ambient light: Vivid paints it in the
        // chosen glow color, FollowModel paints it in the active model's color,
        // Calm turns it fully off. EffectiveEnabled folds in the battery saver.
        if (_haloMode == HaloMode.Rest)
        {
            if (AgentIsland.Backend.Settings.LowPowerModeStore.Shared.Mode == AgentIsland.Backend.Settings.VisualMode.FollowModel)
            {
                var leftWorking = _leftTool is { } lt && monitor.StateFor(lt) == ActivityState.Working;
                var rightWorking = _rightTool is { } rt && monitor.StateFor(rt) == ActivityState.Working;

                if (leftWorking && rightWorking && _leftTool is { } l1 && _rightTool is { } r1)
                {
                    var c1 = AgentIsland.UI.Providers.ProviderIdentity.StreamColor(l1);
                    var c2 = AgentIsland.UI.Providers.ProviderIdentity.StreamColor(r1);
                    Halo.Color = Color.FromRgb(
                        (byte)((c1.R + c2.R) / 2),
                        (byte)((c1.G + c2.G) / 2),
                        (byte)((c1.B + c2.B) / 2));
                }
                else if (leftWorking && _leftTool is { } l2)
                {
                    Halo.Color = AgentIsland.UI.Providers.ProviderIdentity.StreamColor(l2);
                }
                else if (rightWorking && _rightTool is { } r2)
                {
                    Halo.Color = AgentIsland.UI.Providers.ProviderIdentity.StreamColor(r2);
                }
                else
                {
                    Halo.Color = _leftTool is { } l
                        ? AgentIsland.UI.Providers.ProviderIdentity.StreamColor(l)
                        : _rightTool is { } r
                            ? AgentIsland.UI.Providers.ProviderIdentity.StreamColor(r)
                            : AgentIsland.Backend.Settings.GlowColorStore.Shared.Color;
                }
            }
            else
            {
                Halo.Color = AgentIsland.Backend.Settings.GlowColorStore.Shared.Color;
            }
            Halo.Opacity = AgentIsland.Backend.Settings.LowPowerModeStore.Shared.EffectiveEnabled ? 0 : 0.35;
        }
        UpdateSweep();
    }

    /// The orbit sweep hugging the island edge. Vivid keeps it alive
    /// continuously in the glow color (alert tints override); FollowModel dynamically
    /// adopts the active model palette(s); Calm shows no ambient light.
    private void UpdateSweep()
    {
        var attention = AttentionShown();
        var alertTint = attention
            ? IslandColors.AlertRed
            : AgentIsland.Backend.Settings.AlertEngine.Shared.Severity switch
            {
                AgentIsland.Backend.Settings.AlertSeverity.Critical => IslandColors.AlertRed,
                AgentIsland.Backend.Settings.AlertSeverity.Warning => IslandColors.AlertAmber,
                _ => (Color?)null,
            };

        var active = !AgentIsland.Backend.Settings.LowPowerModeStore.Shared.EffectiveEnabled;
        if (!active)
        {
            if (_sweepActive)
            {
                _sweepActive = false;
                Sweep.Visibility = Visibility.Collapsed;
                _sweepTimer?.Stop();
                _sweepSpinning = false;
            }
            return;
        }

        var monitor = ActivityMonitor.Shared;
        var leftState = _leftTool is { } lt ? monitor.StateFor(lt) : ActivityState.Idle;
        var rightState = _rightTool is { } rt ? monitor.StateFor(rt) : ActivityState.Idle;

        var (isDual, palette, rightPalette) = ResolveSweepPalettes(
            alertTint,
            AgentIsland.Backend.Settings.LowPowerModeStore.Shared.Mode,
            AgentIsland.Backend.Settings.GlowColorStore.Shared.Color,
            _leftTool,
            leftState,
            _rightTool,
            rightState);

        if (active == _sweepActive
            && isDual == _isDualSweep
            && SamePalette(palette, _sweepPalette)
            && (!isDual || SamePalette(rightPalette, _sweepRightPalette)))
        {
            return;
        }

        _sweepActive = active;
        _isDualSweep = isDual;
        _sweepPalette = palette;
        _sweepRightPalette = rightPalette;

        Sweep.Visibility = Visibility.Visible;
        Sweep.BorderBrush = isDual
            ? ConicSweepBrush.MakeDual(palette, rightPalette, _sweepRotate)
            : ConicSweepBrush.Make(palette, _sweepRotate);

        if (!_sweepSpinning)
        {
            _sweepSpinning = true;
            // 100°/s like the macOS TimelineView sweep — but stepped at
            // 15fps by a timer, not a smooth 60fps animation. Every frame
            // recomposites the whole layered window in software (measured:
            // the smooth spin held 47% of a core); at 15 steps/s the comet
            // is a soft blur whose 6.7° hops read as motion, and the cost
            // drops to roughly a quarter. macOS spins free on Metal.
            if (_sweepTimer is null)
            {
                _sweepTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(66),
                };
                _sweepTimer.Tick += (_, _) =>
                    _sweepRotate.Angle = (_sweepRotate.Angle + 6.67) % 360;
            }
            _sweepTimer.Start();
        }
    }

    private static bool SamePalette(IReadOnlyList<Color> first, IReadOnlyList<Color> second)
    {
        if (first.Count != second.Count) return false;
        for (var i = 0; i < first.Count; i++)
        {
            if (first[i] != second[i]) return false;
        }

        return true;
    }

    internal static (bool IsDual, IReadOnlyList<Color> LeftPalette, IReadOnlyList<Color> RightPalette) ResolveSweepPalettes(
        Color? alertTint,
        AgentIsland.Backend.Settings.VisualMode visualMode,
        Color fallbackGlowColor,
        TriggerTool? leftTool,
        ActivityState leftState,
        TriggerTool? rightTool,
        ActivityState rightState)
    {
        bool isDual = false;
        IReadOnlyList<Color> palette = new[] { fallbackGlowColor };
        IReadOnlyList<Color> rightPalette = palette;

        if (alertTint is { } at)
        {
            // Alerts intentionally collapse to one sustained warning colour.
            palette = new[] { at };
            rightPalette = palette;
        }
        else if (visualMode == AgentIsland.Backend.Settings.VisualMode.FollowModel)
        {
            var leftWorking = leftTool is not null && leftState == ActivityState.Working;
            var rightWorking = rightTool is not null && rightState == ActivityState.Working;

            if (leftWorking && rightWorking && leftTool is { } l1 && rightTool is { } r1)
            {
                isDual = true;
                palette = AgentIsland.UI.Providers.ProviderIdentity.StreamPalette(l1);
                rightPalette = AgentIsland.UI.Providers.ProviderIdentity.StreamPalette(r1);
            }
            else if (leftWorking && leftTool is { } l2)
            {
                palette = AgentIsland.UI.Providers.ProviderIdentity.StreamPalette(l2);
                rightPalette = palette;
            }
            else if (rightWorking && rightTool is { } r2)
            {
                palette = AgentIsland.UI.Providers.ProviderIdentity.StreamPalette(r2);
                rightPalette = palette;
            }
            else if (leftTool is { } lt && rightTool is { } rt)
            {
                isDual = true;
                palette = AgentIsland.UI.Providers.ProviderIdentity.StreamPalette(lt);
                rightPalette = AgentIsland.UI.Providers.ProviderIdentity.StreamPalette(rt);
            }
            else if (leftTool is { } l)
            {
                palette = AgentIsland.UI.Providers.ProviderIdentity.StreamPalette(l);
                rightPalette = palette;
            }
            else if (rightTool is { } r)
            {
                palette = AgentIsland.UI.Providers.ProviderIdentity.StreamPalette(r);
                rightPalette = palette;
            }
        }

        return (isDual, palette, rightPalette);
    }

    private DispatcherTimer? _sweepTimer;

    private static bool IsToolLoading(TriggerTool tool) => tool switch
    {
        TriggerTool.Claude or TriggerTool.Codex => UsageStore.Shared.Loading,
        TriggerTool.Antigravity => AntigravityUsageStore.Shared.Loading,
        TriggerTool.Grok => GrokUsageStore.Shared.Loading,
        TriggerTool.Cursor => CursorUsageStore.Shared.Loading,
        _ => false,
    };

    private void UpdatePills()
    {
        var engine = AgentIsland.Backend.Settings.AlertEngine.Shared;
        if (_leftTool is { } leftTool)
        {
            LeftPill.Update(
                UsagePage.UsageFor(leftTool.ToDisplayProvider()).FiveHour,
                IsToolLoading(leftTool),
                engine.SeverityFor(leftTool));
        }
        else
        {
            LeftPill.Inlines.Clear();
        }

        if (_rightTool is { } rightTool)
        {
            RightPill.Update(
                UsagePage.UsageFor(rightTool.ToDisplayProvider()).FiveHour,
                IsToolLoading(rightTool),
                engine.SeverityFor(rightTool));
        }
        else
        {
            RightPill.Inlines.Clear();
        }

        // In compact, the pills normally hide. "Always show usage" keeps the
        // visible providers' 5h percent painted on the bare silhouette. A
        // finished FadePills animation holds the opacity, so detach it
        // before assigning or the value silently never lands.
        var alwaysShow = AlwaysShowUsageStore.Shared.Enabled && _model.State == IslandState.Compact;
        if (_model.State == IslandState.Peek || alwaysShow)
        {
            LeftPill.BeginAnimation(OpacityProperty, null);
            RightPill.BeginAnimation(OpacityProperty, null);
            LeftPill.Opacity = _leftTool is not null ? 1 : 0;
            RightPill.Opacity = _rightTool is not null ? 1 : 0;
            LeftPill.Visibility = _leftTool is not null ? Visibility.Visible : Visibility.Collapsed;
            RightPill.Visibility = _rightTool is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (_model.State == IslandState.Compact)
        {
            LeftPill.BeginAnimation(OpacityProperty, null);
            RightPill.BeginAnimation(OpacityProperty, null);
            LeftPill.Opacity = 0;
            RightPill.Opacity = 0;
            if (_leftTool is null) LeftPill.Visibility = Visibility.Collapsed;
            if (_rightTool is null) RightPill.Visibility = Visibility.Collapsed;
        }
    }

    /// First threshold crossing inside a reset window auto-peeks the pills
    /// for ~4s — the ambient nudge from the macOS design.
    private void HandleAlertPulse()
    {
        if (AgentIsland.Backend.Settings.AlertEngine.Shared.Pulse is null) return;
        AgentIsland.Backend.Settings.AlertEngine.Shared.ClearPulse();
        if (_model.State != IslandState.Compact) return;
        SetState(IslandState.Peek);
        var collapse = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        collapse.Tick += (_, _) =>
        {
            collapse.Stop();
            if (!_hovering && _model.State == IslandState.Peek)
            {
                SetState(IslandState.Compact);
            }
        };
        collapse.Start();
    }
}
