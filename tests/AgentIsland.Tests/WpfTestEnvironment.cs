namespace AgentIsland.Tests;

public static class WpfTestEnvironment
{
    private static readonly object Lock = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Lock)
        {
            if (_initialized) return;
            try
            {
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
                if (System.Windows.Application.Current is null)
                {
                    _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                }
                System.Windows.Application.ResourceAssembly ??= typeof(AgentIsland.App).Assembly;
            }
            catch { }
            _initialized = true;
        }
    }
}
