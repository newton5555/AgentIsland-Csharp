using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AgentIsland.Backend.Settings;
using AgentIsland.Backend.Usage;
using AgentIsland.UI.Providers;

namespace AgentIsland.UI.ViewModels;

/// <summary>
/// ViewModel driving the UsagePage data presentation and re-auth actions.
/// </summary>
public sealed partial class UsagePageViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private bool _bothHidden;

    [ObservableProperty]
    private bool _hairlineVisible;

    [ObservableProperty]
    private bool _claudeReauthVisible;

    [ObservableProperty]
    private ProviderUsageViewModel? _leftSlot;

    [ObservableProperty]
    private ProviderUsageViewModel? _rightSlot;

    private readonly IProviderVisibilityStore _visibilityStore;
    private readonly IUsageStore _usageStore;
    private readonly Dictionary<DisplayProvider, ProviderUsageViewModel> _providerModels = new();

    public UsagePageViewModel() : this(
        ProviderVisibilityStore.Shared,
        UsageStore.Shared)
    {
    }

    public UsagePageViewModel(
        IProviderVisibilityStore visibilityStore,
        IUsageStore usageStore)
    {
        _visibilityStore = visibilityStore ?? throw new ArgumentNullException(nameof(visibilityStore));
        _usageStore = usageStore ?? throw new ArgumentNullException(nameof(usageStore));

        foreach (var provider in DisplayProviders.All)
        {
            _providerModels[provider] = new ProviderUsageViewModel(provider);
        }

        _visibilityStore.PropertyChanged += OnVisibilityChanged;
        _usageStore.PropertyChanged += OnUsageChanged;

        UpdateLayout();
    }

    private void OnVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateLayout();
    }

    private void OnUsageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateUsageData();
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

        UpdateUsageData();
    }

    private void UpdateUsageData()
    {
        var claudeUsage = _usageStore.Claude;
        var codexUsage = _usageStore.Codex;

        var claudeUnhealthy = claudeUsage.FiveHour.Error is not null || claudeUsage.Weekly.Error is not null;
        var codexUnhealthy = codexUsage.FiveHour.Error is not null || codexUsage.Weekly.Error is not null;

        if (_providerModels.TryGetValue(DisplayProvider.Claude, out var claudeModel))
        {
            claudeModel.Usage = claudeUsage;
            claudeModel.IsReauthRequired = claudeUnhealthy;
        }

        if (_providerModels.TryGetValue(DisplayProvider.Codex, out var codexModel))
        {
            codexModel.Usage = codexUsage;
            codexModel.IsReauthRequired = codexUnhealthy;
        }

        ClaudeReauthVisible = LeftSlot?.Provider == DisplayProvider.Claude && (LeftSlot?.IsReauthRequired ?? false)
                           || RightSlot?.Provider == DisplayProvider.Claude && (RightSlot?.IsReauthRequired ?? false);
    }

    [RelayCommand]
    private void Refresh()
    {
        _usageStore.Refresh();
    }

    [RelayCommand]
    private void StartClaudeReauth()
    {
        ReauthFlow.Run(AgentIsland.Core.TriggerTool.Claude);
    }

    public void Dispose()
    {
        _visibilityStore.PropertyChanged -= OnVisibilityChanged;
        _usageStore.PropertyChanged -= OnUsageChanged;
    }
}
