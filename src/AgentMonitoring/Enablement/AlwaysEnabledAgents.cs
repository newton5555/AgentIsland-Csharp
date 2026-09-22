using AgentIsland.Core.Agents;

namespace AgentMonitoring.Enablement;

/// Test and diagnostics helper: every agent is enabled.
public sealed class AlwaysEnabledAgents : IAgentEnablement
{
    public static AlwaysEnabledAgents Instance { get; } = new();

    public bool IsEnabled(AgentKey agent) => true;
}
