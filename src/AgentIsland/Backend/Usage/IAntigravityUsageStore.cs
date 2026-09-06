using System.ComponentModel;
using AgentIsland.Providers.Usage.Antigravity;

namespace AgentIsland.Backend.Usage;

public interface IAntigravityUsageStore : INotifyPropertyChanged
{
    AntigravityQuotaSnapshot? Snapshot { get; }
    string? StatusCaption { get; }
    DateTimeOffset? LastUpdated { get; }
    string? AccountEmail { get; }
    string? TierBadge { get; }
    bool Loading { get; }
    bool Detected { get; }
    void KickRefresh();
    void ClearMemory();
}
