using System.IO;
using AgentIsland.Cost;

namespace AgentIsland.Tests;

/// Pins the cf3fb8a accounting fixes: a token_count event whose
/// total_token_usage (input, output) pair exactly matches the previous
/// event is a replayed delta and must not double-bill; shrinking totals
/// (compaction reset) and old lines without totals still count.
public static class CodexReplayGuardTests
{
    public static void RunAll()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-replay-{Guid.NewGuid():N}.jsonl");
        static string Event(long lastIn, long lastOut, long totalIn, long totalOut) =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "event_msg",
                timestamp = "2026-07-16T10:00:00Z",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        last_token_usage = new { input_tokens = lastIn, cached_input_tokens = 0, output_tokens = lastOut },
                        total_token_usage = new { input_tokens = totalIn, output_tokens = totalOut },
                    },
                },
            });
        static string BareEvent(long lastIn, long lastOut) =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "event_msg",
                timestamp = "2026-07-16T10:00:00Z",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        last_token_usage = new { input_tokens = lastIn, cached_input_tokens = 0, output_tokens = lastOut },
                    },
                },
            });

        try
        {
            File.WriteAllLines(path, new[]
            {
                Event(100, 10, 100, 10),   // turn 1 — counts
                Event(100, 10, 100, 10),   // replay of turn 1 — skipped
                Event(50, 5, 150, 15),     // turn 2, totals advanced — counts
                Event(50, 5, 150, 15),     // replay of turn 2 — skipped
                Event(30, 3, 30, 3),       // compaction reset (totals SHRANK) — counts
                BareEvent(20, 2),          // old CLI line without totals — counts
            });
            var events = CodexLogReader.ParseFile(path);
            Expect(events.Count == 4, $"expected 4 billed events, got {events.Count}");
            var input = events.Sum(e => e.InputTokens);
            var output = events.Sum(e => e.OutputTokens);
            Expect(input == 200, $"expected 200 input tokens (100+50+30+20), got {input}");
            Expect(output == 20, $"expected 20 output tokens (10+5+3+2), got {output}");
            Console.WriteLine("PASS replayed deltas skipped, resets and legacy lines kept");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
        Console.WriteLine("CodexReplayGuardTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
