using System.ComponentModel;
using AgentIsland.Core;

namespace AgentIsland.Backend.Settings;

public enum AlertSeverity
{
    None,
    Warning,
    Critical,
}

public interface IAlertEngine : INotifyPropertyChanged
{
    AlertSeverity Severity { get; }
    IReadOnlyList<AlertEngine.PulseLine>? Pulse { get; }
    void ClearPulse();
    AlertSeverity SeverityFor(TriggerTool tool);
    void Start();
    void Stop();
}
