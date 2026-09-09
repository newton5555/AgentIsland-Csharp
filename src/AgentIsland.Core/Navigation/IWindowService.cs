namespace AgentIsland.Core.Navigation;

/// <summary>
/// Contract for top-level application window navigation and visibility management.
/// Decouples ViewModels from direct WPF Window class dependencies.
/// </summary>
public interface IWindowService
{
    void OpenSettings(string? tab = null);
    void OpenReport(string? kind = null);
    void OpenWhatsNew();
    void ShowIsland();
    void HideIsland();
    void ToggleIsland();
    void ToggleTransparentMode();
    bool IsTransparentMode { get; }
}
