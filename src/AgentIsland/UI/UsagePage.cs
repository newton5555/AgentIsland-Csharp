using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentIsland.Model;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;
using AgentIsland.Usage;

namespace AgentIsland.UI;

/// The usage data row: whichever providers hold the island's slots get a
/// REAL tile column, separated by a vertical hairline, with an inline Claude
/// re-auth escape hatch when the stored token can't satisfy the usage
/// endpoint.
///
/// Five providers, one rendering path. Guests used to get a skinny strip
/// instead of tiles, which left the whole data area an empty void the moment
/// a guests-only pair was selected (owner screenshot, 2026-08-08).
public sealed class UsagePage : Border
{
    private readonly Dictionary<DisplayProvider, ProviderChartsBlock> _blocks = new();
    private readonly Dictionary<DisplayProvider, UIElement> _badges = new();
    private readonly Button _reauth;
    private readonly Border _hairline;
    private readonly TextBlock _bothHidden;

    public UsagePage()
    {
        Padding = new Thickness(22, 12, 22, 6);
        var grid = new Grid();
        Child = grid;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _hairline = new Border
        {
            Width = 1,
            Margin = new Thickness(0, 8, 0, 8),
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(IslandColors.White(0.06), 0.5),
                    new GradientStop(Colors.Transparent, 1),
                },
                new Point(0, 0),
                new Point(0, 1)),
        };
        Grid.SetColumn(_hairline, 1);
        grid.Children.Add(_hairline);

        _reauth = MakeReauthButton();

        // Every provider owns a column and a nameplate up front; a slot
        // change only re-columns and re-shows them, so nothing in this deep
        // visual tree is ever rebuilt mid-flight.
        foreach (var provider in DisplayProviders.All)
        {
            var block = new ProviderChartsBlock(
                provider,
                PrimaryLabelKey(provider),
                SecondaryLabelKey(provider),
                // Per-provider seeds keep each Spark wave distinct and stable
                // whichever flank the provider lands on. Claude 1/2 and
                // Codex 3/4 are the seeds those two blocks already used.
                seed: 1 + provider.SlotOrder() * 2,
                extra: provider == DisplayProvider.Claude ? _reauth : null)
            {
                Visibility = Visibility.Collapsed,
            };
            _blocks[provider] = block;
            grid.Children.Add(block);

            var badge = new SoloProviderBadge(provider) { Visibility = Visibility.Collapsed };
            _badges[provider] = badge;
            grid.Children.Add(badge);
        }

        // macOS splits this into a headline plus the route back (it is a
        // Settings destination, not a sentence), and with five providers "both
        // hidden" is no longer the only way to reach an empty island — a
        // selected guest that isn't installed on this machine gets here too.
        _bothHidden = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(0.35)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 360,
            Visibility = Visibility.Collapsed,
            Text = Localization.L10n.Tr("Both providers hidden")
                + "\n"
                + Localization.L10n.Tr("Re-enable in Settings → Providers"),
        };
        Grid.SetColumnSpan(_bothHidden, 3);
        grid.Children.Add(_bothHidden);

        // PagedContent recreates this page whenever the visible screens
        // change; detach on Unloaded or each dead instance stays pinned by
        // the singleton stores and keeps running Update forever.
        System.ComponentModel.PropertyChangedEventHandler onUpdate =
            (_, _) => Dispatcher.BeginInvoke(Update);
        UsageStore.Shared.PropertyChanged += onUpdate;
        StylePreferenceStore.Shared.PropertyChanged += onUpdate;
        QuotaDisplayModeStore.Shared.PropertyChanged += onUpdate;
        ProviderVisibilityStore.Shared.PropertyChanged += onUpdate;
        AntigravityUsageStore.Shared.PropertyChanged += onUpdate;
        GrokUsageStore.Shared.PropertyChanged += onUpdate;
        CursorUsageStore.Shared.PropertyChanged += onUpdate;
        Unloaded += (_, _) =>
        {
            UsageStore.Shared.PropertyChanged -= onUpdate;
            StylePreferenceStore.Shared.PropertyChanged -= onUpdate;
            QuotaDisplayModeStore.Shared.PropertyChanged -= onUpdate;
            ProviderVisibilityStore.Shared.PropertyChanged -= onUpdate;
            AntigravityUsageStore.Shared.PropertyChanged -= onUpdate;
            GrokUsageStore.Shared.PropertyChanged -= onUpdate;
            CursorUsageStore.Shared.PropertyChanged -= onUpdate;
        };
        Update();
    }

    private Button MakeReauthButton()
    {
        var button = new Button
        {
            Content = Localization.L10n.Tr("Re-authenticate"),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.72)),
            Background = IslandColors.Brush(IslandColors.White(0.04)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Visibility = Visibility.Collapsed,
        };
        button.Click += (_, _) => ReauthFlow.Run(Core.TriggerTool.Claude);
        return button;
    }

    /// Speak the window the provider actually meters: Claude's 5h, Codex's 5h,
    /// Gemini's daily bucket, Grok's weekly pool, Cursor's billing
    /// cycle. Guest windows are synthesized without a PeriodSeconds, so these
    /// keys are what the tiles print — ChartTile's derived label has no
    /// 30-day branch and would otherwise call Cursor's cycle a "week".
    private static string PrimaryLabelKey(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Antigravity => "5h",
        DisplayProvider.Grok => "week",
        DisplayProvider.Cursor => "30d",
        _ => "5h",
    };

    private static string SecondaryLabelKey(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Antigravity => "week",
        DisplayProvider.Grok => "week",
        DisplayProvider.Cursor => "30d",
        _ => "week",
    };

    private void Update()
    {
        var store = UsageStore.Shared;
        var style = StylePreferenceStore.Shared.Style;
        var slots = ProviderVisibilityStore.Shared.SlotProviders;

        foreach (var provider in DisplayProviders.All)
        {
            _blocks[provider].Visibility = Visibility.Collapsed;
            _badges[provider].Visibility = Visibility.Collapsed;
        }

        if (slots.Count >= 2)
        {
            Place(_blocks[slots[0]], 0);
            Place(_blocks[slots[1]], 2);
        }
        else if (slots.Count == 1)
        {
            // Solo split: the live column keeps the flank its island logo
            // holds, and the same provider's nameplate fills the freed half.
            var solo = slots[0];
            var leading = solo.SoloLogoFlankIsLeading();
            Place(_blocks[solo], leading ? 0 : 2);
            Place(_badges[solo], leading ? 2 : 0);
        }

        _hairline.Visibility = slots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _bothHidden.Visibility = slots.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        // Only the columns on screen refresh. A collapsed block would still
        // run its meter animations — three hidden providers' worth of
        // per-frame recompositing on every poll — and a block that takes a
        // slot gets its numbers in this same pass, because the visibility
        // store's own change is what brought us here.
        foreach (var provider in DisplayProviders.All)
        {
            var block = _blocks[provider];
            if (block.Visibility != Visibility.Visible) continue;
            block.Update(UsageFor(provider), style);
        }

        // Keep a manual Claude auth escape hatch available whenever the
        // Claude usage fetch is unhealthy — even when the CLI can't be
        // located, the button explains the manual path instead of stranding
        // the user with a bare caption. It rides inside the Claude column, so
        // a Claude-less island hides it for free.
        var claudeUnhealthy = store.Claude.FiveHour.Error is not null
            || store.Claude.Weekly.Error is not null;
        _reauth.Visibility = claudeUnhealthy ? Visibility.Visible : Visibility.Collapsed;
        _reauth.Content = store.ClaudeReauthInProgress
            ? Localization.L10n.Tr("waiting for login…")
            : Localization.L10n.Tr("Re-authenticate");
        _reauth.IsEnabled = !store.ClaudeReauthInProgress;
    }

    private static void Place(UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        element.Visibility = Visibility.Visible;
    }

    /// Shared with the island bar — the slots render whichever providers
    /// hold them, through the same per-provider assembly the panel uses.
    internal static AppUsage UsageFor(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => UsageStore.Shared.Claude,
        DisplayProvider.Codex => UsageStore.Shared.Codex,
        DisplayProvider.Antigravity => AntigravityUsage(),
        DisplayProvider.Grok => GrokUsage(),
        DisplayProvider.Cursor => CursorUsage(),
        _ => AppUsage.Empty,
    };

    /// Antigravity surfaces Gemini's quota: a 5h rolling pool and a weekly pool.
    /// Paid tiers provide both; free tiers may report only weekly.
    /// When both exist, they are displayed side-by-side (5h and week).
    /// If only one exists, it cleanly falls back to single-window display.
    private static AppUsage AntigravityUsage()
    {
        var store = AntigravityUsageStore.Shared;
        var snapshot = store.Snapshot;
        var five = snapshot?.FiveHour;
        var week = snapshot?.Weekly;

        if (five is not null && week is not null)
        {
            return new AppUsage(
                new WindowUsage(
                    five.UsedPercent, five.ResetAt, store.StatusCaption, five.PeriodSeconds),
                new WindowUsage(
                    week.UsedPercent, week.ResetAt, store.StatusCaption, week.PeriodSeconds),
                store.TierBadge?.ToLowerInvariant());
        }

        var single = five ?? week ?? snapshot?.Primary;
        return new AppUsage(
            new WindowUsage(
                single?.UsedPercent ?? 0, single?.ResetAt, store.StatusCaption, single?.PeriodSeconds),
            WindowUsage.Unknown,
            store.TierBadge?.ToLowerInvariant());
    }

    /// Grok meters one weekly credit pool; the monthly dollar budget is a
    /// caption elsewhere, not a second quota window.
    private static AppUsage GrokUsage()
    {
        var grok = GrokUsageStore.Shared;
        var snapshot = grok.Snapshot;
        return new AppUsage(
            new WindowUsage(snapshot?.WeeklyUsedPercent ?? 0, snapshot?.WeeklyPeriodEnd, grok.ErrorCaption),
            WindowUsage.Unknown,
            grok.AuthModeBadge?.ToLowerInvariant());
    }

    /// Cursor reports a single included-usage pool per billing cycle. The
    /// store's own Window property carries CycleSeconds, which the tile would
    /// print as "week"; the cycle length is expressed by the "30d" label key
    /// instead.
    private static AppUsage CursorUsage()
    {
        var cursor = CursorUsageStore.Shared;
        var snapshot = cursor.Snapshot;
        return new AppUsage(
            new WindowUsage(snapshot?.UsedPercent ?? 0, snapshot?.PeriodEnd, cursor.ErrorCaption),
            WindowUsage.Unknown,
            cursor.PlanBadge?.ToLowerInvariant());
    }
}

