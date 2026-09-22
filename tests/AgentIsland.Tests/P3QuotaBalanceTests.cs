using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentMonitoring.Accounts;
using AgentMonitoring.Balances;
using AgentMonitoring.Consumption;
using AgentMonitoring.Quotas;

namespace AgentIsland.Tests;

public class P3QuotaBalanceTests
{
    [Fact]
    public void QuotaMerge_FailedFetchKeepsLastSuccess()
    {
        var good = new AppUsage(new WindowUsage(0.4, null, null), new WindowUsage(0.2, null, null), "pro");
        var failed = AppUsage.ErrorPair("network drop");
        var merged = QuotaMerge.Apply(good, failed);
        Assert.Equal(0.4, merged.FiveHour.UsedPercent);
        Assert.Equal("network drop", merged.FiveHour.Error);
        Assert.Equal("pro", merged.Plan);
    }

    [Fact]
    public void QuotaMerge_PartialWindowFailureKeepsTheOtherWindow()
    {
        var good = new AppUsage(new WindowUsage(0.4, null, null), new WindowUsage(0.2, null, null), "pro");
        var partial = new AppUsage(
            new WindowUsage(0.55, null, null),
            new WindowUsage(0, null, "weekly down"),
            "pro");
        var merged = QuotaMerge.Apply(good, partial);
        Assert.Equal(0.55, merged.FiveHour.UsedPercent);
        Assert.Null(merged.FiveHour.Error);
        Assert.Equal(0.2, merged.Weekly.UsedPercent);
        Assert.Equal("weekly down", merged.Weekly.Error);
        Assert.Equal("pro", merged.Plan);
    }

    [Fact]
    public async Task QuotaRefresher_PartialWindowFailureKeepsSucceededAt()
    {
        var account = new AccountRef(AgentKeys.Codex, "alice");
        var source = new ScriptedQuotaSource(AgentKeys.Codex);
        source.Enqueue(new AppUsage(new WindowUsage(0.4, null, null), new WindowUsage(0.2, null, null), "pro"));
        source.Enqueue(new AppUsage(
            new WindowUsage(0.5, null, null),
            new WindowUsage(0, null, "weekly down"),
            "pro"));
        var store = new QuotaStore();
        var refresher = new QuotaRefresher(store, new[] { source });
        await refresher.RefreshAsync(account);
        var first = store.Read(account)!;
        await refresher.RefreshAsync(account);
        var second = store.Read(account)!;
        Assert.Equal(0.5, second.Usage.FiveHour.UsedPercent);
        Assert.Equal(0.2, second.Usage.Weekly.UsedPercent);
        Assert.Equal("weekly down", second.Usage.Weekly.Error);
        Assert.Null(second.Error);
        Assert.NotNull(second.SucceededAt);
        Assert.True(second.SucceededAt >= first.SucceededAt);
    }

    [Fact]
    public async Task QuotaRefresher_IsolatesAccounts()
    {
        var alice = new AccountRef(AgentKeys.Codex, "alice");
        var bob = new AccountRef(AgentKeys.Codex, "bob");
        var source = new ScriptedQuotaSource(AgentKeys.Codex);
        source.Enqueue(new AppUsage(new WindowUsage(0.1, null, null), WindowUsage.Unknown, "pro"));
        source.Enqueue(new AppUsage(new WindowUsage(0.9, null, null), WindowUsage.Unknown, "plus"));

        var store = new QuotaStore();
        var refresher = new QuotaRefresher(store, new[] { source });
        await refresher.RefreshAsync(alice);
        await refresher.RefreshAsync(bob);

        Assert.Equal(0.1, store.Read(alice)!.Usage.FiveHour.UsedPercent);
        Assert.Equal("pro", store.Read(alice)!.Usage.Plan);
        Assert.Equal(0.9, store.Read(bob)!.Usage.FiveHour.UsedPercent);
        Assert.Equal("plus", store.Read(bob)!.Usage.Plan);
    }

    [Fact]
    public async Task QuotaRefresher_FailureKeepsSuccessForThatAccount()
    {
        var alice = new AccountRef(AgentKeys.Codex, "alice");
        var source = new ScriptedQuotaSource(AgentKeys.Codex);
        source.Enqueue(new AppUsage(new WindowUsage(0.33, null, null), WindowUsage.Unknown, "pro"));
        source.Enqueue(AppUsage.ErrorPair("offline"));

        var store = new QuotaStore();
        var refresher = new QuotaRefresher(store, new[] { source });
        await refresher.RefreshAsync(alice);
        await refresher.RefreshAsync(alice);

        var snapshot = store.Read(alice)!;
        Assert.Equal(0.33, snapshot.Usage.FiveHour.UsedPercent);
        Assert.Equal("offline", snapshot.Error);
        Assert.NotNull(snapshot.SucceededAt);
    }

    [Fact]
    public async Task BalanceRefresher_FailureKeepsSuccess()
    {
        var account = new AccountRef(AgentKeys.DeepSeek, "key-1");
        var source = new ScriptedBalanceSource();
        source.Enqueue(new BalanceFetchResult.Success(new AccountBalance("CNY", 12.5, DateTimeOffset.UtcNow), true));
        source.Enqueue(new BalanceFetchResult.Failed("network drop"));

        var store = new BalanceStore();
        var refresher = new BalanceRefresher(store, new[] { source });
        await refresher.RefreshAsync(account);
        await refresher.RefreshAsync(account);

        var snapshot = store.Read(account)!;
        Assert.Equal(12.5, snapshot.Amount);
        Assert.Equal("CNY", snapshot.Currency);
        Assert.Equal("network drop", snapshot.Error);
        Assert.NotNull(snapshot.SucceededAt);
    }

