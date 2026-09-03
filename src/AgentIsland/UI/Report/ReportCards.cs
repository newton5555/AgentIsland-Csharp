using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AgentIsland.Model;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI.Report;

/// The two share cards, v3 layout (locked 2026-07-16; character art lands
/// later in the reserved slot above the faceoff bar):
///   header  — app logo top-left on the wordmark line, date right
///   hero    — big number with the "≈ $X API value" line sharing its
///             baseline
///   faceoff — official provider logos at both ends, a two-color beam split
///             by share, a white spark at the meeting point, 144px of
///             head-room reserved for the character art
///   middle  — weekly: 7-day bars (peak in brand teal + its value) then a
///             TOP-3 model pie; monthly: a TOP-5 model pie (heatmap gone)
///   footer  — rank block, no plate, no divider: lifetime line + the
///             congratulations line in gold
/// Flat #0D0F13 base, no gradients. Numbers render tabular (no slashed
/// zeros). Fixed 420x560 portrait; `rounded: false` builds the EXPORT
/// version — square corners and full-bleed, because social apps flatten
/// transparency to white and rounded transparent corners paste as nicks.
public static class ReportCards
{
    public const double CardWidth = 420;
    public const double CardHeight = 560;

    private static readonly Color Base = Color.FromRgb(0x0D, 0x0F, 0x13);
    private static readonly Color LiveTeal = Color.FromRgb(0x3D, 0xD6, 0x8C);

    public static FrameworkElement Weekly(WeeklyReportData data, bool rounded = true)
    {
        var zh = ReportFormat.IsChinese;
        var body = FlexColumn(
            (Header("WEEKLY", data.RangeText), 0),
            (Hero(Localization.L10n.Tr("tokens this week"), data.TotalTokens, data.TotalDollars, zh), 14),
            (FaceoffStage(data.Providers, zh), 12),
            (WeekBars(data, zh), 14),
            (ModelTable(data.TopModels, zh), 14));
        return Card(body, rounded);
    }

    public static FrameworkElement Monthly(MonthlyReportData data, bool rounded = true)
    {
        var zh = ReportFormat.IsChinese;
        var body = FlexColumn(
            (Header("MONTHLY", data.MonthText), 0),
            (Hero(Localization.L10n.Tr("tokens this month"), data.TotalTokens, data.TotalDollars, zh), 14),
            (FaceoffStage(data.Providers, zh), 16),
            (ModelTable(data.TopModels, zh), 18));
        return Card(body, rounded);
    }

    /// Sections interleaved with star-sized spacer rows carrying a minimum
    /// height — WPF's rendition of SwiftUI's Spacer(minLength:).
    private static Grid FlexColumn(params (UIElement Element, double MinGap)[] sections)
    {
        var grid = new Grid();
        var row = 0;
        foreach (var (element, minGap) in sections)
        {
            if (minGap > 0)
            {
                grid.RowDefinitions.Add(new RowDefinition
                {
                    Height = new GridLength(1, GridUnitType.Star),
                    MinHeight = minGap,
                });
                row++;
            }
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow((FrameworkElement)element, row);
            grid.Children.Add(element);
            row++;
        }
        return grid;
    }

    // MARK: - Scaffold

