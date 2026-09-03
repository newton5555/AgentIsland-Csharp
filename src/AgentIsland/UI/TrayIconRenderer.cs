using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AgentIsland.Core;

namespace AgentIsland.UI;

/// Draws the system-tray icon: the Agent Island starburst mark, with a
/// status badge in the corner when something is worth a glance — teal while
/// a thread runs, amber for your-turn or high usage, red for attention
/// states or a nearly burned 5-hour window. Idle and quiet = just the logo.
internal static class TrayIconRenderer
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Color AlarmRed = Color.FromArgb(0xF5, 0x57, 0x4A);
    private static readonly Color Amber = Color.FromArgb(0xF5, 0xA5, 0x24);
    private static readonly Color Teal = Color.FromArgb(0x3D, 0xD6, 0x8C);

    /// The brand mark, decoded once from the WPF resource. Copied out of the
    /// stream-backed bitmap because GDI+ requires the source stream to stay
    /// alive otherwise.
    private static readonly Lazy<Bitmap?> Logo = new(() =>
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/agentisland_logo.png"));
            if (resource is null) return null;
            using var stream = resource.Stream;
            using var streamBacked = new Bitmap(stream);
            return new Bitmap(streamBacked);
        }
        catch
        {
            return null;
        }
    });

    private static Color? BadgeColor(ActivityState state, double usage5h)
    {
        if (state.IsAttentionState()) return AlarmRed;
        if (state == ActivityState.NeedsYou) return Amber;
        if (usage5h >= 0.90) return Color.FromArgb(0xE6, 0x5F, 0x5F);
        if (usage5h >= 0.70) return Color.FromArgb(0xE8, 0xA8, 0x5A);
        if (state == ActivityState.Working) return Teal;
        return null;
    }

    public static Icon Render(double usage5h, ActivityState state)
    {
        const int s = 32;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            if (Logo.Value is { } logo)
            {
                // The 1024px source keeps ~15% transparent padding around the
                // starburst; crop to the mark's square so it fills the tile.
                g.DrawImage(logo, new Rectangle(0, 0, s, s), 96, 96, 832, 832, GraphicsUnit.Pixel);
            }
            else
            {
                using var disc = new SolidBrush(Color.FromArgb(235, 0x14, 0x14, 0x18));
                g.FillEllipse(disc, 1, 1, s - 2, s - 2);
            }

            if (BadgeColor(state, usage5h) is { } badge)
            {
                // Dark backing keeps the dot legible over the rays and on
                // both light and dark taskbars.
                const float d = 13f;
                const float x = s - d - 0.5f;
                const float y = s - d - 0.5f;
                using var backing = new SolidBrush(Color.FromArgb(230, 0x14, 0x14, 0x18));
                g.FillEllipse(backing, x, y, d, d);
                using var dot = new SolidBrush(badge);
                g.FillEllipse(dot, x + 2.5f, y + 2.5f, d - 5f, d - 5f);
            }
        }

        // GetHicon leaks its HICON unless destroyed; clone into an owned Icon,
        // then free the temporary handle.
        var handle = bmp.GetHicon();
        try
        {
            using var shared = Icon.FromHandle(handle);
            return (Icon)shared.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
