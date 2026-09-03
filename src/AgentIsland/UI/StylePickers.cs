using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// The visual chart-style picker from the macOS Display tab: four preview
/// tiles (stepped / bar / pie / numeric). Previews and selection speak
/// white-on-black only — the 2026-08-09 de-branding stripped the app's own
/// chrome of every accent color (macOS IslandColor.chrome == white).
public sealed class ChartStylePickerControl : Grid
{
    private readonly List<Border> _tiles = new();
    private readonly List<TextBlock> _labels = new();

    public event Action<ChartStyle>? StyleSelected;

    public ChartStylePickerControl(ChartStyle selected)
    {
        var styles = Enum.GetValues<ChartStyle>();
        for (var i = 0; i < styles.Length; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var i = 0; i < styles.Length; i++)
        {
            var style = styles[i];
            var tile = MakeTile(style);
            SetColumn(tile, i);
            _tiles.Add(tile);
            Children.Add(tile);
        }
        Select(selected);
    }

    private Border MakeTile(ChartStyle style)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var preview = new Grid { Height = 40, Width = 72, Margin = new Thickness(0, 0, 0, 10) };
        preview.Children.Add(MakePreview(style));
        stack.Children.Add(preview);
        var label = new TextBlock
        {
            Text = StyleLabel(style),
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.8)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _labels.Add(label);
        stack.Children.Add(label);

        var tile = new Border
        {
            Child = stack,
            Height = 100,
            CornerRadius = new CornerRadius(9),
            Background = IslandColors.Brush(IslandColors.White(0.025)),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        tile.MouseLeftButtonUp += (_, args) =>
        {
            Select(style);
            StyleSelected?.Invoke(style);
            args.Handled = true;
        };
        StyleTileChrome.AttachHover(tile);
        return tile;
    }

    public void Select(ChartStyle selected)
    {
        var styles = Enum.GetValues<ChartStyle>();
        for (var i = 0; i < _tiles.Count; i++)
        {
            StyleTileChrome.Paint(_tiles[i], _labels[i], styles[i] == selected);
        }
    }

    public static string StyleLabel(ChartStyle style) => Localization.L10n.Tr(style switch
    {
        ChartStyle.Stepped => "Stepped",
        ChartStyle.Bar => "Bar",
        ChartStyle.Ring => "Pie",
        ChartStyle.Numeric => "Numeric",
        _ => style.ToString(),
    });

