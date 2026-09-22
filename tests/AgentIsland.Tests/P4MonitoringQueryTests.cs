using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentMonitoring.Accounts;
using AgentMonitoring.Quotas;
using AgentMonitoring.Queries;

namespace AgentIsland.Tests;

public class P4MonitoringQueryTests
{
    [Fact]
    public void ReportAndSummary_ShareTheSameTotals()
    {
        var events = new[]
        {
            Event(AgentKeys.Codex, "2026-07-16T10:00:00Z", "gpt-5.4", 100, 10),
            Event(AgentKeys.Codex, "2026-07-16T12:00:00Z", "gpt-5.4", 50, 5),
            Event(AgentKeys.Claude, "2026-07-16T11:00:00Z", "claude-sonnet-4-5", 80, 20),
        };
        var query = QueryWith(events);
        var start = DateTimeOffset.Parse("2026-07-01T00:00:00Z");
        var end = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var report = query.GetReport(start, end);
        var summary = query.GetConsumptionSummary(start, end);

        Assert.Equal(report.TotalTokens, summary.Tokens);
        Assert.Equal(report.TotalDollars, summary.Dollars);
        Assert.Equal(events.Sum(item => item.WireTokens), summary.Tokens);
        Assert.Equal(2, summary.ByAgent.Count);
        Assert.Equal(summary.ByAgent.Sum(row => row.Tokens), report.Agents.Sum(row => row.Slice.DailyTokens.Sum(day => day.Tokens)));
    }

    [Fact]
    public async Task GetReport_DoesNotStartALedgerRead()
    {
        var reads = 0;
        var reader = new CountingReader(() => Interlocked.Increment(ref reads));
        var snapshots = new LedgerSnapshotStore();
        var scan = new CostQueryService(AlwaysEnabledAgents.Instance, new[] { new FakeCostProvider(reader) }, snapshots);
        await scan.ScanAsync(AgentKeys.Claude, 30, DateTimeOffset.UtcNow);
        Assert.Equal(1, reads);

        var query = new MonitoringQueryService(snapshots);
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var end = DateTimeOffset.Parse("2027-01-01T00:00:00Z");
        _ = query.GetReport(start, end);
        _ = query.GetConsumptionSummary(start, end);
        _ = query.GetOverview(AgentKeys.Claude);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Overview_KeepsQuotaAndConsumptionTimesSeparate()
    {
        var snapshots = new LedgerSnapshotStore();
        snapshots.Replace(new LedgerSnapshot(
            AgentKeys.Codex, 1, DateTimeOffset.Parse("2026-07-16T10:00:00Z"),
            new[] { Event(AgentKeys.Codex, "2026-07-16T09:00:00Z", "gpt-5.4", 10, 1) },
            ProviderCostSummary.Empty));
        var quotas = new QuotaStore();
        var account = new AccountRef(AgentKeys.Codex, "alice");
        quotas.Commit(new QuotaSnapshot(
            account,
            new AppUsage(new WindowUsage(0.4, null, null), WindowUsage.Unknown, "pro"),
            DateTimeOffset.Parse("2026-07-16T11:00:00Z"),
            DateTimeOffset.Parse("2026-07-16T11:00:00Z"),
            null));
        var directory = new MemoryAccountDirectory();
        directory.Remember(account);
        var query = new MonitoringQueryService(snapshots, quotas: quotas, accounts: directory);
        var overview = query.GetOverview(AgentKeys.Codex);
        Assert.Equal(DateTimeOffset.Parse("2026-07-16T10:00:00Z"), overview.ConsumptionAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-16T11:00:00Z"), overview.QuotaAt);
        Assert.Equal("alice", overview.Account.AccountId);
        Assert.Equal(0.4, overview.Quota!.Usage.FiveHour.UsedPercent);
    }

    [Fact]
    public void GetOverviews_IncludesQuotaBalanceAndActivityAgents()
    {
        var ledgers = new LedgerSnapshotStore();
        var quotas = new QuotaStore();
        quotas.Commit(new QuotaSnapshot(
            new AccountRef(AgentKeys.Claude, "c"),
            new AppUsage(new WindowUsage(0.1, null, null), WindowUsage.Unknown),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
        var balances = new AgentMonitoring.Balances.BalanceStore();
        balances.Commit(new AgentIsland.Core.Usage.RemoteBalanceSnapshot(
            new AccountRef(AgentKeys.DeepSeek, null), "CNY", 3, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, null, true));
        var activity = new AgentMonitoring.Activity.ActivitySnapshotStore();
        activity.Replace(new AgentMonitoring.Activity.AgentActivity(
            new AgentKey("nova"), AgentIsland.Core.ActivityState.Working, null,
            Array.Empty<AgentMonitoring.Activity.ActivityThread>(), DateTimeOffset.UtcNow));
        var query = new MonitoringQueryService(
            ledgers, quotas: quotas, balances: balances, activity: activity);
        var overviews = query.GetOverviews();
        Assert.Contains(overviews, item => item.Agent.Value == AgentKeys.Claude.Value);
        Assert.Contains(overviews, item => item.Agent.Value == AgentKeys.DeepSeek.Value);
        Assert.Contains(overviews, item => item.Agent.Value == "nova");
    }

    private static MonitoringQueryService QueryWith(IReadOnlyList<TokenEvent> events)
    {
        var snapshots = new LedgerSnapshotStore();
        foreach (var group in events.GroupBy(item => item.Provider.RawValue()))
        {
            var agent = new AgentKey(group.Key);
            var list = group.ToList();
            snapshots.Replace(new LedgerSnapshot(
                agent, 1, DateTimeOffset.UtcNow, list, CostSummarizer.Summarize(list, DateTimeOffset.UtcNow)));
        }

        return new MonitoringQueryService(snapshots);
    }

    private static TokenEvent Event(AgentKey agent, string timestamp, string model, long input, long output) =>
        new(TriggerToolExtensions.FromRawValue(agent.Value) ?? TriggerTool.Codex,
            DateTimeOffset.Parse(timestamp), model, input, output, 0, 0);

    private sealed class CountingReader : ICostLedgerReader
    {
        private readonly Action _onRead;
        public CountingReader(Action onRead) => _onRead = onRead;
        public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default)
        {
            _onRead();
            return ValueTask.FromResult<IReadOnlyList<TokenEvent>>(new[]
            {
                Event(AgentKeys.Claude, "2026-07-16T08:00:00Z", "claude-sonnet-4-5", 20, 4),
            });
        }
    }

    private sealed class FakeCostProvider : IAgentProvider, ICostLedgerReader
    {
        private readonly ICostLedgerReader _reader;
        public FakeCostProvider(ICostLedgerReader reader) => _reader = reader;
        public AgentDescriptor Descriptor { get; } = new(AgentKeys.Claude, "Claude", AgentCapabilities.Cost);
        public ICostLedgerReader? CostLedgerReader => _reader;
        public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
            _reader.ReadCostEventsAsync(lookbackDays, ct);
    }
}
