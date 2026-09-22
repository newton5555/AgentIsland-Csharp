using System.IO;
using System.Text.Json;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Providers.Cost.Codex;
using AgentMonitoring.Consumption;
using AgentMonitoring.Enablement;
using AgentMonitoring.Pricing;
using AgentMonitoring.Queries;

namespace AgentIsland.Tests;

public class P2CodexConsumptionTests
{
    [Fact]
    public async Task Pipeline_MatchesTargetTotals()
    {
        foreach (var testCase in LoadCases())
        {
            var facts = await CollectAsync(Describe(testCase));
            Assert.Equal(testCase.Target.Events, facts.Count);
            Assert.Equal(testCase.Target.Input, facts.Sum(item => item.Tokens.Input));
            Assert.Equal(testCase.Target.Output, facts.Sum(item => item.Tokens.Output));
            if (testCase.Id == "06-reasoning-in-output")
            {
                Assert.Equal(8, facts[0].Tokens.Reasoning);
                Assert.Equal(ReasoningAccounting.IncludedInOutput, facts[0].Tokens.ReasoningAccounting);
                Assert.Equal(60, facts[0].Tokens.BillableTokens);
            }
        }
    }

    [Fact]
    public async Task SecondCollect_DoesNotDoubleCount()
    {
        var files = Describe(LoadCases().First(item => item.Id == "01-same-file-replay"));
        var store = new ConsumptionStore();
        var collector = new CodexConsumptionCollector(store, () => CodexRolloutDiscovery.PreferActive(files));
        await collector.CollectAsync(AgentKeys.Codex);
        await collector.CollectAsync(AgentKeys.Codex);
        var facts = store.Read(AgentKeys.Codex);
        Assert.Equal(2, facts.Count);
        Assert.Equal(150, facts.Sum(item => item.Tokens.Input));
    }

