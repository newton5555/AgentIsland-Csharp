using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentIsland.UI.Providers;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI.Report;

/// Daily share card (assets/Report/daily-report-card.html): one local day,
/// hour-resolved. Hero total with a day-over-day delta pill, a two-cell KPI
/// strip (cache / hosts·models), a 24-hour token pulse with the peak hour accented,
/// a segmented capsule tab bar ([ 🌐 总览 ] [ ⚡ DeepSeek ] [ 🔷 Antigravity ] [ 🤖 Codex ]),
/// and multi-view content (Overview tree with stacked proportion bar, or dedicated Agent
/// detail card with all models un-truncated, rankings, intra-agent share bars).
public static partial class ReportCards
{
    public static FrameworkElement Daily(
        DailyReportData data,
        bool rounded = true,
        string? selectedTab = null,
        Action<string>? onTabChanged = null)
    {
        var zh = ReportFormat.IsChinese;
        var activeTab = string.IsNullOrWhiteSpace(selectedTab) ? "overview" : selectedTab;

        var tabBarSlot = new Border();
        var contentSlot = new Border();

        void SwitchTab(string targetTab)
        {
            activeTab = targetTab;
            tabBarSlot.Child = BuildTabBar(data, zh, activeTab, SwitchTab);

            if (activeTab.Equals("overview", StringComparison.OrdinalIgnoreCase))
            {
                contentSlot.Child = BuildOverviewContent(data, zh, SwitchTab);
            }
            else
            {
                var targetAgent = data.AgentTree.FirstOrDefault(
                    a => a.Provider.ToString().Equals(activeTab, StringComparison.OrdinalIgnoreCase));
                if (targetAgent != null)
                {
                    contentSlot.Child = BuildAgentDetailContent(targetAgent, data, zh, SwitchTab);
                }
                else
                {
                    activeTab = "overview";
                    contentSlot.Child = BuildOverviewContent(data, zh, SwitchTab);
                }
            }

            onTabChanged?.Invoke(activeTab);
        }

        // Initialize slots
        SwitchTab(activeTab);

        var body = BuildDailyLayout(
            Header("DAILY", data.DateText),
            DailyHero(data, zh),
            DailyKpiStrip(data, zh),
            DailyPulse(data, zh),
            tabBarSlot,
            contentSlot,
            DailyFooter(zh));

        return Card(body, rounded);
    }

    private static Grid BuildDailyLayout(
        UIElement header,
        UIElement hero,
        UIElement kpi,
        UIElement pulse,
        UIElement tabBar,
        UIElement content,
        UIElement footer)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // 0: header
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 4, MaxHeight = 8 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // 2: hero
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 4, MaxHeight = 8 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // 4: kpi
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 4, MaxHeight = 8 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // 6: pulse
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 4, MaxHeight = 8 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // 8: tab bar
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // 9: content
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // 11: footer

