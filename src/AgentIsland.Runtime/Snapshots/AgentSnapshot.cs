using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;

namespace AgentIsland.Runtime.Snapshots;

/// Why a provider snapshot has no usable value. This is deliberately more
/// precise than an empty AppUsage: UI hosts can distinguish a provider that
/// is not configured from a configured provider whose local ledger is empty.
public enum SnapshotAvailability
{
    Unknown,
    Ready,
    NoData,
    NotConfigured,
    Error,
    Stale,
}

/// Host-neutral state consumed by WPF, Avalonia, and future front ends.
/// Provider-specific readers produce this record; front ends decide how to
/// render colors, animations, and localized captions.
public sealed record AgentSnapshot(
    AgentKey Agent,
    ActivityState Activity,
    SnapshotAvailability Availability,
    DateTimeOffset ObservedAt,
    DateTimeOffset? DataAt = null,
    AppUsage? Usage = null,
    ProviderCostSummary? Cost = null,
    string? Error = null)
{
    public bool HasFreshData => Availability == SnapshotAvailability.Ready
        && DataAt is not null;

    public bool IsUsable => Availability is
        SnapshotAvailability.Ready or SnapshotAvailability.NoData;

    public static AgentSnapshot NoData(AgentKey agent, ActivityState activity,
        DateTimeOffset observedAt) => new(
        agent, activity, SnapshotAvailability.NoData, observedAt,
        DataAt: observedAt);

    public static AgentSnapshot ErrorState(AgentKey agent, ActivityState activity,
        DateTimeOffset observedAt, string error, AgentSnapshot? previous = null) => new(
        agent,
        activity,
        previous?.DataAt is not null
            ? SnapshotAvailability.Stale
            : SnapshotAvailability.Error,
        observedAt,
        previous?.DataAt,
        previous?.Usage,
        previous?.Cost,
        error);
}

public sealed class AgentSnapshotsChangedEventArgs(
    IReadOnlyDictionary<AgentKey, AgentSnapshot> snapshots,
    DateTimeOffset observedAt) : EventArgs
{
    public IReadOnlyDictionary<AgentKey, AgentSnapshot> Snapshots { get; } = snapshots;
    public DateTimeOffset ObservedAt { get; } = observedAt;
}
