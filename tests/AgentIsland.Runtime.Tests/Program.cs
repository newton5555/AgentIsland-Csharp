using System.Net;
using System.Net.Http;
using System.Text;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentIsland.Providers.Sessions.DeepSeek;
using AgentIsland.Runtime.Refresh;
using AgentIsland.Runtime.Scanning;
using AgentIsland.Runtime.Snapshots;
using AgentIsland.Runtime.Sources;

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
        await TestCompositeMergesCostAndActivity();
        await TestCompositeActivityPriorityMerge();
        await TestCompositeNoDataMerge();
        await TestCompositeErrorAndDiagnosticMerge();
        await TestCompositeCancellationPropagates();
        TestCompositeDeterministicDataRules();
        TestLocalTokenSourcesPathResolution();
        TestLocalTokenSourcesEnumeration();
        await TestLocalTokenSourcesCreationAndRead();
        await TestDeepSeekActivitySnapshotSource();
        await TestSameAgentCostAndActivityMerge();
        await TestDeepSeekBalanceSnapshotSourceOk();
        await TestDeepSeekBalanceSnapshotSourceZeroAndNegative();
        await TestDeepSeekBalanceSnapshotSourceNotConfigured();
        await TestDeepSeekBalanceSnapshotSourceUnauthorized();
        await TestDeepSeekBalanceSnapshotSourceTimeout();
        await TestDeepSeekBalanceSnapshotSourceParseError();
        await TestDeepSeekBalanceSnapshotSourceCancellationPropagates();
        await TestCompositeMergesCostActivityAndBalance();
        await TestCompositeBalanceFailureDoesNotBreakCost();
        TestLocalTokenSourcesCreateSourcesIsOffline();
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

    private static async Task TestCompositeMergesCostAndActivity()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var costTime = observed.AddMinutes(-30);
        var costSummary = new ProviderCostSummary(
            12.5, 1000, 800, 50.0, 5000, 4000,
            new double[24], Array.Empty<double>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
            Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        var costSource = new FakeSource("codex", ActivityState.Idle)
        {
            Cost = costSummary,
            DataAt = costTime,
            Availability = SnapshotAvailability.Ready
        };
        var activitySource = new FakeSource("codex", ActivityState.Working)
        {
            Availability = SnapshotAvailability.Ready
        };

        var runtime = new AgentRuntime(new IAgentSnapshotSource[] { costSource, activitySource });
        var refreshed = await runtime.RefreshAsync(observed);

        Expect(refreshed, "refresh succeeds with composite sources");
        Expect(runtime.Snapshots.Count == 1, "duplicate agent sources are collapsed into one snapshot");
        Expect(runtime.Snapshots.ContainsKey("codex"), "contains composite agent key");

        var snapshot = runtime.Snapshots["codex"];
        Expect(snapshot.Activity == ActivityState.Working, "activity is taken from the working source");
        Expect(snapshot.Availability == SnapshotAvailability.Ready, "availability is ready");
        Expect(snapshot.Cost is not null && snapshot.Cost.TodayTokens == 1000, "cost summary is retained from cost facet");
        Expect(snapshot.Error is null, "no error on healthy composite read");

        Console.WriteLine("PASS Composite merges cost and activity facets for duplicate agent");
    }

    private static async Task TestCompositeActivityPriorityMerge()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");

        // Working (1) > Idle (0)
        var s1 = new FakeSource("agent1", ActivityState.Idle);
        var s2 = new FakeSource("agent1", ActivityState.Working);
        var r1 = new AgentRuntime(new[] { s1, s2 });
        await r1.RefreshAsync(observed);
        Expect(r1.Snapshots["agent1"].Activity == ActivityState.Working, "Working beats Idle");

        // NeedsYou (2) > Working (1)
        var s3 = new FakeSource("agent2", ActivityState.Working);
        var s4 = new FakeSource("agent2", ActivityState.NeedsYou);
        var r2 = new AgentRuntime(new[] { s3, s4 });
        await r2.RefreshAsync(observed);
        Expect(r2.Snapshots["agent2"].Activity == ActivityState.NeedsYou, "NeedsYou beats Working");

        // Stalled (3) > NeedsYou (2)
        var s5 = new FakeSource("agent3", ActivityState.NeedsYou);
        var s6 = new FakeSource("agent3", ActivityState.Stalled);
        var r3 = new AgentRuntime(new[] { s5, s6 });
        await r3.RefreshAsync(observed);
        Expect(r3.Snapshots["agent3"].Activity == ActivityState.Stalled, "Stalled beats NeedsYou");

        // RateLimited (4) > Stalled (3)
        var s7 = new FakeSource("agent4", ActivityState.Stalled);
        var s8 = new FakeSource("agent4", ActivityState.RateLimited);
        var r4 = new AgentRuntime(new[] { s7, s8 });
        await r4.RefreshAsync(observed);
        Expect(r4.Snapshots["agent4"].Activity == ActivityState.RateLimited, "RateLimited beats Stalled");

        // AuthRequired (5) > RateLimited (4)
        var s9 = new FakeSource("agent5", ActivityState.RateLimited);
        var s10 = new FakeSource("agent5", ActivityState.AuthRequired);
        var r5 = new AgentRuntime(new[] { s9, s10 });
        await r5.RefreshAsync(observed);
        Expect(r5.Snapshots["agent5"].Activity == ActivityState.AuthRequired, "AuthRequired beats RateLimited");

        Console.WriteLine("PASS Composite resolves activity urgency priority");
    }

    private static async Task TestCompositeNoDataMerge()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var realDataTime = observed.AddMinutes(-45);

        // Source 1: Ready with real data
        var readySource = new FakeSource("claude", ActivityState.Working)
        {
            Availability = SnapshotAvailability.Ready,
            DataAt = realDataTime
        };

        // Source 2: NoData with observed time
        var noDataSource = new FakeSource("claude", ActivityState.Idle)
        {
            Availability = SnapshotAvailability.NoData,
            DataAt = observed
        };

        var runtime = new AgentRuntime(new[] { readySource, noDataSource });
        await runtime.RefreshAsync(observed);

        var snapshot = runtime.Snapshots["claude"];
        Expect(snapshot.Availability == SnapshotAvailability.Ready, "NoData does not overwrite Ready");
        Expect(snapshot.Activity == ActivityState.Working, "Working activity preserved");
        Expect(snapshot.DataAt == realDataTime, "Real DataAt preserved over NoData placeholder");

        // When all sources are NoData
        var empty1 = new FakeSource("grok", ActivityState.Idle) { Availability = SnapshotAvailability.NoData };
        var empty2 = new FakeSource("grok", ActivityState.Idle) { Availability = SnapshotAvailability.NoData };
        var runtimeEmpty = new AgentRuntime(new[] { empty1, empty2 });
        await runtimeEmpty.RefreshAsync(observed);

        var emptySnapshot = runtimeEmpty.Snapshots["grok"];
        Expect(emptySnapshot.Availability == SnapshotAvailability.NoData, "All NoData sources yield NoData");

        Console.WriteLine("PASS Composite preserves Ready availability over NoData");
    }

    private static async Task TestCompositeErrorAndDiagnosticMerge()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var costTime = observed.AddMinutes(-10);
        var costSummary = new ProviderCostSummary(
            5.0, 500, 400, 10.0, 1000, 800,
            new double[24], Array.Empty<double>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
            Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        // 1. Partial failure: One facet Ready, one facet Errors -> Preserves Ready and diagnostic error
        var goodSource = new FakeSource("codex", ActivityState.Working)
        {
            Cost = costSummary,
            DataAt = costTime,
            Availability = SnapshotAvailability.Ready
        };
        var badSource = new FakeSource("codex", ActivityState.Idle)
        {
            Error = "rate limit probe failed"
        };

        var runtime = new AgentRuntime(new[] { goodSource, badSource });
        await runtime.RefreshAsync(observed);

        var snapshot = runtime.Snapshots["codex"];
        Expect(snapshot.Availability == SnapshotAvailability.Ready, "Partial failure remains Ready when a facet is Ready");
        Expect(snapshot.Activity == ActivityState.Working, "Activity is preserved from working facet");
        Expect(snapshot.Cost is not null && snapshot.Cost.TodayTokens == 500, "Cost is preserved from good facet");
        Expect(snapshot.Error is not null && snapshot.Error.Contains("rate limit probe failed"),
            "Error diagnostic is preserved");

        // 2. Full failure preserving stale data
        var failSource1 = new FakeSource("cursor", ActivityState.Working)
        {
            Cost = costSummary,
            DataAt = costTime
        };
        var failSource2 = new FakeSource("cursor", ActivityState.Idle) { DataAt = costTime };
        var failRuntime = new AgentRuntime(new[] { failSource1, failSource2 });
        await failRuntime.RefreshAsync(observed);
        var initialSnapshot = failRuntime.Snapshots["cursor"];
        Expect(initialSnapshot.Availability == SnapshotAvailability.Ready, "initial refresh ready");
        Expect(initialSnapshot.DataAt == costTime, "initial snapshot DataAt matches costTime");

        // In second refresh, both fail
        failSource1.Error = "disk I/O error";
        failSource2.Error = "network timeout";
        await failRuntime.RefreshAsync(observed.AddMinutes(5));

        var staleSnapshot = failRuntime.Snapshots["cursor"];
        Expect(staleSnapshot.Availability == SnapshotAvailability.Stale, "full failure preserves stale availability");
        Expect(staleSnapshot.DataAt == costTime, "stale snapshot preserves previous DataAt");
        Expect(staleSnapshot.Cost is not null && staleSnapshot.Cost.TodayTokens == 500, "stale snapshot preserves previous Cost");
        Expect(staleSnapshot.Error is not null
            && staleSnapshot.Error.Contains("disk I/O error")
            && staleSnapshot.Error.Contains("network timeout"),
            "stale snapshot aggregates all facet error diagnostics");

        // 3. AgentKey mismatch becomes a diagnosable error
        var mismatchedSource = new FakeSource("codex", ActivityState.Idle)
        {
            ReturnAgent = "antigravity"
        };
        var mismatchComposite = new CompositeAgentSnapshotSource("codex", new[] { mismatchedSource });
        var mismatchSnapshot = await mismatchComposite.ReadAsync(observed, CancellationToken.None);
        Expect(mismatchSnapshot.Availability == SnapshotAvailability.Error, "agent mismatch yields Error");
        Expect(mismatchSnapshot.Error is not null && mismatchSnapshot.Error.Contains("mismatch"),
            "agent mismatch is reported in error diagnostics");

        Console.WriteLine("PASS Composite preserves error diagnostics, stale data, and reports agent mismatch");
    }

    private static async Task TestCompositeCancellationPropagates()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s1 = new FakeSource("deepseek", ActivityState.Working) { Gate = gate.Task };
        var s2 = new FakeSource("deepseek", ActivityState.Idle);

        var runtime = new AgentRuntime(new[] { s1, s2 });
        using var cts = new CancellationTokenSource();
        var refresh = runtime.RefreshAsync(cancellationToken: cts.Token);
        cts.Cancel();

        try
        {
            await refresh;
            throw new InvalidOperationException("cancellation was swallowed by composite source");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("PASS Composite propagates cancellation across all facets");
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    private static void TestCompositeDeterministicDataRules()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var time1 = observed.AddMinutes(-20);
        var time2 = observed.AddMinutes(-10);

        var costEmpty = ProviderCostSummary.Empty;
        var costActiveOld = new ProviderCostSummary(
            5.0, 100, 100, 10.0, 200, 200,
            new double[24], Array.Empty<double>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
            Array.Empty<DailyTokenBucket>(), Array.Empty<string>());
        var costActiveNew = new ProviderCostSummary(
            15.0, 300, 300, 30.0, 600, 600,
            new double[24], Array.Empty<double>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
            Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        // Active cost preferred over empty cost
        var snapEmpty = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Ready, observed, time2, Cost: costEmpty);
        var snapActiveOld = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Ready, observed, time1, Cost: costActiveOld);
        var merged1 = CompositeAgentSnapshotSource.Merge("codex", observed, new[] { snapEmpty, snapActiveOld });
        Expect(merged1.Cost == costActiveOld, "active cost is preferred over empty cost");

        // Newer DataAt cost preferred when both active
        var snapActiveNew = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Ready, observed, time2, Cost: costActiveNew);
        var merged2 = CompositeAgentSnapshotSource.Merge("codex", observed, new[] { snapActiveOld, snapActiveNew });
        Expect(merged2.Cost == costActiveNew, "newer cost is preferred when both have data");

        // Valid usage preferred over error-only usage
        var errorUsage = AppUsage.ErrorPair("quota fetch failed");
        var validUsage = new AppUsage(new WindowUsage(0.5, observed.AddHours(1), null, 18000), new WindowUsage(0.2, observed.AddDays(2), null, 604800));
        var snapErrUsage = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Ready, observed, time2, Usage: errorUsage);
        var snapValUsage = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Ready, observed, time1, Usage: validUsage);
        var merged3 = CompositeAgentSnapshotSource.Merge("codex", observed, new[] { snapErrUsage, snapValUsage });
        Expect(merged3.Usage == validUsage, "valid usage preferred over error-only usage");

        Console.WriteLine("PASS Composite uses deterministic data selection rules");
    }

    private static void TestLocalTokenSourcesPathResolution()
    {
        var customHome = Path.Combine(Path.GetTempPath(), "fake_home");
        var resolvedCodex = LocalTokenSources.ResolveCodexHome(envOverride: null, homeDir: customHome);
        Expect(resolvedCodex == Path.Combine(customHome, ".codex"), "Codex home defaults to ~/.codex");

        var envOverride = Path.Combine(Path.GetTempPath(), "custom_codex");
        var resolvedOverride = LocalTokenSources.ResolveCodexHome(envOverride: envOverride, homeDir: customHome);
        Expect(resolvedOverride == envOverride, "Codex home honors override");

        var resolvedDsh = LocalTokenSources.ResolveDeepSeekSessionsRoot(homeDir: customHome);
        Expect(resolvedDsh == Path.Combine(customHome, ".dsh", "sessions"), "DeepSeek sessions root defaults to ~/.dsh/sessions");

        var customAppData = Path.Combine(Path.GetTempPath(), "fake_appdata");
        var resolvedCache = LocalTokenSources.ResolveDefaultCacheDir(localAppData: customAppData, homeDir: customHome);
        Expect(resolvedCache == Path.Combine(customAppData, "AgentIsland", "cache"), "Cache dir resolves under LocalAppData");

        Console.WriteLine("PASS LocalTokenSources resolves cross-platform paths");
    }

    private static void TestLocalTokenSourcesEnumeration()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AgentIsland_Enum_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var codexHome = Path.Combine(tempDir, ".codex");
            var activeDir = Path.Combine(codexHome, "sessions", "2026-09");
            var archivedDir = Path.Combine(codexHome, "archived_sessions");
            Directory.CreateDirectory(activeDir);
            Directory.CreateDirectory(archivedDir);

            var activeFile = Path.Combine(activeDir, "s1.jsonl");
            var archivedFile = Path.Combine(archivedDir, "s2.jsonl");
            var ignoredFile = Path.Combine(activeDir, "other.txt");
            File.WriteAllText(activeFile, "");
            File.WriteAllText(archivedFile, "");
            File.WriteAllText(ignoredFile, "");

            var codexFiles = LocalTokenSources.EnumerateCodexFiles(codexHome).ToList();
            Expect(codexFiles.Count == 2, "Enumerates both active and archived jsonl files");
            Expect(codexFiles.Contains(activeFile) && codexFiles.Contains(archivedFile), "Contains both target files");
            Expect(!codexFiles.Contains(ignoredFile), "Filters non-jsonl files");

            var dshRoot = Path.Combine(tempDir, ".dsh", "sessions");
            var dshSessionDir = Path.Combine(dshRoot, "sess_1");
            Directory.CreateDirectory(dshSessionDir);
            var dshFile = Path.Combine(dshSessionDir, "session.jsonl.zstd");
            File.WriteAllText(dshFile, "");

            var dshFiles = LocalTokenSources.EnumerateDeepSeekHarnessFiles(dshRoot).ToList();
            Expect(dshFiles.Count == 1 && dshFiles[0] == dshFile, "Enumerates deepseek session.jsonl.zstd files");

            // Vanished root returns empty without throwing
            var emptyFiles = LocalTokenSources.EnumerateCodexFiles(Path.Combine(tempDir, "non_existent")).ToList();
            Expect(emptyFiles.Count == 0, "Non-existent codex directory returns empty list");

            Console.WriteLine("PASS LocalTokenSources enumerates files safely");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task TestLocalTokenSourcesCreationAndRead()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AgentIsland_SourceCreate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logFile = Path.Combine(tempDir, "sample.jsonl");
            var nowIso = DateTimeOffset.UtcNow.ToString("o");
            // Codex rollout format
            File.WriteAllText(logFile,
                $"{{\"type\":\"turn_context\",\"payload\":{{\"model\":\"gpt-4o\"}}}}\n" +
                $"{{\"type\":\"event_msg\",\"timestamp\":\"{nowIso}\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"input_tokens\":50,\"output_tokens\":50}},\"last_token_usage\":{{\"input_tokens\":50,\"output_tokens\":50}}}}}}}}\n");

            var codexSource = LocalTokenSources.CreateCodex(
                lookbackDays: 7,
                cacheFilePath: Path.Combine(tempDir, "codex_cache.json"),
                fileProvider: () => new[] { logFile });

            var observed = DateTimeOffset.Now;
            var snapshot = await codexSource.ReadAsync(observed, CancellationToken.None);

            Expect(snapshot.Agent == (AgentKey)"codex", "Agent is codex");
            Expect(snapshot.Availability == SnapshotAvailability.Ready, "Snapshot is ready when events present");
            Expect(snapshot.Cost is not null && snapshot.Cost.TodayTokens == 100, "Tokens are summarized from log");

            var allSources = LocalTokenSources.CreateSources(
                lookbackDays: 7,
                cacheDir: tempDir,
                codexFileProvider: () => new[] { logFile },
                deepSeekFileProvider: Array.Empty<string>);
            Expect(allSources.Count == 3, "CreateSources returns codex cost, deepseek cost, and deepseek activity sources");
            Expect(allSources[0].Agent == (AgentKey)"codex", "First is codex");
            Expect(allSources[1].Agent == (AgentKey)"deepseek", "Second is deepseek cost");
            Expect(allSources[2].Agent == (AgentKey)"deepseek", "Third is deepseek activity");
            var codexSnap = await allSources[0].ReadAsync(observed, CancellationToken.None);
            Expect(codexSnap.Cost?.TodayTokens == 100, "Injected codexFileProvider is used by CreateSources");

            Console.WriteLine("PASS LocalTokenSources creates runnable snapshot sources");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task TestDeepSeekActivitySnapshotSource()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");

        // 1. Working: Active event within 18s (e.g. 5s ago) -> Working, Ready
        var activeRecent = new DeepSeekActivitySnapshot(
            LatestProvider: "deepseek",
            LatestKind: DeepSeekActivityKind.Active,
            LatestTimestamp: observed.AddSeconds(-5),
            Model: "deepseek-chat",
            Turn: 1,
            Step: 1);

        var s1 = new DeepSeekActivitySnapshotSource(
            fileProvider: () => new[] { "session1.zstd" },
            parser: _ => activeRecent);

        var snap1 = await s1.ReadAsync(observed, CancellationToken.None);
        Expect(snap1.Activity == ActivityState.Working, "Active within 18s yields Working");
        Expect(snap1.Availability == SnapshotAvailability.Ready, "Valid session yields Ready");
        Expect(snap1.DataAt == observed.AddSeconds(-5), "DataAt matches latest event time");

        // 2. Idle: Completed event (even if 5s ago) -> Idle
        var completedRecent = new DeepSeekActivitySnapshot(
            LatestProvider: "deepseek",
            LatestKind: DeepSeekActivityKind.Completed,
            LatestTimestamp: observed.AddSeconds(-5),
            Model: "deepseek-chat",
            Turn: 1,
            Step: 2);

        var s2 = new DeepSeekActivitySnapshotSource(
            fileProvider: () => new[] { "session2.zstd" },
            parser: _ => completedRecent);

        var snap2 = await s2.ReadAsync(observed, CancellationToken.None);
        Expect(snap2.Activity == ActivityState.Idle, "Completed event yields Idle");
        Expect(snap2.Availability == SnapshotAvailability.Ready, "Completed event yields Ready availability");

        // 3. 18-second boundary:
        // Exactly 18s -> Working
        var active18s = new DeepSeekActivitySnapshot(
            LatestProvider: "deepseek",
            LatestKind: DeepSeekActivityKind.Active,
            LatestTimestamp: observed.AddSeconds(-18),
            Model: "deepseek-chat",
            Turn: 1,
            Step: 1);
        var s3a = new DeepSeekActivitySnapshotSource(
            fileProvider: () => new[] { "session3a.zstd" },
            parser: _ => active18s);
        var snap3a = await s3a.ReadAsync(observed, CancellationToken.None);
        Expect(snap3a.Activity == ActivityState.Working, "Exactly 18s is within ActiveWindow -> Working");

        // 19s (> 18s) -> Idle
        var active19s = new DeepSeekActivitySnapshot(
            LatestProvider: "deepseek",
            LatestKind: DeepSeekActivityKind.Active,
            LatestTimestamp: observed.AddSeconds(-19),
            Model: "deepseek-chat",
            Turn: 1,
            Step: 1);
        var s3b = new DeepSeekActivitySnapshotSource(
            fileProvider: () => new[] { "session3b.zstd" },
            parser: _ => active19s);
        var snap3b = await s3b.ReadAsync(observed, CancellationToken.None);
        Expect(snap3b.Activity == ActivityState.Idle, "19s is outside ActiveWindow -> Idle");

        // 4. Subagent exclusion:
        // Subagent session is Active 2s ago, but human session is Completed 10s ago
        var subagentActive = new DeepSeekActivitySnapshot(
            LatestProvider: "deepseek",
            LatestKind: DeepSeekActivityKind.Active,
            LatestTimestamp: observed.AddSeconds(-2),
            Model: "deepseek-chat",
            Turn: 1,
            Step: 1,
            Origin: "subagent",
            DelegationDepth: 1);
        Expect(subagentActive.IsSubagent, "Subagent session correctly identifies as IsSubagent");

        var humanCompleted = new DeepSeekActivitySnapshot(
            LatestProvider: "deepseek",
            LatestKind: DeepSeekActivityKind.Completed,
            LatestTimestamp: observed.AddSeconds(-10),
            Model: "deepseek-chat",
            Turn: 1,
            Step: 2);

        var s4 = new DeepSeekActivitySnapshotSource(
            fileProvider: () => new[] { "sub.zstd", "human.zstd" },
            parser: path => path == "sub.zstd" ? subagentActive : humanCompleted);
        var snap4 = await s4.ReadAsync(observed, CancellationToken.None);
        Expect(snap4.Activity == ActivityState.Idle, "Subagent is excluded, human Completed determines Idle");
        Expect(snap4.Availability == SnapshotAvailability.Ready, "Human session present yields Ready");
        Expect(snap4.DataAt == observed.AddSeconds(-10), "DataAt reflects human session, not subagent");

        // Subagent only -> NoData
        var s4OnlySub = new DeepSeekActivitySnapshotSource(
            fileProvider: () => new[] { "sub.zstd" },
            parser: _ => subagentActive);
        var snap4OnlySub = await s4OnlySub.ReadAsync(observed, CancellationToken.None);
        Expect(snap4OnlySub.Availability == SnapshotAvailability.NoData, "Only subagent sessions yields NoData");
        Expect(snap4OnlySub.Activity == ActivityState.Idle, "No human session yields Idle");

        // 5. NoData: empty file list
        var s5Empty = new DeepSeekActivitySnapshotSource(
            fileProvider: () => Array.Empty<string>());
        var snap5 = await s5Empty.ReadAsync(observed, CancellationToken.None);
        Expect(snap5.Availability == SnapshotAvailability.NoData, "Empty sessions yields NoData");

        // 6. Cancellation propagation
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var s6 = new DeepSeekActivitySnapshotSource(
            fileProvider: () => new[] { "session.zstd" },
            parser: _ => activeRecent);
        try
        {
            await s6.ReadAsync(observed, cts.Token);
            throw new InvalidOperationException("Cancellation was ignored");
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Console.WriteLine("PASS DeepSeekActivitySnapshotSource handles Working/Idle, 18s window, subagents, and cancellation");
    }

    private static async Task TestSameAgentCostAndActivityMerge()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var tempDir = Path.Combine(Path.GetTempPath(), $"AgentIsland_Merge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logFile = Path.Combine(tempDir, "session.jsonl");
            var nowIso = observed.AddMinutes(-5).ToString("o");
            // DeepSeek rollout log line for token parsing
            File.WriteAllText(logFile,
                $"{{\"type\":\"request/context\",\"time\":\"{nowIso}\",\"data\":{{\"provider\":\"deepseek-official\",\"model\":\"deepseek-chat\"}}}}\n" +
                $"{{\"type\":\"assistant/message\",\"time\":\"{nowIso}\",\"data\":{{\"model\":\"deepseek-chat\",\"usage\":{{\"inputTokens\":200,\"outputTokens\":100}}}}}}\n");

            var dshCostSource = LocalTokenSources.CreateDeepSeekHarness(
                lookbackDays: 7,
                cacheFilePath: Path.Combine(tempDir, "dsh_cache.json"),
                fileProvider: () => new[] { logFile });

            var activeRecent = new DeepSeekActivitySnapshot(
                LatestProvider: "deepseek-official",
                LatestKind: DeepSeekActivityKind.Active,
                LatestTimestamp: observed.AddSeconds(-6),
                Model: "deepseek-chat",
                Turn: 1,
                Step: 1);

            var dshActivitySource = LocalTokenSources.CreateDeepSeekActivity(
                fileProvider: () => new[] { "session.zstd" },
                parser: _ => activeRecent);

            // Put both into AgentRuntime
            var runtime = new AgentRuntime(new[] { dshCostSource, dshActivitySource });
            await runtime.RefreshAsync(observed, CancellationToken.None);

            Expect(runtime.Snapshots.Count == 1, "AgentRuntime collapses cost and activity into single deepseek snapshot");
            var snapshot = runtime.Snapshots[(AgentKey)"deepseek"];
            Expect(snapshot.Activity == ActivityState.Working, "Activity from activity source is Working");
            Expect(snapshot.Availability == SnapshotAvailability.Ready, "Snapshot availability is Ready");
            Expect(snapshot.Cost is not null && snapshot.Cost.TodayTokens == 300, "Tokens from cost source are preserved");

            Console.WriteLine("PASS Same agent cost and activity facets merge seamlessly in AgentRuntime");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task TestDeepSeekBalanceSnapshotSourceOk()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        const string json = """
        {
            "is_available": true,
            "balance_infos": [
                {
                    "currency": "CNY",
                    "total_balance": "12.50",
                    "granted_balance": "2.50",
                    "topped_up_balance": "10.00"
                },
                {
                    "currency": "USD",
                    "total_balance": "5.00",
                    "granted_balance": "0.00",
                    "topped_up_balance": "5.00"
                }
            ]
        }
        """;

        MockHttpMessageHandler? handler = null;
        handler = new MockHttpMessageHandler((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var source = new DeepSeekBalanceSnapshotSource(
            httpClient: client,
            apiKeyProvider: () => "test-sk-12345");

        var snapshot = await source.ReadAsync(observed, CancellationToken.None);

        Expect(snapshot.Availability == SnapshotAvailability.Ready, "Valid balance response yields Ready");
        Expect(snapshot.Agent == (AgentKey)"deepseek", "Agent is deepseek");
        Expect(snapshot.Balance is not null, "Balance snapshot is populated");
        Expect(snapshot.Balance!.IsAvailable, "Balance is available");
        Expect(snapshot.Balance.Entries.Count == 2, "2 balance entries returned");

        var cny = snapshot.Balance.Entries[0];
        Expect(cny.Currency == "CNY", "First currency is CNY");
        Expect(cny.Total == 12.50m, "CNY total is 12.50");
        Expect(cny.Granted == 2.50m, "CNY granted is 2.50");
        Expect(cny.ToppedUp == 10.00m, "CNY topped up is 10.00");
        Expect(!cny.IsInArrears, "CNY is not in arrears");

        var usd = snapshot.Balance.Entries[1];
        Expect(usd.Currency == "USD", "Second currency is USD");
        Expect(usd.Total == 5.00m, "USD total is 5.00");

        Expect(handler.LastRequest is not null, "Request was sent");
        Expect(handler.LastRequest!.RequestUri?.ToString() == DeepSeekBalanceSnapshotSource.Endpoint, "Hits official endpoint");
        Expect(handler.LastRequest.Headers.Authorization?.Scheme == "Bearer", "Authorization is Bearer");
        Expect(handler.LastRequest.Headers.Authorization?.Parameter == "test-sk-12345", "Bearer token matches");

        Console.WriteLine("PASS DeepSeekBalanceSnapshotSource parses valid balance with CNY and USD");
    }

    private static async Task TestDeepSeekBalanceSnapshotSourceZeroAndNegative()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        // Test zero balance: valid data, not empty or error!
        const string zeroJson = """
        {
            "is_available": true,
            "balance_infos": [
                {
                    "currency": "CNY",
                    "total_balance": "0.00",
                    "granted_balance": "0.00",
                    "topped_up_balance": "0.00"
                }
            ]
        }
        """;

        var handlerZero = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(zeroJson, Encoding.UTF8, "application/json")
        }));
        using var clientZero = new HttpClient(handlerZero);
        var sourceZero = new DeepSeekBalanceSnapshotSource(httpClient: clientZero, apiKeyProvider: () => "key");
        var snapZero = await sourceZero.ReadAsync(observed, CancellationToken.None);

        Expect(snapZero.Availability == SnapshotAvailability.Ready, "Zero balance is Ready");
        Expect(snapZero.Balance is not null && snapZero.Balance.HasEntries, "Zero balance has entries");
        Expect(snapZero.Balance!.Total == 0.00m, "Total balance is 0.00");
        Expect(!snapZero.Balance.PrimaryEntry!.IsInArrears, "0.00 is not in arrears");

        // Test negative balance: in arrears
        const string negativeJson = """
        {
            "is_available": true,
            "balance_infos": [
                {
                    "currency": "CNY",
                    "total_balance": "-3.80",
                    "granted_balance": "0.00",
                    "topped_up_balance": "-3.80"
                }
            ]
        }
        """;

        var handlerNeg = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(negativeJson, Encoding.UTF8, "application/json")
        }));
        using var clientNeg = new HttpClient(handlerNeg);
        var sourceNeg = new DeepSeekBalanceSnapshotSource(httpClient: clientNeg, apiKeyProvider: () => "key");
        var snapNeg = await sourceNeg.ReadAsync(observed, CancellationToken.None);

        Expect(snapNeg.Availability == SnapshotAvailability.Ready, "Negative balance is Ready");
        Expect(snapNeg.Balance is not null, "Negative balance populated");
        Expect(snapNeg.Balance!.Total == -3.80m, "Negative balance total is -3.80");
        Expect(snapNeg.Balance.PrimaryEntry!.IsInArrears, "Negative balance is in arrears");

        Console.WriteLine("PASS DeepSeekBalanceSnapshotSource handles zero and negative (arrears) balances");
    }

    private static async Task TestDeepSeekBalanceSnapshotSourceNotConfigured()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var requestMade = false;
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            requestMade = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var client = new HttpClient(handler);
        var sourceNull = new DeepSeekBalanceSnapshotSource(httpClient: client, apiKeyProvider: () => null);
        var snapNull = await sourceNull.ReadAsync(observed, CancellationToken.None);

        Expect(snapNull.Availability == SnapshotAvailability.NotConfigured, "Null API key yields NotConfigured");
        Expect(snapNull.DataAt is null, "NotConfigured balance has no data timestamp");
        Expect(!requestMade, "No HTTP call when API key is null");

        var sourceEmpty = new DeepSeekBalanceSnapshotSource(httpClient: client, apiKeyProvider: () => "   ");
        var snapEmpty = await sourceEmpty.ReadAsync(observed, CancellationToken.None);

        Expect(snapEmpty.Availability == SnapshotAvailability.NotConfigured, "Whitespace API key yields NotConfigured");
        Expect(snapEmpty.DataAt is null, "Whitespace API key has no data timestamp");
        Expect(!requestMade, "No HTTP call when API key is whitespace");

        Console.WriteLine("PASS DeepSeekBalanceSnapshotSource returns NotConfigured when API key is missing");
    }

    private static async Task TestDeepSeekBalanceSnapshotSourceUnauthorized()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var handler401 = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var client401 = new HttpClient(handler401);
        var source401 = new DeepSeekBalanceSnapshotSource(httpClient: client401, apiKeyProvider: () => "bad-key");
        var snap401 = await source401.ReadAsync(observed, CancellationToken.None);

        Expect(snap401.Availability == SnapshotAvailability.Error, "401 yields Error");
        Expect(snap401.Error == "unauthorized", "401 maps to 'unauthorized'");
        Expect(snap401.DataAt is null, "Unauthorized balance has no data timestamp");

        var handler403 = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var client403 = new HttpClient(handler403);
        var source403 = new DeepSeekBalanceSnapshotSource(httpClient: client403, apiKeyProvider: () => "bad-key");
        var snap403 = await source403.ReadAsync(observed, CancellationToken.None);

        Expect(snap403.Availability == SnapshotAvailability.Error, "403 yields Error");
        Expect(snap403.Error == "unauthorized", "403 maps to 'unauthorized'");

        Console.WriteLine("PASS DeepSeekBalanceSnapshotSource maps 401/403 to unauthorized Error");
    }

    private static async Task TestDeepSeekBalanceSnapshotSourceTimeout()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var handler = new MockHttpMessageHandler((_, _) => throw new OperationCanceledException());
        using var client = new HttpClient(handler);
        var source = new DeepSeekBalanceSnapshotSource(httpClient: client, apiKeyProvider: () => "valid-key");
        var snap = await source.ReadAsync(observed, CancellationToken.None);

        Expect(snap.Availability == SnapshotAvailability.Error, "Timeout yields Error");
        Expect(snap.Error == "network timeout", "Timeout maps to 'network timeout'");
        Expect(snap.DataAt is null, "Timeout balance has no data timestamp");

        Console.WriteLine("PASS DeepSeekBalanceSnapshotSource maps timeout to network timeout Error");
    }

    private static async Task TestDeepSeekBalanceSnapshotSourceParseError()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not valid json }", Encoding.UTF8, "application/json")
        }));
        using var client = new HttpClient(handler);
        var source = new DeepSeekBalanceSnapshotSource(httpClient: client, apiKeyProvider: () => "valid-key");
        var snap = await source.ReadAsync(observed, CancellationToken.None);

        Expect(snap.Availability == SnapshotAvailability.Error, "Corrupt json yields Error");
        Expect(snap.Error == "parse error", "Maps to 'parse error'");
        Expect(snap.DataAt is null, "Parse failure balance has no data timestamp");

        Console.WriteLine("PASS DeepSeekBalanceSnapshotSource maps corrupt json to parse error Error");
    }

    private static async Task TestDeepSeekBalanceSnapshotSourceCancellationPropagates()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        using var client = new HttpClient();
        var source = new DeepSeekBalanceSnapshotSource(httpClient: client, apiKeyProvider: () => "valid-key");

        using var preCts = new CancellationTokenSource();
        preCts.Cancel();

        try
        {
            await source.ReadAsync(observed, preCts.Token);
            throw new InvalidOperationException("pre-cancelled read did not throw");
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        using var inFlightCts = new CancellationTokenSource();
        var inFlightHandler = new MockHttpMessageHandler(async (_, ct) =>
        {
            inFlightCts.Cancel();
            await Task.Delay(100, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var inFlightClient = new HttpClient(inFlightHandler);
        var inFlightSource = new DeepSeekBalanceSnapshotSource(httpClient: inFlightClient, apiKeyProvider: () => "valid-key");

        try
        {
            await inFlightSource.ReadAsync(observed, inFlightCts.Token);
            throw new InvalidOperationException("in-flight cancelled read did not throw");
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Console.WriteLine("PASS DeepSeekBalanceSnapshotSource propagates external cancellation");
    }

    private static async Task TestCompositeMergesCostActivityAndBalance()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var costSummary = new ProviderCostSummary(
            15.0, 1500, 1500, 30.0, 3000, 3000,
            new double[24], Array.Empty<double>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
            Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        var costFacet = new FakeSource("deepseek", ActivityState.Idle)
        {
            Cost = costSummary,
            Availability = SnapshotAvailability.Ready,
            DataAt = observed.AddMinutes(-2),
        };

        var activityFacet = new FakeSource("deepseek", ActivityState.Working)
        {
            Availability = SnapshotAvailability.Ready,
            DataAt = observed.AddSeconds(-5),
        };

        var balanceSnapshot = new AccountBalanceSnapshot(
            IsAvailable: true,
            Entries: new[]
            {
                new AccountBalanceEntry("CNY", 88.50m, 10.00m, 78.50m)
            });

        var balanceFacet = new FakeSource("deepseek", ActivityState.Idle)
        {
            Balance = balanceSnapshot,
            Availability = SnapshotAvailability.Ready,
            DataAt = observed.AddSeconds(-1),
        };

        var composite = new CompositeAgentSnapshotSource(new[] { costFacet, activityFacet, balanceFacet });
        var merged = await composite.ReadAsync(observed, CancellationToken.None);

        Expect(merged.Agent == (AgentKey)"deepseek", "Agent is deepseek");
        Expect(merged.Activity == ActivityState.Working, "Activity takes highest urgency Working");
        Expect(merged.Availability == SnapshotAvailability.Ready, "Merged availability is Ready");
        Expect(merged.Cost is not null && merged.Cost.TodayTokens == 1500, "Cost tokens preserved");
        Expect(merged.Balance is not null, "Balance is populated");
        Expect(merged.Balance!.Total == 88.50m, "Balance total is 88.50");
        Expect(merged.Balance.Currency == "CNY", "Balance currency is CNY");
        Expect(merged.Error is null, "No error on all-ready composite");

        Console.WriteLine("PASS CompositeAgentSnapshotSource merges cost, activity, and balance facets");
    }

    private static async Task TestCompositeBalanceFailureDoesNotBreakCost()
    {
        var observed = DateTimeOffset.Parse("2026-09-04T12:00:00+08:00");
        var costSummary = new ProviderCostSummary(
            20.0, 2000, 2000, 40.0, 4000, 4000,
            new double[24], Array.Empty<double>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
            Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        var costFacet = new FakeSource("deepseek", ActivityState.Idle)
        {
            Cost = costSummary,
            Availability = SnapshotAvailability.Ready,
            DataAt = observed.AddMinutes(-1),
        };

        var balanceFacet = new FakeSource("deepseek", ActivityState.Idle)
        {
            Availability = SnapshotAvailability.Error,
            Error = "unauthorized",
        };

        var composite = new CompositeAgentSnapshotSource(new[] { costFacet, balanceFacet });
        var merged = await composite.ReadAsync(observed, CancellationToken.None);

        Expect(merged.Availability == SnapshotAvailability.Ready, "Ready Cost facet prevents balance error from destroying availability");
        Expect(merged.Cost is not null && merged.Cost.TodayTokens == 2000, "Cost tokens are retained");
        Expect(merged.Error is not null && merged.Error.Contains("unauthorized"), "Error diagnostic from balance facet is preserved");

        Console.WriteLine("PASS CompositeAgentSnapshotSource preserves Ready status and diagnostics when balance fails");
    }

    private static void TestLocalTokenSourcesCreateSourcesIsOffline()
    {
        var sources = LocalTokenSources.CreateSources();
        Expect(sources.Count == 3, "CreateSources returns exactly 3 offline sources");
        Expect(sources.All(s => s is not DeepSeekBalanceSnapshotSource), "CreateSources must NOT contain DeepSeekBalanceSnapshotSource");

        var allSources = LocalTokenSources.CreateAll();
        Expect(allSources.Count == 3, "CreateAll returns exactly 3 offline sources");
        Expect(allSources.All(s => s is not DeepSeekBalanceSnapshotSource), "CreateAll must NOT contain DeepSeekBalanceSnapshotSource");

        var balanceSource = LocalTokenSources.CreateDeepSeekBalance(apiKeyProvider: () => "test");
        Expect(balanceSource is DeepSeekBalanceSnapshotSource, "CreateDeepSeekBalance returns DeepSeekBalanceSnapshotSource explicitly");

        var onlineSources = LocalTokenSources.CreateSources(includeDeepSeekBalance: true);
        Expect(onlineSources.Count == 4, "includeDeepSeekBalance adds one explicit online source");
        Expect(onlineSources.Count(s => s is DeepSeekBalanceSnapshotSource) == 1,
            "includeDeepSeekBalance adds exactly one official balance source");

        var onlineAllSources = LocalTokenSources.CreateAll(includeDeepSeekBalance: true);
        Expect(onlineAllSources.Count == 4, "CreateAll includes the explicit online balance source");

        Console.WriteLine("PASS LocalTokenSources default factory methods are strictly offline");
    }

    private sealed class MockHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return handler(request, cancellationToken);
        }
    }

    private sealed class FakeSource(string key, ActivityState activity) : IAgentSnapshotSource
    {
        public AgentKey Agent { get; } = key;
        public ActivityState Activity { get; set; } = activity;
        public SnapshotAvailability Availability { get; set; } = SnapshotAvailability.Ready;
        public Task? Gate { get; init; }
        public string? Error { get; set; }
        public ProviderCostSummary? Cost { get; set; }
        public AppUsage? Usage { get; set; }
        public DateTimeOffset? DataAt { get; set; }
        public AgentKey? ReturnAgent { get; set; }
        public AccountBalanceSnapshot? Balance { get; set; }

        public async Task<AgentSnapshot> ReadAsync(
            DateTimeOffset observedAt,
            CancellationToken cancellationToken)
        {
            if (Gate is not null) await Gate.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Error is not null && Availability != SnapshotAvailability.Error) throw new InvalidOperationException(Error);
            return new AgentSnapshot(
                ReturnAgent ?? Agent,
                Activity,
                Availability,
                observedAt,
                DataAt ?? (Availability == SnapshotAvailability.NoData ? observedAt : observedAt),
                Usage,
                Cost,
                Availability == SnapshotAvailability.Error ? (Error ?? "error") : null,
                Balance);
        }
    }
}
