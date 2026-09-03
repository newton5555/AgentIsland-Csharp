using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentIsland.Core;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

public partial class ProviderRowControl : UserControl
{
    public DisplayProvider Provider { get; }

    public event Action<bool>? SlotRefusalChanged;
    public event Action? RefreshRequested;
    public event Action? ClaudePasteLoginRequested;
    public event Action? CodexSaveAccountRequested;

    private bool _hovered;

    public ProviderRowControl() : this(DisplayProvider.Claude)
    {
    }

    public ProviderRowControl(DisplayProvider provider)
    {
        Provider = provider;
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

        SlotToggle.IsOn = ProviderVisibilityStore.Shared.IsEnabled(provider);
        SlotToggle.Toggled += enabled =>
        {
            if (!ProviderVisibilityStore.Shared.SetEnabled(provider, enabled))
            {
                SlotToggle.IsOn = ProviderVisibilityStore.Shared.IsEnabled(provider);
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
        SlotToggle.IsOn = ProviderVisibilityStore.Shared.IsEnabled(Provider);
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
                var store = UsageStore.Shared;
                PasteLoginButton.Visibility = store.ClaudeReauthFailureCaption is not null
                    && !store.ClaudeReauthInProgress
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            var available = Provider == DisplayProvider.Claude
                ? ClaudeReauthAvailable()
                : CodexReauthAvailable();
            var waiting = Provider == DisplayProvider.Claude
                ? UsageStore.Shared.ClaudeReauthInProgress
                : UsageStore.Shared.CodexReauthInProgress;
            ReauthButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            ReauthButton.Label = waiting ? L10n.Tr("waiting for login…") : L10n.Tr("Re-authenticate");
        }

        Paint();
    }

    private void Paint()
    {
        var enabledNow = ProviderVisibilityStore.Shared.IsEnabled(Provider);
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

    private static void KickGuestRefresh(DisplayProvider provider)
    {
        switch (provider)
        {
            case DisplayProvider.Antigravity:
                AntigravityUsageStore.Shared.KickRefresh();
                break;
            case DisplayProvider.Grok:
                GrokUsageStore.Shared.KickRefresh();
                break;
            case DisplayProvider.Cursor:
                CursorUsageStore.Shared.KickRefresh();
                break;
            default:
                break;
        }
    }

    private static string ProviderStatus(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => ClaudeStatus(),
        DisplayProvider.Codex => ProviderSubtitle(UsageStore.Shared.Codex),
        DisplayProvider.Antigravity => AntigravityStatus(),
        DisplayProvider.Grok => GrokStatus(),
        DisplayProvider.Cursor => CursorStatus(),
        _ => string.Empty,
    };

    private static string? ProviderChip(DisplayProvider provider)
    {
        var visibility = ProviderVisibilityStore.Shared;
        return provider switch
        {
            DisplayProvider.Claude => UsageStore.Shared.Claude.Plan?.ToUpperInvariant(),
            DisplayProvider.Codex => UsageStore.Shared.Codex.Plan?.ToUpperInvariant(),
            DisplayProvider.Antigravity =>
                visibility.AntigravityDetected ? AntigravityUsageStore.Shared.TierBadge : null,
            DisplayProvider.Grok => visibility.GrokDetected ? GrokUsageStore.Shared.AuthModeBadge : null,
            DisplayProvider.Cursor => visibility.CursorDetected ? CursorUsageStore.Shared.PlanBadge : null,
            _ => null,
        };
    }

    private static string ProviderSubtitle(AppUsage usage)
    {
        var synced = UsageStore.Shared.LastUpdated is { } updated
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

    private static string AntigravityStatus()
    {
        var store = AntigravityUsageStore.Shared;
        if (!ProviderVisibilityStore.Shared.AntigravityDetected)
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

    private static string ClaudeStatus()
    {
        var subtitle = ProviderSubtitle(UsageStore.Shared.Claude);
        var store = UsageStore.Shared;
        if (store.ClaudeReauthFailureCaption is not { } reason || store.ClaudeReauthInProgress)
        {
            return subtitle;
        }
        return $"{subtitle} · ⚠ {ErrorDisplay.Localize(reason)}";
    }

    private static string GrokStatus()
    {
        var store = GrokUsageStore.Shared;
        if (!ProviderVisibilityStore.Shared.GrokDetected)
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

    private static string CursorStatus()
    {
        var store = CursorUsageStore.Shared;
        if (!ProviderVisibilityStore.Shared.CursorDetected)
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

    private static string GuestSync(DateTimeOffset? updated) => updated is { } stamp
        ? L10n.TrFormat("synced {0}", Formatting.RelativeAgo(DateTimeOffset.Now - stamp, L10n.IsChinese))
        : L10n.Tr("idle");

    private static int Percent(double fraction) => Formatting.PercentInt(fraction);

    private static bool ClaudeReauthAvailable()
    {
        if (UsageStore.Shared.ClaudeReauthInProgress) return true;
        var usage = UsageStore.Shared.Claude;
        return ClaudeCredentials.IsAuthRecoverableError(usage.FiveHour.Error)
            || ClaudeCredentials.IsAuthRecoverableError(usage.Weekly.Error);
    }

    private static bool CodexReauthAvailable()
    {
        if (UsageStore.Shared.CodexReauthInProgress) return true;
        var usage = UsageStore.Shared.Codex;
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
