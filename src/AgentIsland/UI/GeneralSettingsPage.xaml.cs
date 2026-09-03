using System.Windows;
using System.Windows.Controls;
using AgentIsland.UI.Localization;
using AgentIsland.Windows;
using AgentIsland.Windows.Startup;

namespace AgentIsland.UI;

public sealed partial class GeneralSettingsPage : UserControl
{
    private bool _initialized;

    public GeneralSettingsPage()
    {
        InitializeComponent();

        // 1. Launch at Login
        LaunchToggle.IsOn = LaunchAtLogin.IsEnabled;
        LaunchToggle.Toggled += enabled =>
        {
            if (!_initialized) return;
            LaunchAtLogin.SetEnabled(enabled);
        };

        // 2. Language selection
        if (LanguageCombo.Items.Count > 0 && LanguageCombo.Items[0] is ComboBoxItem autoItem)
        {
            autoItem.Content = L10n.Tr("Auto (system)");
        }
        var currentLang = AppLanguageStore.Load();
        LanguageCombo.SelectedIndex = currentLang switch
        {
            L10n.Language.English => 1,
            L10n.Language.SimplifiedChinese => 2,
            _ => 0,
        };
        LanguageCombo.SelectionChanged += OnLanguageSelectionChanged;

        // 3. Section label
        UpdatesSectionLabel.Text = L10n.Tr("Updates").ToUpperInvariant();

        // 4. Auto check updates
        AutoCheckUpdatesToggle.IsOn = Preferences.Get<bool?>("AgentIsland.autoCheckUpdates") ?? true;
        AutoCheckUpdatesToggle.Toggled += enabled =>
        {
            if (!_initialized) return;
            Preferences.Set("AgentIsland.autoCheckUpdates", enabled);
        };

        // 5. Check now button
        CheckNowButton.Label = L10n.Tr("Check");
        CheckNowButton.Clicked += () =>
        {
            if (!_initialized) return;
            _ = AgentIsland.Backend.Updates.UpdateChecker.Shared.CheckAsync(userInitiated: true);
        };

        _initialized = true;
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        var chosen = LanguageCombo.SelectedIndex switch
        {
            1 => L10n.Language.English,
            2 => L10n.Language.SimplifiedChinese,
            _ => L10n.Language.Auto,
        };
        if (chosen == AppLanguageStore.Load()) return;

        AppLanguageStore.Save(chosen);
        L10n.Current = chosen;
        if (Application.Current is App app)
        {
            app.RebuildForLanguageChange();
        }
        var window = Window.GetWindow(this);
        window?.Close();
        SettingsWindow.Open();
    }
}
