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
    public static IReadOnlyList<string> ClaudeConfigRoots => ResolveClaudeConfigRoots();

    public static IEnumerable<string> ClaudeProjectRoots =>
        ClaudeConfigRoots.Select(root => Path.Combine(root, "projects"));

    public static string ClaudeCredentialsFile => Path.Combine(ClaudeConfigRoots[0], ".credentials.json");

    public static string ClaudeDesktopSessionsRoot => OverrideOrDefault(
        "CLAUDE_DESKTOP_DIR", Path.Combine(RoamingAppData, "Claude", "claude-code-sessions"));

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

    /// DeepSeek Harness persists compressed JSONL event streams below the
    /// user's `.dsh` directory. The reader intentionally owns only the
    /// sessions subtree; token storage and other Harness state stay private
    /// to DSH.
    public static string DeepSeekSessionsRoot => Path.Combine(Home, ".dsh", "sessions");

    /// Antigravity writes one SQLite conversation database per session. The
    /// environment override accepts comma-separated data roots, matching
    /// ccusage: each item may be the provider root (with a conversations
    /// child) or the conversations directory itself.
    public static IReadOnlyList<string> AntigravityConversationRoots =>
        ResolveAntigravityConversationRoots();

    public static string CursorGlobalStorageDatabase => Path.Combine(
        RoamingAppData, "Cursor", "User", "globalStorage", "state.vscdb");

    /// The normal app keeps preferences under Roaming. Tests and diagnostic
    /// hosts can opt into a process-local root before any singleton is loaded;
    /// keeping this lookup dynamic is important because the static stores are
    /// intentionally shared for the life of the process.
    public static string AppSupportDir => OverrideOrDefault(
        "AGENTISLAND_DATA_DIR", Path.Combine(RoamingAppData, "AgentIsland"));
    public static string SettingsFile => Path.Combine(AppSupportDir, "settings.json");
    public static string TriggerRunsDir => Path.Combine(AppSupportDir, "trigger-runs");
    public static string CacheDir => OverrideOrDefault(
        "AGENTISLAND_CACHE_DIR", Path.Combine(LocalAppData, "AgentIsland", "cache"));

    private static string OverrideOrDefault(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : Path.GetFullPath(value);
    }

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

    private static IReadOnlyList<string> ResolveAntigravityConversationRoots()
    {
        var configured = Environment.GetEnvironmentVariable("ANTIGRAVITY_DATA_DIR");
        var roots = string.IsNullOrWhiteSpace(configured)
            ? new[]
            {
                Path.Combine(Home, ".gemini", "antigravity"),
                Path.Combine(Home, ".gemini", "antigravity-cli"),
                Path.Combine(Home, ".gemini", "antigravity-ide"),
                Path.Combine(Home, ".gemini", "antigravity-backup"),
                Path.Combine(Home, ".config", "antigravity"),
            }
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return roots
            .Select(root =>
            {
                var full = Path.GetFullPath(root);
                var nested = Path.Combine(full, "conversations");
                return Directory.Exists(nested) ? nested : full;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
