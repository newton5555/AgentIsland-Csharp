using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentIsland.UI.Providers;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI.Report;

/// Daily share card (assets/Report/daily-report-card.html): one local day,
/// hour-resolved. Hero total with a day-over-day delta pill, a two-cell KPI
/// strip (cache / hosts·models — no sessions count, no turns: per-call
/// semantics no longer hold across providers), a 24-hour token pulse with
/// the peak hour accented, and the Agent→Model two-level tree with hairline
/// branching. No rank line — nothing invented; the footer keeps only the
/// LOCAL ONLY line.
public static partial class ReportCards
{
    public static FrameworkElement Daily(DailyReportData data, bool rounded = true)
    {
        var zh = ReportFormat.IsChinese;
        var body = BuildDailyLayout(
            Header("DAILY", data.DateText),
            DailyHero(data, zh),
            DailyKpiStrip(data, zh),
            DailyPulse(data, zh),
            DailyHierarchy(data, zh),
            DailyFooter(zh));
        return Card(body, rounded);
    }

    private static Grid BuildDailyLayout(
        UIElement header, UIElement hero, UIElement kpi, UIElement pulse, UIElement tree, UIElement footer)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // header
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 6, MaxHeight = 10 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // hero
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 6, MaxHeight = 10 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // kpi
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 6, MaxHeight = 10 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // pulse
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 6, MaxHeight = 10 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // tree
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // footer

        void Place(UIElement element, int row)
        {
            Grid.SetRow((FrameworkElement)element, row);
            grid.Children.Add(element);
        }
        Place(header, 0);
        Place(hero, 2);
        Place(kpi, 4);
        Place(pulse, 6);
        Place(tree, 8);
        Place(footer, 10);
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
            FontSize = 42,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(HeroText),
            LineHeight = 44,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        }));
        if (unit.Length > 0)
        {
            line.Children.Add(new TextBlock
            {
                Text = unit,
                FontFamily = IslandFonts.Ui,
                FontSize = zh ? 20 : 42,
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
                FontSize = 12,
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
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 6, 10, 6),
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
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.40)),
        });
        cell.Children.Add(Numeric(new TextBlock
        {
            Text = value,
            FontFamily = IslandFonts.Ui,
            FontSize = 12.5,
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
                FontSize = 8.5,
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
        var titleRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 3) };
        titleRow.Children.Add(new TextBlock
        {
            Text = AgentIsland.UI.Localization.L10n.Tr("24H PULSE"),
            FontFamily = IslandFonts.Ui,
            FontSize = 9.5,
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
                FontSize = 9,
                FontWeight = FontWeights.ExtraBold,
                Foreground = IslandColors.Brush(PeriodAccent),
                VerticalAlignment = VerticalAlignment.Center,
            });
            DockPanel.SetDock(peak, Dock.Right);
            titleRow.Children.Add(peak);
        }
        stack.Children.Add(titleRow);

        var grid = new Grid { Height = 34, VerticalAlignment = VerticalAlignment.Bottom };
        for (var i = 0; i < 24; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var max = data.HourlyTokens.Count > 0 ? data.HourlyTokens.Max() : 0;
        for (var h = 0; h < 24; h++)
        {
            var tokens = h < data.HourlyTokens.Count ? data.HourlyTokens[h] : 0;
            var isPeak = tokens > 0 && tokens == max;
            // Empty hours keep the 2.5px hairline stub so the grid reads
            // even when the day is quiet; the peak hour wears the accent.
            var height = tokens > 0 && max > 0 ? Math.Max(2.5, 34.0 * tokens / max) : 2.5;
            var bar = new Border
            {
                CornerRadius = new CornerRadius(1.5),
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
                    BlurRadius = 6,
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
                FontSize = 8,
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

    private static UIElement DailyHierarchy(DailyReportData data, bool zh)
    {
        var stack = new StackPanel();
        var headerRow = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 0, 2, 2) };
        headerRow.Children.Add(new TextBlock
        {
            Text = AgentIsland.UI.Localization.L10n.Tr("AGENTS & MODELS"),
            FontFamily = IslandFonts.Ui,
            FontSize = 10,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.50)),
        });
        var legend = new TextBlock
        {
            Text = "SHARE",
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

        // Row budget: allow each host to show its top models (up to 3 when 1-2 hosts,
        // up to 2 when 3+ hosts) so secondary agents (e.g. Codex) are not starved.
        var maxModelsPerHost = data.AgentTree.Count <= 2 ? 3 : 2;
        var totalBudget = 9;
        foreach (var agent in data.AgentTree)
        {
            if (totalBudget <= 0) break;
            var allowedModels = Math.Min(maxModelsPerHost, Math.Max(0, totalBudget - 1));
            stack.Children.Add(AgentBlock(agent, zh, allowedModels));
            totalBudget -= 1 + Math.Min(allowedModels, agent.Models.Count);
        }
        return stack;
    }

    private static UIElement AgentBlock(DailyAgentRow agent, bool zh, int maxModels)
    {
        var block = new StackPanel();
        var accent = ProviderIdentity.Accent(agent.Provider);

        var parentRow = new Grid { Margin = new Thickness(0, 2.5, 0, 2.5) };
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
            FontSize = 12,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
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
            FontSize = 11.5,
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
            FontSize = 10.5,
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
            FontSize = 11,
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
        var shownCount = Math.Min(agent.Models.Count, maxModels);
        for (var index = 0; index < shownCount; index++)
        {
            children.Children.Add(ModelRow(agent.Models[index], shownCount, index, zh));
        }
        if (children.Children.Count > 0)
        {
            block.Children.Add(children);
        }
        return block;
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
        var stack = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        stack.Children.Add(new Border
        {
            Height = 1,
            Background = IslandColors.Brush(IslandColors.White(0.05)),
        });
        var line = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 0) };
        var lifetime = new TextBlock
        {
            Text = AgentIsland.UI.Localization.L10n.Tr("AGENT ISLAND · LOCAL ONLY"),
            FontFamily = IslandFonts.Ui,
            FontSize = 9.5,
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
