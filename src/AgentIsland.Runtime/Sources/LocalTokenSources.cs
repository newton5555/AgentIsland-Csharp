using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Providers.Cost.Codex;
using AgentIsland.Providers.Cost.DeepSeek;
using AgentIsland.Providers.Sessions.DeepSeek;
using AgentIsland.Runtime.Snapshots;

namespace AgentIsland.Runtime.Sources;

/// <summary>
/// Cross-platform factory for local token and cost snapshot sources (Codex and DeepSeek Harness).
/// Resolves paths via current user home directory and standard environment variables without
/// Windows or WPF dependencies.
/// </summary>
public static class LocalTokenSources
{
    public const string DefaultCodexCacheFileName = "codex-parse-cache.v2.json";
    public const string DefaultDeepSeekCacheFileName = "deepseek-harness-parse-cache.v2.json";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ResolveCodexHome(string? envOverride = null, string? homeDir = null)
    {
        var env = envOverride ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var home = homeDir ?? Home;
        return Path.Combine(home, ".codex");
    }

    public static string ResolveCodexSessionsRoot(string? codexHome = null) =>
        Path.Combine(codexHome ?? ResolveCodexHome(), "sessions");

    public static string ResolveCodexArchivedSessionsRoot(string? codexHome = null) =>
        Path.Combine(codexHome ?? ResolveCodexHome(), "archived_sessions");

    public static string ResolveDeepSeekSessionsRoot(string? homeDir = null) =>
        Path.Combine(homeDir ?? Home, ".dsh", "sessions");

