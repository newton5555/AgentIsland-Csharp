using System.Globalization;
using AgentIsland.Avalonia.Converters;
using Avalonia.Media;

namespace AgentIsland.Avalonia.ViewModels;

/// <summary>
/// Visual presentation state for one provider slot in the floating island.
/// Decoupled from runtime scanners, credentials, and platform stores.
/// </summary>
public class ProviderSlotViewModel : ViewModelBase
{
    private string _name = string.Empty;
    private string _shortId = string.Empty;
    private string _colorKey = "IslandCodexBrush";
    private ProviderSlotStatus _status = ProviderSlotStatus.Idle;
    private string _quotaText = string.Empty;
    private string? _statusText;

    /// <summary>
    /// Provider display name (e.g. "Codex", "DeepSeek", "Antigravity").
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>
    /// Alias for Name to support different consumer conventions.
    /// </summary>
    public string DisplayName => Name;

    /// <summary>
    /// Short identifier for the badge (e.g. "C", "D", "A", "G").
    /// </summary>
    public string ShortId
    {
        get => _shortId;
        set
        {
            if (SetProperty(ref _shortId, value))
            {
                OnPropertyChanged(nameof(Badge));
            }
        }
    }

    /// <summary>
    /// Alias for ShortId.
    /// </summary>
    public string Badge => ShortId;

    /// <summary>
    /// Color resource key or color hex string (e.g. "IslandCodexBrush", "IslandDeepSeekBrush", "#7B61FF").
    /// </summary>
    public string ColorKey
    {
        get => _colorKey;
        set
        {
            if (SetProperty(ref _colorKey, value))
            {
                OnPropertyChanged(nameof(AccentBrush));
            }
        }
    }

    /// <summary>
    /// Resolved Avalonia brush for the badge or accent.
    /// </summary>
    public IBrush? AccentBrush => ColorKeyToBrushConverter.ResolveBrush(ColorKey);

    /// <summary>
    /// Activity state for visual indicator and slot styling.
    /// </summary>
    public ProviderSlotStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(IsNeedsYou));
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(StatusClass));
                if (_statusText == null)
                {
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }
    }

    /// <summary>
    /// Short text for quota percentage, token count, or balance (e.g. "85%", "$12.50").
    /// </summary>
    public string QuotaText
    {
        get => _quotaText;
        set
        {
            if (SetProperty(ref _quotaText, value))
            {
                OnPropertyChanged(nameof(HasQuotaText));
                OnPropertyChanged(nameof(BalanceText));
                OnPropertyChanged(nameof(MetricsText));
            }
        }
    }

    /// <summary>
    /// Alias for QuotaText.
    /// </summary>
    public string BalanceText => QuotaText;

    /// <summary>
    /// Alias for QuotaText.
    /// </summary>
    public string MetricsText => QuotaText;

    /// <summary>
    /// Indicates whether a non-empty quota or balance text is present.
    /// </summary>
    public bool HasQuotaText => !string.IsNullOrWhiteSpace(_quotaText);

    /// <summary>
    /// Status description text (e.g. "Ready", "Working", "Needs You", "Error").
    /// Defaults to the standard string for the current Status if not explicitly set.
    /// </summary>
    public string StatusText
    {
        get => _statusText ?? DefaultStatusText(Status);
        set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsIdle => Status == ProviderSlotStatus.Idle;
    public bool IsWorking => Status == ProviderSlotStatus.Working;
    public bool IsNeedsYou => Status == ProviderSlotStatus.NeedsYou;
    public bool IsError => Status == ProviderSlotStatus.Error;

    /// <summary>
    /// CSS/AXAML style class name corresponding to the current state.
    /// </summary>
    public string StatusClass => Status switch
    {
        ProviderSlotStatus.Working => "status-working",
        ProviderSlotStatus.NeedsYou => "status-needs-you",
        ProviderSlotStatus.Error => "status-error",
        _ => "status-idle",
    };

    public static string DefaultStatusText(ProviderSlotStatus status) => status switch
    {
        ProviderSlotStatus.Idle => "Ready",
        ProviderSlotStatus.Working => "Working",
        ProviderSlotStatus.NeedsYou => "Needs You",
        ProviderSlotStatus.Error => "Error",
        _ => "Ready",
    };
}
