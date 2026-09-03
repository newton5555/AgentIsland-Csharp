using System.IO;
using AgentIsland.Core;

namespace AgentIsland.Backend.Usage;

public static class CodexCredentials
{
    public static bool CanPromptReauth() => CLILocator.Locate("codex") is not null;

    public static DateTimeOffset? AuthModificationStamp()
    {
        try
        {
            var path = IslandPaths.CodexAuthFile;
            if (!File.Exists(path)) return null;
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch
        {
            return null;
        }
    }

    public static bool SpawnReauth()
    {
        if (CLILocator.Locate("codex") is not { } cli) return false;
        return TerminalLauncher.RunVisible(cli, "login", "Agent Island — Codex login");
    }
}