    /// macOS ChartStylePicker previews: every active element is WHITE, every
    /// track a faint white wash — the picker is about SHAPE, not color.
    private static UIElement MakePreview(ChartStyle style)
    {
        switch (style)
        {
            case ChartStyle.Ring:
            {
                var host = new Canvas
                {
                    Width = 26,
                    Height = 26,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var center = new Point(13, 13);
                var outerTrack = new Ellipse
                {
                    Width = 26,
                    Height = 26,
                    Stroke = IslandColors.Brush(IslandColors.White(0.15)),
                    StrokeThickness = 2.0,
                };
                Canvas.SetLeft(outerTrack, 0);
                Canvas.SetTop(outerTrack, 0);
                host.Children.Add(outerTrack);

                var outerArc = new System.Windows.Shapes.Path
                {
                    Data = Charts.RingMeter.ArcGeometry(center, (26 - 2.0) / 2, 0.40 * 359.9),
                    Stroke = Brushes.White,
                    StrokeThickness = 2.0,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                Canvas.SetLeft(outerArc, 0);
                Canvas.SetTop(outerArc, 0);
                host.Children.Add(outerArc);

                var innerOffset = (26 - 15) / 2.0;
                var innerTrack = new Ellipse
                {
                    Width = 15,
                    Height = 15,
                    Stroke = IslandColors.Brush(IslandColors.White(0.10)),
                    StrokeThickness = 1.6,
                    Visibility = Visibility.Collapsed,
                };
                Canvas.SetLeft(innerTrack, innerOffset);
                Canvas.SetTop(innerTrack, innerOffset);
                host.Children.Add(innerTrack);

                var innerArc = new System.Windows.Shapes.Path
                {
                    Data = Charts.RingMeter.ArcGeometry(center, (15 - 1.6) / 2, 0.70 * 359.9),
                    Stroke = IslandColors.Brush(IslandColors.LiveTeal),
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                Canvas.SetLeft(innerArc, 0);
                Canvas.SetTop(innerArc, 0);
                host.Children.Add(innerArc);

                return host;
            }
            case ChartStyle.Bar:
            {
                var host = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Width = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var upper = new Grid { Height = 4 };
                upper.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(2),
                    Background = IslandColors.Brush(IslandColors.White(0.10)),
                });
                upper.Children.Add(new Border
                {
                    Width = 28 * 0.35,
                    CornerRadius = new CornerRadius(2),
                    Background = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
                var lower = new Grid { Height = 2.5, Margin = new Thickness(0, 2.5, 0, 0) };
                lower.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(1.25),
                    Background = IslandColors.Brush(IslandColors.White(0.04)),
                });
                lower.Children.Add(new Border
                {
                    Width = 28 * 0.70,
                    CornerRadius = new CornerRadius(1.25),
                    Background = IslandColors.Brush(IslandColors.LiveTeal),
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
                host.Children.Add(upper);
                host.Children.Add(lower);
                return host;
            }
            case ChartStyle.Stepped:
            {
                var host = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var upperRow = new StackPanel { Orientation = Orientation.Horizontal };
                for (var i = 0; i < 8; i++)
                {
                    upperRow.Children.Add(new Rectangle
                    {
                        Width = 2,
                        Height = 8,
                        RadiusX = 0.75,
                        RadiusY = 0.75,
                        Margin = new Thickness(0.75, 0, 0.75, 0),
                        Fill = i < 3 ? Brushes.White : IslandColors.Brush(IslandColors.White(0.10)),
                    });
                }
                var lowerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                for (var i = 0; i < 8; i++)
                {
                    lowerRow.Children.Add(new Rectangle
                    {
                        Width = 2,
                        Height = 2.5,
                        RadiusX = 0.5,
                        RadiusY = 0.5,
                        Margin = new Thickness(0.75, 0, 0.75, 0),
                        Fill = i < 5 ? IslandColors.Brush(IslandColors.LiveTeal) : IslandColors.Brush(IslandColors.White(0.04)),
                    });
                }
                host.Children.Add(upperRow);
                host.Children.Add(lowerRow);
                return host;
            }
            case ChartStyle.Numeric:
            {
                var host = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var text = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontFamily = IslandFonts.Mono,
                };
                text.Inlines.Add(new System.Windows.Documents.Run("35")
                {
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                });
                text.Inlines.Add(new System.Windows.Documents.Run("%")
                {
                    FontSize = 9.5,
                    Foreground = IslandColors.Brush(IslandColors.White(0.5)),
                });
                var upper = new Grid { Width = 26, Height = 2, Margin = new Thickness(0, 1.5, 0, 0) };
                upper.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(1),
                    Background = IslandColors.Brush(IslandColors.White(0.10)),
                });
                upper.Children.Add(new Border
                {
                    Width = 26 * 0.35,
                    CornerRadius = new CornerRadius(1),
                    Background = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
                var lower = new Grid { Width = 26, Height = 1.5, Margin = new Thickness(0, 1.5, 0, 0) };
                lower.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(0.75),
                    Background = IslandColors.Brush(IslandColors.White(0.04)),
                });
                lower.Children.Add(new Border
                {
                    Width = 26 * 0.70,
                    CornerRadius = new CornerRadius(0.75),
                    Background = IslandColors.Brush(IslandColors.LiveTeal),
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
                host.Children.Add(text);
                host.Children.Add(upper);
                host.Children.Add(lower);
                return host;
            }
            default:
            {
                // Unreachable — every ChartStyle has its own case above.
                return new Grid();
            }
        }
    }

    /// A filled pie wedge from 12 o'clock, clockwise by `fraction` of a turn.
    internal static Geometry PieSliceGeometry(double size, double fraction)
    {
        var center = new Point(size / 2, size / 2);
        var radius = size / 2;
        var start = new Point(center.X, center.Y - radius);
        var angle = fraction * 2 * Math.PI;
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = center, IsClosed = true };
        figure.Segments.Add(new LineSegment(start, false));
        figure.Segments.Add(new ArcSegment(
            end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, false));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}

/// The shared tile chrome for both style pickers — macOS StyleTile: selected
/// = white 0.12 fill + white 0.6 hairline + white 0.95 semibold label;
/// unselected = white 0.025 fill, invisible border, white 0.55 label; hover
/// lifts an unselected tile to 0.05 fill + 0.10 border.
internal static class StyleTileChrome
{
    public static void Paint(Border tile, TextBlock label, bool isOn)
    {
        tile.Tag = isOn;
        tile.Background = IslandColors.Brush(IslandColors.White(isOn ? 0.12 : 0.025));
        tile.BorderBrush = isOn ? IslandColors.Brush(IslandColors.White(0.6)) : Brushes.Transparent;
        label.Foreground = IslandColors.Brush(IslandColors.White(isOn ? 0.95 : 0.55));
        label.FontWeight = isOn ? FontWeights.SemiBold : FontWeights.Medium;
    }

    public static void AttachHover(Border tile)
    {
        // macOS StyleTile hover: an unselected tile lifts to 1.02 with a
        // soft drop and a faint border, easing out over ~0.12s.
        var scale = new ScaleTransform(1, 1);
        tile.RenderTransform = scale;
        tile.RenderTransformOrigin = new Point(0.5, 0.5);

        void Ease(double target)
        {
            var beat = new Duration(TimeSpan.FromMilliseconds(120));
            var ease = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(target, beat) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new System.Windows.Media.Animation.DoubleAnimation(target, beat) { EasingFunction = ease });
        }

        tile.MouseEnter += (_, _) =>
        {
            if (tile.Tag is true) return;
            tile.Background = IslandColors.Brush(IslandColors.White(0.05));
            tile.BorderBrush = IslandColors.Brush(IslandColors.White(0.10));
            Ease(1.02);
        };
        tile.MouseLeave += (_, _) =>
        {
            Ease(1.0);
            if (tile.Tag is true) return;
            tile.Background = IslandColors.Brush(IslandColors.White(0.025));
            tile.BorderBrush = Brushes.Transparent;
        };
    }
}

/// Cost display picker: USD / VALUE / TOKENS / TREND preview tiles, the
/// same white-only voice as the usage picker.
public sealed class CostStylePickerControl : Grid
{
    private readonly List<Border> _tiles = new();

