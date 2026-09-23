using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentMonitoring.Accounts;
using AgentMonitoring.Balances;
using AgentMonitoring.Consumption;
using AgentMonitoring.Pricing;
using AgentMonitoring.Activity;
using AgentMonitoring.Quotas;

namespace AgentMonitoring.Queries;

public sealed class MonitoringQueryService : IMonitoringQuery
{
    private readonly ILedgerSnapshotStore _ledgers;
    private readonly IConsumptionQuery? _consumption;
    private readonly IPricer? _pricer;
    private readonly IQuotaStore? _quotas;
    private readonly IBalanceStore? _balances;
    private readonly IAccountDirectory? _accounts;
    private readonly IActivitySnapshotStore? _activity;

    public MonitoringQueryService(
        ILedgerSnapshotStore ledgers,
        IConsumptionQuery? consumption = null,
        IPricer? pricer = null,
        IQuotaStore? quotas = null,
        IBalanceStore? balances = null,
        IAccountDirectory? accounts = null,
        IActivitySnapshotStore? activity = null)
    {
        _ledgers = ledgers ?? throw new ArgumentNullException(nameof(ledgers));
        _consumption = consumption;
        _pricer = pricer;
        _quotas = quotas;
        _balances = balances;
        _accounts = accounts;
        _activity = activity;
    }

    public AgentOverview GetOverview(AgentKey agent)
    {
        var account = _accounts?.Current(agent) ?? new AccountRef(agent, null);
        var ledger = Events(agent, out var consumptionAt);
        var todayStart = new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset);
        long todayTokens = 0;
        double todayDollars = 0;
        foreach (var tokenEvent in ledger)
        {
            if (tokenEvent.Timestamp.ToLocalTime() < todayStart) continue;
            todayTokens += tokenEvent.WireTokens;
            todayDollars += tokenEvent.Dollars;
        }

