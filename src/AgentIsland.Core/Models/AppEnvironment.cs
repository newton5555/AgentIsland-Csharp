namespace AgentIsland.Core;

public enum AppMode
{
    Normal,
    Demo,
    Debug,
}

/// Mode flags resolved once at launch. Demo injects synthetic data for
/// screenshots and must never fire real resume commands. Only AGENTISLAND_*
/// variables count — the CODEXISLAND_* era ended with 1.7.
public static class AppEnvironment
{
    public static AppMode Current { get; } = Resolve();

    public static bool IsDemo => Current == AppMode.Demo;
    public static bool IsDebug => Current == AppMode.Debug;

    private static AppMode Resolve()
    {
        if (Flag("AGENTISLAND_DEMO")) return AppMode.Demo;
        if (Flag("AGENTISLAND_DEBUG")) return AppMode.Debug;
        return AppMode.Normal;
    }

    private static bool Flag(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is "1" or "true" or "TRUE" or "yes";
    }
}
