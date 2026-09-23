using AgentIsland.Core;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentMonitoring.Activity;
using AgentMonitoring.Queries;

namespace AgentMonitoring.Host;

/// Stable, transport-specific v1 response shapes. Keep AgentKey out of the
/// wire contract so JSON clients receive plain strings rather than CLR types.
public sealed record AgentOverviewApiV1(
    string AgentKey,
    AgentAccountApiV1 Account,
    AgentConsumptionApiV1 Consumption,
    AgentActivityApiV1 Activity,
    AgentQuotaApiV1? Quota,
    AgentBalanceApiV1? Balance);

public sealed record AgentAccountApiV1(string? AccountId, string? Label);

public sealed record AgentConsumptionApiV1(
    long TodayTokens,
    double TodayDollars,
    DateTimeOffset? UpdatedAt);

public sealed record AgentActivityApiV1(
    string State,
    bool Available,
    DateTimeOffset? UpdatedAt,
    AgentActivityThreadApiV1? CurrentThread,
    int NeedsYouCount);

public sealed record AgentActivityThreadApiV1(
    string SessionId,
    string Label,
    string WorkingDirectory,
    DateTimeOffset Modified,
    string? TurnKey);

public sealed record AgentQuotaApiV1(
    DateTimeOffset FetchedAt,
    DateTimeOffset? SucceededAt,
    string? Error,
    string? Plan,
    AgentQuotaWindowApiV1 Primary,
    AgentQuotaWindowApiV1 Secondary,
    int? ResetCards,
    IReadOnlyList<ResetCard>? ResetCardDetails);

public sealed record AgentQuotaWindowApiV1(
    double UsedPercent,
    DateTimeOffset? ResetAt,
    string? Error,
    double? PeriodSeconds);

public sealed record AgentBalanceApiV1(
    string Currency,
    double Amount,
    bool IsAvailable,
    DateTimeOffset FetchedAt,
    DateTimeOffset? SucceededAt,
    string? Error);

public sealed record AgentSpendApiV1(
    string AgentKey,
    long Tokens,
    long BillableTokens,
    double Dollars,
    bool HasUnpriced);

public sealed record ConsumptionSummaryApiV1(
    DateTimeOffset Start,
    DateTimeOffset End,
    long Tokens,
    long BillableTokens,
    double Dollars,
    bool HasUnpriced,
    IReadOnlyList<AgentSpendApiV1> ByAgent,
    IReadOnlyList<ModelSpend> ByModel,
    IReadOnlyList<DailyTokenBucket> Daily);

public sealed record AgentReportApiV1(string AgentKey, ReportSlice Slice);

public sealed record ReportQueryApiV1(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<AgentReportApiV1> Agents,
    long TotalTokens,
    double TotalDollars);

public static class MonitoringApiMapper
{
    public static AgentOverviewApiV1 ToApiV1(AgentOverview overview) => new(
        overview.Agent.Value,
        new AgentAccountApiV1(overview.Account.AccountId, overview.Account.Label),
        new AgentConsumptionApiV1(overview.TodayTokens, overview.TodayDollars, overview.ConsumptionAt),
        new AgentActivityApiV1(
            overview.ActivityAt is null ? "unknown" : StateName(overview.Activity),
            overview.ActivityAt is not null,
            overview.ActivityAt,
            overview.CurrentActivity is null ? null : new AgentActivityThreadApiV1(
                overview.CurrentActivity.SessionId,
                overview.CurrentActivity.Label,
                overview.CurrentActivity.Cwd,
                overview.CurrentActivity.Modified,
                overview.CurrentActivity.TurnKey),
            overview.NeedsYouCount),
        overview.Quota is null ? null : new AgentQuotaApiV1(
            overview.Quota.FetchedAt,
            overview.Quota.SucceededAt,
            overview.Quota.Error,
            overview.Quota.Usage.Plan,
            ToApiV1(overview.Quota.Usage.FiveHour),
            ToApiV1(overview.Quota.Usage.Weekly),
            overview.Quota.Usage.ResetCards,
            overview.Quota.Usage.ResetCardDetails),
        overview.Balance is null ? null : new AgentBalanceApiV1(
            overview.Balance.Currency,
            overview.Balance.Amount,
            overview.Balance.IsAvailable,
            overview.Balance.FetchedAt,
            overview.Balance.SucceededAt,
            overview.Balance.Error));

    public static ConsumptionSummaryApiV1 ToApiV1(ConsumptionSummary summary) => new(
        summary.Start,
        summary.End,
        summary.Tokens,
        summary.BillableTokens,
        summary.Dollars,
        summary.HasUnpriced,
        summary.ByAgent.Select(row => new AgentSpendApiV1(
            row.Agent.Value, row.Tokens, row.BillableTokens, row.Dollars, row.HasUnpriced)).ToList(),
        summary.ByModel,
        summary.Daily);

    public static ReportQueryApiV1 ToApiV1(ReportQueryResult report) => new(
        report.Start,
        report.End,
        report.Agents.Select(row => new AgentReportApiV1(row.Agent.Value, row.Slice)).ToList(),
        report.TotalTokens,
        report.TotalDollars);

    private static AgentQuotaWindowApiV1 ToApiV1(WindowUsage usage) => new(
        usage.UsedPercent, usage.ResetAt, usage.Error, usage.PeriodSeconds);

    private static string StateName(ActivityState state) => state switch
    {
        ActivityState.Idle => "idle",
        ActivityState.Working => "working",
        ActivityState.NeedsYou => "needsYou",
        ActivityState.Stalled => "stalled",
        ActivityState.RateLimited => "rateLimited",
        ActivityState.AuthRequired => "authRequired",
        _ => "unknown",
    };
}
