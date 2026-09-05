using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AgentIsland.Backend.Settings;
using AgentIsland.Backend.Updates;
using AgentIsland.Core.Storage;

namespace AgentIsland.UI.ViewModels;

/// <summary>
/// ViewModel managing preferences and configuration options in SettingsWindow.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private string _activeTab = "General";

    [ObservableProperty]
    private bool _autoCheckUpdates;

    [ObservableProperty]
    private bool _alertsEnabled;

    [ObservableProperty]
    private int _alertWarningPercent;

    [ObservableProperty]
    private int _alertCriticalPercent;

    [ObservableProperty]
    private double _islandScale;

    [ObservableProperty]
    private string _appLanguage = string.Empty;

    private readonly ISettingsStorage _storage;
    private readonly IUpdateChecker _updateChecker;

    public SettingsViewModel() : this(
        Preferences.Storage,
        UpdateChecker.Shared)
    {
    }

    public SettingsViewModel(
        ISettingsStorage storage,
        IUpdateChecker updateChecker)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _updateChecker = updateChecker ?? throw new ArgumentNullException(nameof(updateChecker));
        LoadSettings();
    }

    public void LoadSettings()
    {
        AutoCheckUpdates = _storage.Get<bool?>("AgentIsland.autoCheckUpdates") ?? true;
        AlertsEnabled = AlertThresholdStore.Shared.Enabled;
        AlertWarningPercent = AlertThresholdStore.Shared.WarningPercent;
        AlertCriticalPercent = AlertThresholdStore.Shared.CriticalPercent;
        IslandScale = IslandScaleStore.Shared.Scale;
        AppLanguage = AppLanguageStore.Load().ToString();
    }

    [RelayCommand]
    private void SelectTab(string tab)
    {
        ActiveTab = tab;
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        await _updateChecker.CheckAsync(userInitiated: true).ConfigureAwait(false);
    }
}
