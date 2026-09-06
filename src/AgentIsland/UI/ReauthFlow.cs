using AgentIsland.Core;
using AgentIsland.Core.Usage;

namespace AgentIsland.UI;

/// One-click re-authentication. Claude is browser-only: the PKCE loopback flow
/// needs no CLI, so there is nothing here that can be "not found" and no
/// dialog to raise — a failure lands on UsageStore.ClaudeReauthFailureCaption
/// and the Settings row offers the paste-code fallback from there. Codex keeps
/// the visible-terminal flow, and its dialog's Retry runs the whole flow again
/// so a mid-update CLI swap never dead-ends the user.
public static class ReauthFlow
{
    public static void Run(TriggerTool tool, IUsageStore? usageStore = null)
    {
        var usage = usageStore ?? (App.Instance?.Services?.GetService(typeof(IUsageStore)) as IUsageStore);
        if (tool == TriggerTool.Claude)
        {
            usage?.ReauthenticateClaude();
            return;
        }
        if (usage?.ReauthenticateCodex() == true) return;
        ShowCodexCliMissing(usage);
    }

    private static void ShowCodexCliMissing(IUsageStore? usageStore) =>
        IslandDialog.Show(
            TriggerTool.Codex,
            AgentIsland.UI.Localization.L10n.Tr("Re-authenticate"),
            AgentIsland.UI.Localization.L10n.Tr("Codex CLI not found. Log in from a terminal with: codex login"),
            primaryLabel: AgentIsland.UI.Localization.L10n.Tr("Retry"),
            primaryAction: () => Run(TriggerTool.Codex, usageStore),
            secondaryLabel: AgentIsland.UI.Localization.L10n.Tr("I know"));
}
