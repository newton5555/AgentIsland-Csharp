using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Pins Grok turn detection. Grok writes ACP-shaped update frames, so the
/// turn boundary is `params.update.sessionUpdate == "turn_completed"` and
/// nothing else — anything looser would raise "it's your turn" mid-run.
public static class GrokTurnStateTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("turn_completed is done", TestTurnCompletedIsDone),
            ("a message chunk is still running", TestChunkIsRunning),
            ("the turn key carries the unix second and the kind", TestKeyShape),
            ("a missing timestamp still yields a key", TestMissingTimestamp),
            ("frames without an update block are skipped", TestNonUpdateFramesSkipped),
            ("an out-of-range timestamp reads as no date", TestOutOfRangeTimestamp),
            ("an empty transcript is not done", TestEmptyTranscript),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("GrokTurnStateTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void TestTurnCompletedIsDone()
    {
        var state = SessionTurnState.Grok(new[]
        {
            """{"timestamp":1754640000,"params":{"update":{"sessionUpdate":"turn_completed"}}}""",
        });
        Expect(state.IsDone, "turn_completed must finish the turn");
        Expect(state.ActivityDate?.ToUnixTimeSeconds() == 1754640000,
            "the frame's timestamp must become the activity date");
    }

    private static void TestChunkIsRunning()
    {
        var state = SessionTurnState.Grok(new[]
        {
            """{"timestamp":1754640000,"params":{"update":{"sessionUpdate":"turn_completed"}}}""",
            """{"timestamp":1754640060,"params":{"update":{"sessionUpdate":"agent_message_chunk"}}}""",
        });
        Expect(!state.IsDone, "only the LAST update frame decides, and a chunk is mid-run");
    }

    private static void TestKeyShape()
    {
        var state = SessionTurnState.Grok(new[]
        {
            """{"timestamp":1754640000,"params":{"update":{"sessionUpdate":"turn_completed"}}}""",
        });
        // The key feeds alarm dedup through ReminderDeliveryKey; the shape is
        // "<unix seconds>:<kind>", byte-identical to the macOS builder.
        Expect(state.Key == "1754640000:turn_completed", $"unexpected turn key: {state.Key}");
    }

    private static void TestMissingTimestamp()
    {
        var state = SessionTurnState.Grok(new[]
        {
            """{"params":{"update":{"sessionUpdate":"turn_completed"}}}""",
        });
        Expect(state.IsDone, "a missing timestamp must not hide a finished turn");
        Expect(state.Key == ":turn_completed", $"unexpected turn key: {state.Key}");
        Expect(state.ActivityDate is null, "no timestamp means no activity date; mtime takes over");
    }

    private static void TestNonUpdateFramesSkipped()
    {
        var state = SessionTurnState.Grok(new[]
        {
            """{"timestamp":1754640000,"params":{"update":{"sessionUpdate":"turn_completed"}}}""",
            """{"jsonrpc":"2.0","id":7,"result":{"ok":true}}""",
            """{"timestamp":1754640100,"params":{"somethingElse":{}}}""",
        });
        Expect(state.IsDone, "trailing frames with no update block must not mask the completed turn");
        Expect(state.Key == "1754640000:turn_completed", $"unexpected turn key: {state.Key}");
    }

    private static void TestOutOfRangeTimestamp()
    {
        // If some Grok build ever wrote milliseconds, the value lands past
        // year 9999 and must degrade to "no date" rather than throw.
        var state = SessionTurnState.Grok(new[]
        {
            """{"timestamp":1754640000000,"params":{"update":{"sessionUpdate":"turn_completed"}}}""",
        });
        Expect(state.ActivityDate is null, "an out-of-range timestamp must read as no date");
        Expect(state.IsDone, "the turn kind is still readable without a usable timestamp");
    }

    private static void TestEmptyTranscript()
    {
        var state = SessionTurnState.Grok(Array.Empty<string>());
        Expect(!state.IsDone && state.Key is null && state.ActivityDate is null,
            "an empty transcript must claim nothing");
    }
}
