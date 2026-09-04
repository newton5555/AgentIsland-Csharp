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
    /// Short text for quota percentage (e.g. "85%").
    /// Strictly represents AppUsage rate limits.
    /// </summary>
    public string QuotaText
    {
        get => _quotaText;
        set
        {
            if (SetProperty(ref _quotaText, value))
            {
                OnPropertyChanged(nameof(HasQuotaText));
                OnPropertyChanged(nameof(MetricsText));
            }
        }
    }

    private string _balanceText = string.Empty;

    /// <summary>
    /// Short text for monetary account balance (e.g. "¥12.50", "$5.00").
    /// Strictly represents monetary balance, separate from QuotaText (Usage) and CostText (Spend/Tokens).
    /// </summary>
    public string BalanceText
    {
        get => _balanceText;
        set
        {
            if (SetProperty(ref _balanceText, value))
            {
                OnPropertyChanged(nameof(HasBalanceText));
                OnPropertyChanged(nameof(MetricsText));
            }
        }
    }

    /// <summary>
    /// Backward-compatible primary metrics text.
    /// </summary>
    public string MetricsText => HasQuotaText ? QuotaText : (HasBalanceText ? BalanceText : CostText);

    /// <summary>
    /// Indicates whether a non-empty quota percentage text is present.
    /// </summary>
    public bool HasQuotaText => !string.IsNullOrWhiteSpace(_quotaText);

    /// <summary>
    /// Indicates whether a non-empty monetary balance text is present.
    /// </summary>
    public bool HasBalanceText => !string.IsNullOrWhiteSpace(_balanceText);

    private string _costText = string.Empty;

    /// <summary>
    /// Short text for cost spend or token count (e.g. "$12.50", "1.5k tok").
    /// Kept strictly separate from rate limit window QuotaText.
    /// </summary>
    public string CostText
    {
        get => _costText;
        set
        {
            if (SetProperty(ref _costText, value))
            {
                OnPropertyChanged(nameof(HasCostText));
                OnPropertyChanged(nameof(MetricsText));
            }
        }
    }

    /// <summary>
    /// Indicates whether a non-empty cost or token count text is present.
    /// </summary>
    public bool HasCostText => !string.IsNullOrWhiteSpace(_costText);

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
