using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// The on/off switch shared by every Settings row. White-on-black material
/// only — the 2026-08-09 de-branding stripped every accent color from the
/// app's own chrome, so the ON state speaks a white track wash + solid
/// white knob (macOS SettingsToggle: 34x19 track, 15pt knob).
public sealed class CobaltToggle : Border
{
    // Track 34, knob 15, 2pt inset → the knob travels 15pt between rests.
    private const double KnobTravel = 34 - 15 - 2 * 2;

    private readonly Ellipse _dot;
    private readonly TranslateTransform _slide = new();
    private readonly SolidColorBrush _track = new();
    private readonly SolidColorBrush _rim = new();
    private readonly SolidColorBrush _knob = new();
    private bool _isOn;
    private bool _hovered;
    private bool _seeded;

    public event Action<bool>? Toggled;

    public CobaltToggle(bool isOn)
    {
        _isOn = isOn;
        Width = 34;
        Height = 19;
        CornerRadius = new CornerRadius(9.5);
        BorderThickness = new Thickness(1);
        Background = _track;
        BorderBrush = _rim;
        Cursor = System.Windows.Input.Cursors.Hand;
        _dot = new Ellipse
        {
            Width = 15,
            Height = 15,
            Fill = _knob,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
            RenderTransform = _slide,
        };
        Child = _dot;
        MouseLeftButtonUp += (_, args) =>
        {
            _isOn = !_isOn;
            Render();
            Toggled?.Invoke(_isOn);
            args.Handled = true;
        };
        MouseEnter += (_, _) => { _hovered = true; Render(); };
        MouseLeave += (_, _) => { _hovered = false; Render(); };
        Render();
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            _isOn = value;
            Render();
        }
    }

    /// The knob SLIDES and the washes crossfade (macOS SettingsToggle
    /// spring ~0.3s) — the first paint lands instantly so a freshly built
    /// settings page doesn't ripple with settling toggles.
    private void Render()
    {
        var track = IslandColors.White(_isOn ? 0.34 : 0.07);
        var rim = IslandColors.White(_isOn ? (_hovered ? 0.55 : 0.35) : (_hovered ? 0.22 : 0.13));
        var knob = _isOn ? Colors.White : IslandColors.White(0.55);
        var offset = _isOn ? KnobTravel : 0;
        _dot.Effect = _isOn
            ? new DropShadowEffect { ShadowDepth = 0, BlurRadius = 5, Color = Colors.White, Opacity = 0.32 }
            : new DropShadowEffect { ShadowDepth = 0.5, BlurRadius = 1.5, Color = Colors.Black, Opacity = 0.35 };
        if (!_seeded)
        {
            _seeded = true;
            _track.Color = track;
            _rim.Color = rim;
            _knob.Color = knob;
            _slide.X = offset;
            return;
        }
        var beat = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new System.Windows.Media.Animation.QuadraticEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
        };
        _track.BeginAnimation(SolidColorBrush.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(track, beat) { EasingFunction = ease });
        _rim.BeginAnimation(SolidColorBrush.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(rim, beat) { EasingFunction = ease });
        _knob.BeginAnimation(SolidColorBrush.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(knob, beat) { EasingFunction = ease });
        _slide.BeginAnimation(TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(offset, beat) { EasingFunction = ease });
    }
}

