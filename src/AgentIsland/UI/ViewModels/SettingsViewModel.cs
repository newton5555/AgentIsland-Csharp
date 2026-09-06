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
    private readonly AlertThresholdStore _thresholdStore;
    private readonly IslandScaleStore _scaleStore;
    private readonly AgentIsland.Core.Navigation.IWindowService? _windowService;
    private readonly AgentIsland.Core.Dialogs.IDialogService? _dialogService;

    public SettingsViewModel() : this(
        (App.Instance?.Services?.GetService(typeof(ISettingsStorage)) as ISettingsStorage) ?? Preferences.Storage,
        (App.Instance?.Services?.GetService(typeof(IUpdateChecker)) as IUpdateChecker) ?? new UpdateChecker(),
        (App.Instance?.Services?.GetService(typeof(AlertThresholdStore)) as AlertThresholdStore) ?? new AlertThresholdStore(),
        (App.Instance?.Services?.GetService(typeof(IslandScaleStore)) as IslandScaleStore) ?? new IslandScaleStore(),
        App.Instance?.Services?.GetService(typeof(AgentIsland.Core.Navigation.IWindowService)) as AgentIsland.Core.Navigation.IWindowService,
        App.Instance?.Services?.GetService(typeof(AgentIsland.Core.Dialogs.IDialogService)) as AgentIsland.Core.Dialogs.IDialogService)
    {
    }

    public SettingsViewModel(
        ISettingsStorage storage,
        IUpdateChecker updateChecker,
        AlertThresholdStore? thresholdStore = null,
        IslandScaleStore? scaleStore = null,
        AgentIsland.Core.Navigation.IWindowService? windowService = null,
        AgentIsland.Core.Dialogs.IDialogService? dialogService = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _updateChecker = updateChecker ?? throw new ArgumentNullException(nameof(updateChecker));
        _thresholdStore = thresholdStore ?? new AlertThresholdStore();
        _scaleStore = scaleStore ?? new IslandScaleStore();
        _windowService = windowService;
        _dialogService = dialogService;
        LoadSettings();
    }

    public void LoadSettings()
    {
        AutoCheckUpdates = _storage.Get<bool?>("AgentIsland.autoCheckUpdates") ?? true;
        AlertsEnabled = _thresholdStore.Enabled;
        AlertWarningPercent = _thresholdStore.WarningPercent;
        AlertCriticalPercent = _thresholdStore.CriticalPercent;
        IslandScale = _scaleStore.Scale;
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
