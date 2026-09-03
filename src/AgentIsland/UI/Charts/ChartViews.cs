using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentIsland.UI.Theme;
using AgentIsland.Usage;

namespace AgentIsland.UI.Charts;

/// Shared head for the "label + big number" charts: window label on the
/// left (lowercase, 55% white), percent value on the right in mono
/// semibold tinted by urgency, with a quiet "%" suffix.
public sealed class ChartHead : Grid
{
    private readonly TextBlock _label;
    private readonly TextBlock _value;
    private readonly TextBlock _suffix;

    public ChartHead()
    {
        _label = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _value = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _suffix = new TextBlock
        {
            Text = "%",
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.5)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(1, 0, 0, 1),
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_value);
        right.Children.Add(_suffix);
        Children.Add(_label);
        Children.Add(right);
    }

    public void Update(string label, double value)
    {
        _label.Text = label.ToLowerInvariant();
        _value.Text = $"{(int)value}";
        _value.Foreground = IslandColors.Brush(IslandColors.Urgency(value / 100));
    }
}

public sealed class ChartFoot : TextBlock
{
    public ChartFoot()
    {
        FontFamily = IslandFonts.Mono;
        FontSize = 10;
        Foreground = IslandColors.Brush(IslandColors.White(0.4));
        TextTrimming = TextTrimming.CharacterEllipsis;
    }
}

/// 30-cell segmented meter with dual tracks:
/// - Upper row: Quota cells in provider/urgency color.
/// - Lower row: Remaining reset time micro-segments in glowing LiveTeal green.
public sealed class SteppedMeter : Grid
{
    private const double TickPitch = 4.0;
    private const int MinSegments = 10;

    private Rectangle[] _cells = Array.Empty<Rectangle>();
    private Rectangle[] _timeCells = Array.Empty<Rectangle>();
    private readonly Color _color;
    private double _lastFilled = -1;
    private double _lastValue;
    private double _lastTimeFilled = -1;
    private double _lastTimeRatio;
    private bool _layoutRefreshQueued;

