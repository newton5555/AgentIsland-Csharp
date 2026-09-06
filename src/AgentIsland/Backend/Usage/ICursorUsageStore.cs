using System.ComponentModel;

namespace AgentIsland.Backend.Usage;

public interface ICursorUsageStore : INotifyPropertyChanged
{
    CursorUsageSnapshot? Snapshot { get; }
    string? ErrorCaption { get; }
    DateTimeOffset? LastUpdated { get; }
    string? AccountEmail { get; }
    string? LocalPlan { get; }
    string? PlanBadge { get; }
    bool Loading { get; }
    void KickRefresh();
    void ClearMemory();
}