    [Fact]
    public async Task IncrementalAppend_AddsOnlyNewTurn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"p2-codex-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllTextAsync(path, """
                {"type":"session_meta","timestamp":"2026-07-16T10:00:00Z","payload":{"id":"sess-inc-001"}}
                {"type":"turn_context","timestamp":"2026-07-16T10:00:00Z","payload":{"model":"gpt-5.4"}}
                {"type":"event_msg","timestamp":"2026-07-16T10:00:01Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10},"total_token_usage":{"input_tokens":100,"output_tokens":10}}}}

                """);
            var store = new ConsumptionStore();
            var files = new[] { new CodexRolloutFile(path, "home", "inc.jsonl", false) };
            var collector = new CodexConsumptionCollector(store, () => files);
            await collector.CollectAsync(AgentKeys.Codex);
            Assert.Equal(100, store.Read(AgentKeys.Codex).Sum(item => item.Tokens.Input));

            await File.AppendAllTextAsync(path, """
                {"type":"event_msg","timestamp":"2026-07-16T10:00:02Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":50,"cached_input_tokens":0,"output_tokens":5},"total_token_usage":{"input_tokens":150,"output_tokens":15}}}}

                """);
            await collector.CollectAsync(AgentKeys.Codex);
            var facts = store.Read(AgentKeys.Codex);
            Assert.Equal(2, facts.Count);
            Assert.Equal(150, facts.Sum(item => item.Tokens.Input));
            Assert.Equal(15, facts.Sum(item => item.Tokens.Output));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Query_DoesNotDiscoverFiles()
    {
        var files = Describe(LoadCases().First(item => item.Id == "01-same-file-replay"));
        var discovers = 0;
        var store = new ConsumptionStore();
        var collector = new CodexConsumptionCollector(store, () =>
        {
            Interlocked.Increment(ref discovers);
            return CodexRolloutDiscovery.PreferActive(files);
        });
        await collector.CollectAsync(AgentKeys.Codex);
        Assert.Equal(1, discovers);

        IConsumptionQuery query = store;
        _ = query.Read(AgentKeys.Codex);
        Assert.Equal(1, discovers);

        var reader = new AgentIsland.Backend.Cost.Adapters.CodexCostLedgerReader(query, new SnapshotPricer());
        var events = await reader.ReadCostEventsAsync(3650);
        Assert.Equal(2, events.Count);
        Assert.Equal(1, discovers);

        var scan = new CostQueryService(AlwaysEnabledAgents.Instance, new[]
        {
            new CodexQueryProvider(reader),
        });
        var result = await scan.ScanAsync(AgentKeys.Codex, 3650, DateTimeOffset.Now);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(1, discovers);
    }

    [Fact]
    public async Task RebuildParseFailure_KeepsPreviousFacts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p2-rebuild-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "sess.jsonl");
        try
        {
            await File.WriteAllTextAsync(path, """
                {"type":"session_meta","timestamp":"2026-07-16T10:00:00Z","payload":{"id":"sess-keep"}}
                {"type":"turn_context","timestamp":"2026-07-16T10:00:00Z","payload":{"model":"gpt-5.4"}}
                {"type":"event_msg","timestamp":"2026-07-16T10:00:01Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":40,"cached_input_tokens":0,"output_tokens":8},"total_token_usage":{"input_tokens":40,"output_tokens":8}}}}

                """);
            var persist = Path.Combine(dir, "store.json");
            var store = new ConsumptionStore(persist);
            var files = new[] { new CodexRolloutFile(path, "home", "sess.jsonl", false) };
            var collector = new CodexConsumptionCollector(store, () => files);
            await collector.CollectAsync(AgentKeys.Codex);
            Assert.Equal(40, store.Read(AgentKeys.Codex).Sum(item => item.Tokens.Input));

            File.Delete(path);
            Directory.CreateDirectory(path);
            await collector.CollectAsync(AgentKeys.Codex);
            Assert.Equal(40, store.Read(AgentKeys.Codex).Sum(item => item.Tokens.Input));

            var reloaded = new ConsumptionStore(persist);
            Assert.Equal(40, reloaded.Read(AgentKeys.Codex).Sum(item => item.Tokens.Input));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task ChildRebuild_UsesStoredParentBaseline()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p2-fork-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var parent = Path.Combine(dir, "parent.jsonl");
        var child = Path.Combine(dir, "child.jsonl");
        try
        {
            await File.WriteAllTextAsync(parent, """
                {"type":"session_meta","timestamp":"2026-07-16T10:00:00Z","payload":{"id":"parent-sess"}}
                {"type":"turn_context","timestamp":"2026-07-16T10:00:00Z","payload":{"model":"gpt-5.4"}}
                {"type":"event_msg","timestamp":"2026-07-16T10:00:01Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10},"total_token_usage":{"input_tokens":100,"output_tokens":10}}}}

                """);
            await File.WriteAllTextAsync(child, """
                {"type":"session_meta","timestamp":"2026-07-16T10:00:00Z","payload":{"id":"child-sess","forked_from_id":"parent-sess"}}
                {"type":"turn_context","timestamp":"2026-07-16T10:00:00Z","payload":{"model":"gpt-5.4"}}
                {"type":"event_msg","timestamp":"2026-07-16T10:00:01Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10},"total_token_usage":{"input_tokens":100,"output_tokens":10}}}}
                {"type":"event_msg","timestamp":"2026-07-16T10:00:02Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":30,"cached_input_tokens":0,"output_tokens":6},"total_token_usage":{"input_tokens":130,"output_tokens":16}}}}

                """);
            var store = new ConsumptionStore();
            var all = new[]
            {
                new CodexRolloutFile(parent, "home", "parent.jsonl", false),
                new CodexRolloutFile(child, "home", "child.jsonl", false),
            };
            var collector = new CodexConsumptionCollector(store, () => all);
            await collector.CollectAsync(AgentKeys.Codex);
            Assert.Equal(130, store.Read(AgentKeys.Codex).Sum(item => item.Tokens.Input));
            Assert.Equal(2, store.Read(AgentKeys.Codex).Count);

            File.SetLastWriteTimeUtc(child, DateTime.UtcNow.AddMinutes(1));
            var childOnly = new CodexConsumptionCollector(
                store, () => new[] { new CodexRolloutFile(child, "home", "child.jsonl", false) });
            await childOnly.CollectAsync(AgentKeys.Codex);
            Assert.Equal(2, store.Read(AgentKeys.Codex).Count);
            Assert.Equal(130, store.Read(AgentKeys.Codex).Sum(item => item.Tokens.Input));
            Assert.Equal(16, store.Read(AgentKeys.Codex).Sum(item => item.Tokens.Output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void SnapshotPricer_UnknownModelIsUnpriced()
    {
        var pricer = new SnapshotPricer();
        var unknown = SampleFact("not-a-real-model", official: null);
        var quote = pricer.Price(unknown);
        Assert.Equal(CostKind.Unpriced, quote.Kind);
        Assert.Null(quote.AmountUsd);

        var known = SampleFact("gpt-5.4", official: null);
        var estimated = pricer.Price(known);
        Assert.Equal(CostKind.Estimated, estimated.Kind);
        Assert.True(estimated.AmountUsd > 0);

        var official = pricer.Price(SampleFact("gpt-5.4", official: 1.25));
        Assert.Equal(CostKind.Official, official.Kind);
        Assert.Equal(1.25, official.AmountUsd);
    }

    [Fact]
    public void FactMapping_PreservesReasoningTierAndSource()
    {
        var fact = new ConsumptionFact(
            new SourceRef(AgentKeys.Codex, "rec-9", "sess-9", "proj", "acct", @"C:\a.jsonl", 12),
            DateTimeOffset.Parse("2026-07-16T10:00:00Z"),
            new ModelRef("gpt-5.4", "gpt-5.4", false),
            new TokenBuckets(40, 20, 0, 0, 8, ReasoningAccounting.IncludedInOutput),
            new PricingContext(ServiceTier.Fast, true, DateTimeOffset.Parse("2026-07-16T10:00:00Z"), 1.5));
        var mapped = FactMapping.ToTokenEvent(fact, new SnapshotPricer());
        Assert.Equal(8, mapped.ReasoningTokens);
        Assert.Equal(ReasoningAccounting.IncludedInOutput, mapped.ReasoningAccounting);
        Assert.Equal(ServiceTier.Fast, mapped.ServiceTier);
        Assert.True(mapped.LongContext);
        Assert.Equal("rec-9", mapped.RecordId);
        Assert.Equal("sess-9", mapped.SessionId);
        Assert.Equal("proj", mapped.ProjectId);
        Assert.Equal("acct", mapped.AccountId);
        Assert.Equal(@"C:\a.jsonl", mapped.SourcePath);
        Assert.Equal(60, mapped.BillableTokens);
        Assert.Equal(1.5, mapped.SelfReportedCostUSD);
    }

    [Fact]
    public async Task Reprice_DoesNotRereadLogs()
    {
        var files = Describe(LoadCases().First(item => item.Id == "06-reasoning-in-output"));
        var store = new ConsumptionStore();
        var collector = new CodexConsumptionCollector(store, () => CodexRolloutDiscovery.PreferActive(files));
        await collector.CollectAsync(AgentKeys.Codex);
        var facts = store.Read(AgentKeys.Codex);
        var pricer = new SnapshotPricer();
        var first = facts.Select(fact => pricer.Price(fact)).ToList();
        var second = facts.Select(fact => pricer.Price(fact)).ToList();
        Assert.Equal(first[0].AmountUsd, second[0].AmountUsd);
        Assert.Equal(CostKind.Estimated, first[0].Kind);
    }

    private static async Task<IReadOnlyList<ConsumptionFact>> CollectAsync(IReadOnlyList<CodexRolloutFile> files)
    {
        var store = new ConsumptionStore();
        var collector = new CodexConsumptionCollector(store, () => CodexRolloutDiscovery.PreferActive(files));
        await collector.CollectAsync(AgentKeys.Codex);
        return store.Read(AgentKeys.Codex);
    }

    private static List<CodexRolloutFile> Describe(FixtureCase testCase)
    {
        var files = new List<CodexRolloutFile>();
        foreach (var relative in testCase.Files)
        {
            var archived = relative.Contains("archived_sessions/", StringComparison.Ordinal);
            var name = Path.GetFileName(relative);
            files.Add(new CodexRolloutFile(CodexFile(relative), "home", name, archived));
        }

        return files;
    }

    private static IReadOnlyList<FixtureCase> LoadCases()
    {
        using var stream = File.OpenRead(CodexFile("expected.json"));
        var document = JsonSerializer.Deserialize<FixtureFile>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("expected.json missing.");
        return document.Cases;
    }

    private static string CodexFile(string relative)
    {
        var parts = new List<string> { AppContext.BaseDirectory, "Fixtures", "P0", "Codex" };
        parts.AddRange(relative.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine(parts.ToArray());
    }

    private static ConsumptionFact SampleFact(string model, double? official) => new(
        new SourceRef(AgentKeys.Codex, "rec-1", "sess", null, null, "x.jsonl", 0),
        DateTimeOffset.UnixEpoch,
        new ModelRef(model, Pricing.CanonicalModelName(model), false),
        new TokenBuckets(100, 10, 0, 0, 0, ReasoningAccounting.Absent),
        new PricingContext(ServiceTier.Unspecified, false, DateTimeOffset.UnixEpoch, official));

    private sealed record FixtureFile(string ParserVersion, List<FixtureCase> Cases);
    private sealed record FixtureCase(string Id, List<string> Files, bool Keep, FixtureTotals Current, FixtureTotals Target);
    private sealed record FixtureTotals(int Events, long Input, long Output);

    private sealed class CodexQueryProvider : IAgentProvider, ICostLedgerReader
    {
        private readonly ICostLedgerReader _reader;
        public CodexQueryProvider(ICostLedgerReader reader) => _reader = reader;
        public AgentDescriptor Descriptor { get; } = new(AgentKeys.Codex, "Codex", AgentCapabilities.Cost);
        public ICostLedgerReader? CostLedgerReader => _reader;
        public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
            _reader.ReadCostEventsAsync(lookbackDays, ct);
    }
}
