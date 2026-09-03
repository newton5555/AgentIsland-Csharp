using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AgentIsland.UI;

/// Plain pill action button ("Refresh", "Check").
public sealed partial class PillButtonControl : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(PillButtonControl),
            new PropertyMetadata(string.Empty, (d, e) =>
            {
                if (d is PillButtonControl control)
                {
                    control.LabelBlock.Text = (string)e.NewValue;
                }
            }));

    public event Action? Clicked;

    public PillButtonControl() : this(string.Empty)
    {
    }

    public PillButtonControl(string label)
    {
        InitializeComponent();
        Label = label;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Clicked?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}