    public static string ResolveDefaultCacheDir(string? localAppData = null, string? homeDir = null)
    {
        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgCache))
        {
            return Path.Combine(xdgCache.Trim(), "AgentIsland", "cache");
        }

        var appData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return Path.Combine(appData, "AgentIsland", "cache");
        }

        var home = homeDir ?? Home;
        return Path.Combine(home, ".cache", "AgentIsland");
    }

    /// <summary>
    /// Creates a local token/cost snapshot source for Codex.
    /// Enumerates active and archived sessions roots for *.jsonl files.
    /// </summary>
    public static IAgentSnapshotSource CreateCodex(
        int? lookbackDays = null,
        string? cacheFilePath = null,
        AgentKey? agentKey = null,
        Func<IEnumerable<string>>? fileProvider = null,
        string? codexHome = null)
    {
        var days = lookbackDays ?? CostSummarizer.YearHistoryDays(DateTimeOffset.Now);
        var cachePath = cacheFilePath ?? Path.Combine(ResolveDefaultCacheDir(), DefaultCodexCacheFileName);
        var key = agentKey ?? (AgentKey)"codex";
        var provider = fileProvider ?? (() => EnumerateCodexFiles(codexHome));

        return new TokenCostSnapshotSource(
            key,
            TriggerTool.Codex,
            cachePath,
            CodexLogParser.ParseFile,
            provider,
            lookbackDays: days);
    }

    /// <summary>
    /// Creates a local token/cost snapshot source for DeepSeek Harness.
    /// Enumerates ~/.dsh/sessions for session.jsonl.zstd files.
    /// </summary>
    public static IAgentSnapshotSource CreateDeepSeekHarness(
        int? lookbackDays = null,
        string? cacheFilePath = null,
        AgentKey? agentKey = null,
        Func<IEnumerable<string>>? fileProvider = null,
        string? sessionsRoot = null)
    {
        var days = lookbackDays ?? CostSummarizer.YearHistoryDays(DateTimeOffset.Now);
        var cachePath = cacheFilePath ?? Path.Combine(ResolveDefaultCacheDir(), DefaultDeepSeekCacheFileName);
        var key = agentKey ?? (AgentKey)"deepseek";
        var provider = fileProvider ?? (() => EnumerateDeepSeekHarnessFiles(sessionsRoot));

        return new TokenCostSnapshotSource(
            key,
            TriggerTool.DeepSeek,
            cachePath,
            DeepSeekLogParser.ParseFile,
            provider,
            lookbackDays: days);
    }

    /// <summary>
    /// Convenience forwarder to <see cref="CreateCodex"/>.
    /// </summary>
    public static IAgentSnapshotSource CreateCodexSource(
        int? lookbackDays = null,
        string? cacheFilePath = null,
        AgentKey? agentKey = null,
        Func<IEnumerable<string>>? fileProvider = null,
        string? codexHome = null) =>
        CreateCodex(lookbackDays, cacheFilePath, agentKey, fileProvider, codexHome);

    /// <summary>
    /// Convenience forwarder to <see cref="CreateDeepSeekHarness"/>.
    /// </summary>
    public static IAgentSnapshotSource CreateDeepSeekHarnessSource(
        int? lookbackDays = null,
        string? cacheFilePath = null,
        AgentKey? agentKey = null,
        Func<IEnumerable<string>>? fileProvider = null,
        string? sessionsRoot = null) =>
        CreateDeepSeekHarness(lookbackDays, cacheFilePath, agentKey, fileProvider, sessionsRoot);

    /// <summary>
    /// Creates a local activity snapshot source for DeepSeek Harness.
    /// Enumerates ~/.dsh/sessions for session.jsonl.zstd files and tracks human sessions.
    /// </summary>
    public static IAgentSnapshotSource CreateDeepSeekActivity(
        AgentKey? agentKey = null,
        Func<IEnumerable<string>>? fileProvider = null,
        Func<string, DeepSeekActivitySnapshot?>? parser = null,
        TimeSpan? activeWindow = null,
        string? sessionsRoot = null) =>
        new DeepSeekActivitySnapshotSource(
            agentKey: agentKey,
            fileProvider: fileProvider,
            parser: parser,
            activeWindow: activeWindow,
            sessionsRoot: sessionsRoot);

    /// <summary>
    /// Convenience forwarder to <see cref="CreateDeepSeekActivity"/>.
    /// </summary>
    public static IAgentSnapshotSource CreateDeepSeekActivitySource(
        AgentKey? agentKey = null,
        Func<IEnumerable<string>>? fileProvider = null,
        Func<string, DeepSeekActivitySnapshot?>? parser = null,
        TimeSpan? activeWindow = null,
        string? sessionsRoot = null) =>
        CreateDeepSeekActivity(agentKey, fileProvider, parser, activeWindow, sessionsRoot);

    /// <summary>
    /// Creates an official DeepSeek account balance snapshot source.
    /// Note: This performs live HTTP requests to https://api.deepseek.com/user/balance.
    /// It is NOT included in default offline LocalTokenSources.CreateSources() or --live mode;
    /// callers can opt in through CreateSources(includeDeepSeekBalance: true) or instantiate it directly.
    /// </summary>
    public static IAgentSnapshotSource CreateDeepSeekBalance(
        AgentKey? agentKey = null,
        HttpClient? httpClient = null,
        Func<string?>? apiKeyProvider = null,
        TimeSpan? timeout = null,
        string? credentialsFilePath = null) =>
        new DeepSeekBalanceSnapshotSource(
            agentKey: agentKey,
            httpClient: httpClient,
            apiKeyProvider: apiKeyProvider,
            timeout: timeout,
            credentialsFilePath: credentialsFilePath);

    /// <summary>
    /// Creates all supported cross-platform local token/cost and activity sources:
    /// - Codex local cost source
    /// - DeepSeek Harness local cost source
    /// - DeepSeek Harness local activity source
    /// Optionally adds the official DeepSeek account-balance source when
    /// <paramref name="includeDeepSeekBalance"/> is explicitly enabled.
    /// When passed to AgentRuntime, duplicate agents (DeepSeek) are automatically
    /// composed via CompositeAgentSnapshotSource into a single unified slot.
    /// </summary>
    public static IReadOnlyList<IAgentSnapshotSource> CreateSources(
        int? lookbackDays = null,
        string? cacheDir = null,
        Func<IEnumerable<string>>? codexFileProvider = null,
        Func<IEnumerable<string>>? deepSeekFileProvider = null,
        string? codexHome = null,
        string? deepSeekSessionsRoot = null,
        Func<IEnumerable<string>>? deepSeekActivityFileProvider = null,
        Func<string, DeepSeekActivitySnapshot?>? deepSeekActivityParser = null,
        TimeSpan? deepSeekActiveWindow = null,
        bool includeDeepSeekBalance = false)
    {
        var dir = cacheDir ?? ResolveDefaultCacheDir();
        var codexCache = Path.Combine(dir, DefaultCodexCacheFileName);
        var dshCache = Path.Combine(dir, DefaultDeepSeekCacheFileName);

        var activityFileProvider = deepSeekActivityFileProvider ?? deepSeekFileProvider;

        var sources = new List<IAgentSnapshotSource>
        {
            CreateCodex(lookbackDays: lookbackDays, cacheFilePath: codexCache, fileProvider: codexFileProvider, codexHome: codexHome),
            CreateDeepSeekHarness(lookbackDays: lookbackDays, cacheFilePath: dshCache, fileProvider: deepSeekFileProvider, sessionsRoot: deepSeekSessionsRoot),
            CreateDeepSeekActivity(fileProvider: activityFileProvider, parser: deepSeekActivityParser, activeWindow: deepSeekActiveWindow, sessionsRoot: deepSeekSessionsRoot),
        };

        if (includeDeepSeekBalance)
        {
            sources.Add(CreateDeepSeekBalance());
        }

        return sources;
    }

    /// <summary>
    /// Alias for <see cref="CreateSources"/>.
    /// </summary>
    public static IReadOnlyList<IAgentSnapshotSource> CreateAll(
        int? lookbackDays = null,
        string? cacheDir = null,
        Func<IEnumerable<string>>? codexFileProvider = null,
        Func<IEnumerable<string>>? deepSeekFileProvider = null,
        string? codexHome = null,
        string? deepSeekSessionsRoot = null,
        Func<IEnumerable<string>>? deepSeekActivityFileProvider = null,
        Func<string, DeepSeekActivitySnapshot?>? deepSeekActivityParser = null,
        TimeSpan? deepSeekActiveWindow = null,
        bool includeDeepSeekBalance = false) =>
        CreateSources(
            lookbackDays,
            cacheDir,
            codexFileProvider,
            deepSeekFileProvider,
            codexHome,
            deepSeekSessionsRoot,
            deepSeekActivityFileProvider,
            deepSeekActivityParser,
            deepSeekActiveWindow,
            includeDeepSeekBalance);

    /// <summary>
    /// Enumerates Codex session files (*.jsonl) from active and archived session roots.
    /// </summary>
    public static IEnumerable<string> EnumerateCodexFiles(string? codexHome = null)
    {
        var root = codexHome ?? ResolveCodexHome();
        var active = Path.Combine(root, "sessions");
        var archived = Path.Combine(root, "archived_sessions");

        var files = new List<string>();
        foreach (var dir in new[] { active, archived })
        {
            files.AddRange(SafeEnumerateFiles(dir, "*.jsonl"));
        }
        return files;
    }

    /// <summary>
    /// Enumerates DeepSeek Harness session files (session.jsonl.zstd) from sessions root.
    /// </summary>
    public static IEnumerable<string> EnumerateDeepSeekHarnessFiles(string? sessionsRoot = null)
    {
        var root = sessionsRoot ?? ResolveDeepSeekSessionsRoot();
        return SafeEnumerateFiles(root, "session.jsonl.zstd");
    }

    /// <summary>
    /// Race-tolerant and permission-safe recursive file enumeration.
    /// </summary>
    public static List<string> SafeEnumerateFiles(string root, string pattern)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return result;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            using var walker = Directory.EnumerateFiles(root, pattern, options).GetEnumerator();
            while (true)
            {
                try
                {
                    if (!walker.MoveNext()) break;
                }
                catch
                {
                    // A directory vanished or became unreadable mid-traversal.
                    break;
                }
                result.Add(walker.Current);
            }
        }
        catch
        {
            // Root vanished or inaccessible
        }

        return result;
    }
}
