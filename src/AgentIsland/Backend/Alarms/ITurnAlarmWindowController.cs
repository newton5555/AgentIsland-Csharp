using AgentIsland.Core;
using AgentIsland.Backend.Monitoring;

namespace AgentIsland.Backend.Alarms;

public interface ITurnAlarmWindowController
{
    void Show(TriggerTool provider, ActivityMonitor.ActiveThread? thread, string deliveryKey, TurnAlarmKind? kind = null);
    void AutoDismiss(TriggerTool provider, string deliveryKey);
}
