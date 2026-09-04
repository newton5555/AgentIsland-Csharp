using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentIsland.UI;
using AgentIsland.Windows;

namespace AgentIsland.Tests;

public static class SettingsXamlTests
{
    public static void RunAll()
    {
        Console.WriteLine("--- SettingsXamlTests ---");

        EnsureApplication();

        TestXamlResourceLoading();
        TestCobaltToggle();
        TestPillButtonControl();
        TestSegmentedControl();
        TestDottedLink();
        TestSettingsRowControl();
        TestCaptionButtons();
        TestGeneralSettingsPageInitializationNoSideEffects();
        TestSixNavigationTabsOrderAndIntegrity();
        TestBatch2PagesInstantiationNoSideEffects();
        TestBatch2PagesXamlFilesIntegrity();
        TestStylePickerControls();
        TestBatch3PagesXamlFilesIntegrity();
        TestBatch3PagesInstantiationNoSideEffects();
        TestProvidersSettingsPageStructureAndSlotRejection();
        TestCodexAccountMenuStructure();
        TestNamePromptWindowStructure();

        Console.WriteLine("SettingsXamlTests GREEN");
    }

    private static string FindRepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relativePath} walking up from {AppContext.BaseDirectory}.");
    }

    private static void TestSixNavigationTabsOrderAndIntegrity()
    {
        var expectedTabs = new[]
        {
            "Providers",
            "Display",
            "Alerts",
            "General",
            "Status",
            "About",
        };

        var xamlPath = FindRepoFile(Path.Combine("src", "AgentIsland", "UI", "SettingsWindow.xaml"));

        var xml = System.Xml.Linq.XDocument.Load(xamlPath);
        var navBorders = xml.Descendants()
            .Where(e => e.Name.LocalName == "Border" && e.Attribute("Tag") != null)
            .Select(e => e.Attribute("Tag")!.Value)
            .ToList();

        if (navBorders.Count != expectedTabs.Length)
        {
            throw new Exception($"Expected {expectedTabs.Length} tagged borders in NavPanel, got {navBorders.Count}.");
        }

        for (var i = 0; i < expectedTabs.Length; i++)
        {
            if (navBorders[i] != expectedTabs[i])
            {
                throw new Exception($"Navigation item {i} expected Tag '{expectedTabs[i]}', got '{navBorders[i]}'.");
            }
        }

        Console.WriteLine("PASS Seven navigation items order and integrity preserved in SettingsWindow.xaml");
    }

    private static void EnsureApplication()
    {
        if (Application.Current == null)
        {
            _ = new Application();
        }
    }

    private static void TestXamlResourceLoading()
    {
        var dict = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AgentIsland;component/UI/SettingsStyles.xaml", UriKind.Absolute)
        };

        if (!dict.Contains("DarkComboBoxStyle"))
        {
            throw new Exception("SettingsStyles.xaml missing DarkComboBoxStyle.");
        }
        if (!dict.Contains("DarkComboBoxItemStyle"))
        {
            throw new Exception("SettingsStyles.xaml missing DarkComboBoxItemStyle.");
        }
        if (!dict.Contains("SettingsRowControlTemplate"))
        {
            throw new Exception("SettingsStyles.xaml missing SettingsRowControlTemplate.");
        }

        var style = DarkComboStyle.Style;
        if (style == null)
        {
            throw new Exception("DarkComboStyle.Style must not be null.");
        }

        var combo = DarkComboStyle.Apply(new ComboBox());
        if (combo.Style != style)
        {
            throw new Exception("DarkComboStyle.Apply must assign DarkComboBoxStyle.");
        }

        Console.WriteLine("PASS XAML resource dictionary loads cleanly and contains required styles");
    }

    private static void TestCobaltToggle()
    {
        var toggleOff = new CobaltToggle(false);
        if (toggleOff.IsOn) throw new Exception("CobaltToggle(false) must have IsOn == false.");

        var toggleOn = new CobaltToggle(true);
        if (!toggleOn.IsOn) throw new Exception("CobaltToggle(true) must have IsOn == true.");

        var toggleDefault = new CobaltToggle();
        if (toggleDefault.IsOn) throw new Exception("CobaltToggle() default constructor must have IsOn == false.");

        // Programmatic assignment must NOT fire Toggled event
        var toggledFired = false;
        toggleOff.Toggled += _ => toggledFired = true;
        toggleOff.IsOn = true;
        if (toggledFired)
        {
            throw new Exception("Setting CobaltToggle.IsOn programmatically must NOT trigger Toggled event.");
        }

        // User mouse click MUST fire Toggled event
        var mouseUpArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            Source = toggleOff,
        };
        toggleOff.RaiseEvent(mouseUpArgs);
        if (!toggledFired)
        {
            throw new Exception("User MouseLeftButtonUp must trigger Toggled event.");
        }
        if (toggleOff.IsOn)
        {
            throw new Exception("Clicking active toggle should switch state back to false.");
        }

        Console.WriteLine("PASS CobaltToggle initial value, programmatic assignment, and user toggle event distinction hold");
    }

    private static void TestPillButtonControl()
    {
        var btn = new PillButtonControl("Refresh");
        if (btn.Label != "Refresh") throw new Exception($"Expected label 'Refresh', got '{btn.Label}'.");

        btn.Label = "Check";
        if (btn.Label != "Check") throw new Exception($"Expected updated label 'Check', got '{btn.Label}'.");

        var clicked = false;
        btn.Clicked += () => clicked = true;
        btn.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            Source = btn,
        });
        if (!clicked) throw new Exception("PillButtonControl must trigger Clicked event on mouse left up.");

        Console.WriteLine("PASS PillButtonControl label and clicked event hold");
    }

    private static void TestSegmentedControl()
    {
        var seg = new Segmented(new[] { "Used", "Remaining" }, 1);
        if (seg.SelectedIndex != 1) throw new Exception($"Expected SelectedIndex 1, got {seg.SelectedIndex}.");

        seg.Select(0);
        if (seg.SelectedIndex != 0) throw new Exception($"Expected SelectedIndex 0, got {seg.SelectedIndex}.");

        Console.WriteLine("PASS Segmented control selection and items hold");
    }

    private static void TestDottedLink()
    {
        var link = new DottedLink("GitHub", "https://github.com");
        if (link.Title != "GitHub") throw new Exception($"Expected Title 'GitHub', got '{link.Title}'.");
        if (link.Url != "https://github.com") throw new Exception($"Expected Url 'https://github.com', got '{link.Url}'.");

        Console.WriteLine("PASS DottedLink properties hold");
    }

    private static void TestSettingsRowControl()
    {
        var trailing = new TextBlock { Text = "Trailing" };
        var row = new SettingsRowControl("Title", "Subtitle", trailing, Colors.Cyan, "PRO", true);

        if (row.Title != "Title") throw new Exception($"Expected Title 'Title', got '{row.Title}'.");
        if (row.Subtitle != "Subtitle") throw new Exception($"Expected Subtitle 'Subtitle', got '{row.Subtitle}'.");
        if (row.Trailing != trailing) throw new Exception("Trailing content was not assigned.");
        if (row.DotColor != Colors.Cyan) throw new Exception("DotColor was not assigned.");
        if (row.Chip != "PRO") throw new Exception("Chip was not assigned.");
        if (!row.MonospaceTitle) throw new Exception("MonospaceTitle was not assigned.");

        Console.WriteLine("PASS SettingsRowControl properties and constructor hold");
    }

    private static void TestCaptionButtons()
    {
        var window = new Window();
        var element = CaptionButtons.Build(window);
        if (element is not CaptionButtons buttons)
        {
            throw new Exception("CaptionButtons.Build must return a CaptionButtons instance.");
        }
        if (buttons.TargetWindow != window)
        {
            throw new Exception("CaptionButtons TargetWindow must match target window.");
        }

        Console.WriteLine("PASS CaptionButtons.Build returns valid CaptionButtons control");
    }

    private static void TestGeneralSettingsPageInitializationNoSideEffects()
    {
        var settingsFile = IslandPaths.SettingsFile;
        var beforeExists = File.Exists(settingsFile);
        var beforeBytes = beforeExists ? File.ReadAllBytes(settingsFile) : null;

        // Instantiate GeneralSettingsPage
        var page = new GeneralSettingsPage();

        var afterExists = File.Exists(settingsFile);
        var afterBytes = afterExists ? File.ReadAllBytes(settingsFile) : null;

        if (beforeExists != afterExists)
        {
            throw new Exception($"Instantiating GeneralSettingsPage changed {settingsFile} existence.");
        }
        if (beforeBytes != null && afterBytes != null && !beforeBytes.SequenceEqual(afterBytes))
        {
            throw new Exception($"Instantiating GeneralSettingsPage mutated {settingsFile} content.");
        }

        Console.WriteLine("PASS GeneralSettingsPage initialization does not mutate settings.json");
    }

    private static void TestBatch2PagesInstantiationNoSideEffects()
    {
        var settingsFile = IslandPaths.SettingsFile;
        var beforeExists = File.Exists(settingsFile);
        var beforeBytes = beforeExists ? File.ReadAllBytes(settingsFile) : null;

        var alerts = new AlertsSettingsPage();
        var display = new DisplaySettingsPage();
        var status = new StatusSettingsPage();
        var about = new AboutSettingsPage();

        var afterExists = File.Exists(settingsFile);
        var afterBytes = afterExists ? File.ReadAllBytes(settingsFile) : null;

        if (beforeExists != afterExists)
        {
            throw new Exception($"Instantiating Batch 2 pages changed {settingsFile} existence.");
        }
        if (beforeBytes != null && afterBytes != null && !beforeBytes.SequenceEqual(afterBytes))
        {
            throw new Exception($"Instantiating Batch 2 pages mutated {settingsFile} content.");
        }

        Console.WriteLine("PASS Batch 2 pages initialization does not mutate settings.json");
    }

    private static void TestBatch2PagesXamlFilesIntegrity()
    {
        var relativePaths = new[]
        {
            Path.Combine("src", "AgentIsland", "UI", "AlertsSettingsPage.xaml"),
            Path.Combine("src", "AgentIsland", "UI", "DisplaySettingsPage.xaml"),
            Path.Combine("src", "AgentIsland", "UI", "StatusSettingsPage.xaml"),
            Path.Combine("src", "AgentIsland", "UI", "AboutSettingsPage.xaml"),
        };

        foreach (var rel in relativePaths)
        {
            var fullPath = FindRepoFile(rel);
            var xml = System.Xml.Linq.XDocument.Load(fullPath);
            if (xml.Root?.Name.LocalName != "UserControl")
            {
                throw new Exception($"Expected UserControl root in {rel}, found {xml.Root?.Name.LocalName}");
            }
        }

        Console.WriteLine("PASS Batch 2 XAML files have valid UserControl roots");
    }

    private static void TestStylePickerControls()
    {
        // 1. ChartStylePickerControl
        var chartPicker = new ChartStylePickerControl(ChartStyle.Bar);
        if (chartPicker.Children.Count != 4)
        {
            throw new Exception($"Expected 4 tiles in ChartStylePickerControl, got {chartPicker.Children.Count}");
        }
        for (var i = 0; i < 4; i++)
        {
            if (chartPicker.Children[i] is not StylePickerTile)
            {
                throw new Exception($"Child {i} of ChartStylePickerControl is not StylePickerTile");
            }
        }

        ChartStyle? chartEvent = null;
        chartPicker.StyleSelected += s => chartEvent = s;
        chartPicker.Select(ChartStyle.Numeric);
        if (chartEvent != null)
        {
            throw new Exception("ChartStylePickerControl.Select() must not fire StyleSelected event");
        }

        var chartTile0 = (StylePickerTile)chartPicker.Children[0];
        chartTile0.PerformClick();
        if (chartEvent != ChartStyle.Stepped)
        {
            throw new Exception($"Clicking tile 0 expected ChartStyle.Stepped, got {chartEvent}");
        }

        // 2. CostStylePickerControl
        var costPicker = new CostStylePickerControl(CostStyle.Tokens);
        if (costPicker.Children.Count != 4)
        {
            throw new Exception($"Expected 4 tiles in CostStylePickerControl, got {costPicker.Children.Count}");
        }
        for (var i = 0; i < 4; i++)
        {
            if (costPicker.Children[i] is not StylePickerTile)
            {
                throw new Exception($"Child {i} of CostStylePickerControl is not StylePickerTile");
            }
        }

        CostStyle? costEvent = null;
        costPicker.StyleSelected += s => costEvent = s;
        costPicker.Select(CostStyle.Trend);
        if (costEvent != null)
        {
            throw new Exception("CostStylePickerControl.Select() must not fire StyleSelected event");
        }

        var costTile0 = (StylePickerTile)costPicker.Children[0];
        costTile0.PerformClick();
        if (costEvent != CostStyle.Dollar)
        {
            throw new Exception($"Clicking tile 0 expected CostStyle.Dollar, got {costEvent}");
        }

        Console.WriteLine("PASS StylePickerControls 4-item integrity, templates, and event semantics hold");
    }

    private static void TestBatch3PagesXamlFilesIntegrity()
    {
        var relativePaths = new[]
        {
            Path.Combine("src", "AgentIsland", "UI", "ProvidersSettingsPage.xaml"),
            Path.Combine("src", "AgentIsland", "UI", "ProviderRowControl.xaml"),
            Path.Combine("src", "AgentIsland", "UI", "CodexAccountMenuControl.xaml"),
            Path.Combine("src", "AgentIsland", "UI", "NamePromptWindow.xaml"),
        };

        foreach (var rel in relativePaths)
        {
            var fullPath = FindRepoFile(rel);
            var xml = System.Xml.Linq.XDocument.Load(fullPath);
            var root = xml.Root?.Name.LocalName;
            if (root != "UserControl" && root != "Window")
            {
                throw new Exception($"Expected UserControl or Window root in {rel}, found {root}");
            }
        }

        Console.WriteLine("PASS Batch 3 XAML files have valid roots");
    }

    private static void TestBatch3PagesInstantiationNoSideEffects()
    {
        var settingsFile = IslandPaths.SettingsFile;
        var beforeExists = File.Exists(settingsFile);
        var beforeBytes = beforeExists ? File.ReadAllBytes(settingsFile) : null;

        var providersPage = new ProvidersSettingsPage();
        var row = new ProviderRowControl();
        var menu = new CodexAccountMenuControl();
        var prompt = new NamePromptWindow();

        var afterExists = File.Exists(settingsFile);
        var afterBytes = afterExists ? File.ReadAllBytes(settingsFile) : null;

        if (beforeExists != afterExists)
        {
            throw new Exception($"Instantiating Batch 3 controls changed {settingsFile} existence.");
        }
        if (beforeBytes != null && afterBytes != null && !beforeBytes.SequenceEqual(afterBytes))
        {
            throw new Exception($"Instantiating Batch 3 controls mutated {settingsFile} content.");
        }

        Console.WriteLine("PASS Batch 3 controls initialization does not mutate settings.json");
    }

    private static void TestProvidersSettingsPageStructureAndSlotRejection()
    {
        var page = new ProvidersSettingsPage();
        var expectedCount = DisplayProviders.All.Length;
        if (page.SortableHost.Children.Count != expectedCount)
        {
            throw new Exception($"Expected {expectedCount} rows in ProvidersSettingsPage, got {page.SortableHost.Children.Count}");
        }

        var order = ProviderVisibilityStore.Shared.Order;
        for (var i = 0; i < expectedCount; i++)
        {
            if (page.SortableHost.Children[i] is not ProviderRowControl rowCtrl)
            {
                throw new Exception($"Child {i} of ProvidersSettingsPage is not ProviderRowControl");
            }
            if (rowCtrl.Provider != order[i])
            {
                throw new Exception($"Expected row {i} to be {order[i]}, got {rowCtrl.Provider}");
            }
        }

        // Slot limit refusal visibility
        page.ShowSlotLimit(true);
        if (page.SlotNotice.Visibility != Visibility.Visible)
        {
            throw new Exception("Slot limit notice should be visible when refused");
        }
        page.ShowSlotLimit(false);
        if (page.SlotNotice.Visibility != Visibility.Collapsed)
        {
            throw new Exception("Slot limit notice should be collapsed when not refused");
        }

        Console.WriteLine($"PASS ProvidersSettingsPage {expectedCount} rows order and slot rejection hold");
    }

    private static void TestCodexAccountMenuStructure()
    {
        var originalLang = L10n.Current;
        try
        {
            foreach (var lang in new[] { L10n.Language.English, L10n.Language.SimplifiedChinese })
            {
                L10n.Current = lang;

                var menuControl = new CodexAccountMenuControl();
                if (menuControl.Menu.Items.Count == 0)
                {
                    throw new Exception($"CodexAccountMenu should have static items declared in XAML ({lang})");
                }

                if (menuControl.SaveAccountItem is null)
                {
                    throw new Exception($"SaveAccountItem XAML reference missing ({lang})");
                }
                if (menuControl.AutoSwitchItem is null || !menuControl.AutoSwitchItem.IsCheckable)
                {
                    throw new Exception($"AutoSwitchItem must be checkable ({lang})");
                }

                // Explicitly prepare menu structure without opening popup
                menuControl.PrepareMenu();

                var expectedSave = L10n.Tr("Save current account…");
                var expectedAuto = L10n.Tr("Auto-switch when exhausted");

                if (!string.Equals(menuControl.SaveAccountItem.Header?.ToString(), expectedSave, StringComparison.Ordinal))
                {
                    throw new Exception($"Expected save header '{expectedSave}', got '{menuControl.SaveAccountItem.Header}' ({lang})");
                }
                if (!string.Equals(menuControl.AutoSwitchItem.Header?.ToString(), expectedAuto, StringComparison.Ordinal))
                {
                    throw new Exception($"Expected auto-switch header '{expectedAuto}', got '{menuControl.AutoSwitchItem.Header}' ({lang})");
                }

                // Regression test: RefreshStatus must NEVER rebuild or replace existing menu items
                var firstItemBefore = menuControl.Menu.Items[0];
                var countBefore = menuControl.Menu.Items.Count;
                menuControl.RefreshStatus();

                if (!ReferenceEquals(menuControl.Menu.Items[0], firstItemBefore))
                {
                    throw new Exception($"RefreshStatus must not rebuild or replace existing menu items ({lang})");
                }
                if (menuControl.Menu.Items.Count != countBefore)
                {
                    throw new Exception($"RefreshStatus must not change item count ({lang})");
                }

                // Regression test: ContextMenuOpening event prepares data without opening popup
                var freshControl = new CodexAccountMenuControl();
                var ctor = typeof(ContextMenuEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];
                var args = (ContextMenuEventArgs)ctor.Invoke(new object[] { freshControl.AccountButton, true });
                args.RoutedEvent = ContextMenuService.ContextMenuOpeningEvent;
                freshControl.AccountButton.RaiseEvent(args);

                if (freshControl.Menu.IsOpen)
                {
                    throw new Exception($"ContextMenuOpening must not open Popup ({lang})");
                }
                if (!string.Equals(freshControl.SaveAccountItem.Header?.ToString(), expectedSave, StringComparison.Ordinal))
                {
                    throw new Exception($"ContextMenuOpening must prepare SaveAccountItem header ({lang})");
                }
                if (freshControl.AutoSwitchItem.IsChecked != CodexAccountSwitcher.AutoSwitchEnabled)
                {
                    throw new Exception($"ContextMenuOpening must sync AutoSwitchEnabled ({lang})");
                }
                if (CodexAccountSwitcher.Accounts().Count == 0 && freshControl.NoSavedAccountsMenuItem.Visibility != Visibility.Visible)
                {
                    throw new Exception($"ContextMenuOpening with 0 accounts must show NoSavedAccountsItem ({lang})");
                }
            }
        }
        finally
        {
            L10n.Current = originalLang;
        }

        Console.WriteLine("PASS CodexAccountMenu structure, checkable options, localization, ContextMenuOpening, and non-destructive RefreshStatus hold");
    }

    private static void TestNamePromptWindowStructure()
    {
        var prompt = new NamePromptWindow("Test Title", "Test Message", "Type here", "Confirm", 40, "Extra", () => { });
        if (prompt.PromptTitle.Text != "Test Title") throw new Exception("NamePrompt title mismatch");
        if (prompt.PromptMessage.Text != "Test Message") throw new Exception("NamePrompt message mismatch");
        if (prompt.PromptPlaceholder.Text != "Type here") throw new Exception("NamePrompt placeholder mismatch");
        if (prompt.ConfirmButton.Label != "Confirm") throw new Exception("NamePrompt confirm label mismatch");
        if (prompt.PromptField.MaxLength != 40) throw new Exception("NamePrompt maxLength mismatch");
        if (prompt.ExtraButton.Visibility != Visibility.Visible) throw new Exception("NamePrompt extra button should be visible");

        prompt.PromptField.Text = "entered text";
        if (prompt.PromptPlaceholder.Visibility != Visibility.Collapsed) throw new Exception("Placeholder should collapse on text");
        prompt.PromptField.Text = "";
        if (prompt.PromptPlaceholder.Visibility != Visibility.Visible) throw new Exception("Placeholder should be visible when empty");

        Console.WriteLine("PASS NamePromptWindow structure, placeholder, and length cap hold");
    }
}