        var quota = _quotas?.Read(account);
        var balance = _balances?.Read(account);
        var activity = _activity?.Read(agent);
        return new AgentOverview(
            agent,
            account,
            todayTokens,
            todayDollars,
            quota,
            balance,
            consumptionAt,
            quota?.FetchedAt,
            balance?.FetchedAt,
            activity?.State ?? ActivityState.Idle,
            activity?.UpdatedAt);
    }

    public IReadOnlyList<AgentOverview> GetOverviews() =>
        KnownAgents().Select(GetOverview).ToList();

    public ConsumptionSummary GetConsumptionSummary(
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<AgentKey>? agents = null)
    {
        var keys = agents ?? KnownAgents();
        var eventSnapshots = CaptureEvents(keys);
        var report = BuildReport(start, end, keys, eventSnapshots);
        var byAgent = new List<AgentSpendRow>();
        var models = new Dictionary<string, (long Tokens, long Billable, double Dollars)>(StringComparer.OrdinalIgnoreCase);
        var daily = new Dictionary<DateTime, (long Tokens, long Billable, double Dollars)>();
        long tokens = 0, billable = 0;
        var unpriced = false;

        foreach (var row in report.Agents)
        {
            long agentTokens = 0, agentBillable = 0;
            var agentUnpriced = false;
            foreach (var tokenEvent in eventSnapshots[row.Agent])
            {
                var local = tokenEvent.Timestamp.ToLocalTime();
                if (local < start.ToLocalTime() || local >= end.ToLocalTime()) continue;
                agentTokens += tokenEvent.WireTokens;
                agentBillable += tokenEvent.BillableTokens;
                tokens += tokenEvent.WireTokens;
                billable += tokenEvent.BillableTokens;
                if (!AgentIsland.Core.Cost.Pricing.IsKnown(tokenEvent.Model) && tokenEvent.SelfReportedCostUSD is null)
                    agentUnpriced = true;
                var model = AgentIsland.Core.Cost.Pricing.CanonicalModelName(tokenEvent.Model);
                models.TryGetValue(model, out var entry);
                models[model] = (entry.Tokens + tokenEvent.WireTokens, entry.Billable + tokenEvent.BillableTokens,
                    entry.Dollars + tokenEvent.Dollars);
                var day = local.Date;
                daily.TryGetValue(day, out var bucket);
                daily[day] = (bucket.Tokens + tokenEvent.WireTokens, bucket.Billable + tokenEvent.BillableTokens,
                    bucket.Dollars + tokenEvent.Dollars);
            }

            unpriced |= agentUnpriced;
            byAgent.Add(new AgentSpendRow(row.Agent, agentTokens, agentBillable, row.Slice.Dollars, agentUnpriced));
        }

        var startDay = start.ToLocalTime().Date;
        var lastDay = end.ToLocalTime().AddSeconds(-1).Date;
        var dayCount = Math.Max(1, (int)(lastDay - startDay).TotalDays + 1);
        var dailyList = new List<DailyTokenBucket>(dayCount);
        for (var i = 0; i < dayCount; i++)
        {
            var day = startDay.AddDays(i);
            daily.TryGetValue(day, out var bucket);
            dailyList.Add(new DailyTokenBucket(
                new DateTimeOffset(day, DateTimeOffset.Now.Offset),
                bucket.Tokens, bucket.Billable, bucket.Dollars));
        }

        return new ConsumptionSummary(
            start,
            end,
            tokens,
            billable,
            report.TotalDollars,
            unpriced,
            byAgent,
            models.Select(kv => new ModelSpend(kv.Key, kv.Value.Tokens, kv.Value.Billable, kv.Value.Dollars))
                .OrderByDescending(spend => spend.BillableTokens)
                .ToList(),
            dailyList);
    }

    public ReportQueryResult GetReport(
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<AgentKey>? agents = null)
    {
        var keys = agents ?? KnownAgents();
        return BuildReport(start, end, keys, CaptureEvents(keys));
    }

    private static ReportQueryResult BuildReport(
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<AgentKey> keys,
        IReadOnlyDictionary<AgentKey, IReadOnlyList<TokenEvent>> eventSnapshots)
    {
        var slices = new List<AgentReportSlice>();
        long totalTokens = 0;
        double totalDollars = 0;
        foreach (var agent in keys)
        {
            var events = eventSnapshots[agent];
            var slice = CostSummarizer.Slice(events, start, end);
            slices.Add(new AgentReportSlice(agent, slice));
            totalTokens += slice.DailyTokens.Sum(bucket => bucket.Tokens);
            totalDollars += slice.Dollars;
        }

        return new ReportQueryResult(start, end, slices, totalTokens, totalDollars);
    }

    private Dictionary<AgentKey, IReadOnlyList<TokenEvent>> CaptureEvents(IReadOnlyList<AgentKey> agents)
    {
        var snapshots = new Dictionary<AgentKey, IReadOnlyList<TokenEvent>>();
        foreach (var agent in agents)
        {
            if (!snapshots.ContainsKey(agent))
                snapshots[agent] = Events(agent, out _);
        }
        return snapshots;
    }

    private IReadOnlyList<AgentKey> KnownAgents()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in _ledgers.ReadAll())
            keys.Add(snapshot.Agent.Value);
        if (_consumption is not null)
            keys.Add(AgentKeys.Codex.Value);
        if (_quotas is not null)
        {
            foreach (var snapshot in _quotas.ReadAll())
                keys.Add(snapshot.Account.Agent.Value);
        }
        if (_balances is not null)
        {
            foreach (var snapshot in _balances.ReadAll())
                keys.Add(snapshot.Account.Agent.Value);
        }
        if (_activity is not null)
        {
            foreach (var snapshot in _activity.ReadAll())
                keys.Add(snapshot.Agent.Value);
        }
        return keys.Select(value => new AgentKey(value)).ToList();
    }

    private IReadOnlyList<TokenEvent> Events(AgentKey agent, out DateTimeOffset? scannedAt)
    {
        var snapshot = _ledgers.Read(agent);
        if (snapshot is not null)
        {
            scannedAt = snapshot.ScannedAt;
            return snapshot.Events;
        }

        scannedAt = null;
        if (_consumption is null || agent.Value != AgentKeys.Codex.Value)
            return Array.Empty<TokenEvent>();
        var pricer = _pricer ?? new SnapshotPricer();
        return _consumption.Read(agent).Select(fact => FactMapping.ToTokenEvent(fact, pricer)).ToList();
    }
}