/// Pill-shaped segmented control (Refresh interval, Token counting, Mac
/// type pickers).
public sealed class Segmented : Border
{
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal };
    private readonly List<Border> _cells = new();
    private int _selected;

    public event Action<int>? SelectionChanged;

    public Segmented(IReadOnlyList<string> labels, int selected)
    {
        _selected = selected;
        CornerRadius = new CornerRadius(14);
        Background = IslandColors.Brush(IslandColors.White(0.05));
        BorderBrush = IslandColors.Brush(IslandColors.White(0.05));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(3);
        Child = _items;
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var text = new TextBlock
            {
                Text = labels[i],
                FontFamily = IslandFonts.Mono,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
            };
            var cell = new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(11, 5, 11, 5),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            cell.MouseLeftButtonUp += (_, args) =>
            {
                Select(index);
                SelectionChanged?.Invoke(index);
                args.Handled = true;
            };
            _cells.Add(cell);
            _items.Children.Add(cell);
        }
        Select(selected);
    }

    /// macOS SegmentedControl: near-white thumb, BLACK selected label,
    /// ghost-white unselected labels on a faint capsule track.
    public void Select(int index)
    {
        _selected = Math.Clamp(index, 0, _cells.Count - 1);
        for (var i = 0; i < _cells.Count; i++)
        {
            var isOn = i == _selected;
            _cells[i].Background = isOn ? IslandColors.Brush(IslandColors.White(0.92)) : Brushes.Transparent;
            var label = (TextBlock)_cells[i].Child!;
            label.Foreground = isOn
                ? IslandColors.Brush(Color.FromArgb(0xD9, 0x00, 0x00, 0x00))
                : IslandColors.Brush(IslandColors.White(0.55));
            label.FontWeight = isOn ? FontWeights.SemiBold : FontWeights.Medium;
        }
    }
}

/// Plain pill action button ("Refresh", "Check").
public sealed class PillButtonControl : Border
{
    private readonly TextBlock _label;

    public event Action? Clicked;

    public PillButtonControl(string label)
    {
        _label = new TextBlock
        {
            Text = label,
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.9)),
        };
        Child = _label;
        CornerRadius = new CornerRadius(6);
        Background = IslandColors.Brush(IslandColors.White(0.10));
        BorderBrush = IslandColors.Brush(IslandColors.White(0.08));
        BorderThickness = new Thickness(0.5);
        Padding = new Thickness(12, 5, 12, 5);
        Cursor = System.Windows.Input.Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        MouseLeftButtonUp += (_, args) =>
        {
            Clicked?.Invoke();
            args.Handled = true;
        };
    }

    public string Label
    {
        get => _label.Text;
        set => _label.Text = value;
    }
}

/// Dotted-underline external link ("GitHub ↗").
public sealed class DottedLink : StackPanel
{
    public DottedLink(string title, string url)
    {
        Orientation = Orientation.Horizontal;
        Cursor = System.Windows.Input.Cursors.Hand;
        var text = new TextBlock
        {
            Text = Localization.L10n.Tr(title),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            TextDecorations = null,
        };
        var arrow = new TextBlock
        {
            Text = " ↗",
            FontFamily = IslandFonts.Ui,
            FontSize = 10,
            Foreground = IslandColors.Brush(IslandColors.White(0.3)),
        };
        Children.Add(text);
        Children.Add(arrow);
        MouseEnter += (_, _) =>
        {
            text.Foreground = IslandColors.Brush(IslandColors.White(0.92));
            arrow.Foreground = IslandColors.Brush(IslandColors.White(0.6));
        };
        MouseLeave += (_, _) =>
        {
            text.Foreground = IslandColors.Brush(IslandColors.White(0.55));
            arrow.Foreground = IslandColors.Brush(IslandColors.White(0.3));
        };
        MouseLeftButtonUp += (_, args) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
            }
            catch
            {
            }
            args.Handled = true;
        };
    }
}

/// A single Settings list row: title (+ optional brand dot and plan chip),
/// subtitle, trailing control. Hover lifts the background to a faint wash.
public sealed class SettingsRowControl : Border
{
    public SettingsRowControl(
        string title,
        string? subtitle,
        UIElement trailing,
        Color? dot = null,
        string? chip = null,
        bool monospaceTitle = false)
    {
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10, 11, 10, 11);
        Background = Brushes.Transparent;
        MouseEnter += (_, _) => Background = IslandColors.Brush(IslandColors.White(0.030));
        MouseLeave += (_, _) => Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Child = grid;

