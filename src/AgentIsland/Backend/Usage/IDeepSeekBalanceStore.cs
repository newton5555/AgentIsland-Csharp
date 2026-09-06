using System.ComponentModel;
using AgentIsland.Providers.Usage.DeepSeek;

namespace AgentIsland.Backend.Usage;

public interface IDeepSeekBalanceStore : INotifyPropertyChanged
{
    DeepSeekBalanceSnapshot? Snapshot { get; }
    string? ErrorCaption { get; }
    DateTimeOffset? LastUpdated { get; }
    bool Loading { get; }
    bool Configured { get; }
    void KickRefresh();
    void ClearMemory();
}
