using System.IO;
using AgentIsland.Core;
using AgentIsland.Providers.Cost.Codex;

namespace AgentIsland.Backend.Cost;

/// Windows host adapter for Codex rollout discovery. The replay guard and
/// JSONL interpretation live in Providers so the parser can be tested and
/// reused without depending on Windows paths.
public static class CodexLogReader
{
    // v2: replay guard on total_token_usage + archived_sessions root — both
    // change what a file parses to, so v1 entries must not be trusted.
    private static readonly LogParseCache Cache = new(
        TriggerTool.Codex,
        Path.Combine(IslandPaths.CacheDir, "codex-parse-cache.v2.json"));

    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-lookbackDays);
        var files = new List<string>();
        foreach (var root in new[] { IslandPaths.CodexSessionsRoot, IslandPaths.CodexArchivedSessionsRoot })
        {
            if (Directory.Exists(root)) files.AddRange(SafeFileSystem.EnumerateFiles(root, "*.jsonl"));
        }
        if (files.Count == 0) return new List<TokenEvent>();
        return Cache.Walk(files, cutoff, CodexLogParser.ParseFile);
    }

    // Existing diagnostics and tests use this seam. It remains a host-level
    // forwarding method while the actual parser belongs to Providers.
    internal static List<TokenEvent> ParseFile(string path) => CodexLogParser.ParseFile(path);
}
