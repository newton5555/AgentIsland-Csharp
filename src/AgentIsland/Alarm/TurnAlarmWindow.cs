using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AgentIsland.Core;
using AgentIsland.Model;
using AgentIsland.UI;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.Alarm;

/// The foreground "It's your turn" alarm. Dark 520x520 panel, glow-pulsing
/// provider mark, headline, session line, metadata row, and the two
/// actions. Topmost so it surfaces over full-screen work; Esc dismisses.
public sealed class TurnAlarmWindow : Window
{
    public TriggerTool Provider { get; }
    public ActivityMonitor.ActiveThread? Thread { get; }
    public string DeliveryKey { get; }

    private readonly TurnAlarmKind _kind;
    private readonly TurnAlarmSoundLooper _sound = new();
    public event Action<TurnAlarmWindow>? Dismissed;

    private System.Windows.Controls.Button? _openButton;
    private TextBlock? _error;
    private bool _opening;

    private bool IsExhausted => _kind is TurnAlarmKind.QuotaExhausted;

    public TurnAlarmWindow(
        TriggerTool provider,
        ActivityMonitor.ActiveThread? thread,
        string deliveryKey,
        TurnAlarmKind? kind = null)
    {
        Provider = provider;
        Thread = thread;
        DeliveryKey = deliveryKey;
        _kind = kind ?? new TurnAlarmKind.YourTurn();

        Width = 520;
        Height = 520;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = Headline();
        System.Windows.Media.TextOptions.SetTextFormattingMode(
            this, System.Windows.Media.TextFormattingMode.Display);

        var tint = IslandColors.For(provider);
        var root = BuildContent(tint);
        Content = root;
        IslandMotion.AnimateEntrance(this, root);
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) Acknowledge();
        };
        // The card IS the jump: press-and-move drags the window, a plain
        // click opens the thread — same click-vs-drag split as the floating
        // island. Buttons swallow their own mouse-down, so this only fires
        // on the card body. A quota alarm has no thread to open, so its card
        // only drags.
        MouseLeftButtonDown += async (_, _) =>
        {
            if (IsExhausted)
            {
                DragMoveSafe();
                return;
            }
            var startLeft = Left;
            var startTop = Top;
            DragMoveSafe();
            var moved = Math.Abs(Left - startLeft) > 3 || Math.Abs(Top - startTop) > 3;
            if (!moved) await OpenThread();
        };
        if (!IsExhausted) Cursor = Cursors.Hand;
        Loaded += (_, _) => _sound.Start();
        // Every close path must notify the controller — including an
        // OS-initiated close (Alt+F4, taskbar right-click → Close), which
        // bypasses Acknowledge/DismissSilently. Without this the controller
        // keeps _current pointing at a dead window and the whole alarm queue
        // stalls: no further turn ever surfaces. Dismissed is idempotent
        // (the handler detaches itself), so a normal dismiss won't double-fire.
        Closed += (_, _) =>
        {
            _sound.Stop();
            Dismissed?.Invoke(this);
        };
    }

    private void DragMoveSafe()
    {
        try { DragMove(); } catch { }
    }

    private FrameworkElement BuildContent(Color tint)
    {
        var root = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = IslandColors.Brush(IslandColors.AlarmBackground),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.07)),
            BorderThickness = new Thickness(1),
        };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(40, 24, 40, 24),
        };
        var shell = new Grid();
        shell.Children.Add(BuildBackdrop(tint));
        shell.Children.Add(stack);
        // Embedded caption strip; closing counts as acknowledged so the
        // alarm doesn't redeliver.
        var captions = (FrameworkElement)UI.CaptionButtons.Build(this, Acknowledge);
        captions.Margin = new Thickness(0, 8, 8, 0);
        shell.Children.Add(captions);
        root.Child = shell;

        stack.Children.Add(BuildMarkCluster(tint));

        stack.Children.Add(new TextBlock
        {
            Text = Headline(),
            FontFamily = IslandFonts.Ui,
            FontSize = 36,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = WaitingTitle(),
            FontFamily = IslandFonts.Ui,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(tint),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420,
        });

        stack.Children.Add(new TextBlock
        {
            Text = DetailText(),
            FontFamily = IslandFonts.Ui,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.66)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });

        if (!IsExhausted && AgentReminderStore.Shared.ShowSessionDetails && Thread is { } thread)
        {
            stack.Children.Add(BuildMetadata(thread, tint));
        }

        _error = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.AlertRed),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 396,
            Margin = new Thickness(0, 16, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        // A quota alarm has no thread to open — acknowledge is its only action.
        if (!IsExhausted)
        {
            var open = MakePrimaryButton(Localization.L10n.Tr("Open thread"), tint);
            open.Margin = new Thickness(0, 24, 0, 0);
            open.Click += async (_, _) => await OpenThread();
            _openButton = open;
            stack.Children.Add(open);
        }

        var gotIt = MakeSecondaryButton(Localization.L10n.Tr("I know"));
        gotIt.Margin = new Thickness(0, IsExhausted ? 24 : 12, 0, 0);
        gotIt.Click += (_, _) => Acknowledge();
        stack.Children.Add(gotIt);
        stack.Children.Add(_error);

        return root;
    }

    // MARK: - Kind-dependent copy (the macOS TurnAlarmView computed properties)

    private string Headline() => IsExhausted
        ? Localization.L10n.Tr("Out of quota")
        : Localization.L10n.Tr("It's your turn");

    private string WaitingTitle()
    {
        if (_kind is TurnAlarmKind.QuotaExhausted quota)
        {
            var windowName = quota.Window == QuotaWindowKind.FiveHour
                ? Localization.L10n.Tr("5-hour limit")
                : Localization.L10n.Tr("Weekly limit");
            return $"{Provider.Display()} · {windowName}";
        }
        var sessionLabel = Thread?.Label is { Length: > 0 } label ? label : Provider.Display();
        return Localization.L10n.TrFormat("{0} is waiting", sessionLabel);
    }

    private string DetailText()
    {
        if (_kind is TurnAlarmKind.QuotaExhausted quota)
        {
            if (quota.ResetAt is not { } resetAt)
            {
                return Localization.L10n.Tr("You're rate-limited for now.");
            }
            return ResetDetail(resetAt);
        }
        return Localization.L10n.Tr("The thread finished. Come back and reply.");
    }

    /// "Resets at 15:55 (~2h)" — absolute local time plus a coarse relative
    /// gap, the macOS resetDetail.
    private static string ResetDetail(DateTimeOffset resetAt)
    {
        var culture = Localization.L10n.IsChinese
            ? System.Globalization.CultureInfo.GetCultureInfo("zh-CN")
            : System.Globalization.CultureInfo.CurrentCulture;
        var clock = resetAt.ToLocalTime().ToString("t", culture);
        var minutes = Math.Max(1, (int)Math.Round((resetAt - DateTimeOffset.Now).TotalMinutes));
        var relative = minutes >= 60
            ? Localization.L10n.TrFormat("~{0}h", minutes / 60)
            : Localization.L10n.TrFormat("~{0}m", minutes);
        return Localization.L10n.TrFormat("Resets at {0} ({1})", clock, relative);
    }

    /// Jump to the finished thread. Keeps the alarm up and holds focus while
    /// the resume launches (CLILocator can take a beat), then acknowledges /
    /// closes only on a real launch; a failure surfaces in place instead of
    /// the old silent vanish with nothing opened.
    private async Task OpenThread()
    {
        if (_opening) return;
        if (Thread is not { } target) { Acknowledge(); return; }
        _opening = true;
        if (_openButton is { } button) button.IsEnabled = false;
        if (_error is { } error) error.Visibility = Visibility.Collapsed;
        var launched = await TurnAlarmNavigator.Open(Provider, target);
        _opening = false;
        if (launched)
        {
            Acknowledge();
            return;
        }
        if (_openButton is { } retry) retry.IsEnabled = true;
        if (_error is { } message)
        {
            message.Text = target.LaunchTarget == SessionLaunchTarget.ClaudeDesktop
                ? Localization.L10n.Tr("Couldn't restore Claude Desktop — open it from the Start menu and try again.")
                : Localization.L10n.Tr("Couldn't open the thread — is the claude/codex CLI on your PATH?");
            message.Visibility = Visibility.Visible;
        }
    }

    /// The breathing backdrop of the macOS alarm: a radial provider-color
    /// glow anchored near the top plus a linear tint band, both cycling on
    /// the slow 1.7s loop.
    private static UIElement BuildBackdrop(Color tint)
    {
        var host = new Grid { IsHitTestVisible = false };

        var radial = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(0.5, 0.15),
            GradientOrigin = new Point(0.5, 0.15),
            RadiusX = 0.42,
            RadiusY = 0.42,
        };
        var core = new GradientStop(IslandColors.Alpha(tint, 0.20), 0);
        var mid = new GradientStop(IslandColors.Alpha(tint, 0.05), 0.55);
        radial.GradientStops.Add(core);
        radial.GradientStops.Add(mid);
        radial.GradientStops.Add(new GradientStop(IslandColors.Alpha(tint, 0), 1));
        host.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Fill = radial,
            RadiusX = 18,
            RadiusY = 18,
        });

        var band = new System.Windows.Shapes.Rectangle
        {
            Height = 210,
            VerticalAlignment = VerticalAlignment.Top,
            RadiusX = 18,
            RadiusY = 18,
            Opacity = 0.54,
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(IslandColors.Alpha(tint, 0.28), 0),
                    new GradientStop(IslandColors.Alpha(tint, 0.03), 0.55),
                    new GradientStop(IslandColors.Alpha(tint, 0), 1),
                },
                new Point(0.5, 0),
                new Point(0.5, 1)),
        };
        host.Children.Add(band);
        IslandMotion.Breathe(band, UIElement.OpacityProperty, 0.54, 1.0, 1.7);

        // Radial glow breath: radius 220→285 of the 520 card and stop
        // opacities 0.20→0.34 / 0.05→0.11 (Mac numbers).
        IslandMotion.Breathe(radial, RadialGradientBrush.RadiusXProperty, 0.42, 0.55, 1.7);
        IslandMotion.Breathe(radial, RadialGradientBrush.RadiusYProperty, 0.42, 0.55, 1.7);
        BreatheStopAlpha(core, tint, 0.20, 0.34);
        BreatheStopAlpha(mid, tint, 0.05, 0.11);
        return host;
    }

    private static void BreatheStopAlpha(GradientStop stop, Color tint, double from, double to)
    {
        var pulse = new System.Windows.Media.Animation.ColorAnimation(
            IslandColors.Alpha(tint, from),
            IslandColors.Alpha(tint, to),
            new Duration(TimeSpan.FromSeconds(1.7)))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        stop.BeginAnimation(GradientStop.ColorProperty, pulse);
    }

    /// The macOS mark cluster: breathing glow blob, two counter-phased
    /// rings, and the logo with its own fast micro-scale plus a slow
    /// shadow breath.
    private UIElement BuildMarkCluster(Color tint)
    {
        var cluster = new Grid
        {
            Width = 140,
            Height = 140,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
        };

        // Antigravity's halo sweeps all four Google hues and drifts a slow
        // full turn (macOS TurnAlarmProviderMark); every other provider
        // breathes the same disc in its single colour.
        FrameworkElement glow;
        if (Provider == TriggerTool.Antigravity)
        {
            glow = GoogleWheel(108);
            IslandMotion.Breathe(glow, UIElement.OpacityProperty, 0.38, 0.26, 1.7);
        }
        else
        {
            glow = new System.Windows.Shapes.Ellipse
            {
                Width = 108,
                Height = 108,
                Fill = IslandColors.Brush(tint),
                Opacity = 0.20,
            };
            IslandMotion.Breathe(glow, UIElement.OpacityProperty, 0.20, 0.12, 1.7);
        }
        glow.Effect = new BlurEffect { Radius = 14 };
        glow.HorizontalAlignment = HorizontalAlignment.Center;
        glow.VerticalAlignment = VerticalAlignment.Center;
        cluster.Children.Add(glow);
        IslandMotion.Breathe((BlurEffect)glow.Effect, BlurEffect.RadiusProperty, 14, 22, 1.7);
        IslandMotion.BreatheScale(glow, 0.94, 1.08, 1.7);

        var ringOuter = new System.Windows.Shapes.Ellipse
        {
            Width = 102,
            Height = 102,
            Stroke = IslandColors.Brush(tint),
            StrokeThickness = 1,
            Opacity = 0.25,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        cluster.Children.Add(ringOuter);
        IslandMotion.Breathe(ringOuter, UIElement.OpacityProperty, 0.25, 0.03, 1.7);
        IslandMotion.BreatheScale(ringOuter, 0.88, 1.10, 1.7);

        var ringInner = new System.Windows.Shapes.Ellipse
        {
            Width = 84,
            Height = 84,
            Stroke = IslandColors.Brush(tint),
            StrokeThickness = 0.75,
            Opacity = 0.14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        cluster.Children.Add(ringInner);
        IslandMotion.Breathe(ringInner, UIElement.OpacityProperty, 0.14, 0.30, 1.7);
        IslandMotion.BreatheScale(ringInner, 1.05, 0.96, 1.7);

        // The provider's REAL mark — the old path hardwired "not Claude →
        // OpenAI knot", crowning Grok/Cursor/Antigravity alarms with
        // Codex's mark.
        var mark = new System.Windows.Controls.ContentControl
        {
            Content = UI.ProviderMarks.Mark(Provider.ToDisplayProvider(), 76, tintOpacity: 1),
            Width = 76,
            Height = 76,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 18,
                Color = tint,
                Opacity = 0.48,
            },
        };
        cluster.Children.Add(mark);
        var shadow = (DropShadowEffect)mark.Effect;
        IslandMotion.Breathe(shadow, DropShadowEffect.BlurRadiusProperty, 18, 30, 1.7);
        IslandMotion.Breathe(shadow, DropShadowEffect.OpacityProperty, 0.48, 0.86, 1.7);
        IslandMotion.BreatheScale(mark, 0.985, 1.025, 0.72);

        return cluster;
    }

    /// The Google colour wheel: eight wedges cycling the four hues, turned
    /// slowly by a GPU transform — animating gradient stops instead would
    /// re-rasterize per frame (island conic-glow postmortem).
    private static FrameworkElement GoogleWheel(double side)
    {
        var wedges = new Grid { Width = side, Height = side };
        var hues = new[]
        {
            Color.FromRgb(66, 133, 244),
            Color.FromRgb(52, 168, 83),
            Color.FromRgb(251, 188, 5),
            Color.FromRgb(234, 67, 53),
        };
        for (var i = 0; i < 8; i++)
        {
            var wedge = new System.Windows.Shapes.Path
            {
                Data = UI.ChartStylePickerControl.PieSliceGeometry(side, 0.125),
                Fill = IslandColors.Brush(hues[i % hues.Length]),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(i * 45),
            };
            wedges.Children.Add(wedge);
        }
        var spin = new RotateTransform();
        var host = new Grid
        {
            Width = side,
            Height = side,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = spin,
            Clip = new EllipseGeometry(new Point(side / 2, side / 2), side / 2, side / 2),
        };
        host.Children.Add(wedges);
        var turn = new System.Windows.Media.Animation.DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(14)))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(turn, 24);
        spin.BeginAnimation(RotateTransform.AngleProperty, turn);
        return host;
    }

    private Grid BuildMetadata(ActivityMonitor.ActiveThread thread, Color tint)
    {
        var grid = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddMeta(grid, 0, Localization.L10n.Tr("Alarm provider"), Provider.Display(), tint);
        // The headline already carries the label; the thread cell adds the
        // short session id so two same-named sessions stay tellable apart.
        var threadValue = thread.Label;
        if (thread.SessionId is { Length: > 0 } sid)
        {
            threadValue = $"{thread.Label} · {(sid.Length > 8 ? sid[..8] : sid)}";
        }
        AddMeta(grid, 1, Localization.L10n.Tr("Alarm thread"), threadValue, null, tooltip: thread.SessionId);
        AddMeta(grid, 2, Localization.L10n.Tr("Alarm project"), ProjectDisplay(thread.Cwd), null,
            tooltip: string.IsNullOrEmpty(thread.Cwd) ? null : thread.Cwd);
        return grid;
    }

    /// Last two path segments ("Fable5\skill") beat a bare folder name —
    /// several projects share tail names like src or app.
    private static string ProjectDisplay(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "—";
        var trimmed = cwd.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) return trimmed;
        var parentPath = System.IO.Path.GetDirectoryName(trimmed);
        var parent = string.IsNullOrEmpty(parentPath)
            ? ""
            : System.IO.Path.GetFileName(parentPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
    }

    private static void AddMeta(Grid grid, int column, string caption, string value, Color? dotColor, string? tooltip = null)
    {
        var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        cell.Children.Add(new TextBlock
        {
            Text = caption,
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(0.4)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var valueRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        if (dotColor is { } dot)
        {
            valueRow.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = IslandColors.Brush(dot),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
        }
        var valueText = new TextBlock
        {
            Text = value,
            FontFamily = IslandFonts.Ui,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (tooltip is { Length: > 0 })
        {
            valueText.ToolTip = tooltip;
        }
        valueRow.Children.Add(valueText);
        cell.Children.Add(valueRow);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    /// Mac primary action: 396x48, vertical brand gradient (0.96→0.72), a
    /// colored grounding shadow, and press-scale feedback.
    private static Button MakePrimaryButton(string text, Color tint)
    {
        var button = new Button
        {
            Content = text,
            FontFamily = IslandFonts.Ui,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.LabelOn(tint)),
            Width = 396,
            Height = 48,
            Cursor = Cursors.Hand,
            Effect = new DropShadowEffect
            {
                ShadowDepth = 4,
                Direction = 270,
                BlurRadius = 18,
                Color = tint,
                Opacity = 0.46,
            },
        };
        var fill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(IslandColors.Alpha(tint, 0.96), 0),
                new GradientStop(IslandColors.Alpha(tint, 0.72), 1),
            },
            new Point(0.5, 0),
            new Point(0.5, 1));
        fill.Freeze();
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        border.SetValue(Border.BackgroundProperty, fill);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        IslandMotion.AttachPressFeedback(button);
        return button;
    }

    /// Mac secondary action: 396x42, faint white fill and hairline stroke.
    private static Button MakeSecondaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            FontFamily = IslandFonts.Ui,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.86)),
            Width = 396,
            Height = 42,
            Cursor = Cursors.Hand,
        };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        border.SetValue(Border.BackgroundProperty, IslandColors.Brush(IslandColors.White(0.08)));
        border.SetValue(Border.BorderBrushProperty, IslandColors.Brush(IslandColors.White(0.12)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0.5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        IslandMotion.AttachPressFeedback(button);
        return button;
    }

    private void Acknowledge()
    {
        // Only turn alarms feed the needsYou acknowledge machinery; a quota
        // alarm has no thread turn to mark as seen.
        if (_kind is TurnAlarmKind.YourTurn)
        {
            AgentReminderCenter.Shared.Acknowledge(Provider, Thread);
        }
        DismissSilently();
    }

    /// Close without acknowledging — used when the turn already left
    /// needsYou (the user replied in the thread).
    public void DismissSilently()
    {
        Dismissed?.Invoke(this);
        Close();
    }
}
