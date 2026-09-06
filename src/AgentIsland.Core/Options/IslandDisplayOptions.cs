namespace AgentIsland.Core.Options;

public enum IslandSpacingMode
{
    NotchStyle,
    Compact,
}

public sealed class IslandDisplayOptions
{
    public const string SectionName = "IslandDisplay";

    public IslandSpacingMode SpacingMode { get; set; } = IslandSpacingMode.NotchStyle;
    public double SpacingScale { get; set; } = 1.0;
    public bool AlwaysShowUsage { get; set; } = false;
    public string GlowColor { get; set; } = "teal";
    public bool ShowCostPanelPage { get; set; } = false;
}
