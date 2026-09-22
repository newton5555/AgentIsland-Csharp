using AgentIsland.Core.Agents;

namespace AgentIsland.Core.Cost;

/// How output-side reasoning tokens relate to <see cref="TokenBuckets.Output"/>.
/// Parsers must set this explicitly; aggregators never add Reasoning on top of
/// Output when the value is <see cref="IncludedInOutput"/>.
public enum ReasoningAccounting
{
    /// The source does not emit a reasoning count.
    Absent = 0,

    /// Reasoning is already inside Output (Codex, Claude, typical chat APIs).
    IncludedInOutput = 1,

    /// Reasoning is a separate bucket that is not part of Output.
    Separate = 2,
}

public enum ServiceTier
{
    Unspecified = 0,
    Standard = 1,
    Fast = 2,
    Priority = 3,
}

/// Token buckets for one billable model call. Wire and billable totals are
/// derived so Pricing and reports cannot silently double-count reasoning.
public sealed record TokenBuckets(
    long Input,
    long Output,
    long CacheCreation,
    long CacheRead,
    long Reasoning,
    ReasoningAccounting ReasoningAccounting)
{
    public long WireTokens
    {
        get
        {
            var core = Input + Output + CacheCreation + CacheRead;
            return ReasoningAccounting == ReasoningAccounting.Separate ? core + Reasoning : core;
        }
    }

    public long BillableTokens =>
        ReasoningAccounting == ReasoningAccounting.Separate
            ? Input + Output + Reasoning
            : Input + Output;
}

/// Raw log model plus the name Pricing looks up. IsFallback is true when the
/// parser substituted a default because the log had no model.
public sealed record ModelRef(string Raw, string Canonical, bool IsFallback);

/// Identity needed to dedupe, attribute, and resume a scan. AccountId is null
/// when the log cannot identify an account — callers must not fill it with the
/// currently signed-in user.
public sealed record SourceRef(
    AgentKey Agent,
    string RecordId,
    string? SessionId,
    string? ProjectId,
    string? AccountId,
    string? SourcePath,
    long? ByteOffset);

/// Fields Pricing needs that are not token counts. OfficialCostUsd is a
/// provider-reported amount, not a table estimate.
public sealed record PricingContext(
    ServiceTier ServiceTier,
    bool LongContext,
    DateTimeOffset EventTime,
    double? OfficialCostUsd);

/// Durable consumption fact. P0 fixes this shape; P2 persists it. Do not treat
/// <see cref="TokenEvent"/> as the storage model.
public sealed record ConsumptionFact(
    SourceRef Source,
    DateTimeOffset Timestamp,
    ModelRef Model,
    TokenBuckets Tokens,
    PricingContext Pricing);
