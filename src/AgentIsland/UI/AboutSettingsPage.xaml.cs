using System.Windows.Controls;
using AgentIsland.UI.Localization;

namespace AgentIsland.UI;

public sealed partial class AboutSettingsPage : UserControl
{
    public AboutSettingsPage()
    {
        InitializeComponent();

        Paragraph1.Text = L10n.Tr("Agents are part of every builder's day now — Claude Code, Codex, Gemini, Grok, Cursor… and whatever ships next month. They run, you wait, and nobody tells you when it is your turn again");
        Paragraph2.Text = L10n.Tr("Agent Island 2.0 puts them all on one island. Who is working, whose turn it is, how much quota is left, what it cost — one glance at the notch, no window switching, no terminal tabs to hunt through");
        Paragraph3.Text = L10n.Tr("Every number is computed on your own computer — Mac or Windows — from logs the agents already write. No account, no telemetry, nothing uploaded");
        Paragraph4.Text = L10n.Tr("If Agent Island helps you, a star on GitHub and a share with a friend are the two things that keep it going");

        MadeByLabel.Text = L10n.Tr("Made by");
        SponsorLabel.Text = L10n.Tr("Sponsor");
        SponsorValue.Text = L10n.Tr("Star on GitHub");

        MadeByRow.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            OpenUrl("https://tristan.media");
        };

        SponsorRow.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            OpenUrl("https://github.com/tristan666666/agent-island");
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
