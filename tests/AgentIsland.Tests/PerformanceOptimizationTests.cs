using System.IO;
using System.Text.Json;
using System.Windows;
using AgentIsland.Core;
using AgentIsland.UI;
using AgentIsland.Windows.Memory;

namespace AgentIsland.Tests;

public static class PerformanceOptimizationTests
{
    public static void RunAll()
    {
        Console.WriteLine("--- PerformanceOptimizationTests ---");
        TestCodexMetaReadsAFullFirstLineWithoutReadingTheTail();
        TestSilhouetteBoundsPreserveCornerGeometry();
        TestTrayVisualKeyChangesOnlyAtVisualBoundaries();
        TestMemoryReclaimerAndCodexMetaCacheEviction();
        Console.WriteLine("PerformanceOptimizationTests GREEN");
    }

    private static void TestCodexMetaReadsAFullFirstLineWithoutReadingTheTail()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "AgentIsland.PerformanceOptimizationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var padding = JsonSerializer.Serialize(new string('x', 100_000));
            var firstLine =
                "{\"type\":\"session_meta\",\"payload\":{" +
                "\"id\":\"s1\",\"cwd\":\"C:\\\\p\",\"originator\":\"codex_cli_rs\"," +
                "\"source\":\"cli\",\"padding\":" + padding + "}}";
            var path = Path.Combine(root, "session.jsonl");
            File.WriteAllText(path, firstLine + Environment.NewLine + "not-json-tail");

            var parsed = SessionScanner.CodexMeta(path);
            Expect(parsed is not null, "a first line larger than the initial buffer must still parse");
            Expect(parsed!.Value.Sid == "s1", "CodexMeta must preserve the session id");
            Expect(parsed.Value.Cwd == @"C:\p", "CodexMeta must preserve the working directory");
            Expect(
                parsed.Value.Kind == SessionScanner.CodexRolloutKind.Interactive,
                "the first line source must still classify as interactive");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("PASS CodexMeta reads one complete first line");
    }

    private static void TestSilhouetteBoundsPreserveCornerGeometry()
    {
        var topBar = new SilhouetteScreenBounds(
            Width: 100,
            Height: 40,
            TopLeft: new Point(100, 200),
            ScreenWidth: 100,
            ScreenHeight: 40,
            Radius: 10,
            IsFloating: false);
        Expect(topBar.IsPointInside(new Point(100, 200)), "top-bar top corners remain square");
        Expect(topBar.IsPointInside(new Point(150, 220)), "the cached center remains interactive");
        Expect(!topBar.IsPointInside(new Point(90, 220)), "points outside the cached rectangle pass through");
        Expect(!topBar.IsPointInside(new Point(100, 239)), "top-bar bottom corners remain rounded");
        Expect(topBar.IsPointInside(new Point(105, 230)), "points inside a rounded bottom corner remain interactive");

        var floating = topBar with { IsFloating = true };
        Expect(!floating.IsPointInside(new Point(100, 200)), "floating top corners are rounded");

        var scaled = topBar with { ScreenWidth = 200, ScreenHeight = 80 };
        Expect(scaled.IsPointInside(new Point(200, 240)), "screen-to-DIP normalization survives DPI scaling");
        Console.WriteLine("PASS cached silhouette bounds preserve placement, corners, and scaling");
    }

    private static void TestTrayVisualKeyChangesOnlyAtVisualBoundaries()
    {
        var idle = TrayIconRenderer.GetVisualStateKey(0.20, ActivityState.Idle);
        Expect(
            idle.Equals(TrayIconRenderer.GetVisualStateKey(0.69, ActivityState.Idle)),
            "usage changes inside one badge bucket must not rerender the icon");
        Expect(
            !idle.Equals(TrayIconRenderer.GetVisualStateKey(0.70, ActivityState.Idle)),
            "crossing the amber usage threshold must rerender the icon");
        Expect(
            TrayIconRenderer.GetVisualStateKey(0.70, ActivityState.Idle)
                .Equals(TrayIconRenderer.GetVisualStateKey(0.89, ActivityState.Idle)),
            "amber usage values share one visual state");
        Expect(
            !TrayIconRenderer.GetVisualStateKey(0.89, ActivityState.Idle)
                .Equals(TrayIconRenderer.GetVisualStateKey(0.90, ActivityState.Idle)),
            "crossing the red usage threshold must rerender the icon");
        Expect(
            !idle.Equals(TrayIconRenderer.GetVisualStateKey(0.20, ActivityState.Working)),
            "activity status must remain part of the visual state");
        Console.WriteLine("PASS tray visual key covers badge and activity transitions");
    }

    private static void TestMemoryReclaimerAndCodexMetaCacheEviction()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "AgentIsland.MemoryReclaimTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var firstLine = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"m1\",\"cwd\":\"C:\\\\p\"}}";
            var path = Path.Combine(root, "session.jsonl");
            File.WriteAllText(path, firstLine + Environment.NewLine);

            var meta = SessionScanner.CodexMeta(path);
            Expect(meta is not null && meta.Value.Sid == "m1", "meta parses");

            // Evict via provider-targeted turn cache clear
            SessionScanner.ClearTurnCache(TriggerTool.Codex);

            // Re-read after file delete to verify cache was purged (if cached, it would return cached meta)
            File.Delete(path);
            var afterEviction = SessionScanner.CodexMeta(path);
            Expect(afterEviction is null, "clearing turn cache for Codex must purge CodexMetaCache");

            // Test memory reclaimer execution
            MemoryReclaimer.PerformReclaim();
            MemoryReclaimer.ScheduleReclaim(10);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("PASS memory reclamation and Codex meta cache eviction");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