    private static FrameworkElement Card(UIElement body, bool rounded)
    {
        var radius = rounded ? 26.0 : 0.0;
        var root = new Grid { Width = CardWidth, Height = CardHeight };

        // v3: one flat base, no gradients, no auras. A uniform hairline
        // keeps the card from melting into dark chat backgrounds.
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = IslandColors.Brush(Base),
            BorderThickness = new Thickness(1),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.06)),
        });

        var content = new Grid { Margin = new Thickness(28) };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 0);
        content.Children.Add(body);
        root.Children.Add(content);

        root.Clip = new RectangleGeometry(new Rect(0, 0, CardWidth, CardHeight), radius, radius);
        System.Windows.Media.TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        return root;
    }

    /// Numbers on the card read tabular — and Segoe UI's zero carries no
    /// slash, which the v3 spec calls out explicitly.
    private static TextBlock Numeric(TextBlock block)
    {
        Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }

    /// WPF has no letter-spacing; interleaved spaces fake the macOS wide
    /// wordmark tracking (the mac card spaces its letters visibly apart).
    private static string Track(string text) => string.Join(' ', text.ToCharArray());

    private static UIElement Header(string tag, string rangeText)
    {
        var row = new DockPanel { LastChildFill = false };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // v4: the BARE transparent five-blade mark — neither a plate nor
        // the app icon (owner review ×2, 2026-08-09). The small-optimized
        // variant keeps the blades separable at 22px.
        try
        {
            var logo = new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/agentisland_logo_small.png")),
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
            };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            brand.Children.Add(logo);
        }
        catch
        {
        }
        brand.Children.Add(new TextBlock
        {
            Text = Track("AGENT ISLAND"),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.88)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        brand.Children.Add(new TextBlock
        {
            Text = " " + Track(tag),
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
        });
        DockPanel.SetDock(brand, Dock.Left);
        row.Children.Add(brand);
        var range = Numeric(new TextBlock
        {
            Text = rangeText,
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
        });
        DockPanel.SetDock(range, Dock.Right);
        row.Children.Add(range);
        return row;
    }

    private static UIElement Hero(string title, long totalTokens, double totalDollars, bool zh)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = IslandFonts.Ui,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.5)),
            Margin = new Thickness(0, 0, 0, 6),
        });
        var (value, unit) = ReportFormat.CompactParts(totalTokens, zh);
        // The big number and the "≈ $X" line share one baseline (v3); the
        // number is plain near-white — v3 dropped the vertical sheen.
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var numberBrush = IslandColors.Brush(Color.FromRgb(0xF2, 0xF5, 0xF7));
        line.Children.Add(Numeric(new TextBlock
        {
            Text = value,
            FontFamily = IslandFonts.Ui,
            FontSize = 50,
            FontWeight = FontWeights.ExtraBold,
            Foreground = numberBrush,
            // WPF's default line box adds ~1/3 of the font size; pin the
            // line to the glyphs (P21 lesson).
            LineHeight = 54,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        }));
        if (unit.Length > 0)
        {
            line.Children.Add(new TextBlock
            {
                Text = unit,
                FontFamily = IslandFonts.Ui,
                FontSize = zh ? 24 : 50,
                FontWeight = FontWeights.ExtraBold,
                Foreground = numberBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(3, 0, 0, zh ? 6 : 0),
            });
        }
        if (totalDollars >= 1)
        {
            line.Children.Add(Numeric(new TextBlock
            {
                Text = Localization.L10n.TrFormat("≈ ${0} API value", ReportFormat.Money(totalDollars)),
                FontFamily = IslandFonts.Ui,
                FontSize = 12.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = IslandColors.Brush(Colors.White),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(12, 0, 0, 7),
            }));
        }
        // A wide month (17.12亿 + $2,043) can outgrow the card; scale the
        // whole line down rather than clip the money line's last glyph.
        stack.Children.Add(new Viewbox
        {
            Child = line,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxWidth = CardWidth - 60,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        return stack;
    }

    // MARK: - Top-2 duel (macOS ReportDuel, generalized 2026-08-08)

    /// Provider marks anchor the two ends of a split beam; the clash spark
    /// sits at the usage-share split, and the chibi duel artwork stands over
    /// it. With five providers this is a TOP-2 duel — the two highest-token
    /// providers face off, identity (mark, accent, name) coming from
    /// ProviderIdentity for BOTH sides. Sides are pinned by canonical slot
    /// order so the three pose PNGs — which only exist for Claude vs Codex —
    /// still land correctly; any other pairing shows the beam and marks
    /// alone. One provider → the solo treatment (no phantom opponent).
    private const double DuelArtHeight = 82;
    private const double DuelMarkSide = 18;
    private const double DuelSoloMarkSide = 28;
    private const double DuelMarkGap = 10;
    private const double DuelBeamHeight = 6;

    private static UIElement FaceoffStage(IReadOnlyList<ProviderPeriodSlice> providers, bool zh)
    {
        var contentWidth = CardWidth - 56; // 28pt card padding each side
        var beamX0 = DuelMarkSide + DuelMarkGap;
        var beamWidth = contentWidth - 2 * beamX0;
        var beamY = DuelArtHeight + 10;

        var stack = new StackPanel();
        var canvas = new Canvas { Width = contentWidth, Height = DuelArtHeight + 20 };
        stack.Children.Add(canvas);

        var ran = providers.Where(p => p.Tokens > 0).OrderByDescending(p => p.Tokens).ToList();
        if (ran.Count == 0)
        {
            // No activity in the period: the stage keeps its reserved height,
            // with nothing to duel.
            return stack;
        }
        if (ran.Count == 1)
        {
            SoloStage(stack, canvas, ran[0], contentWidth, beamX0, beamWidth, beamY);
            return stack;
        }

        // Left is the lower slot-order of the top-2 (Claude before Codex,
        // etc.); the pose art assumes Claude-left / Codex-right, and the
        // spark/beam split reads the same regardless of which side is larger.
        var left = ran[0].Provider.SlotOrder() <= ran[1].Provider.SlotOrder() ? ran[0] : ran[1];
        var right = left.Provider == ran[0].Provider ? ran[1] : ran[0];
        var pairTotal = (double)(left.Tokens + right.Tokens);
        var leftShare = pairTotal > 0 ? left.Tokens / pairTotal : 0.5;

        // The spark rides the TRUE split; only the artwork clamps inward so
        // a 90/10 blowout doesn't shove it off the card.
        var sparkX = beamX0 + beamWidth * Math.Min(0.97, Math.Max(0.03, leftShare));
        var artX = beamX0 + beamWidth * Math.Min(0.74, Math.Max(0.26, leftShare));

        // The chibi poses exist only for the Claude/Codex pair; every other
        // pairing shows the beam + marks alone.
        if (left.Provider == DisplayProvider.Claude && right.Provider == DisplayProvider.Codex)
        {
            var pose = leftShare >= 0.52 ? "duel-claude-wins"
                : leftShare <= 0.48 ? "duel-codex-wins" : "duel-draw";
            TryAddDuelArt(canvas, pose, artX);
        }

        var leftAccent = ProviderIdentity.Accent(left.Provider);
        var rightAccent = ProviderIdentity.Accent(right.Provider);

        var leftMark = ProviderMark(left.Provider);
        Canvas.SetLeft(leftMark, 0);
        Canvas.SetTop(leftMark, beamY - DuelMarkSide / 2);
        canvas.Children.Add(leftMark);

        var rightMark = ProviderMark(right.Provider);
        Canvas.SetLeft(rightMark, contentWidth - DuelMarkSide);
        Canvas.SetTop(rightMark, beamY - DuelMarkSide / 2);
        canvas.Children.Add(rightMark);

        // Two capsule beams meeting at the split, each brightening toward
        // its provider's end. No glow — the spark carries the light.
        var leftBeamWidth = Math.Max(3, beamWidth * leftShare - 0.75);
        var leftBeam = new Border
        {
            Width = leftBeamWidth,
            Height = DuelBeamHeight,
            CornerRadius = new CornerRadius(3),
            Background = new LinearGradientBrush(BeamShoulder(left.Provider), leftAccent, 0),
        };
        Canvas.SetLeft(leftBeam, beamX0);
        Canvas.SetTop(leftBeam, beamY - DuelBeamHeight / 2);
        canvas.Children.Add(leftBeam);

        var rightBeam = new Border
        {
            Width = Math.Max(3, beamWidth - leftBeamWidth - 1.5),
            Height = DuelBeamHeight,
            CornerRadius = new CornerRadius(3),
            Background = new LinearGradientBrush(rightAccent, BeamShoulder(right.Provider), 0),
        };
        Canvas.SetLeft(rightBeam, beamX0 + leftBeamWidth + 1.5);
        Canvas.SetTop(rightBeam, beamY - DuelBeamHeight / 2);
        canvas.Children.Add(rightBeam);

        canvas.Children.Add(ClashSpark(sparkX, beamY, leftAccent, rightAccent));

        // Share legend under the bar's ends: ● Claude 67% … ● Codex 33%.
        var legend = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        var leftSide = ShareTag(ProviderIdentity.DisplayName(left.Provider), leftShare, leftAccent);
        DockPanel.SetDock(leftSide, Dock.Left);
        legend.Children.Add(leftSide);
        var rightSide = ShareTag(ProviderIdentity.DisplayName(right.Provider), 1 - leftShare, rightAccent);
        DockPanel.SetDock(rightSide, Dock.Right);
        legend.Children.Add(rightSide);
        stack.Children.Add(legend);
        return stack;
    }

    /// One provider ran: a single centered mark over a full beam, no spark,
    /// no phantom opponent — the card must not imply a duel that didn't
    /// happen.
    private static void SoloStage(
        StackPanel stack, Canvas canvas, ProviderPeriodSlice solo,
        double contentWidth, double beamX0, double beamWidth, double beamY)
    {
        var accent = ProviderIdentity.Accent(solo.Provider);

        var mark = ProviderMark(solo.Provider, DuelSoloMarkSide);
        Canvas.SetLeft(mark, contentWidth / 2 - DuelSoloMarkSide / 2);
        Canvas.SetTop(mark, DuelArtHeight / 2 - DuelSoloMarkSide / 2 + 1);
        canvas.Children.Add(mark);

        var beam = new Border
        {
            Width = beamWidth,
            Height = DuelBeamHeight,
            CornerRadius = new CornerRadius(3),
            Background = new LinearGradientBrush(BeamShoulder(solo.Provider), accent, 0),
        };
        Canvas.SetLeft(beam, beamX0);
        Canvas.SetTop(beam, beamY - DuelBeamHeight / 2);
        canvas.Children.Add(beam);

        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        legend.Children.Add(ShareTag(ProviderIdentity.DisplayName(solo.Provider), 1, accent));
        stack.Children.Add(legend);
    }

    private static void TryAddDuelArt(Canvas canvas, string pose, double artX)
    {
        try
        {
            var bitmap = new BitmapImage(new Uri($"pack://application:,,,/Assets/Report/{pose}.png"));
            var artWidth = DuelArtHeight * bitmap.PixelWidth / Math.Max(1, bitmap.PixelHeight);
            var art = new Image
            {
                Source = bitmap,
                Height = DuelArtHeight,
                Width = artWidth,
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(art, artX - artWidth / 2);
            Canvas.SetTop(art, 1);
            canvas.Children.Add(art);
        }
        catch
        {
            // Art missing from the bundle: the stage keeps its height.
        }
    }

    /// A brighter shoulder for a beam's outer end. Claude and Codex keep the
    /// hand-picked warm/cool shoulders of the original two-way card; other
    /// providers get a generic lift toward white off their accent.
    private static Color BeamShoulder(DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => Color.FromRgb(0xE0, 0x8A, 0x63),
        DisplayProvider.Codex => Color.FromRgb(0x7F, 0xBC, 0xF5),
        _ => Lighten(ProviderIdentity.Accent(provider), 0.28),
    };

    private static Color Lighten(Color c, double t) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t),
        (byte)(c.G + (255 - c.G) * t),
        (byte)(c.B + (255 - c.B) * t));

    /// White core + four-point star, warm shoulder to the left provider's
    /// side and cool to the right — the "swords meet here" moment.
    private static UIElement ClashSpark(double x, double y, Color leftColor, Color rightColor)
    {
        var spark = new Grid { Width = 20, Height = 20 };
        // Side lights first, under the star.
        var warm = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = IslandColors.Brush(IslandColors.Alpha(leftColor, 0.55)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-10, 0, 0, 0),
        };
        var cool = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = IslandColors.Brush(IslandColors.Alpha(rightColor, 0.55)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        spark.Children.Add(warm);
        spark.Children.Add(cool);
        foreach (var angle in new[] { 14.0, 104.0 })
        {
            spark.Children.Add(new Border
            {
                Width = 1.6,
                Height = 17,
                CornerRadius = new CornerRadius(0.8),
                Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(angle),
            });
        }
        spark.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 12,
                Color = Colors.White,
                Opacity = 0.95,
            },
        });
        Canvas.SetLeft(spark, x - 10);
        Canvas.SetTop(spark, y - 10);
        System.Windows.Controls.Panel.SetZIndex(spark, 2);
        return spark;
    }

    private static UIElement ShareTag(string name, double share, Color color)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = IslandColors.Brush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = name + " ",
            FontFamily = IslandFonts.Ui,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.8)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(Numeric(new TextBlock
        {
            Text = $"{Core.Formatting.PercentInt(share)}%",
            FontFamily = IslandFonts.Ui,
            FontSize = 11.5,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(color),
            VerticalAlignment = VerticalAlignment.Center,
        }));
        return row;
    }

    private static UIElement ProviderMark(DisplayProvider provider, double side = DuelMarkSide) =>
        ProviderMarks.Mark(provider, side, tintOpacity: 1);

    // MARK: - Weekly bars

    private static UIElement WeekBars(WeeklyReportData data, bool zh)
    {
        var peak = Math.Max(data.DailyTokens.Max(), 1);
        var grid = new Grid();
        for (var i = 0; i < 7; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var i = 0; i < 7; i++)
        {
            var tokens = data.DailyTokens[i];
            var isPeak = tokens == peak && tokens > 0;
            var cell = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(5, 0, 5, 0),
            };
            // The value slot exists on every column (blank when not the
            // peak) so all seven bars share one floor line (macOS keeps the
            // row with a " " placeholder).
            cell.Children.Add(Numeric(new TextBlock
            {
                Text = isPeak ? ReportFormat.CompactString(tokens, zh) : " ",
                FontFamily = IslandFonts.Ui,
                FontSize = 9,
                FontWeight = FontWeights.ExtraBold,
                Foreground = IslandColors.Brush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5),
            }));
            cell.Children.Add(new Border
            {
                Height = Math.Max(5, 58.0 * tokens / peak),
                CornerRadius = new CornerRadius(4),
                Background = IslandColors.Brush(isPeak ? Colors.White : IslandColors.White(tokens > 0 ? 0.16 : 0.07)),
            });
            cell.Children.Add(new TextBlock
            {
                Text = i < data.DayLetters.Count ? data.DayLetters[i] : "",
                FontFamily = IslandFonts.Ui,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = IslandColors.Brush(isPeak ? Colors.White : IslandColors.White(0.32)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0),
            });
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    // MARK: - Model donut + rows

    /// Donut with "TOP N" in the hole and plain rows beside it — no header
    /// row (macOS v3 final: the dot + display name + three numbers explain
    /// themselves). Segments sweep the TRUE share of dollars; the uncovered
    /// arc IS the long tail, so nothing is normalized and there is no
    /// "Others" row. Display names (Fable 5, GPT-5.6-sol) — raw ids read
    /// like log spam on a share card.
    private static UIElement ModelTable(IReadOnlyList<ModelShare> models, bool zh)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        const double size = 88;
        const double thickness = 13;
        var donut = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        donut.Children.Add(new Ellipse
        {
            Stroke = IslandColors.Brush(IslandColors.White(0.07)),
            StrokeThickness = thickness,
            Margin = new Thickness(thickness / 2),
        });
        var cumulative = 0.0;
        foreach (var model in models)
        {
            var from = cumulative;
            cumulative += model.Percent;
            var gap = model.Percent > 0.03 ? 0.006 : 0.0;
            var start = from + gap;
            var end = Math.Max(start, cumulative - gap);
            if (end - start <= 0.0005) continue;
            donut.Children.Add(DonutSegment(size, thickness, start, end, model.Color));
        }
        donut.Children.Add(new TextBlock
        {
            // macOS v4: the hole says MODELS — a "TOP N" recount went stale
            // the moment a period had fewer than N models.
            Text = Localization.L10n.Tr("MODELS"),
            FontFamily = IslandFonts.Ui,
            FontSize = 10.5,
            FontWeight = FontWeights.ExtraBold,
            Foreground = IslandColors.Brush(IslandColors.White(0.5)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(donut, 0);
        row.Children.Add(donut);

        var table = new Grid { VerticalAlignment = VerticalAlignment.Center };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        var rowIndex = 0;
        foreach (var model in models)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var pad = rowIndex == 0 ? 0 : 4.5; // macOS row spacing 9

            var name = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, pad, 0, 4.5),
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = IslandColors.Brush(model.Color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
            });
            name.Children.Add(new TextBlock
            {
                Text = ReportFormat.DisplayModelName(model.Name),
                FontFamily = IslandFonts.Ui,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = IslandColors.Brush(IslandColors.White(0.85)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetRow(name, rowIndex);
            Grid.SetColumn(name, 0);
            table.Children.Add(name);

            TextBlock Cell(string text, Color color)
            {
                return Numeric(new TextBlock
                {
                    Text = text,
                    FontFamily = IslandFonts.Ui,
                    FontSize = 10.5,
                    FontWeight = FontWeights.ExtraBold,
                    Foreground = IslandColors.Brush(color),
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, pad, 0, 4.5),
                });
            }
            var tokens = Cell(ReportFormat.CompactString(model.Tokens, zh), IslandColors.White(0.5));
            Grid.SetRow(tokens, rowIndex);
            Grid.SetColumn(tokens, 1);
            table.Children.Add(tokens);

            // A dollar figure only where the provider can be priced; Cursor/
            // Gemini rows read "—", never a coined $0 (publish-gate honesty,
            // mirroring macOS ReportModelTable).
            var dollars = Cell(
                ReportFormat.ProvidesDollars(model.Provider)
                    ? $"${ReportFormat.Money(model.Dollars)}"
                    : "—",
                IslandColors.Alpha(Color.FromRgb(0x8C, 0xD9, 0x9E), 0.9));
            Grid.SetRow(dollars, rowIndex);
            Grid.SetColumn(dollars, 2);
            table.Children.Add(dollars);

            var share = Cell($"{Core.Formatting.PercentInt(model.Percent)}%", IslandColors.White(0.88));
            Grid.SetRow(share, rowIndex);
            Grid.SetColumn(share, 3);
            table.Children.Add(share);
            rowIndex++;
        }
        Grid.SetColumn(table, 2);
        row.Children.Add(table);
        return row;
    }

    private static System.Windows.Shapes.Path DonutSegment(
        double size, double thickness, double from, double to, Color color)
    {
        var center = size / 2;
        var radius = (size - thickness) / 2;
        // 0..1 → degrees from 12 o'clock, clockwise.
        var startAngle = from * 360 - 90;
        var endAngle = to * 360 - 90;
        Point PointAt(double deg)
        {
            var rad = deg * Math.PI / 180;
            return new Point(center + radius * Math.Cos(rad), center + radius * Math.Sin(rad));
        }
        var figure = new PathFigure { StartPoint = PointAt(startAngle), IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            PointAt(endAngle),
            new Size(radius, radius),
            0,
            isLargeArc: endAngle - startAngle > 180,
            SweepDirection.Clockwise,
            isStroked: true));
        return new System.Windows.Shapes.Path
        {
            Data = new PathGeometry(new[] { figure }),
            Stroke = IslandColors.Brush(color),
            StrokeThickness = thickness,
        };
    }

}
