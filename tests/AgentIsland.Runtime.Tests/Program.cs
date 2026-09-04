using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Runtime.Refresh;
using AgentIsland.Runtime.Scanning;
using AgentIsland.Runtime.Snapshots;

namespace AgentIsland.Runtime.Tests;

internal static class Program
{
    private static async Task Main()
    {
        await TestPublishesAllSources();
        await TestFailurePreservesPreviousSnapshot();
        await TestRefreshIsCoalesced();
        await TestCancellationIsPropagated();
        TestScannerMemoizesFingerprint();
        TestScannerDeduplicatesFiles();
        await TestScannerPreCancellationThrows();
        await TestTokenCostSnapshotSourceEventsEnterSummary();
        await TestTokenCostSnapshotSourceNoDataWhenEmpty();
        await TestTokenCostSnapshotSourceCancellationPropagates();
        Console.WriteLine("ALL GREEN");
    }

    private static async Task TestPublishesAllSources()
    {
        var codex = new FakeSource("codex", ActivityState.Working);
        var dsh = new FakeSource("deepseek", ActivityState.Idle);
        var runtime = new AgentRuntime(new IAgentSnapshotSource[] { codex, dsh });
        AgentSnapshotsChangedEventArgs? change = null;
        runtime.SnapshotsChanged += (_, args) => change = args;

        var observed = DateTimeOffset.Parse("2026-09-04T10:00:00+08:00");
        Expect(await runtime.RefreshAsync(observed) && change is not null,
            "refresh publishes a snapshot event");
        Expect(runtime.Snapshots.Count == 2
            && runtime.Snapshots["codex"].Activity == ActivityState.Working
            && runtime.Snapshots["deepseek"].Availability == SnapshotAvailability.Ready,
            "all provider sources publish independently");
        Console.WriteLine("PASS Runtime publishes all sources");
    }

    private static async Task TestFailurePreservesPreviousSnapshot()
    {
        var source = new FakeSource("codex", ActivityState.Working);
        var runtime = new AgentRuntime(new[] { source });
        await runtime.RefreshAsync();
        source.Error = "offline";

        await runtime.RefreshAsync();
        var snapshot = runtime.Snapshots["codex"];
        Expect(snapshot.Availability == SnapshotAvailability.Stale
            && snapshot.Activity == ActivityState.Idle
            && snapshot.Error == "offline"
            && snapshot.DataAt is not null,
            "a failed source keeps its last data as stale");
        Console.WriteLine("PASS Runtime preserves stale data on source failure");
    }

