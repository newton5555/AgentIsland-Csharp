using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using static AgentIsland.Backend.Monitoring.ActivityMonitor;

namespace AgentIsland.Backend.Monitoring;

/// <summary>
/// Contract for monitoring session transcripts and aggregating activity states across providers.
/// </summary>
public interface IActivityMonitor : INotifyPropertyChanged
{
    ActivityState Claude { get; }
    ActivityState Codex { get; }
    ActivityState StateFor(TriggerTool tool);
    ActiveThread? ThreadFor(TriggerTool tool);
    void Configure(IAgentCatalog catalog);
    void Demo(ActivityState? state);
    void Start();
    void Stop();
    void ScanNow();
    Task ScanNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

