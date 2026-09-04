using System;
using System.Collections.Generic;
using System.IO;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Providers.Cost.Codex;
using AgentIsland.Providers.Cost.DeepSeek;
using AgentIsland.Runtime.Snapshots;
using AgentIsland.Windows.Storage;

namespace AgentIsland.Windows.Runtime;

/// <summary>
/// Platform factory for Windows-hosted local token and cost snapshot sources.
/// Owns the local session paths, search patterns, cache locations, and file enumeration
/// for Codex and DeepSeek Harness without hardcoding developer directories or performing
/// active filesystem scans at creation time.
/// </summary>
public static class WindowsTokenSources
{
    public const string DefaultCodexCacheFileName = "codex-parse-cache.v2.json";
    public const string DefaultDeepSeekCacheFileName = "deepseek-harness-parse-cache.v2.json";

    /// <summary>
    /// Creates a local token/cost snapshot source for Codex.
    /// Walks <see cref="IslandPaths.CodexSessionsRoot"/> and <see cref="IslandPaths.CodexArchivedSessionsRoot"/> for *.jsonl files.
    /// </summary>
    public static IAgentSnapshotSource CreateCodex(
        int? lookbackDays = null,
        string? cacheFilePath = null,
        AgentKey? agentKey = null,
        Func<IEnumerable<string>>? fileProvider = null)
    {
        var days = lookbackDays ?? CostSummarizer.YearHistoryDays(DateTimeOffset.Now);
        var cachePath = cacheFilePath ?? Path.Combine(IslandPaths.CacheDir, DefaultCodexCacheFileName);
        var key = agentKey ?? (AgentKey)"codex";
        var provider = fileProvider ?? EnumerateCodexFiles;

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
    /// Walks <see cref="IslandPaths.DeepSeekSessionsRoot"/> for session.jsonl.zstd files.
    /// </summary>
    public static IAgentSnapshotSource CreateDeepSeekHarness(
        int? lookbackDays = null,
        string? cacheFilePath = null,
        AgentKey? agentKey = null,
        Func<IEnumerable<string>>? fileProvider = null)
    {
        var days = lookbackDays ?? CostSummarizer.YearHistoryDays(DateTimeOffset.Now);
        var cachePath = cacheFilePath ?? Path.Combine(IslandPaths.CacheDir, DefaultDeepSeekCacheFileName);
        var key = agentKey ?? (AgentKey)"deepseek";
        var provider = fileProvider ?? EnumerateDeepSeekHarnessFiles;

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
        Func<IEnumerable<string>>? fileProvider = null) =>
        CreateCodex(lookbackDays, cacheFilePath, agentKey, fileProvider);

    /// <summary>
    /// Convenience forwarder to <see cref="CreateDeepSeekHarness"/>.
    /// </summary>
    public static IAgentSnapshotSource CreateDeepSeekHarnessSource(
        int? lookbackDays = null,
        string? cacheFilePath = null,
        AgentKey? agentKey = null,
        Func<IEnumerable<string>>? fileProvider = null) =>
        CreateDeepSeekHarness(lookbackDays, cacheFilePath, agentKey, fileProvider);

    /// <summary>
    /// Creates all supported Windows local token/cost sources (Codex and DeepSeek Harness).
    /// </summary>
    public static IReadOnlyList<IAgentSnapshotSource> CreateSources(int? lookbackDays = null) =>
        new[]
        {
            CreateCodex(lookbackDays),
            CreateDeepSeekHarness(lookbackDays),
        };

    /// <summary>
    /// Alias for <see cref="CreateSources"/>.
    /// </summary>
    public static IReadOnlyList<IAgentSnapshotSource> CreateAll(int? lookbackDays = null) =>
        CreateSources(lookbackDays);

    /// <summary>
    /// Enumerates Codex session files from both active and archived session roots.
    /// Returns an empty collection if roots do not exist or if IO errors occur.
    /// </summary>
    public static IEnumerable<string> EnumerateCodexFiles()
    {
        var files = new List<string>();
        var roots = new[]
        {
            IslandPaths.CodexSessionsRoot,
            IslandPaths.CodexArchivedSessionsRoot,
        };

        foreach (var root in roots)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    files.AddRange(SafeFileSystem.EnumerateFiles(root, "*.jsonl"));
                }
            }
            catch
            {
                // Vanished or inaccessible roots return empty without throwing.
            }
        }

        return files;
    }

    /// <summary>
    /// Enumerates DeepSeek Harness session files from the sessions root.
    /// Returns an empty collection if the root does not exist or if IO errors occur.
    /// </summary>
    public static IEnumerable<string> EnumerateDeepSeekHarnessFiles()
    {
        try
        {
            var root = IslandPaths.DeepSeekSessionsRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return Array.Empty<string>();
            }

            return SafeFileSystem.EnumerateFiles(root, "session.jsonl.zstd");
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
