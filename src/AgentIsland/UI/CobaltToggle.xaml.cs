using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// The on/off switch shared by every Settings row. White-on-black material
/// only — the 2026-08-09 de-branding stripped every accent color from the
/// app's own chrome, so the ON state speaks a white track wash + solid
/// white knob (macOS SettingsToggle: 34x19 track, 15pt knob).
public sealed partial class CobaltToggle : UserControl
{
    // Track 34, knob 15, 2pt inset → the knob travels 15pt between rests.
    private const double KnobTravel = 34 - 15 - 2 * 2;

    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(
            nameof(IsOn),
            typeof(bool),
            typeof(CobaltToggle),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOnChanged));

    private readonly SolidColorBrush _track = new();
    private readonly SolidColorBrush _rim = new();
    private readonly SolidColorBrush _knob = new();
    private bool _hovered;
    private bool _seeded;

    public event Action<bool>? Toggled;

    public CobaltToggle() : this(false)
    {
    }

    public CobaltToggle(bool isOn)
    {
        InitializeComponent();
        TrackBorder.Background = _track;
        TrackBorder.BorderBrush = _rim;
        Dot.Fill = _knob;

        IsOn = isOn;
        Loaded += (_, _) => Render();
        Render();
    }

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CobaltToggle toggle)
        {
            toggle.Render();
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Render();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Render();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        IsOn = !IsOn;
        Toggled?.Invoke(IsOn);
        e.Handled = true;
    }

    /// The knob SLIDES and the washes crossfade (macOS SettingsToggle
    /// spring ~0.3s) — the first paint lands instantly so a freshly built
    /// settings page doesn't ripple with settling toggles.
    private void Render()
    {
        if (TrackBorder == null || Dot == null || SlideTransform == null) return;

        var isOn = IsOn;
        var track = IslandColors.White(isOn ? 0.34 : 0.07);
        var rim = IslandColors.White(isOn ? (_hovered ? 0.55 : 0.35) : (_hovered ? 0.22 : 0.13));
        var knob = isOn ? Colors.White : IslandColors.White(0.55);
        var offset = isOn ? KnobTravel : 0;

        Dot.Effect = isOn
            ? new DropShadowEffect { ShadowDepth = 0, BlurRadius = 5, Color = Colors.White, Opacity = 0.32 }
            : new DropShadowEffect { ShadowDepth = 0.5, BlurRadius = 1.5, Color = Colors.Black, Opacity = 0.35 };

        if (!_seeded || !IsLoaded)
        {
            _seeded = true;
            _track.Color = track;
            _rim.Color = rim;
            _knob.Color = knob;
            SlideTransform.X = offset;
            return;
        }

        var beat = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        _track.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(track, beat) { EasingFunction = ease });
        _rim.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(rim, beat) { EasingFunction = ease });
        _knob.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(knob, beat) { EasingFunction = ease });
        SlideTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(offset, beat) { EasingFunction = ease });
    }
}
