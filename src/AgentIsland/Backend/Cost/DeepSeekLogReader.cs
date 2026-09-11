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

    public static List<TokenEvent> Scan(int lookbackDays, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(0, lookbackDays));
        var root = IslandPaths.DeepSeekSessionsRoot;
        if (!Directory.Exists(root)) return new List<TokenEvent>();

        var files = DiscoverSessionFiles(root);
        if (files.Count == 0) return new List<TokenEvent>();
        return Cache.Walk(files, cutoff, DeepSeekLogParser.ParseFile, cancellationToken);
    }

    public static List<string> DiscoverSessionFiles(string root)
    {
        if (!Directory.Exists(root)) return new List<string>();

        var files = SafeFileSystem.EnumerateFiles(root, "session*.jsonl.zstd");
        if (files.Count <= 1) return files;

        // Group by session directory. When DSH migrates formats (e.g. session.v3.jsonl.zstd),
        // multiple files exist in the same session folder; pick the highest version / newest file
        // so events are not double counted.
        return files
            .GroupBy(f => Path.GetDirectoryName(f) ?? f, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(SessionFileVersion).ThenByDescending(SafeFileSystem.LastWriteTime).First())
            .ToList();
    }

    private static int SessionFileVersion(string path)
    {
        var name = Path.GetFileName(path);
        var match = System.Text.RegularExpressions.Regex.Match(
            name, @"\.v(\d+)\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var v) ? v : 1;
    }

    internal static void ClearMemoryCache() => Cache.ClearMemory();

    /// Test/diagnostic seam that keeps the host reader's public surface in
    /// line with the other cost readers.
    internal static List<TokenEvent> ParseFile(string path) => DeepSeekLogParser.ParseFile(path);
}
