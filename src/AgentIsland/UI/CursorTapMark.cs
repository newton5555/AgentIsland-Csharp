using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentIsland.Core;
using AgentIsland.UI.Theme;
using Path = System.Windows.Shapes.Path;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace AgentIsland.UI;

/// <summary>Cursor point-and-ripple animation from the HTML logo preview.</summary>
internal sealed class CursorTapMark : Grid
{
    private readonly TranslateTransform _move = new();
    private readonly ScaleTransform _scale = new();
    private readonly Ellipse[] _ripples = new Ellipse[2];
    private readonly double _size;
    private ActivityState _state;
    internal bool IsActive => _move.HasAnimatedProperties;
    internal double OffsetY => _move.Y;
    internal double RippleOpacity => _ripples[0].Opacity;
    internal bool BothRipplesVisible => _ripples.All(r => r.Opacity > .5);
    internal bool RipplesStopped => _ripples.All(r => !r.HasAnimatedProperties && r.Opacity == 0);

    internal CursorTapMark(double size, Brush fill)
    {
        _size = size;
        Width = Height = size;
        IsHitTestVisible = false;
        var geometry = Geometry.Parse("F1 " + BrandGeometry.CursorPath);
        geometry.Freeze();
        var canvas = new Canvas { Width = 512, Height = 512 };
        canvas.Children.Add(new Path { Data = geometry, Fill = fill });
        var transforms = new TransformGroup();
        transforms.Children.Add(_scale);
        transforms.Children.Add(_move);
        Children.Add(new Viewbox
        {
            Width = size, Height = size, Child = canvas,
            RenderTransform = transforms, RenderTransformOrigin = new Point(.5, .5),
        });
        var overlay = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        for (var i = 0; i < _ripples.Length; i++)
        {
            // The HTML ring is 18px on a 72px logo — 25% of the mark. Keep
            // that proportion at the island's 20 DIP size (5px) so the pair
            // stays visible without swamping the arrow; readability rides on
            // the >=1.1px stroke and the held opacity below.
            var diameter = size * .25;
            var ripple = new Ellipse
            {
                Width = diameter, Height = diameter,
                Stroke = IslandColors.Brush(Color.FromRgb(0xF5, 0xF3, 0xEE)),
                StrokeThickness = Math.Max(1.1, size * 2 / 72), Opacity = 0,
                RenderTransform = new ScaleTransform(.65, .65),
                RenderTransformOrigin = new Point(.5, .5),
            };
            Canvas.SetLeft(ripple, size * .82 - diameter / 2);
            Canvas.SetTop(ripple, size * .32 - diameter / 2);
            overlay.Children.Add(ripple);
            _ripples[i] = ripple;
        }
        Children.Add(overlay);
        Loaded += (_, _) => SetState(_state);
        Unloaded += (_, _) => Stop();
    }

    internal void SetState(ActivityState state)
    {
        _state = state;
        Stop();
        if (state != ActivityState.Working) return;
        AnimateTap(_move, TranslateTransform.YProperty, [0, 3 * _size / 72, -1.5 * _size / 72, 0, 0]);
        AnimateTap(_scale, ScaleTransform.ScaleXProperty, [1, 1.03, .98, 1, 1]);
        AnimateTap(_scale, ScaleTransform.ScaleYProperty, [1, .93, 1.03, 1, 1]);
        for (var i = 0; i < _ripples.Length; i++)
        {
            var delay = TimeSpan.FromSeconds(i == 0 ? .15 : .8);
            var scale = (ScaleTransform)_ripples[i].RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, RippleAnimation(.65, 2.3, delay));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, RippleAnimation(.65, 2.3, delay));
            var opacity = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = delay, Duration = TimeSpan.FromSeconds(1.6), RepeatBehavior = RepeatBehavior.Forever,
            };
            opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.7))));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(.65, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.15))));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6))));
            Timeline.SetDesiredFrameRate(opacity, 24);
            _ripples[i].BeginAnimation(OpacityProperty, opacity);
        }
    }

    private static void AnimateTap(Animatable target, DependencyProperty property, double[] values)
    {
        double[] times = [0, .15, .28, .4, 1];
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.6), RepeatBehavior = RepeatBehavior.Forever,
        };
        for (var i = 0; i < times.Length; i++)
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(values[i],
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(times[i] * 1.6)), new KeySpline(.42, 0, .58, 1)));
        Timeline.SetDesiredFrameRate(animation, 24);
        target.BeginAnimation(property, animation);
    }

    private static DoubleAnimationUsingKeyFrames RippleAnimation(double from, double to, TimeSpan delay)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = delay, Duration = TimeSpan.FromSeconds(1.6), RepeatBehavior = RepeatBehavior.Forever,
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6)),
            new KeySpline(0, 0, .58, 1)));
        Timeline.SetDesiredFrameRate(animation, 24);
        return animation;
    }

    internal void Stop()
    {
        _move.BeginAnimation(TranslateTransform.YProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _move.Y = 0;
        _scale.ScaleX = _scale.ScaleY = 1;
        foreach (var ripple in _ripples)
        {
            ripple.BeginAnimation(OpacityProperty, null);
            ripple.Opacity = 0;
            var scale = (ScaleTransform)ripple.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = .65;
        }
    }
}
