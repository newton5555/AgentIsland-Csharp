using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentIsland.Backend.Alarms;
using AgentIsland.Core;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Providers;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;
using AgentIsland.Core.Usage;

namespace AgentIsland.UI;

/// Settings window — a faithful port of the macOS layout: brand header on
/// top, pill tab bar (General / Display / Providers / Status),
/// hairlines, scrolling row content, and the GitHub/License/Quit footer.
public sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public static void Open()
    {
        if (_open is { } existing)
        {
            WindowActivation.BringToFront(existing);
            return;
        }
        var window = new SettingsWindow();
        _open = window;
        window.Closed += (_, _) => _open = null;
        window.Show();
        WindowActivation.BringToFront(window);

        // Scripted verification: render the active tab's full content (past the
        // viewport) to a PNG — immune to the window occlusion a screen grab hits.
        var png = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SETTINGS_PNG");
        if (!string.IsNullOrEmpty(png)) window.SaveSnapshot(png);
    }

    /// Renders the current tab's content column at full height onto the panel
    /// background, so a verification screenshot shows every row even when the
    /// window is behind something else.
    public void SaveSnapshot(string path)
    {
        var settle = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650),
        };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            try
            {
                if (_scroll.Content is not FrameworkElement content) return;
                var w = (int)Math.Ceiling(content.ActualWidth);
                var h = (int)Math.Ceiling(content.ActualHeight);
                if (w <= 0 || h <= 0) return;
                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(
                        IslandColors.Brush(IslandColors.AlarmBackground), null, new Rect(0, 0, w, h));
                    dc.DrawRectangle(
                        new System.Windows.Media.VisualBrush(content), null, new Rect(0, 0, w, h));
                }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = System.IO.File.Create(path);
                encoder.Save(stream);
            }
            catch
            {
            }
        };
        settle.Start();
    }

    /// Snapshot-sweep entry: open the window, walk every tab, render the
    /// WHOLE window (sidebar + content) per tab, then hand control back.
    public static void SnapshotAllTabs(string dir, Action done)
    {
        Open();
        if (_open is not { } window)
        {
            done();
            return;
        }
        window.SnapshotTabs(dir, done);
    }

    private void SnapshotTabs(string dir, Action done)
    {
        var tabs = Enum.GetValues<Tab>();
        var index = 0;
        void Next()
        {
            if (index >= tabs.Length)
            {
                done();
                return;
            }
            var tab = tabs[index];
            index++;
            Select(tab);
            var settle = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600),
            };
            settle.Tick += (_, _) =>
            {
                settle.Stop();
                var stem = $"settings-{index}-{tab}".ToLowerInvariant();
                try
                {
                    RenderWindow(System.IO.Path.Combine(dir, stem + ".png"));
                    // Full content height too — the window view crops at the
                    // viewport, and the fold is where sins hide.
                    RenderFullContent(System.IO.Path.Combine(dir, stem + "-full.png"));
                }
                catch
                {
                }
                Next();
            };
            settle.Start();
        }
        Next();
    }

    /// The scroll viewer's content at its FULL laid-out height (the same
    /// framing AGENTISLAND_DEBUG_SETTINGS_PNG uses), so below-the-fold rows
    /// are part of the sweep.
    private void RenderFullContent(string path)
    {
        if (_scroll.Content is not FrameworkElement content) return;
        var w = (int)Math.Ceiling(content.ActualWidth);
        var h = (int)Math.Ceiling(content.ActualHeight);
        if (w <= 0 || h <= 0) return;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                IslandColors.Brush(IslandColors.AlarmBackground), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(
                new System.Windows.Media.VisualBrush(content), null, new Rect(0, 0, w, h));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    private void RenderWindow(string path)
    {
        if (Content is not FrameworkElement root) return;
        var w = (int)Math.Ceiling(root.ActualWidth);
        var h = (int)Math.Ceiling(root.ActualHeight);
        if (w <= 0 || h <= 0) return;
        // The window's own Background lives on the Window, not the content
        // tree — composite it first or the sweep PNG reads white-on-white.
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, w, h));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    /// Declaration order IS the sidebar order (macOS 2.1.1 IA): the
    /// providers page leads because it is what the app is about; alerts
    /// stand alone instead of hiding at the bottom of General.
    private enum Tab
    {
        Providers,
        Display,
        Alerts,
        General,
        Status,
        About,
    }

    private static (string Label, string Glyph) TabFace(Tab tab) => tab switch
    {
        Tab.Providers => ("Providers", "\uE8A9"),
        Tab.Display => ("Display", "\uE7F4"),
        Tab.Alerts => ("Alerts", "\uEA8F"),
        Tab.General => ("General", "\uE713"),
        Tab.Status => ("Status", "\uE890"),
        Tab.About => ("About", "\uE946"),
        _ => (tab.ToString(), "\uE713"),
    };

    private readonly List<(Tab Tab, Border Cell, TextBlock Glyph, TextBlock Label)> _navItems = new();
    private Tab _active = Tab.General;

    private ProvidersSettingsPage? _providersPage;

    private SettingsWindow()
    {
        InitializeComponent();
        Title = "Agent Island — " + L10n.Tr("Settings");
        Background = IslandColors.Brush(IslandColors.AlarmBackground);

        SetupSidebarFooter();
        SetupFooter();
        SetupNavItems();

        // Usage lands from background fetches and the guest stores publish on
        // their own schedule; the provider rows follow along instead of going
        // stale until the next tab switch.
        UsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        ProviderVisibilityStore.Shared.PropertyChanged += OnProviderStoreChanged;
        AntigravityUsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        GrokUsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        CursorUsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        DeepSeekBalanceStore.Shared.PropertyChanged += OnProviderStoreChanged;
        Closed += (_, _) =>
        {
            UsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            ProviderVisibilityStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            AntigravityUsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            GrokUsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            CursorUsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            DeepSeekBalanceStore.Shared.PropertyChanged -= OnProviderStoreChanged;
        };

        var savedTab = Preferences.Get<string?>("Settings.activeTab");
        if (Enum.TryParse<Tab>(savedTab, out var restored)) _active = restored;
        // Scripted-verification hook: jump straight to a tab.
        if (Enum.TryParse<Tab>(
                Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_SETTINGS_TAB"),
                out var forced))
        {
            _active = forced;
        }
        Select(_active);
    }

    private void SetupSidebarFooter()
    {
        VersionText.Text = "v" + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0");
        VersionPill.ToolTip = L10n.Tr("View Releases on GitHub");
        VersionPill.MouseEnter += (_, _) => VersionPill.Background = IslandColors.Brush(IslandColors.White(0.10));
        VersionPill.MouseLeave += (_, _) => VersionPill.Background = IslandColors.Brush(IslandColors.White(0.05));
        VersionPill.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            AboutSettingsPage.OpenUrl("https://github.com/newton5555/AgentIsland-Csharp/releases");
        };

        QuitText.Text = L10n.Tr("Quit");
        QuitPill.ToolTip = L10n.Tr("Quit AgentIsland");
        QuitPill.MouseEnter += (_, _) => QuitPill.Background = IslandColors.Brush(IslandColors.White(0.10));
        QuitPill.MouseLeave += (_, _) => QuitPill.Background = IslandColors.Brush(IslandColors.White(0.05));
        QuitPill.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            System.Windows.Application.Current.Shutdown();
        };
    }

    private void SetupFooter()
    {
        WeeklyButton.Label = L10n.Tr("Weekly");
        WeeklyButton.ToolTip = L10n.Tr("Share weekly report");
        WeeklyButton.Clicked += () => Report.ReportWindow.Show(Report.ReportWindow.Kind.Weekly);

        MonthlyButton.Label = L10n.Tr("Monthly");
        MonthlyButton.ToolTip = L10n.Tr("Share monthly report");
        MonthlyButton.Clicked += () => Report.ReportWindow.Show(Report.ReportWindow.Kind.Monthly);
    }

    private void SetupNavItems()
    {
        _navItems.Clear();
        var borders = new[]
        {
            (Tab.Providers, NavProviders),
            (Tab.Display, NavDisplay),
            (Tab.Alerts, NavAlerts),
            (Tab.General, NavGeneral),
            (Tab.Status, NavStatus),
            (Tab.About, NavAbout),
        };

        foreach (var (tab, border) in borders)
        {
            var stack = (StackPanel)border.Child;
            var glyph = (TextBlock)stack.Children[0];
            var label = (TextBlock)stack.Children[1];

            var face = TabFace(tab);
            glyph.Text = face.Glyph;
            label.Text = L10n.Tr(face.Label);

            var captured = tab;
            border.MouseLeftButtonUp += (_, args) =>
            {
                Select(captured);
                args.Handled = true;
            };
            border.MouseEnter += (_, _) =>
            {
                if (_active != captured) border.Background = IslandColors.Brush(IslandColors.White(0.04));
            };
            border.MouseLeave += (_, _) =>
            {
                if (_active != captured) border.Background = Brushes.Transparent;
            };

            _navItems.Add((tab, border, glyph, label));
        }
    }


    /// The stores raise from background completions; hop to the dispatcher
    /// and let each row re-read whatever it shows.
    private void OnProviderStoreChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.BeginInvoke(RefreshProviderRows);

    private void RefreshProviderRows()
    {
        _providersPage?.RefreshRows();
    }

    private void Select(Tab tab)
    {
        _active = tab;
        _providersPage = null;
        Preferences.Set("Settings.activeTab", tab.ToString());
        foreach (var (cellTab, cell, glyph, label) in _navItems)
        {
            var isOn = cellTab == tab;
            cell.Background = isOn
                ? IslandColors.Brush(IslandColors.White(0.13))
                : Brushes.Transparent;
            glyph.Foreground = IslandColors.Brush(IslandColors.White(isOn ? 0.95 : 0.48));
            label.Foreground = IslandColors.Brush(IslandColors.White(isOn ? 0.95 : 0.55));
        }
        var body = tab switch
        {
            Tab.Providers => (UIElement)(_providersPage = new ProvidersSettingsPage()),
            Tab.Display => (UIElement)new DisplaySettingsPage(),
            Tab.Alerts => (UIElement)new AlertsSettingsPage(),
            Tab.General => (UIElement)new GeneralSettingsPage(),
            Tab.Status => (UIElement)new StatusSettingsPage(),
            Tab.About => (UIElement)new AboutSettingsPage(),
            _ => (UIElement)new StackPanel(),
        };
        PageTitle.Text = L10n.Tr(TabFace(tab).Label);
        PageContent.Content = body;
        _scroll.ScrollToTop();
    }
}