    public event Action<CostStyle>? StyleSelected;

    public CostStylePickerControl(CostStyle selected)
    {
        var styles = Enum.GetValues<CostStyle>();
        for (var i = 0; i < styles.Length; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var i = 0; i < styles.Length; i++)
        {
            var style = styles[i];
            var tile = MakeTile(style);
            SetColumn(tile, i);
            _tiles.Add(tile);
            Children.Add(tile);
        }
        Select(selected);
    }

    private readonly List<TextBlock> _labels = new();

    private Border MakeTile(CostStyle style)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var preview = new Grid { Height = 40, Margin = new Thickness(0, 0, 0, 10) };
        preview.Children.Add(MakePreview(style));
        stack.Children.Add(preview);
        var label = new TextBlock
        {
            Text = ChipLabel(style),
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _labels.Add(label);
        stack.Children.Add(label);
        var tile = new Border
        {
            Child = stack,
            Height = 100,
            CornerRadius = new CornerRadius(9),
            Background = IslandColors.Brush(IslandColors.White(0.025)),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        tile.MouseLeftButtonUp += (_, args) =>
        {
            Select(style);
            StyleSelected?.Invoke(style);
            args.Handled = true;
        };
        StyleTileChrome.AttachHover(tile);
        return tile;
    }

    public void Select(CostStyle selected)
    {
        var styles = Enum.GetValues<CostStyle>();
        for (var i = 0; i < _tiles.Count; i++)
        {
            StyleTileChrome.Paint(_tiles[i], _labels[i], styles[i] == selected);
        }
    }

    /// Drawn previews, not typed-out strings — "◞◠◞◠" rendered as tofu-ish
    /// glyph soup on Windows fonts, and the value tile reads as a chart on
    /// macOS, not a dollar string.
    /// macOS CostStylePicker previews — active elements pure white (the
    /// value capsule and spark carry a soft white glow), suffixes at 0.5.
    private static UIElement MakePreview(CostStyle style)
    {
        switch (style)
        {
            case CostStyle.Dollar:
            {
                var text = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = IslandFonts.Mono,
                };
                text.Inlines.Add(new System.Windows.Documents.Run("$")
                {
                    FontSize = 11.5,
                    Foreground = IslandColors.Brush(IslandColors.White(0.5)),
                });
                text.Inlines.Add(new System.Windows.Documents.Run("87")
                {
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                });
                return text;
            }
            case CostStyle.Multi:
            {
                // The value view sketch: a dim short capsule beside a tall
                // white one with a soft glow.
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 8),
                };
                row.Children.Add(new Border
                {
                    Width = 8,
                    Height = 6,
                    CornerRadius = new CornerRadius(4),
                    Background = IslandColors.Brush(IslandColors.White(0.20)),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 4, 0),
                });
                row.Children.Add(new Border
                {
                    Width = 8,
                    Height = 18,
                    CornerRadius = new CornerRadius(4),
                    Background = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        ShadowDepth = 0,
                        BlurRadius = 6,
                        Color = Colors.White,
                        Opacity = 0.6,
                    },
                });
                return row;
            }
            case CostStyle.Tokens:
            {
                var text = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = IslandFonts.Mono,
                };
                text.Inlines.Add(new System.Windows.Documents.Run("2.4")
                {
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                });
                text.Inlines.Add(new System.Windows.Documents.Run("M")
                {
                    FontSize = 11,
                    Foreground = IslandColors.Brush(IslandColors.White(0.5)),
                });
                return text;
            }
            case CostStyle.Trend:
            default:
            {
                // The macOS spark path, normalized to a 32x16 box, white
                // with a soft white glow.
                var line = new Polyline
                {
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        ShadowDepth = 0,
                        BlurRadius = 4,
                        Color = Colors.White,
                        Opacity = 0.6,
                    },
                };
                var points = new (double X, double Y)[]
                {
                    (0.00, 0.92), (0.16, 0.78), (0.34, 0.65), (0.50, 0.50),
                    (0.69, 0.38), (0.84, 0.22), (1.00, 0.10),
                };
                foreach (var (x, y) in points)
                {
                    line.Points.Add(new Point(x * 32, y * 16));
                }
                return line;
            }
        }
    }

    private static string ChipLabel(CostStyle style) => Localization.L10n.Tr(style switch
    {
        CostStyle.Dollar => "USD",
        CostStyle.Multi => "Value",
        CostStyle.Tokens => "TOKEN",
        CostStyle.Trend => "Trend",
        _ => "USD",
    });
}
