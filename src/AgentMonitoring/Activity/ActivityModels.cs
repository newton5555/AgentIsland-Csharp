using AgentIsland.Core;
using AgentIsland.Core.Agents;

namespace AgentMonitoring.Activity;

public sealed record ActivityThread(
    string SessionId,
    string Label,
    string Cwd,
    DateTimeOffset Modified,
    string? TranscriptPath,
    string? TurnKey,
    SessionLaunchTarget LaunchTarget);

public sealed record AgentActivity(
    AgentKey Agent,
    ActivityState State,
    ActivityThread? Thread,
    IReadOnlyList<ActivityThread> NeedsYouThreads,
    DateTimeOffset UpdatedAt);

public interface IActivitySnapshotStore
{
    AgentActivity? Read(AgentKey agent);
    IReadOnlyList<AgentActivity> ReadAll();
    void Replace(AgentActivity snapshot);
}

public sealed class ActivitySnapshotStore : IActivitySnapshotStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentActivity> _snapshots = new(StringComparer.Ordinal);

    public AgentActivity? Read(AgentKey agent)
    {
        lock (_gate) return _snapshots.TryGetValue(agent.Value, out var snapshot) ? snapshot : null;
    }

    public IReadOnlyList<AgentActivity> ReadAll()
    {
        lock (_gate) return _snapshots.Values.ToList();
    }

    public void Replace(AgentActivity snapshot)
    {
        lock (_gate) _snapshots[snapshot.Agent.Value] = snapshot;
    }
}
