using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentIsland.Backend.Alarms;
using AgentIsland.Core;
using AgentIsland.UI.Providers;
using AgentIsland.UI;
using AgentIsland.UI.Theme;

namespace AgentIsland.Tests;

public class ProviderLogoAnimationTests
{
    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAIL: {message}");
        }
    }

    [WpfFact]
    public void TestProviderLogoAnimation() => RunAll();

    internal static void RunAll()
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
        WpfTestEnvironment.EnsureInitialized();
        TestAntigravityWorkingDoesNotSpin();
        TestAntigravityWorkingActivatesWave();
        TestDeepSeekWorkingActivatesSwim();
        TestDeepSeekStateTransitionsAndCleanup();
        TestDeepSeekWorkingRendersPixelChangesBetweenFrames();
        TestClaudeAndCodexContinueSpin();
        TestAntigravityStateTransitionsAndCleanup();
        TestToolSwitchWhileWorking();
        TestAntigravityOldNeedsYouWithNewWorkingAggregatesToWorkingAndStartsWave();
        TestAntigravityOnlyNeedsYouRemainsStationaryAndPreservesReminders();
        TestFollowModelDualActivePipelineWithRemindersDisabled();
        TestFollowModelPalettesCoverEveryProvider();
        TestProviderBrandColorsStayDistinct();
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

    private static void TestDeepSeekWorkingActivatesSwim()
    {
        var logo = new ProviderLogo { Tool = TriggerTool.DeepSeek };
        logo.SetState(ActivityState.Working);

        Expect(logo.IsDeepSeekSwimActive,
            "DeepSeek Working state must activate whale swim animation");
        Expect(logo.DeepSeekBubbleVisibility == Visibility.Visible,
            "DeepSeek Working state must show the bubble layer");
        Expect(!logo.IsSpinActive,
            "DeepSeek Working state must not rotate the whale mark");
        Console.WriteLine("PASS DeepSeek working activates whale swim and bubbles");
    }

    private static void TestDeepSeekWorkingRendersPixelChangesBetweenFrames()
    {
        var logo = new ProviderLogo { Tool = TriggerTool.DeepSeek };
        logo.SetState(ActivityState.Working);

        var window = new Window
        {
            Width = 100,
            Height = 100,
            Content = logo,
            ShowActivated = false,
        };
        window.Show();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Render);
        PumpDispatcher(TimeSpan.FromMilliseconds(100));

        var rtb1 = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
        rtb1.Render(logo);
        var pix1 = new byte[100 * 100 * 4];
        rtb1.CopyPixels(pix1, 400, 0);
        var bob1 = logo.DeepSeekBob;
        var tilt1 = logo.DeepSeekTilt;
        var bubble1 = logo.DeepSeekBubbleOpacity;

        PumpDispatcher(TimeSpan.FromMilliseconds(450));

        var rtb2 = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
        rtb2.Render(logo);
        var pix2 = new byte[100 * 100 * 4];
        rtb2.CopyPixels(pix2, 400, 0);
        var bob2 = logo.DeepSeekBob;
        var tilt2 = logo.DeepSeekTilt;
        var bubble2 = logo.DeepSeekBubbleOpacity;
        logo.SetState(ActivityState.Idle);
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

        Expect(changedPixels > 10,
            $"DeepSeek swim must produce visible pixel changes across frames (got {changedPixels} changed pixels; bob {bob1:F2}->{bob2:F2}, tilt {tilt1:F2}->{tilt2:F2}, bubble {bubble1:F2}->{bubble2:F2})");
        Expect(maxDelta > 30,
            $"DeepSeek swim color delta must be perceptible (got max delta {maxDelta})");
        Console.WriteLine($"PASS DeepSeek whale swim renders pixel changes ({changedPixels} changed pixels, max delta {maxDelta})");
    }

    private static void TestDeepSeekStateTransitionsAndCleanup()
    {
        var logo = new ProviderLogo { Tool = TriggerTool.DeepSeek };
        logo.SetState(ActivityState.Working);
        Expect(logo.IsDeepSeekSwimActive, "DeepSeek swim is active before cleanup transition");

        logo.SetState(ActivityState.Idle);
        Expect(!logo.IsDeepSeekSwimActive,
            "DeepSeek Idle state must stop whale movement");
        Expect(logo.DeepSeekBubbleVisibility == Visibility.Collapsed,
            "DeepSeek Idle state must collapse bubbles");
        Expect(!logo.IsSpinActive, "DeepSeek Idle state must not start generic spin");

        logo.SetState(ActivityState.Working);
        logo.Tool = TriggerTool.Claude;
        Expect(!logo.IsDeepSeekSwimActive,
            "switching away from DeepSeek must stop whale movement");
        Expect(logo.IsSpinActive,
            "switching from a working whale to Claude must start Claude spin");
        logo.SetState(ActivityState.Idle);
        Console.WriteLine("PASS DeepSeek state transitions stop bubbles and do not leak spin");
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = duration,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
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
        var monitor = new ActivityMonitor();
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

        monitor.Apply(sessions, now);

        var agyState = monitor.StateFor(TriggerTool.Antigravity);
        Expect(agyState == ActivityState.Working, $"Antigravity state must be Working when a working sibling exists, got {agyState}");

        var logo = new ProviderLogo { Tool = TriggerTool.Antigravity };
        logo.SetState(agyState);

        Expect(logo.IsAntigravityWaveActive, "AGY logo must activate four-color wave when aggregated state is Working");
        Expect(logo.AntigravityWaveVisibility == Visibility.Visible, "AGY wave host must be visible");
        Expect(logo.AntigravityStaticVisibility == Visibility.Collapsed, "AGY static face must be collapsed");
        Expect(!logo.IsSpinActive, "AGY logo must not spin");

        Console.WriteLine("PASS antigravity old needsyou + new working aggregates to working and activates wave");
    }

    private static void TestAntigravityOnlyNeedsYouRemainsStationaryAndPreservesReminders()
    {
        var monitor = new ActivityMonitor();
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

        monitor.Apply(sessions, now);

        var agyState = monitor.StateFor(TriggerTool.Antigravity);
        Expect(agyState == ActivityState.NeedsYou, $"Antigravity state must be NeedsYou when only needsyou sessions exist, got {agyState}");

        var logo = new ProviderLogo { Tool = TriggerTool.Antigravity };
        logo.SetState(agyState);

        Expect(!logo.IsAntigravityWaveActive, "AGY logo must NOT activate wave when in NeedsYou state");
        Expect(logo.AntigravityWaveVisibility == Visibility.Collapsed, "AGY wave host must be collapsed in NeedsYou");
        Expect(logo.AntigravityStaticVisibility == Visibility.Visible, "AGY static face must be visible in NeedsYou");
        Expect(!logo.IsSpinActive, "AGY logo must not spin in NeedsYou");

        Console.WriteLine("PASS antigravity with only needsyou remains stationary without wave");
    }

    private static void TestFollowModelDualActivePipelineWithRemindersDisabled()
    {
        var monitor = new ActivityMonitor();
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

        monitor.Apply(sessions, now);

        var codexState = monitor.StateFor(TriggerTool.Codex);
        var agyState = monitor.StateFor(TriggerTool.Antigravity);

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

        monitor.Apply(sessionsAfter, now.AddSeconds(2));

        var codexStateAfter = monitor.StateFor(TriggerTool.Codex);
        var agyStateAfter = monitor.StateFor(TriggerTool.Antigravity);

        Expect(codexStateAfter == ActivityState.Working, "Codex remains Working");
        Expect(agyStateAfter == ActivityState.NeedsYou, "Antigravity transitions to NeedsYou");

        leftLogo.SetState(codexStateAfter);
        rightLogo.SetState(agyStateAfter);

        Expect(leftLogo.IsSpinActive, "Codex logo continues spinning");
        Expect(!rightLogo.IsAntigravityWaveActive, "Antigravity wave stops on NeedsYou transition");
        Expect(rightLogo.AntigravityStaticVisibility == Visibility.Visible, "Antigravity static face restored");

        Console.WriteLine("PASS follow model pipeline with dual active and reminders off routes working and wave correctly");
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

    private static void TestProviderBrandColorsStayDistinct()
    {
        Expect(ProviderIdentity.CodexAccent == Color.FromRgb(0xA7, 0x8B, 0xFA),
            "Codex accent must use the requested brighter violet");
        Expect(ProviderIdentity.DeepSeekAccent == Color.FromRgb(0x4D, 0x6B, 0xFE),
            "DeepSeek accent must stay on the whale icon blue");
        Expect(ProviderIdentity.CodexAccent != ProviderIdentity.DeepSeekAccent,
            "Codex violet and DeepSeek whale blue must remain visually distinct");
        Console.WriteLine("PASS Codex violet and DeepSeek whale blue remain distinct");
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
        var glow = new GlowColorStore().Color;
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
