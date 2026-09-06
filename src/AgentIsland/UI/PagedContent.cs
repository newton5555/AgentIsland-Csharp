using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// Horizontal page carousel. Pages sit side by side on a canvas track that
/// slides with the pageSwipe curve; edge-clamped, no wrap-around. Mouse
/// drag, mouse wheel, and the footer dots drive paging on Windows, standing
/// in for the macOS trackpad swipe.
public sealed class PagedContent : Grid
{
    private readonly Canvas _track = new();
    private readonly TranslateTransform _slide = new();
    private readonly List<(IslandScreen Screen, FrameworkElement View)> _pages = new();
    private bool _pressed;
    private bool _dragging;
    private Point _dragOrigin;
    private double _dragOriginX;

    private readonly ScreenPref _screenPref;

    public PagedContent() : this(null) { }

    public PagedContent(ScreenPref? screenPref = null)
    {
        _screenPref = screenPref ?? (App.Instance?.Services?.GetService(typeof(ScreenPref)) as ScreenPref) ?? new ScreenPref();

        ClipToBounds = true;
        Background = Brushes.Transparent;
        _track.RenderTransform = _slide;
        Children.Add(_track);
        BuildPages();
        SizeChanged += (_, _) => Relayout(animate: false);
        _screenPref.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ScreenPref.Screen))
            {
                Dispatcher.BeginInvoke(() => Relayout(animate: true));
            }
            else if (args.PropertyName == nameof(ScreenPref.VisibleScreens))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    BuildPages();
                    Relayout(animate: false);
                });
            }
        };
        MouseWheel += OnWheel;
        // Drag-to-swipe. Preview events so a press anywhere in a page starts
        // a potential drag; nothing is hijacked until clear horizontal
        // intent, so buttons still click and lists still scroll vertically.
        PreviewMouseLeftButtonDown += OnDragPress;
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragRelease;
        LostMouseCapture += (_, _) => { if (_dragging) SettleDrag(); };
    }

    private void OnDragPress(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_pages.Count < 2 || ActualWidth <= 0) return;
        _pressed = true;
        _dragging = false;
        _dragOrigin = e.GetPosition(this);
    }

    private void OnDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_pressed) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _dragOrigin.X;
        var dy = pos.Y - _dragOrigin.Y;
        if (!_dragging)
        {
            if (Math.Abs(dx) < 14 || Math.Abs(dx) < Math.Abs(dy) * 1.2) return;
            // Horizontal intent confirmed: take over from wherever the track
            // currently sits (mid-animation included) and re-anchor there.
            _dragOriginX = _slide.X;
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = _dragOriginX;
            _dragOrigin = pos;
            _dragging = true;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        var width = ActualWidth;
        var raw = _dragOriginX + (pos.X - _dragOrigin.X);
        var min = -(_pages.Count - 1) * width;
        // Rubber-band past the first/last page instead of hard-stopping.
        if (raw > 0) raw /= 3;
        else if (raw < min) raw = min + (raw - min) / 3;
        _slide.X = raw;
        e.Handled = true;
    }

    private void OnDragRelease(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var wasDragging = _dragging;
        _pressed = false;
        if (!wasDragging) return;
        SettleDrag();
        e.Handled = true;
    }

    /// Chooses the landing page after a drag: nearest page, nudged one step
    /// in the drag direction when the displacement passes 18% of a page.
    private void SettleDrag()
    {
        _dragging = false;
        _pressed = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        var width = ActualWidth;
        if (width <= 0 || _pages.Count == 0) return;

        var originIndex = (int)Math.Clamp(Math.Round(-_dragOriginX / width), 0, _pages.Count - 1);
        var displacement = _slide.X - -originIndex * width;
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_LAYOUTLOG") == "1")
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AgentIsland.Windows.IslandPaths.AppSupportDir, "drag.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} settle originX={_dragOriginX:F0} slideX={_slide.X:F0} " +
                    $"width={width:F0} originIndex={originIndex} displacement={displacement:F0} pages={_pages.Count}\n");
            }
            catch
            {
            }
        }
        var target = originIndex;
        if (Math.Abs(displacement) > width * 0.18)
        {
            target = originIndex + (displacement < 0 ? 1 : -1);
        }
        target = Math.Clamp(target, 0, _pages.Count - 1);

        var screen = _pages[target].Screen;
        if (_screenPref.Screen == screen)
        {
            Relayout(animate: true);
        }
        else
        {
            _screenPref.Screen = screen;
        }
    }

    private void BuildPages()
    {
        _track.Children.Clear();
        _pages.Clear();
        foreach (var screen in _screenPref.VisibleScreens)
        {
            FrameworkElement view = screen switch
            {
                IslandScreen.Usage => new UsagePage(),
                IslandScreen.Cost => new CostPage(),
                IslandScreen.Overview => new OverviewPage(),
                _ => new PlaceholderPage(screen),
            };
            _pages.Add((screen, view));
            _track.Children.Add(view);
        }
    }

    private void Relayout(bool animate)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        for (var i = 0; i < _pages.Count; i++)
        {
            var view = _pages[i].View;
            // Margin on a Canvas child offsets it without shrinking an
            // explicit Width — the page would shift right and clip. Pages
            // must own their insets as Padding; enforce margin-free roots.
            view.Margin = new Thickness(0);
            view.Width = width;
            view.Height = height;
            // Pages sit edge to edge; clip each one so nothing bleeds
            // through the seam while a neighbor is showing.
            view.ClipToBounds = true;
            Canvas.SetLeft(view, i * width);
            Canvas.SetTop(view, 0);
        }
        var index = _pages.FindIndex(p => p.Screen == _screenPref.Screen);
        if (index < 0) index = 0;
        var target = -index * width;
        if (!animate)
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = target;
            return;
        }
        var slide = new DoubleAnimation(target, IslandAnimations.PageSwipeDuration)
        {
            EasingFunction = IslandAnimations.PageSwipe(),
        };
        _slide.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    // Last wheel page flip; debounces ticks to one page per notch (macOS
    // scrollWheel: an accelerated flick must not skip across every screen).
    private long _lastWheelPageMs;

    private void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        var now = Environment.TickCount64;
        if (now - _lastWheelPageMs < 250) { e.Handled = true; return; }
        _lastWheelPageMs = now;
        // Wheel down (negative delta) advances, matching macOS.
        _screenPref.ShowNext(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }
}

/// Stand-in for pages whose milestones haven't landed yet.
public sealed class PlaceholderPage : Grid
{
    public PlaceholderPage(IslandScreen screen)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(new TextBlock
        {
            Text = AgentIsland.UI.Localization.L10n.Tr(screen.ToString()),
            FontFamily = IslandFonts.Ui,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.75)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = AgentIsland.UI.Localization.L10n.Tr("coming soon"),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(0.35)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });
        Children.Add(stack);
    }
}
