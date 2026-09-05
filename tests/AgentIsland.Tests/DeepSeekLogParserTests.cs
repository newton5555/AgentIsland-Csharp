using System.IO;
using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Core.Cost;
using AgentIsland.Providers.Cost.DeepSeek;
using ZstdSharp;

namespace AgentIsland.Tests;

/// Contract tests for the DSH JSONL ledger: usage chunks and final messages
/// replace by (turn, step), cache-write maps to creation tokens, reasoning is
/// not double-counted, and concatenated zstd frames are readable.
public class DeepSeekLogParserTests
{
    [Fact]
    public void TestDeepSeekLogParser() => RunAll();

    internal static void RunAll()
    {
        TestUsageReplacement();
        TestMalformedAndFallbacks();
        TestConcatenatedFrames();
        TestAllRoutesCountTowardUsage();
        Console.WriteLine("DeepSeekLogParserTests GREEN");
    }

    private static void TestUsageReplacement()
    {
        var lines = new[]
        {
            Header("deepseek-v4-pro"),
            Chunk(1, 2, 1_700_000_000_000, 100, 5, 1_000, 7, reasoning: 999),
            Message(1, 2, 1_700_000_000_010, "deepseek-v4-pro",
                120, 8, 2_000, 9, reasoning: 888),
            Chunk(1, 3, 1_700_000_000_020, 50, 2, 0, 0, reasoning: 10),
        };

        var events = DeepSeekLogParser.ParseLines(lines);
        Expect(events.Count == 2, $"chunk + final message must produce two steps, got {events.Count}");
        Expect(events[0].Provider == TriggerTool.DeepSeek, "events carry the DeepSeek provider identity");
        Expect(events[0].Model == "deepseek-v4-pro", "message source model wins over the header fallback");
        Expect(events[0].InputTokens == 120 && events[0].OutputTokens == 8,
            "final assistant message replaces the chunk token buckets");
        Expect(events[0].CacheCreationTokens == 9 && events[0].CacheReadTokens == 2_000,
            "cache write/read fields map to TokenEvent cache buckets");
        Expect(events[0].WireTokens == 2_137,
            "reasoningTokens is not added a second time to wire tokens");
        Expect(events[1].InputTokens == 50 && events[1].OutputTokens == 2,
            "chunk-only usage is retained when no final message exists");
        Console.WriteLine("PASS DeepSeek usage replacement and token mapping");
    }

    private static void TestMalformedAndFallbacks()
    {
        var lines = new[]
        {
            Header("deepseek-v4-flash"),
            "not json",
            Chunk(2, 1, 1_700_000_000_030, 10, 1, 0, 0, reasoning: 0),
            // Invalid time and zero usage are ignored.
            Chunk(2, 2, -1, 999, 999, 999, 999, reasoning: 999),
            Message(2, 3, 1_700_000_000_040, "", 0, 0, 0, 0, reasoning: 0),
        };

        var events = DeepSeekLogParser.ParseLines(lines);
        Expect(events.Count == 1, $"malformed/invalid/zero lines must be ignored, got {events.Count}");
        Expect(events[0].Model == "deepseek-v4-flash", "header model is used for chunk-only records");
        Console.WriteLine("PASS DeepSeek malformed and fallback handling");
    }

