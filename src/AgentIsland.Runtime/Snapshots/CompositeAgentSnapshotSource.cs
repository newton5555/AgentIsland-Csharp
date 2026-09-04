using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;

namespace AgentIsland.Runtime.Snapshots;

/// <summary>
/// Combines multiple <see cref="IAgentSnapshotSource"/> facets for the same agent into one merged snapshot.
/// Facets (such as local token/cost logs, live session activity, and account usage) are read concurrently.
///
/// Merging rules:
/// 1. Activity: Highest urgency priority wins (Idle &lt; Working &lt; NeedsYou &lt; Stalled &lt; RateLimited &lt; AuthRequired).
/// 2. Cost / Usage / DataAt: Selects from available facets using deterministic rules (prefers actual data and latest timestamp).
/// 3. Availability: A NoData facet never overwrites a Ready facet. When any facet produces Error/Stale, diagnostic details are preserved.
/// 4. Cancellation: All facets receive and honor the cancellation token.
/// 5. AgentKey mismatch: Reported as a diagnosable error rather than silently dropped.
/// </summary>
public sealed class CompositeAgentSnapshotSource : IAgentSnapshotSource
{
    private readonly IReadOnlyList<IAgentSnapshotSource> _sources;

    public AgentKey Agent { get; }
    public IReadOnlyList<IAgentSnapshotSource> Sources => _sources;

    public CompositeAgentSnapshotSource(IEnumerable<IAgentSnapshotSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var list = sources.Where(s => s is not null).ToArray();
        if (list.Length == 0)
            throw new ArgumentException("Sources cannot be empty.", nameof(sources));

        Agent = list[0].Agent;
        _sources = list;
    }

    public CompositeAgentSnapshotSource(AgentKey agent, IEnumerable<IAgentSnapshotSource> sources)
    {
        if (string.IsNullOrWhiteSpace(agent.Value))
            throw new ArgumentException("Agent key cannot be empty.", nameof(agent));
        ArgumentNullException.ThrowIfNull(sources);

        var list = sources.Where(s => s is not null).ToArray();
        if (list.Length == 0)
            throw new ArgumentException("Sources cannot be empty.", nameof(sources));

        Agent = agent;
        _sources = list;
    }

    public Task<AgentSnapshot> ReadAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        ReadAsync(observedAt, previous: null, cancellationToken);

    public async Task<AgentSnapshot> ReadAsync(
        DateTimeOffset observedAt,
        AgentSnapshot? previous,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tasks = _sources
            .Select(source => ReadFacetSafeAsync(source, observedAt, cancellationToken))
            .ToArray();

        var facetSnapshots = await Task.WhenAll(tasks).ConfigureAwait(false);
        return Merge(Agent, observedAt, facetSnapshots, previous);
    }

    private async Task<AgentSnapshot> ReadFacetSafeAsync(
        IAgentSnapshotSource source,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (source.Agent != Agent)
        {
            return AgentSnapshot.ErrorState(
                Agent,
                ActivityState.Idle,
                observedAt,
                $"facet source agent mismatch: expected '{Agent}', got '{source.Agent}'");
        }

        try
        {
            var snapshot = await source.ReadAsync(observedAt, cancellationToken).ConfigureAwait(false);
            if (snapshot.Agent != Agent)
            {
                return AgentSnapshot.ErrorState(
                    Agent,
                    ActivityState.Idle,
                    observedAt,
                    $"snapshot agent mismatch: expected '{Agent}', got '{snapshot.Agent}'");
            }
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AgentSnapshot.ErrorState(
                Agent,
                ActivityState.Idle,
                observedAt,
                ex.Message);
        }
    }

