using System.ComponentModel;
using AgentIsland.UI.Providers;

namespace AgentIsland.Backend.Settings;

/// <summary>
/// Contract for managing enabled providers and island silhouette slots.
/// </summary>
public interface IProviderVisibilityStore : INotifyPropertyChanged
{
    IReadOnlyList<DisplayProvider> SlotProviders { get; }
    IReadOnlyList<DisplayProvider> Slots { get; }
    IReadOnlyList<DisplayProvider> Enabled { get; }
    IReadOnlyList<DisplayProvider> Order { get; }
    bool ClaudeVisible { get; set; }
    bool CodexVisible { get; set; }
    bool IsShown(DisplayProvider provider);
    bool IsEnabled(DisplayProvider provider);
    bool Toggle(DisplayProvider provider);
}
