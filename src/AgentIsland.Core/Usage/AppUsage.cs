namespace AgentIsland.Usage;

/// One rate-limit window as rendered in the UI. UsedPercent is normalized to
/// [0, 1]; Error carries the caption shown in place of a value.
/// PeriodSeconds is the window length as reported by the provider (Codex
/// sends limit_window_seconds; Claude's windows are the documented 5h/7d).
/// It drives display labels, so when a provider changes its quota model,
/// the tile relabels itself instead of lying under a hardcoded label. null on
/// old cached snapshots; labels fall back to the slot name.
public sealed record WindowUsage(
    double UsedPercent, DateTimeOffset? ResetAt, string? Error, double? PeriodSeconds = null)
{
    public static WindowUsage Unknown { get; } = new(0, null, "no data");

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// A multi-day window (weekly-style) as opposed to an intra-day one.
    public bool IsLongPeriod => (PeriodSeconds ?? 0) >= 2 * 86400;
}

/// A banked Codex rate-limit reset ("reset card") — OpenAI's own title
/// ("Full reset") plus when it stops being usable.
public sealed record ResetCard(string Id, string Title, DateTimeOffset? ExpiresAt);

/// Usage for one provider: the two window slots plus the plan tier ("max",
/// "pro", ...) surfaced as the chip badge next to the provider name.
/// ResetCards carries Codex's banked rate-limit resets (the count OpenAI
/// reports in rate_limit_reset_credits.available_count); ResetCardDetails
/// the per-card title/expiry from wham/rate-limit-reset-credits. null =
/// provider doesn't report any (Claude, old caches).
public sealed record AppUsage(
    WindowUsage FiveHour,
    WindowUsage Weekly,
    string? Plan = null,
    int? ResetCards = null,
    IReadOnlyList<ResetCard>? ResetCardDetails = null)
{
    public static AppUsage Empty { get; } = new(
        new WindowUsage(0, null, null),
        new WindowUsage(0, null, null));

    public static AppUsage ErrorPair(string message) => new(
        new WindowUsage(0, null, message),
        new WindowUsage(0, null, message));

    /// True when this fetch produced nothing usable — both windows errored
    /// and no percentage survived. Used to keep showing the last good value.
    public bool IsErrorOnly =>
        FiveHour.HasError && Weekly.HasError && FiveHour.UsedPercent == 0 && Weekly.UsedPercent == 0;

    /// True when the provider reported only ONE window on a healthy fetch —
    /// Codex's primary-only shape (a single weekly quota, secondary_window
    /// gone). The UI hides the empty tile instead of pinning "no data".
    /// Cold start (both unknown) and fetch failures don't qualify, because
    /// the primary slot carries an error too.
    public bool SecondaryMissing => Weekly.Error == "no data" && FiveHour.Error is null;
}
