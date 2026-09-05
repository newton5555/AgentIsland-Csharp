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

    private readonly IProviderVisibilityStore _visibilityStore;
    private readonly ICostStore _costStore;
    private readonly Dictionary<DisplayProvider, ProviderCostViewModel> _providerModels = new();

    public CostPageViewModel() : this(
        ProviderVisibilityStore.Shared,
        CostStore.Shared)
    {
    }

    public CostPageViewModel(
        IProviderVisibilityStore visibilityStore,
        ICostStore costStore)
    {
        _visibilityStore = visibilityStore ?? throw new ArgumentNullException(nameof(visibilityStore));
        _costStore = costStore ?? throw new ArgumentNullException(nameof(costStore));

        foreach (var provider in DisplayProviders.All)
        {
            _providerModels[provider] = new ProviderCostViewModel(provider);
        }

        _visibilityStore.PropertyChanged += OnVisibilityChanged;
        _costStore.PropertyChanged += OnCostChanged;

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
        var slots = _visibilityStore.SlotProviders;
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
        _costStore.Refresh();
    }

    public void Dispose()
    {
        _visibilityStore.PropertyChanged -= OnVisibilityChanged;
        _costStore.PropertyChanged -= OnCostChanged;
    }
}
