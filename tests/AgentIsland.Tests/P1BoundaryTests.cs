using System.Reflection;
using AgentIsland.Core.Agents;
using AgentMonitoring.Queries;

namespace AgentIsland.Tests;

public class P1BoundaryTests
{
    [Fact]
    public void AgentMonitoring_DoesNotReferenceDesktopAssemblies()
    {
        var names = ReferencedNames(typeof(CostQueryService).Assembly);
        Assert.DoesNotContain("AgentIsland", names);
        Assert.DoesNotContain("PresentationFramework", names);
        Assert.DoesNotContain("PresentationCore", names);
        Assert.DoesNotContain("WindowsBase", names);
        Assert.DoesNotContain("System.Windows.Forms", names);
    }

    [Fact]
    public void AgentMonitoringCore_DoesNotReferenceDesktopAssemblies()
    {
        var names = ReferencedNames(typeof(AgentKey).Assembly);
        Assert.Equal("AgentMonitoring.Core", typeof(AgentKey).Assembly.GetName().Name);
        Assert.DoesNotContain("AgentIsland", names);
        Assert.DoesNotContain("PresentationFramework", names);
        Assert.DoesNotContain("WindowsBase", names);
    }

    [Fact]
    public void CostQueryContract_UsesAgentKey()
    {
        var scan = typeof(ICostQueryService).GetMethod(nameof(ICostQueryService.ScanAsync));
        Assert.NotNull(scan);
        Assert.Equal(typeof(AgentKey), scan!.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(AgentKey), typeof(CostScanResult).GetProperty("Agent")!.PropertyType);
    }

    private static HashSet<string> ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
