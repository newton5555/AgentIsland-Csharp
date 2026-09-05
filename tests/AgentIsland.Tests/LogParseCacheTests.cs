using System.Diagnostics;
using System.IO;
using AgentIsland.Core;
using AgentIsland.Core.Cost;

namespace AgentIsland.Tests;

public static class LogParseCacheTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("clear memory does not wait for or lose a concurrent parse", ClearDuringParse),
            ("concurrent cold walks return complete results", ConcurrentColdWalks),
        };

        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("LogParseCacheTests GREEN");
    }

    private static void ClearDuringParse()
    {
        var root = CreateFixture();
        try
        {
            var path = Path.Combine(root, "session.jsonl");
            var cache = new LogParseCache(
                TriggerTool.Codex,
                Path.Combine(root, "parse-cache.json"));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var parseCalls = 0;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-1);

            var first = Task.Run(() => cache.Walk(new[] { path }, cutoff, _ =>
            {
                Interlocked.Increment(ref parseCalls);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("test parser was not released");
                return Events();
            }));

            Expect(entered.Wait(TimeSpan.FromSeconds(5)), "parser did not start");
            var stopwatch = Stopwatch.StartNew();
            cache.ClearMemory();
            stopwatch.Stop();
            Expect(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
                "ClearMemory waited for a parser holding the old generation");

            release.Set();
            first.GetAwaiter().GetResult();

            // The first scan started before ClearMemory and therefore must not
            // repopulate memory or disk. The second scan has to parse again.
            cache.Walk(new[] { path }, cutoff, _ =>
            {
                Interlocked.Increment(ref parseCalls);
                return Events();
            });
            Expect(parseCalls == 2,
                $"stale parse repopulated the cache (parse calls: {parseCalls})");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    private static void ConcurrentColdWalks()
    {
        var root = CreateFixture();
        try
        {
            var path = Path.Combine(root, "session.jsonl");
            var cache = new LogParseCache(
                TriggerTool.Codex,
                Path.Combine(root, "parse-cache.json"));
            var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
            var first = Task.Run(() => cache.Walk(new[] { path }, cutoff, _ => Events()));
            var second = Task.Run(() => cache.Walk(new[] { path }, cutoff, _ => Events()));

            Task.WaitAll(first, second);
            Expect(first.Result.Count == 1, "first cold walk lost its event");
            Expect(second.Result.Count == 1, "second cold walk lost its event");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    private static List<TokenEvent> Events() => new()
    {
        new TokenEvent(
            TriggerTool.Codex,
            DateTimeOffset.UtcNow,
            "gpt-5",
            100,
            20,
            0,
            10),
    };

    private static string CreateFixture()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "AgentIsland.LogParseCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "session.jsonl"), new byte[2048]);
        return root;
    }

    private static void DeleteFixture(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
