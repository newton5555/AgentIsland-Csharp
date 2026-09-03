using System.Windows.Controls;
using System.Windows.Input;
using AgentIsland.Backend.Settings;
using AgentIsland.UI.Localization;

namespace AgentIsland.UI;

public sealed partial class AlertsSettingsPage : UserControl
{
    private readonly bool _initialized;

    public AlertsSettingsPage()
    {
        InitializeComponent();

        WarningLabelBlock.Text = L10n.Tr("Warning");
        CriticalLabelBlock.Text = L10n.Tr("Critical");

        var store = AlertThresholdStore.Shared;
        AlertsToggle.IsOn = store.Enabled;
        WarningField.Text = store.WarningPercent.ToString();
        CriticalField.Text = store.CriticalPercent.ToString();

        AlertsHost.Opacity = store.Enabled ? 1.0 : 0.40;
        AlertsHost.IsEnabled = store.Enabled;

        AlertsToggle.Toggled += enabled =>
        {
            if (!_initialized) return;
            store.Enabled = enabled;
            AlertsHost.Opacity = enabled ? 1.0 : 0.40;
            AlertsHost.IsEnabled = enabled;
        };

        void CommitWarning()
        {
            if (!_initialized) return;
            if (int.TryParse(WarningField.Text, out var value))
            {
                store.WarningPercent = value;
            }
            WarningField.Text = store.WarningPercent.ToString();
        }

        WarningField.LostFocus += (_, _) => CommitWarning();
        WarningField.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) CommitWarning();
        };

        void CommitCritical()
        {
            if (!_initialized) return;
            if (int.TryParse(CriticalField.Text, out var value))
            {
                store.CriticalPercent = value;
            }
            CriticalField.Text = store.CriticalPercent.ToString();
        }

        CriticalField.LostFocus += (_, _) => CommitCritical();
        CriticalField.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) CommitCritical();
        };

        _initialized = true;
    }
}
