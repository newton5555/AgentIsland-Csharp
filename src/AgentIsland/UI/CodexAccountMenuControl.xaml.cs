using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AgentIsland.Core;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

public partial class CodexAccountMenuControl : UserControl
{
    public event Action? RequestRefresh;
    public event Action? SaveAccountRequested;

    private readonly List<MenuItem> _dynamicAccountItems = new();
    private bool _menuPrepared;

    public ContextMenu Menu => AccountContextMenu;

    public MenuItem NoSavedAccountsMenuItem => NoSavedAccountsItem;
    public MenuItem RemoveAccountMenu => RemoveAccountMenuItem;
    public MenuItem SaveAccountItem => SaveAccountMenuItem;
    public MenuItem AutoSwitchItem => AutoSwitchMenuItem;

    public CodexAccountMenuControl()
    {
        InitializeComponent();
        AccountLabel.Text = L10n.Tr("Accounts");

        NoSavedAccountsItem.Header = L10n.Tr("No saved accounts yet");
        RemoveAccountMenuItem.Header = L10n.Tr("Remove saved account");
        SaveAccountMenuItem.Header = L10n.Tr("Save current account…");
        SaveAccountMenuItem.Click += (_, _) => SaveAccountRequested?.Invoke();

        AutoSwitchMenuItem.Header = L10n.Tr("Auto-switch when exhausted");
        AutoSwitchMenuItem.Click += (_, _) => CodexAccountSwitcher.AutoSwitchEnabled = AutoSwitchMenuItem.IsChecked;

        AccountButton.MouseLeftButtonUp += (_, args) =>
        {
            OpenMenu();
            args.Handled = true;
        };

        AccountButton.ContextMenuOpening += (_, _) =>
        {
            PrepareForOpening();
        };

        RefreshStatus();
    }

    /// Background refresh: ONLY updates the button visual (color and tooltip).
    /// Preserves existing menu items and open menu state without replacing items under the mouse.
    public void RefreshStatus()
    {
        var switched = UsageStore.Shared.CodexAutoSwitched;
        AccountLabel.Foreground = IslandColors.Brush(switched is null
            ? IslandColors.White(0.85)
            : IslandColors.Alpha(IslandColors.AlertAmber, 0.9));
        AccountButton.ToolTip = switched is null
            ? L10n.Tr("Switch Codex account")
            : L10n.TrFormat("Auto-switched to {0}", switched);

        if (_menuPrepared)
        {
            AutoSwitchMenuItem.IsChecked = CodexAccountSwitcher.AutoSwitchEnabled;
        }
    }

    /// Explicitly prepares or refreshes the menu data (called on open or for acceptance testing without opening popup).
    public void PrepareMenu()
    {
        _menuPrepared = true;

        foreach (var item in _dynamicAccountItems)
        {
            AccountContextMenu.Items.Remove(item);
        }
        _dynamicAccountItems.Clear();
        RemoveAccountMenuItem.Items.Clear();

        NoSavedAccountsItem.Header = L10n.Tr("No saved accounts yet");
        RemoveAccountMenuItem.Header = L10n.Tr("Remove saved account");
        SaveAccountMenuItem.Header = L10n.Tr("Save current account…");
        AutoSwitchMenuItem.Header = L10n.Tr("Auto-switch when exhausted");
        AutoSwitchMenuItem.IsChecked = CodexAccountSwitcher.AutoSwitchEnabled;

        var accounts = CodexAccountSwitcher.Accounts();
        var active = CodexAccountSwitcher.ActiveLabel();

        if (accounts.Count == 0)
        {
            NoSavedAccountsItem.Visibility = Visibility.Visible;
            AccountsSeparator.Visibility = Visibility.Collapsed;
            RemoveAccountMenuItem.Visibility = Visibility.Collapsed;
            RemoveSeparator.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoSavedAccountsItem.Visibility = Visibility.Collapsed;
            AccountsSeparator.Visibility = Visibility.Visible;
            RemoveAccountMenuItem.Visibility = Visibility.Visible;
            RemoveSeparator.Visibility = Visibility.Visible;

            var insertIndex = AccountContextMenu.Items.IndexOf(AccountsSeparator);
            if (insertIndex < 0) insertIndex = 0;

            var itemStyle = TryFindResource("CodexAccountItemStyle") as Style;

            foreach (var account in accounts)
            {
                var captured = account;
                var item = new MenuItem
                {
                    Header = string.Equals(account.Label, active, StringComparison.Ordinal)
                        ? "✓ " + account.Label
                        : account.Label,
                    Style = itemStyle,
                };
                item.Click += (_, _) =>
                {
                    if (!CodexAccountSwitcher.Activate(captured)) return;
                    UsageStore.Shared.CodexAutoSwitched = null;
                    UsageStore.Shared.Refresh();
                    RequestRefresh?.Invoke();
                };
                AccountContextMenu.Items.Insert(insertIndex++, item);
                _dynamicAccountItems.Add(item);

                var removeChild = new MenuItem
                {
                    Header = account.Label,
                    Style = itemStyle,
                };
                removeChild.Click += (_, _) =>
                {
                    CodexAccountSwitcher.Forget(captured);
                    RequestRefresh?.Invoke();
                };
                RemoveAccountMenuItem.Items.Add(removeChild);
            }
        }
    }

    /// RebuildMenu synonym for backwards compatibility / acceptance harness calls
    public void RebuildMenu() => PrepareMenu();

    private void PrepareForOpening()
    {
        RefreshStatus();
        PrepareMenu();
    }

    public void OpenMenu()
    {
        PrepareForOpening();
        AccountContextMenu.PlacementTarget = AccountButton;
        AccountContextMenu.Placement = PlacementMode.Bottom;
        AccountContextMenu.IsOpen = true;
    }
}
