using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentIsland.Cost;
using AgentIsland.Model;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// Year overview, macOS layout: a hero row (YYYY TOKEN total, active-day
/// count, provider share legend for every provider that ran), month labels,
/// and the full-year contribution grid. Each day takes its dominant
/// provider's hue (intensity = token volume); a day split roughly between its
/// top-2 providers renders a diagonal two-color cell. Clicking a day shows
/// its per-provider detail line.
public sealed class OverviewPage : Border
{
    private const int Rows = 7;

    private readonly TextBlock _heroLabel;
    private readonly TextBlock _heroValue;
    private readonly TextBlock _activeDays;
    private readonly TextBlock _legend;
    private readonly Canvas _monthLabels = new();
    private readonly Canvas _gridCanvas = new();
    private readonly TextBlock _detail;
    private Dictionary<DateTime, Dictionary<DisplayProvider, DayProviderTotals>> _days = new();
    private readonly DispatcherTimer _renderDebounce = new() { Interval = TimeSpan.FromMilliseconds(80) };

    public OverviewPage()
    {
        Padding = new Thickness(22, 10, 22, 4);
        var grid = new Grid();
        Child = grid;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // hero
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); // month labels
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // detail

        // Hero: "2026 TOKEN" over the total, with active days + legend beside.
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
        var heroLeft = new StackPanel();
        _heroLabel = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.4)),
        };
        heroLeft.Children.Add(_heroLabel);
        _heroValue = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 1, 0, 0),
        };
        heroLeft.Children.Add(_heroValue);
        hero.Children.Add(heroLeft);

        _activeDays = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18, 0, 0, 5),
        };
        hero.Children.Add(_activeDays);

        _legend = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16, 0, 0, 5),
        };
        hero.Children.Add(_legend);
        Grid.SetRow(hero, 0);
        grid.Children.Add(hero);

        Grid.SetRow(_monthLabels, 1);
        grid.Children.Add(_monthLabels);

        Grid.SetRow(_gridCanvas, 2);
        grid.Children.Add(_gridCanvas);

        _detail = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            Margin = new Thickness(2, 6, 0, 0),
            Text = Localization.L10n.Tr("Click a day for details"),
        };
        Grid.SetRow(_detail, 3);
        grid.Children.Add(_detail);

        // RenderGrid re-creates ~365 day cells; SizeChanged fires on every
        // frame of the island's expand/collapse morph, so rebuilding the grid
        // per frame drops the signature animation. Recompute the data only
        // when it actually changes, and coalesce the grid repaint to ~80ms
        // after the size settles (or after a data change).
        _renderDebounce.Tick += (_, _) =>
        {
            _renderDebounce.Stop();
            RenderGrid();
        };
        // Detach on Unloaded (PagedContent recreates this page): a leaked
        // instance would keep the debounce timer alive and repaint forever.
        System.ComponentModel.PropertyChangedEventHandler onData =
            (_, _) => Dispatcher.BeginInvoke(() =>
            {
                RebuildData();
                ScheduleRender();
            });
        CostStore.Shared.PropertyChanged += onData;
        TokenCountModeStore.Shared.PropertyChanged += onData;
        SizeChanged += (_, _) => ScheduleRender();
        Unloaded += (_, _) =>
        {
            _renderDebounce.Stop();
            CostStore.Shared.PropertyChanged -= onData;
            TokenCountModeStore.Shared.PropertyChanged -= onData;
        };
        RebuildData();
    }

    private void ScheduleRender()
    {
        _renderDebounce.Stop();
        _renderDebounce.Start();
    }

    private void RebuildData()
    {
        _days = MergeHistory(CostStore.Shared);
        UpdateHero();
    }

    private void UpdateHero()
    {
        var billable = TokenCountModeStore.Shared.Mode == TokenCountMode.Billable;
        var perProvider = new Dictionary<DisplayProvider, long>();
        long total = 0;
        var activeDays = 0;
        foreach (var day in _days.Values)
        {
            long dayTotal = 0;
            foreach (var (provider, totals) in day)
            {
                var value = billable ? totals.Billable : totals.Tokens;
                if (value <= 0) continue;
                perProvider[provider] = perProvider.GetValueOrDefault(provider) + value;
                dayTotal += value;
            }
            total += dayTotal;
            if (dayTotal > 0) activeDays++;
        }
        _heroLabel.Text = $"{DateTime.Now.Year} TOKEN";
        _heroValue.Text = Core.Formatting.CompactTokens(total);
        _activeDays.Text = Localization.L10n.TrFormat("{0} active days", activeDays);

        // One chip per provider that ran, in canonical slot order — a
        // Grok-only year reads as Grok, not a blank Claude/Codex split.
        _legend.Inlines.Clear();
        var first = true;
        foreach (var provider in DisplayProviders.All)
        {
            var value = perProvider.GetValueOrDefault(provider);
            if (value <= 0) continue;
            if (!first) _legend.Inlines.Add(new System.Windows.Documents.Run("   "));
            AppendLegend(
                IslandColors.For(provider),
                ProviderIdentity.DisplayName(provider),
                total > 0 ? Core.Formatting.PercentInt(value / (double)total) : 0);
            first = false;
        }
    }

    private void AppendLegend(Color tint, string name, int percent)
    {
        _legend.Inlines.Add(new System.Windows.Documents.Run("● ")
        {
            Foreground = IslandColors.Brush(tint),
            FontSize = 9,
        });
        _legend.Inlines.Add(new System.Windows.Documents.Run($"{name} {percent}%")
        {
            Foreground = IslandColors.Brush(IslandColors.White(0.7)),
        });
    }

    private void RenderGrid()
    {
        _gridCanvas.Children.Clear();
        _monthLabels.Children.Clear();
        var width = _gridCanvas.ActualWidth;
        var height = _gridCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var year = DateTime.Now.Year;
        var jan1 = new DateTime(year, 1, 1);
        var dec31 = new DateTime(year, 12, 31);
        var today = DateTime.Today;
        // Columns are Monday-started weeks covering the whole year.
        var start = jan1.AddDays(-(((int)jan1.DayOfWeek + 6) % 7));
        var totalDays = (dec31 - start).Days + 1;
        var columns = (totalDays + Rows - 1) / Rows;
        var pitchX = width / columns;
        var pitchY = height / Rows;
        var cell = Math.Max(3, Math.Min(pitchX, pitchY) - 2);

        // Month labels sit above the week column containing each month's 1st.
        for (var month = 1; month <= 12; month++)
        {
            var first = new DateTime(year, month, 1);
            var column = (first - start).Days / Rows;
            var label = new TextBlock
            {
                Text = Localization.L10n.IsChinese ? $"{month}月" : first.ToString("MMM"),
                FontFamily = IslandFonts.Ui,
                FontSize = 10,
                Foreground = IslandColors.Brush(IslandColors.White(0.45)),
            };
            Canvas.SetLeft(label, column * pitchX);
            Canvas.SetTop(label, 0);
            _monthLabels.Children.Add(label);
        }

        var maxTokens = Math.Max(1, _days.Count == 0
            ? 1
            : _days.Values.Max(day => day.Values.Sum(t => t.Tokens)));

        for (var day = jan1; day <= dec31; day = day.AddDays(1))
        {
            var index = (day - start).Days;
            var column = index / Rows;
            var row = index % Rows;
            var x = column * pitchX;
            var y = row * pitchY;
            // The grid always reads wire tokens (the token-count mode governs
            // the hero, not the grid — same as the pre-generalization code).
            _days.TryGetValue(day, out var providers);
            var ranked = providers is null
                ? new List<KeyValuePair<DisplayProvider, DayProviderTotals>>()
                : providers
                    .Where(kv => kv.Value.Tokens > 0)
                    .OrderByDescending(kv => kv.Value.Tokens)
                    .ToList();
            var total = ranked.Sum(kv => kv.Value.Tokens);

            UIElement element;
            if (day > today)
            {
                element = MakeCellRect(cell, IslandColors.Brush(IslandColors.White(0.03)));
            }
            else if (total == 0)
            {
                element = MakeCellRect(cell, IslandColors.Brush(IslandColors.White(0.07)));
            }
            else
            {
                var intensity = Math.Clamp(
                    Math.Log10(1 + total) / Math.Log10(1 + maxTokens), 0.30, 1.0);
                var dominant = ranked[0];
                var dominantShare = dominant.Value.Tokens / (double)total;
                if (ranked.Count == 1 || dominantShare >= 0.85)
                {
                    element = MakeCellRect(cell, IslandColors.Brush(IslandColors.For(dominant.Key), intensity));
                }
                else
                {
                    // Mixed day: diagonal split between the day's top-2
                    // providers, the lower slot-order upper-left (so a
                    // Claude/Codex day still reads Claude upper-left / Codex
                    // lower-right — the macOS treatment).
                    var pair = ranked.Take(2).OrderBy(kv => kv.Key.SlotOrder()).ToList();
                    element = MakeSplitCell(cell, intensity,
                        IslandColors.For(pair[0].Key), IslandColors.For(pair[1].Key));
                }
            }

            if (element is FrameworkElement fe)
            {
                fe.Cursor = System.Windows.Input.Cursors.Hand;
                var captured = day;
                fe.MouseLeftButtonUp += (_, args) =>
                {
                    ShowDetail(captured);
                    args.Handled = true;
                };
            }
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            _gridCanvas.Children.Add(element);
        }
    }

    private static Rectangle MakeCellRect(double cell, Brush fill) => new()
    {
        Width = cell,
        Height = cell,
        RadiusX = 2,
        RadiusY = 2,
        Fill = fill,
    };

    private static Grid MakeSplitCell(double cell, double intensity, Color upperLeft, Color lowerRight)
    {
        var host = new Grid
        {
            Width = cell,
            Height = cell,
            Clip = new RectangleGeometry(new Rect(0, 0, cell, cell), 2, 2),
        };
        host.Children.Add(new Polygon
        {
            Points = new PointCollection { new(0, 0), new(cell, 0), new(0, cell) },
            Fill = IslandColors.Brush(upperLeft, intensity),
        });
        host.Children.Add(new Polygon
        {
            Points = new PointCollection { new(cell, 0), new(cell, cell), new(0, cell) },
            Fill = IslandColors.Brush(lowerRight, intensity),
        });
        return host;
    }

    private void ShowDetail(DateTime day)
    {
        _days.TryGetValue(day, out var providers);
        var date = day.ToString(Localization.L10n.IsChinese ? "M月d日" : "MMM d");
        var parts = new List<string>();
        if (providers is not null)
        {
            foreach (var provider in DisplayProviders.All)
            {
                if (!providers.TryGetValue(provider, out var totals) || totals.Tokens <= 0) continue;
                var line = $"{ProviderIdentity.DisplayName(provider)} {Core.Formatting.CompactTokens(totals.Tokens)}";
                // Cursor logs tokens with no model → no price; showing "$0.00"
                // would coin a number the publish gate forbids.
                if (Report.ReportFormat.ProvidesDollars(provider))
                {
                    line += $" ({Core.Formatting.Money(totals.Dollars)})";
                }
                parts.Add(line);
            }
        }
        _detail.Text = parts.Count == 0 ? date : $"{date} · {string.Join(" · ", parts)}";
    }

    private static Dictionary<DateTime, Dictionary<DisplayProvider, DayProviderTotals>> MergeHistory(CostStore cost)
    {
        var merged = new Dictionary<DateTime, Dictionary<DisplayProvider, DayProviderTotals>>();
        foreach (var provider in DisplayProviders.All)
        {
            foreach (var bucket in cost.Summary(provider).DailyHistory)
            {
                var key = bucket.DayStart.Date;
                if (!merged.TryGetValue(key, out var day))
                {
                    day = new Dictionary<DisplayProvider, DayProviderTotals>();
                    merged[key] = day;
                }
                day.TryGetValue(provider, out var entry);
                day[provider] = new DayProviderTotals(
                    (entry?.Tokens ?? 0) + bucket.Tokens,
                    (entry?.Billable ?? 0) + bucket.BillableTokens,
                    (entry?.Dollars ?? 0) + bucket.Dollars);
            }
        }
        return merged;
    }

    /// One provider's totals for a single day of the overview grid.
    private sealed record DayProviderTotals(long Tokens, long Billable, double Dollars);
}
