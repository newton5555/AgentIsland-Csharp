using System.IO;
using AgentIsland.Windows;

namespace AgentIsland.Tests;

/// Gives every console-runner process its own preference and parse-cache
/// roots. This runs before the first test touches a singleton, so a regression
/// test cannot read or write the developer's real AgentIsland settings.
internal static class TestIsolation
{
    private static string? _root;

    public static void Initialize()
    {
        if (_root is not null) return;

        var root = Path.Combine(
            Path.GetTempPath(), "AgentIsland.Tests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(cache);

        Environment.SetEnvironmentVariable("AGENTISLAND_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("AGENTISLAND_DATA_DIR", data);
        Environment.SetEnvironmentVariable("AGENTISLAND_CACHE_DIR", cache);
        _root = root;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    private static void Cleanup()
    {
        var root = _root;
        if (root is null) return;
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // A locked diagnostic file can be collected by the OS temp cleanup.
        }
    }
}
