using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AgentIsland.UI;

/// Pill-shaped segmented control (Refresh interval, Token counting, Mac
/// type pickers).
public sealed partial class Segmented : UserControl
{
    public sealed class Item : INotifyPropertyChanged
    {
        private bool _isSelected;

        public int Index { get; init; }
        public string Label { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<Item> _items = new();
    private int _selected;

    public event Action<int>? SelectionChanged;

    public Segmented() : this(Array.Empty<string>(), 0)
    {
    }

    public Segmented(IReadOnlyList<string> labels, int selected)
    {
        InitializeComponent();
        SetLabels(labels, selected);
    }

    public int SelectedIndex => _selected;

    public void SetLabels(IReadOnlyList<string> labels, int selected)
    {
        _items.Clear();
        for (var i = 0; i < labels.Count; i++)
        {
            _items.Add(new Item
            {
                Index = i,
                Label = labels[i],
                IsSelected = (i == selected),
            });
        }
        ItemsHost.ItemsSource = null;
        ItemsHost.ItemsSource = _items;
        Select(selected);
    }

    /// macOS SegmentedControl: near-white thumb, BLACK selected label,
    /// ghost-white unselected labels on a faint capsule track.
    public void Select(int index)
    {
        if (_items.Count == 0)
        {
            _selected = 0;
            return;
        }
        _selected = Math.Clamp(index, 0, _items.Count - 1);
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].IsSelected = (i == _selected);
        }
    }

    private void OnCellMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Item item })
        {
            Select(item.Index);
            SelectionChanged?.Invoke(item.Index);
            e.Handled = true;
        }
    }
}
