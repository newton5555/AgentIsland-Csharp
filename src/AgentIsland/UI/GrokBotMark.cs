using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentIsland.Core;
using AgentIsland.UI.Theme;
using Path = System.Windows.Shapes.Path;

namespace AgentIsland.UI;

/// <summary>Native WPF counterpart of the HTML Grok Bot, including its eye sequence.</summary>
internal sealed class GrokBotMark : Grid
{
    private readonly ScaleTransform _squash = new();
    private readonly RotateTransform _tilt = new();
    private readonly TranslateTransform _move = new();
    private readonly Path _leftEye;
    private readonly Path _rightEye;
    private readonly double _size;
    private ActivityState _state = ActivityState.Idle;

    internal bool IsMoving => _move.HasAnimatedProperties || _squash.HasAnimatedProperties;
    internal bool EyesAnimated => _leftEye.HasAnimatedProperties;
    internal Geometry LeftEye => _leftEye.Data;
    internal int Expression { get; private set; }

    internal GrokBotMark(double size, Brush body)
    {
        _size = size;
        Width = Height = size;
        IsHitTestVisible = false;
        RenderTransformOrigin = new Point(0.5, 0.5);
        var transforms = new TransformGroup();
        // WPF applies transforms in order; CSS translate/scale/rotate applies right-to-left.
        transforms.Children.Add(_tilt);
        transforms.Children.Add(_squash);
        transforms.Children.Add(_move);
        RenderTransform = transforms;
        var canvas = new Canvas { Width = 259, Height = 259 };
        var eyeBrush = IslandColors.Brush(Color.FromRgb(0x14, 0x16, 0x1A));
        _leftEye = new Path { Fill = eyeBrush };
        _rightEye = new Path { Fill = eyeBrush };
        foreach (var path in new[] { new Path { Data = GrokBotGeometry.Body, Fill = body }, _leftEye, _rightEye })
        {
            Canvas.SetLeft(path, 15);
            Canvas.SetTop(path, 15);
            canvas.Children.Add(path);
        }
        Children.Add(new Viewbox { Width = size, Height = size, Child = canvas });
        SetEyes(0);
        Loaded += (_, _) => SetState(_state);
        Unloaded += (_, _) => Stop();
    }

    internal void SetState(ActivityState state)
    {
        _state = state;
        Stop();
        SetEyes(state switch
        {
            ActivityState.Working => 7,
            ActivityState.Stalled or ActivityState.RateLimited => 3,
            ActivityState.AuthRequired => 4,
            _ => 0,
        });
        if (state == ActivityState.Working)
        {
            double[] times = [0, .12, .24, .36, .50, .62, .75, .88, 1];
            Animate(_move, TranslateTransform.YProperty, 1.2, times, [0, -9, 0, -6, 0, -3, 0, -1, 0], _size / 72);
            Animate(_squash, ScaleTransform.ScaleXProperty, 1.2, times, [1, 1.07, .93, 1.05, .96, 1.02, 1, 1.01, 1]);
            Animate(_squash, ScaleTransform.ScaleYProperty, 1.2, times, [1, .91, 1.07, .94, 1.04, .97, 1, .99, 1]);
            Animate(_tilt, RotateTransform.AngleProperty, 1.2, times, [0, -3, 2.5, -2, 1.5, -1, .5, 0, 0]);
            AnimateEyes(_leftEye, left: true);
            AnimateEyes(_rightEye, left: false);
        }
        else if (state is ActivityState.Stalled or ActivityState.RateLimited)
        {
            double[] times = [0, .125, .25, .375, .5, .625, .75, .875, 1];
            Animate(_move, TranslateTransform.XProperty, .35, times, [0, -2, 2, -2, 2, -1.5, 1.5, -1, 0], _size / 72, linear: true);
            Animate(_move, TranslateTransform.YProperty, .35, times, [0, .5, -.5, 0, .5, -.5, 0, 0, 0], _size / 72, linear: true);
            Animate(_tilt, RotateTransform.AngleProperty, .35, times, [0, -1.5, 1.5, -1.5, 1.5, -1, 1, -.5, 0], linear: true);
        }
        else if (state == ActivityState.Idle)
        {
            Animate(_squash, ScaleTransform.ScaleXProperty, 3.4, [0, .5, 1], [1, 1.02, 1]);
            Animate(_squash, ScaleTransform.ScaleYProperty, 3.4, [0, .5, 1], [1, 1.02, 1]);
        }
    }

    private void SetEyes(int expression)
    {
        Expression = expression;
        (_leftEye.Data, _rightEye.Data) = GrokBotGeometry.Eyes[expression];
    }

    private static void AnimateEyes(Path eye, bool left)
    {
        var animation = new ObjectAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.8),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        int[] sequence = [7, 16, 11, 10];
        for (var i = 0; i < sequence.Length; i++)
        {
            var frame = GrokBotGeometry.Eyes[sequence[i]];
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                left ? frame.Left : frame.Right, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450 * i))));
        }
        Timeline.SetDesiredFrameRate(animation, 24);
        eye.BeginAnimation(Path.DataProperty, animation);
    }

    private static void Animate(Animatable target, DependencyProperty property, double seconds,
        double[] times, double[] values, double multiplier = 1, bool linear = false)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(seconds),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        for (var i = 0; i < times.Length; i++)
        {
            var time = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(times[i] * seconds));
            DoubleKeyFrame frame = linear
                ? new LinearDoubleKeyFrame(values[i] * multiplier, time)
                : new SplineDoubleKeyFrame(values[i] * multiplier, time, new KeySpline(.42, 0, .58, 1));
            animation.KeyFrames.Add(frame);
        }
        Timeline.SetDesiredFrameRate(animation, 24);
        target.BeginAnimation(property, animation);
    }

    internal void Stop()
    {
        _move.BeginAnimation(TranslateTransform.XProperty, null);
        _move.BeginAnimation(TranslateTransform.YProperty, null);
        _squash.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _squash.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _tilt.BeginAnimation(RotateTransform.AngleProperty, null);
        _leftEye.BeginAnimation(Path.DataProperty, null);
        _rightEye.BeginAnimation(Path.DataProperty, null);
        _move.X = _move.Y = _tilt.Angle = 0;
        _squash.ScaleX = _squash.ScaleY = 1;
    }
}
