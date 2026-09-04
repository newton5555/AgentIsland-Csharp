using System.Collections.Generic;
using AgentIsland.Avalonia.ViewModels;
using AgentIsland.Runtime.Refresh;

namespace AgentIsland.Avalonia.Binding;

/// <summary>
/// Namespace forwarder for <see cref="AgentIsland.Avalonia.RuntimeIslandBinder"/>.
/// </summary>
public class RuntimeIslandBinder : AgentIsland.Avalonia.RuntimeIslandBinder
{
    public RuntimeIslandBinder(
        IslandViewModel viewModel,
        AgentRuntime runtime,
        IEnumerable<string>? preferredOrder = null)
        : base(viewModel, runtime, preferredOrder)
    {
    }
}

/// <summary>
/// Namespace forwarder for <see cref="AgentIsland.Avalonia.RuntimeIslandController"/>.
/// </summary>
public sealed class RuntimeIslandController : AgentIsland.Avalonia.RuntimeIslandBinder
{
    public RuntimeIslandController(
        IslandViewModel viewModel,
        AgentRuntime runtime,
        IEnumerable<string>? preferredOrder = null)
        : base(viewModel, runtime, preferredOrder)
    {
    }
}
