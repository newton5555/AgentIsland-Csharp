using System.ComponentModel;
using System.Windows;
using AgentIsland.Core;
using AgentIsland.Core.Options;

namespace AgentIsland.UI;

/// <summary>
/// Contract for island presentation state machine and geometry calculation.
/// </summary>
public interface IIslandModel : INotifyPropertyChanged
{
    IslandState State { get; set; }
    IslandSpacingMode SpacingMode { get; set; }
    double ExpandedContentHeight { get; set; }
    TriggerTool? SoloProvider { get; }
    double NotchWidth { get; }
    Size Size { get; }
    double CornerRadius { get; }
    void NotifyAlwaysShowUsageChanged();
}
