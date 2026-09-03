using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AgentIsland.UI.Providers;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// App-styled dialog in the turn-alarm design language — dark rounded card,
/// ringed glowing brand mark, headline, message, optional caption/value meta
/// rows (thread, project, …), and stacked buttons. Replaces every raw
/// Win32 MessageBox in the app.
public sealed partial class IslandDialog : Window
{
    private readonly Action? _primaryAction;
    private readonly Action? _secondaryAction;

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
        InitializeComponent();

        _primaryAction = primaryAction;
        _secondaryAction = secondaryAction;

        Title = title;
        TitleBlock.Text = title;
        MessageBlock.Text = message;

        // Build Icon Slot
        if (appIcon is not null)
        {
            // Real app icon in a dark rounded square — how the icon reads
            // in the macOS updater dialog. Flat on purpose: no breathing
            // glow on an informational card.
            var iconBox = new Border
            {
                Width = 72,
                Height = 72,
                CornerRadius = new CornerRadius(17),
                Background = IslandColors.Brush(IslandColors.White(0.05)),
                BorderBrush = IslandColors.Brush(IslandColors.White(0.10)),
                BorderThickness = new Thickness(1),
                Child = new Image
                {
                    Source = appIcon,
                    Width = 46,
                    Height = 46,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            IconSlot.Children.Add(iconBox);
        }
        else
        {
            // The provider's REAL mark (masked bitmap or full-color art),
            // carrying the breathing brand glow.
            var glyph = new ContentControl
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
            IslandMotion.Breathe((DropShadowEffect)glyph.Effect, DropShadowEffect.BlurRadiusProperty, 14, 24, 1.7);

            var circle = new Border
            {
                Width = 72,
                Height = 72,
                CornerRadius = new CornerRadius(36),
                BorderBrush = IslandColors.Brush(tint, 0.35),
                BorderThickness = new Thickness(1),
                Child = glyph,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            IconSlot.Children.Add(circle);
        }

        // Build Meta Rows
        if (meta is { Count: > 0 })
        {
            MetaGrid.Visibility = Visibility.Visible;
            MetaGrid.ColumnDefinitions.Clear();
            MetaGrid.Children.Clear();
            for (var i = 0; i < meta.Count; i++)
            {
                MetaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
                MetaGrid.Children.Add(cell);
            }
        }

        // Configure Buttons
        if (primaryLabel is not null)
        {
            var primaryFg = IslandColors.Brush(IslandColors.LabelOn(tint));
            var primaryBg = IslandColors.Brush(tint);
            var secondaryFg = IslandColors.Brush(IslandColors.White(0.85));
            var secondaryBg = IslandColors.Brush(IslandColors.White(0.06));

            if (horizontalButtons && secondaryLabel is not null)
            {
                HorizontalButtonRow.Visibility = Visibility.Visible;
                VerticalButtonStack.Visibility = Visibility.Collapsed;

                SecondaryHorizontalButton.Background = secondaryBg;
                SecondaryHorizontalText.Foreground = secondaryFg;
                SecondaryHorizontalText.Text = secondaryLabel;
                IslandMotion.AttachPressFeedback(SecondaryHorizontalButton);

                PrimaryHorizontalButton.Background = primaryBg;
                PrimaryHorizontalText.Foreground = primaryFg;
                PrimaryHorizontalText.Text = primaryLabel;
                IslandMotion.AttachPressFeedback(PrimaryHorizontalButton);
            }
            else
            {
                VerticalButtonStack.Visibility = Visibility.Visible;
                HorizontalButtonRow.Visibility = Visibility.Collapsed;

                PrimaryVerticalButton.Background = primaryBg;
                PrimaryVerticalText.Foreground = primaryFg;
                PrimaryVerticalText.Text = primaryLabel;
                IslandMotion.AttachPressFeedback(PrimaryVerticalButton);

                if (secondaryLabel is not null)
                {
                    SecondaryVerticalButton.Visibility = Visibility.Visible;
                    SecondaryVerticalButton.Background = secondaryBg;
                    SecondaryVerticalText.Foreground = secondaryFg;
                    SecondaryVerticalText.Text = secondaryLabel;
                    IslandMotion.AttachPressFeedback(SecondaryVerticalButton);
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

        IslandMotion.AnimateEntrance(this, RootBorder);
    }

    private void OnPrimaryClicked(object sender, RoutedEventArgs e)
    {
        Close();
        _primaryAction?.Invoke();
    }

    private void OnSecondaryClicked(object sender, RoutedEventArgs e)
    {
        Close();
        _secondaryAction?.Invoke();
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
            primaryLabel ?? AgentIsland.UI.Localization.L10n.Tr("I know"), primaryAction, secondaryLabel));
    }

    /// The five-blade app mark for provider-neutral dialogs.
    private static UIElement AppMark()
    {
        try
        {
            var image = new Image
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

    /// App-branded dialog for provider-neutral messages like update checks.
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
            primaryLabel ?? AgentIsland.UI.Localization.L10n.Tr("I know"), primaryAction, secondaryLabel));
    }

    /// Sparkle-style update dialog: the real app icon, headline, message,
    /// and a side-by-side [secondary][primary] button row.
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
    /// the entrance fade have settled.
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

    /// Button-less progress card for the update flow.
    public static IslandDialog ShowAppProgress(string title, string message)
    {
        var dialog = new IslandDialog(
            title, message, IslandColors.Cobalt,
            AppMark(), null,
            primaryLabel: null, primaryAction: null, secondaryLabel: null);
        Present(dialog);
        return dialog;
    }

    public void SetMessage(string text) => MessageBlock.Text = text;

    private static void Present(IslandDialog dialog)
    {
        dialog.Show();
        dialog.Activate();
    }
}
