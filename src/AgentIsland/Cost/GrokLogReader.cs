using System.IO;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Reconstructs Grok spend from the local CLI session logs. Grok is the one
/// provider that ships its own dollar figure: each `turn_completed` update
/// carries usage.costUsdTicks (USD * 1e-9, verified 158652000 ticks = $0.159),
/// so the event's SelfReportedCostUSD is set directly and OVERRIDES the Pricing
/// table downstream — Grok dollars are never guessed.
///
///   %USERPROFILE%\.grok\sessions\&lt;url-encoded cwd&gt;\&lt;uuid&gt;\updates.jsonl
///
/// A turn's usage carries top-level totals plus a modelUsage map keyed by model
/// id; a multi-model turn emits one event per model from its own sub-figures
/// (never the top-level, which would double count), and a turn with no
/// modelUsage falls back to a single "grok" event.
///
/// Unlike the Claude/Codex readers this does NOT route through LogParseCache:
/// that cache's on-disk row (EventDto) has no cost column, so a warm-cache poll
/// would silently drop Grok's self-reported dollars. Correctness for the
/// gold-mine provider outweighs the re-parse; usage lines are tiny and the
/// non-usage bulk is skipped by a cheap substring guard before any JSON parse.
public static class GrokLogReader
{
    private const string FallbackModel = "grok";

    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-lookbackDays);
        var root = Path.Combine(IslandPaths.Home, ".grok", "sessions");
        if (!Directory.Exists(root)) return new List<TokenEvent>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<TokenEvent>();
        // Same recursive, race-tolerant walk the session picker uses; the tree
        // is <cwd>\<uuid>\updates.jsonl.
        foreach (var path in SessionScanner.SafeEnumerateFiles(root, "updates.jsonl"))
        {
            if (SessionScanner.Mtime(path) < cutoff) continue;
            foreach (var (tokenEvent, dedupKey) in ParseFile(path))
            {
                if (tokenEvent.Timestamp < cutoff) continue;
                if (dedupKey is not null && !seen.Add(dedupKey)) continue;
                output.Add(tokenEvent);
            }
        }
        return output;
    }

    internal static List<(TokenEvent Event, string? DedupKey)> ParseFile(string path)
    {
        var output = new List<(TokenEvent, string?)>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                // Tool-call results dominate the stream and can be multi-MB;
                // only turn_completed lines carry a usage object, and every one
                // contains the "usage" key. Guard on both before parsing.
                if (line.Length > 1_000_000) continue;
                if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null) continue;
                var root = doc.RootElement;
                if (Jsonl.GetObject(root, "params") is not { } prms) continue;
                if (Jsonl.GetObject(prms, "update") is not { } update) continue;
                if (Jsonl.GetObject(update, "usage") is not { } usage) continue;

                // Unix seconds. Range-guard so a corrupt value can't throw out
                // of FromUnixTimeSeconds and abort the rest of the file.
                if (Jsonl.GetDouble(root, "timestamp") is not { } seconds
                    || seconds <= 0 || seconds > 253_402_300_799) continue;
                var timestamp = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
                var promptId = Jsonl.GetString(update, "prompt_id") ?? "";

                if (Jsonl.GetObject(usage, "modelUsage") is { } modelUsage
                    && modelUsage.EnumerateObject().Any())
                {
                    foreach (var model in modelUsage.EnumerateObject())
                    {
                        if (model.Value.ValueKind != JsonValueKind.Object) continue;
                        AddModel(output, model.Value, model.Name, timestamp, promptId);
                    }
                }
                else
                {
                    AddModel(output, usage, FallbackModel, timestamp, promptId);
                }
            }
        }
        catch
        {
            // A session being written can race the reader; the next poll re-reads.
        }
        return output;
    }

    private static void AddModel(
        List<(TokenEvent, string?)> output,
        JsonElement fields,
        string model,
        DateTimeOffset timestamp,
        string promptId)
    {
        var input = Jsonl.GetLong(fields, "inputTokens") ?? 0;
        var outputTokens = Jsonl.GetLong(fields, "outputTokens") ?? 0;
        var cachedRead = Jsonl.GetLong(fields, "cachedReadTokens") ?? 0;
        if (input == 0 && outputTokens == 0 && cachedRead == 0) return;

        // Grok reports cost in ticks of USD * 1e-9. Absent ticks stay null
        // rather than being priced from a table Grok has no entry in.
        var cost = Jsonl.GetLong(fields, "costUsdTicks") is { } ticks
            ? ticks * 1e-9
            : (double?)null;

        // Dedup key pins the model: a multi-model turn shares one prompt_id
        // across its per-model events, so keying on prompt_id alone would drop
        // every model after the first. Empty prompt_id => no dedup.
        var dedupKey = promptId.Length > 0 ? $"{promptId}|{model}" : null;

        output.Add((new TokenEvent(
            TriggerTool.Grok, timestamp, model,
            input, outputTokens, 0, cachedRead, cost), dedupKey));
    }
}
