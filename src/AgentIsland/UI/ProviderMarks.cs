using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AgentIsland.Model;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// The real brand marks (macOS ProviderMark): the mac vector PDFs rendered
/// to alpha PNGs, tinted through an OpacityMask — WPF's equivalent of
/// template rendering. Antigravity keeps its own colours: repainting it in
/// a tint would erase the gradient that IS the brand.
public static class ProviderMarks
{
    private static readonly Dictionary<DisplayProvider, ImageBrush?> MaskCache = new();
    private static BitmapImage? _antigravity;
    internal static BitmapImage? AntigravityBitmap => _antigravity ??= Load("mark-antigravity.png");

    public static UIElement Mark(DisplayProvider provider, double size, double tintOpacity = 0.95)
    {
        if (provider == DisplayProvider.Antigravity)
        {
            if (AntigravityBitmap is { } bitmap)
            {
                var image = new Image
                {
                    Source = bitmap,
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                return image;
            }
        }
        if (BrandGeometry.PathData(provider) is { } data)
        {
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("F1 " + data),
                Fill = BrandGeometry.BrandBrush(provider, tintOpacity),
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else if (MaskFor(provider) is { } mask)
        {
            return new Rectangle
            {
                Width = size,
                Height = size,
                Fill = IslandColors.Brush(IslandColors.Alpha(ProviderIdentity.Accent(provider), tintOpacity)),
                OpacityMask = mask,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        // Missing asset: the accent ring macOS falls back to.
        return new Ellipse
        {
            Width = size,
            Height = size,
            Stroke = IslandColors.Brush(IslandColors.Alpha(ProviderIdentity.Accent(provider), tintOpacity)),
            StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// Island-bar variant: the mark element shares the caller's LIVE fill
    /// brush so the working→attention tint crossfade animates through the
    /// mask exactly as it does through the vector paths. Antigravity keeps
    /// its full-color art (macOS colorMark — the red states speak through
    /// the glow halo instead).
    public static UIElement IslandMark(DisplayProvider provider, double size, Brush fill)
    {
        if (provider == DisplayProvider.Antigravity)
        {
            return Mark(provider, size);
        }
        if (MaskFor(provider) is { } mask)
        {
            return new Rectangle
            {
                Width = size,
                Height = size,
                Fill = fill,
                OpacityMask = mask,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return new Ellipse
        {
            Width = size,
            Height = size,
            Stroke = fill,
            StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static ImageBrush? MaskFor(DisplayProvider provider)
    {
        if (MaskCache.TryGetValue(provider, out var cached)) return cached;
        var name = provider switch
        {
            DisplayProvider.Claude => "mark-claude.png",
            DisplayProvider.Codex => "mark-openai.png",
            DisplayProvider.Grok => "mark-grok.png",
            DisplayProvider.Cursor => "mark-cursor.png",
            _ => null,
        };
        ImageBrush? brush = null;
        if (name is not null && Load(name) is { } bitmap)
        {
            brush = new ImageBrush(bitmap) { Stretch = Stretch.Uniform };
            brush.Freeze();
        }
        MaskCache[provider] = brush;
        return brush;
    }

    private static BitmapImage? Load(string name)
    {
        try
        {
            return new BitmapImage(new Uri($"pack://application:,,,/AgentIsland;component/Assets/{name}"));
        }
        catch
        {
            try
            {
                return new BitmapImage(new Uri($"pack://application:,,,/Assets/{name}"));
            }
            catch
            {
                return null;
            }
        }
    }
}
