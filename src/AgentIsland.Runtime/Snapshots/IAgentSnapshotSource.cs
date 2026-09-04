using AgentIsland.Core.Agents;

namespace AgentIsland.Runtime.Snapshots;

/// One provider adapter. Implementations may read a local ledger, call a
/// provider endpoint, or combine both, but must return one host-neutral
/// snapshot and honor cancellation.
public interface IAgentSnapshotSource
{
    AgentKey Agent { get; }

    Task<AgentSnapshot> ReadAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}
