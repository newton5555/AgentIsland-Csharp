using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Core.Cost;

namespace AgentIsland.Providers.Cost.Cursor;

/// Parses Cursor's JSON bubble token ledger. The Windows project owns the
/// winsqlite3 read; this type only understands the provider payload and can be
/// tested without a Cursor installation.
public static class CursorLogParser
{
    /// Returns null for bubbles without a usable token count. Absent or {0,0}
    /// counts are skipped rather than becoming fake zero-cost events.
    public static TokenEvent? ParseBubble(string json, DateTimeOffset fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("tokenCount", out var counts)
                || counts.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var input = ReadInt(counts, "inputTokens");
            var output = ReadInt(counts, "outputTokens");
            if (input == 0 && output == 0) return null;

            return new TokenEvent(
                TriggerTool.Cursor,
                BubbleDate(root, fallback),
                "cursor",
                input,
                output,
                0,
                0,
                null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static DateTimeOffset BubbleDate(JsonElement root, DateTimeOffset fallback)
    {
        if (!root.TryGetProperty("createdAt", out var raw)) return fallback;
        if (raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out var epoch))
        {
            return epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        if (raw.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(raw.GetString(), out var parsed))
        {
            return parsed;
        }
        return fallback;
    }
}
