using AgentIsland.Core.Dialogs;
using AgentIsland.Core.Threading;

namespace AgentIsland.UI.Services;

/// <summary>
/// WPF implementation of IDialogService.
/// Coordinates popups and modal alerts using IslandDialog on the UI dispatcher.
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    private readonly IUiDispatcher _dispatcher;

    public WpfDialogService(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public Task<bool> ShowConfirmAsync(string title, string message, string confirmLabel, string cancelLabel)
    {
        var tcs = new TaskCompletionSource<bool>();
        _dispatcher.BeginInvoke(() =>
        {
            IslandDialog.ShowApp(
                title,
                message,
                primaryLabel: confirmLabel,
                primaryAction: () => tcs.TrySetResult(true),
                secondaryLabel: cancelLabel);
        });
        return tcs.Task;
    }

    public void ShowAlert(string title, string message, string buttonLabel)
    {
        _dispatcher.BeginInvoke(() =>
        {
            IslandDialog.ShowApp(
                title,
                message,
                primaryLabel: buttonLabel);
        });
    }

    public void ShowUpdateDialog(string title, string message, string version)
    {
        _dispatcher.BeginInvoke(() =>
        {
            IslandDialog.ShowUpdate(
                title,
                message,
                primaryLabel: Localization.L10n.Tr("Update & Relaunch"),
                secondaryLabel: Localization.L10n.Tr("Later"));
        });
    }
}
