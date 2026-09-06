using System.ComponentModel;
using AgentIsland.Providers.Usage.Grok;

namespace AgentIsland.Backend.Usage;

public interface IGrokUsageStore : INotifyPropertyChanged
{
    GrokBillingSnapshot? Snapshot { get; }
    string? ErrorCaption { get; }
    DateTimeOffset? LastUpdated { get; }
    string? AccountEmail { get; }
    string? AuthModeBadge { get; }
    bool Loading { get; }
    void KickRefresh();
    void ClearMemory();
}