/// One provider's tile column: the primary window, the secondary when the
/// provider reports two, and an optional escape hatch underneath (Claude's
/// re-auth button). Colour comes from ProviderIdentity, so a guest column can
/// never inherit Codex's blue.
internal sealed class ProviderChartsBlock : StackPanel
{
    private readonly ChartTile _primary;
    private readonly ChartTile _secondary;

    internal ProviderChartsBlock(
        DisplayProvider provider,
        string primaryLabelKey,
        string secondaryLabelKey,
        int seed,
        UIElement? extra)
    {
        Orientation = Orientation.Vertical;
        var accent = ProviderIdentity.Accent(provider);
        _primary = new ChartTile(accent, primaryLabelKey, seed);
        _secondary = new ChartTile(accent, secondaryLabelKey, seed + 1);

        var tiles = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_primary, 0);
        Grid.SetColumn(_secondary, 2);
        tiles.Children.Add(_primary);
        tiles.Children.Add(_secondary);

        Children.Add(tiles);
        if (extra is not null)
        {
            Children.Add(extra);
        }
    }

    internal void Update(AppUsage usage, ChartStyle style)
    {
        _primary.Update(usage.FiveHour, style);
        _secondary.Update(usage.Weekly, style);

        // A provider reporting one window gets one tile spanning the block —
        // Codex's primary-only shape, and every guest's shape — instead of a
        // permanent "no data" ghost beside the real one.
        var single = usage.SecondaryMissing;
        _secondary.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(_primary, single ? 3 : 1);
    }
}
