using AgentIsland.Core.Agents;

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
    public AgentKey AgentKey { get; init; } = new(Tool.RawValue());

    public string Id => (AgentKey.Value.Length > 0 ? AgentKey.Value : Tool.RawValue()) + ":" + SessionId;

    public ScannedSession(
        AgentKey agentKey,
        string sessionId,
        string cwd,
        string label,
        DateTimeOffset modified,
        ActivityState status,
        string? transcriptPath,
        string? turnKey,
        SessionLaunchTarget launchTarget)
        : this(
            TriggerToolExtensions.FromRawValue(agentKey.Value) ?? TriggerTool.Claude,
            sessionId,
            cwd,
            label,
            modified,
            status,
            transcriptPath,
            turnKey,
            launchTarget)
    {
        AgentKey = agentKey;
    }
}

