namespace AgentIsland.Core.Options;

public sealed class PollingOptions
{
    public const string SectionName = "Polling";

    public int RefreshIntervalSeconds { get; set; } = 300;
    public bool AutoCheckUpdates { get; set; } = true;
}
