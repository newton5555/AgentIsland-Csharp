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

    public IslandViewModel()
    {
        ProviderVisibilityStore.Shared.PropertyChanged += OnVisibilityChanged;
        ActivityMonitor.Shared.PropertyChanged += OnActivityChanged;
        IslandModel.Shared.PropertyChanged += OnIslandModelChanged;

        State = IslandModel.Shared.State;
        RefreshState();
    }

    private void OnIslandModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandModel.State))
        {
            State = IslandModel.Shared.State;
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
        var slots = ProviderVisibilityStore.Shared.SlotProviders;
        LeftProvider = slots.Count > 0 ? slots[0] : null;
        RightProvider = slots.Count > 1 ? slots[1] : null;

        if (LeftProvider is { } p0)
        {
            var t0 = p0.ToTriggerTool();
            LeftActivity = ActivityMonitor.Shared.StateFor(t0);
            LeftThreadTitle = ActivityMonitor.Shared.ThreadFor(t0)?.Label;
        }
        else
        {
            LeftActivity = ActivityState.Idle;
            LeftThreadTitle = null;
        }

        if (RightProvider is { } p1)
        {
            var t1 = p1.ToTriggerTool();
            RightActivity = ActivityMonitor.Shared.StateFor(t1);
            RightThreadTitle = ActivityMonitor.Shared.ThreadFor(t1)?.Label;
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
        IslandModel.Shared.State = newState;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsWindow.Open();
    }

    public void Dispose()
    {
        ProviderVisibilityStore.Shared.PropertyChanged -= OnVisibilityChanged;
        ActivityMonitor.Shared.PropertyChanged -= OnActivityChanged;
        IslandModel.Shared.PropertyChanged -= OnIslandModelChanged;
    }
}
