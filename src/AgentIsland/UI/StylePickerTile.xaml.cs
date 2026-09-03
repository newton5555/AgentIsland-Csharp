using System.Windows;
using System.Windows.Controls;

namespace AgentIsland.UI;

public partial class StylePickerTile : UserControl
{
    public event Action? Clicked;

    public StylePickerTile()
    {
        InitializeComponent();
        StyleTileChrome.AttachHover(TileBorder);
        TileBorder.MouseLeftButtonUp += (_, args) =>
        {
            Clicked?.Invoke();
            args.Handled = true;
        };
    }

    public void PerformClick() => Clicked?.Invoke();

    public Border BorderElement => TileBorder;
    public TextBlock LabelElement => TileLabel;
    public Grid PreviewContainer => PreviewHost;

    public string Text
    {
        get => TileLabel.Text;
        set => TileLabel.Text = value;
    }

    public UIElement? Preview
    {
        get => PreviewSlot.Content as UIElement;
        set => PreviewSlot.Content = value;
    }

    public void SetSelected(bool isSelected)
    {
        StyleTileChrome.Paint(TileBorder, TileLabel, isSelected);
    }
}
