using System.IO;
using AgentIsland.Core;
using AgentIsland.Core.Cost;

namespace AgentIsland.Providers.Cost.Claude;

/// Reconstructs Claude Code spend from the assistant events in the local
/// project transcripts. Dedup key is messageId:requestId (one API response
/// can be re-written across lines); synthetic placeholder models and
/// zero-token bookkeeping lines are skipped.
public static class ClaudeLogParser
{
    public static List<TokenEvent> ParseFile(string path)
    {
        var output = new List<(TokenEvent Event, string? DedupKey)>();
        try
        {
            foreach (var line in Jsonl.ReadLinesShared(path))
            {
                // A pathological multi-MB line (an embedded payload) can't be
                // a usage event; skip before parsing so it never balloons
                // peak memory (the macOS 64 MiB backstop's job, done harder).
                if (line.Length > 1_000_000) continue;
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null) continue;
                var root = doc.RootElement;
                if (Jsonl.GetString(root, "type") != "assistant") continue;
                if (Jsonl.GetObject(root, "message") is not { } message) continue;
                if (Jsonl.GetObject(message, "usage") is not { } usage) continue;

                var model = Jsonl.GetString(message, "model") ?? "";
                if (model.Length == 0
                    || model == "<synthetic>"
                    || model.StartsWith("synthetic", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var input = Jsonl.GetLong(usage, "input_tokens") ?? 0;
                var outputTokens = Jsonl.GetLong(usage, "output_tokens") ?? 0;
                var cacheCreation = Jsonl.GetLong(usage, "cache_creation_input_tokens") ?? 0;
                var cacheRead = Jsonl.GetLong(usage, "cache_read_input_tokens") ?? 0;
                if (input == 0 && outputTokens == 0 && cacheCreation == 0 && cacheRead == 0) continue;

                if (Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")) is not { } timestamp) continue;

                var messageId = Jsonl.GetString(message, "id");
                var requestId = Jsonl.GetString(root, "requestId");
                var dedupKey = messageId is { Length: > 0 } && requestId is { Length: > 0 }
                    ? $"{messageId}:{requestId}"
                    : null;

                output.Add((new TokenEvent(
                    TriggerTool.Claude, timestamp, model,
                    input, outputTokens, cacheCreation, cacheRead), dedupKey));
            }
        }
        catch
        {
            // A transcript being actively written can race the reader; the
            // next poll re-parses it (the fingerprint changed anyway).
        }
        // Within-file dedup happens here so the cache stores clean events;
        // cross-file dedup happens in Scan.
        return DeduplicateKeyed(output);
    }

    private static List<TokenEvent> DeduplicateKeyed(List<(TokenEvent Event, string? DedupKey)> events)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<TokenEvent>(events.Count);
        foreach (var (tokenEvent, key) in events)
        {
            if (key is not null && !seen.Add(key)) continue;
            output.Add(tokenEvent);
        }
        return output;
    }

}