        var text = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot is { } dotColor)
        {
            titleRow.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = IslandColors.Brush(dotColor),
                Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 4, Color = dotColor, Opacity = 0.7 },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
        }
        titleRow.Children.Add(new TextBlock
        {
            Text = monospaceTitle ? title : Localization.L10n.Tr(title),
            FontFamily = monospaceTitle ? IslandFonts.Mono : IslandFonts.Ui,
            FontSize = monospaceTitle ? 10 : 13,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.92)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
        });
        if (!string.IsNullOrEmpty(chip))
        {
            titleRow.Children.Add(new Border
            {
                Child = new TextBlock
                {
                    Text = chip,
                    FontFamily = IslandFonts.Ui,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = IslandColors.Brush(IslandColors.White(0.6)),
                },
                CornerRadius = new CornerRadius(3),
                Background = IslandColors.Brush(IslandColors.White(0.06)),
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        text.Children.Add(titleRow);
        if (!string.IsNullOrEmpty(subtitle))
        {
            text.Children.Add(new TextBlock
            {
                Text = Localization.L10n.Tr(subtitle!),
                FontFamily = IslandFonts.Ui,
                FontSize = 11,
                Foreground = IslandColors.Brush(IslandColors.White(0.55)),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                // Stretch + MaxWidth centers the box in WPF — the subtitle
                // must sit flush with the title's left edge.
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var trailingHost = new ContentControl
        {
            Content = trailing,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        Grid.SetColumn(trailingHost, 1);
        grid.Children.Add(trailingHost);
    }
}

/// WPF's default ComboBox is a light-theme control — white face, grey
/// chrome — and it read as exactly that on the dark settings page (owner
/// review, 2026-08-09: 质感太差). One dark template, parsed once, shared
/// by every picker.
public static class DarkComboStyle
{
    private const string TemplateXaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="ComboBox">
  <Grid>
    <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press"
                  IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
      <ToggleButton.Template>
        <ControlTemplate TargetType="ToggleButton">
          <Border x:Name="Face" CornerRadius="7" Background="#0DFFFFFF"
                  BorderBrush="#1AFFFFFF" BorderThickness="1">
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
              </Grid.ColumnDefinitions>
              <ContentPresenter Grid.Column="0" Margin="10,0,4,0"
                                HorizontalAlignment="Left" VerticalAlignment="Center"/>
              <Path Grid.Column="1" Margin="0,0,9,0" VerticalAlignment="Center"
                    Data="M 0 0 L 3.5 3.5 L 7 0" Stroke="#8CFFFFFF" StrokeThickness="1.4"/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Face" Property="Background" Value="#1AFFFFFF"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter Content="{TemplateBinding SelectionBoxItem}"
                      ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                      Margin="10,0,24,0" HorizontalAlignment="Left"
                      VerticalAlignment="Center" IsHitTestVisible="False"
                      TextBlock.Foreground="#F2FFFFFF"/>
    <Popup IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom"
           VerticalOffset="4" AllowsTransparency="True" Focusable="False"
           PopupAnimation="Fade">
      <Border Background="#FF15171C" CornerRadius="8" BorderBrush="#21FFFFFF"
              BorderThickness="1" MinWidth="{TemplateBinding ActualWidth}"
              MaxHeight="{TemplateBinding MaxDropDownHeight}" Padding="4">
        <ScrollViewer VerticalScrollBarVisibility="Auto"
                      HorizontalScrollBarVisibility="Disabled">
          <ItemsPresenter/>
        </ScrollViewer>
      </Border>
    </Popup>
  </Grid>
</ControlTemplate>
""";

    private const string ItemXaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="ComboBoxItem">
  <Border x:Name="Row" CornerRadius="5" Padding="8,5,8,5" Background="Transparent">
    <ContentPresenter TextBlock.Foreground="#DEFFFFFF"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsHighlighted" Value="True">
      <Setter TargetName="Row" Property="Background" Value="#1FFFFFFF"/>
    </Trigger>
    <Trigger Property="IsSelected" Value="True">
      <Setter TargetName="Row" Property="Background" Value="#26FFFFFF"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""";

    private static ControlTemplate? _template;
    private static ControlTemplate? _itemTemplate;

    public static ComboBox Apply(ComboBox box)
    {
        _template ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(TemplateXaml);
        _itemTemplate ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(ItemXaml);
        box.Template = _template;
        box.Foreground = IslandColors.Brush(IslandColors.White(0.95));
        box.FontFamily = Charts.IslandFonts.Ui;
        box.FontSize = 12;
        box.Height = 28;
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, _itemTemplate));
        itemStyle.Setters.Add(new Setter(Control.FontFamilyProperty, Charts.IslandFonts.Ui));
        itemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        box.ItemContainerStyle = itemStyle;
        return box;
    }
}
