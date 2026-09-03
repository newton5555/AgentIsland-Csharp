using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI.Report;

/// A real month calendar in a dark popover — click any day and the report
/// window re-anchors to it (macOS: graphical DatePicker; WPF's stock
/// Calendar is a light-theme control that would sit on the dark card like
/// a sticker, so the grid is drawn by hand). Days outside
/// [earliest scanned day, today] are dimmed and inert.
public sealed class ReportCalendarPopup : Popup
{
    private readonly DateTime _minDay;
    private readonly DateTime _maxDay;
    private readonly DateTime? _selectedDate;
    private readonly Action<DateTime> _picked;
    private DateTime _visibleMonth;

    public ReportCalendarPopup(DateTime? earliestDataDay, Action<DateTime> picked, DateTime? selectedDate = null)
    {
        _minDay = (earliestDataDay ?? DateTime.Today).Date;
        _maxDay = DateTime.Today;
        _selectedDate = selectedDate?.Date;
        _picked = picked;

        if (_selectedDate is { } sel && sel >= _minDay && sel <= _maxDay)
        {
            _visibleMonth = new DateTime(sel.Year, sel.Month, 1);
        }
        else
        {
            _visibleMonth = new DateTime(_maxDay.Year, _maxDay.Month, 1);
        }

        StaysOpen = false;
        AllowsTransparency = true;
        Placement = PlacementMode.Bottom;
        VerticalOffset = 6;
        PopupAnimation = PopupAnimation.Fade;
        Child = Build();
    }

    private FrameworkElement Build()
    {
        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        var host = new Border
        {
            Background = IslandColors.Brush(Color.FromRgb(0x13, 0x16, 0x1C)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.14)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 6,
                Direction = 270,
                BlurRadius = 24,
                Color = Colors.Black,
                Opacity = 0.45,
            },
            Child = body,
            Width = 264,
        };

        var header = new Grid { Margin = new Thickness(2, 2, 2, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            FontFamily = IslandFonts.Ui,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.95)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        var grid = new UniformGrid { Columns = 7 };

        Border? backArrow = null;
        Border? forwardArrow = null;

        void Refresh()
        {
            title.Text = Localization.L10n.IsChinese
                ? _visibleMonth.ToString("yyyy年M月")
                : _visibleMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

            var canGoBack = _visibleMonth.AddMonths(-1) >= new DateTime(_minDay.Year, _minDay.Month, 1);
            var canGoForward = _visibleMonth < new DateTime(_maxDay.Year, _maxDay.Month, 1);

            if (backArrow is not null) SetArrowState(backArrow, canGoBack);
            if (forwardArrow is not null) SetArrowState(forwardArrow, canGoForward);

            FillDays(grid);
        }

        backArrow = MonthArrow("", () =>
        {
            var prev = _visibleMonth.AddMonths(-1);
            if (prev >= new DateTime(_minDay.Year, _minDay.Month, 1))
            {
                _visibleMonth = prev;
                Refresh();
            }
        });
        Grid.SetColumn(backArrow, 0);

        forwardArrow = MonthArrow("", () =>
        {
            var next = _visibleMonth.AddMonths(1);
            if (next <= new DateTime(_maxDay.Year, _maxDay.Month, 1))
            {
                _visibleMonth = next;
                Refresh();
            }
        });
        Grid.SetColumn(forwardArrow, 2);

        header.Children.Add(backArrow);
        header.Children.Add(title);
        header.Children.Add(forwardArrow);
        body.Children.Add(header);

        var zh = Localization.L10n.IsChinese;
        var weekdayLetters = zh
            ? new[] { "日", "一", "二", "三", "四", "五", "六" }
            : new[] { "S", "M", "T", "W", "T", "F", "S" };
        var letterRow = new UniformGrid { Columns = 7, Margin = new Thickness(0, 2, 0, 6) };
        foreach (var letter in weekdayLetters)
        {
            letterRow.Children.Add(new TextBlock
            {
                Text = letter,
                FontFamily = IslandFonts.Ui,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = IslandColors.Brush(IslandColors.White(0.42)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        body.Children.Add(letterRow);
        body.Children.Add(grid);

        // Footer: "回到今天" / "Jump to Today" shortcut
        var footer = new Border
        {
            Margin = new Thickness(0, 8, 0, 2),
            Padding = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.08)),
        };

        var todayBtn = new Border
        {
            Background = IslandColors.Brush(IslandColors.White(0.05)),
            BorderBrush = IslandColors.Brush(IslandColors.White(0.10)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 4, 12, 4),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = Localization.L10n.IsChinese ? "回到今天" : "Jump to Today",
                FontFamily = IslandFonts.Ui,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = IslandColors.Brush(IslandColors.White(0.85)),
            },
        };
        todayBtn.MouseEnter += (_, _) =>
        {
            todayBtn.Background = IslandColors.Brush(IslandColors.White(0.12));
            todayBtn.BorderBrush = IslandColors.Brush(IslandColors.White(0.18));
        };
        todayBtn.MouseLeave += (_, _) =>
        {
            todayBtn.Background = IslandColors.Brush(IslandColors.White(0.05));
            todayBtn.BorderBrush = IslandColors.Brush(IslandColors.White(0.10));
        };
        todayBtn.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            IsOpen = false;
            _picked(DateTime.Today);
        };
        footer.Child = todayBtn;
        body.Children.Add(footer);

        Refresh();
        return host;
    }

