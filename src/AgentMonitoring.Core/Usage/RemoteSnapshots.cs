using AgentIsland.Core.Agents;

namespace AgentIsland.Core.Usage;

/// Last known official quota for one agent account. Failed fetches keep
/// Usage from the previous success and set Error.
public sealed record QuotaSnapshot(
    AccountRef Account,
    AppUsage Usage,
    DateTimeOffset FetchedAt,
    DateTimeOffset? SucceededAt,
    string? Error);

/// Last known official currency balance for one agent account.
public sealed record RemoteBalanceSnapshot(
    AccountRef Account,
    string Currency,
    double Amount,
    DateTimeOffset FetchedAt,
    DateTimeOffset? SucceededAt,
    string? Error,
    bool IsAvailable);
