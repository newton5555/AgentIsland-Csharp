namespace AgentIsland.Usage;

public sealed class UsageCacheSnapshot
{
    public AppUsage Claude { get; init; } = AppUsage.Empty;
    public AppUsage Codex { get; init; } = AppUsage.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ClaudeUpdatedAt { get; init; }
    public DateTimeOffset? CodexUpdatedAt { get; init; }

    public UsageCacheSnapshot()
    {
    }

    public UsageCacheSnapshot(
        AppUsage claude,
        AppUsage codex,
        DateTimeOffset updatedAt,
        DateTimeOffset? claudeUpdatedAt = null,
        DateTimeOffset? codexUpdatedAt = null)
    {
        Claude = claude;
        Codex = codex;
        UpdatedAt = updatedAt;
        ClaudeUpdatedAt = claudeUpdatedAt ?? updatedAt;
        CodexUpdatedAt = codexUpdatedAt ?? updatedAt;
    }
}

/// Cache rules for the last successful usage fetch. Direct port of the macOS
/// UsageCachePolicy: only fresh, error-free, value-bearing usage is cached;
/// a failed or unfetched provider keeps its prior snapshot and timestamp; a
/// snapshot restores only while young enough per provider.
public static class UsageCachePolicy
{
    public static UsageCacheSnapshot? SnapshotForSave(
        AppUsage claude,
        AppUsage codex,
        UsageCacheSnapshot? existing,
        DateTimeOffset now,
        bool fetchedClaude = true,
        bool fetchedCodex = true)
    {
        var claudeCandidate = ProviderForSave(
            claude, fetchedClaude, existing?.Claude, existing?.ClaudeUpdatedAt ?? existing?.UpdatedAt, now);
        var codexCandidate = ProviderForSave(
            codex, fetchedCodex, existing?.Codex, existing?.CodexUpdatedAt ?? existing?.UpdatedAt, now);
        if (claudeCandidate?.IsFresh != true && codexCandidate?.IsFresh != true) return null;

        return new UsageCacheSnapshot(
            claudeCandidate?.Usage ?? AppUsage.Empty,
            codexCandidate?.Usage ?? AppUsage.Empty,
            Max(claudeCandidate?.UpdatedAt, codexCandidate?.UpdatedAt) ?? now,
            claudeCandidate?.UpdatedAt,
            codexCandidate?.UpdatedAt);
    }

    public static UsageCacheSnapshot? RestoredSnapshot(
        UsageCacheSnapshot snapshot,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        if (now - snapshot.UpdatedAt > maxAge) return null;
        var claudeCopy = RestoredProvider(snapshot.Claude, snapshot.ClaudeUpdatedAt, snapshot.UpdatedAt, now, maxAge);
        var codexCopy = RestoredProvider(snapshot.Codex, snapshot.CodexUpdatedAt, snapshot.UpdatedAt, now, maxAge);
        if (claudeCopy is null && codexCopy is null) return null;
        return new UsageCacheSnapshot(
            claudeCopy ?? AppUsage.Empty,
            codexCopy ?? AppUsage.Empty,
            Max(snapshot.ClaudeUpdatedAt, snapshot.CodexUpdatedAt) ?? snapshot.UpdatedAt,
            claudeCopy is null ? null : snapshot.ClaudeUpdatedAt ?? snapshot.UpdatedAt,
            codexCopy is null ? null : snapshot.CodexUpdatedAt ?? snapshot.UpdatedAt);
    }

    public static AppUsage? CacheableCopy(AppUsage usage)
    {
        // A healthy fetch is cacheable even when the provider reports only
        // one window (like Codex's primary-only shape). Requiring BOTH windows
        // clean would silently stop caching in those cases.
        if (usage.FiveHour.Error is not null
            || (usage.Weekly.Error is not null && !usage.SecondaryMissing)
            || !HasUsageValues(usage))
        {
            return null;
        }
        return new AppUsage(
            new WindowUsage(
                usage.FiveHour.UsedPercent, usage.FiveHour.ResetAt, null,
                usage.FiveHour.PeriodSeconds),
            new WindowUsage(
                usage.Weekly.UsedPercent, usage.Weekly.ResetAt,
                // Keep the "single window" marker so a cold start straight
                // from cache hides the empty tile instead of showing 0%.
                usage.SecondaryMissing ? usage.Weekly.Error : null,
                usage.Weekly.PeriodSeconds),
            usage.Plan,
            usage.ResetCards,
            usage.ResetCardDetails);
    }

    private static bool HasUsageValues(AppUsage usage) =>
        usage.FiveHour.UsedPercent > 0
        || usage.Weekly.UsedPercent > 0
        || usage.FiveHour.ResetAt is not null
        || usage.Weekly.ResetAt is not null
        || usage.FiveHour.PeriodSeconds is not null
        || usage.Weekly.PeriodSeconds is not null;

    private static (AppUsage Usage, DateTimeOffset UpdatedAt, bool IsFresh)? ProviderForSave(
        AppUsage current,
        bool wasFetched,
        AppUsage? existing,
        DateTimeOffset? existingUpdatedAt,
        DateTimeOffset now)
    {
        if (wasFetched && CacheableCopy(current) is { } freshCopy)
        {
            return (freshCopy, now, true);
        }
        if (existing is null || existingUpdatedAt is null) return null;
        if (CacheableCopy(existing) is not { } preservedCopy) return null;
        return (preservedCopy, existingUpdatedAt.Value, false);
    }

    private static AppUsage? RestoredProvider(
        AppUsage usage,
        DateTimeOffset? providerUpdatedAt,
        DateTimeOffset fallbackUpdatedAt,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        var updatedAt = providerUpdatedAt ?? fallbackUpdatedAt;
        if (now - updatedAt > maxAge) return null;
        return CacheableCopy(usage);
    }

    private static DateTimeOffset? Max(DateTimeOffset? lhs, DateTimeOffset? rhs)
    {
        if (lhs is { } l && rhs is { } r) return l > r ? l : r;
        return lhs ?? rhs;
    }
}
