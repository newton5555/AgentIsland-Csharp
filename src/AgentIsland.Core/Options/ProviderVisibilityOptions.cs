namespace AgentIsland.Core.Options;

public sealed class ProviderVisibilityOptions
{
    public const string SectionName = "ProviderVisibility";

    public bool ClaudeVisible { get; set; } = true;
    public bool CodexVisible { get; set; } = true;
    public bool DeepSeekVisible { get; set; } = true;
    public bool GrokVisible { get; set; } = true;
    public bool CursorVisible { get; set; } = true;
}
