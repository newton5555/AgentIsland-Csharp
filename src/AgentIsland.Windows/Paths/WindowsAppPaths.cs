namespace AgentIsland.Windows.Paths;

/// Small injectable facade for platform-owned directories. Existing code
/// still uses the compatibility IslandPaths static during this first pass;
/// new services should depend on this facade instead of reading environment
/// variables directly.
public interface IAppPaths
{
    string AppSupportDirectory { get; }
    string CacheDirectory { get; }
    string CursorGlobalStorageDatabase { get; }
}

public sealed class WindowsAppPaths : IAppPaths
{
    public string AppSupportDirectory => IslandPaths.AppSupportDir;

    public string CacheDirectory => IslandPaths.CacheDir;

    public string CursorGlobalStorageDatabase => IslandPaths.CursorGlobalStorageDatabase;
}
