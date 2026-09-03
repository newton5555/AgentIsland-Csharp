using System.IO;

namespace AgentIsland.Windows;

/// Central path resolution. Mirrors the macOS app's data sources, adapted to
/// Windows conventions:
///   ~/.claude/projects                          -> %USERPROFILE%\.claude\projects
///   ~/Library/Application Support/Claude/...    -> %APPDATA%\Claude\claude-code-sessions
///   ~/.codex/sessions                           -> %USERPROFILE%\.codex\sessions
///   macOS Keychain "Claude Code-credentials"    -> %USERPROFILE%\.claude\.credentials.json
///   ~/Library/Application Support/AgentIsland   -> %APPDATA%\AgentIsland
///   ~/Library/Caches/dev.agentisland...         -> %LOCALAPPDATA%\AgentIsland\cache
public static class IslandPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// Claude Code config roots, in priority order. CLAUDE_CONFIG_DIR
    /// (comma-separated) overrides, matching Claude Code's own behavior.
    public static IReadOnlyList<string> ClaudeConfigRoots { get; } = ResolveClaudeConfigRoots();

    public static IEnumerable<string> ClaudeProjectRoots =>
        ClaudeConfigRoots.Select(root => Path.Combine(root, "projects"));

    public static string ClaudeCredentialsFile => Path.Combine(ClaudeConfigRoots[0], ".credentials.json");

    public static string ClaudeDesktopSessionsRoot => Path.Combine(RoamingAppData, "Claude", "claude-code-sessions");

    public static string CodexHome
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(env) ? Path.Combine(Home, ".codex") : env;
        }
    }

    public static string CodexSessionsRoot => Path.Combine(CodexHome, "sessions");

    /// `codex archive` moves a rollout here instead of deleting it — the
    /// tokens were still spent, so accounting walks both roots.
    public static string CodexArchivedSessionsRoot => Path.Combine(CodexHome, "archived_sessions");

    public static string CodexSessionIndexFile => Path.Combine(CodexHome, "session_index.jsonl");
    public static string CodexAuthFile => Path.Combine(CodexHome, "auth.json");

    public static string AppSupportDir => Path.Combine(RoamingAppData, "AgentIsland");
    public static string SettingsFile => Path.Combine(AppSupportDir, "settings.json");
    public static string TriggerRunsDir => Path.Combine(AppSupportDir, "trigger-runs");
    public static string CacheDir => Path.Combine(LocalAppData, "AgentIsland", "cache");

    private static IReadOnlyList<string> ResolveClaudeConfigRoots()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var parts = env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) return parts;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(home, ".claude"),
            Path.Combine(home, ".config", "claude"),
        };
    }
}
