namespace AgentIsland.Core.Options;

public interface ISettingsManager
{
    IslandDisplayOptions Display { get; }
    PollingOptions Polling { get; }
    AlertOptions Alerts { get; }
    ProviderVisibilityOptions ProviderVisibility { get; }

    void UpdateDisplay(Action<IslandDisplayOptions> configure);
    void UpdatePolling(Action<PollingOptions> configure);
    void UpdateAlerts(Action<AlertOptions> configure);
    void UpdateProviderVisibility(Action<ProviderVisibilityOptions> configure);

    event EventHandler<IslandDisplayOptions>? DisplayChanged;
    event EventHandler<PollingOptions>? PollingChanged;
    event EventHandler<AlertOptions>? AlertsChanged;
    event EventHandler<ProviderVisibilityOptions>? ProviderVisibilityChanged;
}
