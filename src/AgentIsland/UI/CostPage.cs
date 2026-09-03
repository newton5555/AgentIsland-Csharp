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

/// Cost page: per provider, today's spend as the hero number, a cumulative
/// sparkline for the day, and month + token context lines. The hero swaps
/// between USD / TOKENS / TREND per the cost style preference.
///
/// Cost is reconstructed from LOCAL token logs. Claude Code and Codex are
/// table-priced and Grok self-reports dollars, so those three show a full
/// dollar tile; Cursor logs tokens but no model, so its tile shows tokens with
/// "—" for dollars; Gemini ships no local ledger, so its slot keeps the quiet
/// nameplate rather than a fabricated $0 — the same nameplate that fills the
/// freed half of a solo split.
public sealed class CostPage : Border
{
    private readonly Dictionary<DisplayProvider, CostBlock> _blocks = new();
    private readonly Dictionary<DisplayProvider, UIElement> _badges = new();

    /// How a provider's slot presents cost. Claude/Codex are table-priced and
    /// Grok self-reports dollars, so all three show a full dollar tile; Cursor
    /// has token counts but no model and therefore no price, so its tile reads
    /// "—" where a dollar would go; Gemini ships no local ledger at all, so it
    /// gets the quiet nameplate the guests always had, never a fabricated $0.
    private enum CostFace { Dollars, TokensOnly, Cold }

    private static CostFace FaceOf(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Antigravity => CostFace.Cold,
        DisplayProvider.Cursor => CostFace.TokensOnly,
        _ => CostFace.Dollars,
    };

    public CostPage()
    {
        Padding = new Thickness(22, 12, 22, 6);
        var grid = new Grid();
        Child = grid;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var hairline = new Border
        {
            Width = 1,
            Margin = new Thickness(0, 8, 0, 8),
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(IslandColors.White(0.06), 0.5),
                    new GradientStop(Colors.Transparent, 1),
                },
                new Point(0, 0),
                new Point(0, 1)),
        };
        Grid.SetColumn(hairline, 1);
        grid.Children.Add(hairline);

        // Every provider owns its widgets up front; a slot change only
        // re-columns and re-shows them, so nothing here is rebuilt mid-flight
        // (a rebuilt WPF element loses its animation state and can flash empty).
        // Cost tile for each provider that has one, keyed by identity; a
        // nameplate for all five (the freed half of a solo split, and Gemini's
        // cold state).
        foreach (var provider in DisplayProviders.All)
        {
            if (FaceOf(provider) != CostFace.Cold)
            {
                var block = new CostBlock(
                    ProviderIdentity.Accent(provider),
                    showsDollars: FaceOf(provider) == CostFace.Dollars)
                {
                    Visibility = Visibility.Collapsed,
                };
                _blocks[provider] = block;
                grid.Children.Add(block);
            }
            var badge = new SoloProviderBadge(provider) { Visibility = Visibility.Collapsed };
            _badges[provider] = badge;
            grid.Children.Add(badge);
        }

        static void Show(UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            element.Visibility = Visibility.Visible;
        }

        void Place(DisplayProvider provider, int column) =>
            Show(FaceOf(provider) == CostFace.Cold ? _badges[provider] : _blocks[provider], column);

        void ApplyVisibility()
        {
            var slots = ProviderVisibilityStore.Shared.SlotProviders;
            foreach (var block in _blocks.Values) block.Visibility = Visibility.Collapsed;
            foreach (var badge in _badges.Values) badge.Visibility = Visibility.Collapsed;

            if (slots.Count >= 2)
            {
                Place(slots[0], 0);
                Place(slots[1], 2);
            }
            else if (slots.Count == 1)
            {
                // Solo split: the live column keeps the flank its island logo
                // holds, and the same provider's nameplate fills the freed
                // half (macOS soloBadge). A cold-state provider (Gemini) has
                // no cost column, so its nameplate simply takes the logo flank.
                var solo = slots[0];
                var leading = solo.SoloLogoFlankIsLeading();
                if (FaceOf(solo) != CostFace.Cold)
                {
                    Place(solo, leading ? 0 : 2);
                    Show(_badges[solo], leading ? 2 : 0);
                }
                else
                {
                    Show(_badges[solo], leading ? 0 : 2);
                }
            }

            hairline.Visibility = slots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // PagedContent recreates this page on visibility/screen changes;
        // detach on Unloaded or each dead instance stays pinned by the
        // singleton stores and keeps running Update forever.
        System.ComponentModel.PropertyChangedEventHandler onUpdate =
            (_, _) => Dispatcher.BeginInvoke(Update);
        System.ComponentModel.PropertyChangedEventHandler onVisibility =
            (_, _) => Dispatcher.BeginInvoke(ApplyVisibility);
        CostStore.Shared.PropertyChanged += onUpdate;
        CostStylePreferenceStore.Shared.PropertyChanged += onUpdate;
        ProviderVisibilityStore.Shared.PropertyChanged += onVisibility;
        Unloaded += (_, _) =>
        {
            CostStore.Shared.PropertyChanged -= onUpdate;
            CostStylePreferenceStore.Shared.PropertyChanged -= onUpdate;
            ProviderVisibilityStore.Shared.PropertyChanged -= onVisibility;
        };
        ApplyVisibility();
        Update();
    }

    private void Update()
    {
        foreach (var (provider, block) in _blocks)
        {
            block.Update(CostStore.Shared.Summary(provider));
        }
    }
}

