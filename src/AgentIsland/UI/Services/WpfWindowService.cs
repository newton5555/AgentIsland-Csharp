using System.Windows;
using AgentIsland.Core.Navigation;
using AgentIsland.Core.Threading;

namespace AgentIsland.UI.Services;

/// <summary>
/// WPF implementation of IWindowService.
/// Coordinates top-level window activation, visibility, and navigation on the UI dispatcher.
/// </summary>
public sealed class WpfWindowService : IWindowService
{
    private readonly IUiDispatcher _dispatcher;

    public WpfWindowService(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void OpenSettings(string? tab = null)
    {
        _dispatcher.BeginInvoke(() =>
        {
            SettingsWindow.Open();
        });
    }

    public void OpenReport(string? kind = null)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var reportKind = kind switch
            {
                _ when string.Equals(kind, "monthly", StringComparison.OrdinalIgnoreCase) => Report.ReportWindow.Kind.Monthly,
                _ when string.Equals(kind, "daily", StringComparison.OrdinalIgnoreCase) => Report.ReportWindow.Kind.Daily,
                _ => Report.ReportWindow.Kind.Weekly,
            };
            Report.ReportWindow.Show(reportKind);
        });
    }

    public void OpenWhatsNew()
    {
        _dispatcher.BeginInvoke(() =>
        {
            WhatsNewWindow.Open();
        });
    }


    public void ShowIsland()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (Application.Current.MainWindow is IslandWindow island)
            {
                island.DeliberatelyHidden = false;
                island.Show();
                island.PopUp();
            }
        });
    }

    public void HideIsland()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (Application.Current.MainWindow is IslandWindow island)
            {
                island.DeliberatelyHidden = true;
                island.Hide();
            }
        });
    }

    public void ToggleIsland()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (Application.Current.MainWindow is IslandWindow island)
            {
                if (island.IsVisible)
                {
                    HideIsland();
                }
                else
                {
                    ShowIsland();
                }
            }
        });
    }

    public bool IsTransparentMode =>
        Application.Current.MainWindow is IslandWindow island && island.IsTransparentMode;

    public void ToggleTransparentMode()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (Application.Current.MainWindow is IslandWindow island)
            {
                island.ToggleTransparentMode();
            }
        });
    }
}
