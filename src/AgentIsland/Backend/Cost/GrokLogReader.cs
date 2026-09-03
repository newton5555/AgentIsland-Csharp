using System.IO;
using AgentIsland.Core;
using AgentIsland.Providers.Cost.Grok;

namespace AgentIsland.Backend.Cost;

/// Windows host adapter for Grok session-file discovery. Grok's self-reported
/// cost parsing is kept in Providers and is not approximated by the shared
/// pricing table.
public static class GrokLogReader
{
    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-lookbackDays);
        var root = Path.Combine(IslandPaths.Home, ".grok", "sessions");
        if (!Directory.Exists(root)) return new List<TokenEvent>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<TokenEvent>();
        foreach (var path in SafeFileSystem.EnumerateFiles(root, "updates.jsonl"))
        {
            if (SafeFileSystem.LastWriteTime(path) < cutoff) continue;
            foreach (var (tokenEvent, dedupKey) in GrokLogParser.ParseFile(path))
            {
                if (tokenEvent.Timestamp < cutoff) continue;
                if (dedupKey is not null && !seen.Add(dedupKey)) continue;
                output.Add(tokenEvent);
            }
        }
        return output;
    }

    internal static List<(TokenEvent Event, string? DedupKey)> ParseFile(string path) =>
        GrokLogParser.ParseFile(path);
}
