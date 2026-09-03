using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentIsland.Model;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// The freed half of a solo panel: the provider's bare mark and name — a
/// quiet nameplate, not a data widget (macOS SoloProviderBadge). It replaced
/// the per-model breakdown table, whose "5h" legend read as a quota window
/// Codex no longer has; per-model data returns with the report-card redesign.
///
/// Identity comes from ProviderIdentity, so a guest can occupy this half
/// without inheriting Codex's name or blue. BrandGeometry only ships the
/// Claude spark and the OpenAI knot; a provider with no vector falls back to
/// an accent-stroked ring, which is what macOS ProviderMark draws when its
/// logo asset fails to load.
public sealed class SoloProviderBadge : Grid
{
    public SoloProviderBadge(DisplayProvider provider)
    {
        var color = ProviderIdentity.Accent(provider);
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // The optical center sits a touch high of the geometric one
            // (macOS bottom padding 30).
            Margin = new Thickness(12, 0, 12, 30),
        };
        if (provider == DisplayProvider.Antigravity)
        {
            // Optical sizing: the arch's narrow dome requires ~34px bounding box
            // to match the visual mass and perceived volume of Codex's dense circle
            // and Cursor's cube (30px).
            var mark = ProviderMarks.Mark(provider, 34);
            if (mark is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Center;
                fe.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    ShadowDepth = 0,
                    BlurRadius = 30, // macOS shadow radius 10 is a gaussian sigma; ~3x here
                    Color = color,
                    Opacity = 0.35,
                };
            }
            stack.Children.Add(mark);
        }
        else if (BrandGeometry.PathData(provider) is { } data)
        {
            stack.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("F1 " + data),
                Fill = BrandGeometry.BrandBrush(provider, 0.95),
                Width = 30,
                Height = 30,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    ShadowDepth = 0,
                    BlurRadius = 30, // macOS shadow radius 10 is a gaussian sigma; ~3x here
                    Color = color,
                    Opacity = 0.35,
                },
            });
        }
        else
        {
            stack.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 30,
                Height = 30,
                Stroke = IslandColors.Brush(IslandColors.Alpha(color, 0.85)),
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        stack.Children.Add(new TextBlock
        {
            Text = ProviderIdentity.AlarmName(provider),
            FontFamily = IslandFonts.Ui,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.88)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        });
        Children.Add(stack);
    }
}
