namespace AgentIsland.Core;

public enum SessionLaunchTarget
{
    Cli,
    ClaudeDesktop,
}

/// A resumable session discovered on disk, for the trigger picker and the
/// activity monitor.
public sealed record ScannedSession(
    TriggerTool Tool,
    string SessionId,
    string Cwd,
    string Label,
    DateTimeOffset Modified,
    ActivityState Status,
    string? TranscriptPath,
    string? TurnKey,
    SessionLaunchTarget LaunchTarget)
{
    public string Id => Tool.RawValue() + ":" + SessionId;
}
