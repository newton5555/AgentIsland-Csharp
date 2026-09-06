using AgentIsland.Core;
using AgentIsland.Backend.Monitoring;

namespace AgentIsland.Backend.Alarms;

public interface IAgentReminderCenter
{
    void Handle(TriggerTool provider, IReadOnlyList<ActivityMonitor.ActiveThread> needsYouThreads);
    bool HasAcknowledged(TriggerTool provider, ActivityMonitor.ActiveThread? thread);
    void Acknowledge(TriggerTool provider, ActivityMonitor.ActiveThread? thread);
    void ClearProvider(TriggerTool provider);
}
