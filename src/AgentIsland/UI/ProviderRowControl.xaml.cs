using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentIsland.Core;
using AgentIsland.Backend.Cost;
using AgentIsland.Backend.Settings;
using AgentIsland.Backend.Usage;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Providers;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

public partial class ProviderRowControl : UserControl
{
    public DisplayProvider Provider { get; }
    public event Action<bool>? SlotRefusalChanged;
    public event Action? RefreshRequested;
    public event Action? ClaudePasteLoginRequested;
    public event Action? CodexSaveAccountRequested;

    private readonly IProviderVisibilityStore _visibilityStore;
    private readonly IUsageStore _usageStore;
    private readonly IAntigravityUsageStore _antigravityUsageStore;
    private readonly IGrokUsageStore _grokUsageStore;
    private readonly ICursorUsageStore _cursorUsageStore;
    private readonly IDeepSeekBalanceStore _deepSeekBalanceStore;
    private readonly ICostStore _costStore;

    private bool _hovered;

    public ProviderRowControl() : this(DisplayProvider.Claude)
    {
    }

    public ProviderRowControl(
        DisplayProvider provider,
        IProviderVisibilityStore? visibilityStore = null,
        IUsageStore? usageStore = null,
        IAntigravityUsageStore? antigravityUsageStore = null,
        IGrokUsageStore? grokUsageStore = null,
        ICursorUsageStore? cursorUsageStore = null,
        IDeepSeekBalanceStore? deepSeekBalanceStore = null,
        ICostStore? costStore = null)
    {
        Provider = provider;
        var sp = App.Instance?.Services;
        _visibilityStore = visibilityStore ?? (sp?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore) ?? new ProviderVisibilityStore();
        _usageStore = usageStore ?? (sp?.GetService(typeof(IUsageStore)) as IUsageStore) ?? new UsageStore();
        _antigravityUsageStore = antigravityUsageStore ?? (sp?.GetService(typeof(IAntigravityUsageStore)) as IAntigravityUsageStore) ?? new AntigravityUsageStore(_visibilityStore);
        _grokUsageStore = grokUsageStore ?? (sp?.GetService(typeof(IGrokUsageStore)) as IGrokUsageStore) ?? new GrokUsageStore(_visibilityStore);
        _cursorUsageStore = cursorUsageStore ?? (sp?.GetService(typeof(ICursorUsageStore)) as ICursorUsageStore) ?? new CursorUsageStore(_visibilityStore);
        _deepSeekBalanceStore = deepSeekBalanceStore ?? (sp?.GetService(typeof(IDeepSeekBalanceStore)) as IDeepSeekBalanceStore) ?? new DeepSeekBalanceStore(_visibilityStore);
        _costStore = costStore ?? (sp?.GetService(typeof(ICostStore)) as ICostStore) ?? new CostStore();

        InitializeComponent();

        ProviderName.Text = provider.DisplayName();
        DragHandle.ToolTip = L10n.Tr("Drag to reorder");

        MarkHost.Children.Add(ProviderMarks.Mark(provider, 20));

        if (provider.HasFullMonitoring())
        {
            var tool = provider.ToTriggerTool();
            if (provider == DisplayProvider.Codex)
            {
                CodexAccountMenu.Visibility = Visibility.Visible;
                CodexAccountMenu.RequestRefresh += () => RefreshRequested?.Invoke();
                CodexAccountMenu.SaveAccountRequested += () => CodexSaveAccountRequested?.Invoke();
            }
            if (provider == DisplayProvider.Claude)
            {
                PasteLoginButton.Label = L10n.Tr("Sign in with a code");
                PasteLoginButton.Clicked += () => ClaudePasteLoginRequested?.Invoke();
            }
            ReauthButton.Label = L10n.Tr("Re-authenticate");
            ReauthButton.Clicked += () => ReauthFlow.Run(tool);
        }

        SlotToggle.IsOn = _visibilityStore.IsEnabled(provider);
        SlotToggle.Toggled += enabled =>
        {
            if (!_visibilityStore.SetEnabled(provider, enabled))
            {
                SlotToggle.IsOn = _visibilityStore.IsEnabled(provider);
                SlotRefusalChanged?.Invoke(true);
                return;
            }
            SlotRefusalChanged?.Invoke(false);
            if (enabled) KickGuestRefresh(provider);
            RefreshRequested?.Invoke();
        };

        RowRoot.MouseEnter += (_, _) =>
        {
            _hovered = true;
            Paint();
        };
        RowRoot.MouseLeave += (_, _) =>
        {
            _hovered = false;
            Paint();
        };

        // Setup brand wash gradient stops
        var washStops = new GradientStopCollection();
        var stops = ProviderIdentity.BrandStops(provider);
        for (var i = 0; i < stops.Count; i++)
        {
            washStops.Add(new GradientStop(
                IslandColors.Alpha(stops[i], 0.05), 0.7 * i / Math.Max(1, stops.Count - 1)));
        }
        washStops.Add(new GradientStop(Colors.Transparent, 1));
        WashBorder.Background = new LinearGradientBrush(washStops, new Point(0, 0), new Point(1, 0));

        Refresh();
    }

