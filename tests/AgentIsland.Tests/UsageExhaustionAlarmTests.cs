using AgentIsland.Alarm;
using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Tests;

/// Pins the quota-alarm contract (macOS 1.5.7 UsageExhaustionAlarm): warmup
/// swallows pre-existing exhaustion; one alarm per reset cycle per window;
/// a reset_at that DRIFTS by seconds-to-minutes while a window sits exhausted
/// never re-fires (the 1.5.7 fix); a genuine reset (boundary jumps > 30 min)
/// re-arms; both windows crossing in one pass collapse to a single alarm on
/// the binding (latest) reset; and the two gates — the master reminder switch
/// and the dedicated quota-alarm opt-out — suppress without consuming the
/// cycle.
public static class UsageExhaustionAlarmTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("exhausted window fires once per reset cycle", TestFiresOncePerCycle),
            ("drifting reset_at never re-fires (the 1.5.7 fix)", TestDriftNeverRefires),
            ("warmup swallows pre-launch exhaustion", TestWarmupSwallowsExisting),
            ("a genuine reset (> 30 min jump) re-arms", TestGenuineResetRearms),
            ("errored window never fires", TestErroredWindowNeverFires),
            ("reminders off suppresses without consuming the cycle", TestRemindersOffSuppresses),
            ("quota alarm off suppresses without consuming the cycle", TestQuotaAlarmOffSuppresses),
            ("both windows crossing together alarm once, on the later reset", TestBothWindowsCoalesce),
            ("a second window crossing in a later pass is its own event", TestSeparateWindowSeparatePass),
            ("alarm key matches the macOS shape", TestAlarmKeyShape),
            ("hidden provider never fires", TestHiddenProviderNeverFires),
            ("codex raises quota alarm", TestCodexFires),
            ("the alarm names the window's real period", TestAlarmNamesRealPeriod),
        };

        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("UsageExhaustionAlarmTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static readonly DateTimeOffset ResetA = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    // A genuine next 5-hour cycle: five hours past ResetA (well over the 30-min margin).
    private static readonly DateTimeOffset ResetB = DateTimeOffset.FromUnixTimeSeconds(1_800_018_000);
    private static readonly DateTimeOffset ResetWeekly = DateTimeOffset.FromUnixTimeSeconds(1_800_400_000);

    private static AppUsage Usage(double fiveHourPercent, DateTimeOffset? resetAt, string? error = null) =>
        new(new WindowUsage(fiveHourPercent, resetAt, error), new WindowUsage(0.1, null, null));

    private static AppUsage UsageBoth(
        double fiveHourPercent, DateTimeOffset? fiveReset,
        double weeklyPercent, DateTimeOffset? weeklyReset) =>
        new(new WindowUsage(fiveHourPercent, fiveReset, null),
            new WindowUsage(weeklyPercent, weeklyReset, null));

    private static (UsageExhaustionAlarm Alarm, List<string> Fired) Make()
    {
        var fired = new List<string>();
        var alarm = new UsageExhaustionAlarm(
            (provider, window, resetAt) =>
                fired.Add(UsageExhaustionAlarm.QuotaAlarmKey(provider, window, resetAt)));
        return (alarm, fired);
    }

    private static void TestFiresOncePerCycle()
    {
        var (alarm, fired) = Make();
        // Warmup sample: healthy.
        alarm.Recompute(Usage(0.5, ResetA), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 0, "warmup must never fire");

        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 1, "crossing to exhausted must fire exactly once");
        Expect(fired[0] == "exhausted-claude-fiveHour-1800000000", $"unexpected key {fired[0]}");

        // Same cycle: flapping under and back over 100% must not re-fire.
        alarm.Recompute(Usage(0.98, ResetA), AppUsage.Empty, remindersEnabled: true);
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 1, "flapping inside one reset cycle must not re-alarm");
    }

    private static void TestDriftNeverRefires()
    {
        var (alarm, fired) = Make();
        alarm.Recompute(Usage(0.5, ResetA), AppUsage.Empty, remindersEnabled: true);
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 1, "first crossing fires");

        // Anthropic's rolling reset_at creeps forward by seconds-to-minutes on
        // every 5-minute poll while the window stays exhausted. None of these
        // is a new cycle (all within the 30-minute margin), so none re-fires —
        // this is exactly the churn that re-popped the alarm before 1.5.7.
        foreach (var driftSeconds in new[] { 30, 95, 210, 600, 1500 })
        {
            alarm.Recompute(
                Usage(1.0, ResetA.AddSeconds(driftSeconds)), AppUsage.Empty, remindersEnabled: true);
        }
        Expect(fired.Count == 1, "reset_at drift within the margin must never re-fire");
    }

    private static void TestWarmupSwallowsExisting()
    {
        var (alarm, fired) = Make();
        // First real sample is ALREADY exhausted — that predates launch.
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true);
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 0, "exhaustion that predates launch must not alarm");

        // The next genuine cycle does alarm.
        alarm.Recompute(Usage(1.0, ResetB), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 1, "the next reset cycle must alarm normally");
    }

    private static void TestGenuineResetRearms()
    {
        var (alarm, fired) = Make();
        alarm.Recompute(Usage(0.2, ResetA), AppUsage.Empty, remindersEnabled: true);
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true);
        alarm.Recompute(Usage(1.0, ResetB), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 2, "a boundary jumping a full cycle must re-arm the alarm");
        Expect(fired[1] == "exhausted-claude-fiveHour-1800018000", $"unexpected key {fired[1]}");
    }

    private static void TestErroredWindowNeverFires()
    {
        var (alarm, fired) = Make();
        alarm.Recompute(Usage(0.2, ResetA), AppUsage.Empty, remindersEnabled: true);
        alarm.Recompute(Usage(1.0, ResetA, error: "HTTP 500"), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 0, "a window with an error is not a trusted 100%");
    }

    private static void TestRemindersOffSuppresses()
    {
        var (alarm, fired) = Make();
        alarm.Recompute(Usage(0.2, ResetA), AppUsage.Empty, remindersEnabled: true);
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: false);
        Expect(fired.Count == 0, "reminders off must suppress the quota alarm");
        // Re-enabling inside the same cycle still fires — the cycle was never
        // consumed while disabled (the gate sits before AccountFor).
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 1, "re-enabling within the cycle must deliver the pending alarm");
    }

    private static void TestQuotaAlarmOffSuppresses()
    {
        var (alarm, fired) = Make();
        alarm.Recompute(Usage(0.2, ResetA), AppUsage.Empty, remindersEnabled: true, quotaAlarmEnabled: true);
        // Quota alarm opted out (but turn alarms still on): no exhaustion popup.
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true, quotaAlarmEnabled: false);
        Expect(fired.Count == 0, "quota alarm off must suppress the exhaustion popup");
        // Turning it back on within the cycle delivers the pending alarm.
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true, quotaAlarmEnabled: true);
        Expect(fired.Count == 1, "re-enabling the quota alarm within the cycle must deliver it");
    }

    private static void TestBothWindowsCoalesce()
    {
        var (alarm, fired) = Make();
        alarm.Recompute(UsageBoth(0.5, ResetA, 0.9, ResetWeekly), AppUsage.Empty, remindersEnabled: true);
        // Heavy use pushes BOTH windows over 100% in the same poll — the user
        // is blocked once, so exactly one popup, and it names the weekly reset
        // (the true unblock time).
        alarm.Recompute(UsageBoth(1.0, ResetA, 1.0, ResetWeekly), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 1, "both windows crossing together must pop exactly one alarm");
        Expect(fired[0] == "exhausted-claude-weekly-1800400000",
            $"the alarm must carry the later (weekly) reset, got {fired[0]}");
    }

    private static void TestSeparateWindowSeparatePass()
    {
        var (alarm, fired) = Make();
        alarm.Recompute(UsageBoth(0.5, ResetA, 0.9, ResetWeekly), AppUsage.Empty, remindersEnabled: true);
        // 5-hour crosses first.
        alarm.Recompute(UsageBoth(1.0, ResetA, 0.9, ResetWeekly), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 1, "five-hour crossing alarms");
        // The weekly window crosses later, in its own poll — a different limit
        // hit at a different time is a genuinely separate event (this is the
        // mac 1.5.7 per-window semantics; only same-window drift is silenced).
        alarm.Recompute(UsageBoth(1.0, ResetA, 1.0, ResetWeekly), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 2, "a distinct window crossing in a later pass fires its own alarm");
        Expect(fired[1] == "exhausted-claude-weekly-1800400000", $"unexpected key {fired[1]}");
    }

    private static void TestAlarmKeyShape()
    {
        Expect(
            UsageExhaustionAlarm.QuotaAlarmKey(TriggerTool.Codex, QuotaWindowKind.Weekly, ResetA)
                == "exhausted-codex-weekly-1800000000",
            "alarm key must match the macOS exhausted-<provider>-<window>-<unix> shape");
        Expect(
            UsageExhaustionAlarm.QuotaAlarmKey(TriggerTool.Claude, QuotaWindowKind.FiveHour, null)
                == "exhausted-claude-fiveHour-none",
            "a null reset stamps 'none', matching macOS");
    }

    private static void TestCodexFires()
    {
        var (alarm, fired) = Make();
        // Warmup healthy on both providers.
        alarm.Recompute(Usage(0.2, ResetA), Usage(0.2, ResetA), remindersEnabled: true);
        // Codex exhausts its quota and should raise an alarm.
        alarm.Recompute(Usage(0.2, ResetA), Usage(1.0, ResetA), remindersEnabled: true);
        Expect(fired.Count == 1 && fired[0].StartsWith("exhausted-codex-", StringComparison.Ordinal), "codex exhaustion must raise the quota alarm");
        alarm.Recompute(Usage(0.2, ResetA), UsageBoth(1.0, ResetA, 1.0, ResetWeekly), remindersEnabled: true);
        Expect(fired.Count == 2, "codex exhaustion must raise the quota alarm");
    }

    private static void TestAlarmNamesRealPeriod()
    {
        var (alarm, fired) = Make();
        // Claude's primary slot hypothetically re-shaped to a week-long
        // window (periodSeconds = 604800): the alarm must say "weekly", not
        // "5-hour", even though it sits in the fiveHour slot.
        var weekLongPrimary = new AppUsage(
            new WindowUsage(0.2, ResetA, null, PeriodSeconds: 604800),
            new WindowUsage(0.1, null, null));
        alarm.Recompute(weekLongPrimary, AppUsage.Empty, remindersEnabled: true);
        alarm.Recompute(
            weekLongPrimary with { FiveHour = new WindowUsage(1.0, ResetA, null, 604800) },
            AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 1, "week-long primary crossing fires once");
        Expect(fired[0] == "exhausted-claude-weekly-1800000000",
            $"the alarm must carry the real (weekly) period, got {fired[0]}");
    }

    private static void TestHiddenProviderNeverFires()
    {
        var (alarm, fired) = Make();
        // Warmup with the provider still visible and healthy.
        alarm.Recompute(Usage(0.5, ResetA), AppUsage.Empty, remindersEnabled: true);
        // Claude exhausts while hidden in Settings: no alarm, and the cycle is
        // not consumed.
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true, claudeVisible: false);
        Expect(fired.Count == 0, "hidden provider must not alarm");
        // Switching the provider back on lets the same cycle fire normally.
        alarm.Recompute(Usage(1.0, ResetA), AppUsage.Empty, remindersEnabled: true);
        Expect(fired.Count == 1, "re-shown provider should fire for the still-exhausted cycle");
    }
}
