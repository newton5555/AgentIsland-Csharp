using AgentIsland.Alarm;

namespace AgentIsland.Tests;

/// 1:1 port of the macOS ReminderDeliveryKeyTests.
public static class ReminderDeliveryKeyTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("nil turn key is stable", TestNilTurnKeyUsesStableSameTurnKey),
            ("new turn key changes reminder key", TestNewTurnKeyCreatesNewReminderKey),
            ("transcript path identifies thread", TestTranscriptPathIsPreferredForThreadIdentity),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("ReminderDeliveryKeyTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void TestNilTurnKeyUsesStableSameTurnKey()
    {
        var first = ReminderDeliveryKey.Make(
            "claude", 2,
            @"C:\Users\me\.claude\projects\demo\session.jsonl",
            "session-a", @"C:\Users\me\demo", "Demo", null);
        var second = ReminderDeliveryKey.Make(
            "claude", 2,
            @"C:\Users\me\.claude\projects\demo\session.jsonl",
            "session-a", @"C:\Users\me\demo", "Demo", null);
        Expect(first == second, "same thread without a parsed turn key must not become a new reminder key");
        Expect(first.EndsWith("-latest", StringComparison.Ordinal), "nil turn keys should use a stable latest marker");
    }

    private static void TestNewTurnKeyCreatesNewReminderKey()
    {
        var first = ReminderDeliveryKey.Make(
            "codex", 2,
            @"C:\Users\me\.codex\sessions\session.jsonl",
            "session-a", @"C:\Users\me\demo", "Demo", "turn-1");
        var second = ReminderDeliveryKey.Make(
            "codex", 2,
            @"C:\Users\me\.codex\sessions\session.jsonl",
            "session-a", @"C:\Users\me\demo", "Demo", "turn-2");
        Expect(first != second, "a real new turn key must be allowed to trigger a new reminder");
    }

    private static void TestTranscriptPathIsPreferredForThreadIdentity()
    {
        var key = ReminderDeliveryKey.ThreadKey(
            @"C:\Users\me\.claude\projects\demo\session.jsonl",
            "session-a", @"C:\Users\me\demo", "Demo");
        Expect(key == @"C:\Users\me\.claude\projects\demo\session.jsonl",
            "transcript path should be the stable thread identity when available");
    }
}
