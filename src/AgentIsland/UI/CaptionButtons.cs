using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// Minimize / maximize-restore / close, embedded in a window's top-right
/// corner — the in-page caption strip used instead of a system title bar
/// (settings window, turn alarm).
public sealed partial class CaptionButtons : UserControl
{
    private Window? _targetWindow;

    public Window? TargetWindow
    {
        get => _targetWindow;
        set
        {
            if (_targetWindow != null)
            {
                _targetWindow.StateChanged -= OnWindowStateChanged;
            }
            _targetWindow = value;
            if (_targetWindow != null)
            {
                _targetWindow.StateChanged += OnWindowStateChanged;
                UpdateMaxGlyph();
            }
        }
    }

    public Action? CloseAction { get; set; }

    /// onClose lets an alarm route its close through Acknowledge();
    /// defaults to Window.Close().
    public static UIElement Build(Window window, Action? onClose = null)
    {
        return new CaptionButtons
        {
            TargetWindow = window,
            CloseAction = onClose,
        };
    }

    public CaptionButtons()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (TargetWindow == null)
            {
                TargetWindow = Window.GetWindow(this);
            }
        };

        AttachButton(MinButton, MinText, false, () =>
        {
            var win = TargetWindow ?? Window.GetWindow(this);
            if (win != null) win.WindowState = WindowState.Minimized;
        });

        AttachButton(MaxButton, MaxText, false, () =>
        {
            var win = TargetWindow ?? Window.GetWindow(this);
            if (win != null)
            {
                win.WindowState = win.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                UpdateMaxGlyph();
            }
        });

        AttachButton(CloseButton, CloseText, true, () =>
        {
            if (CloseAction != null)
            {
                CloseAction();
            }
            else
            {
                var win = TargetWindow ?? Window.GetWindow(this);
                win?.Close();
            }
        });
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateMaxGlyph();

    private void UpdateMaxGlyph()
    {
        var win = TargetWindow ?? Window.GetWindow(this);
        if (win != null && MaxText != null)
        {
            MaxText.Text = win.WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
    }

    private static void AttachButton(Border host, TextBlock glyphText, bool destructive, Action click)
    {
        host.MouseEnter += (_, _) =>
        {
            host.Background = destructive
                ? IslandColors.Brush(Color.FromRgb(0xC4, 0x2B, 0x1C))
                : IslandColors.Brush(IslandColors.White(0.08));
            glyphText.Foreground = Brushes.White;
        };
        host.MouseLeave += (_, _) =>
        {
            host.Background = Brushes.Transparent;
            glyphText.Foreground = IslandColors.Brush(IslandColors.White(0.55));
        };
        host.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            click();
        };
        // Swallow press so borderless windows don't treat it as drag
        host.MouseLeftButtonDown += (_, args) => args.Handled = true;
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(host, true);
    }
}
