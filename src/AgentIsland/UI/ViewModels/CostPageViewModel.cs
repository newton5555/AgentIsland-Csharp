using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AgentIsland.Backend.Cost;
using AgentIsland.Backend.Settings;
using AgentIsland.UI.Providers;

namespace AgentIsland.UI.ViewModels;

/// <summary>
/// ViewModel driving the CostPage data presentation.
/// </summary>
public sealed partial class CostPageViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private bool _bothHidden;

    [ObservableProperty]
    private bool _hairlineVisible;

    [ObservableProperty]
    private ProviderCostViewModel? _leftSlot;

    [ObservableProperty]
    private ProviderCostViewModel? _rightSlot;

    private readonly Dictionary<DisplayProvider, ProviderCostViewModel> _providerModels = new();

    public CostPageViewModel()
    {
        foreach (var provider in DisplayProviders.All)
        {
            _providerModels[provider] = new ProviderCostViewModel(provider);
        }

        ProviderVisibilityStore.Shared.PropertyChanged += OnVisibilityChanged;
        CostStore.Shared.PropertyChanged += OnCostChanged;

        UpdateLayout();
    }

    private void OnVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateLayout();
    }

    private void OnCostChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateCostData();
    }

    public void UpdateLayout()
    {
        var slots = ProviderVisibilityStore.Shared.SlotProviders;
        var s0 = slots.Count > 0 ? slots[0] : (DisplayProvider?)null;
        var s1 = slots.Count > 1 ? slots[1] : (DisplayProvider?)null;

        BothHidden = s0 is null && s1 is null;
        HairlineVisible = s0 is not null && s1 is not null;

        LeftSlot = s0 is { } p0 ? _providerModels[p0] : null;
        RightSlot = s1 is { } p1 ? _providerModels[p1] : null;

        if (LeftSlot is not null)
        {
            LeftSlot.IsSolo = s1 is null;
            LeftSlot.IsVisible = true;
        }

        if (RightSlot is not null)
        {
            RightSlot.IsSolo = s0 is null;
            RightSlot.IsVisible = true;
        }

        UpdateCostData();
    }

    private void UpdateCostData()
    {
        // Refresh local data indicator
        if (_providerModels.TryGetValue(DisplayProvider.Antigravity, out var agy))
        {
            agy.HasLocalData = false;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        CostStore.Shared.Refresh();
    }

    public void Dispose()
    {
        ProviderVisibilityStore.Shared.PropertyChanged -= OnVisibilityChanged;
        CostStore.Shared.PropertyChanged -= OnCostChanged;
    }
}
