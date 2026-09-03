using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentIsland.Model;
using AgentIsland.UI;
using AgentIsland.UI.Theme;

namespace AgentIsland.Tests;

public static class BrandGeometryTests
{
    public static void RunAll()
    {
        Console.WriteLine("--- BrandGeometryTests ---");

        // 1. Verify all five providers have non-empty vector paths
        foreach (var provider in DisplayProviders.All)
        {
            var path = BrandGeometry.PathData(provider);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new Exception($"PathData for {provider} was null or empty.");
            }

            // 2. Verify Geometry.Parse parses with FillRule Nonzero without throwing
            var geometry = Geometry.Parse("F1 " + path);
            if (geometry.IsEmpty())
            {
                throw new Exception($"Geometry for {provider} parsed as empty.");
            }

            // 3. Verify BrandBrush produces valid brushes
            var brush = BrandGeometry.BrandBrush(provider);
            if (brush is null)
            {
                throw new Exception($"BrandBrush for {provider} was null.");
            }

            if (provider == DisplayProvider.Antigravity)
            {
                if (brush is not LinearGradientBrush lgb)
                {
                    throw new Exception("Antigravity BrandBrush must be a LinearGradientBrush.");
                }
                if (lgb.GradientStops.Count != 5)
                {
                    throw new Exception($"Antigravity BrandBrush must have 5 stops, got {lgb.GradientStops.Count}.");
                }
            }
            else
            {
                if (brush is not SolidColorBrush)
                {
                    throw new Exception($"BrandBrush for {provider} must be a SolidColorBrush.");
                }
            }

            Console.WriteLine($"PASS {provider} vector path parses cleanly and renders {brush.GetType().Name}");
        }

        // Render visual snapshot if AGENTISLAND_SNAPSHOT_DIR is set
        var snapshotDir = Environment.GetEnvironmentVariable("AGENTISLAND_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(snapshotDir))
        {
            RenderSnapshots(snapshotDir);
        }

        Console.WriteLine("BrandGeometryTests GREEN");
    }

    private static void RenderSnapshots(string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var provider in DisplayProviders.All)
        {
            var badge = new SoloProviderBadge(provider)
            {
                Width = 200,
                Height = 150,
                Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x10)),
            };
            badge.Measure(new Size(200, 150));
            badge.Arrange(new Rect(0, 0, 200, 150));
            badge.UpdateLayout();

            var rtb = new RenderTargetBitmap(400, 300, 192, 192, PixelFormats.Pbgra32);
            rtb.Render(badge);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            var file = Path.Combine(outDir, $"solo-badge-{provider.ToString().ToLowerInvariant()}.png");
            using var stream = File.Create(file);
            encoder.Save(stream);
        }
    }
}