/// One provider's cost column.
public sealed class CostBlock : StackPanel
{
    /// Stands in for a dollar figure a tokens-only provider (Cursor) cannot
    /// price. Matches the em dash the rest of the UI uses for "no value"
    /// (ResetCardChip, TurnAlarmWindow).
    private const string NoDollar = "—";

    private readonly TextBlock _hero;
    private readonly TextBlock _heroCaption;
    private readonly Sparkline _sparkline;
    private readonly TextBlock _monthLine;
    private readonly TextBlock _tokenLine;
    private readonly CountUp _countUp;
    private readonly bool _showsDollars;

    public CostBlock(Color tint, bool showsDollars = true)
    {
        _showsDollars = showsDollars;
        Orientation = Orientation.Vertical;
        Margin = new Thickness(12, 0, 12, 0);

        var heroRow = new StackPanel { Orientation = Orientation.Horizontal };
        // Brand-tinted hero with a spend-scaled glow and count-up — the
        // macOS CostBlock centerpiece.
        _hero = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandColors.Brush(tint),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 10,
                Color = tint,
                Opacity = 0,
            },
        };
        _countUp = new CountUp(_hero);
        _heroCaption = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 6),
            Text = Localization.L10n.Tr("today"),
        };
        heroRow.Children.Add(_hero);
        heroRow.Children.Add(_heroCaption);
        Children.Add(heroRow);

        _sparkline = new Sparkline(tint)
        {
            Height = 30,
            Margin = new Thickness(0, 8, 0, 8),
        };
        Children.Add(_sparkline);

        _monthLine = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
        };
        Children.Add(_monthLine);

        _tokenLine = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 10,
            Foreground = IslandColors.Brush(IslandColors.White(0.4)),
            Margin = new Thickness(0, 3, 0, 0),
        };
        Children.Add(_tokenLine);
    }

    public void Update(ProviderCostSummary summary)
    {
        // Tokens-only provider (Cursor): no model → no price, so the tile
        // leads with the token count and every dollar slot reads "—". This
        // ignores the cost-style preference because the dollar/trend styles
        // have nothing to show here, and a "$0.00" would be a fabricated
        // number the publish gate forbids. The cumulative series is dollars
        // (all zero without a price), so the sparkline sits flat and honest.
        if (!_showsDollars)
        {
            _countUp.Animate(summary.TodayTokens, "tokens",
                v => Core.Formatting.CompactTokens((long)Math.Round(v)));
            ApplyGlow(0);
            _heroCaption.Text = Localization.L10n.Tr("tokens today");
            _sparkline.SetSeries(summary.TodayCumulativeDollars);
            _monthLine.Text = Localization.L10n.TrFormat("{0} this month", NoDollar);
            _tokenLine.Text = Localization.L10n.TrFormat(
                "{0} tokens · {1} billable",
                Core.Formatting.CompactTokens(summary.TodayTokens),
                Core.Formatting.CompactTokens(summary.TodayBillableTokens));
            return;
        }
        switch (CostStylePreferenceStore.Shared.Style)
        {
            case CostStyle.Tokens:
                _countUp.Animate(summary.TodayTokens, "tokens",
                    v => Core.Formatting.CompactTokens((long)Math.Round(v)));
                ApplyGlow(summary.TodayDollars);
                _heroCaption.Text = Localization.L10n.Tr("tokens today");
                _sparkline.SetSeries(summary.TodayCumulativeDollars);
                _monthLine.Text = Localization.L10n.TrFormat(
                    "{0} tokens this month", Core.Formatting.CompactTokens(summary.MonthTokens));
                _tokenLine.Text = Localization.L10n.TrFormat(
                    "{0} billable", Core.Formatting.CompactTokens(summary.TodayBillableTokens));
                break;
            case CostStyle.Trend:
                _countUp.Animate(summary.MonthDollars, "money", Core.Formatting.Money);
                ApplyGlow(summary.MonthDollars);
                _heroCaption.Text = Localization.L10n.Tr("this month");
                _sparkline.SetSeries(summary.MonthCumulativeDollars);
                _monthLine.Text = Localization.L10n.TrFormat(
                    "{0} today", Core.Formatting.Money(summary.TodayDollars));
                _tokenLine.Text = Localization.L10n.TrFormat(
                    "{0} tokens · {1} billable",
                    Core.Formatting.CompactTokens(summary.MonthTokens),
                    Core.Formatting.CompactTokens(summary.MonthBillableTokens));
                break;
            case CostStyle.Multi:
                _countUp.Animate(summary.TodayDollars, "money", Core.Formatting.Money);
                ApplyGlow(summary.TodayDollars);
                _heroCaption.Text = Localization.L10n.Tr("today");
                _sparkline.SetSeries(summary.TodayCumulativeDollars);
                _monthLine.Text = Localization.L10n.TrFormat(
                    "{0} this month · {1} tokens",
                    Core.Formatting.Money(summary.MonthDollars),
                    Core.Formatting.CompactTokens(summary.MonthTokens));
                _tokenLine.Text = Localization.L10n.TrFormat(
                    "{0} tokens · {1} billable",
                    Core.Formatting.CompactTokens(summary.TodayTokens),
                    Core.Formatting.CompactTokens(summary.TodayBillableTokens));
                break;
            case CostStyle.Dollar:
            default:
                _countUp.Animate(summary.TodayDollars, "money", Core.Formatting.Money);
                ApplyGlow(summary.TodayDollars);
                _heroCaption.Text = Localization.L10n.Tr("today");
                _sparkline.SetSeries(summary.TodayCumulativeDollars);
                _monthLine.Text = Localization.L10n.TrFormat(
                    "{0} this month", Core.Formatting.Money(summary.MonthDollars));
                _tokenLine.Text = Localization.L10n.TrFormat(
                    "{0} tokens · {1} billable",
                    Core.Formatting.CompactTokens(summary.TodayTokens),
                    Core.Formatting.CompactTokens(summary.TodayBillableTokens));
                break;
        }
    }

    /// Glow scales logarithmically with spend, capped at 0.85 (macOS rule):
    /// a $0 day is calm, a heavy day radiates.
    private void ApplyGlow(double dollars)
    {
        var glow = (System.Windows.Media.Effects.DropShadowEffect)_hero.Effect;
        glow.Opacity = dollars <= 0
            ? 0
            : Math.Min(0.85, 0.30 + 0.16 * Math.Log10(1 + dollars));
    }
}

