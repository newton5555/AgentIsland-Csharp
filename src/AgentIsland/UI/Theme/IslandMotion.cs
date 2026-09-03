using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentIsland.UI.Theme;

/// Shared motion helpers for the alarm/dialog surfaces: the ease-in-out
/// breathing loops the macOS alarm view runs (1.7s glow / 0.72s logo) and
/// the PressableButtonStyle press feedback (scale 0.94, 0.11s ease-out).
public static class IslandMotion
{
    /// SineEase autoreverse-forever loop — WPF's closest match to SwiftUI's
    /// easeInOut(duration:).repeatForever(autoreverses: true).
    public static void Breathe(
        IAnimatable target, DependencyProperty property, double from, double to, double seconds)
    {
        var loop = new DoubleAnimation(from, to, new Duration(TimeSpan.FromSeconds(seconds)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        target.BeginAnimation(property, loop);
    }

    /// Uniform breathing scale around the element center.
    public static void BreatheScale(FrameworkElement element, double from, double to, double seconds)
    {
        var scale = new ScaleTransform(from, from);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        Breathe(scale, ScaleTransform.ScaleXProperty, from, to, seconds);
        Breathe(scale, ScaleTransform.ScaleYProperty, from, to, seconds);
    }

    /// macOS PressableButtonStyle: scale to 0.94 while pressed, ease-out
    /// 0.11s both ways.
    public static void AttachPressFeedback(FrameworkElement element)
    {
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        void To(double target)
        {
            var press = new DoubleAnimation(target, new Duration(TimeSpan.FromSeconds(0.11)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, press.Clone());
        }
        element.PreviewMouseLeftButtonDown += (_, _) => To(0.94);
        element.PreviewMouseLeftButtonUp += (_, _) => To(1.0);
        element.MouseLeave += (_, _) => To(1.0);
    }

    /// Card entrance: fade the window in while the root springs from 97%.
    /// Subtle on purpose — the macOS panel appears instantly; this only
    /// takes the Win32 harshness off the pop-in.
    public static void AnimateEntrance(Window window, FrameworkElement root)
    {
        window.Opacity = 0;
        var scale = new ScaleTransform(0.97, 0.97);
        root.RenderTransform = scale;
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        window.Loaded += (_, _) =>
        {
            window.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, new Duration(TimeSpan.FromSeconds(0.18)))
                {
                    EasingFunction = IslandAnimations.StrongEaseOut(),
                });
            var settle = new DoubleAnimation(1, new Duration(TimeSpan.FromSeconds(0.22)))
            {
                EasingFunction = IslandAnimations.StrongEaseOut(),
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle.Clone());
        };
    }
}