    public SteppedMeter(Color color)
    {
        _color = color;
        Height = 18;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(10, GridUnitType.Pixel) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(2.5, GridUnitType.Pixel) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(3.5, GridUnitType.Pixel) });
        SizeChanged += (_, e) => Rebuild(e.NewSize.Width);
        Loaded += (_, _) => QueueLayoutRefresh();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) QueueLayoutRefresh();
        };
    }

    private void QueueLayoutRefresh()
    {
        if (!IsLoaded || _layoutRefreshQueued) return;

        _layoutRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _layoutRefreshQueued = false;
            if (!IsLoaded || !IsVisible) return;

            // Visibility/column changes can happen immediately before WPF's
            // next measure pass. Re-read the final width after that pass so
            // an update that arrived while the meter had no cells is painted.
            Rebuild(ActualWidth);
            RenderState();
        }));
    }

    private void Rebuild(double width)
    {
        var count = Math.Max(MinSegments, (int)(width / TickPitch));
        if (count == _cells.Length) return;
        Children.Clear();
        ColumnDefinitions.Clear();

        for (var i = 0; i < count; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        _cells = new Rectangle[count];
        _timeCells = new Rectangle[count];

        for (var i = 0; i < count; i++)
        {
            // Upper row: Quota
            var cell = new Rectangle
            {
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(i == 0 ? 0 : 1, 0, i == count - 1 ? 0 : 1, 0),
                Fill = IslandColors.Brush(IslandColors.White(0.10)),
            };
            SetRow(cell, 0);
            SetColumn(cell, i);
            _cells[i] = cell;
            Children.Add(cell);

            // Lower row: Reset time remaining
            var timeCell = new Rectangle
            {
                RadiusX = 1.0,
                RadiusY = 1.0,
                Margin = new Thickness(i == 0 ? 0 : 1, 0, i == count - 1 ? 0 : 1, 0),
                Fill = IslandColors.Brush(IslandColors.White(0.04)),
            };
            SetRow(timeCell, 2);
            SetColumn(timeCell, i);
            _timeCells[i] = timeCell;
            Children.Add(timeCell);
        }

        _lastFilled = -1;
        _lastTimeFilled = -1;
        var filledNow = Math.Floor(_lastValue / 100 * count);
        var timeFilledNow = Math.Floor(_lastTimeRatio * count);
        for (var i = 0; i < count; i++)
        {
            _cells[i].Fill = IslandColors.Brush(i < filledNow ? _color : IslandColors.White(0.10));
            _timeCells[i].Fill = IslandColors.Brush(i < timeFilledNow ? IslandColors.LiveTeal : IslandColors.White(0.04));
        }
        _lastFilled = filledNow;
        _lastTimeFilled = timeFilledNow;
    }

    public void Update(double value, WindowUsage? window = null)
    {
        // Keep both values before checking whether layout has created the
        // cells. UsagePage updates visibility and data in the same dispatcher
        // turn, so the first call can legitimately arrive with zero cells.
        _lastValue = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
        _lastTimeRatio = RemainingRatio(window);

        var segments = _cells.Length;
        if (segments == 0)
        {
            QueueLayoutRefresh();
            return;
        }

        RenderState();
    }

    private void RenderState()
    {
        var segments = _cells.Length;
        if (segments == 0) return;

        var filled = Math.Floor(_lastValue / 100 * segments);
        if (Math.Abs(filled - _lastFilled) >= 0.5 || _lastFilled < 0)
        {
            _lastFilled = filled;
            var stagger = 210.0 / segments;
            for (var i = 0; i < segments; i++)
            {
                var target = i < filled ? _color : IslandColors.White(0.10);
                var brush = new SolidColorBrush(((SolidColorBrush)_cells[i].Fill).Color);
                _cells[i].Fill = brush;
                var animation = new ColorAnimation(target, IslandAnimations.StrongEaseOutDuration)
                {
                    BeginTime = TimeSpan.FromMilliseconds(i * stagger),
                    EasingFunction = IslandAnimations.StrongEaseOut(),
                };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
            }
        }

        var timeFilled = Math.Floor(_lastTimeRatio * segments);
        if (Math.Abs(timeFilled - _lastTimeFilled) >= 0.5 || _lastTimeFilled < 0)
        {
            _lastTimeFilled = timeFilled;
            var stagger = 180.0 / segments;
            for (var i = 0; i < segments; i++)
            {
                var target = i < timeFilled ? IslandColors.LiveTeal : IslandColors.White(0.04);
                var brush = new SolidColorBrush(((SolidColorBrush)_timeCells[i].Fill).Color);
                _timeCells[i].Fill = brush;
                var animation = new ColorAnimation(target, IslandAnimations.StrongEaseOutDuration)
                {
                    BeginTime = TimeSpan.FromMilliseconds(i * stagger),
                    EasingFunction = IslandAnimations.StrongEaseOut(),
                };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
            }
        }
    }

    private static double RemainingRatio(WindowUsage? window)
    {
        double remainingRatio = 0;
        if (window is { ResetAt: { } resetAt } && resetAt > DateTimeOffset.Now)
        {
            double periodSeconds = window.PeriodSeconds is { } p && p > 0
                ? p
                : (window.IsLongPeriod ? 7 * 86400 : 5 * 3600);
            var remainingSeconds = (resetAt - DateTimeOffset.Now).TotalSeconds;
            remainingRatio = Math.Clamp(remainingSeconds / periodSeconds, 0, 1.0);
        }
        return remainingRatio;
    }
}

/// Dual capsule progress bar with quartile ticks:
/// - Upper capsule: Quota in brand/urgency color.
/// - Lower capsule: Remaining reset time in glowing LiveTeal green.
public sealed class CapsuleMeter : StackPanel
{
    private readonly Border _fill;
    private readonly Border _timeFill;
    private readonly Grid _trackGrid;
    private readonly Grid _timeTrackGrid;
    private double _value;
    private double _timeRatio;