/// 0.65s cubic-ease-out numeric count-up — the macOS CountUpDollar
/// behavior, driven by a 60Hz dispatcher timer only while animating.
/// The `unit` guards against counting across incompatible scales: switching
/// the cost style from tokens (millions) to dollars must snap, not tick a
/// 2,000,000 token count rendered as "$2,000,000" down to "$15".
internal sealed class CountUp
{
    private const double Seconds = 0.65;
    private readonly TextBlock _target;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Func<double, string> _format = _ => "";
    private string _unit = "";
    private double _from;
    private double _to;
    private double _current;
    private bool _seeded;
    private DateTime _start;

    public CountUp(TextBlock target)
    {
        _target = target;
        _timer.Tick += (_, _) => Tick();
    }

    public void Animate(double to, string unit, Func<double, string> format)
    {
        _format = format;
        // First appearance: reveal by counting from zero.
        if (!_seeded)
        {
            _seeded = true;
            _unit = unit;
            Run(0, to);
            return;
        }
        // Unit change (tokens <-> dollars): snap, never count across scales.
        if (unit != _unit)
        {
            _unit = unit;
            _timer.Stop();
            _to = _current = to;
            _target.Text = format(to);
            return;
        }
        // Same value already shown: nothing to animate.
        if (Math.Abs(to - _to) < 0.000001)
        {
            if (!_timer.IsEnabled) _target.Text = format(to);
            return;
        }
        // Same unit, new value: continue from what's on screen right now.
        Run(_current, to);
    }

    private void Run(double from, double to)
    {
        _from = from;
        _to = to;
        _current = from;
        _start = DateTime.UtcNow;
        _target.Text = _format(from);
        _timer.Start();
    }

    private void Tick()
    {
        var x = Math.Clamp((DateTime.UtcNow - _start).TotalSeconds / Seconds, 0, 1);
        var eased = 1 - Math.Pow(1 - x, 3);
        _current = _from + (_to - _from) * eased;
        _target.Text = _format(_current);
        if (x >= 1) _timer.Stop();
    }
}

/// Minimal cumulative-cost sparkline: soft area fill under a tinted line.
public sealed class Sparkline : Grid
{
    private readonly Polyline _line;
    private readonly Polygon _fill;
    private IReadOnlyList<double> _series = Array.Empty<double>();

    public Sparkline(Color tint)
    {
        _fill = new Polygon
        {
            Fill = new LinearGradientBrush(
                IslandColors.Alpha(tint, 0.45),
                IslandColors.Alpha(tint, 0),
                90),
        };
        _line = new Polyline
        {
            Stroke = IslandColors.Brush(tint),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 3,
                Color = tint,
                Opacity = 0.7,
            },
        };
        Children.Add(_fill);
        Children.Add(_line);
        SizeChanged += (_, _) => Render();
    }

    public void SetSeries(IReadOnlyList<double> series)
    {
        _series = series;
        Render();
    }

    private void Render()
    {
        _line.Points.Clear();
        _fill.Points.Clear();
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0 || _series.Count < 2) return;
        var max = Math.Max(_series.Max(), 0.0001);
        for (var i = 0; i < _series.Count; i++)
        {
            var x = width * i / (_series.Count - 1);
            var y = height - height * (_series[i] / max) * 0.92 - 1;
            _line.Points.Add(new Point(x, y));
            _fill.Points.Add(new Point(x, y));
        }
        _fill.Points.Add(new Point(width, height));
        _fill.Points.Add(new Point(0, height));
    }
}
