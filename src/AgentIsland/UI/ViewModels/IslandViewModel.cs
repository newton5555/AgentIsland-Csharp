using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AgentIsland.Core;
using AgentIsland.Backend.Monitoring;
using AgentIsland.Backend.Settings;
using AgentIsland.UI.Providers;

namespace AgentIsland.UI.ViewModels;

/// <summary>
/// Core ViewModel managing the Dynamic Island's state, animations, and active session presentation.
/// Keeps low-level Win32 windowing code decoupled from high-level business rules.
/// </summary>
public sealed partial class IslandViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private IslandState _state = IslandState.Compact;

    [ObservableProperty]
    private bool _isDeliberatelyHidden;

    [ObservableProperty]
    private DisplayProvider? _leftProvider;

    [ObservableProperty]
    private DisplayProvider? _rightProvider;

    [ObservableProperty]
    private ActivityState _leftActivity = ActivityState.Idle;

    [ObservableProperty]
    private ActivityState _rightActivity = ActivityState.Idle;

    [ObservableProperty]
    private string? _leftThreadTitle;

    [ObservableProperty]
    private string? _rightThreadTitle;

    [ObservableProperty]
    private bool _hasNeedsYou;

    private readonly IProviderVisibilityStore _visibilityStore;
    private readonly IActivityMonitor _activityMonitor;
    private readonly IIslandModel _islandModel;
    private readonly AgentIsland.Core.Navigation.IWindowService? _windowService;

    public IslandViewModel() : this(
        (App.Instance?.Services?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore) ?? new ProviderVisibilityStore(),
        (App.Instance?.Services?.GetService(typeof(IActivityMonitor)) as IActivityMonitor) ?? new ActivityMonitor(),
        (App.Instance?.Services?.GetService(typeof(IIslandModel)) as IIslandModel) ?? new IslandModel(),
        App.Instance?.Services?.GetService(typeof(AgentIsland.Core.Navigation.IWindowService)) as AgentIsland.Core.Navigation.IWindowService)
    {
    }

    public IslandViewModel(
        IProviderVisibilityStore visibilityStore,
        IActivityMonitor activityMonitor,
        IIslandModel islandModel,
        AgentIsland.Core.Navigation.IWindowService? windowService = null)
    {
        _visibilityStore = visibilityStore ?? throw new ArgumentNullException(nameof(visibilityStore));
        _activityMonitor = activityMonitor ?? throw new ArgumentNullException(nameof(activityMonitor));
        _islandModel = islandModel ?? throw new ArgumentNullException(nameof(islandModel));
        _windowService = windowService;


        _visibilityStore.PropertyChanged += OnVisibilityChanged;
        _activityMonitor.PropertyChanged += OnActivityChanged;
        _islandModel.PropertyChanged += OnIslandModelChanged;

        State = _islandModel.State;
        RefreshState();
    }

    private void OnIslandModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IIslandModel.State))
        {
            State = _islandModel.State;
        }
    }

    private void OnVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RefreshState();
    }

    private void OnActivityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RefreshState();
    }

    public void RefreshState()
    {
        var slots = _visibilityStore.SlotProviders;
        LeftProvider = slots.Count > 0 ? slots[0] : null;
        RightProvider = slots.Count > 1 ? slots[1] : null;

        if (LeftProvider is { } p0)
        {
            var t0 = p0.ToTriggerTool();
            LeftActivity = _activityMonitor.StateFor(t0);
            LeftThreadTitle = _activityMonitor.ThreadFor(t0)?.Label;
        }
        else
        {
            LeftActivity = ActivityState.Idle;
            LeftThreadTitle = null;
        }

        if (RightProvider is { } p1)
        {
            var t1 = p1.ToTriggerTool();
            RightActivity = _activityMonitor.StateFor(t1);
            RightThreadTitle = _activityMonitor.ThreadFor(t1)?.Label;
        }
        else
        {
            RightActivity = ActivityState.Idle;
            RightThreadTitle = null;
        }

        HasNeedsYou = LeftActivity == ActivityState.NeedsYou || RightActivity == ActivityState.NeedsYou;
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        var newState = State == IslandState.Expanded ? IslandState.Compact : IslandState.Expanded;
        State = newState;
        _islandModel.State = newState;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (_windowService is not null)
        {
            _windowService.OpenSettings();
        }
        else
        {
            SettingsWindow.Open();
        }
    }


    public void Dispose()
    {
        _visibilityStore.PropertyChanged -= OnVisibilityChanged;
        _activityMonitor.PropertyChanged -= OnActivityChanged;
        _islandModel.PropertyChanged -= OnIslandModelChanged;
    }
}
