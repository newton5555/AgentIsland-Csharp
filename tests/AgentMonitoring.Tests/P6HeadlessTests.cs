using System.Reflection;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Providers.Cost.Codex;
using AgentMonitoring.Consumption;
using AgentMonitoring.Enablement;
using AgentMonitoring.Host;
using AgentMonitoring.Pricing;
using AgentMonitoring.Queries;
using AgentMonitoring.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMonitoring.Tests;

public class P6HeadlessTests
{
    [Fact]
    public void MonitoringAssemblies_DoNotReferenceWpf()
    {
        AssertNoDesktop(typeof(MonitoringQueryService).Assembly);
        AssertNoDesktop(typeof(AgentKey).Assembly);
        AssertNoDesktop(typeof(HeadlessServices).Assembly);
        Assert.Equal("AgentMonitoring.Host", typeof(HeadlessServices).Assembly.GetName().Name);
    }

    [Fact]
    public async Task HeadlessGraph_CollectsAndQueriesWithoutWpf()
    {
        var services = new ServiceCollection();
        var storePath = Path.Combine(Path.GetTempPath(), $"p6-store-{Guid.NewGuid():N}.json");
        var home = Path.Combine(Path.GetTempPath(), $"p6-codex-{Guid.NewGuid():N}");
        try
        {
            WriteSession(home, "sess-p6", 40, 8);
            services.AddHeadlessMonitoring(new HeadlessOptions
            {
                CodexHomes = new[] { home },
                ConsumptionStorePath = storePath,
            });
            using var provider = services.BuildServiceProvider();
            var collector = provider.GetRequiredService<IConsumptionCollector>();
            var query = provider.GetRequiredService<IMonitoringQuery>();
            await collector.CollectAsync(AgentKeys.Codex);
            var start = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
            var end = DateTimeOffset.Parse("2035-01-01T00:00:00Z");
            var summary = query.GetConsumptionSummary(start, end);
            Assert.True(summary.Tokens >= 48);
            var rebuilt = new MonitoringQueryService(
                new LedgerSnapshotStore(),
                provider.GetRequiredService<IConsumptionQuery>(),
                provider.GetRequiredService<IPricer>());
            var again = rebuilt.GetConsumptionSummary(start, end);
            Assert.Equal(summary.Tokens, again.Tokens);
        }
        finally
        {
            TryDelete(storePath);
            TryDeleteDir(home);
        }
    }

    [Fact]
    public async Task PersistedStore_SurvivesNewProcessGraph()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"p6-persist-{Guid.NewGuid():N}.json");
        var home = Path.Combine(Path.GetTempPath(), $"p6-home-{Guid.NewGuid():N}");
        try
        {
            WriteSession(home, "sess-persist", 20, 2);
            var first = new ConsumptionStore(storePath);
            var collector = new CodexConsumptionCollector(
                first, () => CodexRolloutDiscovery.FromHomes(new[] { home }));
            await collector.CollectAsync(AgentKeys.Codex);
            var tokens = first.Read(AgentKeys.Codex).Sum(fact => fact.Tokens.WireTokens);
            Assert.True(tokens > 0);

            var second = new ConsumptionStore(storePath);
            Assert.Equal(tokens, second.Read(AgentKeys.Codex).Sum(fact => fact.Tokens.WireTokens));
        }
        finally
        {
            TryDelete(storePath);
            TryDeleteDir(home);
        }
    }

    [Fact]
    public async Task AgentRuntime_DoesNotLeaveInFlightWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new BlockingReader(started, gate);
        var runtime = new AgentRuntime(
            new LedgerSnapshotStore(),
            AlwaysEnabledAgents.Instance,
            new[] { new StubProvider(reader) });
        var first = runtime.CollectCostAsync(AgentKeys.Claude, 30, DateTimeOffset.UtcNow);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = runtime.CollectCostAsync(AgentKeys.Claude, 30, DateTimeOffset.UtcNow);
        Assert.Equal(1, reader.Starts);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, reader.Starts);
        await runtime.CollectCostAsync(AgentKeys.Claude, 30, DateTimeOffset.UtcNow);
        Assert.Equal(2, reader.Starts);
    }

    [Fact]
    public async Task AgentRuntime_FirstConsumerCancelDoesNotCancelSharedCollect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new BlockingReader(started, gate);
        var runtime = new AgentRuntime(
            new LedgerSnapshotStore(),
            AlwaysEnabledAgents.Instance,
            new[] { new StubProvider(reader) });
        using var firstCts = new CancellationTokenSource();
        var first = runtime.CollectCostAsync(AgentKeys.Claude, 30, DateTimeOffset.UtcNow, firstCts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        firstCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = runtime.CollectCostAsync(AgentKeys.Claude, 30, DateTimeOffset.UtcNow);
        Assert.Equal(1, reader.Starts);
        gate.SetResult();
        var result = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentKeys.Claude.Value, result.Agent.Value);
        Assert.Equal(1, reader.Starts);
    }

    private static void AssertNoDesktop(Assembly assembly)
    {
        var names = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationFramework", names);
        Assert.DoesNotContain("PresentationCore", names);
        Assert.DoesNotContain("WindowsBase", names);
        Assert.DoesNotContain("System.Windows.Forms", names);
        Assert.DoesNotContain("AgentIsland", names);
    }

    private static void WriteSession(string home, string sessionId, long input, long output)
    {
        var dir = Path.Combine(home, "sessions");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        File.WriteAllLines(path, new[]
        {
            "{\"type\":\"session_meta\",\"timestamp\":\"2026-07-16T10:00:00Z\",\"payload\":{\"id\":\"" + sessionId + "\"}}",
            "{\"type\":\"turn_context\",\"timestamp\":\"2026-07-16T10:00:00Z\",\"payload\":{\"model\":\"gpt-5.4\"}}",
            "{\"type\":\"event_msg\",\"timestamp\":\"2026-07-16T10:00:01Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":" + input + ",\"cached_input_tokens\":0,\"output_tokens\":" + output + "},\"total_token_usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + "}}}}",
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private sealed class BlockingReader : ICostLedgerReader
    {
        private readonly TaskCompletionSource _started;
        private readonly TaskCompletionSource _gate;
        public int Starts;
        public BlockingReader(TaskCompletionSource started, TaskCompletionSource gate)
        {
            _started = started;
            _gate = gate;
        }
        public async ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(
            int lookbackDays = 30, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Starts);
            _started.TrySetResult();
            await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return Array.Empty<TokenEvent>();
        }
    }

    private sealed class StubProvider : IAgentProvider, ICostLedgerReader
    {
        private readonly ICostLedgerReader _reader;
        public StubProvider(ICostLedgerReader reader) => _reader = reader;
        public AgentDescriptor Descriptor { get; } = new(AgentKeys.Claude, "Claude", AgentCapabilities.Cost);
        public ICostLedgerReader? CostLedgerReader => _reader;
        public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
            _reader.ReadCostEventsAsync(lookbackDays, ct);
    }
}
