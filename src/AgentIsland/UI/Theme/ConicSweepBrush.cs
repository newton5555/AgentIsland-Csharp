using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentIsland.UI.Theme;

/// The island's rotating edge shimmer needs an angular gradient, which WPF
/// doesn't have — so the comet is baked into a bitmap once per palette and spun
/// with a RelativeTransform.
/// Single comet: 25% arc with a model colour gradient and brilliant specular core.
/// Dual comet: two 25% comets spaced 180 degrees apart (0.25->0.50 and 0.75->1.00)
/// for dual model display and simultaneous multi-agent thinking.
public static class ConicSweepBrush
{
    private const int Size = 320;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, BitmapSource> Cache = new();

    public static ImageBrush Make(Color tint, RotateTransform rotate)
        => Make(new[] { tint }, rotate);

    public static ImageBrush Make(IReadOnlyList<Color> palette, RotateTransform rotate)
    {
        var colors = Normalize(palette);
        var key = $"single_{PaletteKey(colors)}";
        var bitmap = GetOrAdd(key, () => RenderSingle(colors));
        return new ImageBrush(bitmap)
        {
            Stretch = Stretch.Fill,
            RelativeTransform = rotate,
        };
    }

    public static ImageBrush MakeDual(Color leftTint, Color rightTint, RotateTransform rotate)
        => MakeDual(new[] { leftTint }, new[] { rightTint }, rotate);

    public static ImageBrush MakeDual(
        IReadOnlyList<Color> leftPalette,
        IReadOnlyList<Color> rightPalette,
        RotateTransform rotate)
    {
        var leftColors = Normalize(leftPalette);
        var rightColors = Normalize(rightPalette);
        var key = $"dual_{PaletteKey(leftColors)}_{PaletteKey(rightColors)}";
        var bitmap = GetOrAdd(key, () => RenderDual(leftColors, rightColors));
        return new ImageBrush(bitmap)
        {
            Stretch = Stretch.Fill,
            RelativeTransform = rotate,
        };
    }

    private static BitmapSource GetOrAdd(string key, Func<BitmapSource> factory)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var bitmap)) return bitmap;

            bitmap = factory();
            Cache[key] = bitmap;
            return bitmap;
        }
    }

    private static BitmapSource RenderSingle(IReadOnlyList<Color> palette)
    {
        var stops = new List<(double T, Color Color)>
        {
            (0.00, WithAlpha(palette[0], 0)),
        };
        stops.AddRange(CometStops(palette, 0.75, 1.00));
        return BuildBitmap(stops);
    }

    private static BitmapSource RenderDual(
        IReadOnlyList<Color> leftPalette,
        IReadOnlyList<Color> rightPalette)
    {
        var stops = new List<(double T, Color Color)>
        {
            (0.00, WithAlpha(leftPalette[0], 0)),
        };
        stops.AddRange(CometStops(leftPalette, 0.25, 0.50));
        stops.AddRange(CometStops(rightPalette, 0.75, 1.00));
        return BuildBitmap(stops);
    }

    private static IReadOnlyList<(double T, Color Color)> CometStops(
        IReadOnlyList<Color> palette,
        double start,
        double end)
    {
        var span = end - start;
        var stops = new List<(double T, Color Color)>
        {
            (start, WithAlpha(palette[0], 0)),
            (start + span * 0.22, WithAlpha(palette[0], 0)),
        };

        // Spread the model palette through the saturated body of the comet.
        // The leading white core remains common to every provider so the
        // animation reads as a highlight rather than a sequence of hard bands.
        if (palette.Count == 1)
        {
            stops.Add((start + span * 0.32, WithAlpha(palette[0], 230)));
        }
        else
        {
            for (var i = 0; i < palette.Count; i++)
            {
                var progress = 0.32 + 0.42 * i / (palette.Count - 1.0);
                stops.Add((start + span * progress, WithAlpha(palette[i], 230)));
            }
        }

        stops.Add((start + span * 0.80, WithAlpha(Highlight(PaletteAt(palette, 0.72)), 245)));
        stops.Add((start + span * 0.92, Color.FromArgb(255, 255, 255, 255)));
        stops.Add((end, WithAlpha(palette[palette.Count - 1], 0)));
        return stops;
    }

    private static IReadOnlyList<Color> Normalize(IReadOnlyList<Color>? palette)
    {
        if (palette is null || palette.Count == 0)
        {
            return new[] { Colors.White };
        }

        var copy = new Color[palette.Count];
        for (var i = 0; i < palette.Count; i++) copy[i] = palette[i];
        return copy;
    }

    private static string PaletteKey(IReadOnlyList<Color> palette)
    {
        var colors = new string[palette.Count];
        for (var i = 0; i < palette.Count; i++)
        {
            var color = palette[i];
            colors[i] = $"{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        return string.Join("_", colors);
    }

    private static Color PaletteAt(IReadOnlyList<Color> palette, double position)
    {
        if (palette.Count == 1) return palette[0];

        var scaled = Math.Clamp(position, 0, 1) * (palette.Count - 1);
        var lower = (int)Math.Floor(scaled);
        var upper = Math.Min(palette.Count - 1, lower + 1);
        var fraction = scaled - lower;
        var first = palette[lower];
        var second = palette[upper];
        return Color.FromArgb(
            Lerp(first.A, second.A, fraction),
            Lerp(first.R, second.R, fraction),
            Lerp(first.G, second.G, fraction),
            Lerp(first.B, second.B, fraction));
    }

    private static byte Lerp(byte first, byte second, double fraction)
        => (byte)Math.Clamp(Math.Round(first + (second - first) * fraction), 0, 255);

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Color Highlight(Color c) => Color.FromArgb(
        255,
        (byte)Math.Min(255, c.R + 65),
        (byte)Math.Min(255, c.G + 65),
        (byte)Math.Min(255, c.B + 65));

    private static BitmapSource BuildBitmap(IReadOnlyList<(double T, Color Color)> stops)
    {
        var pixels = new uint[Size * Size];
        const double center = (Size - 1) / 2.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var angle = Math.Atan2(y - center, x - center);
                var t = angle / (2 * Math.PI);
                if (t < 0) t += 1;
                pixels[y * Size + x] = Premultiplied(Interpolate(stops, t));
            }
        }

        var bitmap = new WriteableBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, Size, Size), pixels, Size * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color Interpolate(IReadOnlyList<(double T, Color Color)> stops, double t)
    {
        for (var i = 1; i < stops.Count; i++)
        {
            if (t > stops[i].T) continue;
            var (t0, c0) = stops[i - 1];
            var (t1, c1) = stops[i];
            var f = t1 <= t0 ? 0 : (t - t0) / (t1 - t0);
            return Color.FromArgb(
                (byte)(c0.A + (c1.A - c0.A) * f),
                (byte)(c0.R + (c1.R - c0.R) * f),
                (byte)(c0.G + (c1.G - c0.G) * f),
                (byte)(c0.B + (c1.B - c0.B) * f));
        }
        return stops[stops.Count - 1].Color;
    }

    private static uint Premultiplied(Color c)
    {
        var a = c.A / 255.0;
        return ((uint)c.A << 24)
            | ((uint)(c.R * a) << 16)
            | ((uint)(c.G * a) << 8)
            | (uint)(c.B * a);
    }
}