        void Place(UIElement element, int row)
        {
            Grid.SetRow((FrameworkElement)element, row);
            grid.Children.Add(element);
        }
        Place(header, 0);
        Place(hero, 2);
        Place(kpi, 4);
        Place(pulse, 6);
        Place(tabBar, 8);
        Place(content, 9);
        Place(footer, 11);
        return grid;
    }

    private static UIElement DailyHero(DailyReportData data, bool zh)
    {
        var stack = new StackPanel();
        var titleRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 2) };
        titleRow.Children.Add(new TextBlock
        {
            Text = AgentIsland.UI.Localization.L10n.Tr("tokens today"),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.50)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (data.HasDelta)
        {
            var deltaBrush = data.DeltaUp ? IslandColors.Brush(LiveTeal) : IslandColors.Brush(IslandColors.White(0.55));
            var delta = new TextBlock
            {
                Text = data.DeltaText,
                FontFamily = IslandFonts.Ui,
                FontSize = 10.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = deltaBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(delta, Dock.Right);
            titleRow.Children.Add(delta);
        }
        stack.Children.Add(titleRow);

        var (value, unit) = ReportFormat.CompactParts(data.TotalTokens, zh);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(Numeric(new TextBlock
        {
            Text = value,
            FontFamily = IslandFonts.Ui,
            FontSize = 40,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(HeroText),
            LineHeight = 42,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        }));
        if (unit.Length > 0)
        {
            line.Children.Add(new TextBlock
            {
                Text = unit,
                FontFamily = IslandFonts.Ui,
                FontSize = zh ? 20 : 40,
                FontWeight = FontWeights.ExtraBold,
                Foreground = IslandColors.Brush(HeroText),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(3, 0, 0, zh ? 5 : 0),
            });
        }
        if (data.HasActualDollars)
        {
            var money = ReportFormat.Money(data.TotalDollars);
            var dollarText = zh
                ? (data.IsPartialDollars ? "≈ ${money} 的 API 费用 (部分估算)" : "≈ ${money} 的 API 费用")
                : (data.IsPartialDollars ? "≈ ${money} API value (partial estimate)" : "≈ ${money} API value");
            dollarText = string.Format(dollarText.Replace("{money}", "{0}"), money);
            line.Children.Add(new TextBlock
            {
                Text = dollarText,
                FontFamily = IslandFonts.Ui,
                FontSize = 11.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = IslandColors.Brush(PriceGreen),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(10, 0, 0, 3),
            });
        }
        stack.Children.Add(new Viewbox
        {
            Child = line,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxWidth = CardWidth - 56,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        return stack;
    }

    private static UIElement DailyKpiStrip(DailyReportData data, bool zh)
    {
        var plate = new Border
        {
            Background = IslandColors.Brush(IslandColors.White(0.025)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.05)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 1, 0, 2),
        };
        var cells = new Grid();
        plate.Child = cells;
        cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cells.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cacheCell = KpiCell(
            AgentIsland.UI.Localization.L10n.Tr("PROMPT CACHE"),
            data.HasCacheData ? (data.CacheRate * 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%" : "—",
            data.HasCacheData && data.CacheSavingsDollars >= 0.5
                ? AgentIsland.UI.Localization.L10n.TrFormat("saved ${0}", ReportFormat.Money(data.CacheSavingsDollars))
                : "");
        Grid.SetColumn(cacheCell, 0);
        cells.Children.Add(cacheCell);

        var divider = new Border
        {
            Width = 1,
            Background = IslandColors.Brush(IslandColors.White(0.06)),
            Margin = new Thickness(6, 0, 6, 0),
        };
        Grid.SetColumn(divider, 1);
        cells.Children.Add(divider);

        var agentsCell = KpiCell(
            AgentIsland.UI.Localization.L10n.Tr("HOSTS / MODELS"),
            AgentIsland.UI.Localization.L10n.TrFormat("{0} active", data.ActiveAgentsCount),
            data.TotalModelsCount > 0
                ? AgentIsland.UI.Localization.L10n.TrFormat("{0} models", data.TotalModelsCount)
                : "");
        Grid.SetColumn(agentsCell, 2);
        cells.Children.Add(agentsCell);
        return plate;
    }

    private static StackPanel KpiCell(string label, string value, string sub)
    {
        var cell = new StackPanel();
        cell.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = IslandFonts.Ui,
            FontSize = 8.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.40)),
        });
        cell.Children.Add(Numeric(new TextBlock
        {
            Text = value,
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(Colors.White),
            Margin = new Thickness(0, 1, 0, 0),
        }));
        if (sub.Length > 0)
        {
            cell.Children.Add(new TextBlock
            {
                Text = sub,
                FontFamily = IslandFonts.Ui,
                FontSize = 8,
                FontWeight = FontWeights.SemiBold,
                Foreground = IslandColors.Brush(PeriodAccent),
                Margin = new Thickness(0, 0.5, 0, 0),
            });
        }
        return cell;
    }

    private static UIElement DailyPulse(DailyReportData data, bool zh)
    {
        var stack = new StackPanel();
        var titleRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 2) };
        titleRow.Children.Add(new TextBlock
        {
            Text = AgentIsland.UI.Localization.L10n.Tr("24H PULSE"),
            FontFamily = IslandFonts.Ui,
            FontSize = 9,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.40)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (data.PeakTokens > 0)
        {
            var peak = Numeric(new TextBlock
            {
                Text = AgentIsland.UI.Localization.L10n.TrFormat(
                    "🔥 {0:00}:00 · {1}", data.PeakHour, ReportFormat.CompactString(data.PeakTokens, zh)),
                FontFamily = IslandFonts.Ui,
                FontSize = 8.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = IslandColors.Brush(PeriodAccent),
                VerticalAlignment = VerticalAlignment.Center,
            });
            DockPanel.SetDock(peak, Dock.Right);
            titleRow.Children.Add(peak);
        }
        stack.Children.Add(titleRow);

        var grid = new Grid { Height = 28, VerticalAlignment = VerticalAlignment.Bottom };
        for (var i = 0; i < 24; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var max = data.HourlyTokens.Count > 0 ? data.HourlyTokens.Max() : 0;
        for (var h = 0; h < 24; h++)
        {
            var tokens = h < data.HourlyTokens.Count ? data.HourlyTokens[h] : 0;
            var isPeak = tokens > 0 && tokens == max;
            var height = tokens > 0 && max > 0 ? Math.Max(2.0, 28.0 * tokens / max) : 2.0;
            var bar = new Border
            {
                CornerRadius = new CornerRadius(1),
                Background = isPeak
                    ? IslandColors.Brush(PeriodAccent)
                    : IslandColors.Brush(IslandColors.White(tokens > 0 ? 0.20 : 0.07)),
                Height = height,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0.5, 0, 0.5, 0),
            };
            if (isPeak)
            {
                bar.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    ShadowDepth = 0,
                    BlurRadius = 5,
                    Color = PeriodAccent,
                    Opacity = 0.5,
                };
            }
            Grid.SetColumn(bar, h);
            grid.Children.Add(bar);
        }
        stack.Children.Add(grid);

        string[] marks = { "00:00", "06:00", "12:00", "18:00", "24:00" };
        var labelGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        for (var i = 0; i < marks.Length; i++)
        {
            labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var i = 0; i < marks.Length; i++)
        {
            var label = new TextBlock
            {
                Text = marks[i],
                FontFamily = IslandFonts.Ui,
                FontSize = 7.5,
                FontWeight = FontWeights.Bold,
                Foreground = IslandColors.Brush(IslandColors.White(0.35)),
                TextAlignment = i == 0 ? TextAlignment.Left
                    : i == marks.Length - 1 ? TextAlignment.Right : TextAlignment.Center,
            };
            Grid.SetColumn(label, i);
            labelGrid.Children.Add(label);
        }
        stack.Children.Add(labelGrid);
        return stack;
    }

    private static UIElement BuildTabBar(
        DailyReportData data,
        bool zh,
        string activeTab,
        Action<string> switchTab)
    {
        var bar = new Border
        {
            Background = IslandColors.Brush(IslandColors.White(0.035)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.07)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 2, 0, 2),
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Child = stack;

        var isOverview = activeTab.Equals("overview", StringComparison.OrdinalIgnoreCase);

        var overviewBtn = CreateTabPill(
            CreateOverviewIcon(),
            zh ? "总览" : "Overview",
            null,
            isOverview,
            isOverview ? Color.FromArgb(46, PeriodAccent.R, PeriodAccent.G, PeriodAccent.B) : Colors.Transparent,
            isOverview ? Color.FromArgb(115, PeriodAccent.R, PeriodAccent.G, PeriodAccent.B) : Colors.Transparent,
            () => switchTab("overview"));
        stack.Children.Add(overviewBtn);

        foreach (var agent in data.AgentTree)
        {
            var agentId = agent.Provider.ToString();
            var isActive = activeTab.Equals(agentId, StringComparison.OrdinalIgnoreCase);
            var accent = ProviderIdentity.Accent(agent.Provider);

            var bg = isActive ? Color.FromArgb(46, accent.R, accent.G, accent.B) : Colors.Transparent;
            var border = isActive ? Color.FromArgb(115, accent.R, accent.G, accent.B) : Colors.Transparent;

            var agentBtn = CreateTabPill(
                ProviderMark(agent.Provider, 12),
                agent.Provider.ToString(),
                agent.Models.Count.ToString(),
                isActive,
                bg,
                border,
                () => switchTab(agentId));
            stack.Children.Add(agentBtn);
        }

        return bar;
    }

    private static UIElement CreateTabPill(
        object icon,
        string title,
        string? badge,
        bool isActive,
        Color bg,
        Color border,
        Action onClick)
    {
        var pill = new Border
        {
            Height = 24,
            CornerRadius = new CornerRadius(11),
            Background = IslandColors.Brush(bg),
            BorderBrush = IslandColors.Brush(border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(1.5, 0, 1.5, 0),
            Cursor = Cursors.Hand,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (icon is string iconStr)
        {
            panel.Children.Add(new TextBlock
            {
                Text = iconStr,
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
        }
        else if (icon is UIElement uiIcon)
        {
            panel.Children.Add(new Border
            {
                Child = uiIcon,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = IslandFonts.Ui,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(isActive ? Colors.White : IslandColors.White(0.80)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (!string.IsNullOrEmpty(badge))
        {
            var badgeBorder = new Border
            {
                Background = IslandColors.Brush(IslandColors.White(isActive ? 0.22 : 0.12)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(4, 0.5, 4, 0.5),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Numeric(new TextBlock
                {
                    Text = badge,
                    FontFamily = IslandFonts.Ui,
                    FontSize = 8,
                    FontWeight = FontWeights.ExtraBold,
                    Foreground = IslandColors.Brush(isActive ? Colors.White : IslandColors.White(0.70)),
                    VerticalAlignment = VerticalAlignment.Center,
                }),
            };
            panel.Children.Add(badgeBorder);
        }

        pill.Child = panel;

        pill.MouseEnter += (_, _) =>
        {
            if (!isActive)
            {
                pill.Background = IslandColors.Brush(IslandColors.White(0.08));
            }
        };
        pill.MouseLeave += (_, _) =>
        {
            if (!isActive)
            {
                pill.Background = IslandColors.Brush(bg);
            }
        };
        pill.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return pill;
    }

    private static UIElement CreateOverviewIcon()
    {
        var accent = PeriodAccent;
        var canvas = new Grid
        {
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Subtle glowing background disc + outer ring
        canvas.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = IslandColors.Brush(Color.FromArgb(40, accent.R, accent.G, accent.B)),
            Stroke = IslandColors.Brush(accent),
            StrokeThickness = 1.25,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Vertical meridian ellipse
        canvas.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 5.2,
            Height = 11,
            Stroke = IslandColors.Brush(Color.FromArgb(220, accent.R, accent.G, accent.B)),
            StrokeThickness = 1.05,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Horizontal equator line
        canvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 0.5,
            Y1 = 5.5,
            X2 = 10.5,
            Y2 = 5.5,
            Stroke = IslandColors.Brush(Color.FromArgb(220, accent.R, accent.G, accent.B)),
            StrokeThickness = 1.05,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return canvas;
    }

    private static UIElement BuildOverviewContent(
        DailyReportData data,
        bool zh,
        Action<string> switchTab)
    {
        var stack = new StackPanel();

        // 1. Header Row
        var headerRow = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 0, 2, 2) };
        headerRow.Children.Add(new TextBlock
        {
            Text = zh ? "AGENTS 全局分布与模型树" : "AGENTS & MODELS",
            FontFamily = IslandFonts.Ui,
            FontSize = 9.5,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.50)),
        });
        var legend = new TextBlock
        {
            Text = zh ? "点击 Agent 可展开专属 Tab" : "CLICK AGENT FOR DETAILS",
            FontFamily = IslandFonts.Ui,
            FontSize = 8.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.40)),
        };
        DockPanel.SetDock(legend, Dock.Right);
        headerRow.Children.Add(legend);
        stack.Children.Add(headerRow);

        if (data.AgentTree.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = AgentIsland.UI.Localization.L10n.Tr("No activity recorded"),
                FontFamily = IslandFonts.Ui,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = IslandColors.Brush(IslandColors.White(0.35)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 18, 0, 18),
            });
            return stack;
        }

        // 2. Global Stacked Multi-Segment Bar (宏观彩色分段条)
        var stackedBar = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = IslandColors.Brush(IslandColors.White(0.05)),
            Margin = new Thickness(0, 1, 0, 3),
        };
        var barGrid = new Grid();
        stackedBar.Child = barGrid;
        for (var i = 0; i < data.AgentTree.Count; i++)
        {
            barGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.02, data.AgentTree[i].SharePercent), GridUnitType.Star),
            });
            var seg = new Border
            {
                Background = IslandColors.Brush(ProviderIdentity.Accent(data.AgentTree[i].Provider)),
                CornerRadius = i == 0 ? new CornerRadius(2.5, 0, 0, 2.5) : (i == data.AgentTree.Count - 1 ? new CornerRadius(0, 2.5, 2.5, 0) : new CornerRadius(0)),
            };
            Grid.SetColumn(seg, i);
            barGrid.Children.Add(seg);
        }
        stack.Children.Add(stackedBar);

        // 3. Tree Blocks with Click-to-Jump
        var totalBudget = 10;
        for (var i = 0; i < data.AgentTree.Count; i++)
        {
            if (totalBudget <= 0) break;
            var agent = data.AgentTree[i];
            var remainingHosts = data.AgentTree.Count - 1 - i;
            var reservedForOthers = remainingHosts;
            var budgetForThis = Math.Max(0, totalBudget - 1 - reservedForOthers);
            var maxModels = Math.Min(5, budgetForThis);

            stack.Children.Add(AgentBlock(agent, zh, maxModels, () => switchTab(agent.Provider.ToString())));
            var renderedCount = agent.Models.Count <= maxModels
                ? agent.Models.Count
                : (maxModels > 1 ? maxModels : Math.Min(1, agent.Models.Count));
            totalBudget -= 1 + renderedCount;
        }

        return stack;
    }

    private static UIElement BuildAgentDetailContent(
        DailyAgentRow agent,
        DailyReportData data,
        bool zh,
        Action<string> switchTab)
    {
        var accent = ProviderIdentity.Accent(agent.Provider);
        var stack = new StackPanel();

        // 1. Agent Sub-Header Banner
        var banner = new Border
        {
            Background = IslandColors.Brush(IslandColors.White(0.03)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.06)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 1, 0, 3),
        };
        var bannerGrid = new DockPanel { LastChildFill = false };
        banner.Child = bannerGrid;

        var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var mark = (FrameworkElement)ProviderMark(agent.Provider, 16);
        mark.VerticalAlignment = VerticalAlignment.Center;
        leftStack.Children.Add(mark);

        leftStack.Children.Add(new TextBlock
        {
            Text = agent.Provider + (zh ? " 消耗全貌" : " Overview"),
            FontFamily = IslandFonts.Ui,
            FontSize = 11.5,
            FontWeight = FontWeights.ExtraBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        });

        leftStack.Children.Add(new Border
        {
            Background = IslandColors.Brush(IslandColors.White(0.10)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Numeric(new TextBlock
            {
                Text = zh ? $"{agent.Models.Count} 款模型" : $"{agent.Models.Count} models",
                FontFamily = IslandFonts.Ui,
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = IslandColors.Brush(IslandColors.White(0.80)),
            }),
        });
        DockPanel.SetDock(leftStack, Dock.Left);
        bannerGrid.Children.Add(leftStack);

        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rightStack.Children.Add(Numeric(new TextBlock
        {
            Text = ReportFormat.CompactString(agent.Tokens, zh),
            FontFamily = IslandFonts.Ui,
            FontSize = 12.5,
            FontWeight = FontWeights.ExtraBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        }));

        var shareTag = new Border
        {
            Background = IslandColors.Brush(Color.FromArgb(32, PeriodAccent.R, PeriodAccent.G, PeriodAccent.B)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Numeric(new TextBlock
            {
                Text = (zh ? "全天 " : "Day ") + Core.Formatting.PercentInt(agent.SharePercent) + "%",
                FontFamily = IslandFonts.Ui,
                FontSize = 9,
                FontWeight = FontWeights.ExtraBold,
                Foreground = IslandColors.Brush(PeriodAccent),
            }),
        };
        rightStack.Children.Add(shareTag);
        DockPanel.SetDock(rightStack, Dock.Right);
        bannerGrid.Children.Add(rightStack);
        stack.Children.Add(banner);

        // 2. Models List (Complete, un-truncated)
        var listStack = new StackPanel { Margin = new Thickness(0, 1, 0, 2) };
        for (var idx = 0; idx < agent.Models.Count; idx++)
        {
            listStack.Children.Add(DetailModelCard(agent.Models[idx], idx, agent.Tokens, accent, zh));
        }
        stack.Children.Add(listStack);

        return stack;
    }

    private static UIElement DetailModelCard(
        DailyModelRow model,
        int index,
        long agentTokens,
        Color accent,
        bool zh)
    {
        var card = new Border
        {
            Background = IslandColors.Brush(IslandColors.White(0.02)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.045)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 1, 0, 1),
        };

        var grid = new Grid { Height = 22 };
        card.Child = grid;

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Color rankBg;
        Color rankFg;
        if (index == 0)
        {
            rankBg = Color.FromArgb(46, 245, 158, 11);
            rankFg = Color.FromRgb(245, 158, 11);
        }
        else if (index == 1)
        {
            rankBg = Color.FromArgb(38, 200, 205, 215);
            rankFg = Color.FromRgb(221, 226, 236);
        }
        else if (index == 2)
        {
            rankBg = Color.FromArgb(38, 205, 127, 50);
            rankFg = Color.FromRgb(215, 154, 109);
        }
        else
        {
            rankBg = Color.FromArgb(20, 255, 255, 255);
            rankFg = IslandColors.White(0.40);
        }

        var rankBorder = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            Background = IslandColors.Brush(rankBg),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Child = Numeric(new TextBlock
            {
                Text = $"#{index + 1}",
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 8.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = IslandColors.Brush(rankFg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }),
        };
        Grid.SetColumn(rankBorder, 0);
        grid.Children.Add(rankBorder);

        var midStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameBlock = new TextBlock
        {
            Text = model.Name,
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        midStack.Children.Add(nameBlock);

        var intraPct = agentTokens > 0 ? (double)model.Tokens / agentTokens : 0.0;
        var barTrack = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = IslandColors.Brush(IslandColors.White(0.06)),
            Margin = new Thickness(0, 1.5, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = IslandColors.Brush(accent),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, 160 * Math.Min(1.0, intraPct)),
            },
        };
        midStack.Children.Add(barTrack);
        Grid.SetColumn(midStack, 1);
        grid.Children.Add(midStack);

        var tokensBlock = Numeric(new TextBlock
        {
            Text = ReportFormat.CompactString(model.Tokens, zh),
            FontFamily = IslandFonts.Ui,
            FontSize = 10,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.90)),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MinWidth = 46,
        });
        Grid.SetColumn(tokensBlock, 2);
        grid.Children.Add(tokensBlock);

        var pctBlock = Numeric(new TextBlock
        {
            Text = Core.Formatting.PercentInt(intraPct) + "%",
            FontFamily = IslandFonts.Ui,
            FontSize = 9.5,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(PeriodAccent),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MinWidth = 30,
            Margin = new Thickness(6, 0, 0, 0),
        });
        Grid.SetColumn(pctBlock, 3);
        grid.Children.Add(pctBlock);

        return card;
    }

    private static UIElement AgentBlock(
        DailyAgentRow agent,
        bool zh,
        int maxModels,
        Action? onAgentClicked = null)
    {
        var block = new StackPanel();
        var accent = ProviderIdentity.Accent(agent.Provider);

        var parentRow = new Grid
        {
            Margin = new Thickness(0, 2, 0, 2),
            Cursor = Cursors.Hand,
        };
        if (onAgentClicked != null)
        {
            parentRow.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                onAgentClicked();
            };
        }

        parentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        parentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        parentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        parentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        parentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        parentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var mark = (FrameworkElement)ProviderMark(agent.Provider, 15);
        mark.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(mark, 0);
        parentRow.Children.Add(mark);

        var name = new TextBlock
        {
            Text = agent.Provider.ToString(),
            FontFamily = IslandFonts.Ui,
            FontSize = 11.5,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        Grid.SetColumn(name, 1);
        parentRow.Children.Add(name);

        var pillTrack = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(3),
            Background = IslandColors.Brush(IslandColors.White(0.07)),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(3),
                Background = IslandColors.Brush(accent),
                Width = Math.Max(2, 52 * Math.Min(1.0, agent.SharePercent)),
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };
        Grid.SetColumn(pillTrack, 2);
        parentRow.Children.Add(pillTrack);

        var tokens = Numeric(new TextBlock
        {
            Text = ReportFormat.CompactString(agent.Tokens, zh),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.90)),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(tokens, 4);
        parentRow.Children.Add(tokens);

        var priced = ReportFormat.ProvidesDollars(agent.Provider);
        var dollars = Numeric(new TextBlock
        {
            Text = priced ? "$" + ReportFormat.Money(agent.Dollars) : "—",
            FontFamily = IslandFonts.Ui,
            FontSize = 10,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(priced ? Color.FromRgb(0x8C, 0xD9, 0x9E) : IslandColors.White(0.25)),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });
        Grid.SetColumn(dollars, 5);
        parentRow.Children.Add(dollars);

        var share = Numeric(new TextBlock
        {
            Text = Core.Formatting.PercentInt(agent.SharePercent) + "%",
            FontFamily = IslandFonts.Ui,
            FontSize = 10.5,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.90)),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });
        Grid.SetColumn(share, 6);
        parentRow.Children.Add(share);

        block.Children.Add(parentRow);

        var children = new StackPanel { Margin = new Thickness(8, 1, 0, 0) };
        if (agent.Models.Count <= maxModels)
        {
            for (var index = 0; index < agent.Models.Count; index++)
            {
                children.Children.Add(ModelRow(agent.Models[index], agent.Models.Count, index, zh));
            }
        }
        else if (maxModels > 1)
        {
            var explicitCount = maxModels - 1;
            for (var index = 0; index < explicitCount; index++)
            {
                children.Children.Add(ModelRow(agent.Models[index], explicitCount + 1, index, zh));
            }
            var omittedModels = agent.Models.Skip(explicitCount).ToList();
            var omittedTokens = omittedModels.Sum(m => m.Tokens);
            var omittedShare = omittedModels.Sum(m => m.SharePercent);
            children.Children.Add(OmittedModelRow(omittedModels.Count, omittedTokens, omittedShare, zh, onAgentClicked));
        }
        else if (maxModels == 1)
        {
            children.Children.Add(ModelRow(agent.Models[0], 1, 0, zh));
        }

        if (children.Children.Count > 0)
        {
            block.Children.Add(children);
        }
        return block;
    }

    private static UIElement OmittedModelRow(
        int omittedCount,
        long omittedTokens,
        double omittedShare,
        bool zh,
        Action? onClick = null)
    {
        var row = new Grid
        {
            Height = 17,
            Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow,
        };
        if (onClick != null)
        {
            row.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
        }

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var branch = new TextBlock
        {
            Text = "└─",
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 10,
            Foreground = IslandColors.Brush(IslandColors.White(0.22)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(branch, 0);
        row.Children.Add(branch);

        var name = new TextBlock
        {
            Text = zh ? $"+{omittedCount} 个其他模型 (进入专属Tab ➔)" : $"+{omittedCount} other models ➔",
            FontFamily = IslandFonts.Ui,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(PeriodAccent),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var tokens = Numeric(new TextBlock
        {
            Text = ReportFormat.CompactString(omittedTokens, zh),
            FontFamily = IslandFonts.Ui,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.40)),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 44,
        });
        Grid.SetColumn(tokens, 2);
        row.Children.Add(tokens);

        var share = Numeric(new TextBlock
        {
            Text = Core.Formatting.PercentInt(omittedShare) + "%",
            FontFamily = IslandFonts.Ui,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.35)),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 30,
            Margin = new Thickness(8, 0, 0, 0),
        });
        Grid.SetColumn(share, 3);
        row.Children.Add(share);
        return row;
    }

    private static UIElement ModelRow(DailyModelRow model, int siblingCount, int index, bool zh)
    {
        var row = new Grid { Height = 17 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var branch = new TextBlock
        {
            Text = index == siblingCount - 1 ? "└─" : "├─",
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 10,
            Foreground = IslandColors.Brush(IslandColors.White(0.22)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(branch, 0);
        row.Children.Add(branch);

        var name = new TextBlock
        {
            Text = model.Name,
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.65)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var tokens = Numeric(new TextBlock
        {
            Text = ReportFormat.CompactString(model.Tokens, zh),
            FontFamily = IslandFonts.Ui,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.50)),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 44,
        });
        Grid.SetColumn(tokens, 2);
        row.Children.Add(tokens);

        var share = Numeric(new TextBlock
        {
            Text = Core.Formatting.PercentInt(model.SharePercent) + "%",
            FontFamily = IslandFonts.Ui,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.40)),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 30,
            Margin = new Thickness(8, 0, 0, 0),
        });
        Grid.SetColumn(share, 3);
        row.Children.Add(share);
        return row;
    }

    private static UIElement DailyFooter(bool zh)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(new Border
        {
            Height = 1,
            Background = IslandColors.Brush(IslandColors.White(0.05)),
        });
        var line = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 4, 0, 0) };
        var lifetime = new TextBlock
        {
            Text = AgentIsland.UI.Localization.L10n.Tr("AGENT ISLAND · LOCAL ONLY"),
            FontFamily = IslandFonts.Ui,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.35)),
        };
        DockPanel.SetDock(lifetime, Dock.Right);
        line.Children.Add(lifetime);
        stack.Children.Add(line);
        return stack;
    }

    private static readonly Color HeroText = Color.FromRgb(0xF2, 0xF5, 0xF7);
}
