using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AgentIsland.Model;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// App-styled dialog in the turn-alarm design language — dark rounded card,
/// ringed glowing brand mark, headline, message, optional caption/value meta
/// rows (thread, project, …), and stacked buttons. Replaces every raw
/// Win32 MessageBox in the app.
public sealed class IslandDialog : Window
{
    private readonly TextBlock _message;

    /// A null primaryLabel builds the progress form: no buttons and no
    /// Escape — the flow that opened it owns closing it (a half-finished
    /// exe swap is not something the user can cancel out of). An appIcon
    /// swaps the glowing brand glyph for the real app icon in a dark
    /// rounded square, and horizontalButtons lays the pair side by side —
    /// the Sparkle updater layout the macOS app shows.
    private IslandDialog(
        string title,
        string message,
        Color tint,
        UIElement markGlyph,
        IReadOnlyList<(string Caption, string Value)>? meta,
        string? primaryLabel,
        Action? primaryAction,
        string? secondaryLabel,
        ImageSource? appIcon = null,
        bool horizontalButtons = false,
        Action? secondaryAction = null)
    {
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = title;
        System.Windows.Media.TextOptions.SetTextFormattingMode(
            this, System.Windows.Media.TextFormattingMode.Display);

        var root = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = IslandColors.Brush(IslandColors.AlarmBackground),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.07)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12),
            Effect = new DropShadowEffect
            {
                ShadowDepth = 4,
                Direction = 270,
                BlurRadius = 18,
                Color = Colors.Black,
                Opacity = 0.55,
            },
        };
        Content = root;

        var stack = new StackPanel { Margin = new Thickness(32, 26, 32, 24) };
        root.Child = stack;

        if (appIcon is not null)
        {
            // Real app icon in a dark rounded square — how the icon reads
            // in the macOS updater dialog. Flat on purpose: no breathing
            // glow on an informational card.
            stack.Children.Add(new Border
            {
                Width = 72,
                Height = 72,
                CornerRadius = new CornerRadius(17),
                Background = IslandColors.Brush(IslandColors.White(0.05)),
                BorderBrush = IslandColors.Brush(IslandColors.White(0.10)),
                BorderThickness = new Thickness(1),
                Child = new System.Windows.Controls.Image
                {
                    Source = appIcon,
                    Width = 46,
                    Height = 46,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16),
            });
        }
        else
        {
            // The provider's REAL mark (masked bitmap or full-color art),
            // carrying the breathing brand glow — the old path hardwired
            // "not Claude → OpenAI knot", which crowned a Grok alarm with
            // Codex's mark.
            var glyph = new System.Windows.Controls.ContentControl
            {
                Content = markGlyph,
                Width = 40,
                Height = 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect
                {
                    ShadowDepth = 0,
                    BlurRadius = 16,
                    Color = tint,
                    Opacity = 0.55,
                },
            };
            // The slow alarm-family glow breath, scaled down for a dialog.
            IslandMotion.Breathe((DropShadowEffect)glyph.Effect, DropShadowEffect.BlurRadiusProperty, 14, 24, 1.7);
            stack.Children.Add(new Border
            {
                Width = 72,
                Height = 72,
                CornerRadius = new CornerRadius(36),
                BorderBrush = IslandColors.Brush(tint, 0.35),
                BorderThickness = new Thickness(1),
                Child = glyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16),
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = IslandFonts.Ui,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        _message = new TextBlock
        {
            Text = message,
            FontFamily = IslandFonts.Ui,
            FontSize = 12.5,
            Foreground = IslandColors.Brush(IslandColors.White(0.7)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            LineHeight = 19,
        };
        stack.Children.Add(_message);

        if (meta is { Count: > 0 })
        {
            var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            for (var i = 0; i < meta.Count; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            for (var i = 0; i < meta.Count; i++)
            {
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                cell.Children.Add(new TextBlock
                {
                    Text = meta[i].Caption.ToUpperInvariant(),
                    FontFamily = IslandFonts.Ui,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = IslandColors.Brush(IslandColors.White(0.4)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                cell.Children.Add(new TextBlock
                {
                    Text = meta[i].Value,
                    FontFamily = IslandFonts.Ui,
                    FontSize = 12,
                    FontWeight = FontWeights.Medium,
                    Foreground = IslandColors.Brush(IslandColors.White(0.85)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 110,
                });
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }
            stack.Children.Add(grid);
        }

        if (primaryLabel is not null)
        {
            var primary = MakeButton(primaryLabel, IslandColors.LabelOn(tint), tint, bold: true);
            primary.Click += (_, _) =>
            {
                Close();
                primaryAction?.Invoke();
            };

            Button? secondary = null;
            if (secondaryLabel is not null)
            {
                secondary = MakeButton(
                    secondaryLabel, IslandColors.White(0.85), IslandColors.White(0.06), bold: false);
                secondary.Click += (_, _) =>
                {
                    Close();
                    secondaryAction?.Invoke();
                };
            }

            if (horizontalButtons && secondary is not null)
            {
                // Sparkle layout: [secondary] [primary] sharing one row,
                // primary on the right.
                var row = new Grid { Margin = new Thickness(0, 22, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(secondary, 0);
                Grid.SetColumn(primary, 2);
                row.Children.Add(secondary);
                row.Children.Add(primary);
                stack.Children.Add(row);
            }
            else
            {
                primary.Margin = new Thickness(0, 22, 0, 0);
                stack.Children.Add(primary);
                if (secondary is not null)
                {
                    secondary.Margin = new Thickness(0, 10, 0, 0);
                    stack.Children.Add(secondary);
                }
            }

            KeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape) Close();
            };
        }
        MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { }
        };
        IslandMotion.AnimateEntrance(this, root);
    }

    /// Provider-tinted dialog carrying that provider's REAL mark.
    public static void Show(
        Core.TriggerTool tool,
        string title,
        string message,
        IReadOnlyList<(string Caption, string Value)>? meta = null,
        string? primaryLabel = null,
        Action? primaryAction = null,
        string? secondaryLabel = null)
    {
        Present(new IslandDialog(
            title, message, IslandColors.For(tool),
            ProviderMarks.Mark(tool.ToDisplayProvider(), 40, tintOpacity: 1), meta,
            primaryLabel ?? Localization.L10n.Tr("I know"), primaryAction, secondaryLabel));
    }

    /// The five-blade app mark for provider-neutral dialogs (brand era —
    /// the cobalt Claude spark stand-in is retired).
    private static UIElement AppMark()
    {
        try
        {
            var image = new System.Windows.Controls.Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Assets/agentisland_logo_small.png")),
                Width = 40,
                Height = 40,
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        catch
        {
            return new System.Windows.Shapes.Ellipse
            {
                Width = 40,
                Height = 40,
                Stroke = IslandColors.Brush(IslandColors.White(0.8)),
                StrokeThickness = 2,
            };
        }
    }

    /// App-branded dialog (Claude spark in cobalt) for provider-neutral
    /// messages like update checks.
    public static void ShowApp(
        string title,
        string message,
        string? primaryLabel = null,
        Action? primaryAction = null,
        string? secondaryLabel = null)
    {
        Present(new IslandDialog(
            title, message, IslandColors.Cobalt,
            AppMark(), null,
            primaryLabel ?? Localization.L10n.Tr("I know"), primaryAction, secondaryLabel));
    }

    /// Sparkle-style update dialog: the real app icon, headline, message,
    /// and a side-by-side [secondary][primary] button row — the layout the
    /// macOS updater shows, in the island's dark card.
    public static IslandDialog ShowUpdate(
        string title,
        string message,
        string primaryLabel,
        Action? primaryAction = null,
        string? secondaryLabel = null,
        Action? secondaryAction = null)
    {
        ImageSource? icon = null;
        try
        {
            icon = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/agentisland_logo.png"));
        }
        catch
        {
            // Missing resource falls back to the glowing brand glyph.
        }
        var dialog = new IslandDialog(
            title, message, IslandColors.Cobalt,
            AppMark(), null,
            primaryLabel, primaryAction, secondaryLabel,
            appIcon: icon, horizontalButtons: true, secondaryAction: secondaryAction);
        Present(dialog);
        return dialog;
    }

    /// Scripted verification: render this dialog into a PNG once layout AND
    /// the entrance fade have settled — screenshots that survive virtual
    /// desktops and occlusion.
    public void SaveSnapshot(string path)
    {
        var settle = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            try
            {
                var width = (int)Math.Ceiling(ActualWidth);
                var height = (int)Math.Ceiling(ActualHeight);
                if (width <= 0 || height <= 0) return;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(this);
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

    /// Button-less progress card for the update flow. The caller keeps the
    /// handle: SetMessage for progress ticks, Close when the work is done.
    public static IslandDialog ShowAppProgress(string title, string message)
    {
        var dialog = new IslandDialog(
            title, message, IslandColors.Cobalt,
            AppMark(), null,
            primaryLabel: null, primaryAction: null, secondaryLabel: null);
        Present(dialog);
        return dialog;
    }

    public void SetMessage(string text) => _message.Text = text;

    private static void Present(IslandDialog dialog)
    {
        dialog.Show();
        dialog.Activate();
    }

    private static Button MakeButton(string label, Color foreground, Color background, bool bold)
    {
        var button = new Button
        {
            Content = label,
            FontFamily = IslandFonts.Ui,
            FontSize = 13,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = IslandColors.Brush(foreground),
            Background = IslandColors.Brush(background),
            BorderThickness = new Thickness(0),
            Height = 38,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        // Rounded pill template so the default WPF chrome (square, light)
        // never shows through.
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        factory.SetValue(Border.BackgroundProperty, IslandColors.Brush(background));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
        IslandMotion.AttachPressFeedback(button);
        return button;
    }
}