    private static void TestConcatenatedFrames()
    {
        var first = Header("deepseek-v4-pro") + "\n"
            + Chunk(3, 1, 1_700_000_000_050, 3, 1, 0, 0, reasoning: 0) + "\n";
        var second = Message(3, 1, 1_700_000_000_060, "deepseek-v4-pro",
            4, 2, 0, 1, reasoning: 0) + "\n";
        var firstFrame = Compress(first);
        var secondFrame = Compress(second);
        var bytes = firstFrame.Concat(secondFrame).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"agentisland-deepseek-{Guid.NewGuid():N}.jsonl.zstd");
        try
        {
            File.WriteAllBytes(path, bytes);
            var events = DeepSeekLogParser.ParseFile(path);
            Expect(events.Count == 1, $"concatenated zstd frames must be read as one stream, got {events.Count}");
            Expect(events[0].InputTokens == 4 && events[0].CacheCreationTokens == 1,
                "the second frame replaces the first frame's same step");
            Console.WriteLine("PASS DeepSeek concatenated zstd frames");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void TestAllRoutesCountTowardUsage()
    {
        var lines = new[]
        {
            Header("my-gateway", "deepseek-v4-flash"),
            Chunk(4, 1, 1_700_000_000_070, 100, 5, 10, 0, reasoning: 0),
            Message(4, 1, 1_700_000_000_080, "my-gateway", "deepseek-v4-flash",
                100, 5, 10, 0, reasoning: 0),
        };
        var events = DeepSeekLogParser.ParseLines(lines);
        Expect(events.Count == 1, "all DSH routes must enter DeepSeek token totals");
        Expect(events[0].InputTokens == 100 && events[0].OutputTokens == 5,
            "a non-official route's final usage must replace its earlier chunk");
        Expect(events[0].CacheReadTokens == 10,
            "non-official route cache tokens must be retained in local usage");

        var mixedRoutes = new[]
        {
            Header("my-gateway", "deepseek-v4-flash"),
            Chunk(5, 1, 1_700_000_000_090, 20, 2, 0, 0, reasoning: 0),
            Header("deepseek-official", "deepseek-v4-flash"),
            Chunk(5, 1, 1_700_000_000_100, 30, 3, 0, 0, reasoning: 0),
        };
        var routeEvents = DeepSeekLogParser.ParseLines(mixedRoutes);
        Expect(routeEvents.Count == 2,
            "same turn/step on different routes must remain two token samples");
        Console.WriteLine("PASS DeepSeek token usage includes every DSH route");
    }

    private static byte[] Compress(string text)
    {
        var source = Encoding.UTF8.GetBytes(text);
        using var compressor = new Compressor(3);
        return compressor.Wrap(new ReadOnlySpan<byte>(source)).ToArray();
    }

    private static string Header(string model) => Header("deepseek-official", model);

    private static string Header(string provider, string model) => JsonSerializer.Serialize(new
    {
        type = "request/header",
        data = new
        {
            header = new
            {
                config = new { provider, model },
            },
        },
    });

    private static string Chunk(
        long turn,
        long step,
        long time,
        long input,
        long output,
        long cacheRead,
        long cacheWrite,
        long reasoning) => JsonSerializer.Serialize(new
        {
            type = "assistant/chunk",
            seq = turn * 1_000 + step,
            time,
            data = new
            {
                turn,
                step,
                chunk = new
                {
                    type = "usage",
                    usage = new
                    {
                        inputTokens = input,
                        outputTokens = output,
                        cacheReadTokens = cacheRead,
                        cacheWriteTokens = cacheWrite,
                        reasoningTokens = reasoning,
                    },
                },
            },
        });

    private static string Message(
        long turn,
        long step,
        long time,
        string model,
        long input,
        long output,
        long cacheRead,
        long cacheWrite,
        long reasoning) => Message(
            turn, step, time, "deepseek-official", model,
            input, output, cacheRead, cacheWrite, reasoning);

    private static string Message(
        long turn,
        long step,
        long time,
        string provider,
        string model,
        long input,
        long output,
        long cacheRead,
        long cacheWrite,
        long reasoning) => JsonSerializer.Serialize(new
        {
            type = "assistant/message",
            seq = turn * 1_000 + step + 500,
            time,
            data = new
            {
                turn,
                step,
                message = new
                {
                    role = "assistant",
                    source = new { kind = "model", provider, model },
                },
                usage = new
                {
                    inputTokens = input,
                    outputTokens = output,
                    cacheReadTokens = cacheRead,
                    cacheWriteTokens = cacheWrite,
                    reasoningTokens = reasoning,
                },
            },
        });

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
