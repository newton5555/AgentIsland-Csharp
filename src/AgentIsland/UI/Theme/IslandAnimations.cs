using System.Windows;
using System.Windows.Media.Animation;

namespace AgentIsland.UI.Theme;

/// Animation tokens carried over from Sources/Theme/Animations.swift. Springs
/// are exposed as (ease, duration) pairs; timing curves as KeySpline-style
/// cubic beziers baked into fixed-duration eases.
public static class IslandAnimations
{
    /// compact -> peek -> expanded shape morph: spring(0.42, 0.82).
    public static readonly Duration OpenMorphDuration = new(TimeSpan.FromSeconds(0.84));

    public static SpringEase OpenMorph() => new()
    {
        Response = 0.42,
        DampingFraction = 0.82,
        AnimationSeconds = 0.84,
        EasingMode = EasingMode.EaseIn,
    };

    /// expanded -> compact collapse: spring(0.30, 0.88) — snappier than open.
    public static readonly Duration CloseMorphDuration = new(TimeSpan.FromSeconds(0.60));

    public static SpringEase CloseMorph() => new()
    {
        Response = 0.30,
        DampingFraction = 0.88,
        AnimationSeconds = 0.60,
        EasingMode = EasingMode.EaseIn,
    };

    /// Snappy UI transitions — cubic-bezier(0.23, 1, 0.32, 1) over 0.28s.
    public static readonly Duration StrongEaseOutDuration = new(TimeSpan.FromSeconds(0.28));

    public static IEasingFunction StrongEaseOut() => MakeBezier(0.23, 1, 0.32, 1);

    /// Chart style swap — same curve, 0.22s.
    public static readonly Duration ChartSwapDuration = new(TimeSpan.FromSeconds(0.22));

    /// Page carousel — cubic-bezier(0.25, 0.82, 0.25, 1) over 0.36s.
    public static readonly Duration PageSwipeDuration = new(TimeSpan.FromSeconds(0.36));

    public static IEasingFunction PageSwipe() => MakeBezier(0.25, 0.82, 0.25, 1);

    /// Working-logo spin: one revolution per 1.8s, linear. Faster than the
    /// macOS 3.8s on purpose — at 20px both brand marks are close to
    /// rotationally symmetric, and the slower rate reads as a static logo.
    public static readonly Duration SpinDuration = new(TimeSpan.FromSeconds(1.8));

    /// Working breath: scale 1.0-1.05, ease-in-out, 1.7s each direction.
    public static readonly Duration WorkingBreathDuration = new(TimeSpan.FromSeconds(1.7));

    /// Attention pulse: scale 1.0-1.16, ease-in-out, 0.42s each direction.
    public static readonly Duration AttentionPulseDuration = new(TimeSpan.FromSeconds(0.42));

    /// Antigravity working wave flow: liquid color rotation over 2.4s.
    public static readonly Duration AntigravityWaveDuration = new(TimeSpan.FromSeconds(2.4));

    /// Antigravity working wave caustic sweep: specular light sheen over 1.8s.
    public static readonly Duration AntigravityCausticDuration = new(TimeSpan.FromSeconds(1.8));

    private static IEasingFunction MakeBezier(double x1, double y1, double x2, double y2) =>
        new CubicBezierEase { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
}

/// Fixed cubic-bezier ease (CSS-style control points). WPF's KeySpline only
/// works inside keyframe animations; this brings the same curve to From/To
/// animations.
public sealed class CubicBezierEase : EasingFunctionBase
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    protected override double EaseInCore(double normalizedTime)
    {
        // Invert x(t) = bezier(t) by bisection, then evaluate y(t).
        var target = normalizedTime;
        double lower = 0, upper = 1, t = target;
        for (var i = 0; i < 24; i++)
        {
            var x = Bezier(t, X1, X2);
            if (Math.Abs(x - target) < 1e-5) break;
            if (x < target) lower = t; else upper = t;
            t = (lower + upper) / 2;
        }
        return Bezier(t, Y1, Y2);
    }

    private static double Bezier(double t, double p1, double p2)
    {
        var u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    protected override System.Windows.Freezable CreateInstanceCore() => new CubicBezierEase();
}
