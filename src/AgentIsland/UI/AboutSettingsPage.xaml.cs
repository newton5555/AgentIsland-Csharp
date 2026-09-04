using System.Windows.Controls;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

public sealed partial class AboutSettingsPage : UserControl
{
    public AboutSettingsPage()
    {
        InitializeComponent();

        SubtitleText.Text = L10n.Tr("Windows native edition · Independent implementation");

        ForkRepoLabel.Text = L10n.Tr("This Repo (GitHub)");
        UpstreamRepoLabel.Text = L10n.Tr("Upstream Repo");
        InspirationRepoLabel.Text = L10n.Tr("Inspiration (Codex)");

        ForkRepoRow.MouseEnter += (_, _) => ForkRepoRow.Background = IslandColors.Brush(IslandColors.White(0.12));
        ForkRepoRow.MouseLeave += (_, _) => ForkRepoRow.Background = IslandColors.Brush(IslandColors.White(0.05));
        ForkRepoRow.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            OpenUrl("https://github.com/newton5555/AgentIsland-Csharp");
        };

        UpstreamRepoRow.MouseEnter += (_, _) => UpstreamRepoRow.Background = IslandColors.Brush(IslandColors.White(0.12));
        UpstreamRepoRow.MouseLeave += (_, _) => UpstreamRepoRow.Background = IslandColors.Brush(IslandColors.White(0.05));
        UpstreamRepoRow.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            OpenUrl("https://github.com/tristan666666/agent-island");
        };

        InspirationRepoRow.MouseEnter += (_, _) => InspirationRepoRow.Background = IslandColors.Brush(IslandColors.White(0.12));
        InspirationRepoRow.MouseLeave += (_, _) => InspirationRepoRow.Background = IslandColors.Brush(IslandColors.White(0.05));
        InspirationRepoRow.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            OpenUrl("https://github.com/ericjypark/codex-island");
        };
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
