using System.ComponentModel;
using AgentIsland.Core.Usage;

namespace AgentIsland.Backend.Usage;

/// <summary>
/// Contract for provider usage caching, fetching, and authentication status.
/// </summary>
public interface IUsageStore : INotifyPropertyChanged
{
    AppUsage Claude { get; }
    AppUsage Codex { get; }
    AppUsage Usage(AgentIsland.UI.Providers.DisplayProvider provider);
    DateTimeOffset? LastUpdated { get; }
    string? RefreshWarning { get; }
    bool Loading { get; }
    bool ClaudeReauthInProgress { get; }
    bool CodexReauthInProgress { get; }
    string? ClaudeReauthFailureCaption { get; }
    string? CodexAutoSwitched { get; set; }
    void Refresh();
    void RefreshIfStale();
    void ClearClaudeReauthFailure();
}