    /// <summary>
    /// Merges multiple facet snapshots for an agent into a unified snapshot according to precedence rules.
    /// </summary>
    public static AgentSnapshot Merge(
        AgentKey agent,
        DateTimeOffset observedAt,
        IReadOnlyList<AgentSnapshot> snapshots,
        AgentSnapshot? previous = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        if (snapshots.Count == 0)
        {
            return AgentSnapshot.NoData(agent, ActivityState.Idle, observedAt);
        }

        // 1. Activity: Highest priority according to urgency ranking
        var activity = snapshots
            .Select(s => s.Activity)
            .DefaultIfEmpty(ActivityState.Idle)
            .Max();

        // 2. Cost: Deterministic selection (prefer non-empty data, then latest DataAt, then highest token volume)
        var cost = SelectCost(snapshots) ?? previous?.Cost;

        // 3. Usage: Deterministic selection (prefer non-error, then valid data, then latest DataAt, then highest percent)
        var usage = SelectUsage(snapshots) ?? previous?.Usage;

        // 4. Balance: Deterministic selection (prefer valid entries / available, then latest DataAt)
        var balance = SelectBalance(snapshots) ?? previous?.Balance;

        // 5. DataAt: Prefer timestamp from Ready facets so NoData placeholder doesn't overwrite real data
        var dataAt = SelectDataAt(snapshots) ?? previous?.DataAt;

        // 6. Diagnostics: Collect all errors from facets
        var errors = snapshots
            .Where(s => !string.IsNullOrWhiteSpace(s.Error))
            .Select(s => s.Error!.Trim())
            .Distinct()
            .ToList();

        var mergedError = errors.Count > 0 ? string.Join("; ", errors) : null;

        // 7. Availability:
        // - At least one Ready facet -> Ready (NoData cannot overwrite Ready).
        // - If no Ready facet, but error/stale present -> Stale if dataAt exists, else Error.
        // - If no Ready and no error -> NoData if any NoData, else NotConfigured, else Unknown.
        var anyReady = snapshots.Any(s => s.Availability == SnapshotAvailability.Ready);
        var anyNoData = snapshots.Any(s => s.Availability == SnapshotAvailability.NoData);
        var anyNotConfigured = snapshots.Any(s => s.Availability == SnapshotAvailability.NotConfigured);
        var hasFailure = mergedError is not null || snapshots.Any(s => s.Availability is SnapshotAvailability.Error or SnapshotAvailability.Stale);

        SnapshotAvailability availability;
        if (anyReady)
        {
            availability = SnapshotAvailability.Ready;
        }
        else if (hasFailure)
        {
            availability = dataAt is not null
                ? SnapshotAvailability.Stale
                : SnapshotAvailability.Error;
        }
        else if (anyNoData)
        {
            availability = SnapshotAvailability.NoData;
            dataAt ??= observedAt;
        }
        else if (anyNotConfigured)
        {
            availability = SnapshotAvailability.NotConfigured;
        }
        else
        {
            availability = SnapshotAvailability.Unknown;
        }

        return new AgentSnapshot(
            agent,
            activity,
            availability,
            observedAt,
            DataAt: dataAt,
            Usage: usage,
            Cost: cost,
            Error: mergedError,
            Balance: balance);
    }

    private static ProviderCostSummary? SelectCost(IReadOnlyList<AgentSnapshot> snapshots)
    {
        var candidates = snapshots
            .Where(s => s.Cost is not null)
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Cost;

        return candidates
            .OrderByDescending(s => HasCostData(s.Cost!))
            .ThenByDescending(s => s.DataAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(s => s.Cost!.TodayTokens + s.Cost.MonthTokens)
            .ThenByDescending(s => s.Cost!.TodayDollars + s.Cost.MonthDollars)
            .Select(s => s.Cost)
            .First();
    }

    private static bool HasCostData(ProviderCostSummary c) =>
        c.TodayTokens > 0 || c.MonthTokens > 0 || c.TodayDollars > 0 || c.MonthDollars > 0 ||
        c.RecentModels.Count > 0 || c.DailyHistory.Count > 0;

    private static AppUsage? SelectUsage(IReadOnlyList<AgentSnapshot> snapshots)
    {
        var candidates = snapshots
            .Where(s => s.Usage is not null)
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Usage;

        return candidates
            .OrderByDescending(s => !s.Usage!.IsErrorOnly)
            .ThenByDescending(s => HasUsageData(s.Usage!))
            .ThenByDescending(s => s.DataAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(s => Math.Max(s.Usage!.FiveHour.UsedPercent, s.Usage!.Weekly.UsedPercent))
            .Select(s => s.Usage)
            .First();
    }

    private static bool HasUsageData(AppUsage u) =>
        !u.IsErrorOnly && (
            u.FiveHour.UsedPercent > 0 ||
            u.Weekly.UsedPercent > 0 ||
            !string.IsNullOrEmpty(u.Plan) ||
            u.ResetCards is not null ||
            (u.FiveHour.ResetAt is not null && u.FiveHour.Error is null));

    private static AccountBalanceSnapshot? SelectBalance(IReadOnlyList<AgentSnapshot> snapshots)
    {
        var candidates = snapshots
            .Where(s => s.Balance is not null)
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Balance;

        // Deterministic selection: prefer snapshots with actual balance entries (zero balance is valid data),
        // then available, then latest DataAt.
        return candidates
            .OrderByDescending(s => s.Balance!.HasEntries)
            .ThenByDescending(s => s.Balance!.IsAvailable)
            .ThenByDescending(s => s.DataAt ?? DateTimeOffset.MinValue)
            .Select(s => s.Balance)
            .First();
    }

    private static DateTimeOffset? SelectDataAt(IReadOnlyList<AgentSnapshot> snapshots)
    {
        var readyDataTimes = snapshots
            .Where(s => s.Availability == SnapshotAvailability.Ready && s.DataAt is not null)
            .Select(s => s.DataAt!.Value)
            .ToList();

        if (readyDataTimes.Count > 0)
        {
            return readyDataTimes.Max();
        }

        var otherDataTimes = snapshots
            .Where(s => s.Availability != SnapshotAvailability.NoData && s.DataAt is not null)
            .Select(s => s.DataAt!.Value)
            .ToList();

        if (otherDataTimes.Count > 0)
        {
            return otherDataTimes.Max();
        }

        var allDataTimes = snapshots
            .Where(s => s.DataAt is not null)
            .Select(s => s.DataAt!.Value)
            .ToList();

        return allDataTimes.Count > 0 ? allDataTimes.Max() : null;
    }
}
