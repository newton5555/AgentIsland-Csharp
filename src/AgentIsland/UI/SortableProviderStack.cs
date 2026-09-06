using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentIsland.Core;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

public class SortableProviderStack : StackPanel
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
    private readonly AgentIsland.Backend.Settings.IProviderVisibilityStore _visibilityStore;

    public SortableProviderStack() : this(null) { }

    public SortableProviderStack(AgentIsland.Backend.Settings.IProviderVisibilityStore? visibilityStore = null)
    {
        _visibilityStore = visibilityStore
            ?? (App.Instance?.Services?.GetService(typeof(AgentIsland.Backend.Settings.IProviderVisibilityStore)) as AgentIsland.Backend.Settings.IProviderVisibilityStore)
            ?? new AgentIsland.Backend.Settings.ProviderVisibilityStore();

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
            _visibilityStore.MoveProvider(oldIndex, newIndex);
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