    public CapsuleMeter(Color color)
    {
        Orientation = Orientation.Vertical;
        Height = 16;

        // 1. Quota Track & Fill
        _trackGrid = new Grid { Height = 4.5, VerticalAlignment = VerticalAlignment.Center };
        var track = new Border
        {
            Height = 4.5,
            CornerRadius = new CornerRadius(2.25),
            Background = IslandColors.Brush(IslandColors.White(0.08)),
        };
        _fill = new Border
        {
            Height = 4.5,
            CornerRadius = new CornerRadius(2.25),
            Background = IslandColors.Brush(color),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };
        _trackGrid.Children.Add(track);
        _trackGrid.Children.Add(_fill);

        for (var quartile = 1; quartile <= 3; quartile++)
        {
            var tick = new Rectangle
            {
                Width = 1,
                Height = 4.5,
                Fill = IslandColors.Brush(IslandColors.White(0.12)),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var p = quartile * 0.25;
            _trackGrid.SizeChanged += (_, _) => tick.Margin = new Thickness(_trackGrid.ActualWidth * p, 0, 0, 0);
            _trackGrid.Children.Add(tick);
        }

        // 2. Reset Time Track & Fill (Glowing LiveTeal Green)
        _timeTrackGrid = new Grid { Height = 2.5, Margin = new Thickness(0, 3.5, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var timeTrack = new Border
        {
            Height = 2.5,
            CornerRadius = new CornerRadius(1.25),
            Background = IslandColors.Brush(IslandColors.White(0.04)),
        };
        _timeFill = new Border
        {
            Height = 2.5,
            CornerRadius = new CornerRadius(1.25),
            Background = IslandColors.Brush(IslandColors.LiveTeal),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 3,
                Color = IslandColors.LiveTeal,
                Opacity = 0.45,
            },
        };
        _timeTrackGrid.Children.Add(timeTrack);
        _timeTrackGrid.Children.Add(_timeFill);

        Children.Add(_trackGrid);
        Children.Add(_timeTrackGrid);

        SizeChanged += (_, _) =>
        {
            ApplyWidth(animate: false);
            ApplyTimeWidth(animate: false);
        };
    }

    public void Update(double value, WindowUsage? window = null)
    {
        _value = value;
        ApplyWidth(animate: true);

        double remainingRatio = 0;
        if (window is { ResetAt: { } resetAt } && resetAt > DateTimeOffset.Now)
        {
            double periodSeconds = window.PeriodSeconds is { } p && p > 0
                ? p
                : (window.IsLongPeriod ? 7 * 86400 : 5 * 3600);
            var remainingSeconds = (resetAt - DateTimeOffset.Now).TotalSeconds;
            remainingRatio = Math.Clamp(remainingSeconds / periodSeconds, 0, 1.0);
        }
        _timeRatio = remainingRatio;
        ApplyTimeWidth(animate: true);
    }

    private void ApplyWidth(bool animate)
    {
        var target = Math.Max(0, ActualWidth * _value / 100);
        if (!animate)
        {
            _fill.BeginAnimation(WidthProperty, null);
            _fill.Width = target;
            return;
        }
        var animation = new DoubleAnimation(target, IslandAnimations.StrongEaseOutDuration)
        {
            EasingFunction = IslandAnimations.StrongEaseOut(),
        };
        _fill.BeginAnimation(WidthProperty, animation);
    }

    private void ApplyTimeWidth(bool animate)
    {
        var target = Math.Max(0, ActualWidth * _timeRatio);
        if (!animate)
        {
            _timeFill.BeginAnimation(WidthProperty, null);
            _timeFill.Width = target;
            return;
        }
        var animation = new DoubleAnimation(target, IslandAnimations.StrongEaseOutDuration)
        {
            EasingFunction = IslandAnimations.StrongEaseOut(),
        };
        _timeFill.BeginAnimation(WidthProperty, animation);
    }
}

/// Circular dual progress ring (Apple/macOS-style activity rings):
/// - Outer ring: Quota used / remaining percentage in brand / urgency color.
/// - Inner ring: Reset cycle countdown progress towards 100% reset.
/// The percent (18pt) and label are stacked beside it.
public sealed class RingMeter : Grid
{
    private readonly System.Windows.Shapes.Path _progress;
    private readonly System.Windows.Shapes.Path _resetProgress;
    private readonly System.Windows.Shapes.Ellipse _outerTrack;
    private readonly System.Windows.Shapes.Ellipse _innerTrack;
    private readonly TextBlock _value;
    private readonly TextBlock _label;

    private const double Diameter = 56;
    private const double Stroke = 3.5;
    private const double InnerDiameter = 38;
    private const double InnerStroke = 2.5;

    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(
        nameof(Sweep), typeof(double), typeof(RingMeter),
        new PropertyMetadata(0.0, (d, _) => ((RingMeter)d).Redraw()));

    public static readonly DependencyProperty InnerSweepProperty = DependencyProperty.Register(
        nameof(InnerSweep), typeof(double), typeof(RingMeter),
        new PropertyMetadata(0.0, (d, _) => ((RingMeter)d).RedrawInner()));

    public double Sweep
    {
        get => (double)GetValue(SweepProperty);
        set => SetValue(SweepProperty, value);
    }

    public double InnerSweep
    {
        get => (double)GetValue(InnerSweepProperty);
        set => SetValue(InnerSweepProperty, value);
    }

    public RingMeter(Color color)
    {
        Height = 64;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var host = new Canvas
        {
            Width = Diameter,
            Height = Diameter,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = false,
        };

        // 1. Outer Track (Background ring for quota)
        _outerTrack = new System.Windows.Shapes.Ellipse
        {
            Width = Diameter,
            Height = Diameter,
            Stroke = IslandColors.Brush(IslandColors.White(0.08)),
            StrokeThickness = Stroke,
        };
        Canvas.SetLeft(_outerTrack, 0);
        Canvas.SetTop(_outerTrack, 0);
        host.Children.Add(_outerTrack);

        // 2. Outer Progress Arc (Quota)
        _progress = new System.Windows.Shapes.Path
        {
            Stroke = IslandColors.Brush(color),
            StrokeThickness = Stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Canvas.SetLeft(_progress, 0);
        Canvas.SetTop(_progress, 0);
        host.Children.Add(_progress);

        // 3. Inner Track (Background ring for reset countdown - hidden for clean green arc)
        var innerOffset = (Diameter - InnerDiameter) / 2;
        _innerTrack = new System.Windows.Shapes.Ellipse
        {
            Width = InnerDiameter,
            Height = InnerDiameter,
            Stroke = IslandColors.Brush(IslandColors.White(0.04)),
            StrokeThickness = InnerStroke,
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetLeft(_innerTrack, innerOffset);
        Canvas.SetTop(_innerTrack, innerOffset);
        host.Children.Add(_innerTrack);

        // 4. Inner Progress Arc (Remaining time in glowing green)
        _resetProgress = new System.Windows.Shapes.Path
        {
            Stroke = IslandColors.Brush(IslandColors.LiveTeal),
            StrokeThickness = InnerStroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 4,
                Color = IslandColors.LiveTeal,
                Opacity = 0.45,
            },
        };
        Canvas.SetLeft(_resetProgress, 0);
        Canvas.SetTop(_resetProgress, 0);
        host.Children.Add(_resetProgress);

        SetColumn(host, 0);
        Children.Add(host);

        var beside = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal };
        _value = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        };
        valueRow.Children.Add(_value);
        valueRow.Children.Add(new TextBlock
        {
            Text = "%",
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.5)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(1, 0, 0, 1),
        });
        beside.Children.Add(valueRow);
        _label = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            Margin = new Thickness(0, 1, 0, 0),
        };
        beside.Children.Add(_label);
        SetColumn(beside, 1);
        Children.Add(beside);
    }

    public void Update(string label, double value, WindowUsage? window = null)
    {
        _label.Text = label.ToLowerInvariant();
        _value.Text = $"{(int)value}";
        _value.Foreground = IslandColors.Brush(IslandColors.Urgency(value / 100));
        var sweep = new DoubleAnimation(
            Math.Clamp(value, 0, 100) / 100 * 359.9,
            IslandAnimations.StrongEaseOutDuration)
        {
            EasingFunction = IslandAnimations.StrongEaseOut(),
        };
        BeginAnimation(SweepProperty, sweep);

        double remainingRatio = 0;
        if (window is { ResetAt: { } resetAt } && resetAt > DateTimeOffset.Now)
        {
            double periodSeconds = window.PeriodSeconds is { } p && p > 0
                ? p
                : (window.IsLongPeriod ? 7 * 86400 : 5 * 3600);
            var remainingSeconds = (resetAt - DateTimeOffset.Now).TotalSeconds;
            // Remaining time ratio: 100% when full window remaining, shrinks as reset approaches
            remainingRatio = Math.Clamp(remainingSeconds / periodSeconds, 0.02, 1.0);
        }

        var innerSweep = new DoubleAnimation(
            remainingRatio * 359.9,
            IslandAnimations.StrongEaseOutDuration)
        {
            EasingFunction = IslandAnimations.StrongEaseOut(),
        };
        BeginAnimation(InnerSweepProperty, innerSweep);
    }

    private void Redraw() =>
        _progress.Data = Sweep <= 0.1 ? null : ArcGeometry(new Point(Diameter / 2, Diameter / 2), (Diameter - Stroke) / 2, Sweep);

    private void RedrawInner() =>
        _resetProgress.Data = InnerSweep <= 0.1 ? null : ArcGeometry(new Point(Diameter / 2, Diameter / 2), (InnerDiameter - InnerStroke) / 2, InnerSweep);

    public static Geometry ArcGeometry(Point center, double radius, double sweepDegrees)
    {
        var start = new Point(center.X, center.Y - radius);
        var angle = sweepDegrees * Math.PI / 180;
        var end = new Point(
            center.X + radius * Math.Sin(angle),
            center.Y - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            end,
            new Size(radius, radius),
            0,
            sweepDegrees > 180,
            SweepDirection.Clockwise,
            true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    public static Geometry ArcGeometry(double diameter, double stroke, double sweepDegrees) =>
        ArcGeometry(new Point(diameter / 2, diameter / 2), (diameter - stroke) / 2, sweepDegrees);
}

/// Numbers-first style: oversized percent over dual thin glowing brand & reset meters,
/// then the window label (macOS numbers: 38pt hero, 3pt capsule + 2pt time track).
public sealed class NumericMeter : StackPanel
{
    private readonly TextBlock _value;
    private readonly TextBlock _label;
    private readonly Border _meterFill;
    private readonly Border _timeFill;
    private readonly Grid _meter;
    private readonly Grid _timeMeter;
    private double _fraction;
    private double _timeFraction;

    public NumericMeter(Color color)
    {
        Orientation = Orientation.Vertical;
        _value = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 38,
            FontWeight = FontWeights.SemiBold,
        };

        // 1. Quota Bar (Height 3)
        _meter = new Grid { Height = 3, Margin = new Thickness(1, 4, 8, 0) };
        _meter.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(1.5),
            Background = IslandColors.Brush(IslandColors.White(0.08)),
        });
        _meterFill = new Border
        {
            CornerRadius = new CornerRadius(1.5),
            Background = IslandColors.Brush(color),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 4,
                Color = color,
                Opacity = 0.7,
            },
        };
        _meter.Children.Add(_meterFill);

        // 2. Reset Time Bar (Height 2, Margin Top 2.5)
        _timeMeter = new Grid { Height = 2, Margin = new Thickness(1, 2.5, 8, 0) };
        _timeMeter.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(1),
            Background = IslandColors.Brush(IslandColors.White(0.04)),
        });
        _timeFill = new Border
        {
            CornerRadius = new CornerRadius(1),
            Background = IslandColors.Brush(IslandColors.LiveTeal),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 3,
                Color = IslandColors.LiveTeal,
                Opacity = 0.45,
            },
        };
        _timeMeter.Children.Add(_timeFill);

        _label = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = IslandColors.Brush(IslandColors.White(0.55)),
            Margin = new Thickness(1, 3, 0, 0),
        };
        Children.Add(_value);
        Children.Add(_meter);
        Children.Add(_timeMeter);
        Children.Add(_label);
        _meter.SizeChanged += (_, _) =>
        {
            ApplyMeter(animate: false);
            ApplyTimeMeter(animate: false);
        };
    }

    public void Update(string label, double value, WindowUsage? window = null)
    {
        _value.Inlines.Clear();
        _value.Inlines.Add(new System.Windows.Documents.Run($"{(int)value}")
        {
            Foreground = IslandColors.Brush(IslandColors.Urgency(value / 100)),
        });
        _value.Inlines.Add(new System.Windows.Documents.Run("%")
        {
            FontSize = 14,
            Foreground = IslandColors.Brush(IslandColors.White(0.5)),
            FontWeight = FontWeights.Medium,
        });
        _label.Text = label.ToLowerInvariant();
        _fraction = Math.Clamp(value / 100, 0, 1);
        ApplyMeter(animate: true);

        double remainingRatio = 0;
        if (window is { ResetAt: { } resetAt } && resetAt > DateTimeOffset.Now)
        {
            double periodSeconds = window.PeriodSeconds is { } p && p > 0
                ? p
                : (window.IsLongPeriod ? 7 * 86400 : 5 * 3600);
            var remainingSeconds = (resetAt - DateTimeOffset.Now).TotalSeconds;
            remainingRatio = Math.Clamp(remainingSeconds / periodSeconds, 0, 1.0);
        }
        _timeFraction = remainingRatio;
        ApplyTimeMeter(animate: true);
    }

    private void ApplyMeter(bool animate)
    {
        var target = Math.Max(0, _meter.ActualWidth * _fraction);
        if (!animate)
        {
            _meterFill.BeginAnimation(WidthProperty, null);
            _meterFill.Width = target;
            return;
        }
        var grow = new DoubleAnimation(target, IslandAnimations.StrongEaseOutDuration)
        {
            EasingFunction = IslandAnimations.StrongEaseOut(),
        };
        _meterFill.BeginAnimation(WidthProperty, grow);
    }

    private void ApplyTimeMeter(bool animate)
    {
        var target = Math.Max(0, _timeMeter.ActualWidth * _timeFraction);
        if (!animate)
        {
            _timeFill.BeginAnimation(WidthProperty, null);
            _timeFill.Width = target;
            return;
        }
        var animation = new DoubleAnimation(target, IslandAnimations.StrongEaseOutDuration)
        {
            EasingFunction = IslandAnimations.StrongEaseOut(),
        };
        _timeFill.BeginAnimation(WidthProperty, animation);
    }
}

