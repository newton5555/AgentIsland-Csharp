using AgentIsland.Avalonia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AgentIsland.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow() : this(IslandViewModel.CreateMock())
    {
    }

    public MainWindow(IslandViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
    }

    /// <summary>
    /// Single binding entry point for the island visual state.
    /// Can be swapped at runtime (e.g. when connecting P1 Runtime).
    /// </summary>
    public IslandViewModel? ViewModel
    {
        get => DataContext as IslandViewModel;
        set => DataContext = value;
    }

    private void OnIslandPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Avoid initiating drag when clicking inside interactive controls like buttons
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>(includeSelf: true) != null)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnToggleTopmostClick(object? sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
    }

    private void OnCenterWindowClick(object? sender, RoutedEventArgs e)
    {
        var screens = Screens;
        var screen = screens.ScreenFromWindow(this) ?? screens.Primary;
        if (screen != null)
        {
            var workingArea = screen.WorkingArea;
            var x = workingArea.X + (workingArea.Width - (int)Bounds.Width) / 2;
            var y = workingArea.Y + (workingArea.Height - (int)Bounds.Height) / 2;
            Position = new PixelPoint(x, y);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
