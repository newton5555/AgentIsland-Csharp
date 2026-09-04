namespace AgentIsland.Avalonia.ViewModels;

/// <summary>
/// Visual activity state for a provider slot in the floating island.
/// Idle: Provider is ready and quiet.
/// Working: Session/turn is actively executing.
/// NeedsYou: Task finished or awaiting user action/reply.
/// Error: Attention needed due to failure, error, or stall.
/// </summary>
public enum ProviderSlotStatus
{
    Idle,
    Working,
    NeedsYou,
    Error,
}
