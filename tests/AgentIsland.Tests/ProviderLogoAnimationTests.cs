using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentIsland.Alarm;
using AgentIsland.Core;
using AgentIsland.Model;
using AgentIsland.UI;
using AgentIsland.UI.Theme;

namespace AgentIsland.Tests;

public static class ProviderLogoAnimationTests
{
    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            Console.WriteLine($"FAIL: {message}");
            Environment.Exit(1);
        }
    }

    public static void RunAll()
    {
        Console.WriteLine("--- ProviderLogoAnimationTests ---");
        if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
        {
            Exception? error = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    RunInternal();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error is not null) throw error;
            return;
        }
        RunInternal();
    }

    private static void RunInternal()
    {
        if (Application.Current is null) _ = new Application();
        TestAntigravityWorkingDoesNotSpin();
        TestAntigravityWorkingActivatesWave();
        TestAntigravityWorkingRendersPixelChangesBetweenFrames();
        TestClaudeAndCodexContinueSpin();
        TestAntigravityStateTransitionsAndCleanup();
        TestToolSwitchWhileWorking();
        TestAntigravityOldNeedsYouWithNewWorkingAggregatesToWorkingAndStartsWave();
        TestAntigravityOnlyNeedsYouRemainsStationaryAndPreservesReminders();
        TestFollowModelDualActivePipelineWithRemindersDisabled();
        TestFollowModelPalettesCoverEveryProvider();
        TestGooglePaletteRendersAllFourHues();
        TestDualPaletteRendersBothProviderHues();
        TestFollowModelSweepPaletteSelectionRules();
        Console.WriteLine("ProviderLogoAnimationTests GREEN");
    }

    private static void TestAntigravityWorkingDoesNotSpin()
    {
        var logo = new ProviderLogo { Tool = TriggerTool.Antigravity };
        logo.SetState(ActivityState.Working);

        Expect(!logo.IsSpinActive, "Antigravity Working state must NOT have spin animation active");
        Expect(Math.Abs(logo.CurrentAngle) < 0.001, "Antigravity Working state angle must remain 0");
        Console.WriteLine("PASS antigravity working does not rotate mark");
    }

    private static void TestAntigravityWorkingActivatesWave()
    {
        var logo = new ProviderLogo { Tool = TriggerTool.Antigravity };
        logo.SetState(ActivityState.Working);

        Expect(logo.IsAntigravityWaveActive, "Antigravity Working state must activate wave animation");
        Expect(logo.AntigravityWaveVisibility == Visibility.Visible, "Antigravity wave host must be visible during working");
        Expect(logo.AntigravityStaticVisibility == Visibility.Collapsed, "Antigravity static face must be collapsed during working");
        Console.WriteLine("PASS antigravity working activates four-color liquid wave");
    }

    private static void TestAntigravityWorkingRendersPixelChangesBetweenFrames()
    {
        var logo = new ProviderLogo { Tool = TriggerTool.Antigravity };
        logo.SetState(ActivityState.Working);

        var window = new Window
        {
            Width = 100,
            Height = 100,
            Content = logo,
            ShowActivated = false,
        };
        window.Show();

        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        var rtb1 = new System.Windows.Media.Imaging.RenderTargetBitmap(100, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb1.Render(logo);
        var pix1 = new byte[100 * 100 * 4];
        rtb1.CopyPixels(pix1, 400, 0);

        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < 350)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            System.Threading.Thread.Sleep(20);
        }

        var rtb2 = new System.Windows.Media.Imaging.RenderTargetBitmap(100, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb2.Render(logo);
        var pix2 = new byte[100 * 100 * 4];
        rtb2.CopyPixels(pix2, 400, 0);

        window.Close();

        var changedPixels = 0;
        var maxDelta = 0;
        for (var i = 0; i < pix1.Length / 4; i++)
        {
            var idx = i * 4;
            var bDiff = Math.Abs((int)pix1[idx] - (int)pix2[idx]);
            var gDiff = Math.Abs((int)pix1[idx + 1] - (int)pix2[idx + 1]);
            var rDiff = Math.Abs((int)pix1[idx + 2] - (int)pix2[idx + 2]);
            var delta = bDiff + gDiff + rDiff;
            if (delta > 15)
            {
                changedPixels++;
                if (delta > maxDelta) maxDelta = delta;
            }
        }

        Expect(changedPixels > 50, $"Working animation must produce visible pixel changes across frames (got {changedPixels} changed pixels)");
        Expect(maxDelta > 100, $"Color delta between frames must be perceptible (got max delta {maxDelta})");
        Console.WriteLine($"PASS antigravity working renders real pixel changes across frames ({changedPixels} changed pixels, max delta {maxDelta})");
    }

    private static void TestClaudeAndCodexContinueSpin()
    {
        var claude = new ProviderLogo { Tool = TriggerTool.Claude };
        claude.SetState(ActivityState.Working);
        Expect(claude.IsSpinActive, "Claude Working state must have spin animation active");
        Expect(!claude.IsAntigravityWaveActive, "Claude must not have Antigravity wave active");

        var codex = new ProviderLogo { Tool = TriggerTool.Codex };
        codex.SetState(ActivityState.Working);
        Expect(codex.IsSpinActive, "Codex Working state must have spin animation active");
        Expect(!codex.IsAntigravityWaveActive, "Codex must not have Antigravity wave active");
        Console.WriteLine("PASS claude and codex continue 360-degree spin");
    }

    private static void TestAntigravityStateTransitionsAndCleanup()
    {
        var logo = new ProviderLogo { Tool = TriggerTool.Antigravity };
        logo.SetState(ActivityState.Working);
        Expect(logo.IsAntigravityWaveActive, "Precondition: wave active in Working");

        // Transition to Idle
        logo.SetState(ActivityState.Idle);
        Expect(!logo.IsAntigravityWaveActive, "Idle state must deactivate wave animation");
        Expect(logo.AntigravityWaveVisibility == Visibility.Collapsed, "Idle state must collapse wave host");
        Expect(logo.AntigravityStaticVisibility == Visibility.Visible, "Idle state must show static mark");
        Expect(!logo.IsSpinActive, "Idle state must not spin");

        // Transition to NeedsYou (YourTurn / attention)
        logo.SetState(ActivityState.Working);
        logo.SetState(ActivityState.NeedsYou);
        Expect(!logo.IsAntigravityWaveActive, "NeedsYou state must deactivate wave animation");
        Expect(logo.AntigravityWaveVisibility == Visibility.Collapsed, "NeedsYou state must collapse wave host");
        Expect(logo.AntigravityStaticVisibility == Visibility.Visible, "NeedsYou state must show static mark");

        // Transition to Stalled / RateLimited
        logo.SetState(ActivityState.Working);
        logo.SetState(ActivityState.Stalled);
        Expect(!logo.IsAntigravityWaveActive, "Stalled state must deactivate wave animation");
        Expect(logo.AntigravityWaveVisibility == Visibility.Collapsed, "Stalled state must collapse wave host");
        Expect(logo.AntigravityStaticVisibility == Visibility.Visible, "Stalled state must show static mark");

        // Transition to AuthRequired
        logo.SetState(ActivityState.Working);
        logo.SetState(ActivityState.AuthRequired);
        Expect(!logo.IsAntigravityWaveActive, "AuthRequired state must deactivate wave animation");
        Expect(logo.AntigravityWaveVisibility == Visibility.Collapsed, "AuthRequired state must collapse wave host");
        Expect(logo.AntigravityStaticVisibility == Visibility.Visible, "AuthRequired state must show static mark");

        Console.WriteLine("PASS antigravity state transitions cleanly stop wave without leaks");
    }

    private static void TestToolSwitchWhileWorking()
    {
        var logo = new ProviderLogo { Tool = TriggerTool.Claude };
        logo.SetState(ActivityState.Working);
        Expect(logo.IsSpinActive, "Claude starts with spin");

        // Switch tool to Antigravity while in Working state
        logo.Tool = TriggerTool.Antigravity;
        Expect(!logo.IsSpinActive, "Switching to Antigravity while working stops spin");
        Expect(logo.IsAntigravityWaveActive, "Switching to Antigravity while working starts wave");

        // Switch tool back to Claude while in Working state
        logo.Tool = TriggerTool.Claude;
        Expect(!logo.IsAntigravityWaveActive, "Switching to Claude stops Antigravity wave");
        Expect(logo.IsSpinActive, "Switching to Claude resumes spin");

        logo.SetState(ActivityState.Idle);
        Expect(!logo.IsSpinActive && !logo.IsAntigravityWaveActive, "Idle stops all animations");
        Console.WriteLine("PASS tool switching while working cross-animates properly");
    }

    private static void TestAntigravityOldNeedsYouWithNewWorkingAggregatesToWorkingAndStartsWave()
    {
        var prevReminder = AgentReminderStore.Shared.Enabled;
        try
        {
            // Setup: Reminders disabled (AgentIsland.agentReminders=false)
            AgentReminderStore.Shared.Enabled = false;

            var now = DateTimeOffset.UtcNow;
            var sessions = new List<ScannedSession>
            {
                // Old Antigravity session waiting on user (unacknowledged)
                new(
                    TriggerTool.Antigravity,
                    "session-old-needsyou",
                    @"C:\work\project-old",
                    "Old AGY Task",
                    now.AddMinutes(-10),
                    ActivityState.NeedsYou,
                    @"C:\Users\newto\.gemini\antigravity-cli\brain\old-1\.system_generated\logs\transcript_full.jsonl",
                    "ag:10",
                    SessionLaunchTarget.Cli),
                // New Antigravity session actively working
                new(
                    TriggerTool.Antigravity,
                    "session-new-working",
                    @"C:\work\project-new",
                    "New AGY Task",
                    now.AddSeconds(-2),
                    ActivityState.Working,
                    @"C:\Users\newto\.gemini\antigravity-cli\brain\new-2\.system_generated\logs\transcript_full.jsonl",
                    "ag:20",
                    SessionLaunchTarget.Cli),
            };

            ActivityMonitor.Shared.Apply(sessions, now);

            var agyState = ActivityMonitor.Shared.StateFor(TriggerTool.Antigravity);
            Expect(agyState == ActivityState.Working, $"Antigravity state must be Working when a working sibling exists, got {agyState}");

            var logo = new ProviderLogo { Tool = TriggerTool.Antigravity };
            logo.SetState(agyState);

            Expect(logo.IsAntigravityWaveActive, "AGY logo must activate four-color wave when aggregated state is Working");
            Expect(logo.AntigravityWaveVisibility == Visibility.Visible, "AGY wave host must be visible");
            Expect(logo.AntigravityStaticVisibility == Visibility.Collapsed, "AGY static face must be collapsed");
            Expect(!logo.IsSpinActive, "AGY logo must not spin");

            Console.WriteLine("PASS antigravity old needsyou + new working aggregates to working and activates wave");
        }
        finally
        {
            AgentReminderStore.Shared.Enabled = prevReminder;
        }
    }

    private static void TestAntigravityOnlyNeedsYouRemainsStationaryAndPreservesReminders()
    {
        var prevReminder = AgentReminderStore.Shared.Enabled;
        try
        {
            AgentReminderStore.Shared.Enabled = false;

            var now = DateTimeOffset.UtcNow;
            var sessions = new List<ScannedSession>
            {
                new(
                    TriggerTool.Antigravity,
                    "session-old-needsyou",
                    @"C:\work\project-old",
                    "Old AGY Task",
                    now.AddMinutes(-5),
                    ActivityState.NeedsYou,
                    @"C:\Users\newto\.gemini\antigravity-cli\brain\old-1\.system_generated\logs\transcript_full.jsonl",
                    "ag:10",
                    SessionLaunchTarget.Cli),
                new(
                    TriggerTool.Antigravity,
                    "session-idle",
                    @"C:\work\project-idle",
                    "Idle AGY Task",
                    now.AddHours(-1),
                    ActivityState.Idle,
                    @"C:\Users\newto\.gemini\antigravity-cli\brain\idle-1\.system_generated\logs\transcript_full.jsonl",
                    null,
                    SessionLaunchTarget.Cli),
            };

            ActivityMonitor.Shared.Apply(sessions, now);

            var agyState = ActivityMonitor.Shared.StateFor(TriggerTool.Antigravity);
            Expect(agyState == ActivityState.NeedsYou, $"Antigravity state must be NeedsYou when only needsyou sessions exist, got {agyState}");

            var logo = new ProviderLogo { Tool = TriggerTool.Antigravity };
            logo.SetState(agyState);

            Expect(!logo.IsAntigravityWaveActive, "AGY logo must NOT activate wave when in NeedsYou state");
            Expect(logo.AntigravityWaveVisibility == Visibility.Collapsed, "AGY wave host must be collapsed in NeedsYou");
            Expect(logo.AntigravityStaticVisibility == Visibility.Visible, "AGY static face must be visible in NeedsYou");
            Expect(!logo.IsSpinActive, "AGY logo must not spin in NeedsYou");

            Console.WriteLine("PASS antigravity with only needsyou remains stationary without wave");
        }
        finally
        {
            AgentReminderStore.Shared.Enabled = prevReminder;
        }
    }

    private static void TestFollowModelDualActivePipelineWithRemindersDisabled()
    {
        // Replicate user environment:
        // visualMode: follow_model
        // lowPowerMode: false (EffectiveEnabled = false)
        // agentReminders: false
        var prevMode = LowPowerModeStore.Shared.Mode;
        var prevReminder = AgentReminderStore.Shared.Enabled;
        try
        {
            LowPowerModeStore.Shared.Mode = VisualMode.FollowModel;
            AgentReminderStore.Shared.Enabled = false;

            var now = DateTimeOffset.UtcNow;
            var sessions = new List<ScannedSession>
            {
                new(
                    TriggerTool.Codex,
                    "session-codex-work",
                    @"C:\work\codex-repo",
                    "Codex Task",
                    now.AddSeconds(-2),
                    ActivityState.Working,
                    @"C:\Users\newto\.codex\sessions\1.jsonl",
                    "cdx:1",
                    SessionLaunchTarget.Cli),
                new(
                    TriggerTool.Antigravity,
                    "session-agy-old-needsyou",
                    @"C:\work\agy-repo",
                    "AGY Old Finished Turn",
                    now.AddMinutes(-30),
                    ActivityState.NeedsYou,
                    @"C:\Users\newto\.gemini\antigravity-cli\brain\old\.system_generated\logs\transcript_full.jsonl",
                    "ag:1",
                    SessionLaunchTarget.Cli),
                new(
                    TriggerTool.Antigravity,
                    "session-agy-new-working",
                    @"C:\work\agy-repo",
                    "AGY Active Run",
                    now.AddSeconds(-1),
                    ActivityState.Working,
                    @"C:\Users\newto\.gemini\antigravity-cli\brain\current\.system_generated\logs\transcript_full.jsonl",
                    "ag:2",
                    SessionLaunchTarget.Cli),
            };

            ActivityMonitor.Shared.Apply(sessions, now);

            var codexState = ActivityMonitor.Shared.StateFor(TriggerTool.Codex);
            var agyState = ActivityMonitor.Shared.StateFor(TriggerTool.Antigravity);

            Expect(codexState == ActivityState.Working, $"Codex must be Working, got {codexState}");
            Expect(agyState == ActivityState.Working, $"Antigravity must be Working, got {agyState}");

            var leftLogo = new ProviderLogo { Tool = TriggerTool.Codex };
            var rightLogo = new ProviderLogo { Tool = TriggerTool.Antigravity };

            leftLogo.SetState(codexState);
            rightLogo.SetState(agyState);

            // Left logo (Codex): spinning 360, no wave
            Expect(leftLogo.IsSpinActive, "Codex logo must be spinning while Working");
            Expect(!leftLogo.IsAntigravityWaveActive, "Codex logo must not have wave active");

            // Right logo (Antigravity): four-color wave active, no spin
            Expect(rightLogo.IsAntigravityWaveActive, "Antigravity logo must have four-color wave active while Working");
            Expect(!rightLogo.IsSpinActive, "Antigravity logo must NOT spin");
            Expect(rightLogo.AntigravityWaveVisibility == Visibility.Visible, "AGY wave host must be visible");
            Expect(rightLogo.AntigravityStaticVisibility == Visibility.Collapsed, "AGY static face must be collapsed");

            // Now simulate Antigravity turn completing into NeedsYou while Codex is still working
            var sessionsAfter = new List<ScannedSession>
            {
                new(
                    TriggerTool.Codex,
                    "session-codex-work",
                    @"C:\work\codex-repo",
                    "Codex Task",
                    now.AddSeconds(1),
                    ActivityState.Working,
                    @"C:\Users\newto\.codex\sessions\1.jsonl",
                    "cdx:1",
                    SessionLaunchTarget.Cli),
                new(
                    TriggerTool.Antigravity,
                    "session-agy-old-needsyou",
                    @"C:\work\agy-repo",
                    "AGY Old Finished Turn",
                    now.AddMinutes(-30),
                    ActivityState.NeedsYou,
                    @"C:\Users\newto\.gemini\antigravity-cli\brain\old\.system_generated\logs\transcript_full.jsonl",
                    "ag:1",
                    SessionLaunchTarget.Cli),
                new(
                    TriggerTool.Antigravity,
                    "session-agy-new-working",
                    @"C:\work\agy-repo",
                    "AGY Active Run",
                    now.AddSeconds(2),
                    ActivityState.NeedsYou,
                    @"C:\Users\newto\.gemini\antigravity-cli\brain\current\.system_generated\logs\transcript_full.jsonl",
                    "ag:2",
                    SessionLaunchTarget.Cli),
            };

            ActivityMonitor.Shared.Apply(sessionsAfter, now.AddSeconds(2));

            var codexStateAfter = ActivityMonitor.Shared.StateFor(TriggerTool.Codex);
            var agyStateAfter = ActivityMonitor.Shared.StateFor(TriggerTool.Antigravity);

            Expect(codexStateAfter == ActivityState.Working, "Codex remains Working");
            Expect(agyStateAfter == ActivityState.NeedsYou, "Antigravity transitions to NeedsYou");

            leftLogo.SetState(codexStateAfter);
            rightLogo.SetState(agyStateAfter);

            Expect(leftLogo.IsSpinActive, "Codex logo continues spinning");
            Expect(!rightLogo.IsAntigravityWaveActive, "Antigravity wave stops on NeedsYou transition");
            Expect(rightLogo.AntigravityStaticVisibility == Visibility.Visible, "Antigravity static face restored");

            Console.WriteLine("PASS follow model pipeline with dual active and reminders off routes working and wave correctly");
        }
        finally
        {
            LowPowerModeStore.Shared.Mode = prevMode;
            AgentReminderStore.Shared.Enabled = prevReminder;
        }
    }

    private static void TestFollowModelPalettesCoverEveryProvider()
    {
        foreach (var provider in DisplayProviders.All)
        {
            var palette = ProviderIdentity.StreamPalette(provider);
            Expect(palette.Count >= 3, $"FollowModel palette for {provider} must contain multiple colours");

            var hasTransition = false;
            for (var i = 1; i < palette.Count; i++)
            {
                if (palette[i] != palette[i - 1])
                {
                    hasTransition = true;
                    break;
                }
            }

            Expect(hasTransition, $"FollowModel palette for {provider} must not be monochrome");
        }

        Expect(
            ProviderIdentity.StreamPalette(DisplayProvider.Antigravity).Count == 4,
            "Antigravity FollowModel palette must contain Google's four hues");
        Console.WriteLine("PASS follow model exposes a multi-colour palette for every provider");
    }

    private static void TestGooglePaletteRendersAllFourHues()
    {
        var bitmap = RenderSweep(ProviderIdentity.StreamPalette(DisplayProvider.Antigravity));

        Expect(ContainsVisiblePixel(bitmap, (r, g, b) => b > r * 1.35 && b > g * 1.10),
            "Antigravity sweep must render Google's blue");
        Expect(ContainsVisiblePixel(bitmap, (r, g, b) => g > r * 1.50 && g > b * 1.15),
            "Antigravity sweep must render Google's green");
        Expect(ContainsVisiblePixel(bitmap, (r, g, b) => r > 120 && g > 90 && b < g * 0.35),
            "Antigravity sweep must render Google's yellow");
        Expect(ContainsVisiblePixel(bitmap, (r, g, b) => r > g * 1.40 && r > b * 1.40),
            "Antigravity sweep must render Google's red");
        Console.WriteLine("PASS Antigravity sweep renders Google's four hues");
    }

    private static void TestDualPaletteRendersBothProviderHues()
    {
        var brush = ConicSweepBrush.MakeDual(
            ProviderIdentity.StreamPalette(DisplayProvider.Claude),
            ProviderIdentity.StreamPalette(DisplayProvider.Codex),
            new RotateTransform());
        Expect(brush.ImageSource is BitmapSource, "Dual palette sweep must produce a bitmap source");
        var bitmap = (BitmapSource)brush.ImageSource!;

        Expect(ContainsVisiblePixel(bitmap, (r, g, b) => r > g * 1.15 && g > b * 1.15),
            "Dual sweep must render Claude's warm palette");
        Expect(ContainsVisiblePixel(bitmap, (r, g, b) => b > r * 1.20 && b > g * 1.05),
            "Dual sweep must render Codex's blue palette");
        Console.WriteLine("PASS dual palette sweep renders both provider hues");
    }

    private static BitmapSource RenderSweep(IReadOnlyList<Color> palette)
    {
        var brush = ConicSweepBrush.Make(palette, new RotateTransform());
        Expect(brush.ImageSource is BitmapSource, "Palette sweep must produce a bitmap source");
        return (BitmapSource)brush.ImageSource!;
    }

    private static bool ContainsVisiblePixel(
        BitmapSource bitmap,
        Func<int, int, int, bool> matches)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha < 80) continue;

            // Pbgra32 stores channels as B, G, R, A.
            var blue = pixels[i];
            var green = pixels[i + 1];
            var red = pixels[i + 2];
            if (matches(red, green, blue)) return true;
        }

        return false;
    }

    private static void TestFollowModelSweepPaletteSelectionRules()
    {
        var glow = GlowColorStore.Shared.Color;
        var agyPalette = ProviderIdentity.StreamPalette(TriggerTool.Antigravity);
        var codexPalette = ProviderIdentity.StreamPalette(TriggerTool.Codex);
        var claudePalette = ProviderIdentity.StreamPalette(TriggerTool.Claude);

        // 1. Dual Working: both slots working -> dual stream with respective palettes
        var (dualIsDual, dualLeft, dualRight) = IslandWindow.ResolveSweepPalettes(
            alertTint: null,
            visualMode: VisualMode.FollowModel,
            fallbackGlowColor: glow,
            leftTool: TriggerTool.Antigravity,
            leftState: ActivityState.Working,
            rightTool: TriggerTool.Codex,
            rightState: ActivityState.Working);
        Expect(dualIsDual, "Dual working models must use dual sweep");
        Expect(SamePalette(dualLeft, agyPalette), "Dual sweep left must match Antigravity palette");
        Expect(SamePalette(dualRight, codexPalette), "Dual sweep right must match Codex palette");

        // 2. Single Working: left working, right non-working (NeedsYou/Idle/Stalled/AuthRequired)
        var nonWorkingStates = new[] { ActivityState.NeedsYou, ActivityState.Idle, ActivityState.Stalled, ActivityState.AuthRequired };
        foreach (var nonWorking in nonWorkingStates)
        {
            var (isDual, left, _) = IslandWindow.ResolveSweepPalettes(
                alertTint: null,
                visualMode: VisualMode.FollowModel,
                fallbackGlowColor: glow,
                leftTool: TriggerTool.Antigravity,
                leftState: ActivityState.Working,
                rightTool: TriggerTool.Codex,
                rightState: nonWorking);
            Expect(!isDual, $"Single working (left working, right {nonWorking}) must produce single sweep");
            Expect(SamePalette(left, agyPalette), "Single working left must use Antigravity palette");

            // Right working, left non-working (only Codex working must only show Codex sweep)
            var (isDualRight, leftP, _) = IslandWindow.ResolveSweepPalettes(
                alertTint: null,
                visualMode: VisualMode.FollowModel,
                fallbackGlowColor: glow,
                leftTool: TriggerTool.Antigravity,
                leftState: nonWorking,
                rightTool: TriggerTool.Codex,
                rightState: ActivityState.Working);
            Expect(!isDualRight, $"Single working (left {nonWorking}, right working) must produce single sweep");
            Expect(SamePalette(leftP, codexPalette), "Single working right must only use Codex palette");
        }

        // Single slot configured and working
        var (soloWorkingIsDual, soloWorkingPalette, _) = IslandWindow.ResolveSweepPalettes(
            alertTint: null,
            visualMode: VisualMode.FollowModel,
            fallbackGlowColor: glow,
            leftTool: TriggerTool.Claude,
            leftState: ActivityState.Working,
            rightTool: null,
            rightState: ActivityState.Idle);
        Expect(!soloWorkingIsDual, "Solo working slot must produce single sweep");
        Expect(SamePalette(soloWorkingPalette, claudePalette), "Solo working slot must use Claude palette");

        // 3. No Working: neither slot working -> fallback to configured slots
        foreach (var leftState in nonWorkingStates)
        {
            foreach (var rightState in nonWorkingStates)
            {
                var (fallbackDual, fbLeft, fbRight) = IslandWindow.ResolveSweepPalettes(
                    alertTint: null,
                    visualMode: VisualMode.FollowModel,
                    fallbackGlowColor: glow,
                    leftTool: TriggerTool.Antigravity,
                    leftState: leftState,
                    rightTool: TriggerTool.Codex,
                    rightState: rightState);
                Expect(fallbackDual, $"No working ({leftState}, {rightState}) must fall back to dual sweep for 2 configured slots");
                Expect(SamePalette(fbLeft, agyPalette), "Fallback dual sweep must preserve left configured slot palette");
                Expect(SamePalette(fbRight, codexPalette), "Fallback dual sweep must preserve right configured slot palette");
            }
        }

        // Solo slot configured and not working -> fallback to single slot
        var (soloFallbackIsDual, soloFallbackPalette, _) = IslandWindow.ResolveSweepPalettes(
            alertTint: null,
            visualMode: VisualMode.FollowModel,
            fallbackGlowColor: glow,
            leftTool: TriggerTool.Claude,
            leftState: ActivityState.Idle,
            rightTool: null,
            rightState: ActivityState.Idle);
        Expect(!soloFallbackIsDual, "Solo non-working slot must fall back to single sweep");
        Expect(SamePalette(soloFallbackPalette, claudePalette), "Solo non-working slot must use Claude palette");

        // No slots configured
        var (noSlotDual, noSlotPalette, _) = IslandWindow.ResolveSweepPalettes(
            alertTint: null,
            visualMode: VisualMode.FollowModel,
            fallbackGlowColor: glow,
            leftTool: null,
            leftState: ActivityState.Idle,
            rightTool: null,
            rightState: ActivityState.Idle);
        Expect(!noSlotDual, "No slot configured must produce single sweep");
        Expect(noSlotPalette.Count == 1 && noSlotPalette[0] == glow, "No slot configured must use fallback glow color");

        // 4. Alert override takes precedence over working states
        var (alertIsDual, alertPalette, _) = IslandWindow.ResolveSweepPalettes(
            alertTint: IslandColors.AlertRed,
            visualMode: VisualMode.FollowModel,
            fallbackGlowColor: glow,
            leftTool: TriggerTool.Antigravity,
            leftState: ActivityState.Working,
            rightTool: TriggerTool.Codex,
            rightState: ActivityState.Working);
        Expect(!alertIsDual, "Alert state must override to single sweep");
        Expect(alertPalette.Count == 1 && alertPalette[0] == IslandColors.AlertRed, "Alert state must use AlertRed");

        // 5. Vivid mode ignores FollowModel
        var (vividIsDual, vividPalette, _) = IslandWindow.ResolveSweepPalettes(
            alertTint: null,
            visualMode: VisualMode.Vivid,
            fallbackGlowColor: glow,
            leftTool: TriggerTool.Antigravity,
            leftState: ActivityState.Working,
            rightTool: TriggerTool.Codex,
            rightState: ActivityState.Working);
        Expect(!vividIsDual, "Vivid mode must use single sweep");
        Expect(vividPalette.Count == 1 && vividPalette[0] == glow, "Vivid mode must use GlowColor");

        Console.WriteLine("PASS follow model sweep palette selection covers dual, single, and fallback rules");
    }

    private static bool SamePalette(IReadOnlyList<Color> first, IReadOnlyList<Color> second)
    {
        if (first.Count != second.Count) return false;
        for (var i = 0; i < first.Count; i++)
        {
            if (first[i] != second[i]) return false;
        }

        return true;
    }
}