/// Area line chart rising to the current percent, the macOS Spark style:
/// gradient area under a thin curve, quartile rules, a dotted "now"
/// threshold line, and an end cursor with a halo.
public sealed class SparkMeter : Canvas
{
    private const int Points = 36;
    private readonly double[] _wave = new double[Points];
    private readonly System.Windows.Shapes.Polyline _line;
    private readonly System.Windows.Shapes.Polygon _area;
    private readonly System.Windows.Shapes.Line _threshold;
    private readonly System.Windows.Shapes.Ellipse _cursorHalo;
    private readonly System.Windows.Shapes.Ellipse _cursorDot;
    private readonly System.Windows.Shapes.Rectangle[] _rules = new System.Windows.Shapes.Rectangle[3];
    private double _value;

    public SparkMeter(Color color, int seed)
    {
        Height = 24;
        // Deterministic per-seed wave so the curve is stable frame to frame.
        for (var i = 0; i < Points; i++)
        {
            _wave[i] = Math.Abs(Math.Sin(i * 0.82 + seed * 1.7) * 0.7 + Math.Sin(i * 0.31 + seed) * 0.3);
        }
        for (var q = 0; q < 3; q++)
        {
            _rules[q] = new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Fill = IslandColors.Brush(IslandColors.White(0.04)),
            };
            Children.Add(_rules[q]);
        }
        _area = new System.Windows.Shapes.Polygon
        {
            Fill = new LinearGradientBrush(
                IslandColors.Alpha(color, 0.28), IslandColors.Alpha(color, 0), 90),
        };
        Children.Add(_area);
        _line = new System.Windows.Shapes.Polyline
        {
            Stroke = IslandColors.Brush(color),
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Children.Add(_line);
        _threshold = new System.Windows.Shapes.Line
        {
            Stroke = IslandColors.Brush(IslandColors.White(0.18)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 3 },
        };
        Children.Add(_threshold);
        _cursorHalo = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = IslandColors.Brush(color, 0.25),
        };
        Children.Add(_cursorHalo);
        _cursorDot = new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = IslandColors.Brush(color),
        };
        Children.Add(_cursorDot);
        SizeChanged += (_, _) => Render();
    }

    public void Update(double value)
    {
        _value = value;
        Render();
    }

    private void Render()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        for (var q = 0; q < 3; q++)
        {
            _rules[q].Width = w;
            SetLeft(_rules[q], 0);
            SetTop(_rules[q], h * (q + 1) / 4.0);
        }

        var frac = Math.Clamp(_value / 100, 0, 1);
        double YFor(double level) => h - h * level * 0.92 - 1;

        _line.Points.Clear();
        _area.Points.Clear();
        var lastX = 0.0;
        var lastY = YFor(0);
        for (var i = 0; i < Points; i++)
        {
            // Curve climbs toward the current percent, textured by the wave.
            var progress = i / (double)(Points - 1);
            var level = frac * (0.30 + 0.70 * progress) * (0.72 + 0.28 * _wave[i]);
            var x = w * progress;
            var y = YFor(level);
            _line.Points.Add(new Point(x, y));
            _area.Points.Add(new Point(x, y));
            lastX = x;
            lastY = y;
        }
        _area.Points.Add(new Point(w, h));
        _area.Points.Add(new Point(0, h));

        var thresholdY = YFor(frac);
        _threshold.X1 = 0;
        _threshold.X2 = w;
        _threshold.Y1 = thresholdY;
        _threshold.Y2 = thresholdY;

        SetLeft(_cursorHalo, lastX - 4);
        SetTop(_cursorHalo, lastY - 4);
        SetLeft(_cursorDot, lastX - 2);
        SetTop(_cursorDot, lastY - 2);
    }
}

