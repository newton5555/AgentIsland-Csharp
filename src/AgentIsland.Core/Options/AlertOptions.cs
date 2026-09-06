namespace AgentIsland.Core.Options;

public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    public bool AlertsEnabled { get; set; } = false;
    public int WarningPercent { get; set; } = 80;
    public int CriticalPercent { get; set; } = 95;
}
