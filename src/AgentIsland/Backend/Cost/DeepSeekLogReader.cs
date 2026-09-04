using System.IO;
using AgentIsland.Core;
using AgentIsland.Providers.Cost.DeepSeek;
using AgentIsland.Windows.Storage;

namespace AgentIsland.Backend.Cost;

/// Windows host adapter for DeepSeek Harness' local token ledger. DSH keeps
/// one compressed JSONL stream per session under `%USERPROFILE%\.dsh\sessions`;
/// path discovery and memoization stay here, while event interpretation lives
/// in the Providers project.
public static class DeepSeekLogReader
{
    // v2 includes every DSH route and route-aware usage replacement. Old v1
    // entries retain partial totals even when the source file is unchanged.
    private static readonly LogParseCache Cache = new(
        TriggerTool.DeepSeek,
        Path.Combine(IslandPaths.CacheDir, "deepseek-harness-parse-cache.v2.json"));

    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(0, lookbackDays));
        var root = IslandPaths.DeepSeekSessionsRoot;
        if (!Directory.Exists(root)) return new List<TokenEvent>();

        var files = SafeFileSystem.EnumerateFiles(root, "session.jsonl.zstd");
        if (files.Count == 0) return new List<TokenEvent>();
        return Cache.Walk(files, cutoff, DeepSeekLogParser.ParseFile);
    }

    /// Test/diagnostic seam that keeps the host reader's public surface in
    /// line with the other cost readers.
    internal static List<TokenEvent> ParseFile(string path) => DeepSeekLogParser.ParseFile(path);
}
