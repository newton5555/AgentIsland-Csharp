using System.Windows.Media.Animation;

namespace AgentIsland.UI.Theme;

/// Damped-spring easing that reproduces SwiftUI's
/// `spring(response:dampingFraction:)` inside a fixed-duration WPF animation.
/// The macOS island's openMorph/closeMorph springs are the signature feel of
/// the product, so a plain cubic ease is not an acceptable substitute.
///
/// Use with an animation Duration of ~2x the response time so the tail
/// settles (SwiftUI runs springs to rest; WPF clamps at Duration).
public sealed class SpringEase : EasingFunctionBase
{
    /// SwiftUI response (seconds): period of one undamped oscillation.
    public double Response { get; set; } = 0.42;

    /// SwiftUI dampingFraction: 1 = critically damped, &lt;1 overshoots.
    public double DampingFraction { get; set; } = 0.82;

    /// The Duration assigned to the animation using this ease (seconds);
    /// needed to map normalizedTime back to real seconds.
    public double AnimationSeconds { get; set; } = 0.84;

    protected override double EaseInCore(double normalizedTime)
    {
        var t = normalizedTime * AnimationSeconds;
        var omega = 2 * Math.PI / Response;   // undamped angular frequency
        var zeta = DampingFraction;
        double position;
        if (zeta < 1)
        {
            var omegaD = omega * Math.Sqrt(1 - zeta * zeta);
            var envelope = Math.Exp(-zeta * omega * t);
            position = 1 - envelope * (Math.Cos(omegaD * t) + zeta * omega / omegaD * Math.Sin(omegaD * t));
        }
        else
        {
            var envelope = Math.Exp(-omega * t);
            position = 1 - envelope * (1 + omega * t);
        }
        return position;
    }

    protected override System.Windows.Freezable CreateInstanceCore() => new SpringEase();
}