    private static async Task TestRefreshIsCoalesced()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource("deepseek", ActivityState.Working) { Gate = gate.Task };
        var runtime = new AgentRuntime(new[] { source });
        var first = runtime.RefreshAsync();
        var second = await runtime.RefreshAsync();
        Expect(!second, "a refresh during an in-flight refresh is coalesced");
        gate.SetResult();
        Expect(await first, "the original refresh completes after the source is released");
        Console.WriteLine("PASS Runtime coalesces overlapping refreshes");
    }

    private static async Task TestCancellationIsPropagated()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource("deepseek", ActivityState.Working) { Gate = gate.Task };
        var runtime = new AgentRuntime(new[] { source });
        using var cts = new CancellationTokenSource();
        var refresh = runtime.RefreshAsync(cancellationToken: cts.Token);
        cts.Cancel();
        try
        {
            await refresh;
            throw new InvalidOperationException("cancellation was swallowed");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("PASS Runtime propagates cancellation");
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void TestScannerMemoizesFingerprint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AgentIslandTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logFile = Path.Combine(tempDir, "session.jsonl");
            File.WriteAllText(logFile, "{\"line\":1}\n");
            var cacheFile = Path.Combine(tempDir, "cache.json");

            var parseCount = 0;
            List<TokenEvent> Parser(string path)
            {
                Interlocked.Increment(ref parseCount);
                return new List<TokenEvent>
                {
                    new(TriggerTool.Codex, DateTimeOffset.UtcNow, "gpt-4o", 100, 50, 0, 0)
                };
            }

            var scanner = new TokenLogScanner(TriggerTool.Codex, cacheFile, Parser);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-1);

            var first = scanner.Scan(new[] { logFile }, cutoff);
            Expect(first.Count == 1 && parseCount == 1, "first scan should call parser on cold cache");

            var second = scanner.Scan(new[] { logFile }, cutoff);
            Expect(second.Count == 1 && parseCount == 1, "second scan with identical fingerprint must hit cache");

            var scanner2 = new TokenLogScanner(TriggerTool.Codex, cacheFile, Parser);
            var third = scanner2.Scan(new[] { logFile }, cutoff);
            Expect(third.Count == 1 && parseCount == 1, "new scanner instance must reuse persisted cache on disk");

            // Modifying file changes fingerprint -> cache miss -> parser called again
            File.AppendAllText(logFile, "{\"line\":2}\n");
            File.SetLastWriteTimeUtc(logFile, DateTime.UtcNow.AddSeconds(5));
            var fourth = scanner.Scan(new[] { logFile }, cutoff);
            Expect(fourth.Count == 1 && parseCount == 2, "modified file fingerprint must re-parse");

            Console.WriteLine("PASS TokenLogScanner memoizes by fingerprint");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static void TestScannerDeduplicatesFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AgentIslandTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logFile = Path.Combine(tempDir, "session.jsonl");
            File.WriteAllText(logFile, "{\"line\":1}\n");
            var cacheFile = Path.Combine(tempDir, "cache.json");

            var parseCount = 0;
            List<TokenEvent> Parser(string path)
            {
                Interlocked.Increment(ref parseCount);
                return new List<TokenEvent>
                {
                    new(TriggerTool.DeepSeek, DateTimeOffset.UtcNow, "deepseek-chat", 20, 10, 0, 0)
                };
            }

            var scanner = new TokenLogScanner(TriggerTool.DeepSeek, cacheFile, Parser);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-1);

            var duplicateFiles = new[] { logFile, logFile, logFile };
            var results = scanner.Scan(duplicateFiles, cutoff);

            Expect(parseCount == 1, "parser should be invoked once for duplicate paths");
            Expect(results.Count == 1, "results should contain events from duplicate path only once");

            Console.WriteLine("PASS TokenLogScanner deduplicates files");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task TestScannerPreCancellationThrows()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AgentIslandTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logFile = Path.Combine(tempDir, "session.jsonl");
            File.WriteAllText(logFile, "{\"line\":1}\n");
            var cacheFile = Path.Combine(tempDir, "cache.json");

            var parserCalled = false;
            var scanner = new TokenLogScanner(
                TriggerTool.Codex,
                cacheFile,
                _ =>
                {
                    parserCalled = true;
                    return new List<TokenEvent>();
                });

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            try
            {
                scanner.Scan(new[] { logFile }, DateTimeOffset.UtcNow.AddDays(-1), cts.Token);
                throw new InvalidOperationException("pre-canceled scan did not throw");
            }
            catch (OperationCanceledException)
            {
                Expect(!parserCalled, "parser should not be called when pre-canceled");
            }

            try
            {
                await scanner.ScanAsync(new[] { logFile }, DateTimeOffset.UtcNow.AddDays(-1), cts.Token);
                throw new InvalidOperationException("pre-canceled ScanAsync did not throw");
            }
            catch (OperationCanceledException)
            {
                Expect(!parserCalled, "parser should not be called when pre-canceled via ScanAsync");
            }

            Console.WriteLine("PASS TokenLogScanner pre-cancellation throws OperationCanceledException");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static async Task TestTokenCostSnapshotSourceEventsEnterSummary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AgentIslandTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logFile = Path.Combine(tempDir, "session.jsonl");
            File.WriteAllText(logFile, "{\"line\":1}\n");
            var cacheFile = Path.Combine(tempDir, "cache.json");

            var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
            var eventTime = observed.AddMinutes(-15);

            var parserCalled = 0;
            List<TokenEvent> Parser(string path)
            {
                Interlocked.Increment(ref parserCalled);
                return new List<TokenEvent>
                {
                    new(TriggerTool.Codex, eventTime, "gpt-4o", 100, 50, 0, 0)
                };
            }

            var source = new TokenCostSnapshotSource(
                "codex",
                TriggerTool.Codex,
                cacheFile,
                Parser,
                () => new[] { logFile },
                lookbackDays: 7);

            var snapshot = await source.ReadAsync(observed, CancellationToken.None);

            Expect(snapshot.Agent == (AgentKey)"codex", "agent key matches");
            Expect(snapshot.Activity == ActivityState.Idle, "cost source activity is idle");
            Expect(snapshot.Availability == SnapshotAvailability.Ready, "availability is ready when events present");
            Expect(snapshot.DataAt == eventTime, "data timestamp is latest event timestamp");
            Expect(snapshot.Usage is null, "usage is not fabricated");
            Expect(snapshot.Cost is not null, "cost summary is present");
            var cost = snapshot.Cost!;
            Expect(cost.TodayTokens == 150, "summary includes token count");
            Expect(cost.TodayBillableTokens == 150, "summary includes billable tokens");
            Expect(cost.RecentModels.Any(m => m.Model == "gpt-4o" && m.Tokens == 150),
                "summary includes model spend entry");

            Console.WriteLine("PASS TokenCostSnapshotSource events enter summary with tokens and models");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task TestTokenCostSnapshotSourceNoDataWhenEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AgentIslandTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logFile = Path.Combine(tempDir, "empty.jsonl");
            File.WriteAllText(logFile, "");
            var cacheFile = Path.Combine(tempDir, "cache.json");

            var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");

            var source = new TokenCostSnapshotSource(
                "claude",
                TriggerTool.Claude,
                cacheFile,
                _ => new List<TokenEvent>(),
                () => new[] { logFile },
                lookbackDays: 7);

            var snapshot = await source.ReadAsync(observed, CancellationToken.None);

            Expect(snapshot.Agent == (AgentKey)"claude", "agent key matches");
            Expect(snapshot.Activity == ActivityState.Idle, "activity is idle");
            Expect(snapshot.Availability == SnapshotAvailability.NoData, "availability is NoData when empty");
            Expect(snapshot.DataAt == observed, "DataAt falls back to observedAt on NoData");
            Expect(snapshot.Usage is null, "usage is null");
            Expect(snapshot.Cost is null, "cost is null on NoData");

            Console.WriteLine("PASS TokenCostSnapshotSource yields NoData when no events");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task TestTokenCostSnapshotSourceCancellationPropagates()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AgentIslandTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logFile = Path.Combine(tempDir, "session.jsonl");
            File.WriteAllText(logFile, "{\"line\":1}\n");
            var cacheFile = Path.Combine(tempDir, "cache.json");
            var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");

            var source = new TokenCostSnapshotSource(
                "codex",
                TriggerTool.Codex,
                cacheFile,
                _ => new List<TokenEvent>
                {
                    new(TriggerTool.Codex, observed.AddMinutes(-5), "gpt-4o", 10, 10, 0, 0)
                },
                () => new[] { logFile },
                lookbackDays: 7);

            // Pre-canceled
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                try
                {
                    await source.ReadAsync(observed, cts.Token);
                    throw new InvalidOperationException("pre-canceled ReadAsync did not throw");
                }
                catch (OperationCanceledException)
                {
                    // expected
                }
            }

            // Canceled during parse
            using (var cts = new CancellationTokenSource())
            {
                var parserCalled = false;
                List<TokenEvent> CancelingParser(string path)
                {
                    parserCalled = true;
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                    return new List<TokenEvent>();
                }

                var cancelingSource = new TokenCostSnapshotSource(
                    "codex",
                    TriggerTool.Codex,
                    cacheFile,
                    CancelingParser,
                    () => new[] { logFile },
                    lookbackDays: 7);

                try
                {
                    await cancelingSource.ReadAsync(observed, cts.Token);
                    throw new InvalidOperationException("canceled ReadAsync during parsing did not throw");
                }
                catch (OperationCanceledException)
                {
                    Expect(parserCalled, "parser should have been invoked before cancel");
                }
            }

            Console.WriteLine("PASS TokenCostSnapshotSource propagates cancellation");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private sealed class FakeSource(string key, ActivityState activity) : IAgentSnapshotSource
    {
        public AgentKey Agent { get; } = key;
        public ActivityState Activity { get; } = activity;
        public Task? Gate { get; init; }
        public string? Error { get; set; }

        public async Task<AgentSnapshot> ReadAsync(
            DateTimeOffset observedAt,
            CancellationToken cancellationToken)
        {
            if (Gate is not null) await Gate.WaitAsync(cancellationToken);
            if (Error is not null) throw new InvalidOperationException(Error);
            return new AgentSnapshot(
                Agent,
                Activity,
                SnapshotAvailability.Ready,
                observedAt,
                observedAt);
        }
    }
}