/// One usage window tile: head, meter (per style), reset caption. Locked
/// height so the panel size is identical regardless of the chosen style.
public sealed class ChartTile : StackPanel
{
    public const double TileHeight = 96;

    private readonly ChartHead _head = new();
    private readonly ChartFoot _foot = new();
    private readonly SteppedMeter _stepped;
    private readonly CapsuleMeter _capsule;
    private readonly RingMeter _ring;
    private readonly NumericMeter _numeric;
    private readonly string _labelKey;

    public ChartTile(Color color, string labelKey, int seed = 1)
    {
        _labelKey = labelKey;
        Orientation = Orientation.Vertical;
        Height = TileHeight;
        _stepped = new SteppedMeter(color) { Margin = new Thickness(0, 6, 0, 6) };
        _capsule = new CapsuleMeter(color) { Margin = new Thickness(0, 8, 0, 8) };
        _ring = new RingMeter(color) { Margin = new Thickness(0, 4, 0, 4) };
        _numeric = new NumericMeter(color) { Margin = new Thickness(0, 2, 0, 2) };
        Children.Add(_head);
        Children.Add(_stepped);
        Children.Add(_capsule);
        Children.Add(_ring);
        Children.Add(_numeric);
        Children.Add(_foot);
    }

    public void Update(WindowUsage window, ChartStyle style)
    {
        // 0-100; flips to "percent left" when the user prefers remaining.
        var value = Model.QuotaDisplayModeStore.Shared.DisplayValue(window.UsedPercent);
        var label = PeriodLabel(window, _labelKey);

        // Ring and Numeric render their own heads; the shared head serves
        // the three label+number styles.
        _head.Visibility = style is ChartStyle.Bar or ChartStyle.Stepped
            ? Visibility.Visible
            : Visibility.Collapsed;
        _stepped.Visibility = style == ChartStyle.Stepped ? Visibility.Visible : Visibility.Collapsed;
        _capsule.Visibility = style == ChartStyle.Bar ? Visibility.Visible : Visibility.Collapsed;
        _ring.Visibility = style == ChartStyle.Ring ? Visibility.Visible : Visibility.Collapsed;
        _numeric.Visibility = style == ChartStyle.Numeric ? Visibility.Visible : Visibility.Collapsed;

        switch (style)
        {
            case ChartStyle.Stepped:
                _head.Update(label, value);
                _stepped.Update(value, window);
                break;
            case ChartStyle.Bar:
                _head.Update(label, value);
                _capsule.Update(value, window);
                break;
            case ChartStyle.Ring:
                _ring.Update(label, value, window);
                break;
            case ChartStyle.Numeric:
                _numeric.Update(label, value, window);
                break;
        }
        _foot.Text = SubCaption(window, style);
    }

