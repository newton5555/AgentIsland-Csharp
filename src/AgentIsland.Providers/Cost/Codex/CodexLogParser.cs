using System.IO;
using AgentIsland.Core;
using AgentIsland.Core.Cost;

namespace AgentIsland.Providers.Cost.Codex;

/// Reconstructs Codex spend from rollout files. `turn_context` events set
/// the active model for subsequent `token_count` events (mid-session model
/// changes are respected); cached input bills at the cache-read discount.
public static class CodexLogParser
{
    /// Early CLI versions emitted token_count before any turn_context.
    private const string FallbackModel = "gpt-5.4";

    public static List<TokenEvent> ParseFile(string path)
    {
        var output = new List<TokenEvent>();
        var currentModel = FallbackModel;
        // Replay guard: the CLI re-emits the last token_count on some UI
        // refreshes. A genuine new turn always moves the session's running
        // totals, so an event whose total_token_usage (input, output) pair
        // exactly matches the previous event is the same turn re-sent — its
        // last_token_usage would double-bill. Totals that shrink are a
        // compact reset, and those events still count.
        (long Input, long Output)? lastTotals = null;
        try
        {
            foreach (var line in Jsonl.ReadLinesShared(path))
            {
                // Multi-MB embedded payload lines can't be token_count events;
                // skip before parsing to keep peak memory flat.
                if (line.Length > 1_000_000) continue;
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null) continue;
                var root = doc.RootElement;
                var type = Jsonl.GetString(root, "type");
                if (type == "turn_context")
                {
                    if (Jsonl.GetObject(root, "payload") is { } context
                        && Jsonl.GetString(context, "model") is { Length: > 0 } model)
                    {
                        currentModel = model;
                    }
                    continue;
                }
                if (type != "event_msg") continue;
                if (Jsonl.GetObject(root, "payload") is not { } payload) continue;
                if (Jsonl.GetString(payload, "type") != "token_count") continue;
                if (Jsonl.GetObject(payload, "info") is not { } info) continue;

                if (Jsonl.GetObject(info, "total_token_usage") is { } totals)
                {
                    var pair = (
                        Jsonl.GetLong(totals, "input_tokens") ?? 0,
                        Jsonl.GetLong(totals, "output_tokens") ?? 0);
                    var replayed = lastTotals == pair;
                    lastTotals = pair;
                    if (replayed) continue;
                }

                if (Jsonl.GetObject(info, "last_token_usage") is not { } usage) continue;

                var totalInput = Jsonl.GetLong(usage, "input_tokens") ?? 0;
                var cachedInput = Jsonl.GetLong(usage, "cached_input_tokens") ?? 0;
                var outputTokens = Jsonl.GetLong(usage, "output_tokens") ?? 0;
                if (totalInput == 0 && outputTokens == 0) continue;

                if (Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")) is not { } timestamp) continue;

                // Non-cached input bills at the standard rate; the cached
                // portion at the cache-read discount.
                var nonCached = Math.Max(0, totalInput - cachedInput);
                output.Add(new TokenEvent(
                    TriggerTool.Codex, timestamp, currentModel,
                    nonCached, outputTokens, 0, Math.Min(cachedInput, totalInput)));
            }
        }
        catch
        {
        }
        return output;
    }
}