    [Fact]
    public async Task QuotaFailure_DoesNotTouchConsumptionStore()
    {
        var account = new AccountRef(AgentKeys.Codex, "alice");
        var consumption = new ConsumptionStore();
        consumption.Commit(
            AgentKeys.Codex,
            new[]
            {
                new ConsumptionFact(
                    new SourceRef(AgentKeys.Codex, "r1", "s", null, "alice", "a.jsonl", 0),
                    DateTimeOffset.UnixEpoch,
                    new ModelRef("gpt-5.4", "gpt-5.4", false),
                    new TokenBuckets(10, 1, 0, 0, 0, ReasoningAccounting.Absent),
                    new PricingContext(ServiceTier.Unspecified, false, DateTimeOffset.UnixEpoch, null)),
            },
            new Dictionary<string, CodexFileState>());

        var source = new ScriptedQuotaSource(AgentKeys.Codex);
        source.Enqueue(AppUsage.ErrorPair("offline"));
        var quota = new QuotaStore();
        var refresher = new QuotaRefresher(quota, new[] { source });
        await refresher.RefreshAsync(account);

        Assert.Single(consumption.Read(AgentKeys.Codex));
        Assert.Equal(10, consumption.Read(AgentKeys.Codex)[0].Tokens.Input);
    }

    [Fact]
    public void DeepSeekProvider_DeclaresBalance()
    {
        IAgentProvider provider = new AgentIsland.Backend.Providers.DeepSeekAgentProvider();
        Assert.True(provider.Descriptor.Supports(AgentCapabilities.Balance));
        Assert.NotNull(provider.BalanceFetcher);
    }

    [Fact]
    public async Task QuotaSource_ParkedAccountDoesNotUseLiveCredentials()
    {
        var directory = new MemoryAccountDirectory();
        var live = new AccountRef(AgentKeys.Codex, "alice");
        var parked = new AccountRef(AgentKeys.Codex, "bob");
        directory.Remember(live);
        var fetches = 0;
        var source = new UsageFetcherQuotaSource(
            AgentKeys.Codex,
            new CountingFetcher(() =>
            {
                Interlocked.Increment(ref fetches);
                return new AppUsage(new WindowUsage(0.77, null, null), WindowUsage.Unknown, "pro");
            }),
            directory);

        var store = new QuotaStore();
        store.Commit(new QuotaSnapshot(
            parked,
            new AppUsage(new WindowUsage(0.12, null, null), WindowUsage.Unknown, "plus"),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null));
        var refresher = new QuotaRefresher(store, new[] { source });
        await refresher.RefreshAsync(parked);
        await refresher.RefreshAsync(live);

        Assert.Equal(1, fetches);
        Assert.Equal(0.12, store.Read(parked)!.Usage.FiveHour.UsedPercent);
        Assert.Equal("account credentials not available", store.Read(parked)!.Error);
        Assert.Equal(0.77, store.Read(live)!.Usage.FiveHour.UsedPercent);
    }

    [Fact]
    public void MemoryAccountDirectory_SwitchDoesNotRewriteOtherCurrent()
    {
        var directory = new MemoryAccountDirectory();
        var alice = new AccountRef(AgentKeys.Codex, "alice");
        var bob = new AccountRef(AgentKeys.Codex, "bob");
        directory.Remember(alice);
        directory.Remember(bob);
        Assert.Equal("bob", directory.Current(AgentKeys.Codex).AccountId);
        directory.Remember(alice, makeCurrent: true);
        Assert.Equal("alice", directory.Current(AgentKeys.Codex).AccountId);
        Assert.Equal(2, directory.List(AgentKeys.Codex).Count);
    }

    private sealed class ScriptedQuotaSource : IQuotaSource
    {
        private readonly Queue<AppUsage> _queue = new();
        public ScriptedQuotaSource(AgentKey agent) => Agent = agent;
        public AgentKey Agent { get; }
        public void Enqueue(AppUsage usage) => _queue.Enqueue(usage);
        public Task<AppUsage> FetchAsync(AccountRef account, CancellationToken cancellationToken = default) =>
            Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : AppUsage.ErrorPair("empty"));
    }

    private sealed class ScriptedBalanceSource : IBalanceSource
    {
        private readonly Queue<BalanceFetchResult> _queue = new();
        public AgentKey Agent => AgentKeys.DeepSeek;
        public void Enqueue(BalanceFetchResult result) => _queue.Enqueue(result);
        public Task<BalanceFetchResult> FetchAsync(AccountRef account, CancellationToken cancellationToken = default) =>
            Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : new BalanceFetchResult.Failed("empty"));
    }

    private sealed class CountingFetcher : IUsageFetcher
    {
        private readonly Func<AppUsage> _fetch;
        public CountingFetcher(Func<AppUsage> fetch) => _fetch = fetch;
        public ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(_fetch());
    }
}