    /// Tiles label themselves from the window length the provider actually
    /// reports, so a hardcoded label is not assumed. Old caches carry
    /// no period; those fall back to the slot's historical label.
    internal static string PeriodLabel(WindowUsage window, string fallbackKey)
    {
        if (window.PeriodSeconds is not { } period || period <= 0)
        {
            return Localization.L10n.Tr(fallbackKey);
        }
        // A billing cycle is not a week. Cursor's included-usage pool runs ~30
        // days, and without this branch IsLongPeriod would print "week" over a
        // month's worth of quota.
        if (period >= 20 * 86400) return Localization.L10n.Tr("30d");
        if (window.IsLongPeriod) return Localization.L10n.Tr("week");
        var hours = Math.Max(1, (int)Math.Round(period / 3600));
        return $"{hours}h";
    }

    /// "no data" is the internal sentinel for "API returned null for this
    /// window" — hide it so the tile reads as a passive window-context cue.
    /// Real errors surface before reset countdowns because preserved stale
    /// values may carry an old resetAt that would otherwise render as "0s".
    private static string SubCaption(WindowUsage window, ChartStyle style)
    {
        if (window.Error is { } error && error != "no data")
        {
            // The inline re-auth button below the tiles carries the
            // remediation; repeating it in both captions reads twice.
            if (ClaudeCredentials.IsAuthRecoverableError(error))
            {
                return "";
            }
            return Localization.ErrorDisplay.Localize(error);
        }
        if (window.ResetAt is { } resetAt)
        {
            var delta = resetAt - DateTimeOffset.Now;
            if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
            var compact = Core.Formatting.CompactDuration(delta);
            return style == ChartStyle.Numeric
                ? "↻ " + compact
                : Localization.L10n.TrFormat("resets in {0}", compact);
        }
        return "";
    }
}

public static class IslandFonts
{
    // Explicit CJK fallback: without it WPF walks its composite font for
    // missing glyphs and Chinese lands on a serif face that clashes with
    // the Latin sans (the macOS build gets PingFang for free from SF Pro).
    public static readonly FontFamily Ui = new(
        "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

    public static readonly FontFamily Mono = new(
        "Cascadia Mono, Consolas, Microsoft YaHei UI, Microsoft YaHei");
}
