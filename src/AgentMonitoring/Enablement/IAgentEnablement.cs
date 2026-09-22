using AgentIsland.Core.Agents;

namespace AgentMonitoring.Enablement;

/// Whether an agent is currently enabled for collection. Slot layout and
/// silhouette visibility stay on the desktop side.
public interface IAgentEnablement
{
    bool IsEnabled(AgentKey agent);
}