    private void FillDays(UniformGrid grid)
    {
        grid.Children.Clear();
        var first = _visibleMonth;
        var lead = (int)first.DayOfWeek; // Sunday-start
        for (var i = 0; i < lead; i++)
        {
            grid.Children.Add(new Border { Height = 28 });
        }
        var daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);
        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(first.Year, first.Month, day);
            var inRange = date >= _minDay && date <= _maxDay;
            var isToday = date == _maxDay;
            var isSelected = _selectedDate.HasValue && date == _selectedDate.Value;

            var label = new TextBlock
            {
                Text = day.ToString(CultureInfo.InvariantCulture),
                FontFamily = IslandFonts.Mono,
                FontSize = 11,
                FontWeight = (isSelected || isToday) ? FontWeights.Bold : FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var cell = new Border
            {
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(1),
                Child = label,
                Cursor = inRange ? Cursors.Hand : Cursors.Arrow,
            };

            if (isSelected)
            {
                // Active/selected date: highlighted with brand teal
                cell.Background = IslandColors.Brush(Color.FromRgb(0x3D, 0xD6, 0x8C));
                label.Foreground = IslandColors.Brush(Color.FromRgb(0x0D, 0x0F, 0x13));
            }
            else if (isToday)
            {
                // Today: framed with brand color border
                cell.Background = IslandColors.Brush(IslandColors.White(0.08));
                cell.BorderBrush = IslandColors.Brush(Color.FromRgb(0x3D, 0xD6, 0x8C));
                cell.BorderThickness = new Thickness(1);
                label.Foreground = IslandColors.Brush(Color.FromRgb(0x3D, 0xD6, 0x8C));
            }
            else
            {
                cell.Background = Brushes.Transparent;
                label.Foreground = IslandColors.Brush(IslandColors.White(inRange ? 0.90 : 0.20));
            }

            if (inRange)
            {
                cell.MouseEnter += (_, _) =>
                {
                    if (!isSelected)
                    {
                        cell.Background = IslandColors.Brush(IslandColors.White(0.15));
                    }
                };
                cell.MouseLeave += (_, _) =>
                {
                    if (isSelected)
                    {
                        cell.Background = IslandColors.Brush(Color.FromRgb(0x3D, 0xD6, 0x8C));
                    }
                    else if (isToday)
                    {
                        cell.Background = IslandColors.Brush(IslandColors.White(0.08));
                    }
                    else
                    {
                        cell.Background = Brushes.Transparent;
                    }
                };
                cell.MouseLeftButtonUp += (_, args) =>
                {
                    args.Handled = true;
                    IsOpen = false;
                    _picked(date);
                };
            }
            grid.Children.Add(cell);
        }
    }

    private static void SetArrowState(Border arrow, bool enabled)
    {
        arrow.Opacity = enabled ? 1.0 : 0.25;
        arrow.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
        arrow.IsHitTestVisible = enabled;
    }

    private static Border MonthArrow(string glyph, Action action)
    {
        var face = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = IslandColors.Brush(IslandColors.White(0.08)),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 9.5,
                Foreground = IslandColors.Brush(IslandColors.White(0.85)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        face.MouseEnter += (_, _) => face.Background = IslandColors.Brush(IslandColors.White(0.15));
        face.MouseLeave += (_, _) => face.Background = IslandColors.Brush(IslandColors.White(0.08));
        face.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        return face;
    }
}
