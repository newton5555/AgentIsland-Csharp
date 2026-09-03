using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// A single Settings list row: title (+ optional brand dot and plan chip),
/// subtitle, trailing control. Hover lifts the background to a faint wash.
[ContentProperty(nameof(Trailing))]
public sealed class SettingsRowControl : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(SettingsRowControl),
            new PropertyMetadata(string.Empty, (d, _) => ((SettingsRowControl)d).UpdateVisuals()));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(
            nameof(Subtitle),
            typeof(string),
            typeof(SettingsRowControl),
            new PropertyMetadata(null, (d, _) => ((SettingsRowControl)d).UpdateVisuals()));

    public static readonly DependencyProperty DotColorProperty =
        DependencyProperty.Register(
            nameof(DotColor),
            typeof(Color?),
            typeof(SettingsRowControl),
            new PropertyMetadata(null, (d, _) => ((SettingsRowControl)d).UpdateVisuals()));

    public static readonly DependencyProperty ChipProperty =
        DependencyProperty.Register(
            nameof(Chip),
            typeof(string),
            typeof(SettingsRowControl),
            new PropertyMetadata(null, (d, _) => ((SettingsRowControl)d).UpdateVisuals()));

    public static readonly DependencyProperty MonospaceTitleProperty =
        DependencyProperty.Register(
            nameof(MonospaceTitle),
            typeof(bool),
            typeof(SettingsRowControl),
            new PropertyMetadata(false, (d, _) => ((SettingsRowControl)d).UpdateVisuals()));

    private Ellipse? _dotEllipse;
    private TextBlock? _titleBlock;
    private Border? _chipBorder;
    private TextBlock? _chipBlock;
    private TextBlock? _subtitleBlock;

    static SettingsRowControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingsRowControl),
            new FrameworkPropertyMetadata(typeof(SettingsRowControl)));
    }

    public SettingsRowControl()
    {
        Focusable = false;
    }

    public SettingsRowControl(
        string title,
        string? subtitle,
        UIElement trailing,
        Color? dot = null,
        string? chip = null,
        bool monospaceTitle = false) : this()
    {
        Title = title;
        Subtitle = subtitle;
        Trailing = trailing;
        DotColor = dot;
        Chip = chip;
        MonospaceTitle = monospaceTitle;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? Trailing
    {
        get => Content;
        set => Content = value;
    }

    public Color? DotColor
    {
        get => (Color?)GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
    }

    public string? Chip
    {
        get => (string?)GetValue(ChipProperty);
        set => SetValue(ChipProperty, value);
    }

    public bool MonospaceTitle
    {
        get => (bool)GetValue(MonospaceTitleProperty);
        set => SetValue(MonospaceTitleProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _dotEllipse = GetTemplateChild("PART_DotEllipse") as Ellipse;
        _titleBlock = GetTemplateChild("PART_TitleBlock") as TextBlock;
        _chipBorder = GetTemplateChild("PART_ChipBorder") as Border;
        _chipBlock = GetTemplateChild("PART_ChipBlock") as TextBlock;
        _subtitleBlock = GetTemplateChild("PART_SubtitleBlock") as TextBlock;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        if (_titleBlock != null)
        {
            _titleBlock.Text = MonospaceTitle ? Title : L10n.Tr(Title ?? string.Empty);
            _titleBlock.FontFamily = MonospaceTitle ? IslandFonts.Mono : IslandFonts.Ui;
            _titleBlock.FontSize = MonospaceTitle ? 10 : 13;
        }

        if (_dotEllipse != null)
        {
            if (DotColor is { } dot)
            {
                _dotEllipse.Fill = IslandColors.Brush(dot);
                _dotEllipse.Effect = new DropShadowEffect
                {
                    ShadowDepth = 0,
                    BlurRadius = 4,
                    Color = dot,
                    Opacity = 0.7
                };
                _dotEllipse.Visibility = Visibility.Visible;
            }
            else
            {
                _dotEllipse.Visibility = Visibility.Collapsed;
            }
        }

        if (_chipBorder != null && _chipBlock != null)
        {
            if (!string.IsNullOrEmpty(Chip))
            {
                _chipBlock.Text = Chip;
                _chipBorder.Visibility = Visibility.Visible;
            }
            else
            {
                _chipBorder.Visibility = Visibility.Collapsed;
            }
        }

        if (_subtitleBlock != null)
        {
            if (!string.IsNullOrEmpty(Subtitle))
            {
                _subtitleBlock.Text = L10n.Tr(Subtitle);
                _subtitleBlock.Visibility = Visibility.Visible;
            }
            else
            {
                _subtitleBlock.Visibility = Visibility.Collapsed;
            }
        }
    }
}
