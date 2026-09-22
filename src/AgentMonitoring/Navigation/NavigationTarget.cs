using AgentIsland.Core;
using AgentIsland.Core.Agents;

namespace AgentMonitoring.Navigation;

/// Semantic session location. The desktop host executes the launch.
public sealed record NavigationTarget(
    AgentKey Agent,
    string SessionId,
    string Cwd,
    SessionLaunchTarget Launch,
    string? TranscriptPath);

public interface ISessionNavigator
{
    bool TryOpen(NavigationTarget target);
}
