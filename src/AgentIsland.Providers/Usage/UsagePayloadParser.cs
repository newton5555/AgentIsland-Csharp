using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Core.Usage;

namespace AgentIsland.Providers.Usage;

/// Parses provider usage payloads without knowing where the bytes came from.
/// Network calls and credential resolution remain in the Windows application;
/// these rules are reusable by another host or a future test fixture.
public static class UsagePayloadParser
{
    public static WindowUsage ParseCodexWindow(JsonElement? obj)
    {
        if (obj is not { } window) return WindowUsage.Unknown;
        var used = Jsonl.GetDouble(window, "used_percent") ?? 0;
        DateTimeOffset? resetAt = Jsonl.GetDouble(window, "reset_at") is { } epoch
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000))
            : null;
        var period = Jsonl.GetDouble(window, "limit_window_seconds");
        return new WindowUsage(used / 100, resetAt, null, period);
    }

    public static WindowUsage ParseClaudeWindow(JsonElement? obj, double periodSeconds)
    {
        if (obj is not { } window) return WindowUsage.Unknown;
        var raw = Jsonl.GetDouble(window, "utilization")
            ?? Jsonl.GetDouble(window, "used_percent")
            ?? 0;
        var normalized = raw / 100.0;
        DateTimeOffset? resetAt = null;
        if (Jsonl.GetDouble(window, "resets_at") is { } epoch)
        {
            resetAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000));
        }
        else if (Jsonl.GetString(window, "resets_at") is { } iso)
        {
            resetAt = Jsonl.ParseIso8601(iso);
        }
        return new WindowUsage(Math.Min(1, Math.Max(0, normalized)), resetAt, null, periodSeconds);
    }

    public static int? ParseResetCardCount(JsonElement root)
    {
        if (Jsonl.GetObject(root, "rate_limit_reset_credits") is not { } credits
            || !credits.TryGetProperty("available_count", out var available)
            || available.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        return available.TryGetInt32(out var count) ? count : null;
    }

    public static IReadOnlyList<ResetCard>? ParseResetCardDetails(JsonElement root)
    {
        if (!root.TryGetProperty("credits", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var cards = new List<ResetCard>();
        foreach (var row in rows.EnumerateArray())
        {
            if (Jsonl.GetString(row, "status") != "available") continue;
            if (Jsonl.GetString(row, "id") is not { } id) continue;
            var title = Jsonl.GetString(row, "title") ?? "Reset";
            DateTimeOffset? expires = Jsonl.GetString(row, "expires_at") is { } iso
                && DateTimeOffset.TryParse(iso, out var parsed) ? parsed : null;
            cards.Add(new ResetCard(id, title, expires));
        }
        return cards;
    }
}
