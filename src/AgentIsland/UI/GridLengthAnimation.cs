using System.Windows;
using System.Windows.Media.Animation;

namespace AgentIsland.UI;

/// WPF ships no animation for GridLength; this drives the peek pill slots
/// (0 -> 104 -> 0) in step with the silhouette's width spring so the logos
/// hold their place beside the notch center.
public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(GridLength), typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(GridLength), typeof(GridLengthAnimation));

    public static readonly DependencyProperty EasingFunctionProperty = DependencyProperty.Register(
        nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction
    {
        get => (IEasingFunction?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(
        object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var progress = animationClock.CurrentProgress ?? 0;
        if (EasingFunction is { } easing) progress = easing.Ease(progress);
        var from = From.Value;
        var to = To.Value;
        return new GridLength(Math.Max(0, from + (to - from) * progress));
    }
}
