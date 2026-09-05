using CommunityToolkit.Mvvm.ComponentModel;
using AgentIsland.UI.Providers;

namespace AgentIsland.UI.ViewModels;

/// <summary>
/// ViewModel representing a single provider's cost and token expenditure slot.
/// </summary>
public sealed partial class ProviderCostViewModel : ObservableObject
{
    [ObservableProperty]
    private DisplayProvider _provider;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isSolo;

    [ObservableProperty]
    private string _todayHeroText = "$0.00";

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private bool _hasLocalData = true;

    public ProviderCostViewModel(DisplayProvider provider)
    {
        _provider = provider;
        _displayName = provider.ToString();
    }
}
