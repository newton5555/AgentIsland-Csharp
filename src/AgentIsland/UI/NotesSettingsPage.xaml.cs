using System.Windows.Controls;
using AgentIsland.UI.Localization;

namespace AgentIsland.UI;

public sealed partial class NotesSettingsPage : UserControl
{
    public NotesSettingsPage()
    {
        InitializeComponent();

        ViewButton.Label = L10n.Tr("View");
        ViewButton.Clicked += WhatsNewWindow.Open;

        GuideButton.Label = L10n.Tr("Open");
        GuideButton.Clicked += WhatsNewWindow.OpenGuide;

        ChangelogButton.Label = L10n.Tr("Open");
        ChangelogButton.Clicked += () => OpenUrl("https://github.com/newton5555/AgentIsland-Csharp/releases");
    }

    internal static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }
}
