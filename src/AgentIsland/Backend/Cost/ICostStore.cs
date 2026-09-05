using System.ComponentModel;
using AgentIsland.Core.Cost;
using AgentIsland.UI.Providers;

namespace AgentIsland.Backend.Cost;

/// <summary>
/// Contract for provider token cost rollups and ledger summaries.
/// </summary>
public interface ICostStore : INotifyPropertyChanged
{
    ProviderCostSummary Summary(DisplayProvider provider);
    ProviderCostSummary Claude { get; }
    ProviderCostSummary Codex { get; }
    ProviderCostSummary DeepSeek { get; }
    DateTimeOffset? LastUpdated { get; }
    void Refresh();
    void StartAutoRefresh();
    void StopAutoRefresh();
}
