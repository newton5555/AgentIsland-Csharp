using System.Windows;
using System.Windows.Controls;
using AgentIsland.Core;
using AgentIsland.Backend.Cost;
using AgentIsland.Backend.Settings;
using AgentIsland.Backend.Usage;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Localization;

namespace AgentIsland.UI;

public partial class ProvidersSettingsPage : UserControl
{
    public static string TokensSectionLabel => L10n.Tr("Tokens").ToUpperInvariant();
    public static string CostSectionLabel => L10n.Tr("Cost").ToUpperInvariant();

    private readonly List<ProviderRowControl> _rows = new();
    private readonly IProviderVisibilityStore _visibilityStore;
    private readonly RefreshIntervalStore _refreshIntervalStore;
    private readonly TokenCountModeStore _tokenCountModeStore;
    private readonly ICostStore _costStore;
    private readonly IDeepSeekBalanceStore _deepSeekBalanceStore;
    private readonly IUsageStore _usageStore;

    public ProvidersSettingsPage() : this(null) { }

    public ProvidersSettingsPage(
        IProviderVisibilityStore? visibilityStore = null,
        RefreshIntervalStore? refreshIntervalStore = null,
        TokenCountModeStore? tokenCountModeStore = null,
        ICostStore? costStore = null,
        IDeepSeekBalanceStore? deepSeekBalanceStore = null,
        IUsageStore? usageStore = null)
    {
        var sp = App.Instance?.Services;
        _visibilityStore = visibilityStore ?? (sp?.GetService(typeof(IProviderVisibilityStore)) as IProviderVisibilityStore) ?? new ProviderVisibilityStore();
        _refreshIntervalStore = refreshIntervalStore ?? (sp?.GetService(typeof(RefreshIntervalStore)) as RefreshIntervalStore) ?? new RefreshIntervalStore();
        _tokenCountModeStore = tokenCountModeStore ?? (sp?.GetService(typeof(TokenCountModeStore)) as TokenCountModeStore) ?? new TokenCountModeStore();
        _costStore = costStore ?? (sp?.GetService(typeof(ICostStore)) as ICostStore) ?? new CostStore();
        _deepSeekBalanceStore = deepSeekBalanceStore ?? (sp?.GetService(typeof(IDeepSeekBalanceStore)) as IDeepSeekBalanceStore) ?? new DeepSeekBalanceStore(_visibilityStore);
        _usageStore = usageStore ?? (sp?.GetService(typeof(IUsageStore)) as IUsageStore) ?? new UsageStore();

        InitializeComponent();

        SlotNotice.Text = L10n.Tr("Pick at most two — turn one off first");

        foreach (var provider in _visibilityStore.Order)
        {
            var row = new ProviderRowControl(provider, _visibilityStore);
            row.SlotRefusalChanged += ShowSlotLimit;
            row.RefreshRequested += RefreshRows;
            row.ClaudePasteLoginRequested += StartClaudePasteLogin;
            row.CodexSaveAccountRequested += PromptParkCodexAccount;
            SortableHost.Children.Add(row);
            SortableHost.AttachRow(row);
            _rows.Add(row);
        }

        var refreshPresets = RefreshIntervalStore.Presets;
        RefreshSegmented.SetLabels(
            new[] { "5m", "15m", "30m" },
            Math.Max(0, Array.IndexOf(refreshPresets, _refreshIntervalStore.Seconds)));
        RefreshSegmented.SelectionChanged += index => _refreshIntervalStore.Seconds = refreshPresets[index];

        TokenCountSegmented.SetLabels(
            new[] { L10n.Tr("All tokens"), L10n.Tr("Input + output") },
            _tokenCountModeStore.Mode == TokenCountMode.All ? 0 : 1);
        TokenCountSegmented.SelectionChanged += index =>
        {
            _tokenCountModeStore.Mode = index == 0 ? TokenCountMode.All : TokenCountMode.Billable;
            UpdateTokenSubtitle();
        };
        UpdateTokenSubtitle();

        CostRefreshButton.Label = L10n.Tr("Refresh");
        CostRefreshButton.Clicked += () =>
        {
            _costStore.Refresh();
            _deepSeekBalanceStore.KickRefresh();
        };

        RefreshRows();
    }

    private void UpdateTokenSubtitle()
    {
        TokenCountingRow.Subtitle = _tokenCountModeStore.Mode == TokenCountMode.All
            ? "Input, output, and cache."
            : "Input and output only.";
    }

    public void ShowSlotLimit(bool refused)
    {
        SlotNotice.Visibility = refused ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RefreshRows()
    {
        // Slot header marks + count
        MarksHost.Children.Clear();
        foreach (var provider in _visibilityStore.Enabled)
        {
            var mark = ProviderMarks.Mark(provider, 12, tintOpacity: 0.9);
            ((FrameworkElement)mark).Margin = new Thickness(0, 0, 7, 0);
            MarksHost.Children.Add(mark);
        }
        CountText.Text = $"{_visibilityStore.SelectedCount} / {ProviderSelection.MaxEnabled}";

        // Provider rows
        foreach (var row in _rows)
        {
            row.Refresh();
        }

        // Cost caption
        CostCaption.Text = _costStore.LastUpdated is { } updated
            ? L10n.TrFormat("last scan {0}", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese))
            : L10n.Tr("swipe panel to view");
    }

    private void StartClaudePasteLogin()
    {
        var ticket = ClaudeCredentials.BeginPasteLogin();
        _ = ClaudeWebLogin.OpenInBrowser(ticket.Url);
        var owner = Window.GetWindow(this);
        var pasted = NamePromptWindow.Ask(
            owner,
            L10n.Tr("Sign in with a code"),
            L10n.Tr("Approve the page that just opened, then paste the code it shows here"),
            L10n.Tr("Paste the code"),
            confirmLabel: L10n.Tr("Sign in"),
            maxLength: 4096,
            extraLabel: L10n.Tr("Copy link"),
            extraAction: () =>
            {
                try { System.Windows.Clipboard.SetText(ticket.Url); }
                catch { }
            });
        if (pasted is null) return;
        _ = CompleteClaudePasteLogin(pasted, ticket);
    }

    private async Task CompleteClaudePasteLogin(string pasted, ClaudeCredentials.PasteLoginTicket ticket)
    {
        bool ok;
        try
        {
            ok = await ClaudeCredentials.CompletePasteLogin(pasted, ticket.Verifier, ticket.State);
        }
        catch
        {
            ok = false;
        }
        if (ok)
        {
            _usageStore.ClearClaudeReauthFailure();
            _usageStore.Refresh();
            RefreshRows();
            return;
        }
        IslandDialog.ShowApp(
            L10n.Tr("That code did not work"),
            L10n.Tr("Copy the whole code from the page and try once more"),
            secondaryLabel: L10n.Tr("OK"));
    }

    private void PromptParkCodexAccount()
    {
        var owner = Window.GetWindow(this);
        var label = NamePromptWindow.Ask(
            owner,
            L10n.Tr("Park current Codex account"),
            L10n.Tr("Give this login a name. It will appear in the switch menu so you can rotate back later."),
            L10n.Tr("Account label (e.g. work, personal)"),
            confirmLabel: L10n.Tr("Park"));
        if (string.IsNullOrWhiteSpace(label)) return;

        var ok = CodexAccountSwitcher.ParkCurrent(label.Trim());
        if (!ok)
        {
            IslandDialog.ShowApp(
                L10n.Tr("Could not park account"),
                L10n.Tr("Unknown error"),
                secondaryLabel: L10n.Tr("OK"));
            return;
        }
        RefreshRows();
    }
}
