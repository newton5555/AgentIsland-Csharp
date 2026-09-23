using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentMonitoring.Activity;

namespace AgentMonitoring.Queries;

public sealed record AgentSpendRow(
    AgentKey Agent,
    long Tokens,
    long BillableTokens,
    double Dollars,
    bool HasUnpriced);

public sealed record ConsumptionSummary(
    DateTimeOffset Start,
    DateTimeOffset End,
    long Tokens,
    long BillableTokens,
    double Dollars,
    bool HasUnpriced,
    IReadOnlyList<AgentSpendRow> ByAgent,
    IReadOnlyList<ModelSpend> ByModel,
    IReadOnlyList<DailyTokenBucket> Daily);

public sealed record AgentReportSlice(AgentKey Agent, ReportSlice Slice);

public sealed record ReportQueryResult(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<AgentReportSlice> Agents,
    long TotalTokens,
    double TotalDollars);

public sealed record AgentOverview(
    AgentKey Agent,
    AccountRef Account,
    long TodayTokens,
    double TodayDollars,
    QuotaSnapshot? Quota,
    RemoteBalanceSnapshot? Balance,
    DateTimeOffset? ConsumptionAt,
    DateTimeOffset? QuotaAt,
    DateTimeOffset? BalanceAt,
    ActivityState Activity = ActivityState.Idle,
    DateTimeOffset? ActivityAt = null,
    ActivityThread? CurrentActivity = null,
    int NeedsYouCount = 0);

/// Read-only composition of collected ledgers, quotas, and balances.
/// Does not start file scans or network refresh.
public interface IMonitoringQuery
{
    AgentOverview GetOverview(AgentKey agent);
    IReadOnlyList<AgentOverview> GetOverviews();
    ConsumptionSummary GetConsumptionSummary(
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<AgentKey>? agents = null);
    ReportQueryResult GetReport(
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<AgentKey>? agents = null);
}
