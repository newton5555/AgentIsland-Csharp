using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

/// Dotted-underline external link ("GitHub ↗").
public sealed partial class DottedLink : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(DottedLink),
            new PropertyMetadata(string.Empty, (d, _) => ((DottedLink)d).UpdateText()));

    public static readonly DependencyProperty UrlProperty =
        DependencyProperty.Register(
            nameof(Url),
            typeof(string),
            typeof(DottedLink),
            new PropertyMetadata(string.Empty));

    public DottedLink() : this(string.Empty, string.Empty)
    {
    }

    public DottedLink(string title, string url)
    {
        InitializeComponent();
        Title = title;
        Url = url;

        MouseEnter += (_, _) =>
        {
            TitleBlock.Foreground = IslandColors.Brush(IslandColors.White(0.92));
            ArrowBlock.Foreground = IslandColors.Brush(IslandColors.White(0.6));
        };
        MouseLeave += (_, _) =>
        {
            TitleBlock.Foreground = IslandColors.Brush(IslandColors.White(0.55));
            ArrowBlock.Foreground = IslandColors.Brush(IslandColors.White(0.3));
        };
        MouseLeftButtonUp += (_, args) =>
        {
            if (!string.IsNullOrEmpty(Url))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Url,
                        UseShellExecute = true,
                    });
                }
                catch
                {
                }
            }
            args.Handled = true;
        };
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Url
    {
        get => (string)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    private void UpdateText()
    {
        if (TitleBlock != null)
        {
            TitleBlock.Text = L10n.Tr(Title ?? string.Empty);
        }
    }
}