    public void Refresh()
    {
        SlotToggle.IsOn = _visibilityStore.IsEnabled(Provider);
        StatusText.Text = ProviderStatus(Provider);
        var badge = ProviderChip(Provider);
        PlanChipText.Text = badge ?? string.Empty;
        PlanChip.Visibility = string.IsNullOrEmpty(badge) ? Visibility.Collapsed : Visibility.Visible;

        if (Provider.HasFullMonitoring())
        {
            if (Provider == DisplayProvider.Codex)
            {
                CodexAccountMenu.RefreshStatus();
            }
            if (Provider == DisplayProvider.Claude)
            {
                var store = _usageStore;
                PasteLoginButton.Visibility = store.ClaudeReauthFailureCaption is not null
                    && !store.ClaudeReauthInProgress
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            var available = Provider == DisplayProvider.Claude
                ? ClaudeReauthAvailable()
                : CodexReauthAvailable();
            var waiting = Provider == DisplayProvider.Claude
                ? _usageStore.ClaudeReauthInProgress
                : _usageStore.CodexReauthInProgress;
            ReauthButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            ReauthButton.Label = waiting ? L10n.Tr("waiting for login…") : L10n.Tr("Re-authenticate");
        }

        Paint();
    }

    private void Paint()
    {
        var enabledNow = _visibilityStore.IsEnabled(Provider);
        var ruleOpacity = enabledNow ? (_hovered ? 1.0 : 0.85) : (_hovered ? 0.45 : 0.22);
        RuleBorder.Background = ProviderIdentity.BrandGradient(
            Provider, 1, new Point(0, 0), new Point(0, 1));

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var beat = new Duration(TimeSpan.FromMilliseconds(160));

        WashBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(_hovered ? 1 : 0, beat) { EasingFunction = ease });
        RuleBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(ruleOpacity, beat) { EasingFunction = ease });
        RuleBorder.BeginAnimation(WidthProperty,
            new DoubleAnimation(_hovered ? 3 : 2, beat) { EasingFunction = ease });
    }

    private void KickGuestRefresh(DisplayProvider provider)
    {
        switch (provider)
        {
            case DisplayProvider.Antigravity:
                _antigravityUsageStore.KickRefresh();
                break;
            case DisplayProvider.Grok:
                _grokUsageStore.KickRefresh();
                break;
            case DisplayProvider.Cursor:
                _cursorUsageStore.KickRefresh();
                break;
            case DisplayProvider.DeepSeek:
                _deepSeekBalanceStore.KickRefresh();
                break;
            default:
                break;
        }
    }

    private string ProviderStatus(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => ClaudeStatus(),
        DisplayProvider.Codex => ProviderSubtitle(_usageStore.Codex),
        DisplayProvider.Antigravity => AntigravityStatus(),
        DisplayProvider.Grok => GrokStatus(),
        DisplayProvider.Cursor => CursorStatus(),
        DisplayProvider.DeepSeek => DeepSeekStatus(),
        _ => string.Empty,
    };

    private string? ProviderChip(DisplayProvider provider)
    {
        var visibility = _visibilityStore;
        return provider switch
        {
            DisplayProvider.Claude => _usageStore.Claude.Plan?.ToUpperInvariant(),
            DisplayProvider.Codex => _usageStore.Codex.Plan?.ToUpperInvariant(),
            DisplayProvider.Antigravity =>
                visibility.AntigravityDetected ? _antigravityUsageStore.TierBadge : null,
            DisplayProvider.Grok => visibility.GrokDetected ? _grokUsageStore.AuthModeBadge : null,
            DisplayProvider.Cursor => visibility.CursorDetected ? _cursorUsageStore.PlanBadge : null,
            DisplayProvider.DeepSeek => visibility.DeepSeekDetected
                ? _deepSeekBalanceStore.Snapshot is not null ? "BALANCE" : "TOKENS"
                : null,
            _ => null,
        };
    }

    private string ProviderSubtitle(AppUsage usage)
    {
        var synced = _usageStore.LastUpdated is { } updated
            ? L10n.TrFormat("synced {0}", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese))
            : L10n.Tr("idle");
        var five = WindowCaption(usage.FiveHour);
        var week = WindowCaption(usage.Weekly);
        var caption = usage.SecondaryMissing || (five == week && five.StartsWith("⚠", StringComparison.Ordinal))
            ? five
            : $"{five} / {week}";
        return $"{synced} · {caption}";
    }

    private static string WindowCaption(WindowUsage window)
    {
        var percent = Formatting.PercentInt(window.UsedPercent);
        if (window.Error is { } error && percent == 0)
        {
            return "⚠ " + ErrorDisplay.Localize(error);
        }
        return $"{percent}%";
    }

    private string AntigravityStatus()
    {
        var store = _antigravityUsageStore;
        if (!_visibilityStore.AntigravityDetected)
        {
            return L10n.Tr("Not detected — sign in with the antigravity CLI");
        }
        var parts = new List<string> { GuestSync(store.LastUpdated) };
        if (store.StatusCaption is { } caption)
        {
            parts.Add("⚠ " + ErrorDisplay.Localize(caption));
        }
        else if (store.Snapshot is { } snapshot && (snapshot.FiveHour is not null || snapshot.Weekly is not null || snapshot.Primary is not null))
        {
            var usage = UsagePage.UsageFor(DisplayProvider.Antigravity);
            var five = WindowCaption(usage.FiveHour);
            var week = WindowCaption(usage.Weekly);
            var quotaCaption = usage.SecondaryMissing || (five == week && five.StartsWith("⚠", StringComparison.Ordinal))
                ? five
                : $"{five} / {week}";
            parts.Add(L10n.TrFormat("{0} {1}", snapshot.Primary?.ShortLabel ?? "Gemini", quotaCaption));
        }
        return string.Join(" · ", parts);
    }

    private string ClaudeStatus()
    {
        var subtitle = ProviderSubtitle(_usageStore.Claude);
        var store = _usageStore;
        if (store.ClaudeReauthFailureCaption is not { } reason || store.ClaudeReauthInProgress)
        {
            return subtitle;
        }
        return $"{subtitle} · ⚠ {ErrorDisplay.Localize(reason)}";
    }

    private string GrokStatus()
    {
        var store = _grokUsageStore;
        if (!_visibilityStore.GrokDetected)
        {
            return L10n.Tr("Not detected — sign in with the grok CLI");
        }
        var parts = new List<string> { GuestSync(store.LastUpdated) };
        if (store.ErrorCaption is { } caption)
        {
            parts.Add("⚠ " + ErrorDisplay.Localize(caption));
        }
        else if (store.Snapshot is { } snapshot)
        {
            parts.Add(L10n.TrFormat("week {0}%", Percent(snapshot.WeeklyUsedPercent)));
        }
        return string.Join(" · ", parts);
    }

    private string CursorStatus()
    {
        var store = _cursorUsageStore;
        if (!_visibilityStore.CursorDetected)
        {
            return L10n.Tr("Not detected — sign in inside Cursor");
        }
        var parts = new List<string> { GuestSync(store.LastUpdated) };
        if (store.ErrorCaption is { } caption)
        {
            parts.Add("⚠ " + ErrorDisplay.Localize(caption));
        }
        else if (store.Snapshot is { } snapshot)
        {
            parts.Add(L10n.TrFormat("cycle {0}%", Percent(snapshot.UsedPercent)));
        }
        return string.Join(" · ", parts);
    }

    private string DeepSeekStatus()
    {
        var visibility = _visibilityStore;
        if (!visibility.DeepSeekDetected)
        {
            return L10n.Tr("Not detected — create a DeepSeek Harness session");
        }

        var balance = _deepSeekBalanceStore;
        var parts = new List<string>();
        if (balance.LastUpdated is { } updated)
        {
            parts.Add(L10n.TrFormat("synced {0}", Formatting.RelativeAgo(
                DateTimeOffset.Now - updated, L10n.IsChinese)));
        }

        if (balance.Snapshot is { } snapshot)
        {
            parts.Add(L10n.TrFormat("balance {0}", DeepSeekBalanceText.Total(snapshot)));
            parts.Add(DeepSeekBalanceText.Availability(snapshot));
        }
        else if (balance.ErrorCaption is { } error)
        {
            parts.Add("⚠ " + ErrorDisplay.Localize(error));
        }
        else if (!balance.Configured)
        {
            parts.Add(L10n.Tr("no deepseek api key"));
        }
        else
        {
            parts.Add(L10n.Tr("account balance not fetched"));
        }

        var today = _costStore.DeepSeek.TodayTokens;
        if (today > 0)
        {
            parts.Add(L10n.TrFormat("{0} tokens today", Formatting.CompactTokens(today)));
        }
        return string.Join(" · ", parts);
    }

    private static string GuestSync(DateTimeOffset? updated) => updated is { } stamp
        ? L10n.TrFormat("synced {0}", Formatting.RelativeAgo(DateTimeOffset.Now - stamp, L10n.IsChinese))
        : L10n.Tr("idle");

    private static int Percent(double fraction) => Formatting.PercentInt(fraction);

    private bool ClaudeReauthAvailable()
    {
        if (_usageStore.ClaudeReauthInProgress) return true;
        var usage = _usageStore.Claude;
        return ClaudeCredentials.IsAuthRecoverableError(usage.FiveHour.Error)
            || ClaudeCredentials.IsAuthRecoverableError(usage.Weekly.Error);
    }

    private bool CodexReauthAvailable()
    {
        if (_usageStore.CodexReauthInProgress) return true;
        var usage = _usageStore.Codex;
        if (!MentionsAuthFailure(usage.FiveHour.Error) && !MentionsAuthFailure(usage.Weekly.Error))
        {
            return false;
        }
        return CodexCredentials.CanPromptReauth();
    }

    private static bool MentionsAuthFailure(string? message) =>
        message is not null
        && (message.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("login", StringComparison.OrdinalIgnoreCase)
            || message.Contains("401", StringComparison.Ordinal));
}
