using System.IO;
using AgentIsland.Core;
using AgentIsland.Providers.Cost.Claude;

namespace AgentIsland.Backend.Cost;

/// Windows host adapter for Claude's provider parser. File discovery and the
/// cache location stay here; payload interpretation lives in Providers.
public static class ClaudeLogReader
{
    private static readonly LogParseCache Cache = new(
        TriggerTool.Claude,
        // v2 stores compact per-file DTOs instead of serialized TokenEvents;
        // do not read the incompatible v1 object graph as a cache hit.
        Path.Combine(IslandPaths.CacheDir, "claude-parse-cache.v2.json"));

    public static List<TokenEvent> Scan(int lookbackDays, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-lookbackDays);
        var files = new List<string>();
        foreach (var root in IslandPaths.ClaudeProjectRoots)
        {
            if (!Directory.Exists(root)) continue;
            // Subagent transcripts stay in: their tokens are real spend.
            files.AddRange(SafeFileSystem.EnumerateFiles(root, "*.jsonl"));
        }
        var events = Cache.Walk(files, cutoff, ClaudeLogParser.ParseFile, cancellationToken);
        return Deduplicate(events);
    }

    internal static void ClearMemoryCache() => Cache.ClearMemory();

    /// Cross-file dedup: a resumed session re-writes prior assistant events
    /// into the new transcript. Key on timestamp and token shape when ids
    /// were absent.
    private static List<TokenEvent> Deduplicate(List<TokenEvent> events)
    {
        var seen = new HashSet<DedupKey>();
        var output = new List<TokenEvent>(events.Count);
        foreach (var tokenEvent in events)
        {
            var key = new DedupKey(
                tokenEvent.Timestamp.ToUnixTimeMilliseconds(), tokenEvent.Model,
                tokenEvent.InputTokens, tokenEvent.OutputTokens,
                tokenEvent.CacheCreationTokens, tokenEvent.CacheReadTokens);
            if (!seen.Add(key)) continue;
            output.Add(tokenEvent);
        }
        return output;
    }

    private readonly record struct DedupKey(
        long Timestamp, string Model, long Input, long Output, long CacheCreation, long CacheRead);
}
