namespace AgentIsland.Core.Agents;

/// Names for the legacy TriggerTool values that are still used by the
/// migrated runtime. Keeping this mapping in Core avoids a Core -> WPF
/// dependency while the old UI identity service is migrated in a later step.
public static class AgentNames
{
    public static string DisplayName(TriggerTool tool) => tool switch
    {
        TriggerTool.Claude => "Claude",
        TriggerTool.Codex => "Codex",
        TriggerTool.Antigravity => "Antigravity",
        TriggerTool.Grok => "Grok",
        TriggerTool.Cursor => "Cursor",
        _ => tool.ToString(),
    };

    public static string? CliName(TriggerTool tool) => tool switch
    {
        TriggerTool.Claude => "claude",
        TriggerTool.Codex => "codex",
        TriggerTool.Antigravity => "agy",
        TriggerTool.Grok => "grok",
        TriggerTool.Cursor => null,
        _ => null,
    };
}
