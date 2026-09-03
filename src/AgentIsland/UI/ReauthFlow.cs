using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.UI;

/// One-click re-authentication. Claude is browser-only: the PKCE loopback flow
/// needs no CLI, so there is nothing here that can be "not found" and no
/// dialog to raise — a failure lands on UsageStore.ClaudeReauthFailureCaption
/// and the Settings row offers the paste-code fallback from there. Codex keeps
/// the visible-terminal flow, and its dialog's Retry runs the whole flow again
/// so a mid-update CLI swap never dead-ends the user.
public static class ReauthFlow
{
    public static void Run(TriggerTool tool)
    {
        if (tool == TriggerTool.Claude)
        {
            UsageStore.Shared.ReauthenticateClaude();
            return;
        }
        if (UsageStore.Shared.ReauthenticateCodex()) return;
        ShowCodexCliMissing();
    }

    private static void ShowCodexCliMissing() =>
        IslandDialog.Show(
            TriggerTool.Codex,
            Localization.L10n.Tr("Re-authenticate"),
            Localization.L10n.Tr("Codex CLI not found. Log in from a terminal with: codex login"),
            primaryLabel: Localization.L10n.Tr("Retry"),
            primaryAction: () => Run(TriggerTool.Codex),
            secondaryLabel: Localization.L10n.Tr("I know"));
}
