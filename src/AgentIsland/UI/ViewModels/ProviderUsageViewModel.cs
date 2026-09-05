using CommunityToolkit.Mvvm.ComponentModel;
using AgentIsland.Core.Usage;
using AgentIsland.UI.Providers;

namespace AgentIsland.UI.ViewModels;

/// <summary>
/// ViewModel representing a single provider's usage display slot.
/// </summary>
public sealed partial class ProviderUsageViewModel : ObservableObject
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
    private AppUsage? _usage;

    [ObservableProperty]
    private bool _isReauthRequired;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public ProviderUsageViewModel(DisplayProvider provider)
    {
        _provider = provider;
        _displayName = provider.ToString();
    }
}
