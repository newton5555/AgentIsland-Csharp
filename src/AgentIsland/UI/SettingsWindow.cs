using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentIsland.Alarm;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Trigger;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;
using AgentIsland.Usage;

namespace AgentIsland.UI;

/// Settings window — a faithful port of the macOS layout: brand header on
/// top, pill tab bar (General / Display / Providers / Status),
/// hairlines, scrolling row content, and the GitHub/License/Quit footer.
public sealed class SettingsWindow : Window
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
        Notes,
        About,
    }

    private static (string Label, string Glyph) TabFace(Tab tab) => tab switch
    {
        Tab.Providers => ("Providers", "\uE8A9"),
        Tab.Display => ("Display", "\uE7F4"),
        Tab.Alerts => ("Alerts", "\uEA8F"),
        Tab.General => ("General", "\uE713"),
        Tab.Status => ("Status", "\uE890"),
        Tab.Notes => ("Notes", "\uE70B"),
        Tab.About => ("About", "\uE946"),
        _ => (tab.ToString(), "\uE713"),
    };

    private readonly List<(Tab Tab, Border Cell, TextBlock Glyph, TextBlock Label)> _navItems = new();
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    private Tab _active = Tab.General;

    // Provider rows re-read their stores in place rather than rebuilding the
    // tab: a rebuild would lose the scroll position and yank the account menu
    // out from under the cursor. The list belongs to whichever tab built it;
    // the store subscriptions live for the window's lifetime and just walk it.
    private readonly List<Action> _providerRefreshers = new();
    private TextBlock? _slotNotice;

    private SettingsWindow()
    {
        Title = "Agent Island — " + L10n.Tr("Settings");
        Width = 680;
        Height = 560;
        MinWidth = 640;
        MinHeight = 460;
        Background = IslandColors.Brush(IslandColors.AlarmBackground);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = false;
        // Pixel-snapped glyphs: the 10-13px settings copy is blurry in WPF's
        // default Ideal mode.
        System.Windows.Media.TextOptions.SetTextFormattingMode(
            this, System.Windows.Media.TextFormattingMode.Display);

        // No system title bar: the brand header doubles as the drag strip
        // and the caption buttons live inside the page (top-right), matching
        // the macOS integrated-titlebar look.
        WindowStyle = WindowStyle.None;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 58,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // macOS 2.1.1 shell: full-height sidebar rail, hairline divider,
        // then the content column with the footer parked at its bottom.
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(158) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        main.Children.Add(sidebar);

        var divider = new Border { Background = IslandColors.Brush(IslandColors.White(0.05)) };
        Grid.SetColumn(divider, 1);
        main.Children.Add(divider);

        var contentColumn = new DockPanel();
        var captionSpacer = new Border { Height = 24 };
        DockPanel.SetDock(captionSpacer, Dock.Top);
        contentColumn.Children.Add(captionSpacer);

        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        contentColumn.Children.Add(footer);

        var bottomHairline = Hairline();
        DockPanel.SetDock(bottomHairline, Dock.Bottom);
        contentColumn.Children.Add(bottomHairline);

        contentColumn.Children.Add(_scroll);
        Grid.SetColumn(contentColumn, 2);
        main.Children.Add(contentColumn);

        var shell = new Grid();
        shell.Children.Add(main);
        shell.Children.Add(CaptionButtons.Build(this));
        Content = shell;

        // Usage lands from background fetches and the guest stores publish on
        // their own schedule; the provider rows follow along instead of going
        // stale until the next tab switch.
        UsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        ProviderVisibilityStore.Shared.PropertyChanged += OnProviderStoreChanged;
        AntigravityUsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        GrokUsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        CursorUsageStore.Shared.PropertyChanged += OnProviderStoreChanged;
        Closed += (_, _) =>
        {
            UsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            ProviderVisibilityStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            AntigravityUsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            GrokUsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
            CursorUsageStore.Shared.PropertyChanged -= OnProviderStoreChanged;
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

    // MARK: - Chrome

    /// Left rail: compact brand up top, one item per page, the version
    /// pill + Quit at the bottom (app-level controls live on the rail, not
    /// in the page footer — macOS 2.1.1).
    private UIElement BuildSidebar()
    {
        var rail = new DockPanel { Background = IslandColors.Brush(IslandColors.White(0.016)) };

        var top = new StackPanel();
        top.Children.Add(new Border { Height = 30 });
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 14),
        };
        try
        {
            var mark = new Image
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = new BitmapImage(new Uri(
                    "pack://application:,,,/Assets/agentisland_logo_small.png")),
            };
            RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
            brand.Children.Add(mark);
        }
        catch
        {
        }
        brand.Children.Add(new TextBlock
        {
            Text = "Agent Island",
            FontFamily = IslandFonts.Ui,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.92)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        top.Children.Add(brand);
        foreach (var tab in Enum.GetValues<Tab>())
        {
            top.Children.Add(NavItem(tab));
        }
        DockPanel.SetDock(top, Dock.Top);
        rail.Children.Add(top);

        var bottom = new Grid { Margin = new Thickness(14, 8, 14, 14) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var version = RailPill(
            "v" + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0"),
            L10n.Tr("What's new in this version"),
            WhatsNewWindow.Open);
        Grid.SetColumn(version, 0);
        bottom.Children.Add(version);

        var quit = RailPill(L10n.Tr("Quit"), L10n.Tr("Quit AgentIsland"),
            () => System.Windows.Application.Current.Shutdown());
        Grid.SetColumn(quit, 2);
        bottom.Children.Add(quit);

        DockPanel.SetDock(bottom, Dock.Bottom);
        rail.Children.Add(bottom);
        rail.Children.Add(new Border());
        return rail;
    }

    private static Border RailPill(string text, string help, Action onClick)
    {
        var pill = new Border
        {
            Child = new TextBlock
            {
                Text = text,
                FontFamily = IslandFonts.Ui,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = IslandColors.Brush(IslandColors.White(0.45)),
            },
            CornerRadius = new CornerRadius(10),
            Background = IslandColors.Brush(IslandColors.White(0.05)),
            Padding = new Thickness(9, 4, 9, 4),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = help,
        };
        pill.MouseEnter += (_, _) => pill.Background = IslandColors.Brush(IslandColors.White(0.10));
        pill.MouseLeave += (_, _) => pill.Background = IslandColors.Brush(IslandColors.White(0.05));
        pill.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            onClick();
        };
        return pill;
    }

    private UIElement NavItem(Tab tab)
    {
        var (label, glyph) = TabFace(tab);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Width = 16,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        row.Children.Add(icon);
        var text = new TextBlock
        {
            Text = L10n.Tr(label),
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(text);

        var cell = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(8, 1, 8, 1),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var captured = tab;
        cell.MouseLeftButtonUp += (_, args) =>
        {
            Select(captured);
            args.Handled = true;
        };
        cell.MouseEnter += (_, _) =>
        {
            if (_active != captured) cell.Background = IslandColors.Brush(IslandColors.White(0.04));
        };
        cell.MouseLeave += (_, _) =>
        {
            if (_active != captured) cell.Background = Brushes.Transparent;
        };
        _navItems.Add((tab, cell, icon, text));
        return cell;
    }

    private static UIElement Hairline() => new Border
    {
        Height = 1,
        Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(IslandColors.White(0.055), 0.25),
                new GradientStop(IslandColors.White(0.055), 0.75),
                new GradientStop(Colors.Transparent, 1),
            },
            new Point(0, 0),
            new Point(1, 0)),
    };

    private UIElement BuildFooter()
    {
        var grid = new Grid { Margin = new Thickness(24, 12, 24, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var github = new DottedLink("GitHub", "https://github.com/tristan666666/agent-island");
        github.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(github, 0);
        grid.Children.Add(github);

        var guide = new TextBlock
        {
            Text = L10n.Tr("Guide"),
            FontFamily = IslandFonts.Ui,
            FontSize = 11.5,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            TextDecorations = null,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = L10n.Tr("How Agent Island works"),
        };
        guide.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            WhatsNewWindow.OpenGuide();
        };
        Grid.SetColumn(guide, 1);
        grid.Children.Add(guide);

        var share = new StackPanel { Orientation = Orientation.Horizontal };
        var weekly = new PillButtonControl(L10n.Tr("Weekly")) { ToolTip = L10n.Tr("Share weekly report") };
        weekly.Clicked += () => Report.ReportWindow.Show(Report.ReportWindow.Kind.Weekly);
        weekly.Margin = new Thickness(0, 0, 8, 0);
        share.Children.Add(weekly);
        var monthly = new PillButtonControl(L10n.Tr("Monthly")) { ToolTip = L10n.Tr("Share monthly report") };
        monthly.Clicked += () => Report.ReportWindow.Show(Report.ReportWindow.Kind.Monthly);
        share.Children.Add(monthly);
        Grid.SetColumn(share, 3);
        grid.Children.Add(share);
        return grid;
    }

    /// The stores raise from background completions; hop to the dispatcher
    /// and let each row re-read whatever it shows.
    private void OnProviderStoreChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.BeginInvoke(RefreshProviderRows);

    private void RefreshProviderRows()
    {
        foreach (var refresh in _providerRefreshers)
        {
            refresh();
        }
    }

    private void Select(Tab tab)
    {
        _active = tab;
        // The refreshers (and the slot notice) belong to the tab that built
        // them — anything still in the list points at discarded visuals.
        _providerRefreshers.Clear();
        _slotNotice = null;
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
            Tab.Providers => BuildProviders(),
            Tab.Display => BuildDisplay(),
            Tab.Alerts => BuildAlerts(),
            Tab.General => BuildGeneral(),
            Tab.Status => BuildStatus(),
            Tab.Notes => BuildNotes(),
            Tab.About => BuildAbout(),
            _ => (UIElement)new StackPanel(),
        };
        var pageColumn = new StackPanel();
        pageColumn.Children.Add(new TextBlock
        {
            Text = L10n.Tr(TabFace(tab).Label),
            FontFamily = IslandFonts.Ui,
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.94)),
            Margin = new Thickness(24, 6, 24, 2),
        });
        pageColumn.Children.Add(body);
        _scroll.Content = pageColumn;
    }

    // MARK: - Section helpers

    private static StackPanel TabStack() => new()
    {
        Orientation = Orientation.Vertical,
        Margin = new Thickness(14, 18, 14, 6),
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = L10n.Tr(text).ToUpperInvariant(),
        FontFamily = IslandFonts.Ui,
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Foreground = IslandColors.Brush(IslandColors.White(0.34)),
        Margin = new Thickness(10, 14, 10, 6),
    };

    /// Four 13px color dots (macOS glowColorSwatches): white ring + soft
    /// self-colored halo mark the pick; the ring alone isn't enough at 13px.
    private static UIElement GlowSwatches()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dots = new List<(GlowColorStore.Choice Choice, System.Windows.Shapes.Ellipse Dot)>();
        void Restyle()
        {
            foreach (var (choice, dot) in dots)
            {
                var selected = GlowColorStore.Shared.Value == choice;
                dot.Stroke = IslandColors.Brush(IslandColors.White(selected ? 0.92 : 0.16));
                dot.StrokeThickness = selected ? 1.5 : 0.5;
                dot.Effect = selected
                    ? new System.Windows.Media.Effects.DropShadowEffect
                    {
                        ShadowDepth = 0,
                        BlurRadius = 8,
                        Color = GlowColorStore.ColorOf(choice),
                        Opacity = 0.55,
                    }
                    : null;
            }
        }
        foreach (var choice in GlowColorStore.All)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 13,
                Height = 13,
                Fill = IslandColors.Brush(GlowColorStore.ColorOf(choice)),
            };
            // padding(2) + inset(-3) hit area: a 19px transparent puck.
            var puck = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(3),
                Margin = new Thickness(0, 0, 7, 0),
                Child = dot,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = L10n.Tr(GlowColorStore.LabelKey(choice)),
            };
            var captured = choice;
            puck.MouseLeftButtonDown += (_, e) =>
            {
                GlowColorStore.Shared.Value = captured;
                Restyle();
                e.Handled = true;
            };
            dots.Add((choice, dot));
            row.Children.Add(puck);
        }
        Restyle();
        return row;
    }

    // MARK: - General

    private UIElement BuildGeneral()
    {
        var stack = TabStack();

        var launch = new CobaltToggle(LaunchAtLogin.IsEnabled);
        launch.Toggled += enabled => LaunchAtLogin.SetEnabled(enabled);
        stack.Children.Add(new SettingsRowControl(
            "Launch at Login", null, launch));

        var language = DarkComboStyle.Apply(new ComboBox { Width = 130, VerticalAlignment = VerticalAlignment.Center });
        language.Items.Add(L10n.Tr("Auto (system)"));
        language.Items.Add("English");
        language.Items.Add("简体中文");
        language.SelectedIndex = AppLanguageStore.Load() switch
        {
            L10n.Language.English => 1,
            L10n.Language.SimplifiedChinese => 2,
            _ => 0,
        };
        language.SelectionChanged += (_, _) =>
        {
            var chosen = language.SelectedIndex switch
            {
                1 => L10n.Language.English,
                2 => L10n.Language.SimplifiedChinese,
                _ => L10n.Language.Auto,
            };
            AppLanguageStore.Save(chosen);
            L10n.Current = chosen;
            App.Instance.RebuildForLanguageChange();
            // Recreate this window outright: patching just the title, tab
            // bar, and rows left the brand header and footer in whatever
            // language the window was BORN in (the "inverted slogan" bug).
            // Close() raises Closed synchronously, clearing the singleton,
            // so Open() builds a fresh window on the persisted tab.
            Close();
            Open();
        };
        // Title + picker only — the picker already SHOWS the choice, an
        // echoing subtitle was noise (owner, repeatedly, on macOS).
        stack.Children.Add(new SettingsRowControl(
            "Language", null, language));

        stack.Children.Add(SectionLabel("Updates"));
        var autoCheck = new CobaltToggle(Preferences.Get<bool?>("AgentIsland.autoCheckUpdates") ?? true);
        autoCheck.Toggled += enabled => Preferences.Set("AgentIsland.autoCheckUpdates", enabled);
        stack.Children.Add(new SettingsRowControl(
            "Check for updates automatically",
            null,
            autoCheck));

        var check = new PillButtonControl(L10n.Tr("Check"));
        check.Clicked += () => _ = Update.UpdateChecker.Shared.CheckAsync(userInitiated: true);
        stack.Children.Add(new SettingsRowControl(
            "Check now", null, check));

        return stack;
    }

    /// Alerts get their own page in the 2.1.1 IA — they were buried at the
    /// bottom of General, which is where nobody looks for "why did the
    /// island flash amber".
    private UIElement BuildAlerts()
    {
        var stack = TabStack();
        var alertsHost = new StackPanel();
        var alerts = new CobaltToggle(AlertThresholdStore.Shared.Enabled);
        stack.Children.Add(new SettingsRowControl(
            "Approaching-limit alerts",
            null,
            alerts));

        alertsHost.Children.Add(ThresholdLine(IslandColors.AlertAmber, "Warning",
            () => AlertThresholdStore.Shared.WarningPercent,
            value => AlertThresholdStore.Shared.WarningPercent = value));
        alertsHost.Children.Add(ThresholdLine(IslandColors.AlertRed, "Critical",
            () => AlertThresholdStore.Shared.CriticalPercent,
            value => AlertThresholdStore.Shared.CriticalPercent = value));
        alertsHost.Margin = new Thickness(10, 8, 10, 8);
        alertsHost.Opacity = AlertThresholdStore.Shared.Enabled ? 1.0 : 0.40;
        alertsHost.IsEnabled = AlertThresholdStore.Shared.Enabled;
        alerts.Toggled += enabled =>
        {
            AlertThresholdStore.Shared.Enabled = enabled;
            alertsHost.Opacity = enabled ? 1.0 : 0.40;
            alertsHost.IsEnabled = enabled;
        };
        stack.Children.Add(alertsHost);
        return stack;
    }

    private UIElement BuildNotes()
    {
        var stack = TabStack();
        var view = new PillButtonControl(L10n.Tr("View"));
        view.Clicked += WhatsNewWindow.Open;
        stack.Children.Add(new SettingsRowControl("What's new in this version", null, view));

        var guide = new PillButtonControl(L10n.Tr("Open"));
        guide.Clicked += WhatsNewWindow.OpenGuide;
        stack.Children.Add(new SettingsRowControl("Product guide", null, guide));

        var changelog = new PillButtonControl(L10n.Tr("Open"));
        changelog.Clicked += () => OpenUrl("https://agent-island.dev/changelog/");
        stack.Children.Add(new SettingsRowControl("Full changelog on the website", null, changelog));
        return stack;
    }

    /// The 关于 page — serif manifesto prose plus the open-source facts,
    /// with the ghosted brand mark low-right (macOS 2.1.1 spec).
    private UIElement BuildAbout()
    {
        var host = new Grid { MinHeight = 395, ClipToBounds = true };

        try
        {
            var ghost = new Image
            {
                Source = new BitmapImage(new Uri(
                    "pack://application:,,,/Assets/agentisland_logo.png")),
                Width = 205,
                Opacity = 0.07,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -30, -60),
                RenderTransform = new RotateTransform(-9),
                RenderTransformOrigin = new Point(0.5, 0.5),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 2.5 },
                IsHitTestVisible = false,
            };
            host.Children.Add(ghost);
        }
        catch
        {
        }

        var stack = new StackPanel { Margin = new Thickness(24, 8, 24, 12) };
        stack.Children.Add(new TextBlock
        {
            Text = "Agent Island",
            FontFamily = IslandFonts.Ui,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.95)),
            Margin = new Thickness(0, 0, 0, 16),
        });
        foreach (var paragraph in new[]
        {
            "Agents are part of every builder's day now — Claude Code, Codex, Gemini, Grok, Cursor… and whatever ships next month. They run, you wait, and nobody tells you when it is your turn again",
            "Agent Island 2.0 puts them all on one island. Who is working, whose turn it is, how much quota is left, what it cost — one glance at the notch, no window switching, no terminal tabs to hunt through",
            "Every number is computed on your own computer — Mac or Windows — from logs the agents already write. No account, no telemetry, nothing uploaded",
            "If Agent Island helps you, a star on GitHub and a share with a friend are the two things that keep it going",
        })
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.Tr(paragraph),
                FontFamily = IslandFonts.Ui,
                FontSize = 12.5,
                Foreground = IslandColors.Brush(IslandColors.White(0.74)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }
        stack.Children.Add(new Border
        {
            Height = 0.5,
            Background = IslandColors.Brush(IslandColors.White(0.07)),
            Margin = new Thickness(0, 8, 0, 12),
        });
        stack.Children.Add(AboutFact(L10n.Tr("Made by"), "Tristan Tang", "https://tristan.media"));
        stack.Children.Add(AboutFact(
            L10n.Tr("Sponsor"), L10n.Tr("Star on GitHub"),
            "https://github.com/tristan666666/agent-island"));
        host.Children.Add(stack);
        return host;
    }

    private static UIElement AboutFact(string label, string value, string url)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = IslandFonts.Ui,
            FontSize = 11.5,
            Foreground = IslandColors.Brush(IslandColors.White(0.40)),
            Width = 74,
        });
        row.Children.Add(new TextBlock
        {
            Text = value,
            FontFamily = IslandFonts.Ui,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.80)),
        });
        row.Children.Add(new TextBlock
        {
            Text = " \uE8A7",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 8,
            Foreground = IslandColors.Brush(IslandColors.White(0.30)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            OpenUrl(url);
        };
        return row;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    /// Threshold row: glowing severity dot, label, numeric %-field. The
    /// stores clamp so warning stays below critical.
    private UIElement ThresholdLine(Color color, string label, Func<int> get, Action<int> set)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = IslandColors.Brush(color),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0, BlurRadius = 4, Color = color, Opacity = 0.7,
            },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var text = new TextBlock
        {
            Text = L10n.Tr(label),
            FontFamily = IslandFonts.Ui,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.92)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var fieldHost = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = IslandColors.Brush(IslandColors.White(0.05)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.10)),
            BorderThickness = new Thickness(0.5),
            Width = 64,
            Height = 28,
        };
        var fieldRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var field = new TextBox
        {
            Text = get().ToString(),
            FontFamily = IslandFonts.Mono,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.95)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 26,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CaretBrush = Brushes.White,
        };
        field.LostFocus += (_, _) =>
        {
            if (int.TryParse(field.Text, out var value)) set(value);
            field.Text = get().ToString();
        };
        field.KeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter)
            {
                if (int.TryParse(field.Text, out var value)) set(value);
                field.Text = get().ToString();
            }
        };
        fieldRow.Children.Add(field);
        fieldRow.Children.Add(new TextBlock
        {
            Text = "%",
            FontFamily = IslandFonts.Mono,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        fieldHost.Child = fieldRow;
        Grid.SetColumn(fieldHost, 2);
        grid.Children.Add(fieldHost);
        return grid;
    }

    // MARK: - Display

    private UIElement BuildDisplay()
    {
        var stack = TabStack();

        // 用量显示 — the five visual preview tiles, "click to switch" hint.
        var usageHeader = new Grid { Margin = new Thickness(10, 14, 10, 6) };
        usageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        usageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var usageLabel = SectionLabel("Usage display");
        usageLabel.Margin = new Thickness(0);
        Grid.SetColumn(usageLabel, 0);
        usageHeader.Children.Add(usageLabel);
        stack.Children.Add(usageHeader);

        var stylePicker = new ChartStylePickerControl(StylePreferenceStore.Shared.Style)
        {
            Margin = new Thickness(10, 2, 2, 8),
        };
        stylePicker.StyleSelected += style => StylePreferenceStore.Shared.Style = style;
        stack.Children.Add(stylePicker);

        // 成本显示 — toggle first; the picker tiles appear only when the
        // cost page is on, matching the macOS conditional.
        // macOS SegmentedControl order: items [false, true] → 已用 then 剩余.
        var quotaMode = Model.QuotaDisplayModeStore.Shared;
        var quotaSeg = new Segmented(
            new[] { Localization.L10n.Tr("Used"), Localization.L10n.Tr("Remaining") },
            quotaMode.ShowsRemaining ? 1 : 0);
        quotaSeg.SelectionChanged += index => quotaMode.ShowsRemaining = index == 1;
        stack.Children.Add(new SettingsRowControl(
            "Quota shows",
            null,
            quotaSeg));

        // 成本显示 — the enable toggle rides the section header itself
        // (macOS, 1.7.2 planning): OFF removes the cost page from the panel
        // pager and hides the style tiles entirely.
        var costHeader = new Grid { Margin = new Thickness(10, 14, 10, 6) };
        costHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        costHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var costLabel = SectionLabel("Cost display");
        costLabel.Margin = new Thickness(0);
        Grid.SetColumn(costLabel, 0);
        costHeader.Children.Add(costLabel);
        var costPickerHost = new ContentControl { Margin = new Thickness(10, 2, 2, 8) };
        void RefreshCostPicker()
        {
            if (ScreenPref.Shared.ShowCostPage)
            {
                var picker = new CostStylePickerControl(CostStylePreferenceStore.Shared.Style);
                picker.StyleSelected += style => CostStylePreferenceStore.Shared.Style = style;
                costPickerHost.Content = picker;
            }
            else
            {
                costPickerHost.Content = null;
            }
        }
        var costToggle = new CobaltToggle(ScreenPref.Shared.ShowCostPage);
        costToggle.Toggled += enabled =>
        {
            ScreenPref.Shared.ShowCostPage = enabled;
            RefreshCostPicker();
        };
        Grid.SetColumn(costToggle, 1);
        costHeader.Children.Add(costToggle);
        stack.Children.Add(costHeader);
        RefreshCostPicker();
        stack.Children.Add(costPickerHost);

        // 顶部条.
        stack.Children.Add(SectionLabel("Top bar"));
        // Visual mode lives with the island-appearance controls, title +
        // picker only — no sentence (macOS design review).
        var effects = DarkComboStyle.Apply(new ComboBox { Width = 130, VerticalAlignment = VerticalAlignment.Center });
        effects.Items.Add(L10n.Tr("Calm"));
        effects.Items.Add(L10n.Tr("Vivid"));
        effects.Items.Add(L10n.Tr("Follow model"));
        effects.SelectedIndex = LowPowerModeStore.Shared.Mode switch
        {
            Model.VisualMode.Calm => 0,
            Model.VisualMode.Vivid => 1,
            Model.VisualMode.FollowModel => 2,
            _ => 1,
        };
        stack.Children.Add(new SettingsRowControl(
            "Visual mode",
            null,
            effects));

        // Glow color rides under Vivid only — Calm has no ambient light, Follow model follows the active AI.
        var glowRow = new SettingsRowControl("Glow color", null, GlowSwatches());
        glowRow.Visibility = LowPowerModeStore.Shared.Mode == Model.VisualMode.Vivid
            ? Visibility.Visible
            : Visibility.Collapsed;
        stack.Children.Add(glowRow);
        effects.SelectionChanged += (_, _) =>
        {
            var selectedMode = effects.SelectedIndex switch
            {
                0 => Model.VisualMode.Calm,
                1 => Model.VisualMode.Vivid,
                2 => Model.VisualMode.FollowModel,
                _ => Model.VisualMode.Vivid,
            };
            LowPowerModeStore.Shared.Mode = selectedMode;
            glowRow.Visibility = selectedMode == Model.VisualMode.Vivid
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        // Interface scale (macOS row order: Visual mode → Glow color →
        // Interface scale). Every Windows screen is notchless, so it just
        // applies — no "notchless only" caveat needed.
        var scaleBox = DarkComboStyle.Apply(new ComboBox { Width = 90, VerticalAlignment = VerticalAlignment.Center });
        var scaleSteps = new[] { 1.0, 1.15, 1.3, 1.5 };
        foreach (var step in scaleSteps) scaleBox.Items.Add($"{Math.Round(step * 100)}%");
        var currentScale = IslandScaleStore.Shared.Scale;
        var scaleIndex = Array.FindIndex(scaleSteps, s => Math.Abs(s - currentScale) < 0.01);
        scaleBox.SelectedIndex = scaleIndex < 0 ? 0 : scaleIndex;
        scaleBox.SelectionChanged += (_, _) =>
        {
            if (scaleBox.SelectedIndex >= 0)
                IslandScaleStore.Shared.Scale = scaleSteps[scaleBox.SelectedIndex];
        };
        stack.Children.Add(new SettingsRowControl(
            "Interface scale",
            null,
            scaleBox));

        var alwaysShow = new CobaltToggle(AlwaysShowUsageStore.Shared.Enabled);
        alwaysShow.Toggled += enabled => AlwaysShowUsageStore.Shared.Enabled = enabled;
        stack.Children.Add(new SettingsRowControl(
            "Always show usage in top bar",
            null,
            alwaysShow));


        // 屏幕. (The macOS bar-style choice — Compact vs Notched Mac — is
        // meaningless on Windows, where no display has a notch; the bar is
        // always the wide layout.)
        stack.Children.Add(SectionLabel("Screen"));
        var screens = System.Windows.Forms.Screen.AllScreens;
        var display = DarkComboStyle.Apply(new ComboBox { Width = 180, VerticalAlignment = VerticalAlignment.Center });
        display.Items.Add(L10n.Tr("Auto"));
        foreach (var screen in screens)
        {
            display.Items.Add(screen.DeviceName.TrimStart('\\', '.') + (screen.Primary ? " ★" : ""));
        }
        var choice = IslandTargetDisplayStore.Shared.Choice;
        display.SelectedIndex = choice == "auto"
            ? 0
            : Math.Max(0, Array.FindIndex(screens, s => s.DeviceName == choice) + 1);
        display.SelectionChanged += (_, _) =>
            IslandTargetDisplayStore.Shared.Choice = display.SelectedIndex <= 0
                ? "auto"
                : screens[display.SelectedIndex - 1].DeviceName;
        stack.Children.Add(new SettingsRowControl(
            "Show on",
            choice == "auto"
                ? L10n.Tr("Auto — picks the best available screen.")
                : L10n.Tr("Pinned to a specific display. Falls back to Auto if unplugged."),
            display));

        // 位置 — no notch reserves the top-center on Windows, so placement is
        // a user choice: the Mac-style top bar or a draggable floating widget.
        stack.Children.Add(SectionLabel("Position"));
        var position = IslandPositionStore.Shared;

        var placements = new[] { IslandPlacement.TopBar, IslandPlacement.Floating };
        var placementBox = DarkComboStyle.Apply(new ComboBox { Width = 180, VerticalAlignment = VerticalAlignment.Center });
        foreach (var mode in placements) placementBox.Items.Add(PlacementLabel(mode));
        placementBox.SelectedIndex = Math.Max(0, Array.IndexOf(placements, position.Placement));
        placementBox.SelectionChanged += (_, _) =>
        {
            if (placementBox.SelectedIndex >= 0)
            {
                position.Placement = placements[placementBox.SelectedIndex];
            }
        };
        stack.Children.Add(new SettingsRowControl(
            "Island position",
            "A bar at the top of the screen, or a floating widget you drag anywhere.",
            placementBox));

        // The old "center when solo" toggle is gone: a lone subscription now
        // always splits the flanks (logo one side, number the other), the
        // macOS solo layout — no setting to hunt for.

        return stack;
    }

    private static string PlacementLabel(IslandPlacement mode) => mode switch
    {
        IslandPlacement.TopBar => L10n.Tr("Top bar"),
        IslandPlacement.Floating => L10n.Tr("Floating window"),
        _ => mode.ToString(),
    };

    // MARK: - Providers

    private class SortableProviderStack : StackPanel
    {
        private readonly Border _dropIndicator = new()
        {
            Height = 2,
            Margin = new Thickness(14, 1, 14, 1),
            CornerRadius = new CornerRadius(1),
            Background = IslandColors.Brush(IslandColors.White(0.85)),
            IsHitTestVisible = false,
        };

        private UIElement? _pressedRow;
        private UIElement? _draggingRow;
        private Point _pressPoint;
        private int _dropIndex = -1;
        private double _draggingOpacity = 1;

        public SortableProviderStack()
        {
            AllowDrop = true;
            DragOver += OnDragOver;
            Drop += OnDrop;
        }

        /// Makes the provider row draggable after the pointer moves far enough
        /// to distinguish a reorder gesture from a click on a row control.
        /// Interactive descendants (toggle/action buttons) remain clickable.
        public void AttachRow(UIElement row)
        {
            row.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (_draggingRow is not null || IsInteractiveSource(e.OriginalSource as DependencyObject))
                {
                    return;
                }

                _pressedRow = row;
                _pressPoint = e.GetPosition(this);
                row.CaptureMouse();
            };
            row.PreviewMouseMove += (_, e) =>
            {
                if (!ReferenceEquals(_pressedRow, row) || _draggingRow is not null)
                {
                    return;
                }
                if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
                {
                    CancelPendingDrag(row);
                    return;
                }

                var point = e.GetPosition(this);
                var dy = point.Y - _pressPoint.Y;
                if (Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }

                StartDrag(row, e);
            };
            row.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (_draggingRow is not null)
                {
                    return;
                }
                CancelPendingDrag(row);
            };
            row.LostMouseCapture += (_, _) =>
            {
                if (_draggingRow is null && ReferenceEquals(_pressedRow, row))
                {
                    _pressedRow = null;
                }
            };
        }

        private void StartDrag(UIElement row, System.Windows.Input.MouseEventArgs e)
        {
            var rows = ProviderRows();
            var sourceIndex = rows.IndexOf(row);
            if (sourceIndex < 0) return;

            _draggingRow = row;
            _dropIndex = sourceIndex;
            if (row is FrameworkElement element)
            {
                _draggingOpacity = element.Opacity;
                element.Opacity = 0.48;
            }
            ShowDropIndicator(_dropIndex);

            // DoDragDrop owns the mouse loop. Release the temporary capture
            // first, then always clear state when the gesture is accepted or
            // cancelled so a failed drop cannot poison the next drag.
            if (ReferenceEquals(System.Windows.Input.Mouse.Captured, row))
            {
                row.ReleaseMouseCapture();
            }
            e.Handled = true;
            try
            {
                DragDrop.DoDragDrop(row, row, DragDropEffects.Move);
            }
            finally
            {
                FinishDrag();
            }
        }

        private void CancelPendingDrag(UIElement row)
        {
            if (!ReferenceEquals(_pressedRow, row) || _draggingRow is not null)
            {
                return;
            }
            if (ReferenceEquals(System.Windows.Input.Mouse.Captured, row))
            {
                row.ReleaseMouseCapture();
            }
            _pressedRow = null;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (_draggingRow == null) return;
            e.Effects = DragDropEffects.Move;
            _dropIndex = CalculateDropIndex(e.GetPosition(this));
            ShowDropIndicator(_dropIndex);
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (_draggingRow == null) return;
            var rows = ProviderRows();
            var oldIndex = rows.IndexOf(_draggingRow);
            var targetIndex = Math.Clamp(_dropIndex >= 0
                ? _dropIndex
                : CalculateDropIndex(e.GetPosition(this)), 0, rows.Count);
            var newIndex = targetIndex > oldIndex ? targetIndex - 1 : targetIndex;

            RemoveDropIndicator();
            if (oldIndex >= 0 && newIndex != oldIndex && newIndex >= 0 && newIndex < rows.Count)
            {
                Children.RemoveAt(oldIndex);
                Children.Insert(newIndex, _draggingRow);
                ProviderVisibilityStore.Shared.MoveProvider(oldIndex, newIndex);
            }
            e.Handled = true;
            FinishDrag();
        }

        private int CalculateDropIndex(Point position)
        {
            var rows = ProviderRows();
            for (var i = 0; i < rows.Count; i++)
            {
                var rowPosition = rows[i].TranslatePoint(new Point(0, 0), this);
                if (position.Y < rowPosition.Y + rows[i].RenderSize.Height / 2)
                {
                    return i;
                }
            }
            return rows.Count;
        }

        private List<UIElement> ProviderRows() => Children
            .Cast<UIElement>()
            .Where(child => !ReferenceEquals(child, _dropIndicator))
            .ToList();

        private void ShowDropIndicator(int index)
        {
            RemoveDropIndicator();
            var rowCount = ProviderRows().Count;
            Children.Insert(Math.Clamp(index, 0, rowCount), _dropIndicator);
        }

        private void RemoveDropIndicator()
        {
            if (ReferenceEquals(_dropIndicator.Parent, this))
            {
                Children.Remove(_dropIndicator);
            }
        }

        private void FinishDrag()
        {
            if (_draggingRow is FrameworkElement element)
            {
                element.Opacity = _draggingOpacity;
            }
            RemoveDropIndicator();
            _draggingRow = null;
            _pressedRow = null;
            _dropIndex = -1;
        }

        private static bool IsInteractiveSource(DependencyObject? source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is CobaltToggle
                    || current is PillButtonControl
                    || current is System.Windows.Controls.Primitives.ButtonBase
                    || current is System.Windows.Controls.Primitives.TextBoxBase
                    || current is System.Windows.Controls.Primitives.Selector)
                {
                    return true;
                }

                // Codex's account action is a styled Border rather than a
                // Button, so its Hand cursor is the interaction marker.
                if (current is FrameworkElement element
                    && element.Cursor == System.Windows.Input.Cursors.Hand)
                {
                    return true;
                }

                current = current is Visual
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }
    }

    private UIElement BuildProviders()
    {
        var stack = TabStack();
        stack.Children.Add(BuildSlotHeader());

        var sortableHost = new SortableProviderStack();
        foreach (var provider in ProviderVisibilityStore.Shared.Order)
        {
            sortableHost.Children.Add(ProviderRow(provider, sortableHost));
        }
        stack.Children.Add(sortableHost);

        var refreshPresets = RefreshIntervalStore.Presets;
        var refreshRow = new Segmented(new[] { "5m", "15m", "30m" },
            Math.Max(0, Array.IndexOf(refreshPresets, RefreshIntervalStore.Shared.Seconds)));
        refreshRow.SelectionChanged += index => RefreshIntervalStore.Shared.Seconds = refreshPresets[index];
        stack.Children.Add(new SettingsRowControl(
            "Refresh interval", null, refreshRow));

        stack.Children.Add(SectionLabel("Tokens"));
        var mode = new Segmented(
            new[] { L10n.Tr("All tokens"), L10n.Tr("Input + output") },
            TokenCountModeStore.Shared.Mode == TokenCountMode.All ? 0 : 1);
        mode.SelectionChanged += index =>
            TokenCountModeStore.Shared.Mode = index == 0 ? TokenCountMode.All : TokenCountMode.Billable;
        stack.Children.Add(new SettingsRowControl(
            "Token counting",
            TokenCountModeStore.Shared.Mode == TokenCountMode.All
                ? "Input, output, and cache."
                : "Input and output only.",
            mode));

        // Cost freshness strip: section label + last-scan caption + Refresh.
        var costRow = new Grid { Margin = new Thickness(10, 14, 10, 14) };
        costRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        costRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        costRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var costLabel = SectionLabel("Cost");
        costLabel.Margin = new Thickness(0);
        costLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(costLabel, 0);
        costRow.Children.Add(costLabel);
        var costCaption = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(0.42)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        costCaption.Text = Cost.CostStore.Shared.LastUpdated is { } updated
            ? L10n.TrFormat("last scan {0}", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese))
            : L10n.Tr("swipe panel to view");
        Grid.SetColumn(costCaption, 1);
        costRow.Children.Add(costCaption);
        var costRefresh = new PillButtonControl(L10n.Tr("Refresh"));
        costRefresh.Clicked += () => Cost.CostStore.Shared.Refresh();
        Grid.SetColumn(costRefresh, 2);
        costRow.Children.Add(costRefresh);
        stack.Children.Add(costRow);

        return stack;
    }

    /// Slot occupancy spoken by structure instead of a caption sentence: the
    /// enabled brand marks plus "N / 2". A refused third pick parks its one
    /// line of explanation on the same row.
    private UIElement BuildSlotHeader()
    {
        var row = new Grid { Margin = new Thickness(10, 0, 10, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _slotNotice = new TextBlock
        {
            Text = L10n.Tr("Pick at most two — turn one off first"),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.AlertAmber),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 10, 0),
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(_slotNotice, 0);
        row.Children.Add(_slotNotice);

        var marks = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var count = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            // White capsule voice (macOS chrome) — the enabled marks carry
            // the only color on this row.
            Foreground = IslandColors.Brush(IslandColors.White(0.92)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var capsuleBody = new StackPanel { Orientation = Orientation.Horizontal };
        capsuleBody.Children.Add(marks);
        capsuleBody.Children.Add(count);
        var capsule = new Border
        {
            Child = capsuleBody,
            CornerRadius = new CornerRadius(11),
            Background = IslandColors.Brush(IslandColors.White(0.10)),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(capsule, 1);
        row.Children.Add(capsule);

        void Refresh()
        {
            marks.Children.Clear();
            foreach (var provider in ProviderVisibilityStore.Shared.Enabled)
            {
                // Real brand marks at 12px (macOS ProviderMark).
                var mark = ProviderMarks.Mark(provider, 12, tintOpacity: 0.9);
                ((FrameworkElement)mark).Margin = new Thickness(0, 0, 7, 0);
                marks.Children.Add(mark);
            }
            count.Text = $"{ProviderVisibilityStore.Shared.SelectedCount} / {ProviderSelection.MaxEnabled}";
        }

        _providerRefreshers.Add(Refresh);
        Refresh();
        return row;
    }

    private void ShowSlotLimit(bool refused)
    {
        if (_slotNotice is null) return;
        _slotNotice.Visibility = refused ? Visibility.Visible : Visibility.Collapsed;
    }

    /// One provider row: a brand-tinted rule marks the leading edge, the name
    /// carries the plan/tier chip, the status line is live, and the slot
    /// toggle sits on the trailing edge behind whatever actions the provider
    /// offers. Guests carry no action buttons — their only recovery is signing
    /// in with their own tool. No card chrome: separation is a hairline plus
    /// whitespace (macOS owner call, 2026-08-08: 不喜欢卡片质感).
    private UIElement ProviderRow(DisplayProvider provider, SortableProviderStack sortableHost)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var handle = new TextBlock
        {
            Text = "\uE76F", // GripperBarHorizontal
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = IslandColors.Brush(IslandColors.White(0.3)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = L10n.Tr("Drag to reorder"),
        };
        Grid.SetColumn(handle, 0);
        grid.Children.Add(handle);

        // The real brand mark leads the row (macOS providerCard: 20pt mark
        // in a 24pt slot, 12pt gap). The brand-tinted rule on the leading
        // edge STAYS — rule + mark together are the row's identity.
        var markHost = new Grid
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        markHost.Children.Add(ProviderMarks.Mark(provider, 20));
        Grid.SetColumn(markHost, 1);
        grid.Children.Add(markHost);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = provider.DisplayName(),
            FontFamily = IslandFonts.Ui,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.95)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var chipText = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.62)),
        };
        var chip = new Border
        {
            Child = chipText,
            CornerRadius = new CornerRadius(3),
            Background = IslandColors.Brush(IslandColors.White(0.07)),
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        titleRow.Children.Add(chip);

        var status = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(0.66)),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleRow);
        text.Children.Add(status);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        PillButtonControl? reauth = null;
        PillButtonControl? pasteLogin = null;
        if (provider.HasFullMonitoring())
        {
            var tool = provider.ToTriggerTool();
            if (provider == DisplayProvider.Codex)
            {
                trailing.Children.Add(CodexAccountMenuButton());
            }
            if (provider == DisplayProvider.Claude)
            {
                // ONE sign-in button by default. The code fallback appears in
                // the same spot only after a browser round has actually
                // failed — progressive disclosure, not a toolbar (owner
                // review, 2026-08-08: 搞成这样子很奇怪).
                pasteLogin = new PillButtonControl(L10n.Tr("Sign in with a code"))
                {
                    Margin = new Thickness(0, 0, 8, 0),
                    Visibility = Visibility.Collapsed,
                };
                pasteLogin.Clicked += StartClaudePasteLogin;
                trailing.Children.Add(pasteLogin);
            }
            reauth = new PillButtonControl(L10n.Tr("Re-authenticate"))
            {
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Collapsed,
            };
            reauth.Clicked += () => ReauthFlow.Run(tool);
            trailing.Children.Add(reauth);
        }
        var toggle = new CobaltToggle(ProviderVisibilityStore.Shared.IsEnabled(provider))
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        trailing.Children.Add(toggle);
        Grid.SetColumn(trailing, 3);
        grid.Children.Add(trailing);

        // macOS providerCard chrome: no boxes — a brand-gradient rule on the
        // leading edge (Google's four hues for Antigravity), separation by a
        // bottom hairline + whitespace, and hover breathing a faint brand
        // wash across the row while the rule brightens and widens a hair.
        var content = new Border
        {
            Child = grid,
            Padding = new Thickness(14, 13, 4, 13),
            Background = Brushes.Transparent,
        };
        var washStops = new GradientStopCollection();
        var stops = ProviderIdentity.BrandStops(provider);
        for (var i = 0; i < stops.Count; i++)
        {
            washStops.Add(new GradientStop(
                IslandColors.Alpha(stops[i], 0.05), 0.7 * i / Math.Max(1, stops.Count - 1)));
        }
        washStops.Add(new GradientStop(Colors.Transparent, 1));
        var wash = new Border
        {
            Background = new LinearGradientBrush(washStops, new Point(0, 0), new Point(1, 0)),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        var rule = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };
        var hairline = new Border
        {
            Height = 1,
            Background = IslandColors.Brush(IslandColors.White(0.05)),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
        };
        var row = new Grid { Background = Brushes.Transparent };
        row.Children.Add(wash);
        row.Children.Add(content);
        row.Children.Add(rule);
        row.Children.Add(hairline);

        var hovered = false;
        void Paint()
        {
            var enabledNow = ProviderVisibilityStore.Shared.IsEnabled(provider);
            var ruleOpacity = enabledNow ? (hovered ? 1.0 : 0.85) : (hovered ? 0.45 : 0.22);
            rule.Background = ProviderIdentity.BrandGradient(
                provider, 1, new Point(0, 0), new Point(0, 1));
            var ease = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            };
            var beat = new Duration(TimeSpan.FromMilliseconds(160));
            wash.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(hovered ? 1 : 0, beat) { EasingFunction = ease });
            rule.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(ruleOpacity, beat) { EasingFunction = ease });
            rule.BeginAnimation(WidthProperty,
                new System.Windows.Media.Animation.DoubleAnimation(hovered ? 3 : 2, beat) { EasingFunction = ease });
        }
        row.MouseEnter += (_, _) =>
        {
            hovered = true;
            Paint();
        };
        row.MouseLeave += (_, _) =>
        {
            hovered = false;
            Paint();
        };
        sortableHost.AttachRow(row);

        toggle.Toggled += enabled =>
        {
            // A third pick is REFUSED, never a silent eviction of an earlier
            // one. The switch snaps back to what the store actually holds
            // instead of sitting there showing a state nobody accepted, and
            // the header says why in one line.
            if (!ProviderVisibilityStore.Shared.SetEnabled(provider, enabled))
            {
                toggle.IsOn = ProviderVisibilityStore.Shared.IsEnabled(provider);
                ShowSlotLimit(true);
                return;
            }
            ShowSlotLimit(false);
            if (enabled) KickGuestRefresh(provider);
            RefreshProviderRows();
        };

        void Refresh()
        {
            toggle.IsOn = ProviderVisibilityStore.Shared.IsEnabled(provider);
            status.Text = ProviderStatus(provider);
            var badge = ProviderChip(provider);
            chipText.Text = badge ?? string.Empty;
            chip.Visibility = string.IsNullOrEmpty(badge) ? Visibility.Collapsed : Visibility.Visible;
            if (reauth is not null)
            {
                var available = provider == DisplayProvider.Claude
                    ? ClaudeReauthAvailable()
                    : CodexReauthAvailable();
                var waiting = provider == DisplayProvider.Claude
                    ? UsageStore.Shared.ClaudeReauthInProgress
                    : UsageStore.Shared.CodexReauthInProgress;
                reauth.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                reauth.Label = waiting ? L10n.Tr("waiting for login…") : L10n.Tr("Re-authenticate");
            }
            if (pasteLogin is not null)
            {
                var store = UsageStore.Shared;
                pasteLogin.Visibility = store.ClaudeReauthFailureCaption is not null
                    && !store.ClaudeReauthInProgress
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            Paint();
        }

        _providerRefreshers.Add(Refresh);
        Refresh();
        return row;
    }

    /// A guest slot just turned on: fetch now instead of waiting out the next
    /// usage poll. The guest stores gate themselves on the selection, so an
    /// unselected provider had never fetched at all.
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
            // A badge for a provider with no login on this machine would be a
            // leftover from a cached snapshot, not a fact about this machine.
            DisplayProvider.Antigravity =>
                visibility.AntigravityDetected ? AntigravityUsageStore.Shared.TierBadge : null,
            DisplayProvider.Grok => visibility.GrokDetected ? GrokUsageStore.Shared.AuthModeBadge : null,
            DisplayProvider.Cursor => visibility.CursorDetected ? CursorUsageStore.Shared.PlanBadge : null,
            _ => null,
        };
    }

    /// "synced 2m ago · 69% / 33%" — the most authoritative diagnostic
    /// surface; errors surface in place of the numbers. A single-window
    /// provider (Codex's one weekly quota) shows one percentage, not a
    /// phantom pair (macOS secondaryMissing).
    private static string ProviderSubtitle(AppUsage usage)
    {
        var synced = UsageStore.Shared.LastUpdated is { } updated
            ? L10n.TrFormat("synced {0}", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese))
            : L10n.Tr("idle");
        var five = WindowCaption(usage.FiveHour);
        var week = WindowCaption(usage.Weekly);
        // Both windows failing the same way is ONE fact — "⚠ login required /
        // ⚠ login required" read as a stutter bug (macOS owner screenshot,
        // 2026-08-08).
        var caption = usage.SecondaryMissing || (five == week && five.StartsWith("⚠", StringComparison.Ordinal))
            ? five
            : $"{five} / {week}";
        return $"{synced} · {caption}";
    }

    private static string WindowCaption(WindowUsage window)
    {
        // Gate on the PRINTED number, not the raw fraction: Swift tests
        // percentInt, so a merged stale window sitting at 0.4% printed "0%"
        // on Windows while macOS surfaced its error caption.
        var percent = Formatting.PercentInt(window.UsedPercent);
        if (window.Error is { } error && percent == 0)
        {
            return "⚠ " + ErrorDisplay.Localize(error);
        }
        return $"{percent}%";
    }

    /// Guest rows read like the Claude/Codex rows — sync freshness first, then
    /// the quota numbers. The account email deliberately stays out of the line:
    /// a row leading with a bare email read as a glitch (macOS owner report,
    /// 2026-08-08); identity lives in the usage-strip hover instead.
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
            // Only the Gemini pool: Claude and GPT are other providers'
            // rows in this app, and showing their shared pool here read as
            // cross-wiring (owner call, 2026-08-09).
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

    /// A failed browser sign-in has to SAY what went wrong. The old path
    /// swallowed the reason and spawned a terminal running a retired CLI
    /// command, which surfaced as an "authentication failed" from nowhere
    /// (owner repro, 2026-08-08). The reason replaces the usage numbers only
    /// while it is fresh — a later successful round clears it.
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

    /// #31: the button rides the CURRENT auth state, not the mere presence of
    /// a CLI on disk — the inherited gate parked a permanent Re-authenticate
    /// beside a perfectly healthy row (owner review, 2026-08-08:
    /// 已经登录了为什么会出现重新认证). Kept visible while a login is in
    /// flight so the "waiting" state doesn't vanish mid-flow. Claude's web
    /// login needs no CLI, so there is no binary check here at all.
    private static bool ClaudeReauthAvailable()
    {
        if (UsageStore.Shared.ClaudeReauthInProgress) return true;
        var usage = UsageStore.Shared.Claude;
        return ClaudeCredentials.IsAuthRecoverableError(usage.FiveHour.Error)
            || ClaudeCredentials.IsAuthRecoverableError(usage.Weekly.Error);
    }

    /// Codex has no in-app login, so a locatable CLI stays a precondition for
    /// the terminal flow — but it is not, on its own, a reason to offer
    /// re-auth. Mirrors macOS CodexCredentials.canPromptReauth(usage:), which
    /// the Windows CodexCredentials.CanPromptReauth() (CLI-only) does not.
    private static bool CodexReauthAvailable()
    {
        if (UsageStore.Shared.CodexReauthInProgress) return true;
        // Caption test FIRST. CanPromptReauth is a full disk probe (dozens of
        // stats plus a PATH scan), and this runs on the UI thread once per
        // PropertyChanged from five stores — roughly seven times per poll
        // while the window is open. A healthy Codex row never pays for it.
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

    /// One-click Codex account switching: park the live login under a name,
    /// swap between parked logins, and opt into auto-rotation when the active
    /// account runs dry (macOS codexAccountMenu). The label turns amber once
    /// the auto-switcher has rotated on its own — that is the only trace it
    /// leaves, and the row should say so.
    private UIElement CodexAccountMenuButton()
    {
        var label = new TextBlock
        {
            Text = L10n.Tr("Accounts"),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };
        var button = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(6),
            Background = IslandColors.Brush(IslandColors.White(0.09)),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.MouseLeftButtonUp += (_, args) =>
        {
            OpenCodexAccountMenu(button);
            args.Handled = true;
        };

        void Refresh()
        {
            var switched = UsageStore.Shared.CodexAutoSwitched;
            label.Foreground = IslandColors.Brush(switched is null
                ? IslandColors.White(0.85)
                : IslandColors.Alpha(IslandColors.AlertAmber, 0.9));
            button.ToolTip = switched is null
                ? L10n.Tr("Switch Codex account")
                : L10n.TrFormat("Auto-switched to {0}", switched);
        }

        _providerRefreshers.Add(Refresh);
        Refresh();
        return button;
    }

    private void OpenCodexAccountMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Background = IslandColors.Brush(Color.FromRgb(0x12, 0x12, 0x16)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.10)),
            BorderThickness = new Thickness(1),
            Foreground = IslandColors.Brush(IslandColors.White(0.90)),
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
        };

        var accounts = CodexAccountSwitcher.Accounts();
        var active = CodexAccountSwitcher.ActiveLabel();
        if (accounts.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = L10n.Tr("No saved accounts yet"),
                IsEnabled = false,
            });
        }
        else
        {
            foreach (var account in accounts)
            {
                var captured = account;
                // The tick lives in the header text, not IsChecked: a
                // checkable item toggles its own mark on click and would then
                // claim a login is live before the swap even succeeded.
                var item = new MenuItem
                {
                    Header = string.Equals(account.Label, active, StringComparison.Ordinal)
                        ? "✓ " + account.Label
                        : account.Label,
                };
                item.Click += (_, _) =>
                {
                    if (!CodexAccountSwitcher.Activate(captured)) return;
                    // The badge described the previous rotation; a manual pick
                    // supersedes it (macOS parity).
                    UsageStore.Shared.CodexAutoSwitched = null;
                    UsageStore.Shared.Refresh();
                    RefreshProviderRows();
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = L10n.Tr("Remove saved account") };
            foreach (var account in accounts)
            {
                var captured = account;
                var child = new MenuItem { Header = account.Label };
                child.Click += (_, _) =>
                {
                    CodexAccountSwitcher.Forget(captured);
                    RefreshProviderRows();
                };
                remove.Items.Add(child);
            }
            menu.Items.Add(remove);
            menu.Items.Add(new Separator());
        }

        var save = new MenuItem { Header = L10n.Tr("Save current account…") };
        save.Click += (_, _) => PromptParkCodexAccount();
        menu.Items.Add(save);
        menu.Items.Add(new Separator());

        var auto = new MenuItem
        {
            Header = L10n.Tr("Auto-switch when exhausted"),
            IsCheckable = true,
            IsChecked = CodexAccountSwitcher.AutoSwitchEnabled,
        };
        auto.Click += (_, _) => CodexAccountSwitcher.AutoSwitchEnabled = auto.IsChecked;
        menu.Items.Add(auto);

        menu.IsOpen = true;
    }

    /// Paste-code sign-in: open Anthropic's own code page, let it show a code,
    /// take that code back through a plain text field. Independent of which
    /// browser profile holds the claude.ai session, and of whether a loopback
    /// listener can bind at all — which is exactly why it is the fallback the
    /// failure caption unlocks.
    private void StartClaudePasteLogin()
    {
        var ticket = ClaudeCredentials.BeginPasteLogin();
        // The return value is deliberately ignored: if the default browser
        // won't open, the dialog's own "Copy link" is the remedy, and showing
        // an error instead would hide it.
        _ = ClaudeWebLogin.OpenInBrowser(ticket.Url);
        var pasted = NamePrompt.Ask(
            this,
            L10n.Tr("Sign in with a code"),
            L10n.Tr("Approve the page that just opened, then paste the code it shows here"),
            L10n.Tr("Paste the code"),
            confirmLabel: L10n.Tr("Sign in"),
            // An authorization code plus its state fragment runs well past the
            // 40-character label cap the account prompt uses.
            maxLength: 4096,
            extraLabel: L10n.Tr("Copy link"),
            extraAction: () =>
            {
                // Clipboard access can throw when another process holds it
                // open; a failed copy must not take down the dialog.
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
            UsageStore.Shared.ClearClaudeReauthFailure();
            UsageStore.Shared.Refresh();
            RefreshProviderRows();
            return;
        }
        IslandDialog.ShowApp(
            L10n.Tr("That code did not work"),
            L10n.Tr("Copy the whole code from the page and try once more"),
            secondaryLabel: L10n.Tr("OK"));
    }

    private void PromptParkCodexAccount()
    {
        var label = NamePrompt.Ask(
            this,
            L10n.Tr("Save current account"),
            L10n.Tr("Give this login a name so you can switch back to it later"),
            L10n.Tr("work / personal"));
        if (label is null) return;
        if (!CodexAccountSwitcher.ParkCurrent(label)) return;
        RefreshProviderRows();
    }

    /// Single-field name prompt for parking the live Codex login — the
    /// island-styled stand-in for macOS's NSAlert accessory text field.
    /// IslandDialog carries no input form and this is the only place in the
    /// app that needs one.
    private sealed class NamePrompt : Window
    {
        private readonly TextBox _field;
        private bool _accepted;

        private NamePrompt(
            string title,
            string message,
            string placeholder,
            string confirmLabel,
            int maxLength,
            string? extraLabel,
            Action? extraAction)
        {
            Width = 400;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Title = title;
            System.Windows.Media.TextOptions.SetTextFormattingMode(
                this, System.Windows.Media.TextFormattingMode.Display);

            var card = new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = IslandColors.Brush(IslandColors.AlarmBackground),
                BorderBrush = IslandColors.Brush(IslandColors.White(0.07)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(12),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    ShadowDepth = 4,
                    Direction = 270,
                    BlurRadius = 18,
                    Color = Colors.Black,
                    Opacity = 0.55,
                },
            };
            Content = card;

            var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
            card.Child = stack;
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = IslandFonts.Ui,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = IslandColors.Brush(IslandColors.White(0.95)),
            });
            stack.Children.Add(new TextBlock
            {
                Text = message,
                FontFamily = IslandFonts.Ui,
                FontSize = 12,
                Foreground = IslandColors.Brush(IslandColors.White(0.60)),
                Margin = new Thickness(0, 6, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            _field = new TextBox
            {
                FontFamily = IslandFonts.Ui,
                FontSize = 13,
                Foreground = IslandColors.Brush(IslandColors.White(0.95)),
                CaretBrush = IslandColors.Brush(IslandColors.White(0.80)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                // The switcher caps stored labels at 40 characters anyway;
                // stopping here means the name the user typed is the name
                // they get back in the menu. An OAuth code is far longer, so
                // the cap is per-prompt rather than a constant.
                MaxLength = maxLength,
            };
            var hint = new TextBlock
            {
                Text = placeholder,
                FontFamily = IslandFonts.Ui,
                FontSize = 13,
                Foreground = IslandColors.Brush(IslandColors.White(0.28)),
                IsHitTestVisible = false,
            };
            _field.TextChanged += (_, _) =>
                hint.Visibility = _field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var fieldLayer = new Grid();
            fieldLayer.Children.Add(hint);
            fieldLayer.Children.Add(_field);
            stack.Children.Add(new Border
            {
                Child = fieldLayer,
                CornerRadius = new CornerRadius(8),
                Background = IslandColors.Brush(IslandColors.White(0.06)),
                BorderBrush = IslandColors.Brush(IslandColors.White(0.10)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            if (extraLabel is not null && extraAction is { } onExtra)
            {
                // "Copy link" is a side action, not an exit: it puts the URL
                // on the clipboard and leaves the dialog up, because the user
                // still has to come back and paste the code.
                var extra = new PillButtonControl(extraLabel) { Margin = new Thickness(0, 0, 8, 0) };
                extra.Clicked += () => onExtra();
                buttons.Children.Add(extra);
            }
            var cancel = new PillButtonControl(L10n.Tr("Cancel")) { Margin = new Thickness(0, 0, 8, 0) };
            cancel.Clicked += Close;
            var confirm = new PillButtonControl(confirmLabel);
            confirm.Clicked += Accept;
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            stack.Children.Add(buttons);

            KeyDown += (_, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter) Accept();
                else if (args.Key == System.Windows.Input.Key.Escape) Close();
            };
            Loaded += (_, _) => _field.Focus();
        }

        private void Accept()
        {
            _accepted = true;
            Close();
        }

        /// The typed text, or null when the prompt was dismissed or left blank.
        public static string? Ask(
            Window owner,
            string title,
            string message,
            string placeholder,
            string? confirmLabel = null,
            int maxLength = 40,
            string? extraLabel = null,
            Action? extraAction = null)
        {
            var prompt = new NamePrompt(
                title,
                message,
                placeholder,
                confirmLabel ?? L10n.Tr("Save"),
                maxLength,
                extraLabel,
                extraAction)
            {
                Owner = owner,
            };
            prompt.ShowDialog();
            if (!prompt._accepted) return null;
            var value = prompt._field.Text.Trim();
            return value.Length == 0 ? null : value;
        }
    }

    // MARK: - Status

    private UIElement BuildStatus()
    {
        var stack = TabStack();
        stack.Children.Add(new TextBlock
        {
            Text = L10n.Tr("What the island's two logos are telling you."),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(0.5)),
            Margin = new Thickness(10, 0, 10, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        // One glyph language (macOS StatePreviewLogo): the symmetric mark
        // spinning / steady with a bell badge / pulsing red — no more
        // bell-in-a-box. Stalled drives the pulse demo; the real authRequired
        // is a steady red since P22.
        stack.Children.Add(SectionLabel("Logo states"));
        stack.Children.Add(LegendRow(ActivityState.Working, "Working",
            "The logo rotates while a session is running."));
        stack.Children.Add(BellLegendRow("Your turn",
            "A thread finished — Agent Island opens an alarm window so you can reply."));
        stack.Children.Add(LegendRow(ActivityState.Stalled, "Needs attention",
            "Limits, login, network, or provider errors make the logo pulse red."));

        stack.Children.Add(SectionLabel("Reminders"));
        var enabled = new CobaltToggle(AgentReminderStore.Shared.Enabled);
        enabled.Toggled += value => AgentReminderStore.Shared.Enabled = value;
        var alarmWhenFront = new CobaltToggle(AgentReminderStore.Shared.AlarmWhenFrontmost);
        alarmWhenFront.Toggled += enabled => AgentReminderStore.Shared.AlarmWhenFrontmost = enabled;
        var frontChime = new CobaltToggle(AgentReminderStore.Shared.FrontmostSoundOnly);
        frontChime.Toggled += enabled => AgentReminderStore.Shared.FrontmostSoundOnly = enabled;
        stack.Children.Add(new SettingsRowControl(
            "Turn alarm",
            "Pop up a foreground alarm and system notification when a background run needs you.",
            enabled));

        stack.Children.Add(new SettingsRowControl(
            "Alarm even when in front",
            "Alarm on a finished turn even while that session's own app is frontmost",
            alarmWhenFront));

        stack.Children.Add(new SettingsRowControl(
            "Frontmost chime",
            "Chime instead of staying silent when the session's app is frontmost",
            frontChime));

        var details = new CobaltToggle(AgentReminderStore.Shared.ShowSessionDetails);
        details.Toggled += value => AgentReminderStore.Shared.ShowSessionDetails = value;
        stack.Children.Add(new SettingsRowControl(
            "Show thread details",
            "Show session and project names in alarms and notifications.",
            details));

        // The subagent-alarm toggle is gone: orchestrated subagents and child
        // threads are now skipped by the scanner outright, so there is no
        // setting left to expose.

        // The exhaustion-alarm opt-out: some people only want auto-resume and
        // treat the "out of quota" popup as noise. Subtitle nil, matching mac.
        var quotaAlarm = new CobaltToggle(Model.QuotaAlarmStore.Shared.Enabled);
        quotaAlarm.Toggled += value => Model.QuotaAlarmStore.Shared.Enabled = value;
        stack.Children.Add(new SettingsRowControl(
            "Out-of-quota alarm",
            null,
            quotaAlarm));

        var soundHost = new StackPanel();
        var sound = new CobaltToggle(AgentReminderStore.Shared.SoundEnabled);
        stack.Children.Add(new SettingsRowControl(
            "Alarm sound",
            null,
            sound));

        BuildSoundControls(soundHost);
        soundHost.Visibility = AgentReminderStore.Shared.SoundEnabled ? Visibility.Visible : Visibility.Collapsed;
        sound.Toggled += value =>
        {
            AgentReminderStore.Shared.SoundEnabled = value;
            soundHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        };
        stack.Children.Add(soundHost);

        // Demo buttons — force a state on the island. Visible in demo/debug
        // launches, exactly like macOS.
        if (AppEnvironment.IsDemo || AppEnvironment.IsDebug)
        {
            stack.Children.Add(SectionLabel("Demo — force a state on the island"));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 8) };
            row.Children.Add(DemoButton("Working", ActivityState.Working));
            row.Children.Add(DemoButton("Your turn", ActivityState.NeedsYou));
            row.Children.Add(DemoButton("Auth", ActivityState.AuthRequired));
            row.Children.Add(DemoButton("Rate", ActivityState.RateLimited));
            row.Children.Add(DemoButton("Live", null));
            stack.Children.Add(row);
        }

        return stack;
    }

    private static UIElement DemoButton(string label, ActivityState? state)
    {
        var button = new PillButtonControl(L10n.Tr(label)) { Margin = new Thickness(0, 0, 8, 0) };
        button.Clicked += () => ActivityMonitor.Shared.Demo(state);
        return button;
    }

    /// Expandable sound list with the selected checkmark and the custom-file
    /// row, plus the volume slider — the macOS SoundPicker.
    private void BuildSoundControls(StackPanel host)
    {
        var list = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 10, 8) };

        var headerLabel = new TextBlock
        {
            Text = CurrentSoundLabel(),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(0.58)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chevron = new TextBlock
        {
            Text = "⌄",
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(0.42)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var soundTitle = new TextBlock
        {
            Text = L10n.Tr("Sound"),
            FontFamily = IslandFonts.Ui,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.92)),
        };
        Grid.SetColumn(soundTitle, 0);
        headerRow.Children.Add(soundTitle);
        var headerRight = new StackPanel { Orientation = Orientation.Horizontal };
        headerRight.Children.Add(headerLabel);
        headerRight.Children.Add(chevron);
        Grid.SetColumn(headerRight, 1);
        headerRow.Children.Add(headerRight);
        var header = new Border
        {
            Child = headerRow,
            CornerRadius = new CornerRadius(7),
            Background = IslandColors.Brush(IslandColors.White(0.015)),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(10, 0, 10, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        header.MouseLeftButtonUp += (_, args) =>
        {
            list.Visibility = list.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            chevron.Text = list.Visibility == Visibility.Visible ? "⌃" : "⌄";
            args.Handled = true;
        };
        host.Children.Add(header);

        void RebuildList()
        {
            list.Children.Clear();
            foreach (var tone in AgentReminderStore.SystemTones.Available)
            {
                list.Children.Add(SoundChoiceRow(
                    AgentReminderStore.SystemTones.StoragePrefix + tone.Key,
                    isCustom: false, RebuildList, headerLabel));
            }
            foreach (var preset in AgentReminderStore.SoundPresets)
            {
                list.Children.Add(SoundChoiceRow(preset, isCustom: false, RebuildList, headerLabel));
            }
            list.Children.Add(SoundChoiceRow(
                AgentReminderStore.CustomSoundChoice, isCustom: true, RebuildList, headerLabel));
        }
        RebuildList();
        host.Children.Add(list);

        var volume = new Slider
        {
            Width = 120,
            Minimum = 0,
            Maximum = 1,
            Value = AgentReminderStore.Shared.Volume,
            VerticalAlignment = VerticalAlignment.Center,
        };
        volume.ValueChanged += (_, _) => AgentReminderStore.Shared.Volume = volume.Value;
        host.Children.Add(new SettingsRowControl(
            "Volume", null, volume));
    }

    /// The "your reply is up" legend: the symmetric mark, steady, with a
    /// small bell badge riding its bottom-trailing corner — the macOS
    /// StatePreviewLogo needsYou glyph (the mark stays put because the turn
    /// is DONE; the badge says why the island wants you).
    private UIElement BellLegendRow(string name, string caption)
    {
        var mark = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("F1 " + BrandGeometry.OpenAiPath),
            Fill = IslandColors.Brush(IslandColors.Codex),
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
        };
        var badge = new Border
        {
            Width = 11,
            Height = 11,
            CornerRadius = new CornerRadius(5.5),
            Background = IslandColors.Brush(Color.FromRgb(0x13, 0x16, 0x1C)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.18)),
            BorderThickness = new Thickness(0.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -3, -2),
            Child = new TextBlock
            {
                Text = "",   // Segoe Fluent Ringer bell
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 5.5,
                FontWeight = FontWeights.Bold,
                // White bell (macOS chrome) — the app's own chrome carries
                // no accent color.
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var bellHost = new Grid
        {
            Width = 24,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bellHost.Children.Add(mark);
        bellHost.Children.Add(badge);
        return LegendRowShell(bellHost, name, caption);
    }

    private static string CurrentSoundLabel()
    {
        var store = AgentReminderStore.Shared;
        if (store.SoundChoice == AgentReminderStore.CustomSoundChoice)
        {
            return store.CustomSoundPath.Length > 0
                ? System.IO.Path.GetFileName(store.CustomSoundPath)
                : L10n.Tr("Custom file");
        }
        return AgentReminderStore.PresetLabel(store.SoundChoice);
    }

    private UIElement SoundChoiceRow(string choice, bool isCustom, Action rebuild, TextBlock headerLabel)
    {
        var store = AgentReminderStore.Shared;
        var isSelected = store.SoundChoice == choice;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = isCustom ? L10n.Tr("Custom file") : AgentReminderStore.PresetLabel(choice),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            Foreground = IslandColors.Brush(IslandColors.White(isSelected ? 0.95 : 0.72)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);
        if (isCustom)
        {
            var action = new TextBlock
            {
                Text = store.CustomSoundPath.Length == 0 ? L10n.Tr("Choose") : L10n.Tr("Change"),
                FontFamily = IslandFonts.Ui,
                FontSize = 10,
                Foreground = IslandColors.Brush(IslandColors.White(0.55)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(action, 1);
            row.Children.Add(action);
        }
        if (isSelected)
        {
            var check = new TextBlock
            {
                Text = "✓",
                FontFamily = IslandFonts.Ui,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = IslandColors.Brush(IslandColors.Cobalt),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(check, 2);
            row.Children.Add(check);
        }

        var host = new Border
        {
            Child = row,
            Height = 34,
            Padding = new Thickness(12, 0, 12, 0),
            Background = isSelected ? IslandColors.Brush(IslandColors.White(0.055)) : Brushes.Transparent,
            BorderBrush = IslandColors.Brush(IslandColors.White(0.045)),
            BorderThickness = new Thickness(0, 0, 0, 0.5),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        host.MouseLeftButtonUp += (_, args) =>
        {
            if (isCustom)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Audio|*.wav;*.mp3;*.wma;*.m4a|All files|*.*",
                };
                if (dialog.ShowDialog(this) == true)
                {
                    store.CustomSoundPath = dialog.FileName;
                    store.SoundChoice = AgentReminderStore.CustomSoundChoice;
                }
            }
            else
            {
                store.SoundChoice = choice;
                PreviewSound();
            }
            headerLabel.Text = CurrentSoundLabel();
            rebuild();
            args.Handled = true;
        };
        return host;
    }

    // Held in a field: a local MediaPlayer can be collected mid-playback,
    // making the preview intermittently silent or clipped.
    private static MediaPlayer? _previewPlayer;

    private static void PreviewSound()
    {
        try
        {
            if (AgentReminderStore.Shared.ResolveSoundFile() is not { } file) return;
            _previewPlayer ??= new MediaPlayer();
            _previewPlayer.Stop();
            _previewPlayer.Volume = AgentReminderStore.Shared.Volume;
            _previewPlayer.Open(new Uri(file));
            _previewPlayer.Play();
        }
        catch
        {
        }
    }

    private UIElement LegendRow(ActivityState state, string name, string caption)
    {
        // The symmetric mark: rotationally uniform, so the spinning demo
        // doesn't wobble the way the starburst would (macOS StatePreviewLogo).
        var logo = new ProviderLogo { Tool = TriggerTool.Codex, Width = 30, Height = 26 };
        logo.SetState(state);
        return LegendRowShell(logo, name, caption);
    }

    /// One legend row skeleton — a FIXED 30x26 icon slot + 14 gap (macOS
    /// StatusGuideView), so all three text blocks share one left edge no
    /// matter how each state's icon renders.
    private static UIElement LegendRowShell(UIElement icon, string name, string caption)
    {
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var iconHost = new Grid
        {
            Width = 30,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconHost.Children.Add(icon);
        Grid.SetColumn(iconHost, 0);
        host.Children.Add(iconHost);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = L10n.Tr(name),
            FontFamily = IslandFonts.Ui,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.92)),
        });
        text.Children.Add(new TextBlock
        {
            Text = L10n.Tr(caption),
            FontFamily = IslandFonts.Ui,
            FontSize = 11.5,
            Foreground = IslandColors.Brush(IslandColors.White(0.62)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(text, 1);
        host.Children.Add(text);
        return new Border
        {
            Child = host,
            Padding = new Thickness(10, 9, 10, 9),
        };
    }
}
