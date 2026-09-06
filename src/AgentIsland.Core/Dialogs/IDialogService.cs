namespace AgentIsland.Core.Dialogs;

/// <summary>
/// Contract for displaying application modals, alerts, and confirmation dialogs.
/// Decouples ViewModels from direct WPF dialog classes and static callsites.
/// </summary>
public interface IDialogService
{
    Task<bool> ShowConfirmAsync(string title, string message, string confirmLabel, string cancelLabel);
    void ShowAlert(string title, string message, string buttonLabel);
    void ShowUpdateDialog(string title, string message, string version);
}
